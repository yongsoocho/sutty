using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using sutty.Core.Sessions;
using sutty.Setting;
using sutty.UI.ViewModels;
using System;
using System.Collections.ObjectModel;
using System.Text;
using System.Threading.Tasks;

namespace sutty.UI.Views
{
    /// <summary>
    /// 탭 하나에 대응하는 세션 화면. 보기 방식이 두 가지다 (헤더 토글, 설정에 저장):
    /// - REPL: Jupyter처럼 [명령 입력 블록 + 출력 블록] 셀 단위, 블록마다 paste 버튼
    /// - RAW : PuTTY처럼 검정 화면에 흰 글씨 텍스트 로그
    /// 아래 입력 줄에서 Enter → 실행. 프롬프트에 현재 경로가 항상 표시된다.
    /// </summary>
    public sealed partial class SessionView : UserControl
    {
        public ISshSession Session { get; }
        public ObservableCollection<CommandCell> Cells { get; } = [];

        private readonly string _prompt;
        private readonly StringBuilder _rawLog = new(); // RAW 보기용 텍스트 로그
        private int _cellIndex; // Jupyter의 In [n] 번호
        private string _cwd = "~"; // 현재 원격 작업 디렉터리 (cd로 갱신)
        private bool _isRaw;

        public SessionView(ISshSession session)
        {
            Session = session;
            _prompt = BuildPrompt(session);
            InitializeComponent();

            var settings = SettingsService.Current;
            var mono = new FontFamily(settings.TerminalFontFamily + ", Consolas");
            CellsList.FontFamily = mono;
            CellsList.FontSize = settings.TerminalFontSize;
            CommandBox.FontFamily = mono;
            RawText.FontFamily = mono;
            RawText.FontSize = settings.TerminalFontSize;

            _isRaw = settings.TerminalMode == "Raw";
            ApplyViewMode();

            TitleText.Text = Session.Info.Title;
            UpdatePrompt();

            // StateChanged는 백그라운드 스레드에서 올 수 있으므로 UI 스레드로 마샬링
            Session.StateChanged += OnStateChanged;
            ApplyState(Session.State, initial: true);
        }

        private static string BuildPrompt(ISshSession session)
        {
            var user = string.IsNullOrWhiteSpace(session.Info.Username) ? "root" : session.Info.Username;
            return $"{user}@{session.Info.Host}";
        }

        // ── RAW ↔ REPL 보기 전환 ──

        private void ApplyViewMode()
        {
            ReplView.Visibility = _isRaw ? Visibility.Collapsed : Visibility.Visible;
            RawView.Visibility = _isRaw ? Visibility.Visible : Visibility.Collapsed;
            ModeText.Text = _isRaw ? "RAW" : "REPL";
        }

        private void ModeToggle_Click(object sender, RoutedEventArgs e)
        {
            _isRaw = !_isRaw;
            SettingsService.Current.TerminalMode = _isRaw ? "Raw" : "Repl";
            SettingsService.Save(); // 다음 실행/다음 세션에도 유지
            ApplyViewMode();
            ScrollToBottom();
        }

        // ── 세션 상태 ──

        private void OnStateChanged(object? sender, SessionState state)
            => DispatcherQueue.TryEnqueue(() => ApplyState(state));

        private void ApplyState(SessionState state, bool initial = false)
        {
            var info = Session.Info;

            (string label, string brushKey) = state switch
            {
                SessionState.Connecting => ("connecting…", "StatusAmber"),
                SessionState.Connected => ("connected", "StatusGreen"),
                SessionState.Disconnecting => ("disconnecting…", "StatusAmber"),
                SessionState.Disconnected => ("disconnected", "StatusIdle"),
                SessionState.Failed => ("failed", "StatusRed"),
                _ => ("ready", "StatusIdle"),
            };
            StateText.Text = $"{info.Host}:{info.Port} · {label}";
            StatusDot.Fill = (Brush)Application.Current.Resources[brushKey];

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

        // 프롬프트에 항상 현재 경로를 보여준다: user@host:~/path $
        private void UpdatePrompt()
            => InputPrompt.Text = $"{_prompt}:{_cwd} $";

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
            };
            Cells.Add(cell);
            AppendRaw($"{_prompt}:{_cwd} $ {command}");
            ScrollToBottom();

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
                cell.IsRunning = false;
            }

            if (cell.Output.Length > 0)
                AppendRaw(cell.Output);
            ScrollToBottom();

            return cell.Output;
        }

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

        // 박스(입력/출력)의 내용을 아래 명령 입력줄로 가져온다 (바로 실행하진 않음)
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
