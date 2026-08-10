using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using sutty.Core.Models;
using sutty.Core.Sessions;
using sutty.Setting;
using System;
using System.Threading.Tasks;
using Windows.Graphics;

namespace sutty.UI.Views
{
    public sealed partial class MainWindow : Window
    {
        public sutty.UI.ViewModels.MainViewModel ViewModel { get; }

        /// <summary>동시에 열 수 있는 세션(탭) 최대 개수 — Multi 그리드(4×4)와 맞춤.</summary>
        private const int MaxSessions = 16;

        private readonly SessionManager _sessions = new();
        private Window? _settingWindow;
        private string _appIconPath = "";
        private bool _isMultiView;

        public MainWindow()
        {
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
                FlushRightPanelWidth();
                _settingWindow?.Close();
            };

            ApplyTheme(SettingsService.Current.Theme);

            // 저장된 창 크기 복원 + 리사이즈 시 디바운스 저장
            Helpers.WindowSizePersistence.Attach(this,
                s => new SizeInt32(s.MainWindowWidth, s.MainWindowHeight),
                (s, size) => { s.MainWindowWidth = size.Width; s.MainWindowHeight = size.Height; });

            RestoreRightPanelWidth();

            LeftNav.SelectedItem = LeftNav.MenuItems[0];
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
            // Demo records stay one-click. Real records open a secret-free draft so
            // the user must enter the password/passphrase again before connecting.
            panel.ConnectRequested += async (_, host) =>
            {
                if (host.IsMock)
                {
                    await OpenSessionTabAsync(new SshConnectionInfo
                    {
                        Host = host.Hostname,
                        DisplayName = host.Alias,
                        UseMockSession = true,
                    });
                    return;
                }

                OpenHistoryDraft(host);
            };
            return panel;
        }

        private void OpenHistoryDraft(ViewModels.HostInfoModel host)
        {
            var authMethod = Enum.TryParse<SshAuthMethod>(host.AuthMethod, true, out var parsed) &&
                             parsed is SshAuthMethod.Password or SshAuthMethod.PublicKey
                ? parsed
                : SshAuthMethod.Password;

            var draft = new SshConnectionInfo
            {
                Host = host.Hostname,
                Port = host.Port is >= 1 and <= 65535 ? host.Port : 22,
                DisplayName = host.Alias,
                Username = host.Username,
                AuthMethod = authMethod,
                PrivateKeyPath = authMethod == SshAuthMethod.PublicKey ? host.PrivateKeyPath : "",
                Password = "",
                Passphrase = "",
                Tags = [.. host.Tags],
            };

            var homeItem = LeftNav.MenuItems[0];
            LeftNav.SelectedItem = homeItem;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!ReferenceEquals(LeftNav.SelectedItem, homeItem)) return;

                if (RightPanel.Content is not HomePanel home)
                {
                    home = CreateHomePanel();
                    RightPanel.Content = home;
                }
                home.ApplyConnectionDraft(draft);
            });
        }

        private FileTreePanel CreateFileTreePanel()
        {
            var panel = new FileTreePanel();
            _ = panel.LoadAsync(ActiveSession);
            return panel;
        }

        private MultiCommandPanel CreateMultiPanel()
        {
            var panel = new MultiCommandPanel();
            panel.BroadcastRequested += async (_, command) => await BroadcastAsync(command);
            return panel;
        }

        // 체크된 모든 세션에 같은 명령을 병렬로 전송하고, 결과를 그리드 셀에 표시
        private async Task BroadcastAsync(string command)
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
                return;
            }

            foreach (var slot in targets)
                _ = RunBroadcastOnSlotAsync(slot, command);
        }

        private static async Task RunBroadcastOnSlotAsync(ViewModels.MultiSlotVm slot, string command)
        {
            slot.LastOutput = "…";
            var output = await slot.View!.RunExternalCommandAsync(command);
            slot.LastOutput = string.IsNullOrWhiteSpace(output)
                ? "(no output)"
                : output.Length > 400 ? output[..400] + "…" : output;
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
        private ISshSession? ActiveSession =>
            (TitleTabs.SelectedItem as TabViewItem)?.DataContext is SessionView view
                ? view.Session
                : null;

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

            // 접속 히스토리에 기록 (append-only: 접속마다 새 행 추가)
            sutty.Command.HostHistoryStore.Append(
                info.Title,
                info.Host,
                info.UseMockSession,
                info.Username,
                info.Port,
                info.AuthMethod.ToString(),
                info.AuthMethod == SshAuthMethod.PublicKey ? info.PrivateKeyPath : "",
                info.Tags);

            var session = _sessions.Create(info);
            var view = new SessionView(session);

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

            await session.ConnectAsync();
        }

        private void TitleTabs_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
            => UpdateSessionArea();

        private void UpdateSessionArea()
        {
            SessionHost.Content = (TitleTabs.SelectedItem as TabViewItem)?.DataContext as SessionView;
            SessionHost.Visibility = _isMultiView ? Visibility.Collapsed : Visibility.Visible;
            NoSessionState.Visibility = !_isMultiView && TitleTabs.TabItems.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            // Multi 그리드가 보이는 중이면 열린 세션 목록으로 갱신
            if (_isMultiView)
                MultiGrid.SetSessions(GetOpenSessionViews());

            // Folder 패널이 열려 있으면 활성 탭의 서버 파일 트리로 갱신
            if (RightPanel.Content is FileTreePanel fileTree)
                _ = fileTree.LoadAsync(ActiveSession);
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

        // X 클릭 → SSH·SFTP 채널을 모두 끊고 탭을 닫는다 (확인 없이 즉시)
        private async void TitleTabs_TabCloseRequested(
            TabView sender,
            TabViewTabCloseRequestedEventArgs args)
        {
            sender.TabItems.Remove(args.Tab);
            UpdateSessionArea();

            // 탭은 먼저 닫고, 연결 정리는 백그라운드로 (UI가 안 막히게)
            if (args.Tab.DataContext is SessionView view)
                await _sessions.CloseAsync(view.Session);
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
                    Title = "Sutty — Settings",
                    ExtendsContentIntoTitleBar = true,
                    Content = panel,
                };
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
                changes.HasFlag(SettingChangeKind.TerminalMode))
            {
                foreach (var view in GetOpenSessionViews())
                    view.ApplyTerminalSettings();
            }

            if (changes.HasFlag(SettingChangeKind.Language))
            {
                Bindings.Update();
                foreach (var view in GetOpenSessionViews())
                    view.RefreshLanguage();
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

            if (changes.HasFlag(SettingChangeKind.Window))
                ApplyWindowSizesFromSettings();
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
