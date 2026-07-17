using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using sutty.Core.Sessions;
using sutty.Setting;
using sutty.UI.ViewModels;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace sutty.UI.Views
{
    /// <summary>
    /// 탭 하나에 대응하는 세션 화면.
    /// Jupyter 노트북처럼 [명령 입력 블록 + 출력 블록]이 셀 단위로 쌓인다.
    /// - 아래 입력 줄에서 Enter → 새 셀 실행
    /// - 헤더의 Paste → 클립보드 내용을 명령으로 바로 실행
    /// </summary>
    public sealed partial class SessionView : UserControl
    {
        public ISshSession Session { get; }
        public ObservableCollection<CommandCell> Cells { get; } = [];

        private readonly string _prompt;
        private int _cellIndex; // Jupyter의 In [n] 번호
        private string _cwd = "~"; // 현재 원격 작업 디렉터리 (cd로 갱신)

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
            PasteButton.IsEnabled = connected;

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
            ScrollToBottom();
        }

        /// <summary>Command 패널(playbook) 등 외부에서 이 세션에 명령을 실행할 때 사용.</summary>
        public Task RunExternalCommandAsync(string command) => RunCommandAsync(command);

        // 프롬프트에 항상 현재 경로를 보여준다: user@host:~/path $
        private void UpdatePrompt()
            => InputPrompt.Text = $"{_prompt}:{_cwd} $";

        private async Task RunCommandAsync(string command)
        {
            if (Session.State != SessionState.Connected)
            {
                AddSystemCell("Not connected.");
                return;
            }

            var cell = new CommandCell
            {
                Command = command,
                Prompt = _prompt,
                Index = ++_cellIndex,
                IsRunning = true,
            };
            Cells.Add(cell);
            ScrollToBottom();

            try
            {
                // exec 채널은 명령마다 새로 열려 상태가 안 남으므로
                // cwd를 우리가 기억했다가 매 명령 앞에 cd를 붙여 준다
                string output;
                var trimmed = command.Trim();

                if (trimmed == "cd" || trimmed.StartsWith("cd "))
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
            ScrollToBottom();
        }

        private async void CommandBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            await RunFromInputAsync();
        }

        private async void Run_Click(object sender, RoutedEventArgs e)
            => await RunFromInputAsync();

        private async Task RunFromInputAsync()
        {
            var command = CommandBox.Text.Trim();
            if (command.Length == 0) return;

            CommandBox.Text = "";
            await RunCommandAsync(command);
        }

        // 클립보드 내용을 명령으로 실행
        private async void Paste_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var content = Clipboard.GetContent();
                if (!content.Contains(StandardDataFormats.Text)) return;

                var text = (await content.GetTextAsync())?.Trim();
                if (string.IsNullOrEmpty(text)) return;

                await RunCommandAsync(text);
            }
            catch (Exception ex)
            {
                AddSystemCell($"Paste failed: {ex.Message}");
            }
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

        private void ScrollToBottom()
        {
            CellsScroll.UpdateLayout();
            CellsScroll.ChangeView(null, CellsScroll.ScrollableHeight, null, true);
        }
    }
}
