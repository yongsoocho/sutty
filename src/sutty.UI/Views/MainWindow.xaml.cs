using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using sutty.Core.Diagnostics;
using sutty.Core.Models;
using sutty.Core.Security;
using sutty.Core.Routing;
using sutty.Core.Sessions;
using sutty.Core.Sftp;
using sutty.Core.Terminal;
using sutty.Setting;
using sutty.UI.Controls;
using sutty.UI.Services;
using sutty.UI.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics;

namespace sutty.UI.Views
{
    public sealed partial class MainWindow : Window
    {
        public sutty.UI.ViewModels.MainViewModel ViewModel { get; }

        /// <summary>동시에 열 수 있는 로컬/SSH 작업 탭 최대 개수.</summary>
        private const int MaxSessions = 16;
        private static readonly TimeSpan BroadcastCommandTimeout = TimeSpan.FromSeconds(60);

        private readonly SessionManager _sessions = new();
        private readonly AppShellViewModel _shellState = new();
        private readonly NavigationService _navigation;
        private readonly Dictionary<AppGlobalPage, FrameworkElement> _globalPages = [];
        private readonly Dictionary<SessionView, SessionWorkspaceView> _sessionWorkspaces = [];
        private readonly List<HostListPanel> _hostPanels = [];
        private readonly SemaphoreSlim _hostKeyPromptGate = new(1, 1);
        private SupportBundleContext? _lastFailedSupportContext;
        private DateTimeOffset _lastFailedSupportOccurredUtc = DateTimeOffset.MinValue;
        private long _lastFailedSupportSequence;
        private HomeDashboardPanel? _homeDashboard;
        private SettingsPanel? _embeddedSettings;
        private MultiCommandPanel? _multiCommandPanel;
        private double _detailsPaneWidth = 316;
        private string _appIconPath = "";
        private bool _isMultiView;
        private int _broadcastInProgress;
        private readonly MultiSftpTransferCoordinator _multiSftpCoordinator = new();
        private readonly SftpTransferQueueStore _sftpTransferQueue = SftpTransferQueueStore.Default;
        private MultiSftpBatchResult? _lastMultiSftpBatch;
        private MultiSftpOperation _lastMultiSftpOperation;
        private string? _lastMultiSftpSourcePath;
        private string? _lastMultiSftpDestinationPath;
        private string? _lastMultiSftpQueueJobId;
        private SftpTransferOptions? _lastMultiSftpOptions;
        private int _multiSftpInProgress;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _workspaceSaveTimer;
        private readonly sutty.Command.SuttyLaunchRequest _launchRequest;
        private bool _startupWorkspaceHandled;
        private bool _restoringWorkspace;
        private bool _windowClosing;
        private bool _suppressWorkspacePersistence;
        private bool _suppressTabActivation;

        private enum MultiSftpOperation
        {
            None,
            Upload,
            Download,
        }

        public MainWindow()
            : this(sutty.Command.SuttyLaunchRequest.Default)
        {
        }

        public MainWindow(sutty.Command.SuttyLaunchRequest? launchRequest)
        {
            _launchRequest = launchRequest ?? sutty.Command.SuttyLaunchRequest.Default;
            _navigation = new NavigationService(_shellState);
            ViewModel = new sutty.UI.ViewModels.MainViewModel();
            InitializeComponent();


            ExtendsContentIntoTitleBar = true;
            SetTitleBar(TitleBarDragRegion);

            System.IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WindowId windowId =
                Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow appWindow =
                AppWindow.GetFromWindowId(windowId);
            _appIconPath = System.IO.Path.Combine(
                System.AppContext.BaseDirectory, "Assets", "sutty.ico");
            appWindow.SetIcon(_appIconPath);

            // sutty를 닫으면 설정 창도 같이 닫고, 저장 안 된 패널 폭이 있으면 마저 저장
            Closed += (_, _) =>
            {
                _windowClosing = true;
                FlushWorkspaceSnapshot();
                FlushRightPanelWidth();
                foreach (var localView in GetOpenLocalTerminalViews())
                    _ = localView.CloseAsync();
                LocalCredentialVault.Default.Dispose();
            };

            ApplyTheme(SettingsService.Current.Theme);

            // 저장된 창 크기 복원 + 리사이즈 시 디바운스 저장
            Helpers.WindowSizePersistence.Attach(this,
                s => new SizeInt32(s.MainWindowWidth, s.MainWindowHeight),
                (s, size) => { s.MainWindowWidth = size.Width; s.MainWindowHeight = size.Height; });

            RestoreRightPanelWidth();
            HideDetailsPane();
            InitializeWorkspacePersistence();
            Root.Loaded += Root_Loaded;

            LeftNav.SelectedItem = LeftNav.MenuItems[0];
        }

        private void InitializeWorkspacePersistence()
        {
            _workspaceSaveTimer = DispatcherQueue.CreateTimer();
            _workspaceSaveTimer.Interval = TimeSpan.FromMilliseconds(500);
            _workspaceSaveTimer.IsRepeating = false;
            _workspaceSaveTimer.Tick += (_, _) => SaveWorkspaceSnapshotNow();
        }

        private async void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_startupWorkspaceHandled)
                return;

            _startupWorkspaceHandled = true;
            Root.Loaded -= Root_Loaded;
            try
            {
                if (_launchRequest.Action == sutty.Command.SuttyLaunchAction.Default)
                {
                    await RestoreWorkspaceAsync();
                }
                else
                {
                    // An explicit process launch must not replace the normal window's
                    // persisted workspace when multiple Sutty instances are open.
                    _suppressWorkspacePersistence = true;
                    await HandleLaunchRequestAsync(_launchRequest);
                }
            }
            catch (Exception error)
            {
                _restoringWorkspace = false;
                Debug.WriteLine($"Workspace restore failed: {error.GetType().Name}");
            }
        }

        private void QueueWorkspaceSnapshot()
        {
            if (_restoringWorkspace || _windowClosing || _suppressWorkspacePersistence ||
                !SettingsService.Current.RestoreWorkspaceOnStartup ||
                _workspaceSaveTimer is null)
            {
                return;
            }

            _workspaceSaveTimer.Stop();
            _workspaceSaveTimer.Start();
        }

        private void FlushWorkspaceSnapshot()
        {
            _workspaceSaveTimer?.Stop();
            if (_suppressWorkspacePersistence)
                return;
            if (SettingsService.Current.RestoreWorkspaceOnStartup)
                SaveWorkspaceSnapshotNow();
            else
                WorkspaceStateStore.Clear();
        }

        private void SaveWorkspaceSnapshotNow()
        {
            if (_restoringWorkspace || _suppressWorkspacePersistence ||
                !SettingsService.Current.RestoreWorkspaceOnStartup)
                return;

            var tabs = new List<WorkspaceTabState>(MaxSessions);
            var selectedIndex = -1;
            foreach (var item in TitleTabs.TabItems)
            {
                if (item is not TabViewItem tab)
                    continue;

                WorkspaceTabState? state = tab.DataContext switch
                {
                    LocalTerminalView => new WorkspaceTabState
                    {
                        Kind = WorkspaceTabKinds.LocalTerminal,
                    },
                    SessionView sessionView when
                        !string.IsNullOrWhiteSpace(sessionView.Session.Info.SavedHostId) =>
                        new WorkspaceTabState
                        {
                            Kind = WorkspaceTabKinds.SavedHost,
                            SavedHostId = sessionView.Session.Info.SavedHostId!,
                        },
                    _ => null,
                };
                if (state is null)
                    continue;

                if (ReferenceEquals(TitleTabs.SelectedItem, tab))
                    selectedIndex = tabs.Count;
                tabs.Add(state);
            }

            if (selectedIndex < 0 && tabs.Count > 0)
                selectedIndex = 0;
            var result = WorkspaceStateStore.Save(new WorkspaceSnapshot
            {
                Tabs = tabs,
                SelectedIndex = selectedIndex,
                SavedAtUtc = DateTimeOffset.UtcNow,
            });
            if (!result.Succeeded)
                Debug.WriteLine($"Workspace save failed: {result.Error?.GetType().Name}");
        }

        private async Task RestoreWorkspaceAsync()
        {
            var settings = SettingsService.Current;
            if (!settings.RestoreWorkspaceOnStartup)
                return;

            var snapshot = WorkspaceStateStore.Load();
            if (snapshot.Tabs.Count == 0)
                return;

            var localCount = snapshot.Tabs.Count(tab =>
                tab.Kind == WorkspaceTabKinds.LocalTerminal);
            var sshCount = snapshot.Tabs.Count - localCount;
            if (settings.ConfirmWorkspaceRestore)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = Content.XamlRoot,
                    Title = Helpers.Loc.T("이전 작업공간 복원", "Restore previous workspace"),
                    Content = Helpers.Loc.T(
                        $"로컬 탭 {localCount}개와 SSH 탭 {sshCount}개를 복원할까요?\n\nSSH 탭은 저장 Host로 다시 연결하지만 이전 명령은 재실행하지 않습니다.",
                        $"Restore {localCount} local tab(s) and {sshCount} SSH tab(s)?\n\nSSH tabs reconnect through Saved Hosts, but previous commands are never replayed."),
                    PrimaryButtonText = Helpers.Loc.T("복원", "Restore"),
                    CloseButtonText = Helpers.Loc.T("새로 시작", "Start fresh"),
                    DefaultButton = ContentDialogButton.Close,
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    WorkspaceStateStore.Clear();
                    return;
                }
            }

            _restoringWorkspace = true;
            try
            {
                var restoredSnapshotIndices = new List<int>(snapshot.Tabs.Count);
                var snapshotTabs = snapshot.Tabs.Take(MaxSessions).ToList();
                for (var snapshotIndex = 0; snapshotIndex < snapshotTabs.Count; snapshotIndex++)
                {
                    var tab = snapshotTabs[snapshotIndex];
                    var tabCountBeforeRestore = TitleTabs.TabItems.Count;
                    if (tab.Kind == WorkspaceTabKinds.LocalTerminal)
                    {
                        await OpenLocalTerminalTabAsync();
                    }
                    else if (tab.Kind == WorkspaceTabKinds.SavedHost &&
                             !string.IsNullOrWhiteSpace(tab.SavedHostId))
                    {
                        try
                        {
                            if (sutty.Command.HostProfileStore.GetById(tab.SavedHostId) is { } profile)
                                await OpenHistoryDraftAsync(CreateHostInfo(profile));
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                                      Microsoft.Data.Sqlite.SqliteException or ArgumentException)
                        {
                            Debug.WriteLine($"Workspace Saved Host restore failed: {error.GetType().Name}");
                        }
                    }

                    if (TitleTabs.TabItems.Count > tabCountBeforeRestore)
                        restoredSnapshotIndices.Add(snapshotIndex);
                }

                if (restoredSnapshotIndices.Count > 0)
                {
                    var selectedIndex = restoredSnapshotIndices.FindIndex(
                        index => index == snapshot.SelectedIndex);
                    if (selectedIndex < 0)
                    {
                        selectedIndex = restoredSnapshotIndices.FindLastIndex(
                            index => index < snapshot.SelectedIndex);
                    }
                    if (selectedIndex < 0)
                        selectedIndex = 0;
                    TitleTabs.SelectedItem = TitleTabs.TabItems[selectedIndex];
                }
            }
            finally
            {
                _restoringWorkspace = false;
                SaveWorkspaceSnapshotNow();
            }
        }

        private static ViewModels.HostInfoModel CreateHostInfo(sutty.Command.HostProfile profile) => new()
        {
            ProfileId = profile.Id,
            IsSavedProfile = true,
            CredentialId = profile.CredentialId,
            Alias = profile.DisplayName,
            Hostname = profile.Host,
            LastConnected = profile.LastConnectedAtUtc?.LocalDateTime,
            IsPinned = profile.IsFavorite,
            Username = profile.Username,
            Port = profile.Port,
            AuthMethod = profile.AuthMethod,
            PrivateKeyPath = profile.PrivateKeyPath,
            Tags = [.. profile.Tags],
            GroupName = profile.GroupName,
            Environment = profile.Environment,
            Route = profile.Route,
            Tunnels = [.. profile.Tunnels],
        };

        private async Task HandleLaunchRequestAsync(sutty.Command.SuttyLaunchRequest request)
        {
            if (request.Action == sutty.Command.SuttyLaunchAction.ShowVersion)
            {
                await ShowLaunchDialogAsync(
                    Helpers.Loc.T("Sutty 버전", "Sutty version"),
                    Helpers.AppReleaseInfo.DisplayVersion +
                    (string.IsNullOrWhiteSpace(Helpers.AppReleaseInfo.BuildMetadata)
                        ? ""
                        : $"\nBuild {Helpers.AppReleaseInfo.BuildMetadata}"));
                return;
            }

            if (request.Action == sutty.Command.SuttyLaunchAction.ShowHelp)
            {
                await ShowLaunchDialogAsync(
                    Helpers.Loc.T("Sutty 명령줄", "Sutty command line"),
                    Helpers.Loc.T(
                        "저장 Host 열기:\n\nsutty.UI.exe --host <저장 Host ID 또는 정확한 이름>\n\n버전 확인:\n\nsutty.UI.exe --version\n\n비밀번호와 개인키 암호는 명령줄 인자로 받지 않습니다.",
                        "Open a Saved Host:\n\nsutty.UI.exe --host <Saved Host ID or exact name>\n\nShow version:\n\nsutty.UI.exe --version\n\nPasswords and private-key passphrases are not accepted as command-line arguments."));
                return;
            }

            if (request.Action == sutty.Command.SuttyLaunchAction.Invalid)
            {
                await ShowLaunchDialogAsync(
                    Helpers.Loc.T("명령줄 확인", "Check command line"),
                    Helpers.Loc.T(
                        "지원하지 않는 실행 인자입니다.\n\n사용법: sutty.UI.exe --host <저장 Host ID 또는 정확한 이름>",
                        "The launch arguments are unsupported.\n\nUsage: sutty.UI.exe --host <Saved Host ID or exact name>"));
                return;
            }

            if (request.Action != sutty.Command.SuttyLaunchAction.OpenSavedHost)
                return;

            try
            {
                var profile = ResolveLaunchProfile(request.SavedHostReference, out var ambiguous);
                if (profile is null)
                {
                    await ShowLaunchDialogAsync(
                        Helpers.Loc.T("저장 Host를 찾을 수 없음", "Saved Host not found"),
                        ambiguous
                            ? Helpers.Loc.T(
                                "같은 이름의 저장 Host가 여러 개입니다. 고유한 Host ID를 사용하세요.",
                                "More than one Saved Host has that name. Use its unique Host ID.")
                            : Helpers.Loc.T(
                                $"'{request.SavedHostReference}' 저장 Host가 없습니다.",
                                $"Saved Host '{request.SavedHostReference}' does not exist."));
                    return;
                }

                await OpenHistoryDraftAsync(CreateHostInfo(profile));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                          Microsoft.Data.Sqlite.SqliteException or ArgumentException)
            {
                Debug.WriteLine($"Command-line Saved Host open failed: {error.GetType().Name}");
                await ShowLaunchDialogAsync(
                    Helpers.Loc.T("저장 Host 열기 실패", "Could not open Saved Host"),
                    Helpers.Loc.T(
                        "저장 Host 데이터베이스를 읽을 수 없습니다.",
                        "The Saved Host database could not be read."));
            }
        }

        private static sutty.Command.HostProfile? ResolveLaunchProfile(
            string reference,
            out bool ambiguous)
        {
            ambiguous = false;
            if (reference.Length <= 128 && reference.All(character =>
                    char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            {
                if (sutty.Command.HostProfileStore.GetById(reference) is { } byId)
                    return byId;
            }

            var exactMatches = sutty.Command.HostProfileStore.GetAll(reference, 100)
                .Where(profile => string.Equals(
                    profile.DisplayName,
                    reference,
                    StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToList();
            ambiguous = exactMatches.Count > 1;
            return exactMatches.Count == 1 ? exactMatches[0] : null;
        }

        private async Task ShowLaunchDialogAsync(string title, string message)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = title,
                Content = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                },
                CloseButtonText = "OK",
                DefaultButton = ContentDialogButton.Close,
            };
            await dialog.ShowAsync();
        }

        // ── 오른쪽 패널 폭: 스플리터 드래그 → 디바운스 저장 ──

        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _panelWidthSaveTimer;

        private void RestoreRightPanelWidth()
        {
            var saved = SettingsService.Current.RightPanelWidth;
            if (saved > 0)
                _detailsPaneWidth = Math.Clamp(saved, 300, 800);

            _panelWidthSaveTimer = DispatcherQueue.CreateTimer();
            _panelWidthSaveTimer.Interval = TimeSpan.FromMilliseconds(600);
            _panelWidthSaveTimer.IsRepeating = false;
            _panelWidthSaveTimer.Tick += (_, _) =>
            {
                if (RightPanelHost.Visibility != Visibility.Visible)
                    return;
                SettingsService.Current.RightPanelWidth = (int)RightPanelColumn.ActualWidth;
                SettingsService.Save();
            };

            RightPanelHost.SizeChanged += (_, _) =>
            {
                _panelWidthSaveTimer.Stop();
                _panelWidthSaveTimer.Start(); // 드래그 중 이벤트 폭주 → 마지막만 저장
            };
        }

        // 창을 닫는 순간 디바운스 대기 중이던 폭을 놓치지 않게 즉시 저장
        private void FlushRightPanelWidth()
        {
            if (_panelWidthSaveTimer is { IsRunning: true } &&
                RightPanelHost.Visibility == Visibility.Visible)
            {
                _panelWidthSaveTimer.Stop();
                SettingsService.Current.RightPanelWidth = (int)RightPanelColumn.ActualWidth;
                SettingsService.Save();
            }
        }

        private void ShowDetailsPane()
        {
            RightPanelColumn.MinWidth = 300;
            RightPanelColumn.Width = new GridLength(Math.Clamp(_detailsPaneWidth, 300, 800));
            RightPanelHost.Visibility = Visibility.Visible;
            RightPanelSplitter.Visibility = Visibility.Visible;
            _navigation.SetDetailsPaneOpen(true);
        }

        private void HideDetailsPane()
        {
            if (RightPanelHost.Visibility == Visibility.Visible && RightPanelColumn.ActualWidth >= 300)
                _detailsPaneWidth = RightPanelColumn.ActualWidth;
            RightPanelHost.Visibility = Visibility.Collapsed;
            RightPanelSplitter.Visibility = Visibility.Collapsed;
            RightPanelColumn.MinWidth = 0;
            RightPanelColumn.Width = new GridLength(0);
            RightPanel.Content = null;
            _navigation.SetDetailsPaneOpen(false);
        }

        // ── 테마 (전환 UI는 Setting > Appearance에 있음) ──

        private void ApplyTheme(string theme)
        {
            Helpers.ThemeManager.Apply(theme, Root);
            var preset = Helpers.ThemeManager.Find(theme);
            ApplyTitleBarColors(this, preset);

        }

        private static void ApplyTitleBarColors(Window window, Helpers.ThemePreset preset)
        {
            var titleBar = window.AppWindow.TitleBar;
            var appBackground = ParseColor(preset.Colors["AppBg"]);
            var foreground = ParseColor(preset.Colors["TextPrimary"]);
            var inactiveForeground = ParseColor(preset.Colors["TextFaint"]);
            var hoverBackground = ParseColor(preset.Colors["CardBgHover"]);
            var pressedBackground = ParseColor(preset.Colors["PillBg"]);

            titleBar.BackgroundColor = appBackground;
            titleBar.ForegroundColor = foreground;
            titleBar.InactiveBackgroundColor = appBackground;
            titleBar.InactiveForegroundColor = inactiveForeground;
            titleBar.ButtonBackgroundColor = appBackground;
            titleBar.ButtonForegroundColor = foreground;
            titleBar.ButtonHoverBackgroundColor = hoverBackground;
            titleBar.ButtonHoverForegroundColor = foreground;
            titleBar.ButtonPressedBackgroundColor = pressedBackground;
            titleBar.ButtonPressedForegroundColor = foreground;
            titleBar.ButtonInactiveBackgroundColor = appBackground;
            titleBar.ButtonInactiveForegroundColor = inactiveForeground;
        }

        private static Windows.UI.Color ParseColor(string hex)
        {
            hex = hex.TrimStart('#');
            return Windows.UI.Color.FromArgb(
                255,
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16));
        }

        // ── 왼쪽 네비게이션 ──

        private void LeftNav_ItemInvoked(
            NavigationView sender,
            NavigationViewItemInvokedEventArgs args)
        {
            // SelectionChanged handles a new item; an already-selected item does not raise
            // it, so route that invocation explicitly as well.
            if (args.InvokedItemContainer is NavigationViewItem { Tag: string tag })
                SelectNavigationItem(tag);
        }

        private void LeftNav_SelectionChanged(
            NavigationView sender,
            NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is not NavigationViewItem { Tag: string tag })
                return;

            var page = tag switch
            {
                "Home" => AppGlobalPage.Home,
                "Hosts" => AppGlobalPage.Hosts,
                "Transfers" => AppGlobalPage.Transfers,
                "Commands" => AppGlobalPage.Commands,
                "Settings" => AppGlobalPage.Settings,
                _ => (AppGlobalPage?)null,
            };
            if (page is { } destination)
                NavigateGlobal(destination);
        }

        private void NavigateGlobal(AppGlobalPage page)
        {
            if (page != AppGlobalPage.Home)
                ClearCachedHomeSecrets();
            _navigation.NavigateGlobal(page);
            _isMultiView = false;
            MultiGrid.Visibility = Visibility.Collapsed;
            HideDetailsPane();
            GlobalPageHost.Content = GetGlobalPage(page);
            UpdateSessionArea();
        }

        private FrameworkElement GetGlobalPage(AppGlobalPage page)
        {
            if (_globalPages.TryGetValue(page, out var cached))
            {
                if (cached is Border { Child: TransferCenterPanel transfers })
                    transfers.RefreshFromStore();
                if (page == AppGlobalPage.Home)
                    _homeDashboard?.RefreshHosts();
                if (page == AppGlobalPage.Hosts)
                {
                    foreach (var hosts in _hostPanels)
                        hosts.RefreshFromStore();
                }
                return cached;
            }

            var created = page switch
            {
                AppGlobalPage.Home => CreateHomeDashboard(),
                AppGlobalPage.Hosts => WrapGlobalPage(CreateHostListPanel()),
                AppGlobalPage.Transfers => WrapGlobalPage(new TransferCenterPanel()),
                AppGlobalPage.Commands => CreateCommandsDashboard(),
                AppGlobalPage.Settings => CreateEmbeddedSettingsPanel(),
                _ => throw new ArgumentOutOfRangeException(nameof(page)),
            };
            _globalPages[page] = created;
            return created;
        }

        private HomeDashboardPanel CreateHomeDashboard()
        {
            var dashboard = new HomeDashboardPanel
            {
                OwnerWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this),
            };
            dashboard.ConnectRequested += async (_, info) => await OpenSessionTabAsync(info);
            dashboard.HistoryConnectRequested += async (_, host) => await OpenHistoryDraftAsync(host);
            _homeDashboard = dashboard;
            return dashboard;
        }

        private CommandsDashboardPanel CreateCommandsDashboard()
        {
            var dashboard = new CommandsDashboardPanel();
            dashboard.RunRequested += async (_, command) => await RunCommandOnActiveSessionAsync(command);
            dashboard.PowerToolsRequested += (_, _) => OpenMultiPowerTools();
            dashboard.SetPowerToolsAvailable(GetOpenTerminalViews().Count >= 2);
            return dashboard;
        }

        private SettingsPanel CreateEmbeddedSettingsPanel()
        {
            if (_embeddedSettings is not null)
                return _embeddedSettings;

            var panel = new SettingsPanel
            {
                OwnerWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this),
                SupportBundleTargetsProvider = CreateSupportBundleTargets,
            };
            panel.SettingsChanged += (_, args) => ApplySettingsChanges(args.Changes);
            panel.ThemeChanged += (_, themeName) => ApplyTheme(themeName);
            _embeddedSettings = panel;
            return panel;
        }

        private static FrameworkElement WrapGlobalPage(FrameworkElement content) => new Border
        {
            Padding = new Thickness(24),
            Child = content,
        };

        private void OpenMultiPowerTools()
        {
            if (GetOpenTerminalViews().Count < 2)
                return;
            ClearCachedHomeSecrets();
            _multiCommandPanel ??= CreateMultiPanel();
            RightPanel.Content = _multiCommandPanel;
            ShowDetailsPane();
            _isMultiView = true;
            MultiGrid.SetSessions(GetOpenTerminalViews());
            MultiGrid.Visibility = Visibility.Visible;
            UpdateSessionArea();
        }

        private HostListPanel CreateHostListPanel()
        {
            var panel = new HostListPanel();
            panel.ConnectRequested += async (_, host) => await OpenHistoryDraftAsync(host);
            _hostPanels.Add(panel);
            return panel;
        }

        private async Task OpenHistoryDraftAsync(ViewModels.HostInfoModel host)
        {
            var alias = host.Alias;
            var hostname = host.Hostname;
            var username = host.Username;
            var port = host.Port;
            var authMethodName = host.AuthMethod;
            var privateKeyPath = host.PrivateKeyPath;
            var tags = host.Tags;
            var profileId = host.ProfileId;
            var credentialId = host.CredentialId;
            var groupName = host.GroupName;
            var environment = host.Environment;
            var favorite = host.IsPinned;
            var routeProfile = host.Route;
            var tunnelProfiles = host.Tunnels;
            CredentialSecret? credential = null;

            if (!string.IsNullOrWhiteSpace(profileId))
            {
                try
                {
                    if (sutty.Command.HostProfileStore.GetById(profileId) is { } profile)
                    {
                        alias = profile.DisplayName;
                        hostname = profile.Host;
                        username = profile.Username;
                        port = profile.Port;
                        authMethodName = profile.AuthMethod;
                        privateKeyPath = profile.PrivateKeyPath;
                        tags = profile.Tags;
                        credentialId = profile.CredentialId;
                        groupName = profile.GroupName;
                        environment = profile.Environment;
                        favorite = profile.IsFavorite;
                        routeProfile = profile.Route;
                        tunnelProfiles = profile.Tunnels;
                    }

                    if (!string.IsNullOrWhiteSpace(credentialId))
                        LocalCredentialVault.Default.TryRead(credentialId, out credential);
                }
                catch (Exception error) when (error is System.IO.IOException or
                                              UnauthorizedAccessException or
                                              System.Security.Cryptography.CryptographicException or
                                              System.ComponentModel.Win32Exception or
                                              Microsoft.Data.Sqlite.SqliteException or
                                              ArgumentException)
                {
                    Debug.WriteLine($"Saved credential load failed: {error.GetType().Name}");
                    var warning = new ContentDialog
                    {
                        Title = Helpers.Loc.T("저장 자격증명 확인 필요", "Saved credential needs attention"),
                        Content = Helpers.Loc.T(
                            "저장된 자격증명을 읽지 못했습니다. 호스트 정보는 불러왔으며 비밀값을 다시 입력할 수 있습니다.",
                            "The saved credential could not be read. The host details were loaded; enter the secret again."),
                        CloseButtonText = "OK",
                        XamlRoot = Content.XamlRoot,
                    };
                    await warning.ShowAsync();
                }
            }

            var authMethod = Enum.TryParse<SshAuthMethod>(authMethodName, true, out var parsed) &&
                             Enum.IsDefined(parsed)
                ? parsed
                : SshAuthMethod.Password;

            var draft = new SshConnectionInfo
            {
                Host = hostname,
                Port = port is >= 1 and <= 65535 ? port : 22,
                DisplayName = alias,
                Username = username,
                AuthMethod = authMethod,
                PrivateKeyPath = authMethod == SshAuthMethod.PublicKey ? privateKeyPath : "",
                Password = authMethod is SshAuthMethod.Password or SshAuthMethod.KeyboardInteractive
                    ? credential?.Password ?? ""
                    : "",
                Passphrase = authMethod == SshAuthMethod.PublicKey
                    ? credential?.PrivateKeyPassphrase ?? ""
                    : "",
                Tags = [.. tags],
                SavedHostId = profileId,
                SaveProfile = !string.IsNullOrWhiteSpace(profileId),
                RememberCredential = credential is not null,
                CredentialId = credentialId,
                GroupName = groupName,
                Environment = environment,
                IsFavorite = favorite,
                Route = RestoreRoute(routeProfile, credential),
                RoutePolicy = new ConnectionRoutePolicy
                {
                    DisableDirect = routeProfile.DisableDirect,
                },
                PortForwardings = tunnelProfiles.Select(RestoreTunnel).ToList(),
            };

            if (!routeProfile.CanConnect)
            {
                await ShowSavedRouteIssueAsync(routeProfile, draft);
                return;
            }

            // History cards execute a connection instead of returning to the editor.
            // Secrets never live in SQLite, so request only a missing password in place.
            draft.SaveProfile = false;
            if (authMethod == SshAuthMethod.Password &&
                string.IsNullOrEmpty(draft.Password) &&
                !await PromptForHistoryPasswordAsync(draft))
            {
                return;
            }

            await OpenSessionTabAsync(draft);
        }

        private async Task ShowSavedRouteIssueAsync(
            sutty.Command.HostRouteProfile route,
            SshConnectionInfo draft)
        {
            var source = string.IsNullOrWhiteSpace(route.SourceType)
                ? ""
                : $" ({route.SourceType})";
            var message = route.State == sutty.Command.SavedRouteState.Unsupported
                ? Helpers.Loc.T(
                    $"이 저장 Host는 더 이상 지원하지 않는 연결 경로{source}를 사용합니다. " +
                    "Host를 편집하여 Direct, Proxy 또는 SSH Jump 경로를 다시 선택하세요.",
                    $"This Saved Host uses a connection route{source} that is no longer supported. " +
                    "Edit the host and select Direct, Proxy, or SSH Jump again.")
                : Helpers.Loc.T(
                    "이 저장 Host의 연결 경로 정보가 손상되었습니다. " +
                    "Host를 편집하여 Direct, Proxy 또는 SSH Jump 경로를 다시 선택하세요.",
                    "This Saved Host has corrupt connection-route data. " +
                    "Edit the host and select Direct, Proxy, or SSH Jump again.");
            var dialog = new ContentDialog
            {
                Title = Helpers.Loc.T("저장된 연결 경로 확인", "Saved route needs attention"),
                Content = $"{message}\n\n{Helpers.Loc.T("오류 코드", "Error code")}: {route.ErrorCode}",
                PrimaryButtonText = Helpers.Loc.T("Host 편집", "Edit host"),
                CloseButtonText = Helpers.Loc.T("취소", "Cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            // Preserve the fail-closed choice in the editor. The user must either choose
            // a supported indirect route or deliberately turn Strict route off for Direct.
            draft.Route = new ConnectionRoute();
            draft.RoutePolicy = new ConnectionRoutePolicy { DisableDirect = true };
            draft.SaveProfile = !string.IsNullOrWhiteSpace(draft.SavedHostId);
            SelectNavigationItem("Home");
            _homeDashboard?.ApplyConnectionDraft(draft);
        }

        private async Task<bool> PromptForHistoryPasswordAsync(SshConnectionInfo draft)
        {
            var passwordBox = new PasswordBox
            {
                Header = Helpers.Loc.T("비밀번호", "Password"),
                PlaceholderText = Helpers.Loc.T("SSH 비밀번호 입력", "Enter SSH password"),
                MinWidth = 320,
            };
            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(new TextBlock
            {
                Text = $"{draft.Username}@{draft.Host}:{draft.Port}",
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                Foreground = Helpers.ThemeResources.Brush(Root, "TextMuted"),
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(passwordBox);

            var dialog = new ContentDialog
            {
                Title = Helpers.Loc.T("접속 기록에서 연결", "Connect from history"),
                Content = content,
                PrimaryButtonText = Helpers.Loc.T("연결", "Connect"),
                CloseButtonText = Helpers.Loc.T("취소", "Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = false,
                XamlRoot = Content.XamlRoot,
            };
            passwordBox.PasswordChanged += (_, _) =>
                dialog.IsPrimaryButtonEnabled = passwordBox.Password.Length > 0;
            dialog.Opened += (_, _) => passwordBox.Focus(FocusState.Programmatic);

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return false;

            draft.Password = passwordBox.Password;
            return draft.Password.Length > 0;
        }

        private MultiCommandPanel CreateMultiPanel()
        {
            var panel = new MultiCommandPanel
            {
                OwnerWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this),
            };
            panel.BroadcastRequested += async (_, command) =>
                await BroadcastFromPanelAsync(panel, command);
            panel.SftpUploadRequested += async (_, request) =>
                await UploadToSelectedSessionsAsync(panel, request);
            panel.SftpDownloadRequested += async (_, request) =>
                await DownloadFromSelectedSessionsAsync(panel, request);
            panel.SftpRetryFailedRequested += async (_, _) =>
                await RetryFailedSftpTargetsAsync(panel);
            panel.SftpResumePendingRequested += async (_, _) =>
                await ResumePendingSftpTransferAsync(panel);
            RefreshRecoveredSftpCount(panel);
            return panel;
        }

        private async Task UploadToSelectedSessionsAsync(
            MultiCommandPanel panel,
            MultiSftpUploadRequest request)
        {
            if (Interlocked.CompareExchange(ref _multiSftpInProgress, 1, 0) != 0)
            {
                panel.ShowSftpStatus(
                    "이미 다중 SFTP 전송이 실행 중입니다.",
                    "A multi-server SFTP transfer is already running.");
                return;
            }

            panel.ResetSftpTargets();
            panel.SetSftpRunning(true, Helpers.Loc.T(
                "선택한 서버별로 업로드를 준비합니다…",
                "Preparing the upload for each selected server…"));
            try
            {
                var (targets, selectedCount) = GetSelectedSftpTargets(request.RemoteDirectory);
                if (targets.Length == 0)
                {
                    panel.ShowSftpStatus(
                        "SFTP를 사용할 수 있는 체크된 SSH 세션이 없습니다.",
                        "No checked SSH session has an available SFTP subsystem.");
                    return;
                }

                var skipped = selectedCount - targets.Length;
                if (skipped > 0)
                {
                    panel.ShowSftpStatus(
                        $"SFTP 사용 불가 또는 로컬 대상 {skipped}개를 제외하고 전송합니다.",
                        $"Uploading after excluding {skipped} local or SFTP-unavailable target(s).");
                }

                var options = await ConfirmMultiSftpTransferAsync(
                    targets,
                    sutty.Core.Sftp.SftpTransferDirection.Upload,
                    request.LocalPath,
                    request.RemoteDirectory);
                if (options is null)
                {
                    panel.ShowSftpStatus(
                        "다중 SFTP 업로드를 취소했습니다.",
                        "Multi-server SFTP upload was cancelled.");
                    return;
                }
                var queueJob = CreateQueuedJob(
                    SftpQueueMode.FanOut,
                    sutty.Core.Sftp.SftpTransferDirection.Upload,
                    request.LocalPath,
                    request.RemoteDirectory,
                    targets,
                    options);
                _sftpTransferQueue.Upsert(queueJob);
                _lastMultiSftpOperation = MultiSftpOperation.Upload;
                _lastMultiSftpSourcePath = request.LocalPath;
                _lastMultiSftpDestinationPath = request.RemoteDirectory;
                _lastMultiSftpQueueJobId = queueJob.Id;
                _lastMultiSftpOptions = options;
                var progress = CreateMultiSftpProgress(panel, queueJob.Id);
                _lastMultiSftpBatch = await _multiSftpCoordinator.UploadAsync(
                    request.LocalPath,
                    targets,
                    options,
                    progress);
                PersistMultiSftpBatch(queueJob.Id, _lastMultiSftpBatch);
                panel.CompleteSftpBatch(_lastMultiSftpBatch);
            }
            catch (Exception error)
            {
                Debug.WriteLine($"Multi-server SFTP upload failed: {error}");
                panel.ShowSftpStatus(
                    $"다중 SFTP 업로드를 시작하지 못했습니다: {error.Message}",
                    $"Could not start the multi-server SFTP upload: {error.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _multiSftpInProgress, 0);
                panel.SetSftpRunning(false);
                RefreshRecoveredSftpCount(panel);
            }
        }

        private async Task DownloadFromSelectedSessionsAsync(
            MultiCommandPanel panel,
            MultiSftpDownloadRequest request)
        {
            if (Interlocked.CompareExchange(ref _multiSftpInProgress, 1, 0) != 0)
            {
                panel.ShowSftpStatus(
                    "이미 다중 SFTP 전송이 실행 중입니다.",
                    "A multi-server SFTP transfer is already running.");
                return;
            }

            panel.ResetSftpTargets();
            panel.SetSftpRunning(true, Helpers.Loc.T(
                "선택한 서버들의 다운로드를 준비합니다…",
                "Preparing downloads from the selected servers…"));
            try
            {
                var (sources, selectedCount) = GetSelectedSftpTargets(request.RemotePath);
                if (sources.Length == 0)
                {
                    panel.ShowSftpStatus(
                        "SFTP를 사용할 수 있는 체크된 SSH 세션이 없습니다.",
                        "No checked SSH session has an available SFTP subsystem.");
                    return;
                }

                var skipped = selectedCount - sources.Length;
                if (skipped > 0)
                {
                    panel.ShowSftpStatus(
                        $"SFTP 사용 불가·로컬·중복 대상 {skipped}개를 제외합니다.",
                        $"Excluding {skipped} local, duplicate, or SFTP-unavailable target(s).");
                }

                var options = await ConfirmMultiSftpTransferAsync(
                    sources,
                    sutty.Core.Sftp.SftpTransferDirection.Download,
                    request.RemotePath,
                    request.LocalDirectory);
                if (options is null)
                {
                    panel.ShowSftpStatus(
                        "다중 SFTP 다운로드를 취소했습니다.",
                        "Multi-server SFTP download was cancelled.");
                    return;
                }
                var queueJob = CreateQueuedJob(
                    SftpQueueMode.FanIn,
                    sutty.Core.Sftp.SftpTransferDirection.Download,
                    request.RemotePath,
                    request.LocalDirectory,
                    sources,
                    options);
                _sftpTransferQueue.Upsert(queueJob);
                _lastMultiSftpOperation = MultiSftpOperation.Download;
                _lastMultiSftpSourcePath = request.RemotePath;
                _lastMultiSftpDestinationPath = request.LocalDirectory;
                _lastMultiSftpQueueJobId = queueJob.Id;
                _lastMultiSftpOptions = options;
                var progress = CreateMultiSftpProgress(panel, queueJob.Id);
                _lastMultiSftpBatch = await _multiSftpCoordinator.DownloadAsync(
                    request.RemotePath,
                    request.LocalDirectory,
                    sources,
                    options,
                    progress);
                PersistMultiSftpBatch(queueJob.Id, _lastMultiSftpBatch);
                panel.CompleteSftpBatch(_lastMultiSftpBatch);
            }
            catch (Exception error)
            {
                Debug.WriteLine($"Multi-server SFTP download failed: {error}");
                panel.ShowSftpStatus(
                    $"다중 SFTP 다운로드를 시작하지 못했습니다: {error.Message}",
                    $"Could not start the multi-server SFTP download: {error.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _multiSftpInProgress, 0);
                panel.SetSftpRunning(false);
                RefreshRecoveredSftpCount(panel);
            }
        }

        private async Task RetryFailedSftpTargetsAsync(MultiCommandPanel panel)
        {
            if (_lastMultiSftpBatch is null ||
                _lastMultiSftpOperation == MultiSftpOperation.None ||
                string.IsNullOrWhiteSpace(_lastMultiSftpSourcePath) ||
                string.IsNullOrWhiteSpace(_lastMultiSftpDestinationPath))
            {
                panel.ShowSftpStatus(
                    "재시도할 이전 실패 기록이 없습니다.",
                    "There is no previous failed transfer to retry.");
                return;
            }
            if (Interlocked.CompareExchange(ref _multiSftpInProgress, 1, 0) != 0)
            {
                panel.ShowSftpStatus(
                    "이미 다중 SFTP 전송이 실행 중입니다.",
                    "A multi-server SFTP transfer is already running.");
                return;
            }

            panel.SetSftpRunning(true, Helpers.Loc.T(
                "실패한 서버만 다시 전송합니다…",
                "Retrying failed servers only…"));
            try
            {
                var progress = CreateMultiSftpProgress(panel, _lastMultiSftpQueueJobId);
                _lastMultiSftpBatch = _lastMultiSftpOperation == MultiSftpOperation.Upload
                    ? await _multiSftpCoordinator.RetryFailedAsync(
                        _lastMultiSftpSourcePath,
                        _lastMultiSftpBatch,
                        _lastMultiSftpOptions ?? CreateSftpTransferOptions(SftpConflictPolicy.Skip),
                        progress)
                    : await _multiSftpCoordinator.RetryFailedDownloadAsync(
                        _lastMultiSftpSourcePath,
                        _lastMultiSftpDestinationPath,
                        _lastMultiSftpBatch,
                        _lastMultiSftpOptions ?? CreateSftpTransferOptions(SftpConflictPolicy.Skip),
                        progress);
                PersistMultiSftpBatch(_lastMultiSftpQueueJobId, _lastMultiSftpBatch);
                panel.CompleteSftpBatch(_lastMultiSftpBatch);
            }
            catch (Exception error)
            {
                Debug.WriteLine($"Failed-target SFTP retry failed: {error}");
                panel.ShowSftpStatus(
                    $"실패 서버 재시도를 시작하지 못했습니다: {error.Message}",
                    $"Could not retry failed servers: {error.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _multiSftpInProgress, 0);
                panel.SetSftpRunning(false);
                RefreshRecoveredSftpCount(panel);
            }
        }

        private async Task ResumePendingSftpTransferAsync(MultiCommandPanel panel)
        {
            if (Interlocked.CompareExchange(ref _multiSftpInProgress, 1, 0) != 0)
            {
                panel.ShowSftpStatus(
                    "이미 다중 SFTP 전송이 실행 중입니다.",
                    "A multi-server SFTP transfer is already running.");
                return;
            }

            panel.ResetSftpTargets();
            panel.SetSftpRunning(true, Helpers.Loc.T(
                "복원된 전송 대상을 확인합니다…",
                "Matching targets for the restored transfer…"));
            try
            {
                var job = _sftpTransferQueue.RecoverIncomplete().FirstOrDefault(item =>
                    item.Mode is SftpQueueMode.FanOut or SftpQueueMode.FanIn);
                if (job is null)
                {
                    panel.ShowSftpStatus(
                        "복원할 다중 전송이 없습니다.",
                        "There is no multi-server transfer to restore.");
                    return;
                }
                if (job.Direction == sutty.Core.Sftp.SftpTransferDirection.Upload &&
                    !File.Exists(job.SourcePath) && !Directory.Exists(job.SourcePath))
                {
                    panel.ShowSftpStatus(
                        "원본 로컬 파일 또는 폴더가 없어 재개할 수 없습니다.",
                        "The local source no longer exists, so the transfer cannot resume.");
                    return;
                }

                var retryIds = SftpTransferQueueStore.GetRetryTargetIds(job);
                var remotePath = job.Direction == sutty.Core.Sftp.SftpTransferDirection.Upload
                    ? job.DestinationPath
                    : job.SourcePath;
                var (available, _) = GetSelectedSftpTargets(remotePath);
                var targets = available
                    .Where(target => retryIds.Contains(target.PersistenceId))
                    .ToArray();
                if (targets.Length == 0)
                {
                    panel.ShowSftpStatus(
                        "원래 대상과 일치하는 SSH 세션을 체크한 뒤 다시 재개하세요.",
                        "Check SSH sessions matching the original targets, then resume again.");
                    return;
                }

                var missing = retryIds.Count - targets.Length;
                _lastMultiSftpQueueJobId = job.Id;
                _lastMultiSftpSourcePath = job.SourcePath;
                _lastMultiSftpDestinationPath = job.DestinationPath;
                _lastMultiSftpOperation = job.Direction == sutty.Core.Sftp.SftpTransferDirection.Upload
                    ? MultiSftpOperation.Upload
                    : MultiSftpOperation.Download;
                _lastMultiSftpOptions = job.Options;
                var progress = CreateMultiSftpProgress(panel, job.Id);
                _lastMultiSftpBatch = _lastMultiSftpOperation == MultiSftpOperation.Upload
                    ? await _multiSftpCoordinator.UploadAsync(
                        job.SourcePath,
                        targets,
                        job.Options,
                        progress)
                    : await _multiSftpCoordinator.DownloadAsync(
                        job.SourcePath,
                        job.DestinationPath,
                        targets,
                        job.Options,
                        progress);
                PersistMultiSftpBatch(job.Id, _lastMultiSftpBatch);
                panel.CompleteSftpBatch(_lastMultiSftpBatch);
                if (missing > 0)
                {
                    panel.ShowSftpStatus(
                        $"현재 일치한 서버 전송 완료 · 나머지 {missing}개 대상은 복원 대기 중입니다.",
                        $"Matched servers completed · {missing} target(s) remain pending restore.");
                }
            }
            catch (Exception error)
            {
                Debug.WriteLine($"Restored SFTP transfer failed: {error}");
                panel.ShowSftpStatus(
                    $"복원된 전송을 재개하지 못했습니다: {error.Message}",
                    $"Could not resume the restored transfer: {error.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _multiSftpInProgress, 0);
                panel.SetSftpRunning(false);
                RefreshRecoveredSftpCount(panel);
            }
        }

        private (MultiSftpTarget[] Targets, int SelectedCount) GetSelectedSftpTargets(
            string remotePath)
        {
            var selected = MultiGrid.GetTargetSlots();
            var targets = selected
                .Select(slot => slot.CreateSftpTarget(remotePath))
                .OfType<MultiSftpTarget>()
                .GroupBy(target => target.PersistenceId, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
            return (targets, selected.Count);
        }

        private static SftpQueuedJob CreateQueuedJob(
            SftpQueueMode mode,
            sutty.Core.Sftp.SftpTransferDirection direction,
            string sourcePath,
            string destinationPath,
            IReadOnlyCollection<MultiSftpTarget> targets,
            SftpTransferOptions options) => new()
        {
            Mode = mode,
            Direction = direction,
            SourcePath = sourcePath,
            DestinationPath = destinationPath,
            Options = options,
            State = SftpQueueJobState.Running,
            Targets = targets.Select(target => new SftpQueuedTarget
            {
                Id = target.PersistenceId,
                DisplayName = target.DisplayName,
                SourcePath = sourcePath,
                DestinationPath = destinationPath,
                State = SftpQueueTargetState.Pending,
            }).ToList(),
        };

        private IProgress<MultiSftpTargetStatus> CreateMultiSftpProgress(
            MultiCommandPanel panel,
            string? queueJobId) => new Progress<MultiSftpTargetStatus>(status =>
        {
            panel.UpdateSftpTarget(status);
            if (string.IsNullOrWhiteSpace(queueJobId) ||
                status.State == MultiSftpTargetState.Transferring &&
                status.TransferProgress is not null)
            {
                return;
            }

            var state = status.State switch
            {
                MultiSftpTargetState.Transferring => SftpQueueTargetState.Running,
                MultiSftpTargetState.Succeeded => SftpQueueTargetState.Succeeded,
                MultiSftpTargetState.Failed => SftpQueueTargetState.Failed,
                MultiSftpTargetState.Cancelled => SftpQueueTargetState.Cancelled,
                _ => SftpQueueTargetState.Pending,
            };
            try
            {
                _sftpTransferQueue.UpdateTarget(
                    queueJobId,
                    status.Target.PersistenceId,
                    state,
                    status.Result?.BytesTransferred ?? status.TransferProgress?.BytesTransferred ?? 0,
                    status.TransferProgress?.TotalBytes ?? 0,
                    status.Error);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                          ArgumentException or InvalidOperationException)
            {
                Debug.WriteLine($"SFTP queue status persistence failed: {error.GetType().Name}");
            }
        });

        private void PersistMultiSftpBatch(string? queueJobId, MultiSftpBatchResult batch)
        {
            if (string.IsNullOrWhiteSpace(queueJobId))
                return;
            foreach (var status in batch.Targets)
            {
                var state = status.State switch
                {
                    MultiSftpTargetState.Succeeded => SftpQueueTargetState.Succeeded,
                    MultiSftpTargetState.Failed => SftpQueueTargetState.Failed,
                    MultiSftpTargetState.Cancelled => SftpQueueTargetState.Cancelled,
                    MultiSftpTargetState.Transferring => SftpQueueTargetState.Interrupted,
                    _ => SftpQueueTargetState.Pending,
                };
                try
                {
                    _sftpTransferQueue.UpdateTarget(
                        queueJobId,
                        status.Target.PersistenceId,
                        state,
                        status.Result?.BytesTransferred ?? status.TransferProgress?.BytesTransferred ?? 0,
                        status.TransferProgress?.TotalBytes ?? 0,
                        status.Error);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                              ArgumentException or InvalidOperationException)
                {
                    Debug.WriteLine($"SFTP final queue persistence failed: {error.GetType().Name}");
                }
            }
        }

        private void RefreshRecoveredSftpCount(MultiCommandPanel panel)
        {
            try
            {
                panel.SetRecoveredJobCount(_sftpTransferQueue.RecoverIncomplete().Count(item =>
                    item.Mode is SftpQueueMode.FanOut or SftpQueueMode.FanIn));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"SFTP queue recovery failed: {error.GetType().Name}");
                panel.SetRecoveredJobCount(0);
            }
        }

        private async Task<SftpTransferOptions?> ConfirmMultiSftpTransferAsync(
            IReadOnlyCollection<MultiSftpTarget> targets,
            sutty.Core.Sftp.SftpTransferDirection direction,
            string sourcePath,
            string destinationPath)
        {
            var configuredPolicy = SftpTransferOptions.ParseConflictPolicy(
                SettingsService.Current.SftpConflictPolicy);
            var content = new StackPanel { Spacing = 9 };
            var isUpload = direction == sutty.Core.Sftp.SftpTransferDirection.Upload;
            content.Children.Add(new TextBlock
            {
                Text = Helpers.Loc.T(
                    $"{targets.Count}개 서버에 {(isUpload ? "업로드" : "다운로드")}합니다. 대상과 충돌 처리 정책을 확인하세요.",
                    $"{(isUpload ? "Upload to" : "Download from")} {targets.Count} server(s). Review the targets and conflict policy."),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(new TextBlock
            {
                Text = Helpers.Loc.T(
                    $"원본: {sourcePath}{Environment.NewLine}대상: {destinationPath}",
                    $"Source: {sourcePath}{Environment.NewLine}Destination: {destinationPath}"),
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 11,
                Foreground = Helpers.ThemeResources.Brush(Root, "TextMuted"),
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(new TextBlock
            {
                Text = Helpers.Loc.T(
                    "선택된 서버 (최대 16개)",
                    "Selected servers (up to 16)"),
                FontSize = 11,
                Foreground = Helpers.ThemeResources.Brush(Root, "TextMuted"),
            });
            content.Children.Add(new TextBlock
            {
                Text = string.Join(Environment.NewLine, targets.Select(target => $"• {target.DisplayName}")),
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 11,
                MaxHeight = 180,
                TextWrapping = TextWrapping.Wrap,
            });

            ComboBox? policyChoices = null;
            if (configuredPolicy == SftpConflictPolicy.Ask)
            {
                content.Children.Add(new TextBlock
                {
                    Text = Helpers.Loc.T("같은 이름 파일 처리", "File conflict policy"),
                    FontSize = 11,
                    Foreground = Helpers.ThemeResources.Brush(Root, "TextMuted"),
                });
                policyChoices = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
                policyChoices.Items.Add(new ComboBoxItem
                {
                    Content = Helpers.Loc.T("기존 파일 건너뛰기", "Skip existing files"),
                    Tag = SftpConflictPolicy.Skip,
                });
                policyChoices.Items.Add(new ComboBoxItem
                {
                    Content = Helpers.Loc.T("안전하게 덮어쓰기", "Safely overwrite"),
                    Tag = SftpConflictPolicy.Overwrite,
                });
                policyChoices.Items.Add(new ComboBoxItem
                {
                    Content = Helpers.Loc.T("새 이름으로 저장", "Keep both with a new name"),
                    Tag = SftpConflictPolicy.Rename,
                });
                policyChoices.Items.Add(new ComboBoxItem
                {
                    Content = Helpers.Loc.T("새 파일일 때만 교체", "Replace only when source is newer"),
                    Tag = SftpConflictPolicy.NewerOnly,
                });
                policyChoices.SelectedIndex = 0;
                content.Children.Add(policyChoices);
            }
            else
            {
                content.Children.Add(new TextBlock
                {
                    Text = Helpers.Loc.T(
                        $"충돌 정책: {DescribeConflictPolicy(configuredPolicy, korean: true)}",
                        $"Conflict policy: {DescribeConflictPolicy(configuredPolicy, korean: false)}"),
                    FontSize = 11,
                    Foreground = Helpers.ThemeResources.Brush(Root, "TextMuted"),
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = Helpers.Loc.T("다중 SFTP 전송 확인", "Confirm multi-server SFTP transfer"),
                Content = content,
                PrimaryButtonText = Helpers.Loc.T(
                    $"{targets.Count}개 서버에서 시작",
                    $"Start on {targets.Count} servers"),
                CloseButtonText = Helpers.Loc.T("취소", "Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return null;

            var policy = policyChoices?.SelectedItem is ComboBoxItem { Tag: SftpConflictPolicy selected }
                ? selected
                : configuredPolicy;
            return CreateSftpTransferOptions(policy);
        }

        private static string DescribeConflictPolicy(SftpConflictPolicy policy, bool korean) => policy switch
        {
            SftpConflictPolicy.Overwrite => korean ? "안전하게 덮어쓰기" : "Safely overwrite",
            SftpConflictPolicy.Skip => korean ? "기존 파일 건너뛰기" : "Skip existing files",
            SftpConflictPolicy.Rename => korean ? "새 이름으로 저장" : "Keep both with a new name",
            SftpConflictPolicy.NewerOnly => korean ? "새 파일일 때만 교체" : "Replace only when source is newer",
            _ => korean ? "매번 확인" : "Ask every time",
        };

        private static SftpTransferOptions CreateSftpTransferOptions(SftpConflictPolicy conflictPolicy)
        {
            var settings = SettingsService.Current;
            return new SftpTransferOptions
            {
                Overwrite = conflictPolicy is SftpConflictPolicy.Overwrite or SftpConflictPolicy.NewerOnly,
                ConflictPolicy = conflictPolicy,
                Resume = true,
                VerifyChecksum = string.Equals(
                    settings.SftpVerificationMode,
                    "Sha256",
                    StringComparison.OrdinalIgnoreCase),
                RetryEnabled = settings.SftpRetryEnabled,
                MaxRetries = settings.SftpRetryCount,
            };
        }

        private async Task BroadcastFromPanelAsync(MultiCommandPanel panel, string command)
        {
            if (Interlocked.CompareExchange(ref _broadcastInProgress, 1, 0) != 0)
            {
                panel.ShowBroadcastStatus(Helpers.Loc.T(
                    "이미 다른 브로드캐스트가 실행 중입니다.",
                    "Another broadcast is already running."));
                return;
            }

            var completed = false;
            var failed = false;
            panel.SetBroadcastRunning(true);
            try
            {
                completed = await BroadcastAsync(command);
            }
            catch (Exception error)
            {
                failed = true;
                Debug.WriteLine($"Broadcast batch failed: {error}");
            }
            finally
            {
                Interlocked.Exchange(ref _broadcastInProgress, 0);
                panel.SetBroadcastRunning(false, failed
                    ? Helpers.Loc.T("브로드캐스트 실행 실패", "Broadcast failed")
                    : completed
                        ? Helpers.Loc.T("브로드캐스트 완료", "Broadcast complete")
                        : null);
            }
        }

        // 체크된 모든 세션에 같은 명령을 병렬로 전송하고, 결과를 그리드 셀에 표시
        private async Task<bool> BroadcastAsync(string command)
        {
            var targets = MultiGrid.GetTargetSlots();
            if (targets.Count == 0)
            {
                var dialog = new ContentDialog
                {
                    Title = Helpers.Loc.T("대상 세션 없음", "No target sessions"),
                    Content = Helpers.Loc.T(
                        "체크된 세션이 없습니다. 그리드에서 대상 세션을 체크하세요.",
                        "No sessions are checked. Check target sessions in the grid."),
                    CloseButtonText = "OK",
                    XamlRoot = Content.XamlRoot,
                };
                await dialog.ShowAsync();
                return false;
            }

            var productionTargets = targets
                .Where(slot => slot.IsProduction)
                .ToList();
            if (productionTargets.Count > 0)
            {
                var targetNames = string.Join(", ", productionTargets.Select(slot => slot.Title));
                var warning = new StackPanel { Spacing = 10 };
                warning.Children.Add(new TextBlock
                {
                    Text = Helpers.Loc.T(
                        $"PROD 태그가 있는 {productionTargets.Count}개 세션이 포함되어 있습니다.",
                        $"This broadcast includes {productionTargets.Count} session(s) tagged PROD."),
                    Foreground = Helpers.ThemeResources.Brush(Root, "StatusRed"),
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                });
                warning.Children.Add(new TextBlock
                {
                    Text = targetNames,
                    Foreground = Helpers.ThemeResources.Brush(Root, "TextMuted"),
                    TextWrapping = TextWrapping.Wrap,
                });
                warning.Children.Add(new TextBlock
                {
                    Text = command,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
                    Foreground = Helpers.ThemeResources.Brush(Root, "TextPrimary"),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                });

                var confirm = new ContentDialog
                {
                    Title = Helpers.Loc.T("PROD 브로드캐스트 확인", "Confirm PROD broadcast"),
                    Content = warning,
                    PrimaryButtonText = Helpers.Loc.T(
                        $"{targets.Count}개 세션에서 실행",
                        $"Run on {targets.Count} sessions"),
                    CloseButtonText = Helpers.Loc.T("취소", "Cancel"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = Content.XamlRoot,
                };
                if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                    return false;
            }

            await Task.WhenAll(targets.Select(slot =>
                RunBroadcastOnSlotAsync(slot, command)));
            return true;
        }

        private static async Task RunBroadcastOnSlotAsync(ViewModels.MultiSlotVm slot, string command)
        {
            slot.LastOutput = "…";
            slot.ResultText = Helpers.Loc.T("실행 중", "running");
            using var timeoutCancellation = new CancellationTokenSource(BroadcastCommandTimeout);
            try
            {
                var result = await slot.ExecuteAsync(command, timeoutCancellation.Token);
                var output = result.CombinedOutput;
                var timedOut = timeoutCancellation.IsCancellationRequested &&
                               string.Equals(
                                   result.ExitSignal,
                                   "CANCELLED",
                                   StringComparison.OrdinalIgnoreCase);
                slot.ResultText = timedOut
                    ? Helpers.Loc.T("시간 초과", "timed out")
                    : slot.LocalView is not null
                    ? Helpers.Loc.T("완료", "complete")
                    : result.ExitCode is int exitCode
                        ? $"exit {exitCode}"
                        : result.ExitSignal is { Length: > 0 } signal
                            ? signal.ToLowerInvariant()
                            : Helpers.Loc.T("실패", "failed");
                slot.LastOutput = string.IsNullOrWhiteSpace(output)
                    ? timedOut
                        ? Helpers.Loc.T(
                            "60초 안에 응답이 없어 중단했습니다.",
                            "Stopped after no response for 60 seconds.")
                        : slot.LocalView is not null
                        ? Helpers.Loc.T("(출력 없음)", "(no output)")
                        : result.Succeeded
                            ? Helpers.Loc.T("(출력 없음)", "(no output)")
                            : Helpers.Loc.T("(출력 없이 실패)", "(failed with no output)")
                    : output.Length > 400 ? output[..400] + "…" : output;
            }
            catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
            {
                slot.ResultText = Helpers.Loc.T("시간 초과", "timed out");
                slot.LastOutput = Helpers.Loc.T(
                    "60초 안에 응답이 없어 중단했습니다.",
                    "Stopped after no response for 60 seconds.");
            }
            catch (Exception ex)
            {
                slot.ResultText = Helpers.Loc.T("실패", "failed");
                slot.LastOutput = $"error: {ex.Message}";
            }
        }

        private async Task RunCommandOnActiveSessionAsync(string command)
        {
            if ((TitleTabs.SelectedItem as TabViewItem)?.DataContext is not SessionView view)
            {
                var dialog = new ContentDialog
                {
                    Title = Helpers.Loc.T("활성 세션 없음", "No active session"),
                    Content = Helpers.Loc.T(
                        "명령을 실행할 세션이 없습니다. 먼저 서버에 연결하세요.",
                        "There is no session to run the command in. Connect to a server first."),
                    CloseButtonText = "OK",
                    XamlRoot = Content.XamlRoot,
                };
                await dialog.ShowAsync();
                return;
            }
            await view.RunExternalCommandAsync(command);
            if (_sessionWorkspaces.TryGetValue(view, out var workspace))
                ActivateWorkspace(workspace, SessionWorkspaceSection.Commands);
        }

        // ── 세션 탭 ──

        /// <summary>현재 선택된 탭의 세션. 없으면 null.</summary>
        private SessionView? ActiveSessionView =>
            (TitleTabs.SelectedItem as TabViewItem)?.DataContext as SessionView;

        private ISshSession? ActiveSession => ActiveSessionView?.Session;

        private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            var controlDown = IsKeyDown(Windows.System.VirtualKey.Control);
            var altDown = IsKeyDown(Windows.System.VirtualKey.Menu);
            var shortcutNumber = ShortcutNumber(e.Key);

            if (controlDown && !altDown)
            {
                if (shortcutNumber is int tabNumber)
                {
                    if (SelectTabWithShortcut(tabNumber))
                    {
                        e.Handled = true;
                    }
                    return;
                }

                if (e.Key == Windows.System.VirtualKey.T)
                {
                    e.Handled = true;
                    ShowNewTabMenu();
                    return;
                }

                // Win32 VK_OEM_COMMA. VirtualKey does not expose a named comma member.
                if ((int)e.Key == 0xBC)
                {
                    e.Handled = true;
                    OpenSettingWindow();
                }
                return;
            }
        }

        private void NavigationKeyboardAccelerator_Invoked(
            KeyboardAccelerator sender,
            KeyboardAcceleratorInvokedEventArgs args)
        {
            args.Handled = true;
            if (ShortcutNumber(sender.Key) is not int navigationNumber)
                return;

            NavigateWithAccelerator(navigationNumber);
        }

        private void NavigateWithAccelerator(int navigationNumber)
        {

            if (NavigationService.TryGetGlobalPageForAccelerator(navigationNumber, out var page))
            {
                SelectNavigationItem(page.ToString());
                return;
            }

            if ((TitleTabs.SelectedItem as TabViewItem)?.DataContext is SessionView sessionView &&
                _sessionWorkspaces.TryGetValue(sessionView, out var workspace) &&
                NavigationService.TryGetSessionSectionForAccelerator(
                    navigationNumber,
                    out var section))
            {
                ActivateWorkspace(workspace, section);
                return;
            }

            // Alt+6 still returns to a selected local terminal. Alt+7 is deliberately
            // consumed for local tabs because there is no remote filesystem.
            if (navigationNumber == 6 && TitleTabs.SelectedItem is TabViewItem)
                ActivateSelectedSession();
        }

        private void TerminalView_AppShortcutRequested(
            object? sender,
            TerminalAppShortcutRequest request)
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(() => HandleTerminalAppShortcut(request));
                return;
            }

            HandleTerminalAppShortcut(request);
        }

        private void HandleTerminalAppShortcut(TerminalAppShortcutRequest request)
        {
            switch (request.Action)
            {
                case TerminalAppShortcutAction.Navigate:
                    NavigateWithAccelerator(request.Number);
                    break;
                case TerminalAppShortcutAction.SelectTab:
                    SelectTabWithShortcut(request.Number);
                    break;
                case TerminalAppShortcutAction.NewTab:
                    ShowNewTabMenu();
                    break;
                case TerminalAppShortcutAction.Settings:
                    OpenSettingWindow();
                    break;
            }
        }

        private bool SelectTabWithShortcut(int tabNumber)
        {
            var index = tabNumber - 1;
            if (index < 0 || index >= TitleTabs.TabItems.Count)
                return false;

            TitleTabs.SelectedItem = TitleTabs.TabItems[index];
            ActivateSelectedSession();
            UpdateSessionArea();
            return true;
        }

        private static int? ShortcutNumber(Windows.System.VirtualKey key)
        {
            var value = (int)key;
            if (value is >= 0x31 and <= 0x39)
                return value - 0x30;
            if (value is >= 0x61 and <= 0x69)
                return value - 0x60;
            return null;
        }

        private static bool IsKeyDown(Windows.System.VirtualKey key) =>
            Microsoft.UI.Input.InputKeyboardSource
                .GetKeyStateForCurrentThread(key)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        private void TitleTabs_AddTabButtonClick(TabView sender, object args) => ShowNewTabMenu();

        private void ShowNewTabMenu() => NewTabMenu.ShowAt(TitleTabs);

        private void NewSshConnection_Click(object sender, RoutedEventArgs e)
        {
            SelectNavigationItem("Home");
            DispatcherQueue.TryEnqueue(() => _homeDashboard?.FocusQuickConnect());
        }

        private void OpenSavedHost_Click(object sender, RoutedEventArgs e) =>
            SelectNavigationItem("Hosts");

        private async void OpenLocalPowerShell_Click(object sender, RoutedEventArgs e) =>
            await OpenLocalTerminalTabAsync();

        private void ImportHosts_Click(object sender, RoutedEventArgs e)
        {
            SelectNavigationItem("Settings");
            CreateEmbeddedSettingsPanel().NavigateToSection("Connection");
        }

        private async Task OpenLocalTerminalTabAsync()
        {
            if (TitleTabs.TabItems.Count >= MaxSessions)
            {
                var limitDialog = new ContentDialog
                {
                    Title = Helpers.Loc.T("탭 개수 제한", "Tab limit"),
                    Content = Helpers.Loc.T(
                        $"탭은 최대 {MaxSessions}개까지 열 수 있습니다. 탭을 닫고 다시 시도하세요.",
                        $"You can open up to {MaxSessions} tabs. Close a tab and try again."),
                    CloseButtonText = "OK",
                    XamlRoot = Content.XamlRoot,
                };
                await limitDialog.ShowAsync();
                return;
            }

            var view = new LocalTerminalView();
            view.AppShortcutRequested += TerminalView_AppShortcutRequested;
            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 7,
                Height = 7,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = Helpers.ThemeResources.Brush(Root, "StatusIdle"),
            };
            var metadata = new TextBlock
            {
                Text = Environment.MachineName,
                FontSize = 11,
                Foreground = Helpers.ThemeResources.Brush(Root, "TextFaint"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            header.Children.Add(dot);
            header.Children.Add(new TextBlock
            {
                Text = "PowerShell",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            header.Children.Add(metadata);

            void UpdateStatusDot(TerminalState state) =>
                dot.Fill = Helpers.ThemeResources.Brush(Root, state switch
                {
                    TerminalState.Open => "StatusGreen",
                    TerminalState.Opening => "StatusAmber",
                    TerminalState.Failed => "StatusRed",
                    _ => "StatusIdle",
                });

            dot.ActualThemeChanged += (_, _) => UpdateStatusDot(view.Terminal.TerminalState);
            metadata.ActualThemeChanged += (_, _) =>
                metadata.Foreground = Helpers.ThemeResources.Brush(Root, "TextFaint");
            view.Terminal.TerminalStateChanged += (_, state) =>
                DispatcherQueue.TryEnqueue(() => UpdateStatusDot(state));

            var tab = new TabViewItem
            {
                Header = header,
                IsClosable = true,
                DataContext = view,
            };
            tab.Tapped += SessionTab_Tapped;

            TitleTabs.TabItems.Add(tab);
            TitleTabs.SelectedItem = tab;
            ActivateSelectedSession();
            UpdateSessionArea();
            QueueWorkspaceSnapshot();
        }

        private async Task OpenSessionTabAsync(SshConnectionInfo info)
        {
            // 세션 개수 제한 (Multi 그리드 4×4와 맞춤)
            if (TitleTabs.TabItems.Count >= MaxSessions)
            {
                var limitDialog = new ContentDialog
                {
                    Title = Helpers.Loc.T("세션 개수 제한", "Session limit"),
                    Content = Helpers.Loc.T(
                        $"세션은 최대 {MaxSessions}개까지 열 수 있습니다. 탭을 닫고 다시 시도하세요.",
                        $"You can open up to {MaxSessions} sessions. Close a tab and try again."),
                    CloseButtonText = "OK",
                    XamlRoot = Content.XamlRoot,
                };
                await limitDialog.ShowAsync();
                info.ClearTransientSecrets();
                return;
            }

            try
            {
                SshConnectionPreflightValidator.Validate(info);
            }
            catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
            {
                await HandlePreflightFailureAsync(info, error);
                info.ClearTransientSecrets();
                return;
            }

            if (!await ConfirmHighRiskConnectionFeaturesAsync(info))
            {
                info.ClearTransientSecrets();
                return;
            }

            info.HostKeyPromptAsync ??= (verification, ct) => DispatchPromptToUiAsync(
                () => PromptUnknownHostKeyAsync(verification, ct),
                ct);
            info.HostKeyRotationPromptAsync ??= (verification, ct) => DispatchPromptToUiAsync(
                () => PromptChangedHostKeyRotationAsync(verification, ct),
                ct);
            info.KeyboardInteractivePromptAsync ??= PromptKeyboardInteractiveAsync;

            ISshSession session;
            try
            {
                session = _sessions.Create(info);
            }
            catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
            {
                await HandlePreflightFailureAsync(info, error);
                info.ClearTransientSecrets();
                return;
            }
            sutty.Command.HostProfile? savedProfile = null;
            var view = new SessionView(session);
            view.AppShortcutRequested += TerminalView_AppShortcutRequested;
            var workspace = new SessionWorkspaceView(
                view,
                WinRT.Interop.WindowNative.GetWindowHandle(this));
            _sessionWorkspaces[view] = workspace;

            // 리디자인 탭 헤더: [상태점] 세션이름 username
            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 7,
                Height = 7,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = Helpers.ThemeResources.Brush(Root, "StatusIdle"),
            };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            header.Children.Add(dot);
            header.Children.Add(new TextBlock
            {
                Text = info.Title,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (!string.IsNullOrWhiteSpace(info.Username))
            {
                var metadata = new TextBlock
                {
                    Text = info.Username,
                    FontSize = 11,
                    Foreground = Helpers.ThemeResources.Brush(Root, "TextFaint"),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                metadata.ActualThemeChanged += (_, _) =>
                    metadata.Foreground = Helpers.ThemeResources.Brush(Root, "TextFaint");
                header.Children.Add(metadata);
            }

            void UpdateStatusDot(SessionState state) =>
                dot.Fill = Helpers.ThemeResources.Brush(Root, state switch
                {
                    SessionState.Connected => "StatusGreen",
                    SessionState.Connecting or SessionState.Disconnecting => "StatusAmber",
                    SessionState.Failed => "StatusRed",
                    _ => "StatusIdle",
                });

            // 상태와 테마가 바뀔 때 코드로 만든 탭 표시도 함께 갱신한다.
            dot.ActualThemeChanged += (_, _) => UpdateStatusDot(session.State);
            session.StateChanged += (_, state) =>
                DispatcherQueue.TryEnqueue(() =>
                {
                    UpdateStatusDot(state);
                    if (state == SessionState.Failed)
                        RememberFailedSupportContext(session);
                });
            session.SftpStateChanged += (_, state) =>
            {
                if (state == SftpConnectionState.Unavailable)
                {
                    DispatcherQueue.TryEnqueue(() =>
                        RememberFailedSupportContext(session));
                }
            };

            var tab = new TabViewItem
            {
                Header = header,
                IsClosable = true,
                DataContext = view,
            };
            tab.Tapped += SessionTab_Tapped;

            TitleTabs.TabItems.Add(tab);
            TitleTabs.SelectedItem = tab;
            ActivateWorkspace(workspace, workspace.CurrentSection);
            UpdateSessionArea();
            QueueWorkspaceSnapshot();

            var timer = Stopwatch.StartNew();
            var outcome = ConnectionAttemptOutcome.Failed;
            string? errorCode = ConnectionDiagnosticErrorCodes.UnexpectedFailure;
            try
            {
                await session.ConnectAsync();
                if (session.State == SessionState.Connected)
                {
                    outcome = ConnectionAttemptOutcome.Success;
                    errorCode = null;
                    // Persist only after the connection proves usable. This keeps failed
                    // one-off attempts out of Saved Hosts while secrets are still available
                    // for an explicitly requested encrypted-vault save.
                    if (ConnectionPersistencePolicy.ShouldOfferSave(
                            outcome,
                            info.SaveProfile,
                            info.SavedHostId))
                    {
                        await OfferSaveAfterSuccessAsync(info);
                    }
                    if (ConnectionPersistencePolicy.ShouldPersistProfile(
                            outcome,
                            info.SaveProfile))
                    {
                        savedProfile = await PersistSavedProfileAsync(info);
                    }
                    var connectedProfileId = savedProfile?.Id ?? info.SavedHostId;
                    if (!string.IsNullOrWhiteSpace(connectedProfileId))
                        sutty.Command.HostProfileStore.MarkConnected(connectedProfileId);
                }
                else if (session.LastDiagnostic is { } failure)
                {
                    errorCode = failure.ErrorCode;
                }
            }
            catch (OperationCanceledException)
            {
                outcome = ConnectionAttemptOutcome.Cancelled;
                errorCode = session.LastDiagnostic?.ErrorCode ??
                    ConnectionDiagnosticErrorCodes.ConnectionCancelled;
            }
            catch (Exception error)
            {
                errorCode = session.LastDiagnostic?.ErrorCode ??
                    ConnectionExceptionClassifier.Classify(
                        error,
                        routeType: session.CorrelationContext.RouteType).ErrorCode;
                Debug.WriteLine($"Unexpected connection failure: {error.GetType().Name}");
                ConnectionLogStore.Append(
                    session.Id,
                    info.Title,
                    $"{info.Host}:{info.Port}",
                    ConnectionLogSeverity.Error,
                    "UI connection flow",
                    "SSH 연결 처리 중 예상하지 못한 오류가 발생했습니다.",
                    "An unexpected error occurred in the SSH connection flow.",
                    error.ToString());
            }
            finally
            {
                timer.Stop();
                info.ClearTransientSecrets();
                try
                {
                    sutty.Command.HostHistoryStore.Append(
                        info.Title,
                        info.Host,
                        info.Username,
                        info.Port,
                        info.AuthMethod.ToString(),
                        info.AuthMethod == SshAuthMethod.PublicKey ? info.PrivateKeyPath : "",
                        info.Tags,
                        outcome.ToString(),
                        errorCode,
                        timer.ElapsedMilliseconds);
                }
                catch (Exception historyError) when (historyError is System.IO.IOException or
                                                     Microsoft.Data.Sqlite.SqliteException or
                                                     UnauthorizedAccessException)
                {
                    Debug.WriteLine($"Connection history append failed: {historyError.GetType().Name}");
                }

                foreach (var historyPanel in _hostPanels)
                    historyPanel.RefreshFromStore();
                _homeDashboard?.RefreshHosts();
            }

            if (outcome is ConnectionAttemptOutcome.Failed or
                ConnectionAttemptOutcome.Cancelled)
                RememberFailedSupportContext(session, errorCode);

            if (outcome == ConnectionAttemptOutcome.Failed)
                await ShowConnectionFailureAsync(session);
        }

        private async Task HandlePreflightFailureAsync(
            SshConnectionInfo info,
            Exception error)
        {
            var routeType = info.Route is { Type: var requestedRoute } &&
                            Enum.IsDefined(requestedRoute)
                ? requestedRoute
                : ConnectionRouteType.Direct;
            var authenticationType = Enum.IsDefined(info.AuthMethod)
                ? info.AuthMethod
                : SshAuthMethod.Password;
            var diagnosis = ConnectionExceptionClassifier.Classify(
                error,
                error is RoutePolicyViolationException
                    ? ConnectionDiagnosticStage.ProxyOrJumpRoute
                    : null,
                routeType);
            var correlationId = Guid.NewGuid().ToString("N");
            ConnectionDiagnosticEventStore.Shared.Append(correlationId, diagnosis);
            RememberFailedSupportContext(
                routeType,
                authenticationType,
                diagnosis.ErrorCode,
                diagnosis.Stage,
                correlationId,
                allowUnsequencedOverwrite: true);

            ConnectionLogStore.Append(
                Guid.ParseExact(correlationId, "N"),
                info.Title,
                $"{info.Host}:{info.Port}",
                ConnectionLogSeverity.Error,
                "Connection Doctor",
                "네트워크 연결 전에 설정 검증이 실패했습니다.",
                "Connection settings failed validation before network access.",
                diagnosis.TechnicalDetail);

            await ShowPreflightFailureAsync(
                diagnosis,
                routeType,
                authenticationType,
                correlationId);
        }

        private async Task ShowPreflightFailureAsync(
            ConnectionDiagnosticResult diagnosis,
            ConnectionRouteType routeType,
            SshAuthMethod authenticationType,
            string correlationId)
        {
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock
            {
                Text = Helpers.Loc.T(
                    "네트워크에 연결하기 전에 설정을 확인해야 합니다.",
                    "Review the settings before a network connection can start."),
                Foreground = Helpers.ThemeResources.Brush(Root, "TextPrimary"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(new TextBlock
            {
                Text = Helpers.Loc.T(diagnosis.UserActionKo, diagnosis.UserActionEn),
                Foreground = Helpers.ThemeResources.Brush(Root, "TextPrimary"),
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(CreateDiagnosticDetails(correlationId, diagnosis));
            content.Children.Add(new TextBlock
            {
                Text = Helpers.Loc.T(
                    "복사 요약과 지원 번들은 Hostname·사용자 이름·비밀번호·경로를 포함하지 않습니다.",
                    "The copied summary and support bundle exclude hostnames, usernames, passwords, and paths."),
                Foreground = Helpers.ThemeResources.Brush(Root, "TextMuted"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });

            var dialog = new ContentDialog
            {
                Title = "Connection Doctor",
                Content = new ScrollViewer
                {
                    MaxHeight = 580,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = content,
                },
                PrimaryButtonText = Helpers.Loc.T("상세 로그 열기", "Open detailed logs"),
                SecondaryButtonText = Helpers.Loc.T("안전한 요약 복사", "Copy safe summary"),
                CloseButtonText = Helpers.Loc.T("닫기", "Close"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
                OpenTroubleshooting();
            else if (result == ContentDialogResult.Secondary)
            {
                Helpers.ClipboardHelper.CopyText(BuildSafeDiagnosticSummary(
                    diagnosis,
                    routeType,
                    authenticationType,
                    correlationId));
            }
        }

        private async Task ShowConnectionFailureAsync(ISshSession session)
        {
            var logEntry = ConnectionLogStore.Snapshot()
                .LastOrDefault(entry => entry.SessionId == session.Id &&
                                        entry.Severity >= ConnectionLogSeverity.Error);
            var diagnosis = session.LastDiagnostic;
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock
            {
                Text = logEntry is null
                    ? Helpers.Loc.T(
                        "SSH 연결을 완료하지 못했습니다.",
                        "The SSH connection could not be completed.")
                    : Helpers.Loc.T(logEntry.MessageKo, logEntry.MessageEn),
                Foreground = Helpers.ThemeResources.Brush(Root, "TextPrimary"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            if (diagnosis is not null)
            {
                content.Children.Add(new TextBlock
                {
                    Text = Helpers.Loc.T(diagnosis.UserActionKo, diagnosis.UserActionEn),
                    Foreground = Helpers.ThemeResources.Brush(Root, "TextPrimary"),
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            content.Children.Add(CreateDiagnosticDetails(
                session.CorrelationContext.CorrelationId,
                diagnosis));
            content.Children.Add(new TextBlock
            {
                Text = Helpers.Loc.T(
                    "복사 요약에는 Hostname·사용자 이름·비밀번호·경로·터미널 내용이 포함되지 않습니다.",
                    "The copied summary excludes hostnames, usernames, passwords, paths, and terminal content."),
                Foreground = Helpers.ThemeResources.Brush(Root, "TextMuted"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });

            var dialog = new ContentDialog
            {
                Title = Helpers.Loc.T("Connection Doctor", "Connection Doctor"),
                Content = new ScrollViewer
                {
                    MaxHeight = 580,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = content,
                },
                PrimaryButtonText = Helpers.Loc.T("상세 로그 열기", "Open detailed logs"),
                SecondaryButtonText = Helpers.Loc.T("안전한 요약 복사", "Copy safe summary"),
                CloseButtonText = Helpers.Loc.T("닫기", "Close"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
                OpenTroubleshooting();
            else if (result == ContentDialogResult.Secondary)
                Helpers.ClipboardHelper.CopyText(BuildSafeConnectionSummary(session));
        }

        private Expander CreateDiagnosticDetails(
            string correlationId,
            ConnectionDiagnosticResult? diagnosis)
        {
            var details = new StackPanel { Spacing = 8 };
            details.Children.Add(CreateConnectionStageSummary(correlationId));
            if (diagnosis is not null)
            {
                details.Children.Add(new TextBlock
                {
                    Text = $"{diagnosis.ErrorCode} · " +
                        $"{LocalizeDiagnosticStage(diagnosis.Stage)}",
                    Foreground = Helpers.ThemeResources.Brush(Root, "StatusRed"),
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 10.5,
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            details.Children.Add(new TextBlock
            {
                Text = string.Join(
                    Environment.NewLine,
                    new[]
                    {
                        diagnosis?.TechnicalDetail,
                        $"correlation={correlationId}",
                    }.Where(value => !string.IsNullOrWhiteSpace(value))),
                Foreground = Helpers.ThemeResources.Brush(Root, "TextMuted"),
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 10.5,
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.Wrap,
            });
            return new Expander
            {
                Header = Helpers.Loc.T("진단 상세", "Diagnostic details"),
                IsExpanded = false,
                Content = details,
            };
        }

        private Border CreateConnectionStageSummary(string correlationId)
        {
            var latestByStage = ConnectionDiagnosticEventStore.Shared
                .Snapshot(correlationId, 128)
                .GroupBy(entry => entry.Stage)
                .ToDictionary(group => group.Key, group => group.Last());
            var stages = new StackPanel { Spacing = 5 };
            stages.Children.Add(new TextBlock
            {
                Text = Helpers.Loc.T("연결 단계", "Connection stages"),
                CharacterSpacing = 55,
                FontSize = 10.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Helpers.ThemeResources.Brush(Root, "TextMuted"),
            });

            foreach (var stage in Enum.GetValues<ConnectionDiagnosticStage>())
            {
                latestByStage.TryGetValue(stage, out var entry);
                var status = entry?.Status ?? ConnectionDiagnosticStatus.NotStarted;
                var colorKey = status switch
                {
                    ConnectionDiagnosticStatus.Succeeded => "StatusGreen",
                    ConnectionDiagnosticStatus.Failed => "StatusRed",
                    ConnectionDiagnosticStatus.Cancelled => "StatusAmber",
                    ConnectionDiagnosticStatus.Running => "AccentTeal",
                    _ => "TextFaint",
                };
                var statusText = LocalizeDiagnosticStatus(status);
                var detail = entry is { ErrorCode: not ConnectionDiagnosticErrorCodes.None }
                    ? $"{statusText} · {entry.ErrorCode}"
                    : statusText;
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock
                {
                    Text = status switch
                    {
                        ConnectionDiagnosticStatus.Succeeded => "✓",
                        ConnectionDiagnosticStatus.Failed => "×",
                        ConnectionDiagnosticStatus.Cancelled => "!",
                        ConnectionDiagnosticStatus.Running => "…",
                        ConnectionDiagnosticStatus.Skipped => "—",
                        _ => "○",
                    },
                    Width = 14,
                    Foreground = Helpers.ThemeResources.Brush(Root, colorKey),
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextAlignment = TextAlignment.Center,
                });
                var stageText = new TextBlock
                {
                    Text = LocalizeDiagnosticStage(stage),
                    FontSize = 10.5,
                    Foreground = Helpers.ThemeResources.Brush(Root, "TextPrimary"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                Grid.SetColumn(stageText, 1);
                row.Children.Add(stageText);
                var detailText = new TextBlock
                {
                    Text = detail,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 9.5,
                    Foreground = Helpers.ThemeResources.Brush(Root, colorKey),
                };
                Grid.SetColumn(detailText, 2);
                row.Children.Add(detailText);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                    row,
                    $"{LocalizeDiagnosticStage(stage)}, {detail}");
                stages.Children.Add(row);
            }

            return new Border
            {
                Padding = new Thickness(10),
                Background = Helpers.ThemeResources.Brush(Root, "CardBg"),
                BorderBrush = Helpers.ThemeResources.Brush(Root, "CardBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Child = stages,
            };
        }

        private static string LocalizeDiagnosticStage(ConnectionDiagnosticStage stage) => stage switch
        {
            ConnectionDiagnosticStage.InputValidation => Helpers.Loc.T("입력값 검증", "Input validation"),
            ConnectionDiagnosticStage.DnsAndTcp => Helpers.Loc.T("DNS·TCP", "DNS and TCP"),
            ConnectionDiagnosticStage.ProxyOrJumpRoute => Helpers.Loc.T("Proxy·Jump 경로", "Proxy or jump route"),
            ConnectionDiagnosticStage.SshHandshake => Helpers.Loc.T("SSH 핸드셰이크", "SSH handshake"),
            ConnectionDiagnosticStage.HostKey => Helpers.Loc.T("호스트 키", "Host key"),
            ConnectionDiagnosticStage.Authentication => Helpers.Loc.T("인증", "Authentication"),
            ConnectionDiagnosticStage.Pty => "PTY",
            ConnectionDiagnosticStage.SftpSubsystem => "SFTP subsystem",
            ConnectionDiagnosticStage.PortForwarding => Helpers.Loc.T("포트 포워딩", "Port forwarding"),
            _ => stage.ToString(),
        };

        private static string LocalizeDiagnosticStatus(ConnectionDiagnosticStatus status) => status switch
        {
            ConnectionDiagnosticStatus.NotStarted => Helpers.Loc.T("미실행", "Not run"),
            ConnectionDiagnosticStatus.Running => Helpers.Loc.T("진행 중", "Running"),
            ConnectionDiagnosticStatus.Succeeded => Helpers.Loc.T("성공", "Succeeded"),
            ConnectionDiagnosticStatus.Failed => Helpers.Loc.T("실패", "Failed"),
            ConnectionDiagnosticStatus.Cancelled => Helpers.Loc.T("취소", "Cancelled"),
            ConnectionDiagnosticStatus.Skipped => Helpers.Loc.T("건너뜀", "Skipped"),
            _ => status.ToString(),
        };

        private static string BuildSafeConnectionSummary(ISshSession session) =>
            BuildSafeDiagnosticSummary(
                session.LastDiagnostic,
                session.CorrelationContext.RouteType,
                session.Info.AuthMethod,
                session.CorrelationContext.CorrelationId);

        private static string BuildSafeDiagnosticSummary(
            ConnectionDiagnosticResult? diagnosis,
            ConnectionRouteType routeType,
            SshAuthMethod authenticationType,
            string correlationId)
        {
            var action = diagnosis is null
                ? Helpers.Loc.T("연결 설정을 확인한 뒤 다시 시도하세요.", "Review the connection settings and retry.")
                : Helpers.Loc.T(diagnosis.UserActionKo, diagnosis.UserActionEn);
            return string.Join(
                Environment.NewLine,
                Helpers.AppReleaseInfo.DisplayVersion,
                $"Code: {diagnosis?.ErrorCode ?? ConnectionDiagnosticErrorCodes.UnexpectedFailure}",
                $"Stage: {(diagnosis is null ? "Unknown" : diagnosis.Stage)}",
                $"Route: {routeType}",
                $"Authentication: {authenticationType}",
                $"Correlation ID: {correlationId}",
                $"Action: {action}",
                $"Technical: {diagnosis?.TechnicalDetail ?? "Unavailable"}");
        }

        private void SelectNavigationItem(string tag)
        {
            var item = LeftNav.MenuItems
                .Concat(LeftNav.FooterMenuItems)
                .OfType<NavigationViewItem>()
                .FirstOrDefault(candidate => string.Equals(candidate.Tag as string, tag, StringComparison.Ordinal));
            if (item is null)
                return;
            if (ReferenceEquals(LeftNav.SelectedItem, item) &&
                Enum.TryParse<AppGlobalPage>(tag, out var page))
            {
                NavigateGlobal(page);
            }
            else
            {
                LeftNav.SelectedItem = item;
            }
        }

        private async Task<sutty.Command.HostProfile?> PersistSavedProfileAsync(SshConnectionInfo info)
        {
            if (!info.SaveProfile) return null;

            var originalCredentialId = info.CredentialId;
            string? credentialId = null;
            string? createdCredentialId = null;

            try
            {
                if (info.RememberCredential)
                {
                    var secret = new CredentialSecret(
                        Password: info.AuthMethod is SshAuthMethod.Password or
                            SshAuthMethod.KeyboardInteractive ? info.Password : "",
                        PrivateKeyPassphrase: info.AuthMethod == SshAuthMethod.PublicKey
                            ? info.Passphrase
                            : "",
                        RoutePassword: info.Route.Type is not ConnectionRouteType.Direct and
                            not ConnectionRouteType.ExternalProxyCommand
                            ? info.Route.Password
                            : "",
                        RoutePrivateKeyPassphrase: info.Route.Type == ConnectionRouteType.SshJump &&
                                                   info.Route.AuthMethod == SshAuthMethod.PublicKey
                            ? info.Route.Passphrase
                            : "");

                    if (secret.IsEmpty)
                    {
                        credentialId = null;
                    }
                    else
                    {
                        // Write a new encrypted record first. The profile reference is then
                        // swapped atomically in SQLite, so a failed save never corrupts the
                        // credential used by the existing profile.
                        credentialId = LocalCredentialVault.Default.Store(secret);
                        createdCredentialId = credentialId;
                    }
                }

                var profile = sutty.Command.HostProfileStore.Save(new sutty.Command.HostProfileDraft
                {
                    DisplayName = info.Title,
                    Host = info.Host,
                    Port = info.Port,
                    Username = info.Username,
                    AuthMethod = info.AuthMethod.ToString(),
                    PrivateKeyPath = info.AuthMethod == SshAuthMethod.PublicKey ? info.PrivateKeyPath : "",
                    Tags = info.Tags,
                    GroupName = info.GroupName,
                    Environment = info.Environment,
                    IsFavorite = info.IsFavorite,
                    CredentialId = credentialId,
                    Route = PersistRoute(info),
                    Tunnels = info.PortForwardings.Select(PersistTunnel).ToArray(),
                }, info.SavedHostId);

                info.SavedHostId = profile.Id;
                info.CredentialId = profile.CredentialId;

                if (!string.IsNullOrWhiteSpace(originalCredentialId) &&
                    !string.Equals(originalCredentialId, credentialId, StringComparison.Ordinal))
                {
                    try { LocalCredentialVault.Default.Delete(originalCredentialId); }
                    catch (Exception cleanupError) when (cleanupError is System.IO.IOException or
                                                         UnauthorizedAccessException or
                                                         System.Security.Cryptography.CryptographicException or
                                                         System.ComponentModel.Win32Exception or
                                                         ArgumentException)
                    {
                        Debug.WriteLine(
                            $"Superseded encrypted credential cleanup failed: {cleanupError.GetType().Name}");
                    }
                }
                return profile;
            }
            catch (Exception error) when (error is System.IO.IOException or
                                          UnauthorizedAccessException or
                                          System.Security.Cryptography.CryptographicException or
                                          System.ComponentModel.Win32Exception or
                                          Microsoft.Data.Sqlite.SqliteException or
                                          ArgumentException or InvalidOperationException)
            {
                if (!string.IsNullOrWhiteSpace(createdCredentialId))
                {
                    try { LocalCredentialVault.Default.Delete(createdCredentialId); }
                    catch (Exception cleanupError) when (cleanupError is System.IO.IOException or
                                                         UnauthorizedAccessException or
                                                         System.Security.Cryptography.CryptographicException or
                                                         System.ComponentModel.Win32Exception or
                                                         ArgumentException)
                    {
                        Debug.WriteLine(
                            $"Failed credential rollback cleanup: {cleanupError.GetType().Name}");
                    }
                }

                Debug.WriteLine($"Saved-host persistence failed: {error.GetType().Name}");
                var warning = new ContentDialog
                {
                    Title = Helpers.Loc.T("호스트 저장 실패", "Could not save host"),
                    Content = Helpers.Loc.T(
                        "저장 호스트 또는 자격증명을 기록하지 못했습니다. 이번 연결은 계속 진행합니다.",
                        "The saved host or credential could not be written. This connection attempt will continue."),
                    CloseButtonText = "OK",
                    XamlRoot = Content.XamlRoot,
                };
                await warning.ShowAsync();
                return null;
            }
        }

        private async Task<bool> ConfirmHighRiskConnectionFeaturesAsync(SshConnectionInfo info)
        {
            if (info.Route?.Type == ConnectionRouteType.ExternalProxyCommand)
            {
                string expandedCommand;
                try
                {
                    expandedCommand = ProxyCommandTemplate.Expand(
                        info.Route.Command,
                        info.Host,
                        info.Port,
                        info.Username);
                }
                catch (RoutePolicyViolationException error)
                {
                    var invalidDialog = new ContentDialog
                    {
                        Title = Helpers.Loc.T(
                            "ProxyCommand 안전성 검사 실패",
                            "ProxyCommand safety check failed"),
                        Content = error.Message,
                        CloseButtonText = "OK",
                        XamlRoot = Content.XamlRoot,
                    };
                    await invalidDialog.ShowAsync();
                    return false;
                }

                var commandWarning = new TextBlock
                {
                    Text = Helpers.Loc.T(
                        "ProxyCommand는 이 컴퓨터에서 임의 명령을 실행할 수 있습니다. 아래 최종 명령을 확인한 뒤 계속하세요.",
                        "ProxyCommand can run an arbitrary local command. Review the final command before continuing."),
                    Foreground = Helpers.ThemeResources.Brush(Root, "StatusRed"),
                    TextWrapping = TextWrapping.Wrap,
                };
                var commandPreview = new TextBox
                {
                    Text = expandedCommand,
                    IsReadOnly = true,
                    AcceptsReturn = false,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    TextWrapping = TextWrapping.Wrap,
                };
                var commandContent = new StackPanel { Spacing = 12 };
                commandContent.Children.Add(commandWarning);
                commandContent.Children.Add(commandPreview);
                var commandDialog = new ContentDialog
                {
                    Title = Helpers.Loc.T(
                        "ProxyCommand 실행 확인",
                        "Confirm ProxyCommand execution"),
                    Content = commandContent,
                    PrimaryButtonText = Helpers.Loc.T("확인 후 연결", "Review and connect"),
                    CloseButtonText = Helpers.Loc.T("취소", "Cancel"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = Content.XamlRoot,
                };
                if (await commandDialog.ShowAsync() != ContentDialogResult.Primary)
                    return false;
            }

            var externalBindings = (info.PortForwardings ?? [])
                .Where(rule => ForwardingExposurePolicy.IsExternalBind(rule.BindHost))
                .Select(rule => $"{rule.Type}: {rule.BindHost}:{rule.BindPort}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (externalBindings.Length == 0)
                return true;

            var forwardingWarning = new TextBlock
            {
                Text = Helpers.Loc.T(
                    "아래 포워딩은 루프백 전용이 아니므로 다른 장치에서 접근할 수 있습니다. 방화벽과 접근 정책을 확인한 경우에만 계속하세요.\n\n" +
                    string.Join(Environment.NewLine, externalBindings),
                    "The forwarding listeners below are not loopback-only and may be reachable from other devices. Continue only after reviewing firewall and access policy.\n\n" +
                    string.Join(Environment.NewLine, externalBindings)),
                Foreground = Helpers.ThemeResources.Brush(Root, "StatusRed"),
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                TextWrapping = TextWrapping.Wrap,
            };
            var forwardingDialog = new ContentDialog
            {
                Title = Helpers.Loc.T("외부 포트 노출 경고", "External port exposure warning"),
                Content = forwardingWarning,
                PrimaryButtonText = Helpers.Loc.T(
                    "위험을 이해하고 연결",
                    "I understand, connect"),
                CloseButtonText = Helpers.Loc.T("취소", "Cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            return await forwardingDialog.ShowAsync() == ContentDialogResult.Primary;
        }

        private static sutty.Command.HostRouteProfile PersistRoute(SshConnectionInfo info) => new()
        {
            Id = info.Route.Id,
            Type = info.Route.Type.ToString(),
            Host = info.Route.Host,
            Port = info.Route.Port,
            Username = info.Route.Username,
            AuthMethod = info.Route.AuthMethod.ToString(),
            PrivateKeyPath = info.Route.PrivateKeyPath,
            Command = info.Route.Command,
            ProxyDns = info.Route.ProxyDns,
            DisableDirect = info.RoutePolicy.DisableDirect,
        };

        private static sutty.Command.HostTunnelProfile PersistTunnel(
            SshPortForwardingRule tunnel) => new()
        {
            Type = tunnel.Type.ToString(),
            BindHost = tunnel.BindHost,
            BindPort = tunnel.BindPort,
            DestinationHost = tunnel.DestinationHost,
            DestinationPort = tunnel.DestinationPort,
        };

        private static ConnectionRoute RestoreRoute(
            sutty.Command.HostRouteProfile profile,
            CredentialSecret? credential)
        {
            var type = Enum.TryParse<ConnectionRouteType>(profile.Type, out var parsedType) &&
                       Enum.IsDefined(parsedType)
                ? parsedType
                : ConnectionRouteType.Direct;
            var auth = Enum.TryParse<SshAuthMethod>(profile.AuthMethod, out var parsedAuth) &&
                       Enum.IsDefined(parsedAuth)
                ? parsedAuth
                : SshAuthMethod.Password;
            return new ConnectionRoute
            {
                Id = profile.Id,
                Type = type,
                Host = profile.Host,
                Port = profile.Port,
                Username = profile.Username,
                Password = credential?.RoutePassword ?? "",
                AuthMethod = auth,
                PrivateKeyPath = profile.PrivateKeyPath,
                Passphrase = credential?.RoutePrivateKeyPassphrase ?? "",
                Command = profile.Command,
                ProxyDns = profile.ProxyDns,
            };
        }

        private static SshPortForwardingRule RestoreTunnel(
            sutty.Command.HostTunnelProfile profile)
        {
            var type = Enum.TryParse<SshPortForwardingType>(profile.Type, out var parsed) &&
                       Enum.IsDefined(parsed)
                ? parsed
                : SshPortForwardingType.Local;
            return new SshPortForwardingRule
            {
                Type = type,
                BindHost = profile.BindHost,
                BindPort = profile.BindPort,
                DestinationHost = profile.DestinationHost,
                DestinationPort = profile.DestinationPort,
            };
        }

        private Task<System.Collections.Generic.IReadOnlyList<string>?>
            PromptKeyboardInteractiveAsync(
                KeyboardInteractiveChallenge challenge,
                CancellationToken ct) => DispatchPromptToUiAsync(
                    () => ShowKeyboardInteractivePromptAsync(challenge, ct),
                    ct);

        private Task<TResult> DispatchPromptToUiAsync<TResult>(
            Func<Task<TResult>> prompt,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(prompt);
            if (DispatcherQueue.HasThreadAccess)
                return prompt();

            var completion = new TaskCompletionSource<TResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        completion.TrySetResult(await prompt());
                    }
                    catch (Exception error)
                    {
                        completion.TrySetException(error);
                    }
                }))
            {
                completion.TrySetException(new InvalidOperationException(
                    "The SSH prompt could not be dispatched to the UI."));
            }
            return completion.Task.WaitAsync(ct);
        }

        private async Task<System.Collections.Generic.IReadOnlyList<string>?>
            ShowKeyboardInteractivePromptAsync(
                KeyboardInteractiveChallenge challenge,
                CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (challenge.Prompts.Count is < 1 or > 16)
                throw new InvalidOperationException(
                    "The server supplied an invalid number of authentication prompts.");

            await _hostKeyPromptGate.WaitAsync(ct);
            try
            {
                var content = new StackPanel { Spacing = 9, MinWidth = 340 };
                if (!string.IsNullOrWhiteSpace(challenge.Instruction))
                {
                    content.Children.Add(new TextBlock
                    {
                        Text = challenge.Instruction,
                        Foreground = Helpers.ThemeResources.Brush(Root, "TextMuted"),
                        TextWrapping = TextWrapping.Wrap,
                    });
                }

                var readers = new System.Collections.Generic.List<Func<string>>();
                foreach (var prompt in challenge.Prompts)
                {
                    var request = string.IsNullOrWhiteSpace(prompt.Request)
                        ? Helpers.Loc.T("응답", "Response")
                        : prompt.Request[..Math.Min(prompt.Request.Length, 512)];
                    if (prompt.IsEchoed)
                    {
                        var input = new TextBox { Header = request, MinWidth = 340 };
                        content.Children.Add(input);
                        readers.Add(() => input.Text);
                    }
                    else
                    {
                        var input = new PasswordBox { Header = request, MinWidth = 340 };
                        content.Children.Add(input);
                        readers.Add(() => input.Password);
                    }
                }

                var dialog = new ContentDialog
                {
                    Title = Helpers.Loc.T("SSH 추가 인증", "Additional SSH authentication"),
                    Content = new ScrollViewer
                    {
                        MaxHeight = 480,
                        Content = content,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    },
                    PrimaryButtonText = Helpers.Loc.T("응답 전송", "Submit responses"),
                    CloseButtonText = Helpers.Loc.T("취소", "Cancel"),
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = Content.XamlRoot,
                };
                using var cancellation = ct.Register(() =>
                    DispatcherQueue.TryEnqueue(dialog.Hide));
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                    return null;
                ct.ThrowIfCancellationRequested();
                return readers.Select(read => read()).ToArray();
            }
            finally
            {
                _hostKeyPromptGate.Release();
            }
        }

        private async Task<HostKeyDecision> PromptUnknownHostKeyAsync(
            HostKeyVerification verification,
            CancellationToken ct)
        {
            await _hostKeyPromptGate.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();

                var details = new StackPanel { Spacing = 8 };
                details.Children.Add(new TextBlock
                {
                    Text = Helpers.Loc.T(
                        "이 서버의 공개 호스트키가 아직 저장되어 있지 않습니다. 아래 지문을 서버 관리자와 확인하세요.",
                        "This server's public host key is not saved yet. Verify the fingerprint with the server administrator."),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Helpers.ThemeResources.Brush(Root, "TextPrimary"),
                });
                details.Children.Add(new TextBlock
                {
                    Text = verification.Endpoint.Value,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = Helpers.ThemeResources.Brush(Root, "TextPrimary"),
                    IsTextSelectionEnabled = true,
                });
                details.Children.Add(new TextBlock
                {
                    Text = verification.PresentedKey.Algorithm,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    Foreground = Helpers.ThemeResources.Brush(Root, "TextMuted"),
                    IsTextSelectionEnabled = true,
                });
                details.Children.Add(new TextBlock
                {
                    Text = verification.PresentedKey.Sha256Fingerprint,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    Foreground = Helpers.ThemeResources.Brush(Root, "AccentTeal"),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                });

                var dialog = new ContentDialog
                {
                    Title = Helpers.Loc.T("알 수 없는 SSH 호스트", "Unknown SSH host"),
                    Content = details,
                    PrimaryButtonText = Helpers.Loc.T("신뢰하고 저장", "Trust and save"),
                    SecondaryButtonText = Helpers.Loc.T("이번만 연결", "Connect once"),
                    CloseButtonText = Helpers.Loc.T("취소", "Cancel"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = Content.XamlRoot,
                };

                return (await dialog.ShowAsync()) switch
                {
                    ContentDialogResult.Primary => HostKeyDecision.TrustAndSave,
                    ContentDialogResult.Secondary => HostKeyDecision.TrustOnce,
                    _ => HostKeyDecision.Cancel,
                };
            }
            finally
            {
                _hostKeyPromptGate.Release();
            }
        }

        private async Task<HostKeyRotationDecision> PromptChangedHostKeyRotationAsync(
            HostKeyVerification verification,
            CancellationToken ct)
        {
            await _hostKeyPromptGate.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                if (verification.TrustedKey is null)
                    return HostKeyRotationDecision.Cancelled;

                var confirm = new CheckBox
                {
                    Content = Helpers.Loc.T(
                        "서버 관리자와 두 지문을 직접 확인했습니다.",
                        "I independently verified both fingerprints with the server administrator."),
                    Foreground = Helpers.ThemeResources.Brush(Root, "TextPrimary"),
                };
                var reason = new TextBox
                {
                    Header = Helpers.Loc.T("변경 사유", "Reason for rotation"),
                    PlaceholderText = Helpers.Loc.T(
                        "예: 계획된 서버 키 교체",
                        "For example: planned server-key rotation"),
                    MaxLength = HostKeyRotationReason.MaximumLength,
                };
                var details = new StackPanel { Spacing = 10 };
                details.Children.Add(new TextBlock
                {
                    Text = Helpers.Loc.T(
                        "경고: 저장된 호스트 키와 서버가 제시한 키가 다릅니다. 예상한 교체인지 별도 경로로 확인하기 전에는 계속하지 마세요.",
                        "Warning: the server key differs from the saved key. Do not continue until you verify the change through an independent channel."),
                    Foreground = Helpers.ThemeResources.Brush(Root, "StatusRed"),
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                });
                details.Children.Add(new TextBlock
                {
                    Text = verification.Endpoint.Value,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    Foreground = Helpers.ThemeResources.Brush(Root, "TextPrimary"),
                    IsTextSelectionEnabled = true,
                });
                details.Children.Add(CreateHostKeyComparisonBlock(
                    Helpers.Loc.T("기존 키", "Saved key"),
                    verification.TrustedKey,
                    "TextMuted"));
                details.Children.Add(CreateHostKeyComparisonBlock(
                    Helpers.Loc.T("새 키", "Presented key"),
                    verification.PresentedKey,
                    "StatusAmber"));
                details.Children.Add(confirm);
                details.Children.Add(reason);
                details.Children.Add(new TextBlock
                {
                    Text = Helpers.Loc.T(
                        "변경 사유는 이 PC의 로컬 보안 활동 기록에 평문으로 저장됩니다. 비밀번호·OTP 등 비밀정보를 입력하지 마세요.",
                        "The reason is stored in plaintext in this PC's local security activity. Do not enter passwords, OTPs, or other secrets."),
                    FontSize = 10,
                    Foreground = Helpers.ThemeResources.Brush(Root, "TextMuted"),
                    TextWrapping = TextWrapping.Wrap,
                });

                var dialog = new ContentDialog
                {
                    Title = Helpers.Loc.T("SSH 호스트 키 변경", "SSH host key changed"),
                    Content = new ScrollViewer
                    {
                        MaxHeight = 520,
                        Content = details,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    },
                    PrimaryButtonText = Helpers.Loc.T("확인한 키로 교체", "Rotate verified key"),
                    CloseButtonText = Helpers.Loc.T("취소", "Cancel"),
                    DefaultButton = ContentDialogButton.Close,
                    IsPrimaryButtonEnabled = false,
                    XamlRoot = Content.XamlRoot,
                };

                void UpdateConfirmationState()
                {
                    dialog.IsPrimaryButtonEnabled =
                        confirm.IsChecked == true &&
                        HostKeyRotationReason.TryNormalize(reason.Text, out _);
                }

                confirm.Checked += (_, _) => UpdateConfirmationState();
                confirm.Unchecked += (_, _) => UpdateConfirmationState();
                reason.TextChanged += (_, _) => UpdateConfirmationState();
                using var cancellation = ct.Register(() =>
                    DispatcherQueue.TryEnqueue(dialog.Hide));

                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                    return HostKeyRotationDecision.Cancelled;

                ct.ThrowIfCancellationRequested();
                if (!HostKeyRotationReason.TryNormalize(reason.Text, out var normalizedReason))
                    return HostKeyRotationDecision.Cancelled;
                return new HostKeyRotationDecision(true, normalizedReason);
            }
            finally
            {
                _hostKeyPromptGate.Release();
            }
        }

        private Border CreateHostKeyComparisonBlock(
            string label,
            HostKeyData key,
            string fingerprintBrush)
        {
            var content = new StackPanel { Spacing = 3 };
            content.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 10.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Helpers.ThemeResources.Brush(Root, "TextMuted"),
            });
            content.Children.Add(new TextBlock
            {
                Text = key.Algorithm,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                Foreground = Helpers.ThemeResources.Brush(Root, "TextPrimary"),
                IsTextSelectionEnabled = true,
            });
            content.Children.Add(new TextBlock
            {
                Text = key.Sha256Fingerprint,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                Foreground = Helpers.ThemeResources.Brush(Root, fingerprintBrush),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            });

            return new Border
            {
                Padding = new Thickness(10),
                Background = Helpers.ThemeResources.Brush(Root, "CardBg"),
                BorderBrush = Helpers.ThemeResources.Brush(Root, "CardBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Child = content,
            };
        }

        private void TitleTabs_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (_suppressTabActivation)
                return;
            ActivateSelectedSession();
            UpdateSessionArea();
            QueueWorkspaceSnapshot();
        }

        private async Task OfferSaveAfterSuccessAsync(SshConnectionInfo info)
        {
            if (info.SaveProfile || !string.IsNullOrWhiteSpace(info.SavedHostId))
                return;

            var nameBox = new TextBox
            {
                Header = Helpers.Loc.T("표시 이름", "Display name"),
                Text = info.Title,
                PlaceholderText = info.Host,
            };
            var rememberSecret = new CheckBox
            {
                Content = Helpers.Loc.T(
                    "자격증명을 Windows 암호화 저장소에 저장",
                    "Save credential in the Windows encrypted vault"),
                IsChecked = false,
                Visibility = info.AuthMethod is SshAuthMethod.Password or
                    SshAuthMethod.KeyboardInteractive or SshAuthMethod.PublicKey
                    ? Visibility.Visible
                    : Visibility.Collapsed,
            };
            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(new TextBlock
            {
                Text = Helpers.Loc.T(
                    "연결에 성공했습니다. 다음 연결을 위해 이 호스트를 저장할 수 있습니다.",
                    "Connection succeeded. You can save this host for next time."),
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(nameBox);
            content.Children.Add(rememberSecret);

            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = Helpers.Loc.T("호스트 저장", "Save host"),
                Content = content,
                PrimaryButtonText = Helpers.Loc.T("저장", "Save"),
                CloseButtonText = Helpers.Loc.T("지금은 안 함", "Not now"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            info.DisplayName = string.IsNullOrWhiteSpace(nameBox.Text)
                ? info.Title
                : nameBox.Text.Trim();
            info.SaveProfile = true;
            info.RememberCredential = rememberSecret.IsChecked == true;
        }

        private void SessionTab_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is not TabViewItem tab || IsTapFromButton(e.OriginalSource, tab))
                return;

            TitleTabs.SelectedItem = tab;
            ActivateSelectedSession();
            UpdateSessionArea();
        }

        private static bool IsTapFromButton(object? originalSource, TabViewItem tab)
        {
            for (var current = originalSource as DependencyObject;
                 current is not null && !ReferenceEquals(current, tab);
                 current = VisualTreeHelper.GetParent(current))
            {
                if (current is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase)
                    return true;
            }

            return false;
        }

        private void ActivateSelectedSession()
        {
            if (TitleTabs.SelectedItem is not TabViewItem selected)
            {
                NavigateGlobal(AppGlobalPage.Home);
                return;
            }

            ClearCachedHomeSecrets();
            _isMultiView = false;
            MultiGrid.Visibility = Visibility.Collapsed;
            HideDetailsPane();
            LeftNav.SelectedItem = null;
            if (selected.DataContext is SessionView sessionView &&
                _sessionWorkspaces.TryGetValue(sessionView, out var workspace))
            {
                _navigation.ActivateSession(workspace.ViewModel);
                SessionHost.Content = workspace;
            }
            else
            {
                _navigation.ActivateSession(null);
                SessionHost.Content = selected.DataContext as FrameworkElement;
            }
        }

        private void ActivateWorkspace(
            SessionWorkspaceView workspace,
            SessionWorkspaceSection section)
        {
            ArgumentNullException.ThrowIfNull(workspace);
            ClearCachedHomeSecrets();
            _isMultiView = false;
            MultiGrid.Visibility = Visibility.Collapsed;
            HideDetailsPane();
            LeftNav.SelectedItem = null;
            workspace.NavigateTo(section);
            _navigation.NavigateWorkspace(workspace.ViewModel, section);
            SessionHost.Content = workspace;
            UpdateSessionArea();
        }

        private void ClearCachedHomeSecrets()
        {
            if (_shellState.Mode == AppShellMode.Global &&
                _shellState.GlobalPage == AppGlobalPage.Home)
            {
                _homeDashboard?.ClearTransientSecrets();
            }
        }

        private void UpdateSessionArea()
        {
            if (_globalPages.TryGetValue(AppGlobalPage.Commands, out var commandPage) &&
                commandPage is CommandsDashboardPanel commands)
            {
                commands.SetPowerToolsAvailable(GetOpenTerminalViews().Count >= 2);
            }
            EmptyTabHeader.Visibility = TitleTabs.TabItems.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            var showGlobal = !_isMultiView && _shellState.Mode == AppShellMode.Global;
            var showSession = !_isMultiView && _shellState.Mode == AppShellMode.Session;
            GlobalPageHost.Visibility = showGlobal ? Visibility.Visible : Visibility.Collapsed;
            SessionHost.Visibility = showSession ? Visibility.Visible : Visibility.Collapsed;
            MultiGrid.Visibility = _isMultiView ? Visibility.Visible : Visibility.Collapsed;
            NoSessionState.Visibility = showSession && TitleTabs.SelectedItem is null
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (showSession && TitleTabs.SelectedItem is TabViewItem selected)
            {
                SessionHost.Content = selected.DataContext is SessionView sessionView &&
                    _sessionWorkspaces.TryGetValue(sessionView, out var workspace)
                    ? workspace
                    : selected.DataContext as FrameworkElement;
            }

            // Multi 그리드가 보이는 중이면 열린 세션 목록으로 갱신
            if (_isMultiView)
                MultiGrid.SetSessions(GetOpenTerminalViews());

        }

        private System.Collections.Generic.List<SessionView> GetOpenSessionViews()
        {
            var views = new System.Collections.Generic.List<SessionView>();
            foreach (var item in TitleTabs.TabItems)
            {
                if (item is TabViewItem tab && tab.DataContext is SessionView view)
                    views.Add(view);
            }
            return views;
        }

        private System.Collections.Generic.List<FrameworkElement> GetOpenTerminalViews()
        {
            var views = new System.Collections.Generic.List<FrameworkElement>();
            foreach (var item in TitleTabs.TabItems)
            {
                if (item is not TabViewItem tab)
                    continue;
                if (tab.DataContext is SessionView sessionView)
                    views.Add(sessionView);
                else if (tab.DataContext is LocalTerminalView localView)
                    views.Add(localView);
            }
            return views;
        }

        private System.Collections.Generic.List<LocalTerminalView> GetOpenLocalTerminalViews()
        {
            var views = new System.Collections.Generic.List<LocalTerminalView>();
            foreach (var item in TitleTabs.TabItems)
            {
                if (item is TabViewItem tab && tab.DataContext is LocalTerminalView view)
                    views.Add(view);
            }
            return views;
        }

        // X 클릭 → SSH·SFTP 채널을 모두 끊고 탭을 닫는다 (확인 없이 즉시)
        private async void TitleTabs_TabCloseRequested(
            TabView sender,
            TabViewTabCloseRequestedEventArgs args)
        {
            if (args.Tab.DataContext is SessionView closingSession)
            {
                RememberFailedSupportContext(
                    closingSession.Session,
                    allowUnsequencedOverwrite: false);
                if (_sessionWorkspaces.TryGetValue(closingSession, out var closingWorkspace))
                    closingWorkspace.CancelTransfers(userInitiated: true);
            }

            var preserveGlobalPage = !_isMultiView &&
                _shellState.Mode == AppShellMode.Global;
            var preserveMultiView = _isMultiView;
            _suppressTabActivation = true;
            try
            {
                sender.TabItems.Remove(args.Tab);
                if (!preserveGlobalPage && !preserveMultiView &&
                    sender.TabItems.Count > 0 &&
                    (sender.SelectedItem is null ||
                     !sender.TabItems.Contains(sender.SelectedItem)))
                {
                    sender.SelectedItem = sender.TabItems[0];
                }
            }
            finally
            {
                _suppressTabActivation = false;
            }

            if (preserveMultiView)
            {
                var remainingTerminals = GetOpenTerminalViews();
                if (remainingTerminals.Count >= 2)
                {
                    MultiGrid.SetSessions(remainingTerminals);
                }
                else
                {
                    // Multi is available only while at least two terminal sessions exist.
                    SelectNavigationItem("Commands");
                }
            }
            else if (!preserveGlobalPage && sender.TabItems.Count == 0)
            {
                SelectNavigationItem("Home");
            }
            else if (!preserveGlobalPage)
            {
                ActivateSelectedSession();
            }
            UpdateSessionArea();
            QueueWorkspaceSnapshot();

            // 탭은 먼저 닫고, 연결 정리는 백그라운드로 (UI가 안 막히게)
            if (args.Tab.DataContext is SessionView view)
            {
                view.AppShortcutRequested -= TerminalView_AppShortcutRequested;
                if (_sessionWorkspaces.Remove(view, out var workspace))
                {
                    _navigation.ForgetWorkspace(workspace.ViewModel);
                    await workspace.DetachAsync(userInitiated: true);
                }
                await _sessions.CloseAsync(view.Session);
            }
            else if (args.Tab.DataContext is LocalTerminalView localView)
            {
                localView.AppShortcutRequested -= TerminalView_AppShortcutRequested;
                await localView.CloseAsync();
            }
        }

        // ── 설정 창 ──

        private void OpenSettingWindow()
        {
            SelectNavigationItem("Settings");
        }

        private void OpenTroubleshooting()
        {
            SelectNavigationItem("Settings");
            CreateEmbeddedSettingsPanel().NavigateToSection(
                "Troubleshooting",
                showAdvancedLogs: true);
        }

        private IReadOnlyList<SupportBundleTarget> CreateSupportBundleTargets()
        {
            var targets = new List<SupportBundleTarget>();
            var openSessions = GetOpenSessionViews()
                .Select(view => view.Session)
                .ToList();
            if (ActiveSession is { } activeSession)
            {
                openSessions.Remove(activeSession);
                openSessions.Insert(0, activeSession);
            }
            else if (openSessions.Count > 1)
            {
                var mostRecentSession = openSessions[^1];
                openSessions.RemoveAt(openSessions.Count - 1);
                openSessions.Insert(0, mostRecentSession);
            }

            foreach (var session in openSessions)
                targets.Add(BuildSupportBundleTarget(session));

            if (_lastFailedSupportContext is { } retained &&
                !targets.Any(target => string.Equals(
                    target.CorrelationId,
                    retained.CorrelationId,
                    StringComparison.Ordinal)))
            {
                var preview = PreviewSupportBundleDiagnostics(
                    retained.CorrelationId,
                    retained.StableErrorCode,
                    retained.FallbackFailureStage);
                if (preview.StableErrorCode == ConnectionDiagnosticErrorCodes.None)
                {
                    _lastFailedSupportContext = null;
                    _lastFailedSupportOccurredUtc = DateTimeOffset.MinValue;
                    _lastFailedSupportSequence = 0;
                    return targets;
                }

                var refreshedContext = retained with
                {
                    StableErrorCode = preview.StableErrorCode,
                    FallbackFailureStage = preview.FailureStage ?? retained.FallbackFailureStage,
                    ExpectedDiagnosticSnapshotSha256 = preview.SnapshotSha256,
                };
                _lastFailedSupportContext = refreshedContext;
                var retainedTitle = Helpers.Loc.T(
                    "마지막 실패 연결 시도",
                    "Last failed connection attempt");
                var endpoint = Helpers.Loc.T(
                    "보존하지 않음 (탭 닫힘 또는 연결 전 실패)",
                    "Not retained (closed tab or preflight failure)");
                targets.Add(new SupportBundleTarget(
                    retainedTitle,
                    retainedTitle,
                    endpoint,
                    FormatSupportBundleStatus(preview),
                    preview.StableErrorCode,
                    refreshedContext.CorrelationId,
                    preview.EventCount,
                    refreshedContext));
            }

            return targets;
        }

        private SupportBundleTarget BuildSupportBundleTarget(ISshSession session)
        {
            var fallbackDiagnostic = SelectFallbackDiagnostic(session);
            var preview = PreviewSupportBundleDiagnostics(
                session.CorrelationContext.CorrelationId,
                fallbackDiagnostic?.ErrorCode ?? ConnectionDiagnosticErrorCodes.None,
                fallbackDiagnostic?.Stage);
            var statusText = preview.FailureStage is null
                ? LocalizeSessionState(session.State)
                : FormatSupportBundleStatus(preview);
            var endpoint = HostEndpointIdentity.Create(
                session.Info.Host,
                session.Info.Port).Value;
            var sessionTitle = string.IsNullOrWhiteSpace(session.Info.Title)
                ? Helpers.Loc.T("SSH 연결", "SSH connection")
                : session.Info.Title;
            var context = BuildSupportBundleContext(
                session.CorrelationContext.RouteType,
                session.Info.AuthMethod,
                preview.StableErrorCode,
                session.CorrelationContext.CorrelationId,
                preview.FailureStage,
                preview.SnapshotSha256);

            return new SupportBundleTarget(
                $"{sessionTitle} — {endpoint}",
                sessionTitle,
                endpoint,
                statusText,
                preview.StableErrorCode,
                session.CorrelationContext.CorrelationId,
                preview.EventCount,
                context);
        }

        private void RememberFailedSupportContext(
            ISshSession session,
            string? fallbackErrorCode = null,
            bool allowUnsequencedOverwrite = true)
        {
            var fallbackDiagnostic = SelectFallbackDiagnostic(session);
            RememberFailedSupportContext(
                session.CorrelationContext.RouteType,
                session.Info.AuthMethod,
                fallbackErrorCode ?? fallbackDiagnostic?.ErrorCode ??
                    ConnectionDiagnosticErrorCodes.None,
                fallbackDiagnostic?.Stage,
                session.CorrelationContext.CorrelationId,
                allowUnsequencedOverwrite);
        }

        private void RememberFailedSupportContext(
            ConnectionRouteType routeType,
            SshAuthMethod authenticationType,
            string fallbackErrorCode,
            ConnectionDiagnosticStage? fallbackFailureStage,
            string correlationId,
            bool allowUnsequencedOverwrite)
        {
            var preview = PreviewSupportBundleDiagnostics(
                correlationId,
                fallbackErrorCode,
                fallbackFailureStage);
            if (preview.StableErrorCode == ConnectionDiagnosticErrorCodes.None)
                return;

            var occurredUtc = preview.FailureTimestampUtc ?? DateTimeOffset.UtcNow;
            var sequence = preview.FailureSequence ?? 0;
            if (sequence > 0 && _lastFailedSupportSequence > 0)
            {
                if (sequence <= _lastFailedSupportSequence)
                    return;
            }
            else
            {
                if (!allowUnsequencedOverwrite &&
                    _lastFailedSupportContext is not null &&
                    !string.Equals(
                        _lastFailedSupportContext.CorrelationId,
                        correlationId,
                        StringComparison.Ordinal))
                {
                    return;
                }
                if (occurredUtc < _lastFailedSupportOccurredUtc ||
                    (occurredUtc == _lastFailedSupportOccurredUtc &&
                     sequence <= _lastFailedSupportSequence))
                {
                    return;
                }
            }

            _lastFailedSupportContext = BuildSupportBundleContext(
                routeType,
                authenticationType,
                preview.StableErrorCode,
                correlationId,
                preview.FailureStage ?? fallbackFailureStage,
                preview.SnapshotSha256);
            _lastFailedSupportOccurredUtc = occurredUtc;
            _lastFailedSupportSequence = sequence;
        }

        private static ConnectionDiagnosticResult? SelectFallbackDiagnostic(
            ISshSession session)
        {
            static bool IsFailure(ConnectionDiagnosticResult? result) => result?.Status is
                ConnectionDiagnosticStatus.Failed or ConnectionDiagnosticStatus.Cancelled;

            if (session.State == SessionState.Failed && IsFailure(session.LastDiagnostic))
                return session.LastDiagnostic;
            if (session.SftpState == SftpConnectionState.Unavailable &&
                IsFailure(session.LastSftpDiagnostic))
            {
                return session.LastSftpDiagnostic;
            }
            if (IsFailure(session.LastDiagnostic))
                return session.LastDiagnostic;
            return IsFailure(session.LastSftpDiagnostic)
                ? session.LastSftpDiagnostic
                : null;
        }

        private static SupportBundleDiagnosticPreview PreviewSupportBundleDiagnostics(
            string correlationId,
            string fallbackErrorCode,
            ConnectionDiagnosticStage? fallbackFailureStage)
        {
            var service = new SupportBundleService(ConnectionDiagnosticEventStore.Shared);
            var eventPreview = service.Preview(correlationId);
            if (eventPreview.StableErrorCode != ConnectionDiagnosticErrorCodes.None ||
                string.IsNullOrWhiteSpace(fallbackErrorCode) ||
                fallbackErrorCode == ConnectionDiagnosticErrorCodes.None)
            {
                return eventPreview;
            }

            try
            {
                return service.Preview(
                    correlationId,
                    fallbackErrorCode,
                    fallbackFailureStage);
            }
            catch (SupportBundleDiagnosticCodeMismatchException)
            {
                // A new failure can be appended between the event-only preview and
                // the stage-aware fallback preview. Re-snapshot without the stale
                // fallback so target enumeration remains safe and event-authoritative.
                return service.Preview(correlationId);
            }
        }

        private static string FormatSupportBundleStatus(
            SupportBundleDiagnosticPreview preview) => preview.FailureStage is { } stage &&
                                                        preview.FailureStatus is { } status
            ? $"{LocalizeDiagnosticStage(stage)} · {LocalizeDiagnosticStatus(status)}"
            : Helpers.Loc.T("실패 진단 보존됨", "Failure diagnostics retained");

        private static string LocalizeSessionState(SessionState state) => state switch
        {
            SessionState.Idle => Helpers.Loc.T("대기 중", "Idle"),
            SessionState.Connecting => Helpers.Loc.T("연결 중", "Connecting"),
            SessionState.Connected => Helpers.Loc.T("연결됨", "Connected"),
            SessionState.Disconnecting => Helpers.Loc.T("연결 종료 중", "Disconnecting"),
            SessionState.Disconnected => Helpers.Loc.T("연결 종료됨", "Disconnected"),
            SessionState.Failed => Helpers.Loc.T("실패", "Failed"),
            _ => state.ToString(),
        };

        private static SupportBundleContext BuildSupportBundleContext(
            ConnectionRouteType routeType,
            SshAuthMethod authenticationType,
            string stableErrorCode,
            string correlationId,
            ConnectionDiagnosticStage? fallbackFailureStage,
            string expectedDiagnosticSnapshotSha256)
        {
            var build = !string.IsNullOrWhiteSpace(Helpers.AppReleaseInfo.Commit)
                ? Helpers.AppReleaseInfo.Commit
                : !string.IsNullOrWhiteSpace(Helpers.AppReleaseInfo.BuildMetadata)
                    ? Helpers.AppReleaseInfo.BuildMetadata
                    : "local";
            return new SupportBundleContext(
                Helpers.AppReleaseInfo.Version,
                build,
                Environment.OSVersion.Version.ToString(),
                RuntimeInformation.ProcessArchitecture,
                routeType,
                authenticationType,
                stableErrorCode,
                correlationId,
                SettingsService.SchemaVersion,
                fallbackFailureStage,
                expectedDiagnosticSnapshotSha256);
        }

        private void ApplySettingsChanges(SettingChangeKind changes)
        {
            if (changes.HasFlag(SettingChangeKind.TerminalAppearance) ||
                changes.HasFlag(SettingChangeKind.TerminalMode) ||
                changes.HasFlag(SettingChangeKind.TerminalFeatures))
            {
                foreach (var workspace in _sessionWorkspaces.Values)
                    workspace.ReapplyTerminalSettings();

                if (changes.HasFlag(SettingChangeKind.TerminalAppearance))
                {
                    foreach (var localView in GetOpenLocalTerminalViews())
                        localView.ApplyTerminalSettings();
                }
            }

            if (changes.HasFlag(SettingChangeKind.Language))
            {
                Bindings.Update();
                foreach (var workspace in _sessionWorkspaces.Values)
                    workspace.RefreshLanguage();
                foreach (var localView in GetOpenLocalTerminalViews())
                    localView.RefreshLanguage();
                MultiGrid.RefreshLanguage();
                _homeDashboard?.RefreshLanguage();
                foreach (var hosts in _hostPanels)
                    hosts.RefreshLanguage();
                if (_globalPages.TryGetValue(AppGlobalPage.Transfers, out var transferPage) &&
                    transferPage is Border { Child: TransferCenterPanel transfers })
                {
                    transfers.RefreshLanguage();
                }
                if (_globalPages.TryGetValue(AppGlobalPage.Commands, out var commandsPage) &&
                    commandsPage is CommandsDashboardPanel commands)
                {
                    commands.RefreshLanguage();
                }
                _multiCommandPanel?.RefreshLanguage();

                // 입력 중인 Home 폼이나 검색 상태를 잃지 않고 현재 패널의
                // one-time localization binding만 다시 평가한다.
                switch (RightPanel.Content)
                {
                    case HomePanel localizedHome: localizedHome.RefreshLanguage(); break;
                    case HostListPanel localizedHistory: localizedHistory.RefreshLanguage(); break;
                    case FileTreePanel localizedFiles: localizedFiles.RefreshLanguage(); break;
                    case CommandPanel localizedCommands: localizedCommands.RefreshLanguage(); break;
                    case MultiCommandPanel localizedMulti: localizedMulti.RefreshLanguage(); break;
                    case ConnectionLogPanel localizedLogs: localizedLogs.RefreshLanguage(); break;
                }
            }

            var applyDefaultPort = (changes & SettingChangeKind.ConnectionPort) != 0;
            var applyDefaultKeepAlive = (changes & SettingChangeKind.ConnectionKeepAlive) != 0;
            if (applyDefaultPort || applyDefaultKeepAlive)
                _homeDashboard?.ApplyConnectionDefaults(applyDefaultPort, applyDefaultKeepAlive);

            if (changes.HasFlag(SettingChangeKind.History) ||
                changes.HasFlag(SettingChangeKind.HostProfiles))
            {
                foreach (var hosts in _hostPanels)
                    hosts.RefreshFromStore();
            }

            if (changes.HasFlag(SettingChangeKind.Window))
                ApplyWindowSizesFromSettings();

            if (changes.HasFlag(SettingChangeKind.Workspace))
            {
                if (SettingsService.Current.RestoreWorkspaceOnStartup)
                    SaveWorkspaceSnapshotNow();
                else
                {
                    _workspaceSaveTimer?.Stop();
                    WorkspaceStateStore.Clear();
                }
            }
        }

        // 설정 패널에서 저장한 창 크기 숫자를 즉시 반영
        private void ApplyWindowSizesFromSettings()
        {
            var s = SettingsService.Current;

            if (s.MainWindowWidth > 0 && s.MainWindowHeight > 0)
                AppWindow.ResizeClient(new SizeInt32(s.MainWindowWidth, s.MainWindowHeight));

            // Right details remains collapsed by default; remember a requested width and
            // apply it only while an explicit details surface is open.
            if (s.RightPanelWidth > 0)
            {
                _detailsPaneWidth = Math.Clamp(s.RightPanelWidth, 300, 800);
                if (RightPanelHost.Visibility == Visibility.Visible)
                    RightPanelColumn.Width = new GridLength(_detailsPaneWidth);
            }
        }
    }
}
