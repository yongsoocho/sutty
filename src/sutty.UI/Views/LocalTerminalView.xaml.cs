using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using sutty.Core.Commands;
using sutty.Core.Terminal;
using sutty.Setting;
using sutty.UI.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI;

namespace sutty.UI.Views;

/// <summary>
/// A Windows-local PowerShell tab backed by ConPTY and the package-local terminal renderer.
/// Local processes remain isolated from SSH/SFTP session ownership.
/// </summary>
public sealed partial class LocalTerminalView : UserControl
{
    private const int MaxTerminalBacklogBytes = 4 * 1024 * 1024;
    private const int MaxTerminalDrainBytes = 256 * 1024;

    private readonly object _terminalOutputGate = new();
    private readonly Queue<byte[]> _terminalOutputQueue = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _broadcastCommandGate = new(1, 1);
    private readonly object _broadcastCaptureGate = new();
    private TerminalBroadcastCapture? _broadcastCapture;
    private int _terminalQueuedBytes;
    private long _terminalDroppedBytes;
    private bool _terminalBacklogResetPending;
    private int _terminalDrainQueued;
    private bool _terminalResizeInProgress;
    private int _closed;
    private TerminalSize _requestedTerminalSize = new(120, 40, 0, 0);

    public LocalTerminalView()
        : this(new WindowsConPtyTerminal(
            loadProfile: SettingsService.Current.LoadLocalShellProfile))
    {
    }

    public LocalTerminalView(IInteractiveTerminal terminal)
    {
        Terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        InitializeComponent();

        Terminal.TerminalStateChanged += OnTerminalStateChanged;
        Terminal.TerminalDataReceived += OnTerminalDataReceived;
        TerminalSurface.InputReceived += (_, data) => _ = SendTerminalTextAsync(data);
        TerminalSurface.TerminalSizeChanged += TerminalSurface_TerminalSizeChanged;
        TerminalSurface.RendererFailed += (_, message) =>
        {
            TerminalStatusText.Text = Loc.T("터미널 렌더러 오류", "Terminal renderer error");
            ToolTipService.SetToolTip(StatusPill, message);
        };

        ApplyTerminalSettings();
        RefreshLanguage();
        UpdateTerminalStatus(Terminal.TerminalState);
        ActualThemeChanged += (_, _) => UpdateTerminalStatus(Terminal.TerminalState);
    }

    public IInteractiveTerminal Terminal { get; }

    /// <summary>Apply current terminal font settings to this already-open local tab.</summary>
    public void ApplyTerminalSettings()
    {
        var settings = SettingsService.Current;
        var familyName = string.IsNullOrWhiteSpace(settings.TerminalFontFamily)
            ? "Cascadia Mono"
            : settings.TerminalFontFamily.Trim();
        var family = new FontFamily($"{familyName}, Consolas");
        TitleText.FontFamily = family;
        TerminalStatusText.FontFamily = family;
        TerminalSurface.ApplyCurrentSettings();
    }

    /// <summary>Refresh labels that depend on the current Korean/English setting.</summary>
    public void RefreshLanguage()
    {
        SubtitleText.Text = Loc.T(
            $"로컬 · {Environment.UserName}@{Environment.MachineName}",
            $"Local · {Environment.UserName}@{Environment.MachineName}");
        UpdateTerminalStatus(Terminal.TerminalState);
    }

    /// <summary>Close the ConPTY process tree. Unloading alone intentionally does not close it.</summary>
    public async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;

        _lifetimeCancellation.Cancel();
        Terminal.TerminalStateChanged -= OnTerminalStateChanged;
        Terminal.TerminalDataReceived -= OnTerminalDataReceived;
        ClearTerminalBacklog();

        try
        {
            await Terminal.CloseTerminalAsync();
        }
        catch (Exception error)
        {
            Debug.WriteLine($"Local terminal close failed: {error}");
        }
    }

    private void OnTerminalStateChanged(object? sender, TerminalState state)
    {
        if (Volatile.Read(ref _closed) != 0)
            return;

        if (state is TerminalState.Closed or TerminalState.Failed)
        {
            FailActiveBroadcast(new InvalidOperationException(
                Terminal.LastTerminalError ?? "The local terminal closed during broadcast."));
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (Volatile.Read(ref _closed) != 0)
                return;

            if (state == TerminalState.Opening)
            {
                ClearTerminalBacklog();
                TerminalSurface.Reset();
            }

            UpdateTerminalStatus(state);
            if (state == TerminalState.Open)
                TerminalSurface.FocusTerminal();
        });
    }

    private void OnTerminalDataReceived(object? sender, TerminalDataReceivedEventArgs args)
    {
        if (Volatile.Read(ref _closed) != 0)
            return;

        var data = args.Data.ToArray();
        CaptureBroadcastOutput(data);
        lock (_terminalOutputGate)
        {
            if (data.Length > MaxTerminalBacklogBytes)
            {
                _terminalDroppedBytes += _terminalQueuedBytes + data.LongLength;
                _terminalOutputQueue.Clear();
                _terminalQueuedBytes = 0;
                _terminalBacklogResetPending = true;
            }
            else
            {
                if (_terminalQueuedBytes + data.Length > MaxTerminalBacklogBytes)
                {
                    // Arbitrarily trimming a VT stream can split UTF-8 or an escape sequence.
                    // Reset the pending generation instead and show an explicit warning.
                    _terminalDroppedBytes += _terminalQueuedBytes;
                    _terminalOutputQueue.Clear();
                    _terminalQueuedBytes = 0;
                    _terminalBacklogResetPending = true;
                }

                _terminalOutputQueue.Enqueue(data);
                _terminalQueuedBytes += data.Length;
            }
        }

        QueueTerminalDrain();
    }

    private void QueueTerminalDrain()
    {
        if (Volatile.Read(ref _closed) != 0 ||
            Interlocked.Exchange(ref _terminalDrainQueued, 1) != 0)
        {
            return;
        }

        if (!DispatcherQueue.TryEnqueue(DrainTerminalOutput))
        {
            Interlocked.Exchange(ref _terminalDrainQueued, 0);
            ClearTerminalBacklog();
        }
    }

    private void DrainTerminalOutput()
    {
        List<byte[]> batch = [];
        long droppedBytes;
        bool resetScreen;
        lock (_terminalOutputGate)
        {
            var batchBytes = 0;
            while (_terminalOutputQueue.Count > 0 && batchBytes < MaxTerminalDrainBytes)
            {
                var data = _terminalOutputQueue.Dequeue();
                _terminalQueuedBytes -= data.Length;
                batchBytes += data.Length;
                batch.Add(data);
            }

            droppedBytes = _terminalDroppedBytes;
            resetScreen = _terminalBacklogResetPending;
            _terminalDroppedBytes = 0;
            _terminalBacklogResetPending = false;
        }

        if (resetScreen)
        {
            var warning = Loc.T(
                $"[sutty: 터미널 출력이 4 MiB 대기 한도를 초과하여 {droppedBytes:N0}바이트를 버리고 화면을 재설정했습니다.]\r\n",
                $"[sutty: terminal output exceeded the 4 MiB backlog; dropped {droppedBytes:N0} bytes and reset the screen.]\r\n");
            TerminalSurface.Reset(warning);
        }

        foreach (var data in batch)
            TerminalSurface.Write(data);

        Interlocked.Exchange(ref _terminalDrainQueued, 0);
        if (HasTerminalBacklog())
            QueueTerminalDrain();
    }

    private bool HasTerminalBacklog()
    {
        lock (_terminalOutputGate)
            return _terminalOutputQueue.Count > 0 || _terminalBacklogResetPending;
    }

    private void ClearTerminalBacklog()
    {
        lock (_terminalOutputGate)
        {
            _terminalOutputQueue.Clear();
            _terminalQueuedBytes = 0;
            _terminalDroppedBytes = 0;
            _terminalBacklogResetPending = false;
        }
    }

    private async Task EnsureTerminalStartedAsync()
    {
        if (Volatile.Read(ref _closed) != 0 ||
            Terminal.TerminalState is TerminalState.Open or TerminalState.Opening)
        {
            return;
        }

        _requestedTerminalSize = TerminalSurface.ViewportSize;
        ClearTerminalBacklog();
        TerminalSurface.Reset();

        try
        {
            await Terminal.OpenTerminalAsync(
                _requestedTerminalSize,
                _lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // The owning tab is closing.
        }
        catch (Exception error)
        {
            Debug.WriteLine($"Local terminal open failed: {error}");
            UpdateTerminalStatus(Terminal.TerminalState);
        }
    }

    private async Task SendTerminalTextAsync(string text)
    {
        if (Volatile.Read(ref _closed) != 0 ||
            Terminal.TerminalState != TerminalState.Open ||
            string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            await Terminal.SendTerminalInputAsync(
                Encoding.UTF8.GetBytes(text),
                _lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // The owning tab is closing.
        }
        catch (InvalidOperationException) when (Terminal.TerminalState != TerminalState.Open)
        {
            // The shell exited between the state check and the write.
        }
        catch (Exception error)
        {
            Debug.WriteLine($"Local terminal input failed: {error}");
            UpdateTerminalStatus(Terminal.TerminalState);
        }
    }

    /// <summary>
    /// Sends a broadcast command to whichever shell currently owns this ConPTY tab. The
    /// shell may be local PowerShell or a manually opened SSH shell. Portable echo markers
    /// delimit the response without replacing that foreground process.
    /// </summary>
    public async Task<CommandExecutionResult> RunExternalCommandDetailedAsync(
        string command,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token,
            cancellationToken);
        await _broadcastCommandGate.WaitAsync(linkedCancellation.Token);
        try
        {
            if (Terminal.TerminalState != TerminalState.Open)
                throw new InvalidOperationException("Local terminal is not open.");

            var startedAt = DateTimeOffset.UtcNow;
            var started = Stopwatch.GetTimestamp();
            var token = Guid.NewGuid().ToString("N");
            var beginMarker = $"__SUTTY_BROADCAST_BEGIN_{token}__";
            var endMarker = $"__SUTTY_BROADCAST_END_{token}__";
            var capture = new TerminalBroadcastCapture(beginMarker, endMarker);
            lock (_broadcastCaptureGate)
                _broadcastCapture = capture;

            var normalizedCommand = ClipboardHelper.NormalizeTerminalPaste(command).TrimEnd('\r');
            var wireInput = $"echo {beginMarker}\r{normalizedCommand}\recho {endMarker}\r";

            try
            {
                await Terminal.SendTerminalInputAsync(
                    Encoding.UTF8.GetBytes(wireInput),
                    linkedCancellation.Token);
                var output = await capture.Completion.WaitAsync(linkedCancellation.Token);
                return new CommandExecutionResult(
                    command,
                    output,
                    "",
                    null,
                    null,
                    startedAt,
                    Stopwatch.GetElapsedTime(started));
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested &&
                !_lifetimeCancellation.IsCancellationRequested)
            {
                var partialOutput = await InterruptBroadcastAsync(capture);
                return new CommandExecutionResult(
                    command,
                    partialOutput,
                    Loc.T(
                        "브로드캐스트 명령이 취소되었거나 시간 제한을 초과했습니다.",
                        "The broadcast command was cancelled or timed out."),
                    null,
                    "CANCELLED",
                    startedAt,
                    Stopwatch.GetElapsedTime(started));
            }
            finally
            {
                lock (_broadcastCaptureGate)
                {
                    if (ReferenceEquals(_broadcastCapture, capture))
                        _broadcastCapture = null;
                }
            }
        }
        finally
        {
            _broadcastCommandGate.Release();
        }
    }

    private async Task<string> InterruptBroadcastAsync(TerminalBroadcastCapture capture)
    {
        if (Terminal.TerminalState == TerminalState.Open &&
            !_lifetimeCancellation.IsCancellationRequested)
        {
            using var interruptCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCancellation.Token);
            interruptCancellation.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await Terminal.SendTerminalInputAsync(
                    new byte[] { 0x03 },
                    interruptCancellation.Token);
            }
            catch (Exception error) when (error is OperationCanceledException or
                                          InvalidOperationException or IOException)
            {
                Debug.WriteLine($"Broadcast interrupt failed: {error.GetType().Name}");
            }
        }

        try
        {
            return await capture.Completion.WaitAsync(
                TimeSpan.FromSeconds(2),
                _lifetimeCancellation.Token);
        }
        catch (Exception error) when (error is TimeoutException or
                                      OperationCanceledException or
                                      InvalidOperationException)
        {
            Debug.WriteLine($"Broadcast recovery used partial output: {error.GetType().Name}");
            return capture.Snapshot();
        }
    }

    private void FailActiveBroadcast(Exception error)
    {
        TerminalBroadcastCapture? capture;
        lock (_broadcastCaptureGate)
            capture = _broadcastCapture;
        capture?.Fail(error);
    }

    private void CaptureBroadcastOutput(byte[] data)
    {
        TerminalBroadcastCapture? capture;
        lock (_broadcastCaptureGate)
            capture = _broadcastCapture;
        capture?.Feed(data);
    }

    private void TerminalSurface_Loaded(object sender, RoutedEventArgs args)
    {
        if (Volatile.Read(ref _closed) != 0)
            return;

        _requestedTerminalSize = TerminalSurface.ViewportSize;
        TerminalSurface.FocusTerminal();
        _ = EnsureTerminalStartedAsync();
    }

    private void TerminalSurface_TerminalSizeChanged(object? sender, TerminalSize size)
    {
        _requestedTerminalSize = size.Clamp();
        UpdateTerminalStatus(Terminal.TerminalState);

        if (Terminal.TerminalState != TerminalState.Open)
            return;

        // xterm fit events arrive in bursts while the window or splitter is dragged.
        // Keep one worker and coalesce them so ConPTY ends at the latest dimensions.
        if (!_terminalResizeInProgress)
            _ = ResizeTerminalToLatestAsync();
    }

    private async Task ResizeTerminalToLatestAsync()
    {
        if (_terminalResizeInProgress)
            return;

        _terminalResizeInProgress = true;
        try
        {
            while (Volatile.Read(ref _closed) == 0 &&
                   Terminal.TerminalState == TerminalState.Open)
            {
                var pending = _requestedTerminalSize;
                if (!await Terminal.ResizeTerminalAsync(
                        pending,
                        _lifetimeCancellation.Token))
                {
                    break;
                }

                UpdateTerminalStatus(Terminal.TerminalState);

                if (pending == _requestedTerminalSize)
                    break;
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // The owning tab is closing.
        }
        catch (Exception error)
        {
            Debug.WriteLine($"Local terminal resize failed: {error}");
        }
        finally
        {
            _terminalResizeInProgress = false;
        }
    }

    private void UpdateTerminalStatus(TerminalState state)
    {
        var (pillLabel, statusLabel, resourceKey) = state switch
        {
            TerminalState.Opening =>
                ("STARTING", Loc.T("CONPTY · 시작 중", "CONPTY · starting"), "StatusAmber"),
            TerminalState.Open =>
                ("RUNNING", $"CONPTY {_requestedTerminalSize.Columns}\u00D7{_requestedTerminalSize.Rows}", "StatusGreen"),
            TerminalState.Failed =>
                ("FAILED", Loc.T("CONPTY · 오류", "CONPTY · error"), "StatusRed"),
            _ =>
                ("CLOSED", Loc.T("CONPTY · 닫힘", "CONPTY · closed"), "StatusIdle"),
        };

        var foreground = ThemeResources.Brush(this, resourceKey);
        var color = foreground is SolidColorBrush solid
            ? solid.Color
            : Color.FromArgb(255, 0x6E, 0x7C, 0x8B);

        StatusPillText.Text = pillLabel;
        StatusPillText.Foreground = foreground;
        StatusPill.Background = new SolidColorBrush(Color.FromArgb(36, color.R, color.G, color.B));
        TerminalStatusText.Text = statusLabel;
        TerminalStatusText.Foreground = foreground;
        ToolTipService.SetToolTip(
            StatusPill,
            state == TerminalState.Failed
                ? Terminal.LastTerminalError ?? Loc.T("알 수 없는 오류", "Unknown error")
                : null);
    }

}
