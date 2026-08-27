using sutty.UI.ViewModels;
using System;

namespace sutty.UI.Services;

/// <summary>
/// Coordinates shell navigation without owning TabView items, session services, or views.
/// </summary>
public sealed class NavigationService
{
    private readonly AppShellViewModel _shell;

    public NavigationService(AppShellViewModel shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
    }

    public AppShellViewModel Shell => _shell;

    public void NavigateGlobal(AppGlobalPage page)
    {
        if (!Enum.IsDefined(page))
            throw new ArgumentOutOfRangeException(nameof(page));

        _shell.GlobalPage = page;
        _shell.Mode = AppShellMode.Global;
    }

    /// <summary>
    /// Shows a selected tab. Pass null for a local-terminal tab; SSH tabs pass their workspace.
    /// </summary>
    public void ActivateSession(SessionWorkspaceViewModel? workspace)
    {
        _shell.ActiveWorkspace = workspace;
        _shell.Mode = AppShellMode.Session;
    }

    public void NavigateWorkspace(
        SessionWorkspaceViewModel workspace,
        SessionWorkspaceSection section)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        workspace.SelectSection(section);
        ActivateSession(workspace);
    }

    /// <summary>
    /// Clears a closed SSH workspace only when it is still active. The caller remains
    /// responsible for selecting the replacement tab or navigating to a global page.
    /// </summary>
    public void ForgetWorkspace(SessionWorkspaceViewModel workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (ReferenceEquals(_shell.ActiveWorkspace, workspace))
            _shell.ActiveWorkspace = null;
    }

    public void SetDetailsPaneOpen(bool isOpen) => _shell.IsDetailsPaneOpen = isOpen;

    /// <summary>Stable Alt+number mapping; it never depends on visual menu order.</summary>
    public static bool TryGetGlobalPageForAccelerator(
        int number,
        out AppGlobalPage page)
    {
        page = number switch
        {
            1 => AppGlobalPage.Home,
            2 => AppGlobalPage.Hosts,
            3 => AppGlobalPage.Transfers,
            4 => AppGlobalPage.Commands,
            5 => AppGlobalPage.Settings,
            _ => default,
        };
        return number is >= 1 and <= 5;
    }

    public static int GetAcceleratorNumber(AppGlobalPage page) => page switch
    {
        AppGlobalPage.Home => 1,
        AppGlobalPage.Hosts => 2,
        AppGlobalPage.Transfers => 3,
        AppGlobalPage.Commands => 4,
        AppGlobalPage.Settings => 5,
        _ => throw new ArgumentOutOfRangeException(nameof(page)),
    };

    /// <summary>Stable selected-SSH-workspace destinations for Alt+6 and Alt+7.</summary>
    public static bool TryGetSessionSectionForAccelerator(
        int number,
        out SessionWorkspaceSection section)
    {
        section = number switch
        {
            6 => SessionWorkspaceSection.Terminal,
            7 => SessionWorkspaceSection.Files,
            _ => default,
        };
        return number is 6 or 7;
    }
}
