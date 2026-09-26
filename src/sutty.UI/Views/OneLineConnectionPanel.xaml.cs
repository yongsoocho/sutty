using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;
using sutty.Command;
using sutty.Core.Terminal;
using sutty.UI.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace sutty.UI.Views;

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

/// <summary>Opens an external terminal command and saves favorites with the host list.</summary>
public sealed partial class OneLineConnectionPanel : UserControl
{
    private bool _commandLauncherInFlight;
    private bool _commandLauncherStoreSubscribed;
    private long _commandDraftRevision;
    public event LocalCommandLaunchRequestedEventHandler? LocalCommandLaunchRequested;

    public OneLineConnectionPanel()
    {
        InitializeComponent();
        Loaded += Panel_Loaded;
        Unloaded += Panel_Unloaded;
        RefreshRecentCommands();
    }

    public void RefreshLanguage()
    {
        Bindings.Update();
        RefreshRecentCommands();
    }

    private void Panel_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_commandLauncherStoreSubscribed)
        {
            CommandLauncherStore.Changed += CommandLauncherStore_Changed;
            _commandLauncherStoreSubscribed = true;
        }

        RefreshRecentCommands();
    }

    private void Panel_Unloaded(object sender, RoutedEventArgs e)
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
        DispatcherQueue.TryEnqueue(RefreshRecentCommands);
    }

    private void CommandLauncherBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _commandDraftRevision++;
        ClearCommandLauncherStatus();
    }

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
            HostProfileStore.SaveCommandFavorite(plan);
            CommandLauncherBox.Text = plan.CanonicalCommand;
            ShowCommandLauncherStatus(
                "호스트 즐겨찾기에 저장했습니다. 호스트 화면에서 열 수 있습니다.",
                "Saved to host favorites. Open it from Hosts.",
                "StatusGreen");
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Command launcher favorite save failed: {error.GetType().Name}");
            ShowCommandLauncherStoreError(error, CommandLauncherBox);
        }
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

        var launchedFromInput = ReferenceEquals(focusTarget, CommandLauncherBox);
        var draftRevision = _commandDraftRevision;
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

            // Clear only the successfully submitted draft. A new draft typed while
            // opening, or an unrelated draft when reopening history, stays intact.
            if (launchedFromInput && draftRevision == _commandDraftRevision)
                CommandLauncherBox.Text = string.Empty;
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

    public void RefreshRecentCommands()
    {
        if (CommandHistoryPanel is null)
            return;

        try
        {
            RenderCommandHistory(CommandLauncherStore.GetRecentHistory(CommandLauncherStore.MaximumHistoryEntries));
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Command launcher list refresh failed: {error.GetType().Name}");
            CommandHistoryPanel.Children.Clear();
            CommandHistorySection.Visibility = Visibility.Collapsed;
            ShowCommandLauncherStatus(
                "최근 명령을 불러오지 못했습니다. 앱을 다시 열어 보세요.",
                "Recent commands could not be loaded. Try reopening the app.");
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
        if (item is not CommandLauncherHistoryEntry entry) return null;
        var deleteItem = new MenuFlyoutItem
        {
            Text = Loc.T("최근 명령에서 제거", "Remove from recent commands"),
            Tag = entry.Id,
        };
        deleteItem.Click += DeleteHistoryMenuItem_Click;
        AutomationProperties.SetName(deleteItem, deleteItem.Text);
        AutomationProperties.SetHelpText(deleteItem,
            Loc.T("이 최근 명령 항목을 제거합니다.", "Remove this recent command entry."));
        var flyout = new MenuFlyout();
        flyout.Items.Add(deleteItem);
        return flyout;
    }
}
