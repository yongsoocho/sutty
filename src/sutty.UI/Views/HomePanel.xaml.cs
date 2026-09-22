using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;
using sutty.Command;
using sutty.Core.Models;
using sutty.Core.Routing;
using sutty.Core.Terminal;
using sutty.Setting;
using sutty.UI.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace sutty.UI.Views;

public delegate Task HomeConnectRequestedEventHandler(object? sender, SshConnectionInfo info);

/// <summary>
/// Carries one already-validated local terminal launch to the window that owns
/// tabs and process lifetime.  The view deliberately does not start a shell or
/// write launch history itself.
/// </summary>
public sealed class LocalCommandLaunchRequest
{
    public LocalCommandLaunchRequest(
        LocalTerminalLaunchPlan plan,
        long? favoriteId,
        string? displayName)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        FavoriteId = favoriteId is > 0 ? favoriteId : null;
        DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? null
            : displayName.Trim();
    }

    public LocalTerminalLaunchPlan Plan { get; }
    public long? FavoriteId { get; }
    public string? DisplayName { get; }
}

/// <summary>Asks the owning window to open a direct local terminal tab.</summary>
public delegate Task<bool> LocalCommandLaunchRequestedEventHandler(
    object? sender,
    LocalCommandLaunchRequest request);

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
    private bool _connectInFlight;
    private bool _commandLauncherInFlight;
    private bool _commandLauncherStoreSubscribed;

    public event HomeConnectRequestedEventHandler? ConnectRequested;
    public event LocalCommandLaunchRequestedEventHandler? LocalCommandLaunchRequested;
    public IntPtr OwnerWindowHandle { get; set; }
    public ObservableCollection<string> Tags { get; } = [];

    private void AuthenticationChoices_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < 420;
        AuthenticationChoices.ColumnDefinitions[2].Width = new GridLength(compact ? 0 : 1, GridUnitType.Star);
        AuthenticationChoices.ColumnDefinitions[3].Width = new GridLength(compact ? 0 : 1, GridUnitType.Star);
        Grid.SetColumn(AuthAgentBtn, compact ? 0 : 2);
        Grid.SetRow(AuthAgentBtn, compact ? 1 : 0);
        Grid.SetColumn(AuthKeyboardBtn, compact ? 1 : 3);
        Grid.SetRow(AuthKeyboardBtn, compact ? 1 : 0);
    }

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
        Loaded += HomePanel_Loaded;
        Unloaded += HomePanel_Unloaded;
        RefreshCommandLauncherLists();
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
        RefreshCommandLauncherLists();
    }

    private void HomePanel_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_commandLauncherStoreSubscribed)
        {
            CommandLauncherStore.Changed += CommandLauncherStore_Changed;
            _commandLauncherStoreSubscribed = true;
        }

        RefreshCommandLauncherLists();
    }

    private void HomePanel_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!_commandLauncherStoreSubscribed)
            return;

        CommandLauncherStore.Changed -= CommandLauncherStore_Changed;
        _commandLauncherStoreSubscribed = false;
    }

    private void CommandLauncherStore_Changed(object? sender, EventArgs e)
    {
        // A launch can be recorded by MainWindow after the terminal tab is
        // created.  Store events are not guaranteed to arrive on this view's UI
        // thread, so marshal the small list refresh back to the dispatcher.
        DispatcherQueue.TryEnqueue(RefreshCommandLauncherLists);
    }

    private void CommandLauncherBox_TextChanged(object sender, TextChangedEventArgs e)
        => ClearCommandLauncherStatus();

    private async void CommandLauncherBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || _commandLauncherInFlight)
            return;

        e.Handled = true;
        await LaunchCommandAsync(CommandLauncherBox.Text, null, null, CommandLauncherBox);
    }

    private async void RunCommandLauncher_Click(object sender, RoutedEventArgs e)
        => await LaunchCommandAsync(CommandLauncherBox.Text, null, null, CommandLauncherBox);

    private void SaveCommandFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (_commandLauncherInFlight)
            return;

        ClearCommandLauncherStatus();
        if (!TryCreateCommandLaunchPlan(CommandLauncherBox.Text, CommandLauncherBox, out var plan))
            return;

        try
        {
            // The store independently rejects data that looks like a password,
            // token, passphrase, or private key content.  Saving a planner plan
            // also keeps the resolved executable path out of the database.
            CommandLauncherStore.SaveFavorite(null, plan);
            CommandLauncherBox.Text = plan.CanonicalCommand;
            ShowCommandLauncherStatus(
                "명령을 즐겨찾기에 저장했습니다.",
                "The command was saved to favorites.",
                "StatusGreen");
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Command launcher favorite save failed: {error.GetType().Name}");
            ShowCommandLauncherStoreError(error, CommandLauncherBox);
        }
    }

    private async void FavoriteCommand_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CommandLauncherFavorite favorite)
            return;

        // A favorite carries its ID so the owner can atomically record the
        // launch and increment its usage.  History rows below intentionally use
        // no favorite ID: they are immutable snapshots that must remain usable
        // after the favorite changes or is deleted.
        await LaunchCommandAsync(
            favorite.CommandText,
            favorite.Id,
            favorite.DisplayName,
            sender as Control ?? CommandLauncherBox);
    }

    private async void HistoryCommand_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CommandLauncherHistoryEntry entry)
            return;

        await LaunchCommandAsync(
            entry.CommandText,
            null,
            entry.DisplayName,
            sender as Control ?? CommandLauncherBox);
    }

    private void DeleteFavoriteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not long favoriteId)
            return;

        try
        {
            if (CommandLauncherStore.DeleteFavorite(favoriteId))
            {
                ShowCommandLauncherStatus(
                    "명령을 즐겨찾기에서 제거했습니다.",
                    "The command was removed from favorites.",
                    "StatusGreen");
            }
            else
            {
                ShowCommandLauncherStatus(
                    "해당 즐겨찾기는 이미 제거되었습니다.",
                    "That favorite was already removed.");
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Command launcher favorite delete failed: {error.GetType().Name}");
            ShowCommandLauncherStatus(
                "즐겨찾기를 제거하지 못했습니다. 다시 시도하세요.",
                "The favorite could not be removed. Try again.");
        }
    }

    private void DeleteHistoryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not long historyId)
            return;

        try
        {
            if (CommandLauncherStore.DeleteHistory(historyId))
            {
                ShowCommandLauncherStatus(
                    "최근 명령에서 제거했습니다.",
                    "The command was removed from recent history.",
                    "StatusGreen");
            }
            else
            {
                ShowCommandLauncherStatus(
                    "해당 최근 명령은 이미 제거되었습니다.",
                    "That recent command was already removed.");
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Command launcher history delete failed: {error.GetType().Name}");
            ShowCommandLauncherStatus(
                "최근 명령을 제거하지 못했습니다. 다시 시도하세요.",
                "The recent command could not be removed. Try again.");
        }
    }

    private async Task LaunchCommandAsync(
        string? commandText,
        long? favoriteId,
        string? displayName,
        Control focusTarget)
    {
        if (_commandLauncherInFlight)
            return;

        ClearCommandLauncherStatus();
        if (!TryCreateCommandLaunchPlan(commandText, focusTarget, out var plan))
            return;

        _commandLauncherInFlight = true;
        SetCommandLauncherBusy(true);
        try
        {
            var request = new LocalCommandLaunchRequest(plan, favoriteId, displayName);
            var launched = await InvokeLocalCommandLaunchRequestedAsync(request);
            if (!launched)
            {
                ShowCommandLauncherStatus(
                    "명령 터미널을 열지 못했습니다. 열린 탭 수를 확인하고 다시 시도하세요.",
                    "The command terminal could not be opened. Check the open tab limit and try again.");
                focusTarget.Focus(FocusState.Programmatic);
                return;
            }

            // Preserve a canonical, re-parseable form in the input after a
            // successful one-click favorite or history launch.
            CommandLauncherBox.Text = plan.CanonicalCommand;
            ClearCommandLauncherStatus();
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Command launcher request failed: {error.GetType().Name}");
            ShowCommandLauncherStatus(
                "명령 터미널을 열지 못했습니다. 명령과 설치 상태를 확인한 뒤 다시 시도하세요.",
                "The command terminal could not be opened. Check the command and installation, then try again.");
            focusTarget.Focus(FocusState.Programmatic);
        }
        finally
        {
            SetCommandLauncherBusy(false);
            _commandLauncherInFlight = false;
        }
    }

    private async Task<bool> InvokeLocalCommandLaunchRequestedAsync(LocalCommandLaunchRequest request)
    {
        if (LocalCommandLaunchRequested is not { } callbacks)
            return false;

        var launched = false;
        foreach (LocalCommandLaunchRequestedEventHandler callback in callbacks.GetInvocationList())
            launched |= await callback(this, request);
        return launched;
    }

    private bool TryCreateCommandLaunchPlan(
        string? commandText,
        Control focusTarget,
        out LocalTerminalLaunchPlan plan)
    {
        plan = null!;
        if (string.IsNullOrWhiteSpace(commandText))
        {
            ShowCommandLauncherStatus(
                "한 줄 연결 명령을 입력하세요. 예: ssh worker1",
                "Enter a one-line connection command, for example: ssh worker1.");
            focusTarget.Focus(FocusState.Programmatic);
            return false;
        }

        if (LocalTerminalLaunchPlanner.TryCreate(commandText, out var created, out var error) && created is not null)
        {
            plan = created;
            return true;
        }

        var message = DescribeCommandLaunchValidationError(error);
        ShowCommandLauncherStatus(message.Korean, message.English);
        focusTarget.Focus(FocusState.Programmatic);
        return false;
    }

    private static (string Korean, string English) DescribeCommandLaunchValidationError(string? error)
    {
        var text = error ?? "";
        if (text.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("existing absolute", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("resolved", StringComparison.OrdinalIgnoreCase))
        {
            return (
                "명령을 찾지 못했습니다. 설치하거나 PATH에 추가한 뒤 다시 시도하세요.",
                "The command was not found. Install it or add it to PATH, then try again.");
        }

        if (text.Contains("quote", StringComparison.OrdinalIgnoreCase))
        {
            return (
                "따옴표가 올바르게 닫혔는지 확인하세요.",
                "Check that every quote is closed.");
        }

        if (text.Contains("SSH", StringComparison.OrdinalIgnoreCase))
        {
            return (
                "SSH 연결 형식을 확인하세요. 예: ssh worker1 또는 ssh -p 2222 worker1",
                "Check the SSH connection form, for example: ssh worker1 or ssh -p 2222 worker1.");
        }

        if (text.Contains("Multipass", StringComparison.OrdinalIgnoreCase))
        {
            return (
                "Multipass는 multipass connect <인스턴스> 형식으로 입력하세요.",
                "Use Multipass in the form: multipass connect <instance>.");
        }

        return (
            "한 개의 직접 실행 명령만 사용할 수 있습니다. ssh worker1 또는 multipass connect master처럼 입력하세요.",
            "Use one direct executable command, such as ssh worker1 or multipass connect master.");
    }

    private void ShowCommandLauncherStoreError(Exception error, Control focusTarget)
    {
        var text = error.Message;
        if (text.Contains("already a favorite", StringComparison.OrdinalIgnoreCase))
        {
            ShowCommandLauncherStatus(
                "이미 즐겨찾기에 저장된 명령입니다.",
                "That command is already a favorite.");
        }
        else if (text.Contains("limit", StringComparison.OrdinalIgnoreCase))
        {
            ShowCommandLauncherStatus(
                "즐겨찾기 한도에 도달했습니다. 사용하지 않는 항목을 정리한 뒤 다시 시도하세요.",
                "The favorite limit has been reached. Remove an unused item and try again.");
        }
        else if (text.Contains("sensitive", StringComparison.OrdinalIgnoreCase))
        {
            ShowCommandLauncherStatus(
                "비밀번호나 토큰처럼 민감할 수 있는 값이 포함된 명령은 저장할 수 없습니다.",
                "Commands that may contain a password or token cannot be saved.");
        }
        else
        {
            ShowCommandLauncherStatus(
                "즐겨찾기를 저장하지 못했습니다. 명령을 확인하고 다시 시도하세요.",
                "The favorite could not be saved. Check the command and try again.");
        }

        focusTarget.Focus(FocusState.Programmatic);
    }

    private void SetCommandLauncherBusy(bool busy)
    {
        CommandLauncherRunButton.IsEnabled = !busy;
        SaveCommandFavoriteButton.IsEnabled = !busy;
        SetCommandLauncherRowsEnabled(CommandFavoritesPanel, !busy);
        SetCommandLauncherRowsEnabled(CommandHistoryPanel, !busy);
        CommandLauncherRunButton.Content = busy
            ? Loc.T("여는 중…", "Opening…")
            : Loc.T("열기", "Open");
    }

    private static void SetCommandLauncherRowsEnabled(Panel panel, bool enabled)
    {
        foreach (var button in panel.Children.OfType<Button>())
            button.IsEnabled = enabled;
    }

    private void ShowCommandLauncherStatus(string korean, string english, string brushKey = "StatusRed")
    {
        CommandLauncherStatusText.Text = Loc.T(korean, english);
        CommandLauncherStatusText.Foreground = ThemeResources.Brush(this, brushKey);
        CommandLauncherStatusText.Visibility = Visibility.Visible;
    }

    private void ClearCommandLauncherStatus()
    {
        if (CommandLauncherStatusText is null)
            return;

        CommandLauncherStatusText.Text = "";
        CommandLauncherStatusText.Visibility = Visibility.Collapsed;
    }

    private void RefreshCommandLauncherLists()
    {
        if (CommandFavoritesPanel is null || CommandHistoryPanel is null)
            return;

        try
        {
            RenderCommandFavorites(CommandLauncherStore.GetFavorites(CommandLauncherStore.MaximumFavorites));
            RenderCommandHistory(CommandLauncherStore.GetRecentHistory(CommandLauncherStore.MaximumHistoryEntries));
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Command launcher list refresh failed: {error.GetType().Name}");
            CommandFavoritesPanel.Children.Clear();
            CommandHistoryPanel.Children.Clear();
            CommandFavoritesSection.Visibility = Visibility.Collapsed;
            CommandHistorySection.Visibility = Visibility.Collapsed;
            ShowCommandLauncherStatus(
                "명령 즐겨찾기와 기록을 불러오지 못했습니다. 앱을 다시 열어 보세요.",
                "Favorites and recent commands could not be loaded. Try reopening the app.");
        }
    }

    private void RenderCommandFavorites(IReadOnlyList<CommandLauncherFavorite> favorites)
    {
        CommandFavoritesPanel.Children.Clear();
        CommandFavoritesSection.Visibility = favorites.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;

        foreach (var favorite in favorites)
        {
            CommandFavoritesPanel.Children.Add(CreateCommandLauncherRow(
                favorite.DisplayName,
                favorite.CommandText,
                Loc.T("즐겨찾기 명령 열기", "Open favorite command"),
                favorite,
                FavoriteCommand_Click,
                favorite.LaunchCount > 0
                    ? Loc.T($"{favorite.LaunchCount}회 열음", $"Opened {favorite.LaunchCount} times")
                    : Loc.T("아직 열지 않음", "Not opened yet")));
        }
    }

    private void RenderCommandHistory(IReadOnlyList<CommandLauncherHistoryEntry> history)
    {
        CommandHistoryPanel.Children.Clear();
        CommandHistorySection.Visibility = history.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;

        foreach (var entry in history)
        {
            var localTime = entry.LaunchedAtUtc.ToLocalTime();
            CommandHistoryPanel.Children.Add(CreateCommandLauncherRow(
                entry.DisplayName,
                entry.CommandText,
                Loc.T("최근 명령 다시 열기", "Reopen recent command"),
                entry,
                HistoryCommand_Click,
                Loc.T($"최근 실행 · {localTime:g}", $"Last opened · {localTime:g}")));
        }
    }

    private Button CreateCommandLauncherRow(
        string displayName,
        string commandText,
        string accessibleAction,
        object item,
        RoutedEventHandler handler,
        string detail)
    {
        var content = new StackPanel { Spacing = 1 };
        if (!string.Equals(displayName, commandText, StringComparison.Ordinal))
        {
            content.Children.Add(new TextBlock
            {
                Text = displayName,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeResources.Brush(this, "TextPrimary"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
            });
        }

        content.Children.Add(new TextBlock
        {
            Text = commandText,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 10.5,
            FontWeight = string.Equals(displayName, commandText, StringComparison.Ordinal)
                ? FontWeights.SemiBold
                : FontWeights.Normal,
            Foreground = ThemeResources.Brush(this, "TextPrimary"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = detail,
            FontSize = 9.5,
            Foreground = ThemeResources.Brush(this, "TextFaint"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        });

        var button = new Button
        {
            Tag = item,
            Content = content,
            MinHeight = 48,
            IsEnabled = !_commandLauncherInFlight,
            Padding = new Thickness(9, 6, 9, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = ThemeResources.Brush(this, "PillBg"),
            BorderBrush = ThemeResources.Brush(this, "CardBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
        };
        AutomationProperties.SetName(button, $"{accessibleAction}: {displayName}. {commandText}");
        AutomationProperties.SetHelpText(
            button,
            Loc.T(
                "한 번 누르면 로컬 터미널에서 이 명령을 엽니다. 오른쪽 클릭 메뉴에서 제거할 수 있습니다.",
                "Select to open this command in a local terminal. Use the right-click menu to remove it."));
        ToolTipService.SetToolTip(
            button,
            Loc.T("한 번 눌러 로컬 터미널에서 열기", "Open in a local terminal with one click"));
        button.ContextFlyout = CreateCommandLauncherContextFlyout(item);
        button.Click += handler;
        return button;
    }

    private MenuFlyout? CreateCommandLauncherContextFlyout(object item)
    {
        MenuFlyoutItem? deleteItem = item switch
        {
            CommandLauncherFavorite favorite => new MenuFlyoutItem
            {
                Text = Loc.T("즐겨찾기에서 제거", "Remove from favorites"),
                Tag = favorite.Id,
            },
            CommandLauncherHistoryEntry entry => new MenuFlyoutItem
            {
                Text = Loc.T("최근 명령에서 제거", "Remove from recent commands"),
                Tag = entry.Id,
            },
            _ => null,
        };

        if (deleteItem is null)
            return null;

        deleteItem.Click += item is CommandLauncherFavorite
            ? DeleteFavoriteMenuItem_Click
            : DeleteHistoryMenuItem_Click;
        AutomationProperties.SetName(deleteItem, deleteItem.Text);
        AutomationProperties.SetHelpText(
            deleteItem,
            item is CommandLauncherFavorite
                ? Loc.T("이 명령 즐겨찾기를 제거합니다.", "Remove this command from favorites.")
                : Loc.T("이 최근 명령 항목을 제거합니다.", "Remove this recent command entry."));

        var flyout = new MenuFlyout();
        flyout.Items.Add(deleteItem);
        return flyout;
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
        StrictRouteCheck.IsChecked = draft.RoutePolicy?.DisableDirect == true;

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

        OrganizationExpander.IsExpanded = draft.SaveProfile ||
            _savedHostId is not null ||
            !string.IsNullOrWhiteSpace(draft.DisplayName) ||
            !string.IsNullOrWhiteSpace(draft.GroupName) ||
            Tags.Count > 0 ||
            draft.IsFavorite;
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
        ClearFormStatus();
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

    /// <summary>Moves keyboard focus to the first Quick Connect field.</summary>
    public void FocusHost() => HostBox.Focus(FocusState.Programmatic);

    /// <summary>
    /// Clears every transient authentication value before the cached Home surface is
    /// hidden. Non-secret connection fields remain available for the user's draft.
    /// </summary>
    public void ClearTransientSecrets()
    {
        PasswordBox.Password = "";
        PassphraseBox.Password = "";
        ProxyPasswordBox.Password = "";
        JumpPassphraseBox.Password = "";
    }

    private void ProxyCommandInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        ClearFormStatus();
        RefreshProxyCommandPreview();
    }

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
        {
            ClearFormStatus();
            UpdateKeyPathSuggestions(sender.Text);
        }
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

    private async void Connect_Click(object sender, RoutedEventArgs e)
        => await SubmitConnectionAsync();

    private async void QuickConnectInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || _connectInFlight)
            return;

        // The first Enter accepts an open private-key suggestion. A subsequent Enter
        // submits, matching ordinary TextBox and PasswordBox behaviour without racing
        // AutoSuggestBox's own selection handling.
        if (sender is AutoSuggestBox { IsSuggestionListOpen: true })
            return;

        e.Handled = true;
        await SubmitConnectionAsync();
    }

    private void SecretInput_PasswordChanged(object sender, RoutedEventArgs e)
        => ClearFormStatus();

    private async Task SubmitConnectionAsync()
    {
        if (_connectInFlight)
            return;

        ClearFormStatus();
        var host = HostBox.Text.Trim();
        if (host.Length == 0)
        {
            ShowFormError(
                HostBox,
                "서버 주소를 입력하세요.",
                "Enter a server address.");
            return;
        }

        if (string.IsNullOrWhiteSpace(UsernameBox.Text))
        {
            ShowFormError(
                UsernameBox,
                "사용자 이름을 입력하세요.",
                "Enter a user name.");
            return;
        }

        if (!int.TryParse(PortBox.Text, out var port) || port is < 1 or > 65535)
        {
            ShowFormError(
                PortBox,
                "SSH 포트는 1~65535 사이여야 합니다.",
                "SSH port must be between 1 and 65535.");
            return;
        }

        if (_authMethod == SshAuthMethod.PublicKey && string.IsNullOrWhiteSpace(KeyPathBox.Text))
        {
            ShowFormError(
                KeyPathBox,
                "개인 키 파일을 선택하세요.",
                "Choose a private-key file.");
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
        var strictRouteOnly = StrictRouteCheck.IsChecked == true;
        if (strictRouteOnly && routeType == ConnectionRouteType.Direct)
        {
            SettingsSaveStatusText.Text = Loc.T(
                $"엄격 경로에서는 프록시 또는 점프 경로를 선택해야 합니다. ({ConnectionRouteErrorCodes.StrictRouteDirectBlocked})",
                $"Strict route requires a proxy or jump route. ({ConnectionRouteErrorCodes.StrictRouteDirectBlocked})");
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

        var info = new SshConnectionInfo
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
                DisableDirect = strictRouteOnly,
            },
        };

        await InvokeConnectRequestedAsync(info);
    }

    private async Task InvokeConnectRequestedAsync(SshConnectionInfo info)
    {
        _connectInFlight = true;
        ConnectButton.IsEnabled = false;
        var idleContent = ConnectButton.Content;
        ConnectButton.Content = Loc.T("연결 중…", "Connecting…");
        Exception? callbackError = null;

        try
        {
            if (ConnectRequested is { } callbacks)
            {
                foreach (HomeConnectRequestedEventHandler callback in callbacks.GetInvocationList())
                    await callback(this, info);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            callbackError = error;
            System.Diagnostics.Debug.WriteLine(
                $"Quick Connect callback failed: {error.GetType().Name}");
        }
        finally
        {
            // The callback owns the connection attempt and may clear these values itself.
            // Clear them again here so an absent or faulted callback cannot extend the
            // lifetime of secrets held by the transient connection request.
            info.ClearTransientSecrets();
            ClearTransientSecrets();

            ConnectButton.Content = idleContent;
            ConnectButton.IsEnabled = true;
            _connectInFlight = false;
        }

        // PasswordChanged clears stale validation text. Publish the callback error only
        // after secret boxes have been cleared so that the actionable message remains.
        if (callbackError is not null)
        {
            ShowFormError(
                HostBox,
                "연결을 시작하지 못했습니다. 입력을 확인하고 다시 시도하세요.",
                "Could not start the connection. Review the inputs and try again.");
        }
    }

    private void ShowFormError(Control target, string korean, string english)
    {
        SettingsSaveStatusText.Text = Loc.T(korean, english);
        SettingsSaveStatusText.Visibility = Visibility.Visible;
        target.Focus(FocusState.Programmatic);
    }

    private void ClearFormStatus()
    {
        if (SettingsSaveStatusText is null)
            return;

        SettingsSaveStatusText.Text = "";
        SettingsSaveStatusText.Visibility = Visibility.Collapsed;
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
