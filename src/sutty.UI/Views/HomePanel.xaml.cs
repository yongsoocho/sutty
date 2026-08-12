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

    public void ApplyConnectionDefaults(bool applyPort = true, bool applyKeepAlive = true)
    {
        var settings = SettingsService.Current;
        if (applyPort)
            PortBox.Text = settings.DefaultSshPort.ToString();
        if (applyKeepAlive)
            KeepAliveBox.Value = settings.DefaultKeepAliveSeconds;
    }

    public void RefreshLanguage() => Bindings.Update();

    /// <summary>Loads a saved-host or history draft. Secrets are supplied only from the encrypted vault.</summary>
    public void ApplyConnectionDraft(SshConnectionInfo draft)
    {
        HostBox.Text = draft.Host?.Trim() ?? "";
        PortBox.Text = (draft.Port is >= 1 and <= 65535 ? draft.Port : 22).ToString();
        DisplayNameBox.Text = draft.DisplayName?.Trim() ?? "";
        UsernameBox.Text = draft.Username?.Trim() ?? "";

        _authMethod = draft.AuthMethod is SshAuthMethod.Password or SshAuthMethod.PublicKey
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
        EnterpriseRouteCheck.IsChecked = draft.RoutePolicy?.EnterpriseMode == true;

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
        if (requested is not (SshAuthMethod.Password or SshAuthMethod.PublicKey))
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
    }

    private void RouteCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProxyPanel is null || ProxyPortBox is null)
            return;

        var type = SelectedRouteType();
        ProxyPanel.Visibility = type == ConnectionRouteType.Direct
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (type != ConnectionRouteType.Direct && string.IsNullOrWhiteSpace(ProxyPortBox.Text))
            ProxyPortBox.Text = type == ConnectionRouteType.HttpConnect ? "8080" : "1080";
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

    private void UpdateAuthUi()
    {
        StyleAuthButton(AuthPasswordBtn, _authMethod == SshAuthMethod.Password);
        StyleAuthButton(AuthKeyBtn, _authMethod == SshAuthMethod.PublicKey);
        PasswordPanel.Visibility = _authMethod == SshAuthMethod.Password
            ? Visibility.Visible
            : Visibility.Collapsed;
        KeyPanel.Visibility = _authMethod == SshAuthMethod.PublicKey
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
        RememberCredentialCheck.IsEnabled = saveProfile;
        if (!saveProfile)
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
        if (routeType != ConnectionRouteType.Direct &&
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
                Host = routeType == ConnectionRouteType.Direct ? "" : ProxyHostBox.Text.Trim(),
                Port = routeType == ConnectionRouteType.Direct ? 0 : proxyPort,
                Username = routeType == ConnectionRouteType.Direct ? "" : ProxyUsernameBox.Text.Trim(),
                Password = routeType == ConnectionRouteType.Direct ? "" : ProxyPasswordBox.Password,
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
