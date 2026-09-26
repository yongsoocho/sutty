using sutty.UI.Helpers;
using sutty.Core.Plugins;
using sutty.Core.Terminal;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;

var screen = new VtScreenBuffer(columns: 20, rows: 5, maxScrollback: 3);

Feed("\x1b[?25labc\rZ");
Assert(Line(0) == "Zbc", "carriage return overwrites at the current row");

Feed("\x1b[2;3Hhello\x1b[2D!\x1b[K");
Assert(Line(1) == "  hel!", "cursor addressing and erase-line are interpreted");

Feed("\x1b[31mRED\x1b[0m");
Assert(!screen.Render().Contains('\x1b'), "SGR is consumed instead of rendered");
Assert(screen.Render().Contains("RED"), "styled text remains visible");

screen.Reset();
Feed("\x1b[?25lmain");
Feed("\x1b[?1049hALT");
Assert(screen.IsAlternateScreen && screen.Render().Contains("ALT"), "alternate screen is entered");
Assert(!screen.Render().Contains("main"), "alternate screen does not expose main screen");
Feed("\x1b[?1049l");
Assert(!screen.IsAlternateScreen && screen.Render().Contains("main"), "main screen is restored");

screen.Reset();
var korean = Encoding.UTF8.GetBytes("한글");
screen.Feed(korean.AsSpan(0, 2));
screen.Feed(korean.AsSpan(2));
Feed("\x1b[?25l");
Assert(screen.Render().Contains("한글"), "split UTF-8 sequences decode incrementally");

screen.Reset();
Feed("\x1b[?25l1\r\n2\r\n3\r\n4\r\n5\r\n6\r\n7");
Assert(screen.Render().Split('\n').Length <= 8, "scrollback remains bounded");
Assert(screen.Render().Contains('7'), "scrolling preserves newest output");

string? response = null;
screen.ResponseRequested += value => response = value;
Feed("\x1b[3;4H\x1b[6n");
Assert(response == "\x1b[3;4R", "cursor-position queries receive a VT response");

Feed("\x1b[?1h");
Assert(screen.ApplicationCursorKeys, "DECCKM enables SS3 application cursor keys");
Feed("\x1b[?1l");
Assert(!screen.ApplicationCursorKeys, "DECCKM reset restores normal cursor keys");

screen.Reset();
Feed("ABC\x1b[1D");
Assert(screen.Render().Contains("C\u0332", StringComparison.Ordinal),
    "cursor underlines occupied cells without hiding their character");

var captureBegin = $"__SUTTY_BROADCAST_BEGIN_{Guid.NewGuid():N}__";
var captureEnd = $"__SUTTY_BROADCAST_END_{Guid.NewGuid():N}__";
var broadcastCapture = new TerminalBroadcastCapture(captureBegin, captureEnd);
var capturedWire = Encoding.UTF8.GetBytes(
    $"PS> echo {captureBegin}\r\n{captureBegin}\r\n" +
    $"PS> Write-Output result\r\nresult\r\nPS> echo {captureEnd}\r\n{captureEnd}\r\n");
for (var offset = 0; offset < capturedWire.Length;)
{
    var length = Math.Min(7, capturedWire.Length - offset);
    broadcastCapture.Feed(capturedWire.AsSpan(offset, length));
    offset += length;
}
var capturedBroadcastOutput = await broadcastCapture.Completion;
Assert(capturedBroadcastOutput.Contains("result", StringComparison.Ordinal),
    "broadcast output markers survive packet splitting");
Assert(!capturedBroadcastOutput.Contains(captureBegin, StringComparison.Ordinal),
    "echoed begin-marker command is not mistaken for marker output");

var coloredBegin = $"__SUTTY_BROADCAST_BEGIN_{Guid.NewGuid():N}__";
var coloredEnd = $"__SUTTY_BROADCAST_END_{Guid.NewGuid():N}__";
var coloredCapture = new TerminalBroadcastCapture(coloredBegin, coloredEnd);
coloredCapture.Feed(Encoding.UTF8.GetBytes(
    $"\x1b[93mPS> echo \x1b[37m{coloredBegin}\r\n" +
    $"\x1b[38;5;9m{coloredBegin}\x1b[0m\r\n" +
    $"colored result\r\n" +
    $"\x1b[93mPS> echo \x1b[37m{coloredEnd}\r\n" +
    $"\x1b[38;5;9m{coloredEnd}\x1b[0m\r\n"));
var coloredOutput = await coloredCapture.Completion;
Assert(coloredOutput.Contains("colored result", StringComparison.Ordinal),
    "ANSI-colored shell markers complete broadcast capture");

var positionedBegin = $"__SUTTY_BROADCAST_BEGIN_{Guid.NewGuid():N}__";
var positionedEnd = $"__SUTTY_BROADCAST_END_{Guid.NewGuid():N}__";
var positionedCapture = new TerminalBroadcastCapture(positionedBegin, positionedEnd);
var positionedWire = Encoding.UTF8.GetBytes(
    $"C:\\>echo {positionedBegin}\r\n{positionedBegin}\x1b[2;1H" +
    "\x1b]133;A\x1b\\C:\\>\x1b]133;B\x1b\\echo result\r\nresult\x1b[3;1H" +
    $"\x1b]133;A\x1b\\C:\\>\x1b]133;B\x1b\\echo {positionedEnd}\r\n" +
    $"{positionedEnd}\x1b[4;1H\x1b[?25h\x1b]133;A\x1b\\C:\\>");
foreach (var value in positionedWire)
    positionedCapture.Feed(new[] { value });
Assert(positionedCapture.Completion.IsCompletedSuccessfully,
    "CMD cursor-position and OSC prompt boundaries finish packet-split broadcast markers");
Assert((await positionedCapture.Completion).Contains("result", StringComparison.Ordinal),
    "CMD optimized output keeps actual broadcast content");

var partialBegin = $"__SUTTY_BROADCAST_BEGIN_{Guid.NewGuid():N}__";
var partialEnd = $"__SUTTY_BROADCAST_END_{Guid.NewGuid():N}__";
var partialCapture = new TerminalBroadcastCapture(partialBegin, partialEnd);
partialCapture.Feed(Encoding.UTF8.GetBytes($"{partialBegin}\r\npartial result\r\n"));
Assert(partialCapture.Snapshot().Contains("partial result", StringComparison.Ordinal),
    "broadcast timeout recovery preserves partial output");

var failedCapture = new TerminalBroadcastCapture(
    $"__SUTTY_BROADCAST_BEGIN_{Guid.NewGuid():N}__",
    $"__SUTTY_BROADCAST_END_{Guid.NewGuid():N}__");
failedCapture.Fail(new InvalidOperationException("terminal closed"));
var captureFailed = false;
try
{
    await failedCapture.Completion;
}
catch (InvalidOperationException)
{
    captureFailed = true;
}
Assert(captureFailed, "terminal closure releases a pending broadcast capture");

var json = "{\"service\":\"api\",\"replicas\":3,\"ready\":true}";
var jsonSpans = TerminalTextClassifier.Classify(json);
Assert(HasKind(jsonSpans, TerminalTextHighlightKind.Property),
    "JSON properties are classified");
Assert(HasKind(jsonSpans, TerminalTextHighlightKind.String),
    "JSON strings are classified");
Assert(HasKind(jsonSpans, TerminalTextHighlightKind.Number),
    "JSON numbers are classified");

var yaml = "service: api\nreplicas: 3\n# rollout warning";
var yamlSpans = TerminalTextClassifier.Classify(yaml);
Assert(HasKind(yamlSpans, TerminalTextHighlightKind.Property),
    "YAML properties are classified");
Assert(HasKind(yamlSpans, TerminalTextHighlightKind.Comment),
    "YAML comments are classified");
Assert(HasKind(yamlSpans, TerminalTextHighlightKind.Warning),
    "warning terms are classified");

var dangerSpans = TerminalTextClassifier.Classify("sudo rm -rf /srv/cache");
Assert(HasKind(dangerSpans, TerminalTextHighlightKind.Critical),
    "dangerous commands are classified as critical");

var suggestions = new CommandSuggestionEngine();
var suggestion = suggestions.Suggest(new CommandSuggestionRequest(
    "kubectl get",
    ["kubectl get pods", "kubectl get services"],
    ["kubectl get nodes"]));
Assert(suggestion?.Text == "kubectl get services",
    "newest matching command is suggested first");
Assert(suggestions.Suggest(new CommandSuggestionRequest("no-match", [], [])) is null,
    "suggestion engine leaves unmatched input unchanged");

if (OperatingSystem.IsWindows())
{
    LocalWorkingDirectorySelfTests.VerifyParser();
    var installedFonts = await InstalledFontCatalog.GetAsync();
    Assert(installedFonts.Count > 0, "Windows font families are enumerated for Settings");
    Assert(installedFonts.All(font => !font.StartsWith('@')),
        "vertical aliases are excluded from the Settings font list");
}

VerifyPackagedRenderer();
VerifyLocalTerminalLaunchPlans();
ShellTabLifetimeSelfTests.Run();

if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
{
    if (args is ["--capture-copy-replay", var replayDirectory])
        await TerminalOutputCopyReplay.CaptureAsync(replayDirectory);

    await VerifyDirectExecutableConPtyAsync();
    foreach (var shellKind in Enum.GetValues<LocalShellKind>())
    {
        await VerifyLocalConPtyAsync(shellKind);
        await LocalWorkingDirectorySelfTests.VerifyNativeAsync(shellKind);
        await LocalWorkingDirectorySelfTests.VerifyNativeAsync(shellKind, outputCodePage: 437);
        await VerifyCloseDuringHeavyOutputAsync(shellKind);
    }
}

Console.WriteLine("Terminal VT and local ConPTY self-tests passed.");
return;

void Feed(string value) => screen.Feed(Encoding.UTF8.GetBytes(value));

string Line(int index) => screen.Render().Split('\n')[index];

static void Assert(bool condition, string description)
{
    if (!condition)
        throw new InvalidOperationException($"Self-test failed: {description}");
}

static bool HasKind(
    IReadOnlyList<TerminalTextSpan> spans,
    TerminalTextHighlightKind kind) => spans.Any(span => span.Kind == kind);

static void VerifyLocalTerminalLaunchPlans()
{
    Console.WriteLine("Verifying direct local command launch plans...");
    var scratch = Path.Combine(Path.GetTempPath(), "sutty-terminal-launch-" + Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(scratch);
        var sshPath = Path.Combine(scratch, "ssh.exe");
        var multipassPath = Path.Combine(scratch, "multipass.exe");
        var wslPath = Path.Combine(scratch, "wsl.exe");
        var wrongPath = Path.Combine(scratch, "wrong.exe");
        File.WriteAllBytes(sshPath, []);
        File.WriteAllBytes(multipassPath, []);
        File.WriteAllBytes(wslPath, []);
        File.WriteAllBytes(wrongPath, []);
        var resolver = new TestTerminalExecutableResolver(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ssh.exe"] = sshPath,
            ["multipass.exe"] = multipassPath,
            ["wsl.exe"] = wslPath,
        });

        var ssh = LocalTerminalLaunchPlanner.Create("ssh worker1", resolver);
        Assert(ssh.Kind == LocalTerminalLaunchKind.OpenSsh,
            "OpenSSH configuration alias creates an SSH launch plan");
        Assert(ssh.ExecutablePath == Path.GetFullPath(sshPath) &&
               ssh.Arguments.SequenceEqual(["worker1"]),
            "SSH plan keeps a direct executable and exact argv");
        Assert(ssh.CanonicalCommand == "ssh worker1" && ssh.LaunchTitle == "SSH · worker1",
            "SSH plan has a canonical portable command and tab title");
        Assert(ssh.Arguments is not string[], "launch argv cannot be mutated by casting the public list");
        var sshStart = ssh.CreateProcessStartInfo();
        Assert(!sshStart.UseShellExecute && sshStart.FileName == sshPath &&
               sshStart.ArgumentList.SequenceEqual(ssh.Arguments),
            "SSH plan produces structured no-shell ProcessStartInfo");

        var withKey = LocalTerminalLaunchPlanner.Create(
            "ssh -p 2222 -i \"C:\\Users\\dev user\\.ssh\\id_ed25519\" worker1",
            resolver);
        Assert(withKey.Arguments.SequenceEqual(
                ["-p", "2222", "-i", "C:\\Users\\dev user\\.ssh\\id_ed25519", "worker1"]),
            "quoted SSH key paths stay one direct argv item");
        var replannedKey = LocalTerminalLaunchPlanner.Create(withKey.CanonicalCommand, resolver);
        Assert(replannedKey.Arguments.SequenceEqual(withKey.Arguments),
            "canonical commands retain quoted key paths when planned again");

        var multipass = LocalTerminalLaunchPlanner.Create("multipass connect master", resolver);
        Assert(multipass.Kind == LocalTerminalLaunchKind.MultipassConnect &&
               multipass.LaunchTitle == "Multipass · master" &&
               multipass.Arguments.SequenceEqual(["connect", "master"]),
            "Multipass connect gets a direct labeled connection plan");

        var wsl = LocalTerminalLaunchPlanner.Create("wsl -d Ubuntu", resolver);
        Assert(wsl.Kind == LocalTerminalLaunchKind.DirectExecutable &&
               wsl.CanonicalCommand == "wsl -d Ubuntu" &&
               wsl.Arguments.SequenceEqual(["-d", "Ubuntu"]),
            "other direct PATH executables remain available without shell evaluation");

        ExpectLaunchPlanFailure("ssh worker1 whoami", resolver,
            "SSH connection shortcut rejects a remote command");
        ExpectLaunchPlanFailure("ssh worker1 & whoami", resolver,
            "shell command chaining is rejected before launch");
        ExpectLaunchPlanFailure("cmd /c whoami", resolver,
            "cmd.exe is never an approved direct launcher");
        ExpectLaunchPlanFailure("powershell -NoProfile", resolver,
            "PowerShell is never an approved direct launcher");
        ExpectLaunchPlanFailure("multipass connect master extra", resolver,
            "Multipass connection shortcut has a bounded form");
        ExpectLaunchPlanFailure("ssh \"unterminated", resolver,
            "unmatched quotes are rejected");
        ExpectLaunchPlanFailure("ssh worker1\u2028", resolver,
            "Unicode line separators are rejected from one-line commands");
        ExpectLaunchPlanFailure("ssh worker1\r\n", resolver,
            "newline command injection is rejected");

        var wrongResolver = new TestTerminalExecutableResolver(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ssh.exe"] = wrongPath,
        });
        ExpectLaunchPlanFailure("ssh worker1", wrongResolver,
            "resolver paths must match the planned executable filename");
        var relativeResolver = new TestTerminalExecutableResolver(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ssh.exe"] = "ssh.exe",
        });
        ExpectLaunchPlanFailure("ssh worker1", relativeResolver,
            "relative resolver results are rejected before process creation");
        Console.WriteLine("Direct local command launch plan self-tests passed.");
    }
    finally
    {
        if (Directory.Exists(scratch))
            Directory.Delete(scratch, recursive: true);
    }
}

static void ExpectLaunchPlanFailure(
    string command,
    ILocalTerminalExecutableResolver resolver,
    string description)
{
    Assert(!LocalTerminalLaunchPlanner.TryCreate(command, out var plan, out var error, resolver) &&
           plan is null && !string.IsNullOrWhiteSpace(error),
        description);
}

[SupportedOSPlatform("windows10.0.17763")]
static async Task VerifyDirectExecutableConPtyAsync()
{
    Console.WriteLine("Verifying direct executable ConPTY launch...");
    var wherePath = Path.Combine(Environment.SystemDirectory, "where.exe");
    Assert(File.Exists(wherePath), "Windows where.exe is available for direct ConPTY launch test");
    var resolver = new TestTerminalExecutableResolver(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
    {
        ["where.exe"] = wherePath,
    });
    var plan = LocalTerminalLaunchPlanner.Create("where __sutty_command_that_does_not_exist__", resolver);
    var terminal = new WindowsConPtyTerminal(plan);
    var completed = new TaskCompletionSource<TerminalState>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    terminal.TerminalStateChanged += (_, state) =>
    {
        if (state is TerminalState.Closed or TerminalState.Failed)
            completed.TrySetResult(state);
    };

    try
    {
        await terminal.OpenTerminalAsync(new TerminalSize(80, 24));
        var finalState = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(finalState == TerminalState.Closed && terminal.LaunchPlan == plan,
            "direct executable owns and cleanly closes its ConPTY tab without a shell");
        Assert(terminal.WorkingDirectory.Length == 0,
            "a direct executable does not invent a local working directory");
    }
    finally
    {
        await terminal.CloseTerminalAsync();
    }
}

static void VerifyPackagedRenderer()
{
    var assetDirectory = Path.Combine(AppContext.BaseDirectory, "TerminalAssets");
    var html = File.ReadAllText(Path.Combine(assetDirectory, "index.html"));
    var bridge = File.ReadAllText(Path.Combine(assetDirectory, "sutty-terminal.js"));
    var xtermPath = Path.Combine(assetDirectory, "xterm-6.0.0.js");

    Assert(html.Contains("default-src 'none'", StringComparison.Ordinal),
        "terminal renderer denies resources by default");
    Assert(html.Contains("connect-src 'none'", StringComparison.Ordinal),
        "terminal renderer cannot open network connections");
    Assert(!html.Contains("http://", StringComparison.OrdinalIgnoreCase) &&
           !html.Contains("https://", StringComparison.OrdinalIgnoreCase),
        "terminal renderer has no remote asset URL");
    Assert(!bridge.Contains("innerHTML", StringComparison.Ordinal),
        "terminal bridge does not inject terminal data into HTML");
    Assert(bridge.Contains("terminal.onData", StringComparison.Ordinal) &&
           bridge.Contains("terminal.write", StringComparison.Ordinal) &&
           bridge.Contains("writeComplete", StringComparison.Ordinal) &&
           bridge.Contains("ResizeObserver", StringComparison.Ordinal),
        "terminal bridge covers input, output acknowledgement, and resize");
    Assert(bridge.Contains("type: 'appShortcut'", StringComparison.Ordinal) &&
           bridge.Contains("action: 'navigate'", StringComparison.Ordinal) &&
           bridge.Contains("action = 'selectTab'", StringComparison.Ordinal) &&
           bridge.Contains("action = 'newTab'", StringComparison.Ordinal) &&
           bridge.Contains("action = 'settings'", StringComparison.Ordinal) &&
           bridge.Contains("event.preventDefault()", StringComparison.Ordinal) &&
           bridge.Contains("document.addEventListener('keydown', handleAppShortcut, true)", StringComparison.Ordinal) &&
           bridge.Contains("/^Digit([1-8])$/", StringComparison.Ordinal) &&
           bridge.Contains("/^Digit([1-9])$/", StringComparison.Ordinal) &&
           !bridge.Contains("/^Numpad([1-8])$/", StringComparison.Ordinal),
        "terminal bridge captures documented Alt and Ctrl application shortcuts for all WebView focus targets without intercepting AltGr or numpad input");

    // Git may materialize text assets with CRLF on Windows. Pin the reviewed content
    // independently of that checkout-only line-ending conversion.
    var normalizedXterm = File.ReadAllText(xtermPath).Replace("\r\n", "\n", StringComparison.Ordinal);
    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedXterm)));
    Assert(hash == "14903579FF54664CD72F8E8699E6961A6272C21863EC1C3B118CDC8AF5D4A972",
        "packaged xterm.js bytes match the reviewed version");
}

[SupportedOSPlatform("windows10.0.17763")]
static async Task VerifyLocalConPtyAsync(LocalShellKind shellKind)
{
    Console.WriteLine($"Verifying local {shellKind} ConPTY I/O...");
    var terminal = new WindowsConPtyTerminal(shellKind, loadProfile: false);
    var output = new StringBuilder();
    var outputGate = new object();
    var decoder = Encoding.UTF8.GetDecoder();
    var sentinelId = Guid.NewGuid().ToString("N");
    var sentinel = $"__SUTTY_LOCAL_PTY_{sentinelId}__";
    const string koreanSentinel = "한글-로컬-터미널";
    var broadcastBegin = $"__SUTTY_BROADCAST_BEGIN_{Guid.NewGuid():N}__";
    var broadcastResult = $"__SUTTY_BROADCAST_RESULT_{Guid.NewGuid():N}__";
    var broadcastEnd = $"__SUTTY_BROADCAST_END_{Guid.NewGuid():N}__";
    var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var liveBroadcastCapture = new TerminalBroadcastCapture(broadcastBegin, broadcastEnd);
    var shellExited = new TaskCompletionSource<TerminalState>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    terminal.TerminalStateChanged += (_, state) =>
    {
        if (state is TerminalState.Closed or TerminalState.Failed)
            shellExited.TrySetResult(state);
    };

    terminal.TerminalDataReceived += (_, args) =>
    {
        liveBroadcastCapture.Feed(args.Data.Span);
        lock (outputGate)
        {
            var characters = new char[Encoding.UTF8.GetMaxCharCount(args.Data.Length)];
            var characterCount = decoder.GetChars(args.Data.Span, characters, flush: false);
            output.Append(characters, 0, characterCount);
            var snapshot = output.ToString();
            if (snapshot.Contains(sentinel, StringComparison.Ordinal) &&
                snapshot.Contains(koreanSentinel, StringComparison.Ordinal))
            {
                observed.TrySetResult();
            }
        }
    };

    try
    {
        await terminal.OpenTerminalAsync(new TerminalSize(80, 24));
        Assert(terminal.TerminalState == TerminalState.Open, $"local {shellKind} ConPTY opens");

        // Construct the expected value inside each shell: echoed keystrokes alone
        // must not satisfy the execution check.
        var probeCommand = shellKind == LocalShellKind.CommandPrompt
            ? $"set __SUTTY_PROBE={sentinelId}\recho __SUTTY_LOCAL_PTY_%__SUTTY_PROBE%__\recho {koreanSentinel}\r"
            : $"Write-Output ('__SUTTY_LOCAL_PTY_' + '{sentinelId}__'); Write-Output '{koreanSentinel}'\r";
        await terminal.SendTerminalInputAsync(
            Encoding.UTF8.GetBytes(probeCommand));
        try
        {
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException error)
        {
            string captured;
            lock (outputGate)
                captured = output.ToString();
            throw new TimeoutException(
                $"Local {shellKind} ConPTY did not execute the probe. Captured: {captured}", error);
        }
        Assert(await terminal.ResizeTerminalAsync(new TerminalSize(100, 30)),
            "local ConPTY resizes");
        lock (outputGate)
        {
            var integratedOutput = output.ToString();
            Assert(integratedOutput.Contains("\x1b]133;A", StringComparison.Ordinal) &&
                   integratedOutput.Contains("\x1b]133;B", StringComparison.Ordinal),
                $"{shellKind} exposes explicit prompt boundaries for last-output copying");
        }
        lock (outputGate)
            output.Clear();
        var broadcastCommand = shellKind == LocalShellKind.CommandPrompt
            ? $"echo {broadcastResult}"
            : $"Write-Output '{broadcastResult}'";
        await terminal.SendTerminalInputAsync(
            Encoding.UTF8.GetBytes(
                $"echo {broadcastBegin}\r{broadcastCommand}\recho {broadcastEnd}\r"));
        string liveBroadcastOutput;
        try
        {
            liveBroadcastOutput = await liveBroadcastCapture.Completion
                .WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException error)
        {
            string captured;
            lock (outputGate)
                captured = output.ToString();
            throw new TimeoutException(
                $"Local broadcast markers did not complete. " +
                $"Partial marker output: {liveBroadcastCapture.Snapshot()} " +
                $"Captured terminal output: {captured}",
                error);
        }
        Assert(liveBroadcastOutput.Contains(broadcastResult, StringComparison.Ordinal),
            "portable broadcast markers delimit actual local shell output");
        Assert(!liveBroadcastOutput.Contains(broadcastBegin, StringComparison.Ordinal),
            "actual local capture excludes begin marker");
        await terminal.SendTerminalInputAsync(Encoding.UTF8.GetBytes("exit\r"));
        var exitState = await shellExited.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(exitState == TerminalState.Closed && terminal.LastTerminalError is null,
            $"{shellKind} exit retires its terminal without failure");
        await terminal.OpenTerminalAsync(new TerminalSize(80, 24));
        Assert(terminal.TerminalState == TerminalState.Open,
            $"{shellKind} terminal reopens after the shell exits");
        Console.WriteLine($"Local {shellKind} ConPTY I/O, resize, exit, and reopen passed; closing...");
    }
    finally
    {
        await terminal.CloseTerminalAsync();
    }

    Assert(terminal.TerminalState == TerminalState.Closed, "local ConPTY closes");
    Console.WriteLine($"Local {shellKind} ConPTY normal close passed.");
}

[SupportedOSPlatform("windows10.0.17763")]
static async Task VerifyCloseDuringHeavyOutputAsync(LocalShellKind shellKind)
{
    Console.WriteLine($"Verifying local {shellKind} ConPTY close under heavy output...");
    var terminal = new WindowsConPtyTerminal(shellKind, loadProfile: false);
    var readerPaused = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    using var releaseReader = new ManualResetEventSlim(false);
    long receivedBytes = 0;
    var pauseOnce = 0;
    var closeStarted = false;

    terminal.TerminalDataReceived += (_, args) =>
    {
        var total = Interlocked.Add(ref receivedBytes, args.Data.Length);
        if (total < 512 * 1024 || Interlocked.Exchange(ref pauseOnce, 1) != 0)
            return;

        readerPaused.TrySetResult();
        // Self-release prevents a failed assertion from pinning the reader forever.
        releaseReader.Wait(TimeSpan.FromSeconds(5));
    };

    try
    {
        await terminal.OpenTerminalAsync(new TerminalSize(80, 24))
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert(terminal.TerminalState == TerminalState.Open,
            "local ConPTY opens for teardown stress");

        var flood = shellKind == LocalShellKind.CommandPrompt
            ? $"for /L %i in (1,1,1000000) do @echo {new string('x', 4096)}\r"
            : "$chunk='x'*4096; while ($true) { [Console]::Out.WriteLine($chunk) }\r";
        await terminal.SendTerminalInputAsync(Encoding.UTF8.GetBytes(flood))
            .WaitAsync(TimeSpan.FromSeconds(5));
        await readerPaused.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Ensure the producer has filled the synchronous output channel before close.
        await Task.Delay(250);
        closeStarted = true;
#pragma warning disable CA1416 // This function is reached only from the guarded Windows path.
        var closeTask = Task.Run(() => terminal.CloseTerminalAsync());
#pragma warning restore CA1416
        await Task.Delay(250);
        releaseReader.Set();

        try
        {
            await closeTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException error)
        {
            throw new TimeoutException(
                $"Local ConPTY teardown stalled after " +
                $"{Interlocked.Read(ref receivedBytes):N0} output bytes.",
                error);
        }

        Assert(terminal.TerminalState == TerminalState.Closed,
            "local ConPTY closes while output exceeds pipe capacity");
    }
    catch
    {
        releaseReader.Set();
        if (!closeStarted)
        {
            try
            {
#pragma warning disable CA1416 // This function is reached only from the guarded Windows path.
                await Task.Run(() => terminal.CloseTerminalAsync())
                    .WaitAsync(TimeSpan.FromSeconds(5));
#pragma warning restore CA1416
            }
            catch { }
        }
        throw;
    }
    finally
    {
        releaseReader.Set();
    }
}

sealed class TestTerminalExecutableResolver(
    IReadOnlyDictionary<string, string?> executables) : ILocalTerminalExecutableResolver
{
    public string? ResolveExecutable(string executableFileName) =>
        executables.TryGetValue(executableFileName, out var value) ? value : null;
}
