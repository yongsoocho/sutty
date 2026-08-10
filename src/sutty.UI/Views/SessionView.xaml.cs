using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using sutty.Core.Sessions;
using sutty.Core.Sftp;
using sutty.Setting;
using sutty.UI.Helpers;
using sutty.UI.ViewModels;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Windows.UI;

namespace sutty.UI.Views
{
    /// <summary>
    /// 탭 하나에 대응하는 세션 화면 (Deep Field 리디자인).
    /// - REPL: ❯ N 명령 + 타임스탬프·소요시간, 들여쓴 출력 (플랫)
    /// - RAW : 명령 실행 결과를 이어 붙이는 연속 로그 (PTY 터미널 아님)
    /// - 헤더의 CONNECTED 필은 업타임을 실시간 카운트
    /// </summary>
    public sealed partial class SessionView : UserControl
    {
        public ISshSession Session { get; }
        public ObservableCollection<CommandCell> Cells { get; } = [];

        private readonly string _prompt;
        private readonly StringBuilder _rawLog = new(); // RAW 보기용 텍스트 로그
        private int _cellIndex;
        private string _cwd = "~"; // 현재 원격 작업 디렉터리 (cd로 갱신)
        private bool _isRaw;

        private DateTime _connectedAt;
        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _uptimeTimer;

        public SessionView(ISshSession session)
        {
            Session = session;
            var user = string.IsNullOrWhiteSpace(session.Info.Username) ? "root" : session.Info.Username;
            _prompt = $"{user}@{session.Info.Host}";
            InitializeComponent();

            ApplyTerminalSettings();
            ActualThemeChanged += (_, _) =>
            {
                ApplyViewMode();
                UpdateStatusPill(Session.State);
                UpdateSftpPill(Session.SftpState);
            };

            TitleText.Text = $"{user}@{Session.Info.Title} · {Session.Info.Host}:{Session.Info.Port}";
            UpdatePrompt();

            // 업타임 카운터 (연결 중일 때 1초마다 갱신)
            _uptimeTimer = DispatcherQueue.CreateTimer();
            _uptimeTimer.Interval = TimeSpan.FromSeconds(1);
            _uptimeTimer.IsRepeating = true;
            _uptimeTimer.Tick += (_, _) => UpdateStatusPill(Session.State);

            // StateChanged는 백그라운드 스레드에서 올 수 있으므로 UI 스레드로 마샬링
            Session.StateChanged += OnStateChanged;
            Session.SftpStateChanged += OnSftpStateChanged;
            ApplyState(Session.State, initial: true);
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
            RawText.FontFamily = family;
            RawText.FontSize = size;
            RawText.LineHeight = Math.Ceiling(size * 1.5);
            InputPrompt.FontFamily = family;
            InputPrompt.FontSize = size;

            var modeChanged = _isRaw != (settings.TerminalMode == "Raw");
            _isRaw = settings.TerminalMode == "Raw";
            ApplyViewMode();
            if (modeChanged)
                ScrollToBottom();
        }

        /// <summary>Refresh one-time localized bindings without recreating the session.</summary>
        public void RefreshLanguage()
        {
            Bindings.Update();
            UpdateSftpPill(Session.SftpState);
        }

        // ── RAW ↔ REPL 세그먼트 토글 ──

        private void ReplBtn_Click(object sender, RoutedEventArgs e) => SetViewMode(isRaw: false);
        private void RawBtn_Click(object sender, RoutedEventArgs e) => SetViewMode(isRaw: true);

        private void SetViewMode(bool isRaw)
        {
            if (_isRaw == isRaw) return;
            _isRaw = isRaw;
            SettingsService.Current.TerminalMode = _isRaw ? "Raw" : "Repl";
            SettingsService.Save(); // 다음 실행/다음 세션에도 유지
            ApplyViewMode();
            ScrollToBottom();
        }

        private void ApplyViewMode()
        {
            ReplView.Visibility = _isRaw ? Visibility.Collapsed : Visibility.Visible;
            RawView.Visibility = _isRaw ? Visibility.Visible : Visibility.Collapsed;

            var active = ThemeResources.Brush(this, "AccentTint");
            var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            ReplBtn.Background = _isRaw ? transparent : active;
            RawBtn.Background = _isRaw ? active : transparent;
            ReplBtn.Foreground = ThemeResources.Brush(this, _isRaw ? "TextFaint" : "TextPrimary");
            RawBtn.Foreground = ThemeResources.Brush(this, _isRaw ? "TextPrimary" : "TextFaint");
        }

        // ── 세션 상태 / 업타임 필 ──

        private void OnStateChanged(object? sender, SessionState state)
            => DispatcherQueue.TryEnqueue(() => ApplyState(state));

        private void OnSftpStateChanged(object? sender, SftpConnectionState state)
            => DispatcherQueue.TryEnqueue(() => UpdateSftpPill(state));

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

            if (initial) return;

            switch (state)
            {
                case SessionState.Connecting:
                    AddSystemCell($"Connecting to {info.Host}:{info.Port} as {_prompt} ...");
                    break;

                case SessionState.Connected:
                    AddSystemCell("Connected.");
                    _ = ShowBannerAsync(); // 서버 정보(uname)를 실제로 실행해서 출력
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
        }

        // ── 셀 실행 ──

        private void AddSystemCell(string message)
        {
            Cells.Add(new CommandCell { Output = message });
            AppendRaw(message);
            ScrollToBottom();
        }

        /// <summary>
        /// Command/Multi 패널 등 외부에서 이 세션에 명령을 실행할 때 사용.
        /// 실행 결과(출력)를 돌려주므로 Multi 그리드가 셀에 미리보기를 띄울 수 있다.
        /// </summary>
        public Task<string> RunExternalCommandAsync(string command) => RunCommandAsync(command);

        // 프롬프트에 항상 현재 경로를 보여준다: /var/www ❯
        private void UpdatePrompt()
            => InputPrompt.Text = $"{_cwd} ❯";

        private async Task<string> RunCommandAsync(string command)
        {
            if (Session.State != SessionState.Connected)
            {
                AddSystemCell("Not connected.");
                return "Not connected.";
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
            AppendRaw($"{_prompt}:{_cwd} $ {command}");
            ScrollToBottom();

            var watch = Stopwatch.StartNew();
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
                    output = (await Session.RunCommandAsync(
                        $"cd {_cwd} 2>/dev/null; cd {target} && pwd"))?.Trim() ?? "";

                    var newCwd = output.Split('\n')[^1].Trim();
                    if (newCwd.StartsWith('/'))
                    {
                        _cwd = newCwd;
                        UpdatePrompt();
                        output = newCwd;
                    }
                }
                else
                {
                    output = await Session.RunCommandAsync($"cd {_cwd} 2>/dev/null; {command}") ?? "";
                }

                cell.Output = output.TrimEnd();
            }
            catch (Exception ex)
            {
                cell.Output = $"error: {ex.Message}";
            }
            finally
            {
                watch.Stop();
                cell.IsRunning = false;
                cell.TimeText = $"{cell.StartedAt:HH:mm:ss} · {FormatDuration(watch.ElapsedMilliseconds)}";
            }

            if (cell.Output.Length > 0)
                AppendRaw(cell.Output);
            ScrollToBottom();

            return cell.Output;
        }

        private static string FormatDuration(long ms) =>
            ms < 1000 ? $"{ms}ms" : $"{ms / 1000.0:0.#}s";

        // WinUI TextBox는 줄구분자로 '\r'을 쓴다 — 검사/전송 전에 '\n'으로 통일
        private static string NormalizeNewlines(string text)
            => text.Replace("\r\n", "\n").Replace('\r', '\n');

        private async void CommandBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
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

        // ── 스크롤/로그 ──

        private void AppendRaw(string line)
        {
            _rawLog.AppendLine(line);
            RawText.Text = _rawLog.ToString();
        }

        private void ScrollToBottom()
        {
            if (_isRaw)
            {
                RawScroll.UpdateLayout();
                RawScroll.ChangeView(null, RawScroll.ScrollableHeight, null, true);
            }
            else
            {
                CellsScroll.UpdateLayout();
                CellsScroll.ChangeView(null, CellsScroll.ScrollableHeight, null, true);
            }
        }
    }
}
