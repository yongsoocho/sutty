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
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        private readonly SemaphoreSlim _hostKeyPromptGate = new(1, 1);
        private Window? _settingWindow;
        private FileTreePanel? _fileTreePanel;
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
                _settingWindow?.Close();
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
            if (request.Action == sutty.Command.SuttyLaunchAction.ShowHelp)
            {
                await ShowLaunchDialogAsync(
                    Helpers.Loc.T("Sutty 명령줄", "Sutty command line"),
                    Helpers.Loc.T(
                        "저장 Host 열기:\n\nsutty.exe --host <저장 Host ID 또는 정확한 이름>\n\n비밀번호와 개인키 암호는 명령줄 인자로 받지 않습니다.",
                        "Open a Saved Host:\n\nsutty.exe --host <Saved Host ID or exact name>\n\nPasswords and private-key passphrases are not accepted as command-line arguments."));
                return;
            }

            if (request.Action == sutty.Command.SuttyLaunchAction.Invalid)
            {
                await ShowLaunchDialogAsync(
                    Helpers.Loc.T("명령줄 확인", "Check command line"),
                    Helpers.Loc.T(
                        "지원하지 않는 실행 인자입니다.\n\n사용법: sutty.exe --host <저장 Host ID 또는 정확한 이름>",
                        "The launch arguments are unsupported.\n\nUsage: sutty.exe --host <Saved Host ID or exact name>"));
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
                RightPanelColumn.Width = new GridLength(Math.Clamp(saved, 300, 800));

            _panelWidthSaveTimer = DispatcherQueue.CreateTimer();
            _panelWidthSaveTimer.Interval = TimeSpan.FromMilliseconds(600);
            _panelWidthSaveTimer.IsRepeating = false;
            _panelWidthSaveTimer.Tick += (_, _) =>
            {
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
            if (_panelWidthSaveTimer is { IsRunning: true })
            {
                _panelWidthSaveTimer.Stop();
                SettingsService.Current.RightPanelWidth = (int)RightPanelColumn.ActualWidth;
                SettingsService.Save();
            }
        }

        // ── 테마 (전환 UI는 Setting > Appearance에 있음) ──

        private void ApplyTheme(string theme)
        {
            Helpers.ThemeManager.Apply(theme, Root);
            var preset = Helpers.ThemeManager.Find(theme);
            ApplyTitleBarColors(this, preset);

            // 설정 창이 열려 있으면 같이 바꿔 준다
            if (_settingWindow?.Content is FrameworkElement settingRoot)
            {
                settingRoot.RequestedTheme = Root.RequestedTheme;
                ApplyTitleBarColors(_settingWindow, preset);
            }
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
            if (args.InvokedItemContainer is NavigationViewItem item && item.Tag is "Setting")
            {
                OpenSettingWindow();
            }
        }

        private void LeftNav_SelectionChanged(
            NavigationView sender,
            NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is NavigationViewItem item)
            {
                RightPanel.Content = item.Tag switch
                {
                    "Home" => CreateHomePanel(),
                    "Search" => CreateHostListPanel(),
                    "Folder" => CreateFileTreePanel(),
                    "Command" => CreateCommandPanel(),
                    "Multi" => CreateMultiPanel(),
                    "Logs" => new ConnectionLogPanel(),
                    _ => null
                };

                // Multi에서는 메인 영역이 4×4 세션 그리드로 바뀐다
                _isMultiView = item.Tag as string == "Multi";
                MultiGrid.Visibility = _isMultiView ? Visibility.Visible : Visibility.Collapsed;
                UpdateSessionArea();
            }
        }

        private HomePanel CreateHomePanel()
        {
            var panel = new HomePanel
            {
                OwnerWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this)
            };
            panel.ConnectRequested += async (_, info) => await OpenSessionTabAsync(info);
            return panel;
        }

        private HostListPanel CreateHostListPanel()
        {
            var panel = new HostListPanel();
            panel.ConnectRequested += async (_, host) => await OpenHistoryDraftAsync(host);
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
                    EnterpriseMode = routeProfile.EnterpriseMode,
                    DisableDirect = routeProfile.EnterpriseMode,
                },
                PortForwardings = tunnelProfiles.Select(RestoreTunnel).ToList(),
            };

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

        private FileTreePanel CreateFileTreePanel()
        {
            if (_fileTreePanel is null)
            {
                _fileTreePanel = new FileTreePanel
                {
                    OwnerWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this),
                };
                _fileTreePanel.OpenTerminalHereRequested += FileTree_OpenTerminalHereRequested;
            }

            var panel = _fileTreePanel;
            panel.OwnerWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            panel.RefreshLanguage();
            // Reconcile the cached panel immediately so stale nodes and transfers from a
            // previously active server are invalidated before the control is reattached.
            _ = LoadFileTreeForActiveSessionAsync(panel);
            // The first call can happen while the cached control is still unloaded. Run a
            // second pass after the selection change attaches it so cwd navigation is applied.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (ReferenceEquals(panel, RightPanel.Content))
                    _ = LoadFileTreeForActiveSessionAsync(panel);
            });
            return panel;
        }

        private async void FileTree_OpenTerminalHereRequested(object? sender, string remotePath)
        {
            var view = ActiveSessionView;
            if (view is null)
            {
                await ShowOpenTerminalFailedAsync();
                return;
            }

            var allowExistingInput = false;
            if (view.HasOpenInteractiveTerminal)
            {
                allowExistingInput = await ConfirmTerminalInputAsync(remotePath);
                if (!allowExistingInput)
                    return;
            }

            if (await view.OpenDirectoryInTerminalAsync(remotePath, allowExistingInput))
                return;

            // The PTY may have opened while the remote path was being validated. Never
            // inject into that newly-existing foreground program without a fresh prompt.
            if (!allowExistingInput && view.HasOpenInteractiveTerminal)
            {
                if (!await ConfirmTerminalInputAsync(remotePath))
                    return;
                if (await view.OpenDirectoryInTerminalAsync(remotePath, true))
                    return;
            }

            await ShowOpenTerminalFailedAsync();
        }

        private async Task<bool> ConfirmTerminalInputAsync(string remotePath)
        {
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock
            {
                Text = Helpers.Loc.T(
                    "터미널에서 이미 프로그램이 실행 중일 수 있습니다. 아래 명령을 현재 PTY 입력으로 보내시겠습니까?",
                    "A program may already be running in the terminal. Send this command to the current PTY input?"),
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(new TextBlock
            {
                Text = $"cd {remotePath}",
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                Foreground = Helpers.ThemeResources.Brush(Root, "AccentTeal"),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            });

            var dialog = new ContentDialog
            {
                Title = Helpers.Loc.T("기존 터미널 입력 확인", "Confirm terminal input"),
                Content = content,
                PrimaryButtonText = Helpers.Loc.T("cd 명령 보내기", "Send cd command"),
                CloseButtonText = Helpers.Loc.T("취소", "Cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

        private async Task ShowOpenTerminalFailedAsync()
        {
            var dialog = new ContentDialog
            {
                Title = Helpers.Loc.T("터미널에서 열 수 없음", "Could not open in terminal"),
                Content = Helpers.Loc.T(
                    "활성 SSH 세션과 원격 경로를 확인하세요.",
                    "Check the active SSH session and remote path."),
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot,
            };
            await dialog.ShowAsync();
        }

        private async Task LoadFileTreeForActiveSessionAsync(FileTreePanel panel)
        {
            var view = ActiveSessionView;
            await panel.LoadAsync(view?.Session);
            if (view is null || !ReferenceEquals(view, ActiveSessionView) ||
                !ReferenceEquals(panel, RightPanel.Content))
                return;

            if (view.WorkingDirectory.StartsWith("/", StringComparison.Ordinal))
                await panel.NavigateToPathAsync(view.WorkingDirectory);
        }

        private void SessionView_WorkingDirectoryChanged(object? sender, string remotePath)
        {
            if (sender is not SessionView view || !ReferenceEquals(view, ActiveSessionView) ||
                RightPanel.Content is not FileTreePanel panel)
                return;

            _ = panel.NavigateToPathAsync(remotePath);
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

        private CommandPanel CreateCommandPanel()
        {
            var panel = new CommandPanel();
            // playbook 실행 → 현재 선택된 세션 탭의 터미널에 입력
            panel.RunRequested += async (_, command) =>
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
            };
            return panel;
        }

        // ── 세션 탭 ──

        /// <summary>현재 선택된 탭의 세션. 없으면 null.</summary>
        private SessionView? ActiveSessionView =>
            (TitleTabs.SelectedItem as TabViewItem)?.DataContext as SessionView;

        private ISshSession? ActiveSession => ActiveSessionView?.Session;

        private async void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            var controlDown = IsKeyDown(Windows.System.VirtualKey.Control);
            var altDown = IsKeyDown(Windows.System.VirtualKey.Menu);
            var shortcutNumber = ShortcutNumber(e.Key);

            if (controlDown && !altDown)
            {
                if (shortcutNumber is int tabNumber)
                {
                    var index = tabNumber - 1;
                    if (index >= 0 && index < TitleTabs.TabItems.Count)
                    {
                        e.Handled = true;
                        if (_isMultiView && LeftNav.MenuItems.Count > 0)
                            LeftNav.SelectedItem = LeftNav.MenuItems[0];
                        TitleTabs.SelectedItem = TitleTabs.TabItems[index];
                        UpdateSessionArea();
                    }
                    return;
                }

                if (e.Key == Windows.System.VirtualKey.T)
                {
                    e.Handled = true;
                    await OpenLocalTerminalTabAsync();
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

            if (!altDown || controlDown || shortcutNumber is not int navigationNumber)
                return;

            var items = LeftNav.MenuItems
                .Concat(LeftNav.FooterMenuItems)
                .OfType<NavigationViewItem>()
                .ToArray();
            var navigationIndex = navigationNumber - 1;
            if (navigationIndex < 0 || navigationIndex >= items.Length)
                return;

            e.Handled = true;
            var item = items[navigationIndex];
            if (item.Tag is "Setting")
                OpenSettingWindow();
            else
                LeftNav.SelectedItem = item;
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

        private async void TitleTabs_AddTabButtonClick(TabView sender, object args)
            => await OpenLocalTerminalTabAsync();

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

            // The + button is a direct request to show a terminal. Leave the Multi
            // dashboard first so the newly selected local tab is immediately visible.
            if (_isMultiView && LeftNav.MenuItems.Count > 0)
                LeftNav.SelectedItem = LeftNav.MenuItems[0];

            var view = new LocalTerminalView();
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

            TitleTabs.TabItems.Add(tab);
            TitleTabs.SelectedItem = tab;
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
                return;
            }

            try
            {
                _ = HostEndpointIdentity.Create(info.Host, info.Port);
            }
            catch (ArgumentException ex)
            {
                ConnectionLogStore.Append(
                    Guid.Empty,
                    info.Title,
                    $"{info.Host}:{info.Port}",
                    ConnectionLogSeverity.Error,
                    "Validation",
                    "SSH 연결 주소가 올바르지 않습니다.",
                    "The SSH connection address is invalid.",
                    ex.Message);
                var invalidHostDialog = new ContentDialog
                {
                    Title = Helpers.Loc.T("연결 주소 확인", "Check connection address"),
                    Content = Helpers.Loc.T(
                        $"호스트 또는 포트가 올바르지 않습니다.\n\n{ex.Message}",
                        $"The host or port is invalid.\n\n{ex.Message}"),
                    CloseButtonText = "OK",
                    XamlRoot = Content.XamlRoot,
                };
                await invalidHostDialog.ShowAsync();
                return;
            }

            if (!await ConfirmHighRiskConnectionFeaturesAsync(info))
                return;

            info.HostKeyPromptAsync ??= PromptUnknownHostKeyAsync;
            info.KeyboardInteractivePromptAsync ??= PromptKeyboardInteractiveAsync;

            ISshSession session;
            try
            {
                session = _sessions.Create(info);
            }
            catch (RoutePolicyViolationException error)
            {
                ConnectionLogStore.Append(
                    Guid.Empty,
                    info.Title,
                    $"{info.Host}:{info.Port}",
                    ConnectionLogSeverity.Error,
                    "Route policy",
                    "연결 경로 정책이 SSH 연결을 차단했습니다.",
                    "The connection route policy blocked this SSH connection.",
                    error.Message);
                var routeDialog = new ContentDialog
                {
                    Title = Helpers.Loc.T("연결 경로 정책", "Connection route policy"),
                    Content = error.Message,
                    CloseButtonText = "OK",
                    XamlRoot = Content.XamlRoot,
                };
                await routeDialog.ShowAsync();
                return;
            }
            var savedProfile = await PersistSavedProfileAsync(info);
            var view = new SessionView(session);
            view.WorkingDirectoryChanged += SessionView_WorkingDirectoryChanged;

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
                DispatcherQueue.TryEnqueue(() => UpdateStatusDot(state));

            var tab = new TabViewItem
            {
                Header = header,
                IsClosable = true,
                DataContext = view,
            };

            TitleTabs.TabItems.Add(tab);
            TitleTabs.SelectedItem = tab;
            UpdateSessionArea();
            QueueWorkspaceSnapshot();

            var timer = Stopwatch.StartNew();
            var outcome = "Failed";
            string? errorCode = "SSH.CONNECT.FAILED";
            try
            {
                await session.ConnectAsync();
                if (session.State == SessionState.Connected)
                {
                    outcome = "Success";
                    errorCode = null;
                    var connectedProfileId = savedProfile?.Id ?? info.SavedHostId;
                    if (!string.IsNullOrWhiteSpace(connectedProfileId))
                        sutty.Command.HostProfileStore.MarkConnected(connectedProfileId);
                }
            }
            catch (OperationCanceledException)
            {
                outcome = "Cancelled";
                errorCode = "SSH.CONNECT.CANCELLED";
            }
            catch (Exception error)
            {
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
                info.Password = "";
                info.Passphrase = "";
                if (info.Route is not null)
                {
                    info.Route.Password = "";
                    info.Route.Passphrase = "";
                }
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
                        outcome,
                        errorCode,
                        timer.ElapsedMilliseconds);
                }
                catch (Exception historyError) when (historyError is System.IO.IOException or
                                                     Microsoft.Data.Sqlite.SqliteException or
                                                     UnauthorizedAccessException)
                {
                    Debug.WriteLine($"Connection history append failed: {historyError.GetType().Name}");
                }

                if (RightPanel.Content is HostListPanel historyPanel)
                    historyPanel.RefreshFromStore();
            }

            if (outcome == "Failed")
                await ShowConnectionFailureAsync(session);
        }

        private async Task ShowConnectionFailureAsync(ISshSession session)
        {
            var diagnostic = ConnectionLogStore.Snapshot()
                .LastOrDefault(entry => entry.SessionId == session.Id &&
                                        entry.Severity >= ConnectionLogSeverity.Error);
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock
            {
                Text = diagnostic is null
                    ? Helpers.Loc.T(
                        "SSH 연결을 완료하지 못했습니다.",
                        "The SSH connection could not be completed.")
                    : Helpers.Loc.T(diagnostic.MessageKo, diagnostic.MessageEn),
                Foreground = Helpers.ThemeResources.Brush(Root, "TextPrimary"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(new TextBlock
            {
                Text = session.LastError ?? Helpers.Loc.T("알 수 없는 오류", "Unknown error"),
                Foreground = Helpers.ThemeResources.Brush(Root, "StatusRed"),
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            });
            content.Children.Add(new TextBlock
            {
                Text = Helpers.Loc.T(
                    "DNS·소켓·키 교환·호스트 키·인증 단계의 상세 기록은 로그 화면에서 확인할 수 있습니다.",
                    "Detailed DNS, socket, key-exchange, host-key, and authentication diagnostics are available in Logs."),
                Foreground = Helpers.ThemeResources.Brush(Root, "TextMuted"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });

            var dialog = new ContentDialog
            {
                Title = Helpers.Loc.T("SSH 연결 실패", "SSH connection failed"),
                Content = content,
                PrimaryButtonText = Helpers.Loc.T("로그 열기", "Open logs"),
                CloseButtonText = Helpers.Loc.T("닫기", "Close"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                SelectNavigationItem("Logs");
        }

        private void SelectNavigationItem(string tag)
        {
            var item = LeftNav.MenuItems
                .Concat(LeftNav.FooterMenuItems)
                .OfType<NavigationViewItem>()
                .FirstOrDefault(candidate => string.Equals(candidate.Tag as string, tag, StringComparison.Ordinal));
            if (item is not null)
                LeftNav.SelectedItem = item;
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
            EnterpriseMode = info.RoutePolicy.EnterpriseMode,
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
                CancellationToken ct)
        {
            var completion = new TaskCompletionSource<
                System.Collections.Generic.IReadOnlyList<string>?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        completion.TrySetResult(
                            await ShowKeyboardInteractivePromptAsync(challenge, ct));
                    }
                    catch (OperationCanceledException)
                    {
                        completion.TrySetResult(null);
                    }
                    catch (Exception error)
                    {
                        completion.TrySetException(error);
                    }
                }))
            {
                completion.TrySetException(new InvalidOperationException(
                    "The SSH authentication prompt could not be dispatched to the UI."));
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

        private void TitleTabs_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            UpdateSessionArea();
            QueueWorkspaceSnapshot();
        }

        private void UpdateSessionArea()
        {
            EmptyTabHeader.Visibility = TitleTabs.TabItems.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            SessionHost.Content = (TitleTabs.SelectedItem as TabViewItem)?.DataContext as FrameworkElement;
            SessionHost.Visibility = _isMultiView ? Visibility.Collapsed : Visibility.Visible;
            NoSessionState.Visibility = !_isMultiView && TitleTabs.TabItems.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            // Multi 그리드가 보이는 중이면 열린 세션 목록으로 갱신
            if (_isMultiView)
                MultiGrid.SetSessions(GetOpenTerminalViews());

            // Keep the cached Files panel bound to the active tab even while it is hidden.
            // This invalidates old-server nodes/transfers before the panel can be shown again.
            if (_fileTreePanel is { } fileTree)
                _ = LoadFileTreeForActiveSessionAsync(fileTree);
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
                _fileTreePanel?.CancelTransfersForSession(closingSession.Session, userInitiated: true);

            sender.TabItems.Remove(args.Tab);
            UpdateSessionArea();
            QueueWorkspaceSnapshot();

            // 탭은 먼저 닫고, 연결 정리는 백그라운드로 (UI가 안 막히게)
            if (args.Tab.DataContext is SessionView view)
            {
                view.WorkingDirectoryChanged -= SessionView_WorkingDirectoryChanged;
                await _sessions.CloseAsync(view.Session);
            }
            else if (args.Tab.DataContext is LocalTerminalView localView)
            {
                await localView.CloseAsync();
            }
        }

        // ── 설정 창 ──

        private void OpenSettingWindow()
        {
            if (_settingWindow is null)
            {
                var panel = new SettingsPanel();
                panel.SettingsChanged += (_, args) => ApplySettingsChanges(args.Changes);
                panel.ThemeChanged += (_, themeName) => ApplyTheme(themeName);

                _settingWindow = new Window
                {
                    Title = Helpers.Loc.T("Sutty — 설정", "Sutty — Settings"),
                    ExtendsContentIntoTitleBar = true,
                    Content = panel,
                };
                panel.OwnerWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(_settingWindow);
                _settingWindow.AppWindow.SetIcon(_appIconPath); // 설정 창에도 앱 아이콘
                panel.RequestedTheme = Root.RequestedTheme;
                ApplyTitleBarColors(_settingWindow, Helpers.ThemeManager.Find(SettingsService.Current.Theme));
                _settingWindow.Closed += (_, _) => _settingWindow = null;

                // 저장된 크기로 열고, 드래그 리사이즈도 기억
                Helpers.WindowSizePersistence.Attach(_settingWindow,
                    s => new SizeInt32(s.SettingWindowWidth, s.SettingWindowHeight),
                    (s, size) => { s.SettingWindowWidth = size.Width; s.SettingWindowHeight = size.Height; });

                _settingWindow.Activate();
            }

            DispatcherQueue.TryEnqueue(() => BringToFront(_settingWindow));
        }

        private void ApplySettingsChanges(SettingChangeKind changes)
        {
            if (changes.HasFlag(SettingChangeKind.TerminalAppearance) ||
                changes.HasFlag(SettingChangeKind.TerminalMode) ||
                changes.HasFlag(SettingChangeKind.TerminalFeatures))
            {
                foreach (var view in GetOpenSessionViews())
                    view.ApplyTerminalSettings();

                if (changes.HasFlag(SettingChangeKind.TerminalAppearance))
                {
                    foreach (var localView in GetOpenLocalTerminalViews())
                        localView.ApplyTerminalSettings();
                }
            }

            if (changes.HasFlag(SettingChangeKind.Language))
            {
                Bindings.Update();
                if (_settingWindow is not null)
                    _settingWindow.Title = Helpers.Loc.T("Sutty — 설정", "Sutty — Settings");
                foreach (var view in GetOpenSessionViews())
                    view.RefreshLanguage();
                foreach (var localView in GetOpenLocalTerminalViews())
                    localView.RefreshLanguage();
                MultiGrid.RefreshLanguage();

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
            if ((applyDefaultPort || applyDefaultKeepAlive) &&
                RightPanel.Content is HomePanel homePanel)
            {
                homePanel.ApplyConnectionDefaults(applyDefaultPort, applyDefaultKeepAlive);
            }

            if (changes.HasFlag(SettingChangeKind.History) &&
                RightPanel.Content is HostListPanel historyPanel)
            {
                historyPanel.RefreshFromStore();
            }

            if (changes.HasFlag(SettingChangeKind.HostProfiles) &&
                RightPanel.Content is HostListPanel importedHostsPanel)
            {
                importedHostsPanel.RefreshFromStore();
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

            if (_settingWindow is not null && s.SettingWindowWidth > 0 && s.SettingWindowHeight > 0)
                _settingWindow.AppWindow.ResizeClient(new SizeInt32(s.SettingWindowWidth, s.SettingWindowHeight));

            // 오른쪽 패널 폭도 숫자로 지정 가능
            if (s.RightPanelWidth > 0)
                RightPanelColumn.Width = new GridLength(Math.Clamp(s.RightPanelWidth, 300, 800));
        }

        private static void BringToFront(Window window)
        {
            if (window.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                if (presenter.State == OverlappedPresenterState.Minimized)
                    presenter.Restore();

                presenter.IsAlwaysOnTop = true;
                presenter.IsAlwaysOnTop = false;
            }
            window.Activate();
        }
    }
}
