using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using sutty.Core.Models;
using sutty.Setting;
using sutty.UI.Helpers;
using sutty.UI.ViewModels;
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace sutty.UI.Views;

/// <summary>Keeps Terminal, Files, Commands, and Tunnels bound to one exact SSH session.</summary>
public sealed partial class SessionWorkspaceView : UserControl
{
    private bool _filesBound;
    private bool _detached;
    private int _openTerminalHereInFlight;
    public SessionWorkspaceViewModel ViewModel { get; }

    public SessionView SessionView { get; }

    public FileTreePanel FileTree => FilesPanel;

    public ObservableCollection<string> Tunnels { get; } = [];

    public SessionWorkspaceSection CurrentSection => ViewModel.CurrentSection;

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
        sessionView.UseWorkspaceNavigation();
        sessionView.WorkingDirectoryChanged += SessionView_WorkingDirectoryChanged;

        FilesPanel.OwnerWindowHandle = ownerWindowHandle;
        FilesPanel.OpenTerminalHereRequested += FilesPanel_OpenTerminalHereRequested;
        CommandLibrary.RunRequested += CommandLibrary_RunRequested;

        foreach (var rule in info.PortForwardings ?? [])
            Tunnels.Add(DescribeTunnel(rule));
        NoTunnelsState.Visibility = Tunnels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // This page is informational until runtime tunnel controls exist. Hiding an empty
        // peer tab avoids implying that Sutty already provides a Tunnel Manager.
        TunnelsButton.Visibility = Tunnels.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        NavigateTo(ViewModel.CurrentSection);
        ActualThemeChanged += (_, _) => UpdateNavigationVisuals();
    }

    public void NavigateTo(SessionWorkspaceSection section)
    {
        ViewModel.SelectSection(section);
        InteractivePane.Visibility = section is SessionWorkspaceSection.Terminal or
            SessionWorkspaceSection.Commands
            ? Visibility.Visible
            : Visibility.Collapsed;
        FilesPanel.Visibility = section == SessionWorkspaceSection.Files
            ? Visibility.Visible
            : Visibility.Collapsed;
        TunnelsPane.Visibility = section == SessionWorkspaceSection.Tunnels
            ? Visibility.Visible
            : Visibility.Collapsed;

        var commands = section == SessionWorkspaceSection.Commands;
        CommandLibraryColumn.Width = commands ? new GridLength(320) : new GridLength(0);
        CommandLibraryHost.Visibility = commands ? Visibility.Visible : Visibility.Collapsed;
        if (commands)
            SessionView.ShowCommandsWorkspace();
        else if (section == SessionWorkspaceSection.Terminal)
            SessionView.ShowTerminalWorkspace();
        else if (section == SessionWorkspaceSection.Files && !_filesBound)
        {
            _filesBound = true;
            _ = BindFilesAsync();
        }

        UpdateNavigationVisuals();
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
    }

    public void CancelTransfers(bool userInitiated) =>
        FilesPanel.CancelTransfersForSession(SessionView.Session, userInitiated);

    public async Task DetachAsync(bool userInitiated)
    {
        _detached = true;
        CancelTransfers(userInitiated);
        SessionView.WorkingDirectoryChanged -= SessionView_WorkingDirectoryChanged;
        FilesPanel.OpenTerminalHereRequested -= FilesPanel_OpenTerminalHereRequested;
        CommandLibrary.RunRequested -= CommandLibrary_RunRequested;
        if (_filesBound)
            await FilesPanel.LoadAsync(null);
    }

    private async Task BindFilesAsync()
    {
        await FilesPanel.LoadAsync(SessionView.Session);
        if (_detached)
        {
            await FilesPanel.LoadAsync(null);
            return;
        }
        if (CurrentSection == SessionWorkspaceSection.Files &&
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
        if (!_detached && CurrentSection == SessionWorkspaceSection.Files &&
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
        if (_detached)
            return;

        var allowExistingInput = false;
        if (SessionView.HasOpenInteractiveTerminal)
        {
            allowExistingInput = await ConfirmTerminalInputAsync(remotePath);
            if (_detached || !allowExistingInput)
                return;
        }

        var opened = await SessionView.OpenDirectoryInTerminalAsync(
            remotePath,
            allowExistingInput);
        if (_detached)
            return;
        if (opened)
        {
            NavigateTo(SessionWorkspaceSection.Terminal);
            return;
        }

        // A PTY can finish opening while the remote path is being validated. Re-check
        // and obtain a fresh confirmation before injecting into that now-existing PTY.
        if (!allowExistingInput && SessionView.HasOpenInteractiveTerminal)
        {
            if (!await ConfirmTerminalInputAsync(remotePath) || _detached)
                return;
            opened = await SessionView.OpenDirectoryInTerminalAsync(remotePath, true);
            if (_detached)
                return;
            if (opened)
            {
                NavigateTo(SessionWorkspaceSection.Terminal);
                return;
            }
        }

        if (XamlRoot is not { } root)
            return;
        var failure = new ContentDialog
        {
            XamlRoot = root,
            Title = Loc.T("터미널에서 열 수 없음", "Could not open in terminal"),
            Content = Loc.T("연결 상태와 원격 경로를 확인하세요.", "Check the connection and remote path."),
            CloseButtonText = "OK",
        };
        await failure.ShowAsync();
    }

    private async Task<bool> ConfirmTerminalInputAsync(string remotePath)
    {
        if (_detached || XamlRoot is not { } root)
            return false;

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = Loc.T(
                "현재 터미널에서 프로그램이 실행 중일 수 있습니다. 이 경로로 이동하는 명령을 보내시겠습니까?",
                "A program may be running in the terminal. Send the command that changes to this path?"),
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = $"cd {remotePath}",
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
        });
        var confirmation = new ContentDialog
        {
            XamlRoot = root,
            Title = Loc.T("터미널 입력 확인", "Confirm terminal input"),
            Content = content,
            PrimaryButtonText = Loc.T("명령 보내기", "Send command"),
            CloseButtonText = Loc.T("취소", "Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await confirmation.ShowAsync() == ContentDialogResult.Primary;
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

    private static string DescribeTunnel(SshPortForwardingRule rule) => rule.Type switch
    {
        SshPortForwardingType.Local =>
            $"LOCAL   {rule.BindHost}:{rule.BindPort} → {rule.DestinationHost}:{rule.DestinationPort}",
        SshPortForwardingType.Remote =>
            $"REMOTE  {rule.BindHost}:{rule.BindPort} → {rule.DestinationHost}:{rule.DestinationPort}",
        SshPortForwardingType.Dynamic =>
            $"SOCKS   {rule.BindHost}:{rule.BindPort}",
        _ => rule.Type.ToString().ToUpperInvariant(),
    };
}
