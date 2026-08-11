using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using sutty.Core.Models;
using sutty.Setting;
using sutty.UI.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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

        public ObservableCollection<string> Tags { get; } = [];

        private SshAuthMethod _authMethod = SshAuthMethod.Password;
        private readonly List<string> _keyPathHistory = [];

        private const int MaxSavedKeyPaths = 12;
        private const int MaxRecentTags = 20;
        private const int MaxConnectionTags = 8;

        public HomePanel()
        {
            this.InitializeComponent();

            // 설정에 저장된 기본값 반영
            var settings = SettingsService.Current;
            ApplyConnectionDefaults();

            if (Enum.TryParse<SshAuthMethod>(settings.LastAuthMethod, true, out var savedMethod) &&
                savedMethod is SshAuthMethod.Password or SshAuthMethod.PublicKey)
            {
                _authMethod = savedMethod;
            }

            settings.RecentPrivateKeyPaths ??= [];
            _keyPathHistory.AddRange(settings.RecentPrivateKeyPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxSavedKeyPaths));
            KeyPathBox.ItemsSource = _keyPathHistory;
            if (_authMethod == SshAuthMethod.PublicKey && _keyPathHistory.Count > 0)
                KeyPathBox.Text = _keyPathHistory[0];

            UpdateAuthUi();
            ActualThemeChanged += (_, _) => UpdateAuthUi();
        }

        /// <summary>Settings에서 바뀐 기본 연결값을 현재 Home 폼에도 즉시 반영한다.</summary>
        public void ApplyConnectionDefaults(bool applyPort = true, bool applyKeepAlive = true)
        {
            var settings = SettingsService.Current;
            if (applyPort)
                PortBox.Value = settings.DefaultSshPort;
            if (applyKeepAlive)
                KeepAliveBox.Value = settings.DefaultKeepAliveSeconds;
        }

        public void RefreshLanguage() => Bindings.Update();

        /// <summary>
        /// History에서 가져온 비밀 없는 연결 초안으로 폼을 채운다.
        /// 비밀번호와 key passphrase는 의도적으로 항상 비운다.
        /// </summary>
        public void ApplyConnectionDraft(SshConnectionInfo draft)
        {
            HostBox.Text = draft.Host?.Trim() ?? "";
            PortBox.Value = draft.Port is >= 1 and <= 65535 ? draft.Port : 22;
            DisplayNameBox.Text = draft.DisplayName?.Trim() ?? "";
            UsernameBox.Text = draft.Username?.Trim() ?? "";

            _authMethod = draft.AuthMethod is SshAuthMethod.Password or SshAuthMethod.PublicKey
                ? draft.AuthMethod
                : SshAuthMethod.Password;

            PasswordBox.Password = "";
            PassphraseBox.Password = "";
            KeyPathBox.Text = _authMethod == SshAuthMethod.PublicKey
                ? draft.PrivateKeyPath?.Trim() ?? ""
                : "";

            if (_authMethod == SshAuthMethod.PublicKey &&
                !string.IsNullOrWhiteSpace(KeyPathBox.Text) &&
                !_keyPathHistory.Contains(KeyPathBox.Text, StringComparer.OrdinalIgnoreCase))
            {
                _keyPathHistory.Insert(0, KeyPathBox.Text);
                if (_keyPathHistory.Count > MaxSavedKeyPaths)
                    _keyPathHistory.RemoveRange(MaxSavedKeyPaths, _keyPathHistory.Count - MaxSavedKeyPaths);
                KeyPathBox.ItemsSource = _keyPathHistory.ToList();
            }

            Tags.Clear();
            foreach (var tag in (draft.Tags ?? [])
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxConnectionTags))
            {
                Tags.Add(tag);
            }

            AddTagButton.IsEnabled = Tags.Count < MaxConnectionTags;
            UpdateAuthUi();

            DispatcherQueue.TryEnqueue(() =>
            {
                if (_authMethod == SshAuthMethod.Password)
                    PasswordBox.Focus(FocusState.Programmatic);
                else if (string.IsNullOrWhiteSpace(KeyPathBox.Text))
                    KeyPathBox.Focus(FocusState.Programmatic);
                else
                    PassphraseBox.Focus(FocusState.Programmatic);
            });
        }

        // ── 인증 방식 2×2 버튼 ──

        private void AuthButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string tag && int.TryParse(tag, out var index))
            {
                var requestedMethod = (SshAuthMethod)index;
                if (requestedMethod is not (SshAuthMethod.Password or SshAuthMethod.PublicKey))
                    return;

                _authMethod = requestedMethod;
                if (_authMethod == SshAuthMethod.PublicKey &&
                    string.IsNullOrWhiteSpace(KeyPathBox.Text) &&
                    _keyPathHistory.Count > 0)
                {
                    KeyPathBox.Text = _keyPathHistory[0];
                }
                SettingsService.Current.LastAuthMethod = _authMethod.ToString();
                PersistSettings();
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
                button.BorderBrush = ThemeResources.Brush(this, "InputBorder");
                button.Background = ThemeResources.Brush(this, "InputBg");
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
            {
                KeyPathBox.Text = file.Path;
                RememberKeyPath(file.Path);
            }
        }

        private void KeyPathBox_GotFocus(object sender, RoutedEventArgs e)
        {
            UpdateKeyPathSuggestions(KeyPathBox.Text);
            KeyPathBox.IsSuggestionListOpen = _keyPathHistory.Count > 0;
        }

        private void KeyPathBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                UpdateKeyPathSuggestions(sender.Text);
        }

        private void KeyPathBox_SuggestionChosen(
            AutoSuggestBox sender,
            AutoSuggestBoxSuggestionChosenEventArgs args)
        {
            if (args.SelectedItem is string path)
                sender.Text = path;
        }

        private void UpdateKeyPathSuggestions(string text)
        {
            var query = text.Trim();
            var matches = _keyPathHistory
                .Where(path => query.Length == 0 || path.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
            KeyPathBox.ItemsSource = matches;
            KeyPathBox.IsSuggestionListOpen = matches.Count > 0;
        }

        private void RememberKeyPath(string path)
        {
            path = path.Trim();
            if (path.Length == 0) return;

            _keyPathHistory.RemoveAll(saved =>
                string.Equals(saved, path, StringComparison.OrdinalIgnoreCase));
            _keyPathHistory.Insert(0, path);
            if (_keyPathHistory.Count > MaxSavedKeyPaths)
                _keyPathHistory.RemoveRange(MaxSavedKeyPaths, _keyPathHistory.Count - MaxSavedKeyPaths);

            var settings = SettingsService.Current;
            settings.RecentPrivateKeyPaths = [.. _keyPathHistory];
            settings.LastAuthMethod = SshAuthMethod.PublicKey.ToString();
            PersistSettings();
            KeyPathBox.ItemsSource = _keyPathHistory.ToList();
        }

        // ── 연결 태그 ──

        private async void AddTag_Click(object sender, RoutedEventArgs e)
        {
            if (Tags.Count >= MaxConnectionTags) return;

            var settings = SettingsService.Current;
            settings.RecentConnectionTags ??= [];
            var recent = settings.RecentConnectionTags
                .Where(tag => !string.IsNullOrWhiteSpace(tag) && !Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxRecentTags)
                .ToList();

            var input = new AutoSuggestBox
            {
                PlaceholderText = Loc.T("태그 이름", "Tag name"),
                ItemsSource = recent,
                MinWidth = 260,
            };
            input.TextChanged += (_, args) =>
            {
                if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
                var query = input.Text.Trim();
                var matches = recent
                    .Where(tag => query.Length == 0 || tag.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                input.ItemsSource = matches;
                input.IsSuggestionListOpen = matches.Count > 0;
            };
            input.SuggestionChosen += (_, args) =>
            {
                if (args.SelectedItem is string tag)
                    input.Text = tag;
            };
            input.GotFocus += (_, _) => input.IsSuggestionListOpen = recent.Count > 0;

            var dialog = new ContentDialog
            {
                Title = Loc.T("연결 태그 추가", "Add connection tag"),
                Content = input,
                PrimaryButtonText = Loc.T("추가", "Add"),
                CloseButtonText = Loc.T("취소", "Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };
            dialog.Opened += (_, _) =>
            {
                input.Focus(FocusState.Programmatic);
                input.IsSuggestionListOpen = recent.Count > 0;
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            var value = input.Text.Trim();
            if (value.Length == 0 || value.Length > 32 ||
                Tags.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            Tags.Add(value);
            RememberTags();
            AddTagButton.IsEnabled = Tags.Count < MaxConnectionTags;
        }

        private void RemoveTag_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string tag) return;

            var existing = Tags.FirstOrDefault(value =>
                string.Equals(value, tag, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
                Tags.Remove(existing);
            AddTagButton.IsEnabled = true;
        }

        private void RememberTags()
        {
            var settings = SettingsService.Current;
            settings.RecentConnectionTags ??= [];
            foreach (var tag in Tags.Reverse())
            {
                settings.RecentConnectionTags.RemoveAll(saved =>
                    string.Equals(saved, tag, StringComparison.OrdinalIgnoreCase));
                settings.RecentConnectionTags.Insert(0, tag);
            }
            if (settings.RecentConnectionTags.Count > MaxRecentTags)
            {
                settings.RecentConnectionTags.RemoveRange(
                    MaxRecentTags,
                    settings.RecentConnectionTags.Count - MaxRecentTags);
            }
            PersistSettings();
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

            SettingsService.Current.LastAuthMethod = _authMethod.ToString();
            if (_authMethod == SshAuthMethod.PublicKey)
                RememberKeyPath(KeyPathBox.Text);
            else
                PersistSettings();
            if (Tags.Count > 0)
                RememberTags();

            var info = new SshConnectionInfo
            {
                Host = host,
                Port = double.IsNaN(PortBox.Value) ? 22 : (int)PortBox.Value,
                DisplayName = DisplayNameBox.Text.Trim(),
                Username = UsernameBox.Text.Trim(),
                AuthMethod = _authMethod,
                Password = PasswordBox.Password,
                PrivateKeyPath = _authMethod == SshAuthMethod.PublicKey
                    ? KeyPathBox.Text.Trim()
                    : "",
                Passphrase = PassphraseBox.Password,
                // These fields remain in the shared model for compatibility, but the
                // current connection engine does not implement them yet.
                JumpHost = "",
                KeepAliveSeconds = double.IsNaN(KeepAliveBox.Value) ? 0 : (int)KeepAliveBox.Value,
                Compression = false,
                X11Forwarding = false,
                Tags = [.. Tags],
            };

            ConnectRequested?.Invoke(this, info);
        }

        private void PersistSettings()
        {
            var result = SettingsService.Save();
            if (result.Succeeded)
            {
                SettingsSaveStatusText.Visibility = Visibility.Collapsed;
                return;
            }

            SettingsSaveStatusText.Text = result.Error is UnauthorizedAccessException or System.Security.SecurityException
                ? Loc.T("설정을 저장할 권한이 없습니다. 연결은 계속할 수 있습니다.",
                    "Settings cannot be saved due to file permissions. You can still connect.")
                : Loc.T("설정을 저장하지 못했습니다. 연결은 계속할 수 있습니다.",
                    "Settings could not be saved. You can still connect.");
            SettingsSaveStatusText.Visibility = Visibility.Visible;
            System.Diagnostics.Debug.WriteLine($"Settings save failed: {result.Error}");
        }
    }
}
