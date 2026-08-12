using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using sutty.Core.Plugins;
using sutty.Core.Commands;
using sutty.Core.Sessions;
using sutty.Core.Sftp;
using sutty.Core.Terminal;
using sutty.Setting;
using sutty.UI.Helpers;
using sutty.UI.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI;

namespace sutty.UI.Views
{
    /// <summary>
    /// 탭 하나에 대응하는 세션 화면 (Deep Field 리디자인).
    /// - REPL: ❯ N 명령 + 타임스탬프·소요시간, 들여쓴 출력 (플랫)
    /// - TERMINAL: persistent ShellStream PTY + bounded VT screen buffer
    /// - 헤더의 CONNECTED 필은 업타임을 실시간 카운트
    /// </summary>
    public sealed partial class SessionView : UserControl
    {
        public ISshSession Session { get; }
        public ObservableCollection<CommandCell> Cells { get; } = [];
        public string WorkingDirectory => _cwd;

        /// <summary>
        /// Raised when Sutty explicitly changes the REPL working directory. Commands typed
        /// directly inside the PTY are intentionally not guessed or parsed.
        /// </summary>
        public event EventHandler<string>? WorkingDirectoryChanged;

        /// <summary>
        /// True when a persistent PTY is already running. Callers must not inject shell
        /// commands into an existing terminal without an explicit user confirmation,
        /// because the foreground program may be vim, top, a database console, or TUI.
        /// </summary>
        public bool HasOpenInteractiveTerminal => Session.TerminalState == TerminalState.Open;

        private readonly string _prompt;
        private readonly VtScreenBuffer _terminalBuffer = new();
        private const int MaxTerminalBacklogBytes = 4 * 1024 * 1024;
        private const int MaxTerminalDrainBytes = 256 * 1024;
        private readonly object _terminalOutputGate = new();
        private readonly Queue<byte[]> _terminalOutputQueue = new();
        private readonly SemaphoreSlim _commandGate = new(1, 1);
        private readonly CommandSuggestionEngine _suggestionEngine = new();
        private readonly List<string> _commandHistory = [];
        private IReadOnlyList<string> _savedCommandSuggestions = [];
        private string? _activeSuggestion;
        private int _terminalQueuedBytes;
        private long _terminalDroppedBytes;
        private bool _terminalBacklogResetPending;
        private int _cellIndex;
        private string _cwd = "~"; // 현재 원격 작업 디렉터리 (cd로 갱신)
        private bool _isTerminal;
        private int _terminalDrainQueued;
        private bool _terminalResizeInProgress;
        private long _workingDirectoryRequestVersion;
        private TerminalSize _requestedTerminalSize = new(120, 40, 0, 0);

        private DateTime _connectedAt;
        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _uptimeTimer;

        public SessionView(ISshSession session)
        {
            Session = session;
            var user = string.IsNullOrWhiteSpace(session.Info.Username) ? "root" : session.Info.Username;
            _prompt = $"{user}@{session.Info.Host}";
            InitializeComponent();

            ReloadSavedCommandSuggestions();

            ApplyTerminalSettings();
            ActualThemeChanged += (_, _) =>
            {
                ApplyViewMode();
                UpdateStatusPill(Session.State);
                UpdateSftpPill(Session.SftpState);
            };

            TitleText.Text = $"{user}@{Session.Info.Title} · {Session.Info.Host}:{Session.Info.Port}";
            RoutePillText.Text = Session.Info.Route?.DisplayName ??
                Session.AuditContext.RouteType.ToString().ToUpperInvariant();
            ToolTipService.SetToolTip(
                RoutePillText,
                $"route={Session.AuditContext.RouteId} · correlation={Session.AuditContext.CorrelationId}");
            UpdatePrompt();

            // 업타임 카운터 (연결 중일 때 1초마다 갱신)
            _uptimeTimer = DispatcherQueue.CreateTimer();
            _uptimeTimer.Interval = TimeSpan.FromSeconds(1);
            _uptimeTimer.IsRepeating = true;
            _uptimeTimer.Tick += (_, _) => UpdateStatusPill(Session.State);

            // StateChanged는 백그라운드 스레드에서 올 수 있으므로 UI 스레드로 마샬링
            Session.StateChanged += OnStateChanged;
            Session.SftpStateChanged += OnSftpStateChanged;
            Session.TerminalStateChanged += OnTerminalStateChanged;
            Session.TerminalDataReceived += OnTerminalDataReceived;
            _terminalBuffer.ResponseRequested += response => _ = SendTerminalTextAsync(response);
            ApplyState(Session.State, initial: true);
            UpdateTerminalStatus(Session.TerminalState);
        }

        /// <summary>Apply terminal-related settings to this already-open session.</summary>
        public void ApplyTerminalSettings()
        {
            var settings = SettingsService.Current;
            var familyName = string.IsNullOrWhiteSpace(settings.TerminalFontFamily)
                ? "Cascadia Mono"
                : settings.TerminalFontFamily.Trim();
            var family = new FontFamily($"{familyName}, Consolas");
            var size = Math.Clamp(settings.TerminalFontSize, 8, 32);

            CellsList.FontFamily = family;
            CellsList.FontSize = size;
            CommandBox.FontFamily = family;
            CommandBox.FontSize = size;
            TerminalText.FontFamily = family;
            TerminalText.FontSize = size;
            TerminalText.LineHeight = Math.Ceiling(size * 1.5);
            InputPrompt.FontFamily = family;
            InputPrompt.FontSize = size;
            Controls.TerminalHighlight.Refresh(CellsList);
            UpdateCommandSuggestion();

            var modeChanged = _isTerminal != (settings.TerminalMode == "Terminal");
            _isTerminal = settings.TerminalMode == "Terminal";
            ApplyViewMode();
            if (modeChanged)
                ScrollToBottom();
        }

        /// <summary>Refresh one-time localized bindings without recreating the session.</summary>
        public void RefreshLanguage()
        {
            Bindings.Update();
            UpdateSftpPill(Session.SftpState);
            UpdateTerminalStatus(Session.TerminalState);
        }

        // ── TERMINAL ↔ REPL 세그먼트 토글 ──

        private void ReplBtn_Click(object sender, RoutedEventArgs e) => SetViewMode(isTerminal: false);
        private void TerminalBtn_Click(object sender, RoutedEventArgs e) => SetViewMode(isTerminal: true);

        private void SetViewMode(bool isTerminal)
        {
            if (_isTerminal == isTerminal) return;
            _isTerminal = isTerminal;
            SettingsService.Current.TerminalMode = _isTerminal ? "Terminal" : "Repl";
            SettingsService.Save(); // 다음 실행/다음 세션에도 유지
            ApplyViewMode();
            ScrollToBottom();
        }

        private void ApplyViewMode()
        {
            ReplView.Visibility = _isTerminal ? Visibility.Collapsed : Visibility.Visible;
            TerminalView.Visibility = _isTerminal ? Visibility.Visible : Visibility.Collapsed;
            InputBar.Visibility = _isTerminal ? Visibility.Collapsed : Visibility.Visible;

            var active = ThemeResources.Brush(this, "AccentTint");
            var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            ReplBtn.Background = _isTerminal ? transparent : active;
            TerminalBtn.Background = _isTerminal ? active : transparent;
            ReplBtn.Foreground = ThemeResources.Brush(this, _isTerminal ? "TextFaint" : "TextPrimary");
            TerminalBtn.Foreground = ThemeResources.Brush(this, _isTerminal ? "TextPrimary" : "TextFaint");

            if (_isTerminal && Session.State == SessionState.Connected && TerminalSurface.IsLoaded)
            {
                _ = EnsureTerminalStartedAsync();
                TerminalSurface.Focus(FocusState.Programmatic);
            }
        }

        // ── 세션 상태 / 업타임 필 ──

        private void OnStateChanged(object? sender, SessionState state)
            => DispatcherQueue.TryEnqueue(() => ApplyState(state));

        private void OnSftpStateChanged(object? sender, SftpConnectionState state)
            => DispatcherQueue.TryEnqueue(() => UpdateSftpPill(state));

        private void OnTerminalStateChanged(object? sender, TerminalState state)
            => DispatcherQueue.TryEnqueue(() =>
            {
                if (state == TerminalState.Opening)
                {
                    // Retire output queued by the previous PTY generation before the new
                    // stream can publish its startup prompt.
                    ClearTerminalBacklog();
                    _terminalBuffer.Reset();
                    TerminalText.Text = _terminalBuffer.Render();
                }
                UpdateTerminalStatus(state);
                if (state == TerminalState.Open && _isTerminal)
                    TerminalSurface.Focus(FocusState.Programmatic);
            });

        private void OnTerminalDataReceived(object? sender, TerminalDataReceivedEventArgs e)
        {
            var data = e.Data.ToArray();
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
                        // A partial VT stream can leave UTF-8/escape parser state invalid.
                        // Drop the whole pending generation and request an explicit screen
                        // reset instead of silently trimming arbitrary leading bytes.
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
            if (Interlocked.Exchange(ref _terminalDrainQueued, 1) != 0)
                return;

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
                    $"[sutty: 터미널 출력이 너무 빨라 {droppedBytes:N0}바이트를 버리고 화면을 재설정했습니다.]\r\n",
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

        private void UpdateTerminalStatus(TerminalState state)
        {
            var (label, resourceKey) = state switch
            {
                TerminalState.Opening => (Loc.T("터미널 여는 중", "Opening terminal"), "StatusAmber"),
                TerminalState.Open when Session.SupportsTerminalResize =>
                    ($"PTY {_terminalBuffer.Columns}×{_terminalBuffer.Rows}", "StatusGreen"),
                TerminalState.Open =>
                    ($"PTY {_terminalBuffer.Columns}×{_terminalBuffer.Rows} · {Loc.T("고정 크기", "fixed size")}", "StatusGreen"),
                TerminalState.Failed =>
                    (Loc.T("터미널 오류", "Terminal error"), "StatusRed"),
                _ => (Loc.T("터미널 닫힘", "Terminal closed"), "TextMuted"),
            };

            TerminalStatusText.Text = label;
            TerminalStatusText.Foreground = ThemeResources.Brush(this, resourceKey);
            ToolTipService.SetToolTip(
                TerminalStatus,
                state == TerminalState.Failed
                    ? Session.LastTerminalError ?? Loc.T("알 수 없는 오류", "Unknown error")
                    : !Session.SupportsTerminalResize && state == TerminalState.Open
                        ? Loc.T(
                            "현재 SSH 엔진에서는 실행 중 PTY 크기 변경을 사용할 수 없습니다.",
                            "Runtime PTY resize is unavailable in the active SSH engine.")
                        : null);
        }

        private static string StatusResourceKey(SessionState state) => state switch
        {
            SessionState.Connected => "StatusGreen",
            SessionState.Connecting or SessionState.Disconnecting => "StatusAmber",
            SessionState.Failed => "StatusRed",
            _ => "StatusIdle",
        };

        private void UpdateStatusPill(SessionState state)
        {
            var label = state switch
            {
                SessionState.Connecting => "CONNECTING",
                SessionState.Connected => $"CONNECTED {DateTime.Now - _connectedAt:hh\\:mm\\:ss}",
                SessionState.Disconnecting => "DISCONNECTING",
                SessionState.Disconnected => "DISCONNECTED",
                SessionState.Failed => "FAILED",
                _ => "READY",
            };
            var foreground = ThemeResources.Brush(this, StatusResourceKey(state));
            var color = foreground is SolidColorBrush solid
                ? solid.Color
                : Color.FromArgb(255, 0x6E, 0x7C, 0x8B);

            StatusPillText.Text = label;
            StatusPillText.Foreground = foreground;
            StatusPill.Background = new SolidColorBrush(Color.FromArgb(36, color.R, color.G, color.B));
        }

        private void UpdateSftpPill(SftpConnectionState state)
        {
            if (Session.State != SessionState.Connected || state == SftpConnectionState.NotConnected)
            {
                SftpPill.Visibility = Visibility.Collapsed;
                return;
            }

            SftpPill.Visibility = Visibility.Visible;
            var (label, resourceKey) = state switch
            {
                SftpConnectionState.Ready => (Loc.T("SFTP 준비됨", "SFTP ready"), "AccentTeal"),
                SftpConnectionState.Unavailable => (Loc.T("SFTP 사용 불가", "SFTP unavailable"), "StatusRed"),
                _ => (Loc.T("SFTP 연결 중", "SFTP connecting"), "StatusAmber"),
            };
            SftpPillText.Text = label;
            SftpPillText.Foreground = ThemeResources.Brush(this, resourceKey);
        }

        private void ApplyState(SessionState state, bool initial = false)
        {
            var info = Session.Info;

            if (state == SessionState.Connected)
            {
                if (!_uptimeTimer.IsRunning)
                {
                    _connectedAt = DateTime.Now;
                    _uptimeTimer.Start();
                }
            }
            else
            {
                _uptimeTimer.Stop();
            }

            UpdateStatusPill(state);
            UpdateSftpPill(Session.SftpState);

            var connected = state == SessionState.Connected;
            CommandBox.IsEnabled = connected;
            RunButton.IsEnabled = connected;
            TerminalSurface.IsEnabled = connected;

            if (initial) return;

            switch (state)
            {
                case SessionState.Connecting:
                    AddSystemCell($"Connecting to {info.Host}:{info.Port} as {_prompt} ...");
                    break;

                case SessionState.Connected:
                    AddSystemCell("Connected.");
                    _ = ShowBannerAsync(); // 서버 정보(uname)를 실제로 실행해서 출력
                    if (_isTerminal && TerminalSurface.IsLoaded)
                        _ = EnsureTerminalStartedAsync();
                    break;

                case SessionState.Disconnecting:
                    AddSystemCell("Disconnecting ...");
                    break;

                case SessionState.Disconnected:
                    AddSystemCell($"Connection to {info.Host} closed.");
                    break;

                case SessionState.Failed:
                    AddSystemCell($"Connection failed: {Session.LastError ?? "unknown error"}");
                    break;
            }
        }

        private async Task ShowBannerAsync()
        {
            try
            {
                var banner = await Session.RunCommandAsync("uname -a");
                if (!string.IsNullOrWhiteSpace(banner))
                    AddSystemCell(banner.TrimEnd());
            }
            catch
            {
                // 배너는 장식일 뿐 — 실패해도 무시
            }

            try
            {
                // Resolve the symbolic initial cwd once so Files consumers never receive
                // an invalid "/~" path. Failure deliberately leaves the visible "~".
                var initialVersion = Volatile.Read(ref _workingDirectoryRequestVersion);
                if (_cwd == "~")
                    await ResolveWorkingDirectoryAsync("~", initialVersion);
            }
            catch
            {
                // Home discovery is optional; connection and terminal remain usable.
            }
        }

        // ── 셀 실행 ──

        private void AddSystemCell(string message)
        {
            Cells.Add(new CommandCell { Output = message });
            ScrollToBottom();
        }

        /// <summary>
        /// Command/Multi 패널 등 외부에서 이 세션에 명령을 실행할 때 사용.
        /// 실행 결과(출력)를 돌려주므로 Multi 그리드가 셀에 미리보기를 띄울 수 있다.
        /// </summary>
        public async Task<string> RunExternalCommandAsync(string command)
            => (await RunCommandCoreAsync(command)).CombinedOutput;

        /// <summary>Structured result used by Multi so exit failures cannot look successful.</summary>
        public Task<CommandExecutionResult> RunExternalCommandDetailedAsync(string command)
            => RunCommandCoreAsync(command);

        // 프롬프트에 항상 현재 경로를 보여준다: /var/www ❯
        private void UpdatePrompt()
            => InputPrompt.Text = $"{_cwd} ❯";

        /// <summary>
        /// Validate and change Sutty's explicit REPL working directory. This does not infer
        /// cwd from arbitrary PTY output and does not silently inject input into the PTY.
        /// </summary>
        public async Task<bool> ChangeWorkingDirectoryAsync(string remotePath)
            => await ResolveWorkingDirectoryAsync(remotePath) is not null;

        /// <summary>
        /// Explicit Files → Terminal integration point: validate the directory through an
        /// exec channel, switch to TERMINAL, then send one safely quoted cd command to PTY.
        /// </summary>
        public Task<bool> OpenDirectoryInTerminalAsync(string remotePath)
            => OpenDirectoryInTerminalAsync(remotePath, allowInputToExistingTerminal: false);

        /// <summary>
        /// Opens a new PTY at the requested directory. Sending <c>cd</c> to a PTY that
        /// was already open is fail-closed unless the caller has obtained explicit user
        /// confirmation and passes <paramref name="allowInputToExistingTerminal"/>.
        /// </summary>
        public async Task<bool> OpenDirectoryInTerminalAsync(
            string remotePath,
            bool allowInputToExistingTerminal)
        {
            await _commandGate.WaitAsync();
            try
            {
                var resolved = await ResolveWorkingDirectoryCoreAsync(remotePath);
                if (resolved is null)
                    return false;

                if (Session.TerminalState == TerminalState.Open && !allowInputToExistingTerminal)
                    return false;

                if (Session.TerminalState != TerminalState.Open)
                    await EnsureTerminalStartedAsync();
                if (Session.TerminalState != TerminalState.Open)
                    return false;

                SetViewMode(isTerminal: true);
                await SendTerminalTextAsync($"cd {QuotePosix(resolved)}\r");
                return true;
            }
            finally
            {
                _commandGate.Release();
            }
        }

        private async Task<string?> ResolveWorkingDirectoryAsync(
            string remotePath,
            long? expectedRequestVersion = null)
        {
            await _commandGate.WaitAsync();
            try
            {
                return await ResolveWorkingDirectoryCoreAsync(remotePath, expectedRequestVersion);
            }
            finally
            {
                _commandGate.Release();
            }
        }

        private async Task<string?> ResolveWorkingDirectoryCoreAsync(
            string remotePath,
            long? expectedRequestVersion = null,
            Action<CommandExecutionResult>? resultCaptured = null)
        {
            if (Session.State != SessionState.Connected || string.IsNullOrWhiteSpace(remotePath))
                return null;

            var requestVersion = expectedRequestVersion ??
                Interlocked.Increment(ref _workingDirectoryRequestVersion);
            if (expectedRequestVersion.HasValue &&
                Volatile.Read(ref _workingDirectoryRequestVersion) != requestVersion)
                return null;

            var target = remotePath.Trim();
            var result = await Session.ExecuteCommandAsync(
                $"cd {ShellDirectoryArgument(_cwd)} 2>/dev/null || exit $?; " +
                $"cd {ShellDirectoryArgument(target)} 2>/dev/null && pwd");
            resultCaptured?.Invoke(result);
            var output = result.StandardOutput.Trim();
            var resolved = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault()?.Trim();

            if (resolved is null || !resolved.StartsWith('/'))
                return null;
            if (Volatile.Read(ref _workingDirectoryRequestVersion) != requestVersion)
                return null;

            _cwd = resolved;
            UpdatePrompt();
            WorkingDirectoryChanged?.Invoke(this, _cwd);
            return resolved;
        }

        private static string ShellDirectoryArgument(string value)
            => value is "~" or "$HOME" ? value : QuotePosix(value);

        private static string QuotePosix(string value)
            => $"'{value.Replace("'", "'\"'\"'")}'";

        private async Task<string> RunCommandAsync(string command)
            => (await RunCommandCoreAsync(command)).CombinedOutput;

        private async Task<CommandExecutionResult> RunCommandCoreAsync(string command)
        {
            RememberCommand(command);
            await _commandGate.WaitAsync();
            try
            {
                return await RunCommandCoreLockedAsync(command);
            }
            finally
            {
                _commandGate.Release();
            }
        }

        private async Task<CommandExecutionResult> RunCommandCoreLockedAsync(string command)
        {
            if (Session.State != SessionState.Connected)
            {
                var message = Loc.T("연결되어 있지 않습니다.", "Not connected.");
                AddSystemCell(message);
                return new CommandExecutionResult(
                    command, "", message, null, null,
                    DateTimeOffset.UtcNow, TimeSpan.Zero);
            }

            var cell = new CommandCell
            {
                Command = command,
                Prompt = _prompt,
                Index = ++_cellIndex,
                IsRunning = true,
                StartedAt = DateTime.Now,
            };
            Cells.Add(cell);
            ScrollToBottom();

            var watch = Stopwatch.StartNew();
            CommandExecutionResult? result = null;
            try
            {
                // exec 채널은 명령마다 새로 열려 상태가 안 남으므로
                // cwd를 우리가 기억했다가 매 명령 앞에 cd를 붙여 준다
                string output;
                var trimmed = command.Trim();

                // cd 추적은 한 줄짜리 cd 명령일 때만 (멀티라인은 그대로 실행)
                if (!trimmed.Contains('\n') && (trimmed == "cd" || trimmed.StartsWith("cd ")))
                {
                    var target = trimmed == "cd" ? "~" : trimmed[3..].Trim();
                    output = await ResolveWorkingDirectoryCoreAsync(
                        target,
                        resultCaptured: captured => result = captured) ?? "";
                    if (result is null)
                    {
                        result = new CommandExecutionResult(
                            command, "", Loc.T("디렉터리를 변경할 수 없습니다.", "Could not change directory."),
                            null, null, DateTimeOffset.UtcNow, watch.Elapsed);
                    }
                    else if (string.IsNullOrWhiteSpace(output) && !result.Succeeded)
                    {
                        result = result with
                        {
                            StandardError = Loc.T(
                                "디렉터리를 변경할 수 없습니다.",
                                "Could not change directory."),
                        };
                    }
                }
                else
                {
                    result = await Session.ExecuteCommandAsync(
                        $"cd {ShellDirectoryArgument(_cwd)} 2>/dev/null || exit $?; {command}");
                    output = result.CombinedOutput;
                }

                result ??= new CommandExecutionResult(
                    command, output, "", 0, null, DateTimeOffset.UtcNow, watch.Elapsed);
            }
            catch (OperationCanceledException)
            {
                result = new CommandExecutionResult(
                    command, "", Loc.T("명령 실행이 취소되었습니다.", "Command cancelled."),
                    null, "CANCELLED", DateTimeOffset.UtcNow, watch.Elapsed);
            }
            catch (Exception ex)
            {
                result = new CommandExecutionResult(
                    command, "", $"error: {ex.Message}", null, null,
                    DateTimeOffset.UtcNow, watch.Elapsed);
            }
            finally
            {
                watch.Stop();
                result ??= new CommandExecutionResult(
                    command, "", Loc.T("명령 결과를 확인할 수 없습니다.", "Command result unavailable."),
                    null, null, DateTimeOffset.UtcNow, watch.Elapsed);
                cell.StandardOutput = result.StandardOutput;
                cell.StandardError = result.StandardError;
                cell.ExitCode = result.ExitCode;
                cell.Output = result.CombinedOutput.TrimEnd();
                cell.IsRunning = false;
                cell.TimeText = $"{cell.StartedAt:HH:mm:ss} · {FormatDuration(watch.ElapsedMilliseconds)}";
            }

            ScrollToBottom();

            return result!;
        }

        private static string FormatDuration(long ms) =>
            ms < 1000 ? $"{ms}ms" : $"{ms / 1000.0:0.#}s";

        // WinUI TextBox는 줄구분자로 '\r'을 쓴다 — 검사/전송 전에 '\n'으로 통일
        private static string NormalizeNewlines(string text)
            => text.Replace("\r\n", "\n").Replace('\r', '\n');

        private async void CommandBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (TryAcceptCommandSuggestion(e))
                return;

            if (e.Key != Windows.System.VirtualKey.Enter) return;

            // Shift+Enter → 줄바꿈 (기본 동작에 맡긴다)
            var shiftDown = Microsoft.UI.Input.InputKeyboardSource
                .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (shiftDown) return;

            // 커서가 있는 줄이 연속 문자로 끝나면(\ = bash, ` = PowerShell) Enter도 줄바꿈
            var caret = Math.Min(CommandBox.SelectionStart, CommandBox.Text.Length);
            var currentLine = NormalizeNewlines(CommandBox.Text[..caret]).Split('\n')[^1].TrimEnd();
            if (currentLine.EndsWith('\\') || currentLine.EndsWith('`')) return;

            // 그 외의 Enter → 실행 (Handled로 줄바꿈 삽입을 막는다)
            e.Handled = true;
            await RunFromInputAsync();
        }

        private async void Run_Click(object sender, RoutedEventArgs e)
            => await RunFromInputAsync();

        private async Task RunFromInputAsync()
        {
            var command = NormalizeNewlines(CommandBox.Text).Trim();
            if (command.Length == 0) return;

            CommandBox.Text = "";
            await RunCommandAsync(command);
        }

        private void CommandBox_TextChanged(object sender, TextChangedEventArgs e)
            => UpdateCommandSuggestion();

        private void CommandBox_GotFocus(object sender, RoutedEventArgs e)
        {
            ReloadSavedCommandSuggestions();
            UpdateCommandSuggestion();
        }

        private void ReloadSavedCommandSuggestions()
        {
            try
            {
                _savedCommandSuggestions = sutty.Command.CommandStore.GetAll()
                    .Select(command => command.CommandText)
                    .Where(command => !string.IsNullOrWhiteSpace(command))
                    .ToArray();
            }
            catch (Exception error)
            {
                Debug.WriteLine($"Command suggestion source unavailable: {error.GetType().Name}");
            }
        }

        private void RememberCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return;

            _commandHistory.Add(NormalizeNewlines(command).Trim());
            if (_commandHistory.Count > 500)
                _commandHistory.RemoveRange(0, _commandHistory.Count - 500);
        }

        private void UpdateCommandSuggestion()
        {
            _activeSuggestion = null;
            if (!SettingsService.Current.EnableCommandSuggestions ||
                string.IsNullOrWhiteSpace(CommandBox.Text))
            {
                SuggestionPanel.Visibility = Visibility.Collapsed;
                return;
            }

            var input = NormalizeNewlines(CommandBox.Text);
            var suggestion = _suggestionEngine.Suggest(new CommandSuggestionRequest(
                input,
                _commandHistory,
                _savedCommandSuggestions));
            if (suggestion is null)
            {
                SuggestionPanel.Visibility = Visibility.Collapsed;
                return;
            }

            _activeSuggestion = suggestion.Text;
            SuggestionText.Text = suggestion.Text;
            SuggestionHint.Text = SettingsService.Current.AcceptSuggestionWithTab
                ? Loc.T("→ / Tab으로 적용", "Accept with → / Tab")
                : Loc.T("→로 적용", "Accept with →");
            SuggestionPanel.Visibility = Visibility.Visible;
        }

        private bool TryAcceptCommandSuggestion(KeyRoutedEventArgs e)
        {
            if (_activeSuggestion is null ||
                CommandBox.SelectionLength != 0 ||
                CommandBox.SelectionStart != CommandBox.Text.Length ||
                IsKeyDown(Windows.System.VirtualKey.Shift) ||
                IsKeyDown(Windows.System.VirtualKey.Control) ||
                IsKeyDown(Windows.System.VirtualKey.Menu))
            {
                return false;
            }

            var accepts = e.Key == Windows.System.VirtualKey.Right ||
                (e.Key == Windows.System.VirtualKey.Tab &&
                 SettingsService.Current.AcceptSuggestionWithTab);
            if (!accepts)
                return false;

            e.Handled = true;
            CommandBox.Text = _activeSuggestion;
            CommandBox.SelectionStart = CommandBox.Text.Length;
            return true;
        }

        private async void SessionView_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            var controlDown = IsKeyDown(Windows.System.VirtualKey.Control);
            var shiftDown = IsKeyDown(Windows.System.VirtualKey.Shift);
            if (e.Key != Windows.System.VirtualKey.Insert || (!controlDown && !shiftDown))
                return;

            e.Handled = true;
            if (controlDown)
            {
                var focused = FocusManager.GetFocusedElement(XamlRoot);
                var selected = focused switch
                {
                    TextBox textBox => textBox.SelectedText,
                    TextBlock textBlock => textBlock.SelectedText,
                    _ when _isTerminal => TerminalText.SelectedText,
                    _ => string.Empty,
                };
                ClipboardHelper.CopyText(selected);
                return;
            }

            var clipboardText = await ClipboardHelper.GetTextAsync();
            if (string.IsNullOrEmpty(clipboardText))
                return;

            if (_isTerminal)
            {
                await SendTerminalTextAsync(ClipboardHelper.NormalizeTerminalPaste(clipboardText));
            }
            else
            {
                ClipboardHelper.InsertAtSelection(CommandBox, clipboardText);
                CommandBox.Focus(FocusState.Programmatic);
            }
        }

        // ── Interactive PTY input/output ──

        private async Task EnsureTerminalStartedAsync()
        {
            if (Session.State != SessionState.Connected ||
                Session.TerminalState is TerminalState.Open or TerminalState.Opening)
                return;

            _requestedTerminalSize = CalculateTerminalSize();
            ClearTerminalBacklog();
            _terminalBuffer.Reset();
            _terminalBuffer.Resize(
                checked((int)_requestedTerminalSize.Columns),
                checked((int)_requestedTerminalSize.Rows));
            TerminalText.Text = _terminalBuffer.Render();

            try
            {
                await Session.OpenTerminalAsync(_requestedTerminalSize);
            }
            catch (OperationCanceledException)
            {
                // Session shutdown owns cancellation; state events update the badge.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Terminal open failed: {ex}");
                UpdateTerminalStatus(Session.TerminalState);
            }
        }

        private async Task SendTerminalTextAsync(string text)
        {
            if (Session.TerminalState != TerminalState.Open || string.IsNullOrEmpty(text))
                return;

            try
            {
                await Session.SendTerminalInputAsync(Encoding.UTF8.GetBytes(text));
            }
            catch (OperationCanceledException)
            {
                // Disconnect cancellation is expected.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Terminal input failed: {ex}");
                UpdateTerminalStatus(Session.TerminalState);
            }
        }

        private async void TerminalSurface_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (Session.TerminalState != TerminalState.Open)
                return;

            string? sequence = null;
            if (IsKeyDown(Windows.System.VirtualKey.Control) &&
                e.Key is >= Windows.System.VirtualKey.A and <= Windows.System.VirtualKey.Z)
            {
                sequence = ((char)((int)e.Key - (int)Windows.System.VirtualKey.A + 1)).ToString();
            }
            else
            {
                sequence = e.Key switch
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

            e.Handled = true;
            await SendTerminalTextAsync(sequence);
        }

        private async void TerminalSurface_CharacterReceived(
            UIElement sender,
            CharacterReceivedRoutedEventArgs args)
        {
            if (Session.TerminalState != TerminalState.Open ||
                IsKeyDown(Windows.System.VirtualKey.Control) ||
                IsKeyDown(Windows.System.VirtualKey.Menu) ||
                args.Character is < ' ' or '\x7f')
                return;

            args.Handled = true;
            await SendTerminalTextAsync(args.Character.ToString());
        }

        private void TerminalSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
            => TerminalSurface.Focus(FocusState.Pointer);

        private void TerminalSurface_Loaded(object sender, RoutedEventArgs e)
        {
            _requestedTerminalSize = CalculateTerminalSize();
            if (Session.TerminalState != TerminalState.Open)
            {
                _terminalBuffer.Resize(
                    checked((int)_requestedTerminalSize.Columns),
                    checked((int)_requestedTerminalSize.Rows));
                TerminalText.Text = _terminalBuffer.Render();
            }

            if (_isTerminal && Session.State == SessionState.Connected)
                _ = EnsureTerminalStartedAsync();
        }

        private async void TerminalSurface_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var size = CalculateTerminalSize();
            _requestedTerminalSize = size;

            if (Session.TerminalState != TerminalState.Open)
            {
                _terminalBuffer.Resize(checked((int)size.Columns), checked((int)size.Rows));
                TerminalText.Text = _terminalBuffer.Render();
                return;
            }

            if (!Session.SupportsTerminalResize)
                return;

            // SizeChanged can fire in bursts while the splitter/window is dragged. Keep one
            // worker and coalesce pending events so the remote PTY finishes at the newest size.
            if (_terminalResizeInProgress)
                return;

            _terminalResizeInProgress = true;

            try
            {
                while (Session.TerminalState == TerminalState.Open)
                {
                    var pending = _requestedTerminalSize;
                    if (!await Session.ResizeTerminalAsync(pending))
                        break;

                    _terminalBuffer.Resize(
                        checked((int)pending.Columns),
                        checked((int)pending.Rows));
                    TerminalText.Text = _terminalBuffer.Render();
                    UpdateTerminalStatus(Session.TerminalState);

                    if (pending == _requestedTerminalSize)
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Terminal resize failed: {ex}");
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

        private static bool IsKeyDown(Windows.System.VirtualKey key)
            => Microsoft.UI.Input.InputKeyboardSource
                .GetKeyStateForCurrentThread(key)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        private string CursorKeySequence(char final)
            => _terminalBuffer.ApplicationCursorKeys
                ? $"\x1bO{final}"
                : $"\x1b[{final}";

        // 줄(명령/출력)의 내용을 아래 명령 입력줄로 가져온다 (바로 실행하진 않음)
        private void PasteCell_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not CommandCell cell)
                return;

            var text = (el.Tag as string == "output" ? cell.Output : cell.Command ?? "").Trim();
            if (text.Length == 0) return;

            CommandBox.Text = text;
            CommandBox.SelectionStart = text.Length; // 커서를 끝으로
            CommandBox.Focus(FocusState.Programmatic);
        }

        // ── 스크롤 ──

        private void ScrollToBottom()
        {
            if (!_isTerminal)
            {
                CellsScroll.UpdateLayout();
                CellsScroll.ChangeView(null, CellsScroll.ScrollableHeight, null, true);
            }
        }
    }
}
