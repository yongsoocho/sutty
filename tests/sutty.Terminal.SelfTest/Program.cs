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
    var installedFonts = await InstalledFontCatalog.GetAsync();
    Assert(installedFonts.Count > 0, "Windows font families are enumerated for Settings");
    Assert(installedFonts.All(font => !font.StartsWith('@')),
        "vertical aliases are excluded from the Settings font list");
}

VerifyPackagedRenderer();

if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
{
    await VerifyLocalConPtyAsync();
    await VerifyCloseDuringHeavyOutputAsync();
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

    var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(xtermPath)));
    Assert(hash == "14903579FF54664CD72F8E8699E6961A6272C21863EC1C3B118CDC8AF5D4A972",
        "packaged xterm.js bytes match the reviewed version");
}

[SupportedOSPlatform("windows10.0.17763")]
static async Task VerifyLocalConPtyAsync()
{
    Console.WriteLine("Verifying local ConPTY I/O...");
    var terminal = new WindowsConPtyTerminal(loadProfile: false);
    var output = new StringBuilder();
    var outputGate = new object();
    var decoder = Encoding.UTF8.GetDecoder();
    var sentinel = $"__SUTTY_LOCAL_PTY_{Guid.NewGuid():N}__";
    const string koreanSentinel = "한글-로컬-터미널";
    var broadcastBegin = $"__SUTTY_BROADCAST_BEGIN_{Guid.NewGuid():N}__";
    var broadcastResult = $"__SUTTY_BROADCAST_RESULT_{Guid.NewGuid():N}__";
    var broadcastEnd = $"__SUTTY_BROADCAST_END_{Guid.NewGuid():N}__";
    var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var liveBroadcastCapture = new TerminalBroadcastCapture(broadcastBegin, broadcastEnd);

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
        Assert(terminal.TerminalState == TerminalState.Open, "local ConPTY opens");

        await terminal.SendTerminalInputAsync(
            Encoding.UTF8.GetBytes(
                $"Write-Output '{sentinel}'; Write-Output '{koreanSentinel}'\r"));
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
                $"Local ConPTY did not echo the sentinel. Captured: {captured}", error);
        }
        Assert(await terminal.ResizeTerminalAsync(new TerminalSize(100, 30)),
            "local ConPTY resizes");
        lock (outputGate)
            output.Clear();
        await terminal.SendTerminalInputAsync(
            Encoding.UTF8.GetBytes(
                $"echo {broadcastBegin}\rWrite-Output '{broadcastResult}'\recho {broadcastEnd}\r"));
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
        Console.WriteLine("Local ConPTY I/O and resize passed; closing...");
    }
    finally
    {
        await terminal.CloseTerminalAsync();
    }

    Assert(terminal.TerminalState == TerminalState.Closed, "local ConPTY closes");
    Console.WriteLine("Local ConPTY normal close passed.");
}

[SupportedOSPlatform("windows10.0.17763")]
static async Task VerifyCloseDuringHeavyOutputAsync()
{
    Console.WriteLine("Verifying local ConPTY close under heavy output...");
    var terminal = new WindowsConPtyTerminal(loadProfile: false);
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

        const string flood =
            "$chunk='x'*4096; while ($true) { [Console]::Out.WriteLine($chunk) }\r";
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
