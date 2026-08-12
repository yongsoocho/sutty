using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using sutty.Core.Commands;
using sutty.Core.Terminal;
using sutty.Setting;
using sutty.UI.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI;

namespace sutty.UI.Views;

/// <summary>
/// A Windows-local PowerShell tab backed by ConPTY. The view shares Sutty's bounded
/// VT screen model with SSH terminals while keeping local processes out of SSH/SFTP flows.
/// </summary>
public sealed partial class LocalTerminalView : UserControl
{
    private const int MaxTerminalBacklogBytes = 4 * 1024 * 1024;
    private const int MaxTerminalDrainBytes = 256 * 1024;

    private readonly VtScreenBuffer _terminalBuffer = new();
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
        : this(new WindowsConPtyTerminal())
    {
    }

    public LocalTerminalView(IInteractiveTerminal terminal)
    {
        Terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        InitializeComponent();

        Terminal.TerminalStateChanged += OnTerminalStateChanged;
        Terminal.TerminalDataReceived += OnTerminalDataReceived;
        _terminalBuffer.ResponseRequested += response => _ = SendTerminalTextAsync(response);

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
        var size = Math.Clamp(settings.TerminalFontSize, 8, 32);

        TitleText.FontFamily = family;
        TerminalStatusText.FontFamily = family;
        TerminalText.FontFamily = family;
        TerminalText.FontSize = size;
        TerminalText.LineHeight = Math.Ceiling(size * 1.5);

        RequestTerminalResize();
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

        DispatcherQueue.TryEnqueue(() =>
        {
            if (Volatile.Read(ref _closed) != 0)
                return;

            if (state == TerminalState.Opening)
            {
                ClearTerminalBacklog();
                _terminalBuffer.Reset();
                _terminalBuffer.Resize(
                    checked((int)_requestedTerminalSize.Columns),
                    checked((int)_requestedTerminalSize.Rows));
                TerminalText.Text = _terminalBuffer.Render();
            }

            UpdateTerminalStatus(state);
            if (state == TerminalState.Open)
                TerminalSurface.Focus(FocusState.Programmatic);
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
            _terminalBuffer.Reset();
            var warning = Loc.T(
                $"[sutty: 터미널 출력이 4 MiB 대기 한도를 초과하여 {droppedBytes:N0}바이트를 버리고 화면을 재설정했습니다.]\r\n",
                $"[sutty: terminal output exceeded the 4 MiB backlog; dropped {droppedBytes:N0} bytes and reset the screen.]\r\n");
            _terminalBuffer.Feed(Encoding.UTF8.GetBytes(warning));
        }

        foreach (var data in batch)
            _terminalBuffer.Feed(data);

        TerminalText.Text = _terminalBuffer.Render();
        if (!_terminalBuffer.IsAlternateScreen)
        {
            TerminalSurface.UpdateLayout();
            TerminalSurface.ChangeView(null, TerminalSurface.ScrollableHeight, null, true);
        }

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

        _requestedTerminalSize = CalculateTerminalSize();
        ClearTerminalBacklog();
        _terminalBuffer.Reset();
        _terminalBuffer.Resize(
            checked((int)_requestedTerminalSize.Columns),
            checked((int)_requestedTerminalSize.Rows));
        TerminalText.Text = _terminalBuffer.Render();

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
    public async Task<CommandExecutionResult> RunExternalCommandDetailedAsync(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        await _broadcastCommandGate.WaitAsync(_lifetimeCancellation.Token);
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
                await SendTerminalTextAsync(wireInput);
                var output = await capture.Completion
                    .WaitAsync(TimeSpan.FromMinutes(10), _lifetimeCancellation.Token);
                return new CommandExecutionResult(
                    command,
                    output,
                    "",
                    null,
                    null,
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

    private void CaptureBroadcastOutput(byte[] data)
    {
        TerminalBroadcastCapture? capture;
        lock (_broadcastCaptureGate)
            capture = _broadcastCapture;
        capture?.Feed(data);
    }

    private async void TerminalSurface_PreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (Terminal.TerminalState != TerminalState.Open)
            return;

        string? sequence = null;
        if (IsKeyDown(Windows.System.VirtualKey.Control) &&
            args.Key is >= Windows.System.VirtualKey.A and <= Windows.System.VirtualKey.Z)
        {
            sequence = ((char)((int)args.Key - (int)Windows.System.VirtualKey.A + 1)).ToString();
        }
        else
        {
            sequence = args.Key switch
            {
                Windows.System.VirtualKey.Enter => "\r",
                Windows.System.VirtualKey.Back => "\x7f",
                Windows.System.VirtualKey.Tab => "\t",
                Windows.System.VirtualKey.Escape => "\x1b",
                Windows.System.VirtualKey.Up => CursorKeySequence('A'),
                Windows.System.VirtualKey.Down => CursorKeySequence('B'),
                Windows.System.VirtualKey.Right => CursorKeySequence('C'),
                Windows.System.VirtualKey.Left => CursorKeySequence('D'),
                Windows.System.VirtualKey.Home => CursorKeySequence('H'),
                Windows.System.VirtualKey.End => CursorKeySequence('F'),
                Windows.System.VirtualKey.Insert => "\x1b[2~",
                Windows.System.VirtualKey.Delete => "\x1b[3~",
                Windows.System.VirtualKey.PageUp => "\x1b[5~",
                Windows.System.VirtualKey.PageDown => "\x1b[6~",
                Windows.System.VirtualKey.F1 => "\x1bOP",
                Windows.System.VirtualKey.F2 => "\x1bOQ",
                Windows.System.VirtualKey.F3 => "\x1bOR",
                Windows.System.VirtualKey.F4 => "\x1bOS",
                Windows.System.VirtualKey.F5 => "\x1b[15~",
                Windows.System.VirtualKey.F6 => "\x1b[17~",
                Windows.System.VirtualKey.F7 => "\x1b[18~",
                Windows.System.VirtualKey.F8 => "\x1b[19~",
                Windows.System.VirtualKey.F9 => "\x1b[20~",
                Windows.System.VirtualKey.F10 => "\x1b[21~",
                Windows.System.VirtualKey.F11 => "\x1b[23~",
                Windows.System.VirtualKey.F12 => "\x1b[24~",
                _ => null,
            };
        }

        if (sequence is null)
            return;

        args.Handled = true;
        await SendTerminalTextAsync(sequence);
    }

    private async void LocalTerminalView_PreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        var controlDown = IsKeyDown(Windows.System.VirtualKey.Control);
        var shiftDown = IsKeyDown(Windows.System.VirtualKey.Shift);
        if (args.Key != Windows.System.VirtualKey.Insert || (!controlDown && !shiftDown))
            return;

        args.Handled = true;
        if (controlDown)
        {
            ClipboardHelper.CopyText(TerminalText.SelectedText);
            return;
        }

        var clipboardText = await ClipboardHelper.GetTextAsync();
        if (!string.IsNullOrEmpty(clipboardText))
            await SendTerminalTextAsync(ClipboardHelper.NormalizeTerminalPaste(clipboardText));
    }

    private async void TerminalSurface_CharacterReceived(
        UIElement sender,
        CharacterReceivedRoutedEventArgs args)
    {
        if (Terminal.TerminalState != TerminalState.Open ||
            IsKeyDown(Windows.System.VirtualKey.Control) ||
            IsKeyDown(Windows.System.VirtualKey.Menu) ||
            args.Character is < ' ' or '\x7f')
        {
            return;
        }

        args.Handled = true;
        await SendTerminalTextAsync(args.Character.ToString());
    }

    private void TerminalSurface_PointerPressed(object sender, PointerRoutedEventArgs args)
        => TerminalSurface.Focus(FocusState.Pointer);

    private void TerminalSurface_Loaded(object sender, RoutedEventArgs args)
    {
        if (Volatile.Read(ref _closed) != 0)
            return;

        _requestedTerminalSize = CalculateTerminalSize();
        if (Terminal.TerminalState != TerminalState.Open)
        {
            _terminalBuffer.Resize(
                checked((int)_requestedTerminalSize.Columns),
                checked((int)_requestedTerminalSize.Rows));
            TerminalText.Text = _terminalBuffer.Render();
        }

        TerminalSurface.Focus(FocusState.Programmatic);
        _ = EnsureTerminalStartedAsync();
    }

    private void TerminalSurface_SizeChanged(object sender, SizeChangedEventArgs args)
        => RequestTerminalResize();

    private void RequestTerminalResize()
    {
        _requestedTerminalSize = CalculateTerminalSize();

        if (Terminal.TerminalState != TerminalState.Open)
        {
            _terminalBuffer.Resize(
                checked((int)_requestedTerminalSize.Columns),
                checked((int)_requestedTerminalSize.Rows));
            TerminalText.Text = _terminalBuffer.Render();
            UpdateTerminalStatus(Terminal.TerminalState);
            return;
        }

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

                _terminalBuffer.Resize(
                    checked((int)pending.Columns),
                    checked((int)pending.Rows));
                TerminalText.Text = _terminalBuffer.Render();
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

    private TerminalSize CalculateTerminalSize()
    {
        var fontSize = Math.Clamp(SettingsService.Current.TerminalFontSize, 8, 32);
        if (TerminalSurface.ActualWidth < 100 || TerminalSurface.ActualHeight < 80)
            return new TerminalSize(120, 40);

        var width = Math.Max(320, TerminalSurface.ActualWidth - 24);
        var height = Math.Max(120, TerminalSurface.ActualHeight - 20);
        var columns = (uint)Math.Clamp((int)Math.Floor(width / (fontSize * 0.62)), 20, 300);
        var rows = (uint)Math.Clamp((int)Math.Floor(height / (fontSize * 1.5)), 5, 120);
        return new TerminalSize(columns, rows, (uint)width, (uint)height);
    }

    private void UpdateTerminalStatus(TerminalState state)
    {
        var (pillLabel, statusLabel, resourceKey) = state switch
        {
            TerminalState.Opening =>
                ("STARTING", Loc.T("CONPTY · 시작 중", "CONPTY · starting"), "StatusAmber"),
            TerminalState.Open =>
                ("RUNNING", $"CONPTY {_terminalBuffer.Columns}\u00D7{_terminalBuffer.Rows}", "StatusGreen"),
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

    private static bool IsKeyDown(Windows.System.VirtualKey key)
        => Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private string CursorKeySequence(char final)
        => _terminalBuffer.ApplicationCursorKeys
            ? $"\x1bO{final}"
            : $"\x1b[{final}";

}
