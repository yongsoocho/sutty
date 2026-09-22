using sutty.Core.Terminal;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

/// <summary>Records actual ConPTY traffic for replay through the packaged xterm renderer.</summary>
internal static class TerminalOutputCopyReplay
{
    [SupportedOSPlatform("windows10.0.17763")]
    public static async Task CaptureAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var shell in Enum.GetValues<LocalShellKind>())
        {
            var terminal = new WindowsConPtyTerminal(shell, loadProfile: false);
            var gate = new object();
            var records = new List<ReplayEvent>();
            var raw = new StringBuilder();
            var decoder = Encoding.UTF8.GetDecoder();
            terminal.TerminalDataReceived += (_, args) =>
            {
                lock (gate)
                {
                    records.Add(new("write", Convert.ToBase64String(args.Data.Span)));
                    var chars = new char[Encoding.UTF8.GetMaxCharCount(args.Data.Length)];
                    var length = decoder.GetChars(args.Data.Span, chars, false);
                    raw.Append(chars, 0, length);
                }
            };
            try
            {
                await terminal.OpenTerminalAsync(new TerminalSize(80, 24));
                await WaitForPromptAsync(1);
                if (shell == LocalShellKind.PowerShell)
                    await RunAsync("Set-PSReadLineOption -HistorySaveStyle SaveNothing", null);
                var command = shell == LocalShellKind.PowerShell
                    ? "[Console]::Out.Write(\"`r`n  stdout  `r`n\"); [Console]::Error.Write(\"  stderr  `r`n`r`n\")"
                    : "echo(&echo(  stdout  &echo(  stderr  1>&2&echo(";
                await RunAsync(command, "\r\n  stdout  \r\n  stderr  \r\n\r\n");
                await RunAsync(shell == LocalShellKind.PowerShell ? "$null = 1" : "cd .", "");
                await RunAsync(shell == LocalShellKind.PowerShell
                    ? "[Console]::Write('  no newline  ')"
                    : "<nul set /p \"=  no newline  \"",
                    shell == LocalShellKind.PowerShell ? "  no newline\r\n" : "no newline  ");
                await File.WriteAllTextAsync(Path.Combine(directory, shell + ".json"),
                    JsonSerializer.Serialize(records));
            }
            finally
            {
                await terminal.CloseTerminalAsync();
            }

            async Task RunAsync(string command, string? expected)
            {
                int nextPrompt;
                lock (gate)
                {
                    nextPrompt = CountPrompts() + 1;
                    records.Add(new("input", command + "\r"));
                }
                await terminal.SendTerminalInputAsync(Encoding.UTF8.GetBytes(command + "\r"));
                await WaitForPromptAsync(nextPrompt);
                if (expected is not null)
                    lock (gate)
                        records.Add(new("snapshot", expected));
            }

            int CountPrompts() => raw.ToString().Split("\x1b]133;B", StringSplitOptions.None).Length - 1;

            async Task WaitForPromptAsync(int count)
            {
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed < TimeSpan.FromSeconds(10))
                {
                    lock (gate)
                        if (CountPrompts() >= count) return;
                    await Task.Delay(25);
                }
                throw new TimeoutException($"{shell} did not display prompt {count} for output-copy replay.");
            }
        }
    }

    private sealed record ReplayEvent(string Type, string Data);
}
