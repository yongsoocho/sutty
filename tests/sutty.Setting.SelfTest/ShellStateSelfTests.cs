using sutty.UI.Services;
using sutty.UI.ViewModels;

internal static class ShellStateSelfTests
{
    public static void Run()
    {
        InitialStateIsGlobalHome();
        AcceleratorMapIsStable();
        PersistedTerminalModeMapsToWorkspaceSection();
        ConnectionPersistenceRequiresSuccessAndConsent();
        SessionSectionsFollowTheActiveWorkspace();
        SwitchingTabsPreservesOpenTools();
        InvalidEnumValuesAreRejected();
        ForgettingOnlyClearsTheActiveWorkspace();
        Console.WriteLine("Shell state and navigation self-tests passed.");
    }

    private static void ConnectionPersistenceRequiresSuccessAndConsent()
    {
        Assert(ConnectionPersistencePolicy.ShouldOfferSave(
                ConnectionAttemptOutcome.Success,
                saveAlreadyRequested: false,
                savedHostId: null),
            "successful one-off connection offers Saved Host persistence");
        Assert(!ConnectionPersistencePolicy.ShouldOfferSave(
                ConnectionAttemptOutcome.Failed,
                saveAlreadyRequested: false,
                savedHostId: null) &&
               !ConnectionPersistencePolicy.ShouldOfferSave(
                ConnectionAttemptOutcome.Cancelled,
                saveAlreadyRequested: false,
                savedHostId: null),
            "failed and cancelled connections never offer persistence");
        Assert(!ConnectionPersistencePolicy.ShouldOfferSave(
                ConnectionAttemptOutcome.Success,
                saveAlreadyRequested: true,
                savedHostId: null) &&
               !ConnectionPersistencePolicy.ShouldOfferSave(
                ConnectionAttemptOutcome.Success,
                saveAlreadyRequested: false,
                savedHostId: "saved-host"),
            "existing or pre-requested profiles do not prompt twice");

        Assert(ConnectionPersistencePolicy.ShouldPersistProfile(
                ConnectionAttemptOutcome.Success,
                saveRequested: true),
            "successful opted-in connection can persist");
        Assert(!ConnectionPersistencePolicy.ShouldPersistProfile(
                ConnectionAttemptOutcome.Success,
                saveRequested: false) &&
               !ConnectionPersistencePolicy.ShouldPersistProfile(
                ConnectionAttemptOutcome.Failed,
                saveRequested: true) &&
               !ConnectionPersistencePolicy.ShouldPersistProfile(
                ConnectionAttemptOutcome.Cancelled,
                saveRequested: true),
            "declined, failed, and cancelled connections cannot persist");
    }

    private static void PersistedTerminalModeMapsToWorkspaceSection()
    {
        Assert(SessionWorkspaceViewModel.ResolveInitialSection("Terminal") ==
               SessionWorkspaceSection.Terminal,
            "Terminal setting opens Terminal workspace");
        Assert(SessionWorkspaceViewModel.ResolveInitialSection("Repl") ==
               SessionWorkspaceSection.Terminal,
            "legacy Repl setting keeps the initial shell visible");
        Assert(SessionWorkspaceViewModel.ResolveInitialSection(null) ==
               SessionWorkspaceSection.Terminal,
            "missing terminal setting uses Terminal workspace");

        var commands = new SessionWorkspaceViewModel(
            Guid.NewGuid(),
            "Commands first",
            "operator",
            "example.test",
            22,
            SessionWorkspaceSection.Commands);
        Assert(commands.CurrentSection == SessionWorkspaceSection.Commands &&
               commands.IsCommandsSelected,
            "workspace constructor preserves initial section");
    }

    private static void InitialStateIsGlobalHome()
    {
        var shell = new AppShellViewModel();

        Assert(shell.Mode == AppShellMode.Global, "shell initial mode");
        Assert(shell.GlobalPage == AppGlobalPage.Home, "shell initial Home page");
        Assert(shell.IsGlobalContentVisible && shell.IsSessionContentVisible,
            "Home opens beside the visible shell");
        Assert(!shell.HasActiveWorkspace && shell.ActiveWorkspace is null,
            "shell initial workspace");
        Assert(!shell.IsDetailsPaneOpen, "shell initial details pane");
    }

    private static void AcceleratorMapIsStable()
    {
        var expected = new[]
        {
            AppGlobalPage.Home,
            AppGlobalPage.Hosts,
            AppGlobalPage.Transfers,
            AppGlobalPage.Commands,
            AppGlobalPage.Settings,
        };

        for (var index = 0; index < expected.Length; index++)
        {
            var number = index + 1;
            Assert(NavigationService.TryGetGlobalPageForAccelerator(number, out var page),
                $"Alt+{number} is mapped");
            Assert(page == expected[index], $"Alt+{number} destination");
            Assert(NavigationService.GetAcceleratorNumber(page) == number,
                $"Alt+{number} reverse mapping");
        }

        Assert(!NavigationService.TryGetGlobalPageForAccelerator(0, out _),
            "Alt+0 is not a global destination");
        Assert(!NavigationService.TryGetGlobalPageForAccelerator(6, out _),
            "Alt+6 is not a global destination");
        Assert(NavigationService.TryGetGlobalPageForAccelerator(8, out var multi) &&
               multi == AppGlobalPage.MultiCommand &&
               NavigationService.GetAcceleratorNumber(multi) == 8,
            "Alt+8 opens independent Multi Command without changing existing shortcuts");

        Assert(NavigationService.TryGetSessionSectionForAccelerator(6, out var terminal) &&
               terminal == SessionWorkspaceSection.Terminal,
            "Alt+6 maps to the selected SSH Terminal");
        Assert(NavigationService.TryGetSessionSectionForAccelerator(7, out var files) &&
               files == SessionWorkspaceSection.Files,
            "Alt+7 maps to the selected SSH Files");
        Assert(!NavigationService.TryGetSessionSectionForAccelerator(5, out _),
            "Alt+5 is not a session destination");
        Assert(!NavigationService.TryGetSessionSectionForAccelerator(8, out _),
            "Alt+8 has no shell destination");
    }

    private static void SessionSectionsFollowTheActiveWorkspace()
    {
        var shell = new AppShellViewModel();
        var navigation = new NavigationService(shell);
        var workspace = CreateWorkspace();

        Assert(workspace.CurrentSection == SessionWorkspaceSection.Terminal &&
               workspace.IsTerminalSelected,
            "session workspace starts in Terminal");
        Assert(workspace.ConnectionIdentity == "operator@example.test:2222",
            "session workspace credential-free identity");

        navigation.ActivateSession(workspace);
        Assert(shell.Mode == AppShellMode.Session &&
               shell.IsSessionContentVisible &&
               !shell.IsGlobalContentVisible &&
               ReferenceEquals(shell.ActiveWorkspace, workspace),
            "activate SSH workspace");

        foreach (var page in Enum.GetValues<AppGlobalPage>())
        {
            navigation.NavigateGlobal(page);
            Assert(shell.IsSessionContentVisible == (page != AppGlobalPage.Settings) &&
                   shell.IsFullPageVisible == (page == AppGlobalPage.Settings) && shell.IsGlobalContentVisible &&
                   ReferenceEquals(shell.ActiveWorkspace, workspace),
                $"{page} preserves the active session and only Settings covers the shell");
        }

        navigation.NavigateWorkspace(workspace, SessionWorkspaceSection.Files);
        Assert(workspace.CurrentSection == SessionWorkspaceSection.Files && workspace.IsFilesSelected,
            "navigate to Files");
        navigation.NavigateWorkspace(workspace, SessionWorkspaceSection.Commands);
        Assert(workspace.CurrentSection == SessionWorkspaceSection.Commands && workspace.IsCommandsSelected,
            "navigate to Commands");
        navigation.NavigateWorkspace(workspace, SessionWorkspaceSection.Tunnels);
        Assert(workspace.CurrentSection == SessionWorkspaceSection.Tunnels && workspace.IsTunnelsSelected,
            "navigate to Tunnels");
        navigation.NavigateWorkspace(workspace, SessionWorkspaceSection.Terminal);
        Assert(workspace.CurrentSection == SessionWorkspaceSection.Terminal && workspace.IsTerminalSelected,
            "return to Terminal");
    }

    private static void SwitchingTabsPreservesOpenTools()
    {
        var shell = new AppShellViewModel();
        var navigation = new NavigationService(shell);
        var first = CreateWorkspace();
        var second = CreateWorkspace();
        navigation.ActivateSession(first);
        foreach (var page in Enum.GetValues<AppGlobalPage>())
        {
            navigation.NavigateGlobal(page);
            navigation.SetDetailsPaneOpen(page != AppGlobalPage.Settings);
            navigation.SwitchSession(second);
            Assert(ReferenceEquals(shell.ActiveWorkspace, second) &&
                   shell.Mode == AppShellMode.Global && shell.GlobalPage == page &&
                   shell.IsDetailsPaneOpen == (page != AppGlobalPage.Settings),
                $"SSH tab switch preserves {page} and its pane");
            navigation.SwitchSession(null);
            Assert(shell.ActiveWorkspace is null && shell.GlobalPage == page &&
                   shell.Mode == AppShellMode.Global &&
                   shell.IsFullPageVisible == (page == AppGlobalPage.Settings),
                $"local tab switch preserves {page}");
        }

        foreach (var section in new[] { SessionWorkspaceSection.Files,
                     SessionWorkspaceSection.Commands, SessionWorkspaceSection.Tunnels })
        {
            navigation.NavigateWorkspace(first, section);
            navigation.SwitchSession(second);
            Assert(second.CurrentSection == section && shell.Mode == AppShellMode.Session,
                $"SSH tool {section} follows the selected SSH tab");
            navigation.SwitchSession(null);
            Assert(navigation.SessionSection == section,
                $"local tab retains {section} intent");
            navigation.SwitchSession(first);
            Assert(first.CurrentSection == section, $"returning to SSH restores {section}");
        }
        navigation.ActivateSession(null);
        Assert(navigation.SessionSection == SessionWorkspaceSection.Terminal,
            "explicit return to local shell closes the supporting tool");
    }

    private static void InvalidEnumValuesAreRejected()
    {
        var shell = new AppShellViewModel();
        var navigation = new NavigationService(shell);
        var workspace = CreateWorkspace();

        AssertThrows<ArgumentOutOfRangeException>(
            () => navigation.NavigateGlobal((AppGlobalPage)999),
            "invalid global destination rejection");
        AssertThrows<ArgumentOutOfRangeException>(
            () => workspace.CurrentSection = (SessionWorkspaceSection)999,
            "invalid session section rejection");
        AssertThrows<ArgumentOutOfRangeException>(
            () => NavigationService.GetAcceleratorNumber((AppGlobalPage)999),
            "invalid accelerator destination rejection");
    }

    private static void ForgettingOnlyClearsTheActiveWorkspace()
    {
        var shell = new AppShellViewModel();
        var navigation = new NavigationService(shell);
        var active = CreateWorkspace();
        var other = new SessionWorkspaceViewModel(
            Guid.NewGuid(), "Other", "root", "other.example.test", 22);

        navigation.ActivateSession(active);
        navigation.ForgetWorkspace(other);
        Assert(ReferenceEquals(shell.ActiveWorkspace, active),
            "forgetting inactive workspace preserves active workspace");

        navigation.ForgetWorkspace(active);
        Assert(shell.ActiveWorkspace is null && !shell.HasActiveWorkspace,
            "forgetting active workspace clears it");
        Assert(shell.Mode == AppShellMode.Session,
            "caller chooses replacement after active workspace closes");
    }

    private static SessionWorkspaceViewModel CreateWorkspace() => new(
        Guid.NewGuid(),
        "Example",
        "operator",
        "example.test",
        2222);

    private static void AssertThrows<TException>(Action action, string name)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Self-test failed: {name}.");
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition)
            throw new InvalidOperationException($"Self-test failed: {name}.");
    }
}
