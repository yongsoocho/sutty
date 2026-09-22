using sutty.Core.Terminal;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

internal static class LocalWorkingDirectorySelfTests
{
    public static void VerifyParser()
    {
        const string nonce = "01a0c928b3477cf0845c0e25856dc1ef";
        const string location = "C:\\space 한글 & semi;\\nested";
        var received = new List<string>();
        var tracker = new LocalWorkingDirectoryTracker(nonce, received.Add);
        var wire = Encoding.UTF8.GetBytes($"normal output\x1b]777;sutty-cwd;{nonce};{location}\x1b\\");
        foreach (var value in wire)
            tracker.Feed(new[] { value });
        Assert(received.SequenceEqual([location]), "split UTF-8, ST and semicolons preserve exact directory");

        Feed($"\x1b]777;sutty-cwd;{nonce};C:\\\x07");
        Assert(received[^1] == "C:\\", "BEL terminated drive-root report is accepted");
        var before = received.Count;
        Feed("C:\\fake plain text\x1b]7;file://localhost/C:/fake\x07");
        Feed("\x1b]777;sutty-cwd;foreign-session;C:\\fake\x07");
        Feed($"\x1b]777;sutty-cwd;{nonce};relative\\path\x07");
        Feed($"\x1b]777;sutty-cwd;{nonce};C:\\bad\rpath\x07");
        Feed($"\x1b]777;sutty-cwd;{nonce};C:\\bad\x1bXpayload\x07");
        tracker.Feed(Encoding.UTF8.GetBytes($"\x1b]777;sutty-cwd;{nonce};C:\\").Concat(new byte[] { 0xff, 0x07 }).ToArray());
        Feed($"\x1b]777;sutty-cwd;{nonce};C:\\{new string('x', 140000)}\x1b\\");
        Assert(received.Count == before, "unrelated, foreign, malformed and oversized reports are ignored");
        Feed($"\x1b]777;sutty-cwd;{nonce};\\\\server\\share\\한글 & space\x07");
        Assert(received[^1] == "\\\\server\\share\\한글 & space", "UNC paths remain intact after rejected reports");
        Feed($"\x1b]777;sutty-cwd;{nonce};\x07");
        Assert(received[^1] == string.Empty, "non-filesystem provider explicitly clears the reported directory");
        Console.WriteLine("Local working-directory parser self-tests passed.");
        return;

        void Feed(string text) => tracker.Feed(Encoding.UTF8.GetBytes(text));
    }

    [SupportedOSPlatform("windows10.0.17763")]
    public static async Task VerifyNativeAsync(LocalShellKind shell)
    {
        Console.WriteLine($"Verifying actual {shell} current-directory reports...");
        var root = Path.Combine(Path.GetTempPath(), "sutty-cwd-" + Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(root, "space 한글 & semi;");
        Directory.CreateDirectory(nested);
        var terminal = new WindowsConPtyTerminal(shell, loadProfile: false);
        var gate = new object();
        var raw = new StringBuilder();
        var reports = new List<string>();
        var decoder = Encoding.UTF8.GetDecoder();
        terminal.WorkingDirectoryChanged += (_, value) =>
        {
            lock (gate) reports.Add(value);
        };
        terminal.TerminalDataReceived += (_, args) =>
        {
            lock (gate)
            {
                var chars = new char[Encoding.UTF8.GetMaxCharCount(args.Data.Length)];
                var count = decoder.GetChars(args.Data.Span, chars, false);
                raw.Append(chars, 0, count);
            }
        };

        try
        {
            Assert(terminal.WorkingDirectory.Length == 0, "directory is initially unknown");
            await terminal.OpenTerminalAsync(new TerminalSize(100, 30));
            await WaitForPromptAsync(1, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            if (shell == LocalShellKind.PowerShell)
                await RunAsync("Set-PSReadLineOption -HistorySaveStyle SaveNothing", terminal.WorkingDirectory);
            await RunAsync(ChangeDirectory(nested), nested);
            await RunAsync(shell == LocalShellKind.PowerShell
                ? $"Push-Location -LiteralPath '{QuotePowerShell(root)}'"
                : $"pushd \"{root}\"", root);
            await RunAsync(shell == LocalShellKind.PowerShell ? "Pop-Location" : "popd", nested);
            await RunAsync(shell == LocalShellKind.PowerShell ? "Set-Location .." : "cd ..", root);
            if (shell == LocalShellKind.PowerShell)
            {
                int count;
                lock (gate) count = reports.Count;
                await RunAsync("[Console]::Write([char]27 + ']777;sutty-cwd;wrong;C:\\fake' + [char]7); [Console]::Write([char]27 + ']7;file://localhost/C:/fake' + [char]7)", root);
                lock (gate) Assert(reports.Count == count, "ordinary process OSC cannot change local directory");
                await RunAsync("Set-Location Env:\\", string.Empty);
                await RunAsync(ChangeDirectory(nested), nested);
            }
            await terminal.CloseTerminalAsync();
            Assert(terminal.WorkingDirectory.Length == 0, "closing clears the previous process directory");
            lock (gate)
            {
                raw.Clear();
                decoder.Reset();
            }
            await terminal.OpenTerminalAsync(new TerminalSize(100, 30));
            await WaitForPromptAsync(1, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            Console.WriteLine($"Actual {shell} startup, cd, pushd/popd, Unicode paths and reopen directory reports passed.");
        }
        finally
        {
            await terminal.CloseTerminalAsync();
            Directory.Delete(nested);
            Directory.Delete(root);
        }
        return;

        string ChangeDirectory(string path) => shell == LocalShellKind.PowerShell
            ? $"Set-Location -LiteralPath '{QuotePowerShell(path)}'"
            : $"cd /d \"{path}\"";

        async Task RunAsync(string command, string expected)
        {
            int next;
            lock (gate) next = CountPrompts() + 1;
            await terminal.SendTerminalInputAsync(Encoding.UTF8.GetBytes(command + "\r"));
            await WaitForPromptAsync(next, expected);
        }

        int CountPrompts() => raw.ToString().Split("\x1b]133;B", StringSplitOptions.None).Length - 1;

        async Task WaitForPromptAsync(int count, string expected)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(12))
            {
                lock (gate)
                    if (CountPrompts() >= count && string.Equals(terminal.WorkingDirectory, expected, StringComparison.OrdinalIgnoreCase))
                        return;
                await Task.Delay(20);
            }
            lock (gate)
                throw new TimeoutException($"{shell}: expected prompt {count} at '{expected}', actual '{terminal.WorkingDirectory}'. Raw: {raw}");
        }
    }

    private static string QuotePowerShell(string text) => text.Replace("'", "''", StringComparison.Ordinal);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Working-directory self-test failed: " + message);
    }
}
