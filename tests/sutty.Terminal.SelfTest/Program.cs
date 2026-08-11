using sutty.UI.Helpers;
using sutty.Core.Terminal;
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

[SupportedOSPlatform("windows10.0.17763")]
static async Task VerifyLocalConPtyAsync()
{
    Console.WriteLine("Verifying local ConPTY I/O...");
    var terminal = new WindowsConPtyTerminal();
    var output = new StringBuilder();
    var outputGate = new object();
    var decoder = Encoding.UTF8.GetDecoder();
    var sentinel = $"__SUTTY_LOCAL_PTY_{Guid.NewGuid():N}__";
    const string koreanSentinel = "한글-로컬-터미널";
    var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    terminal.TerminalDataReceived += (_, args) =>
    {
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
    var terminal = new WindowsConPtyTerminal();
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
