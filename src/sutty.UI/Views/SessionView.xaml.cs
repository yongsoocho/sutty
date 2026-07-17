using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using sutty.Core.Sessions;
using sutty.Setting;
using System;
using System.Threading.Tasks;

namespace sutty.UI.Views
{
    /// <summary>
    /// 탭 하나에 대응하는 세션 화면.
    /// 지금은 상태 표시 + 모의 터미널이고, 실제 터미널은 sutty.Core의
    /// SSH 채널이 준비되면 이 자리에 연결한다.
    /// </summary>
    public sealed partial class SessionView : UserControl
    {
        public ISshSession Session { get; }

        public SessionView(ISshSession session)
        {
            Session = session;
            InitializeComponent();

            var settings = SettingsService.Current;
            TermText.FontFamily = new FontFamily(settings.TerminalFontFamily + ", Consolas");
            TermText.FontSize = settings.TerminalFontSize;

            TitleText.Text = Session.Info.Title;

            // StateChanged는 백그라운드 스레드에서 올 수 있으므로 UI 스레드로 마샬링
            Session.StateChanged += OnStateChanged;
            ApplyState(Session.State, initial: true);
        }

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
                SessionState.Disconnected => ("disconnected", "TextFaint"),
                SessionState.Failed => ("failed", "StatusRed"),
                _ => ("ready", "TextFaint"),
            };
            StateText.Text = $"{info.Host}:{info.Port} · {label}";
            StatusDot.Fill = (Brush)Application.Current.Resources[brushKey];

            if (initial) return;

            var user = string.IsNullOrWhiteSpace(info.Username) ? "root" : info.Username;
            switch (state)
            {
                case SessionState.Connecting:
                    AppendLine($"Connecting to {info.Host}:{info.Port} as {user} ...");
                    break;

                case SessionState.Connected:
                    AppendLine("Connected.");
                    _ = ShowBannerAsync(user); // 서버 정보(uname)를 실제로 실행해서 출력
                    break;

                case SessionState.Disconnecting:
                    AppendLine("");
                    AppendLine("Disconnecting ...");
                    break;

                case SessionState.Disconnected:
                    AppendLine($"Connection to {info.Host} closed.");
                    break;

                case SessionState.Failed:
                    AppendLine($"Connection failed: {Session.LastError ?? "unknown error"}");
                    break;
            }
        }

        private async Task ShowBannerAsync(string user)
        {
            try
            {
                var banner = await Session.RunCommandAsync("uname -a");
                if (!string.IsNullOrWhiteSpace(banner))
                {
                    AppendLine("");
                    AppendLine(banner.TrimEnd());
                }
            }
            catch
            {
                // 배너는 장식일 뿐 — 실패해도 프롬프트는 띄운다
            }
            AppendLine("");
            AppendLine($"{user}@{Session.Info.Host}:~$ ");
        }

        private void AppendLine(string line)
        {
            TermText.Text += line + Environment.NewLine;

            // 항상 마지막 줄이 보이도록 스크롤
            TermScroll.UpdateLayout();
            TermScroll.ChangeView(null, TermScroll.ScrollableHeight, null, true);
        }
    }
}
