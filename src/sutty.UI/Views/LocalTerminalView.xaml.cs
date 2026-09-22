using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using sutty.Core.Commands;
using sutty.Core.Terminal;
using sutty.Setting;
using sutty.UI.Controls;
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
    private readonly LocalTerminalLaunchPlan? _launchPlan;
    private readonly ILocalWorkingDirectoryTerminal? _workingDirectorySource;
    private string _lastNotifiedWorkingDirectory = string.Empty;
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
    private int _directLaunchAttempted;
    private int _closed;
    private TerminalSize _requestedTerminalSize = new(120, 40, 0, 0);

    /// <summary>Raised when xterm consumes an application-level shortcut.</summary>
    public event EventHandler<TerminalAppShortcutRequest>? AppShortcutRequested;

    public LocalTerminalView()
        : this(LocalShellKind.PowerShell)
    {
    }

    public LocalTerminalView(LocalShellKind shellKind)
        : this(new WindowsConPtyTerminal(shellKind,
            loadProfile: SettingsService.Current.LoadLocalShellProfile), null, shellKind)
    {
    }

    /// <summary>
    /// Opens an already-validated local connection command directly inside this
    /// ConPTY tab. The command is not replayed during workspace restore.
    /// </summary>
    public LocalTerminalView(LocalTerminalLaunchPlan launchPlan)
        : this(
            new WindowsConPtyTerminal(launchPlan ?? throw new ArgumentNullException(nameof(launchPlan))),
            launchPlan)
    {
    }

    public LocalTerminalView(IInteractiveTerminal terminal)
        : this(terminal, null)
    {
    }

    private LocalTerminalView(
        IInteractiveTerminal terminal,
        LocalTerminalLaunchPlan? launchPlan,
        LocalShellKind shellKind = LocalShellKind.PowerShell)
    {
        Terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        _workingDirectorySource = terminal as ILocalWorkingDirectoryTerminal;
        _launchPlan = launchPlan;
        ShellKind = shellKind;
        InitializeComponent();
        TerminalSurface.OutputShell = launchPlan is null && shellKind == LocalShellKind.CommandPrompt
            ? "cmd" : null;
        SizeChanged += (_, args) =>
        {
            StatusPill.Visibility = args.NewSize.Width < 460 ? Visibility.Collapsed : Visibility.Visible;
            SubtitleText.Visibility = args.NewSize.Width < 360 ? Visibility.Collapsed : Visibility.Visible;
        };

        Terminal.TerminalStateChanged += OnTerminalStateChanged;
        Terminal.TerminalDataReceived += OnTerminalDataReceived;
        if (_workingDirectorySource is not null)
            _workingDirectorySource.WorkingDirectoryChanged += OnWorkingDirectoryChanged;
        TerminalSurface.InputReceived += (_, data) => _ = SendTerminalTextAsync(data);
        TerminalSurface.AppShortcutRequested += (_, request) =>
            AppShortcutRequested?.Invoke(this, request);
        TerminalSurface.TerminalSizeChanged += TerminalSurface_TerminalSizeChanged;
        TerminalSurface.OutputCopyCompleted += (_, copied) =>
            ToolTipService.SetToolTip(CopyOutputButton, copied
                ? Loc.T("마지막 출력을 복사했습니다.", "Last output copied.")
                : Loc.T("클립보드에 복사하지 못했습니다. 다시 눌러 주세요.", "Could not copy to the clipboard. Try again."));
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

    /// <summary>The shell's current filesystem location, or empty until it is known.</summary>
    public string WorkingDirectory => _workingDirectorySource?.WorkingDirectory ?? string.Empty;

    /// <summary>Raised on the UI thread after the shell reports a different location.</summary>
    public event EventHandler<string>? WorkingDirectoryChanged;

    public LocalShellKind ShellKind { get; }
    public string DisplayTitle => _launchPlan?.LaunchTitle ??
        (ShellKind == LocalShellKind.CommandPrompt ? "CMD" : "PowerShell");

    /// <summary>
    /// Direct command tabs intentionally are not restored: their owner can use
    /// the bounded local history or a favorite to explicitly run them again.
    /// </summary>
    public bool CanRestoreWorkspace => _launchPlan is null;

    /// <summary>Return keyboard focus to the active local shell.</summary>
    public void FocusTerminal() => TerminalSurface.FocusTerminal();

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
        var copyLabel = Loc.T("마지막 출력 복사", "Copy last output");
        AutomationProperties.SetName(CopyOutputButton, copyLabel);
        ToolTipService.SetToolTip(CopyOutputButton, copyLabel);
        if (_launchPlan is null)
        {
            TitleText.Text = DisplayTitle;
            SubtitleText.Text = Loc.T(
                $"로컬 · {Environment.UserName}@{Environment.MachineName}",
                $"Local · {Environment.UserName}@{Environment.MachineName}");
            AutomationProperties.SetName(TerminalSurface, Loc.T(
                $"로컬 {DisplayTitle} 터미널",
                $"Local {DisplayTitle} terminal"));
        }
        else
        {
            TitleText.Text = _launchPlan.LaunchTitle;
            SubtitleText.Text = _launchPlan.Kind == LocalTerminalLaunchKind.OpenSsh
                ? Loc.T(
                    "로컬 SSH · .ssh/config와 SSH Agent 설정 사용",
                    "Local SSH · uses .ssh/config and SSH Agent settings")
                : Loc.T(
                    "로컬 명령 · 설치된 프로그램과 PATH 설정 사용",
                    "Local command · uses the installed program and PATH settings");
            AutomationProperties.SetName(TerminalSurface, Loc.T(
                "로컬 연결 명령 터미널",
                "Local connection command terminal"));
        }
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
        if (_workingDirectorySource is not null)
            _workingDirectorySource.WorkingDirectoryChanged -= OnWorkingDirectoryChanged;
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

    private void OnWorkingDirectoryChanged(object? sender, string path)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (Volatile.Read(ref _closed) != 0)
                return;
            // Read the current source value rather than an older queued event:
            // a close/reopen can replace a shell before the UI drains this queue.
            var current = WorkingDirectory;
            if (string.Equals(_lastNotifiedWorkingDirectory, current, StringComparison.Ordinal))
                return;
            _lastNotifiedWorkingDirectory = current;
            WorkingDirectoryChanged?.Invoke(this, current);
        });
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

    private void DrainTerminalOutput() => DrainTerminalOutput(allOutput: false);

    private void DrainTerminalOutput(bool allOutput)
    {
        List<byte[]> batch = [];
        long droppedBytes;
        bool resetScreen;
        lock (_terminalOutputGate)
        {
            var batchBytes = 0;
            while (_terminalOutputQueue.Count > 0 && (allOutput || batchBytes < MaxTerminalDrainBytes))
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

        // Tab selection can reload this view after a process exits or fails.
        // A saved command is one explicit launch, so reloading must preserve its
        // final output instead of silently starting another connection.
        if (_launchPlan is not null && Interlocked.Exchange(ref _directLaunchAttempted, 1) != 0)
            return;

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

    private void CopyOutput_Click(object sender, RoutedEventArgs e)
    {
        DrainTerminalOutput(allOutput: true);
        TerminalSurface.CopyLatestOutput();
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
