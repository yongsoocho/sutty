using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using sutty.Core.Models;
using sutty.Setting;
using sutty.UI.Helpers;
using System;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace sutty.UI.Views
{
    /// <summary>새 연결 폼 (Deep Field 리디자인: 플랫 필드 + 2×2 인증 선택).</summary>
    public sealed partial class HomePanel : UserControl
    {
        /// <summary>Connect 버튼이 눌리면 수집된 연결 정보와 함께 발생한다.</summary>
        public event EventHandler<SshConnectionInfo>? ConnectRequested;

        /// <summary>키 파일 선택 다이얼로그를 띄우려면 호스트 윈도우 핸들이 필요하다.</summary>
        public IntPtr OwnerWindowHandle { get; set; }

        private SshAuthMethod _authMethod = SshAuthMethod.Password;

        public HomePanel()
        {
            this.InitializeComponent();

            // 설정에 저장된 기본값 반영
            var settings = SettingsService.Current;
            PortBox.Value = settings.DefaultSshPort;
            KeepAliveBox.Value = settings.DefaultKeepAliveSeconds;

            UpdateAuthUi();
        }

        // ── 인증 방식 2×2 버튼 ──

        private void AuthButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string tag && int.TryParse(tag, out var index))
            {
                _authMethod = (SshAuthMethod)index;
                UpdateAuthUi();
            }
        }

        private void UpdateAuthUi()
        {
            StyleAuthButton(AuthPasswordBtn, _authMethod == SshAuthMethod.Password);
            StyleAuthButton(AuthKeyBtn, _authMethod == SshAuthMethod.PublicKey);
            StyleAuthButton(AuthAgentBtn, _authMethod == SshAuthMethod.Agent);
            StyleAuthButton(AuthKbiBtn, _authMethod == SshAuthMethod.KeyboardInteractive);

            PasswordPanel.Visibility = _authMethod == SshAuthMethod.Password ? Visibility.Visible : Visibility.Collapsed;
            KeyPanel.Visibility = _authMethod == SshAuthMethod.PublicKey ? Visibility.Visible : Visibility.Collapsed;
            AgentPanel.Visibility = _authMethod == SshAuthMethod.Agent ? Visibility.Visible : Visibility.Collapsed;
            KbiPanel.Visibility = _authMethod == SshAuthMethod.KeyboardInteractive ? Visibility.Visible : Visibility.Collapsed;
        }

        private void StyleAuthButton(Button button, bool selected)
        {
            if (selected)
            {
                // 선택: 액센트 테두리 + 파랑→틸 그라디언트 틴트 (디자인 원본)
                button.BorderBrush = ThemeResources.Brush(this, "AccentBlue");
                button.Background = ThemeResources.Brush(this, "AccentTint");
                button.Foreground = ThemeResources.Brush(this, "TextPrimary");
            }
            else
            {
                button.BorderBrush = ThemeResources.Brush(this, "CardBorder");
                button.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                button.Foreground = ThemeResources.Brush(this, "TextMuted");
            }
        }

        // ── 키 파일 선택 ──

        private async void BrowseKey_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.Desktop
            };
            picker.FileTypeFilter.Add(".pem");  // AWS 등에서 받는 PEM 키
            picker.FileTypeFilter.Add(".key");
            picker.FileTypeFilter.Add(".ppk");  // 선택은 되지만 연결 시 변환 안내가 뜸
            picker.FileTypeFilter.Add("*");     // id_rsa, id_ed25519 등 확장자 없는 키

            // WinUI3 데스크톱에서는 피커에 윈도우 핸들을 연결해야 한다
            if (OwnerWindowHandle != IntPtr.Zero)
                InitializeWithWindow.Initialize(picker, OwnerWindowHandle);

            var file = await picker.PickSingleFileAsync();
            if (file is not null)
                KeyPathBox.Text = file.Path;
        }

        // ── Connect ──

        private void Connect_Click(object sender, RoutedEventArgs e)
        {
            var host = HostBox.Text.Trim();
            if (string.IsNullOrEmpty(host))
            {
                HostBox.Focus(FocusState.Programmatic);
                return;
            }

            // Public key 인증인데 키 파일이 비어 있으면 먼저 채우게 한다
            if (_authMethod == SshAuthMethod.PublicKey && string.IsNullOrWhiteSpace(KeyPathBox.Text))
            {
                KeyPathBox.Focus(FocusState.Programmatic);
                return;
            }

            var info = new SshConnectionInfo
            {
                Host = host,
                Port = double.IsNaN(PortBox.Value) ? 22 : (int)PortBox.Value,
                DisplayName = DisplayNameBox.Text.Trim(),
                Username = UsernameBox.Text.Trim(),
                AuthMethod = _authMethod,
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
