using CommunityToolkit.Mvvm.ComponentModel;

namespace sutty.UI.ViewModels;

/// <summary>Navigation focus; only full-page settings cover the selected shell.</summary>
public enum AppShellMode
{
    Global,
    Session,
}

/// <summary>Top-level destinations that do not belong to one SSH session.</summary>
public enum AppGlobalPage
{
    Home,
    Hosts,
    Transfers,
    Commands,
    MultiCommand,
    Settings,
}

/// <summary>
/// Observable, view-independent state for the main shell. UI elements and session services
/// remain owned by their existing views and coordinators.
/// </summary>
public sealed class AppShellViewModel : ObservableObject
{
    private AppShellMode _mode = AppShellMode.Global;
    private AppGlobalPage _globalPage = AppGlobalPage.Home;
    private SessionWorkspaceViewModel? _activeWorkspace;
    private bool _isDetailsPaneOpen;

    public AppShellMode Mode
    {
        get => _mode;
        internal set
        {
            if (!SetProperty(ref _mode, value))
                return;

            OnPropertyChanged(nameof(IsGlobalContentVisible));
            OnPropertyChanged(nameof(IsSessionContentVisible));
            OnPropertyChanged(nameof(IsFullPageVisible));
        }
    }

    public AppGlobalPage GlobalPage
    {
        get => _globalPage;
        internal set
        {
            if (!SetProperty(ref _globalPage, value))
                return;
            OnPropertyChanged(nameof(IsSessionContentVisible));
            OnPropertyChanged(nameof(IsFullPageVisible));
        }
    }

    /// <summary>
    /// The active SSH workspace, when the selected tab represents SSH. A local terminal can
    /// use <see cref="AppShellMode.Session"/> with this property left null.
    /// </summary>
    public SessionWorkspaceViewModel? ActiveWorkspace
    {
        get => _activeWorkspace;
        internal set
        {
            if (!SetProperty(ref _activeWorkspace, value))
                return;

            OnPropertyChanged(nameof(HasActiveWorkspace));
        }
    }

    public bool IsDetailsPaneOpen
    {
        get => _isDetailsPaneOpen;
        internal set => SetProperty(ref _isDetailsPaneOpen, value);
    }

    public bool IsGlobalContentVisible => Mode == AppShellMode.Global;

    public bool IsFullPageVisible => Mode == AppShellMode.Global && GlobalPage == AppGlobalPage.Settings;

    public bool IsSessionContentVisible => !IsFullPageVisible;

    public bool HasActiveWorkspace => ActiveWorkspace is not null;
}
