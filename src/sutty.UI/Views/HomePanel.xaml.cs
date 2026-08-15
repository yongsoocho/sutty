using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using sutty.Core.Models;
using sutty.Core.Routing;
using sutty.Setting;
using sutty.UI.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace sutty.UI.Views;

/// <summary>Creates a real SSH/SFTP connection and optionally saves a reusable host profile.</summary>
public sealed partial class HomePanel : UserControl
{
    private const int MaxSavedKeyPaths = 12;
    private const int MaxRecentTags = 20;
    private const int MaxConnectionTags = 8;

    private readonly List<string> _keyPathHistory = [];
    private SshAuthMethod _authMethod = SshAuthMethod.Password;
    private string? _savedHostId;
    private string? _credentialId;

    public event EventHandler<SshConnectionInfo>? ConnectRequested;
    public IntPtr OwnerWindowHandle { get; set; }
    public ObservableCollection<string> Tags { get; } = [];

    public HomePanel()
    {
        InitializeComponent();

        var settings = SettingsService.Current;
        ApplyConnectionDefaults();
        if (Enum.TryParse<SshAuthMethod>(settings.LastAuthMethod, true, out var savedMethod) &&
            Enum.IsDefined(savedMethod))
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
        RefreshProxyCommandPreview();
        ActualThemeChanged += (_, _) => UpdateAuthUi();
    }

    public void ApplyConnectionDefaults(bool applyPort = true, bool applyKeepAlive = true)
    {
        var settings = SettingsService.Current;
        if (applyPort)
            PortBox.Text = settings.DefaultSshPort.ToString();
        if (applyKeepAlive)
            KeepAliveBox.Value = settings.DefaultKeepAliveSeconds;
    }

    public void RefreshLanguage()
    {
        Bindings.Update();
        RefreshProxyCommandPreview();
    }

    /// <summary>Loads a saved-host or history draft. Secrets are supplied only from the encrypted vault.</summary>
    public void ApplyConnectionDraft(SshConnectionInfo draft)
    {
        HostBox.Text = draft.Host?.Trim() ?? "";
        PortBox.Text = (draft.Port is >= 1 and <= 65535 ? draft.Port : 22).ToString();
        DisplayNameBox.Text = draft.DisplayName?.Trim() ?? "";
        UsernameBox.Text = draft.Username?.Trim() ?? "";

        _authMethod = Enum.IsDefined(draft.AuthMethod)
            ? draft.AuthMethod
            : SshAuthMethod.Password;
        PasswordBox.Password = draft.Password ?? "";
        PassphraseBox.Password = draft.Passphrase ?? "";
        KeyPathBox.Text = _authMethod == SshAuthMethod.PublicKey
            ? draft.PrivateKeyPath?.Trim() ?? ""
            : "";

        SelectRoute(draft.Route?.Type ?? ConnectionRouteType.Direct);
        ProxyHostBox.Text = draft.Route?.Host ?? "";
        ProxyPortBox.Text = draft.Route is { Port: > 0 } ? draft.Route.Port.ToString() : "";
        ProxyUsernameBox.Text = draft.Route?.Username ?? "";
        ProxyPasswordBox.Password = draft.Route?.Password ?? "";
        if (draft.Route is not null)
        {
            JumpAuthCombo.SelectedItem = JumpAuthCombo.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag as string,
                    draft.Route.AuthMethod.ToString(),
                    StringComparison.Ordinal))
                ?? JumpAuthCombo.Items[0];
            JumpKeyPathBox.Text = draft.Route.PrivateKeyPath ?? "";
            JumpPassphraseBox.Password = draft.Route.Passphrase ?? "";
            ProxyCommandBox.Text = draft.Route.Command ?? "";
        }
        RefreshProxyCommandPreview();
        EnterpriseRouteCheck.IsChecked = draft.RoutePolicy?.EnterpriseMode == true;

        var forwarding = draft.PortForwardings?.FirstOrDefault();
        SelectForwarding(forwarding?.Type);
        if (forwarding is not null)
        {
            ForwardBindHostBox.Text = forwarding.BindHost;
            ForwardBindPortBox.Text = forwarding.BindPort.ToString();
            ForwardDestinationHostBox.Text = forwarding.DestinationHost;
            ForwardDestinationPortBox.Text = forwarding.DestinationPort.ToString();
        }

        _savedHostId = string.IsNullOrWhiteSpace(draft.SavedHostId) ? null : draft.SavedHostId;
        _credentialId = string.IsNullOrWhiteSpace(draft.CredentialId) ? null : draft.CredentialId;
        SaveProfileCheck.IsChecked = draft.SaveProfile || _savedHostId is not null;
        RememberCredentialCheck.IsChecked = draft.RememberCredential && _credentialId is not null;
        GroupBox.Text = draft.GroupName?.Trim() ?? "";
        FavoriteCheck.IsChecked = draft.IsFavorite;
        SelectEnvironment(draft.Environment);
        UpdateProfileOptions();

        if (_authMethod == SshAuthMethod.PublicKey &&
            !string.IsNullOrWhiteSpace(KeyPathBox.Text) &&
            !_keyPathHistory.Contains(KeyPathBox.Text, StringComparer.OrdinalIgnoreCase))
        {
            _keyPathHistory.Insert(0, KeyPathBox.Text);
            TrimKeyHistory();
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

    private void PortBox_BeforeTextChanging(TextBox sender, TextBoxBeforeTextChangingEventArgs args)
        => args.Cancel = args.NewText.Any(character => !char.IsAsciiDigit(character));

    private void AuthButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string tag || !int.TryParse(tag, out var index))
            return;

        var requested = (SshAuthMethod)index;
        if (!Enum.IsDefined(requested))
            return;

        _authMethod = requested;
        if (_authMethod == SshAuthMethod.PublicKey &&
            string.IsNullOrWhiteSpace(KeyPathBox.Text) && _keyPathHistory.Count > 0)
        {
            KeyPathBox.Text = _keyPathHistory[0];
        }

        SettingsService.Current.LastAuthMethod = _authMethod.ToString();
        PersistSettings();
        UpdateAuthUi();
        UpdateProfileOptions();
    }

    private void RouteCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProxyPanel is null || ProxyPortBox is null)
            return;

        var type = SelectedRouteType();
        var usesHost = type is ConnectionRouteType.HttpConnect or ConnectionRouteType.Socks4 or
            ConnectionRouteType.Socks5 or ConnectionRouteType.SshJump;
        ProxyPanel.Visibility = usesHost ? Visibility.Visible : Visibility.Collapsed;
        ProxyCommandPanel.Visibility = type == ConnectionRouteType.ExternalProxyCommand
            ? Visibility.Visible
            : Visibility.Collapsed;
        JumpOptionsPanel.Visibility = type == ConnectionRouteType.SshJump
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (type != ConnectionRouteType.SshJump)
            ProxyPasswordBox.Visibility = Visibility.Visible;
        RouteHostLabel.Text = type == ConnectionRouteType.SshJump
            ? Loc.T("점프 호스트", "JUMP HOST")
            : Loc.T("프록시 주소", "PROXY HOST");

        if (usesHost && string.IsNullOrWhiteSpace(ProxyPortBox.Text))
            ProxyPortBox.Text = type switch
            {
                ConnectionRouteType.HttpConnect => "8080",
                ConnectionRouteType.SshJump => "22",
                _ => "1080",
            };

        RefreshProxyCommandPreview();
    }

    private void ProxyCommandInput_TextChanged(object sender, TextChangedEventArgs e)
        => RefreshProxyCommandPreview();

    private void RefreshProxyCommandPreview()
    {
        if (ProxyCommandPreviewText is null || ProxyCommandBox is null ||
            HostBox is null || PortBox is null || UsernameBox is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ProxyCommandBox.Text))
        {
            ProxyCommandPreviewText.Text = Loc.T(
                "실행 미리보기는 명령을 입력하면 표시됩니다.",
                "The execution preview appears after you enter a command.");
            ProxyCommandPreviewText.Foreground = ThemeResources.Brush(this, "TextMuted");
            return;
        }

        if (!int.TryParse(PortBox.Text, out var port))
        {
            ProxyCommandPreviewText.Text = Loc.T(
                "실행 미리보기 · 대상 포트를 확인하세요.",
                "Execution preview · check the target port.");
            ProxyCommandPreviewText.Foreground = ThemeResources.Brush(this, "StatusRed");
            return;
        }

        try
        {
            var expanded = ProxyCommandTemplate.Expand(
                ProxyCommandBox.Text,
                HostBox.Text,
                port,
                UsernameBox.Text);
            ProxyCommandPreviewText.Text = Loc.T(
                $"실행 미리보기 · {expanded}",
                $"Execution preview · {expanded}");
            ProxyCommandPreviewText.Foreground = ThemeResources.Brush(this, "TextMuted");
        }
        catch (RoutePolicyViolationException error)
        {
            ProxyCommandPreviewText.Text = Loc.T(
                $"실행할 수 없음 · {error.Message}",
                $"Cannot execute · {error.Message}");
            ProxyCommandPreviewText.Foreground = ThemeResources.Brush(this, "StatusRed");
        }
    }

    private ConnectionRouteType SelectedRouteType()
    {
        var value = (RouteCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        return Enum.TryParse<ConnectionRouteType>(value, out var type)
            ? type
            : ConnectionRouteType.Direct;
    }

    private void SelectRoute(ConnectionRouteType type)
    {
        RouteCombo.SelectedItem = RouteCombo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(
                item.Tag as string,
                type.ToString(),
                StringComparison.Ordinal))
            ?? RouteCombo.Items[0];
    }

    private void JumpAuthCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (JumpKeyPanel is null)
            return;
        JumpKeyPanel.Visibility = SelectedJumpAuthMethod() == SshAuthMethod.PublicKey
            ? Visibility.Visible
            : Visibility.Collapsed;
        ProxyPasswordBox.Visibility = SelectedJumpAuthMethod() == SshAuthMethod.Password
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private SshAuthMethod SelectedJumpAuthMethod()
    {
        var value = (JumpAuthCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        return Enum.TryParse<SshAuthMethod>(value, out var method) &&
               method is SshAuthMethod.Password or SshAuthMethod.PublicKey or SshAuthMethod.Agent
            ? method
            : SshAuthMethod.Password;
    }

    private void ForwardingTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ForwardingPanel is null || ForwardDestinationPanel is null)
            return;
        var type = SelectedForwardingType();
        ForwardingPanel.Visibility = type is null ? Visibility.Collapsed : Visibility.Visible;
        ForwardDestinationPanel.Visibility = type == SshPortForwardingType.Dynamic
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private SshPortForwardingType? SelectedForwardingType()
    {
        var value = (ForwardingTypeCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        return Enum.TryParse<SshPortForwardingType>(value, out var type) ? type : null;
    }

    private void SelectForwarding(SshPortForwardingType? type)
    {
        var tag = type?.ToString() ?? "None";
        ForwardingTypeCombo.SelectedItem = ForwardingTypeCombo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
            ?? ForwardingTypeCombo.Items[0];
    }

    private void UpdateAuthUi()
    {
        StyleAuthButton(AuthPasswordBtn, _authMethod == SshAuthMethod.Password);
        StyleAuthButton(AuthKeyBtn, _authMethod == SshAuthMethod.PublicKey);
        StyleAuthButton(AuthAgentBtn, _authMethod == SshAuthMethod.Agent);
        StyleAuthButton(AuthKeyboardBtn, _authMethod == SshAuthMethod.KeyboardInteractive);
        PasswordPanel.Visibility = _authMethod is SshAuthMethod.Password or SshAuthMethod.KeyboardInteractive
            ? Visibility.Visible
            : Visibility.Collapsed;
        KeyPanel.Visibility = _authMethod == SshAuthMethod.PublicKey
            ? Visibility.Visible
            : Visibility.Collapsed;
        AgentPanel.Visibility = _authMethod == SshAuthMethod.Agent
            ? Visibility.Visible
            : Visibility.Collapsed;
        KeyboardInteractiveHint.Visibility = _authMethod == SshAuthMethod.KeyboardInteractive
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void StyleAuthButton(Button button, bool selected)
    {
        button.BorderBrush = ThemeResources.Brush(this, selected ? "AccentBlue" : "InputBorder");
        button.Background = ThemeResources.Brush(this, selected ? "AccentTint" : "InputBg");
        button.Foreground = ThemeResources.Brush(this, selected ? "TextPrimary" : "TextMuted");
    }

    private void SaveProfileCheck_Changed(object sender, RoutedEventArgs e) => UpdateProfileOptions();

    private void UpdateProfileOptions()
    {
        var saveProfile = SaveProfileCheck.IsChecked == true;
        ProfileOptionsPanel.Visibility = saveProfile ? Visibility.Visible : Visibility.Collapsed;
        RememberCredentialCheck.IsEnabled = saveProfile && _authMethod != SshAuthMethod.Agent;
        if (!RememberCredentialCheck.IsEnabled)
            RememberCredentialCheck.IsChecked = false;
    }

    private async void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Desktop };
        picker.FileTypeFilter.Add(".pem");
        picker.FileTypeFilter.Add(".key");
        picker.FileTypeFilter.Add(".ppk");
        picker.FileTypeFilter.Add("*");
        if (OwnerWindowHandle != IntPtr.Zero)
            InitializeWithWindow.Initialize(picker, OwnerWindowHandle);

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        KeyPathBox.Text = file.Path;
        RememberKeyPath(file.Path);
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

        _keyPathHistory.RemoveAll(saved => string.Equals(saved, path, StringComparison.OrdinalIgnoreCase));
        _keyPathHistory.Insert(0, path);
        TrimKeyHistory();

        SettingsService.Current.RecentPrivateKeyPaths = [.. _keyPathHistory];
        SettingsService.Current.LastAuthMethod = SshAuthMethod.PublicKey.ToString();
        PersistSettings();
        KeyPathBox.ItemsSource = _keyPathHistory.ToList();
    }

    private void TrimKeyHistory()
    {
        if (_keyPathHistory.Count > MaxSavedKeyPaths)
            _keyPathHistory.RemoveRange(MaxSavedKeyPaths, _keyPathHistory.Count - MaxSavedKeyPaths);
    }

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
            input.ItemsSource = recent
                .Where(tag => query.Length == 0 || tag.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
        };

        var dialog = new ContentDialog
        {
            Title = Loc.T("연결 태그 추가", "Add connection tag"),
            Content = input,
            PrimaryButtonText = Loc.T("추가", "Add"),
            CloseButtonText = Loc.T("취소", "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var value = input.Text.Trim();
        if (value.Length is < 1 or > 32 || Tags.Contains(value, StringComparer.OrdinalIgnoreCase))
            return;

        Tags.Add(value);
        RememberTags();
        AddTagButton.IsEnabled = Tags.Count < MaxConnectionTags;
    }

    private void RemoveTag_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string tag) return;
        var existing = Tags.FirstOrDefault(value => string.Equals(value, tag, StringComparison.OrdinalIgnoreCase));
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

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim();
        if (host.Length == 0)
        {
            HostBox.Focus(FocusState.Programmatic);
            return;
        }

        if (string.IsNullOrWhiteSpace(UsernameBox.Text))
        {
            UsernameBox.Focus(FocusState.Programmatic);
            return;
        }

        if (!int.TryParse(PortBox.Text, out var port) || port is < 1 or > 65535)
        {
            PortBox.Focus(FocusState.Programmatic);
            return;
        }

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

        var saveProfile = SaveProfileCheck.IsChecked == true;
        var rememberCredential = saveProfile && RememberCredentialCheck.IsChecked == true;
        var selectedEnvironment = (EnvironmentCombo.SelectedItem as ComboBoxItem)?.Tag as string
            ?? "Unclassified";

        var routeType = SelectedRouteType();
        var enterpriseMode = EnterpriseRouteCheck.IsChecked == true;
        if (enterpriseMode && routeType == ConnectionRouteType.Direct)
        {
            SettingsSaveStatusText.Text = Loc.T(
                "기업 모드에서는 프록시 경로를 선택해야 합니다.",
                "Enterprise mode requires a proxy route.");
            SettingsSaveStatusText.Visibility = Visibility.Visible;
            RouteCombo.Focus(FocusState.Programmatic);
            return;
        }

        var proxyPort = 0;
        var routeUsesHost = routeType is ConnectionRouteType.HttpConnect or
            ConnectionRouteType.Socks4 or ConnectionRouteType.Socks5 or
            ConnectionRouteType.SshJump;
        if (routeUsesHost &&
            (string.IsNullOrWhiteSpace(ProxyHostBox.Text) ||
             !int.TryParse(ProxyPortBox.Text, out proxyPort) ||
             proxyPort is < 1 or > 65_535))
        {
            SettingsSaveStatusText.Text = Loc.T(
                "프록시 주소와 포트를 확인하세요.",
                "Check the proxy host and port.");
            SettingsSaveStatusText.Visibility = Visibility.Visible;
            ProxyHostBox.Focus(FocusState.Programmatic);
            return;
        }

        var jumpAuthMethod = SelectedJumpAuthMethod();
        if (routeType == ConnectionRouteType.SshJump &&
            string.IsNullOrWhiteSpace(ProxyUsernameBox.Text))
        {
            SettingsSaveStatusText.Text = Loc.T(
                "점프 호스트 사용자를 입력하세요.",
                "Enter the jump-host username.");
            SettingsSaveStatusText.Visibility = Visibility.Visible;
            ProxyUsernameBox.Focus(FocusState.Programmatic);
            return;
        }
        if (routeType == ConnectionRouteType.SshJump &&
            jumpAuthMethod == SshAuthMethod.PublicKey &&
            string.IsNullOrWhiteSpace(JumpKeyPathBox.Text))
        {
            SettingsSaveStatusText.Text = Loc.T(
                "점프 호스트 개인 키 경로를 입력하세요.",
                "Enter the jump-host private-key path.");
            SettingsSaveStatusText.Visibility = Visibility.Visible;
            JumpKeyPathBox.Focus(FocusState.Programmatic);
            return;
        }
        if (routeType == ConnectionRouteType.ExternalProxyCommand &&
            string.IsNullOrWhiteSpace(ProxyCommandBox.Text))
        {
            SettingsSaveStatusText.Text = Loc.T(
                "ProxyCommand를 입력하세요.",
                "Enter a ProxyCommand.");
            SettingsSaveStatusText.Visibility = Visibility.Visible;
            ProxyCommandBox.Focus(FocusState.Programmatic);
            return;
        }

        if (routeType == ConnectionRouteType.ExternalProxyCommand)
        {
            try
            {
                _ = ProxyCommandTemplate.Expand(
                    ProxyCommandBox.Text,
                    host,
                    port,
                    UsernameBox.Text);
            }
            catch (RoutePolicyViolationException error)
            {
                SettingsSaveStatusText.Text = Loc.T(
                    $"ProxyCommand 안전성 검사 실패: {error.Message}",
                    $"ProxyCommand safety check failed: {error.Message}");
                SettingsSaveStatusText.Visibility = Visibility.Visible;
                ProxyCommandBox.Focus(FocusState.Programmatic);
                return;
            }
        }

        List<SshPortForwardingRule> forwardings = [];
        if (SelectedForwardingType() is { } forwardingType)
        {
            if (!int.TryParse(ForwardBindPortBox.Text, out var bindPort) ||
                bindPort is < 1 or > 65_535 ||
                string.IsNullOrWhiteSpace(ForwardBindHostBox.Text))
            {
                SettingsSaveStatusText.Text = Loc.T(
                    "포워딩 바인드 주소와 포트를 확인하세요.",
                    "Check the forwarding bind host and port.");
                SettingsSaveStatusText.Visibility = Visibility.Visible;
                ForwardBindPortBox.Focus(FocusState.Programmatic);
                return;
            }

            var destinationPort = 0;
            if (forwardingType != SshPortForwardingType.Dynamic &&
                (string.IsNullOrWhiteSpace(ForwardDestinationHostBox.Text) ||
                 !int.TryParse(ForwardDestinationPortBox.Text, out destinationPort) ||
                 destinationPort is < 1 or > 65_535))
            {
                SettingsSaveStatusText.Text = Loc.T(
                    "포워딩 대상 주소와 포트를 확인하세요.",
                    "Check the forwarding destination host and port.");
                SettingsSaveStatusText.Visibility = Visibility.Visible;
                ForwardDestinationPortBox.Focus(FocusState.Programmatic);
                return;
            }

            forwardings.Add(new SshPortForwardingRule
            {
                Type = forwardingType,
                BindHost = ForwardBindHostBox.Text.Trim(),
                BindPort = bindPort,
                DestinationHost = forwardingType == SshPortForwardingType.Dynamic
                    ? "127.0.0.1"
                    : ForwardDestinationHostBox.Text.Trim(),
                DestinationPort = destinationPort,
            });
        }

        SettingsSaveStatusText.Visibility = Visibility.Collapsed;

        ConnectRequested?.Invoke(this, new SshConnectionInfo
        {
            Host = host,
            Port = port,
            DisplayName = DisplayNameBox.Text.Trim(),
            Username = UsernameBox.Text.Trim(),
            AuthMethod = _authMethod,
            Password = PasswordBox.Password,
            PrivateKeyPath = _authMethod == SshAuthMethod.PublicKey ? KeyPathBox.Text.Trim() : "",
            Passphrase = PassphraseBox.Password,
            KeepAliveSeconds = double.IsNaN(KeepAliveBox.Value) ? 0 : (int)KeepAliveBox.Value,
            Tags = [.. Tags],
            PortForwardings = forwardings,
            SavedHostId = _savedHostId,
            SaveProfile = saveProfile,
            RememberCredential = rememberCredential,
            CredentialId = _credentialId,
            GroupName = saveProfile ? GroupBox.Text.Trim() : "",
            Environment = saveProfile ? selectedEnvironment : "Unclassified",
            IsFavorite = saveProfile && FavoriteCheck.IsChecked == true,
            Route = new ConnectionRoute
            {
                Id = routeType == ConnectionRouteType.Direct
                    ? "direct"
                    : $"adhoc-{routeType.ToString().ToLowerInvariant()}",
                Type = routeType,
                Host = routeUsesHost ? ProxyHostBox.Text.Trim() : "",
                Port = routeUsesHost ? proxyPort : 0,
                Username = routeUsesHost ? ProxyUsernameBox.Text.Trim() : "",
                Password = routeUsesHost ? ProxyPasswordBox.Password : "",
                AuthMethod = routeType == ConnectionRouteType.SshJump
                    ? jumpAuthMethod
                    : SshAuthMethod.Password,
                PrivateKeyPath = routeType == ConnectionRouteType.SshJump &&
                                 jumpAuthMethod == SshAuthMethod.PublicKey
                    ? JumpKeyPathBox.Text.Trim()
                    : "",
                Passphrase = routeType == ConnectionRouteType.SshJump &&
                             jumpAuthMethod == SshAuthMethod.PublicKey
                    ? JumpPassphraseBox.Password
                    : "",
                Command = routeType == ConnectionRouteType.ExternalProxyCommand
                    ? ProxyCommandBox.Text.Trim()
                    : "",
                ProxyDns = true,
            },
            RoutePolicy = new ConnectionRoutePolicy
            {
                EnterpriseMode = enterpriseMode,
                DisableDirect = enterpriseMode,
            },
        });
    }

    private void SelectEnvironment(string? environment)
    {
        var desired = string.IsNullOrWhiteSpace(environment) ? "Unclassified" : environment;
        EnvironmentCombo.SelectedItem = EnvironmentCombo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, desired, StringComparison.OrdinalIgnoreCase))
            ?? EnvironmentCombo.Items[0];
    }

    private void PersistSettings()
    {
        var result = SettingsService.Save();
        if (result.Succeeded)
        {
            SettingsSaveStatusText.Visibility = Visibility.Collapsed;
            return;
        }

        SettingsSaveStatusText.Text = Loc.T(
            "설정을 저장하지 못했습니다. 연결은 계속할 수 있습니다.",
            "Settings could not be saved. You can still connect.");
        SettingsSaveStatusText.Visibility = Visibility.Visible;
        System.Diagnostics.Debug.WriteLine($"Settings save failed: {result.Error}");
    }
}
