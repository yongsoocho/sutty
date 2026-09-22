using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using sutty.UI.Helpers;
using System;
using System.Runtime.CompilerServices;

namespace sutty.UI.Views;

/// <summary>Shows only the selected shell's browser above the durable transfer queue.</summary>
public sealed partial class TransfersDashboardPanel : UserControl
{
    private readonly ConditionalWeakTable<LocalTerminalView, LocalBrowserPanel> _localBrowsers = new();
    private SessionWorkspaceView? _activeWorkspace;
    private LocalTerminalView? _activeLocal;
    private bool _mounted;
    private int _selectionVersion;

    public TransfersDashboardPanel()
    {
        InitializeComponent();
        TransferQueue.SetCompactPresentation(true);
    }

    /// <summary>No alternate session is selected when the current shell has no SSH workspace.</summary>
    public void SetActiveShell(SessionWorkspaceView? workspace, LocalTerminalView? local)
    {
        if (workspace is not null) local = null;
        if (ReferenceEquals(workspace, _activeWorkspace) && ReferenceEquals(local, _activeLocal))
        {
            UpdateIdentity();
            return;
        }
        DetachBrowser();
        _activeWorkspace = workspace;
        _activeLocal = local;
        UpdateIdentity();
        if (_mounted) AttachBrowser();
    }

    /// <summary>Releases a closed tab's cached filesystem browser and outstanding enumeration.</summary>
    public void ReleaseLocalShell(LocalTerminalView local)
    {
        if (ReferenceEquals(_activeLocal, local)) SetActiveShell(null, null);
        if (_localBrowsers.TryGetValue(local, out var browser))
        {
            _localBrowsers.Remove(local);
            browser.Dispose();
        }
    }

    public void RefreshFromStore() => TransferQueue.RefreshFromStore();

    public void RefreshLanguage()
    {
        Bindings.Update();
        TransferQueue.RefreshLanguage();
        if (BrowserHost.Content is LocalBrowserPanel localBrowser) localBrowser.RefreshLanguage();
        UpdateIdentity();
    }

    private void UpdateIdentity()
    {
        ActiveShellText.Text = _activeWorkspace is { } workspace
            ? $"{workspace.ViewModel.DisplayName} · {workspace.ViewModel.ConnectionIdentity}"
            : _activeLocal?.DisplayTitle ?? "";
        var hasShell = _activeWorkspace is not null || _activeLocal is not null;
        BrowserCard.Visibility = hasShell ? Visibility.Visible : Visibility.Collapsed;
        NoTargetState.Visibility = hasShell ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Dashboard_Loaded(object sender, RoutedEventArgs e)
    {
        _mounted = true;
        AttachBrowser();
        TransferQueue.RefreshFromStore();
    }

    private void Dashboard_Unloaded(object sender, RoutedEventArgs e)
    {
        _mounted = false;
        DetachBrowser();
    }

    private void DetachBrowser()
    {
        ++_selectionVersion;
        if (_activeLocal is not null) _activeLocal.WorkingDirectoryChanged -= Local_WorkingDirectoryChanged;
        if (BrowserHost.Content is LocalBrowserPanel localBrowser) localBrowser.CancelPendingNavigation();
        _activeWorkspace?.RestoreFileBrowser(BrowserHost);
        BrowserHost.Content = null;
        BrowserError.Visibility = Visibility.Collapsed;
    }

    private async void AttachBrowser()
    {
        var version = ++_selectionVersion;
        UpdateIdentity();
        BrowserError.Visibility = Visibility.Collapsed;
        try
        {
            if (_activeWorkspace is { } workspace)
                await workspace.ShowFileBrowserAsync(BrowserHost);
            else if (_activeLocal is { } local)
            {
                var browser = _localBrowsers.GetValue(local, _ => new LocalBrowserPanel());
                browser.RefreshLanguage();
                BrowserHost.Content = browser;
                local.WorkingDirectoryChanged -= Local_WorkingDirectoryChanged;
                local.WorkingDirectoryChanged += Local_WorkingDirectoryChanged;
                await browser.FollowWorkingDirectoryAsync(local.WorkingDirectory, force: true);
            }
        }
        catch (Exception error)
        {
            if (!_mounted || version != _selectionVersion) return;
            BrowserError.Text = Loc.T($"파일을 열 수 없습니다. {error.Message}",
                $"Could not open the file browser. {error.Message}");
            BrowserError.Visibility = Visibility.Visible;
        }
    }

    private async void Local_WorkingDirectoryChanged(object? sender, string directory)
    {
        if (!_mounted || !ReferenceEquals(sender, _activeLocal) ||
            BrowserHost.Content is not LocalBrowserPanel browser) return;
        // The per-tab browser owns cancellation/versioning; a late A result cannot publish in B.
        await browser.FollowWorkingDirectoryAsync(directory);
    }
}
