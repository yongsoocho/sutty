using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using sutty.Core.Models;
using sutty.Setting;
using System;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace sutty.UI.Views
{
    public sealed partial class HomePanel : UserControl
    {
        /// <summary>Connect 버튼이 눌리면 수집된 연결 정보와 함께 발생한다.</summary>
        public event EventHandler<SshConnectionInfo>? ConnectRequested;

        /// <summary>
        /// 키 파일 선택 다이얼로그를 띄우려면 호스트 윈도우 핸들이 필요하다.
        /// 이 컨트롤을 올리는 쪽에서 설정해 줄 것.
        /// 예) hostDetails.OwnerWindowHandle = WindowNative.GetWindowHandle(this);
        /// </summary>
        public IntPtr OwnerWindowHandle { get; set; }

        public HomePanel()
        {
            this.InitializeComponent();

            // 설정에 저장된 기본값 반영
            var settings = SettingsService.Current;
            PortBox.Value = settings.DefaultSshPort;
            KeepAliveBox.Value = settings.DefaultKeepAliveSeconds;
        }

        // 인증 방식에 따라 보여줄 입력 패널 토글
        private void AuthCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // XAML 로드 순서상 패널이 아직 null일 수 있으므로 방어
            if (PasswordPanel is null) return;

            var method = (SshAuthMethod)AuthCombo.SelectedIndex;

            PasswordPanel.Visibility = method == SshAuthMethod.Password ? Visibility.Visible : Visibility.Collapsed;
            KeyPanel.Visibility = method == SshAuthMethod.PublicKey ? Visibility.Visible : Visibility.Collapsed;
            AgentPanel.Visibility = method == SshAuthMethod.Agent ? Visibility.Visible : Visibility.Collapsed;
            KbiPanel.Visibility = method == SshAuthMethod.KeyboardInteractive ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void BrowseKey_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.Desktop
            };
            picker.FileTypeFilter.Add("*"); // 키 파일은 확장자가 없는 경우가 많음

            // WinUI3 데스크톱에서는 피커에 윈도우 핸들을 연결해야 한다
            if (OwnerWindowHandle != IntPtr.Zero)
                InitializeWithWindow.Initialize(picker, OwnerWindowHandle);

            var file = await picker.PickSingleFileAsync();
            if (file is not null)
                KeyPathBox.Text = file.Path;
        }

        private void Connect_Click(object sender, RoutedEventArgs e)
        {
            var host = HostBox.Text.Trim();
            if (string.IsNullOrEmpty(host))
            {
                HostBox.Focus(FocusState.Programmatic);
                return;
            }

            var info = new SshConnectionInfo
            {
                Host = host,
                Port = double.IsNaN(PortBox.Value) ? 22 : (int)PortBox.Value,
                DisplayName = DisplayNameBox.Text.Trim(),
                Username = UsernameBox.Text.Trim(),
                AuthMethod = (SshAuthMethod)AuthCombo.SelectedIndex,
                Password = PasswordBox.Password,
                PrivateKeyPath = KeyPathBox.Text.Trim(),
                Passphrase = PassphraseBox.Password,
                JumpHost = JumpHostBox.Text.Trim(),
                KeepAliveSeconds = double.IsNaN(KeepAliveBox.Value) ? 0 : (int)KeepAliveBox.Value,
                Compression = CompressionCheck.IsChecked == true,
                X11Forwarding = X11Check.IsChecked == true
            };

            ConnectRequested?.Invoke(this, info);
        }
    }
}
