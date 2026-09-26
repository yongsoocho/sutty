using Microsoft.UI.Xaml.Controls;
using sutty.Core.Sessions;
using sutty.Core.Terminal;
using sutty.Setting;
using sutty.UI.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace sutty.UI.Views;

public sealed partial class MainWindow
{
    private readonly Dictionary<TabViewItem, ShellTabLifetimeRegistration> _shellTabLifetimes = [];
    private readonly HashSet<TabViewItem> _closingShellTabs = [];
    private readonly Dictionary<TabViewItem, ShellTabLifetimePolicy> _pendingShellAutoCloses = [];
    private bool _processingShellAutoCloses;
    private bool _autoClosedLastTab;

    private void TrackShellTabLifetime(TabViewItem tab)
    {
        var policy = new ShellTabLifetimePolicy();
        EventHandler<TerminalState> terminalChanged = (_, state) =>
        {
            policy.ObserveTerminal(state);
            if (policy.HasEnded)
                DispatcherQueue.TryEnqueue(() => QueueEndedShellTab(tab, policy));
        };
        if (tab.DataContext is SessionView view)
        {
            var session = view.Session;
            policy.ObserveSession(session.State);
            policy.ObserveTerminal(session.TerminalState);
            EventHandler<SessionState> sessionChanged = (_, state) =>
            {
                policy.ObserveSession(state);
                if (policy.HasEnded)
                    DispatcherQueue.TryEnqueue(() => QueueEndedShellTab(tab, policy));
            };
            _shellTabLifetimes.Add(tab, new(policy, () =>
            {
                session.TerminalStateChanged -= terminalChanged;
                session.StateChanged -= sessionChanged;
            }));
            session.TerminalStateChanged += terminalChanged;
            session.StateChanged += sessionChanged;
        }
        else if (tab.DataContext is LocalTerminalView local)
        {
            policy.ObserveTerminal(local.Terminal.TerminalState);
            _shellTabLifetimes.Add(tab, new(policy,
                () => local.Terminal.TerminalStateChanged -= terminalChanged));
            local.Terminal.TerminalStateChanged += terminalChanged;
        }
    }

    private void QueueEndedShellTab(TabViewItem tab, ShellTabLifetimePolicy policy)
    {
        if (_windowClosing || !TitleTabs.TabItems.Contains(tab)) return;
        _pendingShellAutoCloses[tab] = policy;
        ResumePendingShellAutoCloses();
    }

    private void ResumePendingShellAutoCloses()
    {
        if (_processingShellAutoCloses || _closePromptOpen || _windowClosing || _pendingShellAutoCloses.Count == 0) return;
        _ = ProcessEndedShellTabsAsync();
    }

    private async Task ProcessEndedShellTabsAsync()
    {
        _processingShellAutoCloses = true;
        try
        {
            while (!_windowClosing && !_closePromptOpen && _pendingShellAutoCloses.Count > 0)
            {
                var (tab, policy) = _pendingShellAutoCloses.First();
                _pendingShellAutoCloses.Remove(tab);
                if (!TitleTabs.TabItems.Contains(tab) ||
                    !_shellTabLifetimes.TryGetValue(tab, out var registration) ||
                    !ReferenceEquals(registration.Policy, policy) ||
                    !policy.TryRequestClose(SettingsService.Current.AutoCloseDisconnectedTabs)) continue;
                // A concurrent manual close already owns the owner's recovery decision.
                if (!_closingShellTabs.Contains(tab)) await CloseShellTabAsync(tab, automatic: true);
            }
        }
        catch (Exception error)
        {
            // Retaining a stopped tab is safer than bypassing a failed recovery prompt.
            Debug.WriteLine($"Automatic shell-tab close failed: {error.GetType().Name}");
        }
        finally
        {
            _processingShellAutoCloses = false;
            if (!_windowClosing && !_closePromptOpen && _pendingShellAutoCloses.Count > 0)
                DispatcherQueue.TryEnqueue(ResumePendingShellAutoCloses);
        }
    }

    private void UntrackShellTab(TabViewItem tab)
    {
        _pendingShellAutoCloses.Remove(tab);
        if (_shellTabLifetimes.Remove(tab, out var registration)) registration.Unsubscribe();
    }

    private void StopTrackingShellTabs()
    {
        foreach (var tab in _shellTabLifetimes.Keys.ToArray()) UntrackShellTab(tab);
    }

    private sealed record ShellTabLifetimeRegistration(ShellTabLifetimePolicy Policy, Action Unsubscribe);

    private async Task CloseShellTabAsync(TabViewItem tab, bool automatic)
    {
        if (_closePromptOpen || _windowClosing || !TitleTabs.TabItems.Contains(tab) ||
            !_closingShellTabs.Add(tab)) return;
        try
        {
            if (tab.DataContext is SessionView pendingSession &&
                _sessionWorkspaces.TryGetValue(pendingSession, out var pendingWorkspace) &&
                !await ConfirmWorkspacesCloseAsync([pendingWorkspace]))
            {
                // Keep working means keep this owner, including a logout observed during
                // the manual prompt. Other ended tabs remain queued for their own review.
                _pendingShellAutoCloses.Remove(tab);
                if (_shellTabLifetimes.TryGetValue(tab, out var retainedLifetime))
                    retainedLifetime.Policy.TryRequestClose(SettingsService.Current.AutoCloseDisconnectedTabs);
                return;
            }

            // Dialogs can yield to settings, terminal restart, or another close request.
            if (_windowClosing || !TitleTabs.TabItems.Contains(tab) || (automatic &&
                (!SettingsService.Current.AutoCloseDisconnectedTabs ||
                 !_shellTabLifetimes.TryGetValue(tab, out var lifetime) || !lifetime.Policy.HasEnded)))
            {
                if (tab.DataContext is SessionView retained && _sessionWorkspaces.TryGetValue(retained, out var retainedWorkspace))
                    retainedWorkspace.FileTree.SuspendAutomaticEdits(false);
                return;
            }

            UntrackShellTab(tab);
            if (tab.DataContext is SessionView closingSession)
            {
                RememberFailedSupportContext(closingSession.Session, allowUnsequencedOverwrite: false);
                if (_sessionWorkspaces.TryGetValue(closingSession, out var closingWorkspace))
                    closingWorkspace.CancelTransfers(userInitiated: !automatic);
            }

            var preserveGlobalPage = !_isMultiView && _shellState.Mode == AppShellMode.Global;
            var preserveMultiView = _isMultiView;
            _suppressTabActivation = true;
            try
            {
                TitleTabs.TabItems.Remove(tab);
                if (TitleTabs.TabItems.Count > 0 && (TitleTabs.SelectedItem is null ||
                    !TitleTabs.TabItems.Contains(TitleTabs.SelectedItem)))
                    TitleTabs.SelectedItem = TitleTabs.TabItems[0];
            }
            finally { _suppressTabActivation = false; }

            if (TitleTabs.TabItems.Count == 0)
            {
                if (automatic)
                {
                    _autoClosedLastTab = true;
                    SelectNavigationItem("Home");
                }
                else
                {
                    var page = _shellState.GlobalPage;
                    await OpenLocalTerminalTabAsync();
                    if (preserveGlobalPage || preserveMultiView) SelectNavigationItem(page.ToString());
                }
            }

            SwitchSelectedSession();
            UpdateSessionArea();
            QueueWorkspaceSnapshot();

            if (tab.DataContext is SessionView view)
            {
                view.AppShortcutRequested -= TerminalView_AppShortcutRequested;
                if (_sessionWorkspaces.Remove(view, out var workspace))
                {
                    workspace.SectionChanged -= SessionWorkspace_SectionChanged;
                    workspace.TerminalActivationRequested -= SessionWorkspace_TerminalActivationRequested;
                    _navigation.ForgetWorkspace(workspace.ViewModel);
                    try { await DetachAndCloseSessionAsync(workspace, userInitiated: !automatic).WaitAsync(TimeSpan.FromSeconds(10)); }
                    catch (TimeoutException) { Debug.WriteLine("Session cleanup is continuing after tab close."); }
                }
                else await ObserveCloseOperationAsync(_sessions.CloseAsync(view.Session));
            }
            else if (tab.DataContext is LocalTerminalView localView)
            {
                localView.AppShortcutRequested -= TerminalView_AppShortcutRequested;
                ReleaseLocalFileBrowser(localView);
                await ObserveCloseOperationAsync(localView.CloseAsync());
            }
        }
        finally { _closingShellTabs.Remove(tab); }
    }
}
