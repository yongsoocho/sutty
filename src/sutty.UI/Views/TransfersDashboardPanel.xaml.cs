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
            ? $"Sutty SSH · {workspace.ViewModel.ConnectionIdentity} · {workspace.ViewModel.DisplayName}"
            : _activeLocal is { } local
                ? $"{local.ConnectionKindText} · {local.DisplayTitle} · {local.ConnectionIdentity}"
                : "";
        var hasShell = _activeWorkspace is not null || _activeLocal is not null;
        var isExternal = _activeLocal?.IsExternalCommand == true;
        BrowserCard.Visibility = hasShell && !isExternal ? Visibility.Visible : Visibility.Collapsed;
        ExternalTerminalState.Visibility = isExternal ? Visibility.Visible : Visibility.Collapsed;
        NoTargetState.Visibility = hasShell ? Visibility.Collapsed : Visibility.Visible;
        BrowserScopeText.Text = _activeWorkspace is not null
            ? Loc.T("선택한 Sutty SSH 연결의 원격 파일 · 아래 전송 큐는 모든 탭의 작업을 표시합니다.",
                "Remote files for the selected Sutty SSH connection · the queue below includes all tabs.")
            : _activeLocal is { IsExternalCommand: false }
                ? Loc.T("이 PC의 로컬 파일 · 아래 전송 큐는 모든 탭의 작업을 표시합니다.",
                    "Local files on this PC · the queue below includes all tabs.")
                : Loc.T("아래 전송 큐는 모든 탭의 작업이며, 선택한 터미널의 원격 파일 목록이 아닙니다.",
                    "The queue below includes work from all tabs; it is not a remote file listing for the selected terminal.");
        ToolTipService.SetToolTip(ActiveShellText, ActiveShellText.Text);
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
            else if (_activeLocal is { IsExternalCommand: false } local)
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
