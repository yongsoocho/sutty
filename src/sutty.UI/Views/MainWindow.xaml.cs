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

        private readonly SessionManager _sessions = new();
        private Window? _settingWindow;

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
            string iconPath = System.IO.Path.Combine(
                System.AppContext.BaseDirectory, "Assets", "sutty.ico");
            appWindow.SetIcon(iconPath);

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
                RightPanelColumn.Width = new GridLength(Math.Clamp(saved, 220, 800));

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

        // ── 다크/라이트 테마 ──

        private void ApplyTheme(string theme)
        {
            Helpers.ThemeManager.Apply(theme, Root);
            var isLight = !Helpers.ThemeManager.IsDark(theme);
            // 다크일 땐 해(라이트로 전환), 라이트일 땐 달(다크로 전환) 아이콘
            ThemeToggleIcon.Glyph = isLight ? "" : "";

            // 설정 창이 열려 있으면 같이 바꿔 준다
            if (_settingWindow?.Content is FrameworkElement settingRoot)
                settingRoot.RequestedTheme = Root.RequestedTheme;
        }

        private void ThemeToggle_Click(object sender, RoutedEventArgs e)
        {
            var settings = SettingsService.Current;
            settings.Theme = Helpers.ThemeManager.IsDark(settings.Theme) ? "Light" : "Dark";
            SettingsService.Save();
            ApplyTheme(settings.Theme);
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
                    "More" => new MorePanel(),
                    _ => null
                };
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
            // History 카드 클릭 → 바로 연결 (샘플 호스트는 mock 세션으로)
            panel.ConnectRequested += async (_, host) =>
            {
                host.LastConnected = DateTime.Now;
                await OpenSessionTabAsync(new SshConnectionInfo
                {
                    Host = string.IsNullOrWhiteSpace(host.IP) ? host.Hostname : host.IP,
                    DisplayName = host.Hostname,
                    Username = host.Username,
                    UseMockSession = true,
                });
            };
            return panel;
        }

        private FileTreePanel CreateFileTreePanel()
        {
            var panel = new FileTreePanel();
            _ = panel.LoadAsync(ActiveSession);
            return panel;
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
                        Title = "No active session",
                        Content = "명령을 실행할 세션이 없습니다. 먼저 서버에 연결하세요.",
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
            var session = _sessions.Create(info);
            var view = new SessionView(session);

            var tab = new TabViewItem
            {
                Header = info.Title,
                IsClosable = true,
                DataContext = view,
                IconSource = new FontIconSource { Glyph = "" },
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
            NoSessionState.Visibility = TitleTabs.TabItems.Count > 0
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private async void TitleTabs_TabCloseRequested(
            TabView sender,
            TabViewTabCloseRequestedEventArgs args)
        {
            if (args.Tab.DataContext is SessionView view)
            {
                // 연결 중인 세션이면 (설정에 따라) 먼저 확인
                if (SettingsService.Current.ConfirmOnTabClose &&
                    view.Session.State == SessionState.Connected)
                {
                    var dialog = new ContentDialog
                    {
                        Title = "Disconnect session?",
                        Content = $"\"{view.Session.Info.Title}\" is still connected. Close the tab and disconnect?",
                        PrimaryButtonText = "Disconnect",
                        CloseButtonText = "Cancel",
                        DefaultButton = ContentDialogButton.Primary,
                        XamlRoot = Content.XamlRoot,
                    };
                    if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                        return;
                }

                // 세션을 안전하게 끊은 뒤 탭 제거
                await _sessions.CloseAsync(view.Session);
            }

            sender.TabItems.Remove(args.Tab);
            UpdateSessionArea();
        }

        // ── 설정 창 ──

        private void OpenSettingWindow()
        {
            if (_settingWindow is null)
            {
                var panel = new SettingsPanel();
                panel.Saved += (_, _) => ApplyWindowSizesFromSettings();
                panel.ThemeChanged += (_, themeName) => ApplyTheme(themeName);

                _settingWindow = new Window
                {
                    Title = "sutty — Settings",
                    ExtendsContentIntoTitleBar = true,
                    SystemBackdrop = new MicaBackdrop(),
                    Content = panel,
                };
                _settingWindow.Closed += (_, _) => _settingWindow = null;

                // 저장된 크기로 열고, 드래그 리사이즈도 기억
                Helpers.WindowSizePersistence.Attach(_settingWindow,
                    s => new SizeInt32(s.SettingWindowWidth, s.SettingWindowHeight),
                    (s, size) => { s.SettingWindowWidth = size.Width; s.SettingWindowHeight = size.Height; });

                _settingWindow.Activate();
            }

            DispatcherQueue.TryEnqueue(() => BringToFront(_settingWindow));
        }

        // 설정 패널에서 저장한 창 크기 숫자를 즉시 반영
        private void ApplyWindowSizesFromSettings()
        {
            var s = SettingsService.Current;

            if (s.MainWindowWidth > 0 && s.MainWindowHeight > 0)
                AppWindow.ResizeClient(new SizeInt32(s.MainWindowWidth, s.MainWindowHeight));

            if (_settingWindow is not null && s.SettingWindowWidth > 0 && s.SettingWindowHeight > 0)
                _settingWindow.AppWindow.ResizeClient(new SizeInt32(s.SettingWindowWidth, s.SettingWindowHeight));
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
