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

            LeftNav.SelectedItem = LeftNav.MenuItems[0];
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
                _settingWindow = new Window
                {
                    Title = "sutty — Settings",
                    ExtendsContentIntoTitleBar = true,
                    SystemBackdrop = new MicaBackdrop(),
                    Content = new SettingsPanel(),
                };
                _settingWindow.Closed += (_, _) => _settingWindow = null;
                _settingWindow.AppWindow.ResizeClient(new SizeInt32(520, 660));
                _settingWindow.Activate();
            }

            DispatcherQueue.TryEnqueue(() => BringToFront(_settingWindow));
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
