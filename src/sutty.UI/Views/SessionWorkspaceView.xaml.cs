using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using sutty.Core.Models;
using sutty.Core.Routing;
using sutty.Core.Terminal;
using Windows.ApplicationModel.DataTransfer;
using sutty.Setting;
using sutty.UI.Helpers;
using sutty.UI.ViewModels;
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace sutty.UI.Views;

/// <summary>Keeps a shell visible while displaying tools for that exact SSH session beside it.</summary>
public sealed partial class SessionWorkspaceView : UserControl
{
    private bool _filesBound;
    private bool _detached;
    private int _openTerminalHereInFlight;
    private Task? _filesBindingTask;
    private ContentControl? _externalFilesHost;
    private readonly CancellationTokenSource _lifetime = new();
    public SessionWorkspaceViewModel ViewModel { get; }

    public SessionView SessionView { get; }

    public FileTreePanel FileTree => FilesPanel;

    public ObservableCollection<TunnelRow> Tunnels { get; } = [];

    public SessionWorkspaceSection CurrentSection => ViewModel.CurrentSection;

    public event EventHandler<SessionWorkspaceSection>? SectionChanged;
    public event EventHandler? TerminalActivationRequested;

    // A borrowed file browser can be visible while its owning SSH workspace is not.
    private XamlRoot? DialogRoot => FilesPanel.XamlRoot ?? XamlRoot;

    public SessionWorkspaceView(SessionView sessionView, IntPtr ownerWindowHandle)
    {
        SessionView = sessionView ?? throw new ArgumentNullException(nameof(sessionView));
        var info = sessionView.Session.Info;
        ViewModel = new SessionWorkspaceViewModel(
            sessionView.Session.Id,
            info.Title,
            info.Username,
            info.Host,
            info.Port,
            SessionWorkspaceViewModel.ResolveInitialSection(
                SettingsService.Current.TerminalMode));

        InitializeComponent();
        SessionContent.Content = sessionView;
        CommandResultsHost.Content = sessionView.TakeCommandRunner();
        sessionView.WorkingDirectoryChanged += SessionView_WorkingDirectoryChanged;

        FilesPanel.OwnerWindowHandle = ownerWindowHandle;
        FilesPanel.OpenTerminalHereRequested += FilesPanel_OpenTerminalHereRequested;
        CommandLibrary.RunRequested += CommandLibrary_RunRequested;

        if (sessionView.Session is IPortForwardingSession tunnels)
            tunnels.TunnelsChanged += Tunnels_Changed;
        sessionView.Session.StateChanged += Tunnels_SessionStateChanged;
        RefreshTunnels();

        NavigateTo(ViewModel.CurrentSection);
        ActualThemeChanged += (_, _) => UpdateNavigationVisuals();
    }

    public void NavigateTo(SessionWorkspaceSection section)
    {
        var previousSection = CurrentSection;
        ViewModel.SelectSection(section);
        AuxiliaryPane.Visibility = section == SessionWorkspaceSection.Terminal
            ? Visibility.Collapsed
            : Visibility.Visible;
        UpdateWorkspaceLayout();
        if (section == SessionWorkspaceSection.Files && _externalFilesHost is { } externalHost)
            RestoreFileBrowser(externalHost);
        FilesPanelHost.Visibility = section == SessionWorkspaceSection.Files
            ? Visibility.Visible
            : Visibility.Collapsed;
        TunnelsPane.Visibility = section == SessionWorkspaceSection.Tunnels
            ? Visibility.Visible
            : Visibility.Collapsed;

        var commands = section == SessionWorkspaceSection.Commands;
        CommandsPane.Visibility = commands ? Visibility.Visible : Visibility.Collapsed;
        if (commands)
            SessionView.FocusCommandRunner();
        else if (section == SessionWorkspaceSection.Terminal)
            SessionView.FocusTerminal();
        else if (section == SessionWorkspaceSection.Files && !_filesBound)
        {
            _filesBound = true;
            _filesBindingTask = BindFilesAsync();
        }

        UpdateNavigationVisuals();
        if (CurrentSection != previousSection)
            SectionChanged?.Invoke(this, CurrentSection);
    }

    private void Workspace_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateWorkspaceLayout();

    private void UpdateWorkspaceLayout()
    {
        if (AuxiliaryBorder is null) return;
        var compactHeader = ActualWidth < 740;
        Grid.SetRow(WorkspaceNavigation, compactHeader ? 1 : 0);
        Grid.SetColumn(WorkspaceNavigation, compactHeader ? 0 : 1);
        Grid.SetColumnSpan(WorkspaceNavigation, compactHeader ? 2 : 1);

        var toolsOpen = CurrentSection != SessionWorkspaceSection.Terminal;
        var stacked = ActualWidth < 960;
        AuxiliaryBorder.Visibility = toolsOpen ? Visibility.Visible : Visibility.Collapsed;
        AuxiliaryColumn.Width = toolsOpen && !stacked
            ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        AuxiliaryRow.Height = toolsOpen && stacked
            ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ShellRow.Height = new GridLength(toolsOpen && stacked ? 1.4 : 1, GridUnitType.Star);
        Grid.SetColumn(AuxiliaryBorder, stacked ? 0 : 1);
        Grid.SetRow(AuxiliaryBorder, stacked ? 1 : 0);
        AuxiliaryBorder.BorderThickness = stacked ? new Thickness(0, 1, 0, 0) : new Thickness(1, 0, 0, 0);
    }

    public void ReapplyTerminalSettings()
    {
        SessionView.ApplyTerminalSettings();
        NavigateTo(CurrentSection);
    }

    public void RefreshLanguage()
    {
        Bindings.Update();
        SessionView.RefreshLanguage();
        FilesPanel.RefreshLanguage();
        CommandLibrary.RefreshLanguage();
        RefreshTunnels();
    }

    public void CancelTransfers(bool userInitiated) =>
        FilesPanel.CancelTransfersForSession(SessionView.Session, userInitiated);

    /// <summary>
    /// Lends this session's existing browser to the global Transfers page. The browser,
    /// transfer executor and drag payloads retain their single session owner.
    /// </summary>
    public Task ShowFileBrowserAsync(ContentControl host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (_detached) return Task.CompletedTask;
        if (_externalFilesHost is { } previousHost && !ReferenceEquals(previousHost, host))
            RestoreFileBrowser(previousHost);
        if (!ReferenceEquals(_externalFilesHost, host) || !ReferenceEquals(host.Content, FilesPanel))
        {
            FilesPanelHost.Content = null;
            _externalFilesHost = host;
            FilesPanel.PreferRemotePane = true;
            host.Content = FilesPanel;
        }
        if (!_filesBound)
        {
            _filesBound = true;
            _filesBindingTask = BindFilesAsync();
        }
        return _filesBindingTask ?? Task.CompletedTask;
    }

    /// <summary>Returns a borrowed browser without disconnecting or cancelling transfers.</summary>
    public void RestoreFileBrowser(ContentControl host)
    {
        if (!ReferenceEquals(_externalFilesHost, host)) return;
        if (ReferenceEquals(host.Content, FilesPanel))
            host.Content = null;
        _externalFilesHost = null;
        FilesPanel.PreferRemotePane = false;
        if (!_detached)
            FilesPanelHost.Content = FilesPanel;
    }

    public async Task DetachAsync(bool userInitiated)
    {
        if (_detached) return;
        _detached = true;
        if (_externalFilesHost is { } externalHost)
            RestoreFileBrowser(externalHost);
        FilesPanel.StopRemoteEditing();
        _lifetime.Cancel();
        if (SessionView.Session is IPortForwardingSession tunnels)
            tunnels.TunnelsChanged -= Tunnels_Changed;
        SessionView.Session.StateChanged -= Tunnels_SessionStateChanged;
        CancelTransfers(userInitiated);
        SessionView.WorkingDirectoryChanged -= SessionView_WorkingDirectoryChanged;
        FilesPanel.OpenTerminalHereRequested -= FilesPanel_OpenTerminalHereRequested;
        CommandLibrary.RunRequested -= CommandLibrary_RunRequested;
        if (_activeTunnelOperation is { } operation)
        {
            try { await operation; }
            catch { /* The operation UI owns its failure; detach must still release Files. */ }
        }
        _lifetime.Dispose();
        try
        {
            if (_filesBindingTask is { } binding)
            {
                try { await binding; }
                catch { /* A failed/cancelled initial bind must still release panel subscriptions. */ }
            }
            if (_filesBound)
                await FilesPanel.LoadAsync(null);
        }
        finally { FilesPanel.LocalBrowser.Dispose(); }
    }

    private async Task BindFilesAsync()
    {
        await FilesPanel.LoadAsync(SessionView.Session);
        if (_detached)
        {
            await FilesPanel.LoadAsync(null);
            return;
        }
        if ((CurrentSection == SessionWorkspaceSection.Files || _externalFilesHost is not null) &&
            SessionView.WorkingDirectory.StartsWith("/", StringComparison.Ordinal))
        {
            await FilesPanel.NavigateToPathAsync(SessionView.WorkingDirectory);
        }
    }

    private void TerminalButton_Click(object sender, RoutedEventArgs e) =>
        NavigateTo(SessionWorkspaceSection.Terminal);

    private void FilesButton_Click(object sender, RoutedEventArgs e) =>
        NavigateTo(SessionWorkspaceSection.Files);

    private void CommandsButton_Click(object sender, RoutedEventArgs e) =>
        NavigateTo(SessionWorkspaceSection.Commands);

    private void TunnelsButton_Click(object sender, RoutedEventArgs e) =>
        NavigateTo(SessionWorkspaceSection.Tunnels);

    private async void CommandLibrary_RunRequested(object? sender, string command) =>
        await RunCommandIfAttachedAsync(command);

    private async Task RunCommandIfAttachedAsync(string command)
    {
        if (!_detached)
            await SessionView.RunExternalCommandAsync(command);
    }

    private void SessionView_WorkingDirectoryChanged(object? sender, string remotePath)
    {
        if (!_detached && (CurrentSection == SessionWorkspaceSection.Files || _externalFilesHost is not null) &&
            remotePath.StartsWith("/", StringComparison.Ordinal))
        {
            _ = FilesPanel.NavigateToPathAsync(remotePath);
        }
    }

    private async void FilesPanel_OpenTerminalHereRequested(object? sender, string remotePath)
    {
        if (_detached || Interlocked.Exchange(ref _openTerminalHereInFlight, 1) != 0)
            return;

        try
        {
            await OpenTerminalHereCoreAsync(remotePath);
        }
        finally
        {
            Volatile.Write(ref _openTerminalHereInFlight, 0);
        }
    }

    private async Task OpenTerminalHereCoreAsync(string remotePath)
    {
        if (_detached || DialogRoot is not { } root) return;
        string command;
        try { command = SessionView.PrepareDirectoryCommand(remotePath); }
        catch (ArgumentException)
        {
            await ShowWorkspaceMessageAsync(Loc.T("경로 확인", "Check path"),
                Loc.T("제어 문자가 없는 원격 절대 경로를 선택하세요.",
                    "Choose an absolute remote path without control characters."));
            return;
        }

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = ViewModel.ConnectionIdentity,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = Loc.T(
                "아래 POSIX 셸 명령을 복사해 사용할 수 있습니다. vim·top 등에서 나온 뒤 셸 프롬프트에 직접 붙여넣고 Enter로 실행하세요. 다른 셸에서는 경로 이동 구문을 확인하세요.",
                "Copy this POSIX shell command. Leave vim, top, or other programs, paste it at a shell prompt, then press Enter yourself. Check directory syntax if using a different shell."),
            TextWrapping = TextWrapping.Wrap,
        });
        var prepared = new TextBox
        {
            Text = command,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(prepared,
            Loc.T("준비된 경로 이동 명령", "Prepared directory command"));
        content.Children.Add(prepared);
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = Loc.T("터미널에서 열기", "Open in terminal"),
            Content = content,
            PrimaryButtonText = Loc.T("복사 후 터미널 보기", "Copy and show terminal"),
            CloseButtonText = Loc.T("닫기", "Close"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || _detached) return;
        try
        {
            var data = new DataPackage();
            data.SetText(command); // No newline: copying cannot request shell execution.
            Clipboard.SetContent(data);
        }
        catch
        {
            await ShowWorkspaceMessageAsync(Loc.T("복사 실패", "Copy failed"),
                Loc.T("클립보드를 사용할 수 없습니다. 다시 시도하세요.",
                    "The clipboard is unavailable. Try again."));
            return;
        }
        NavigateTo(SessionWorkspaceSection.Terminal);
        TerminalActivationRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void OpenFilesPath_Click(object sender, RoutedEventArgs e)
    {
        if (_detached || DialogRoot is not { } root) return;
        var path = new TextBox
        {
            Header = Loc.T("원격 절대 경로", "Absolute remote path"),
            PlaceholderText = "/var/www",
            Text = SessionView.WorkingDirectory.StartsWith("/", StringComparison.Ordinal)
                ? SessionView.WorkingDirectory : "",
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(path,
            Loc.T("Files에서 열 원격 절대 경로", "Absolute remote path to open in Files"));
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock { Text = ViewModel.ConnectionIdentity, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(path);
        content.Children.Add(error);
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = Loc.T("Files에서 경로 열기", "Open path in Files"),
            Content = content,
            PrimaryButtonText = Loc.T("열기", "Open"),
            CloseButtonText = Loc.T("취소", "Cancel"),
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try { TerminalDirectoryCommand.ValidateAbsolutePath(path.Text); }
            catch (ArgumentException)
            {
                args.Cancel = true;
                error.Text = Loc.T("/로 시작하며 제어 문자가 없는 경로를 입력하세요.",
                    "Enter a path beginning with / and without control characters.");
            }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || _detached) return;
        NavigateTo(SessionWorkspaceSection.Files);
        if (_filesBindingTask is not null) await _filesBindingTask;
        if (!_detached) await FilesPanel.NavigateToPathAsync(path.Text);
    }

    private async Task ShowWorkspaceMessageAsync(string title, string message)
    {
        if (_detached || DialogRoot is not { } root) return;
        await new ContentDialog
        {
            XamlRoot = root, Title = title, Content = message,
            CloseButtonText = Loc.T("닫기", "Close"),
        }.ShowAsync();
    }

    private void UpdateNavigationVisuals()
    {
        SetButtonState(TerminalButton, CurrentSection == SessionWorkspaceSection.Terminal);
        SetButtonState(FilesButton, CurrentSection == SessionWorkspaceSection.Files);
        SetButtonState(CommandsButton, CurrentSection == SessionWorkspaceSection.Commands);
        SetButtonState(TunnelsButton, CurrentSection == SessionWorkspaceSection.Tunnels);
    }

    private void SetButtonState(ToggleButton button, bool selected)
    {
        button.IsChecked = selected;
        button.Background = ThemeResources.Brush(this, selected ? "AccentTint" : "PillBg");
        button.Foreground = ThemeResources.Brush(this, selected ? "TextPrimary" : "TextMuted");
    }

}
