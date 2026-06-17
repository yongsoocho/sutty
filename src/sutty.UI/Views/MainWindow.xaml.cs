using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace sutty.UI.Views
{
    public sealed partial class MainWindow : Window
    {
        public sutty.UI.ViewModels.MainViewModel ViewModel { get; }
        private Window? _settingWindow;

        public MainWindow()
        {
            ViewModel = new sutty.UI.ViewModels.MainViewModel();
            InitializeComponent();


            ExtendsContentIntoTitleBar = true;
            SetTitleBar(CustomDragRegion);

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
                    "Home" => new HomePanel(),
                    "Document" => new HostListPanel(),
                    "More" => new MorePanel(),
                    _ => null
                };
            }
        }

        private void OpenSettingWindow()
        {
            if (_settingWindow is null)
            {
                _settingWindow = new Window
                {
                    ExtendsContentIntoTitleBar = true,
                    SystemBackdrop = new MicaBackdrop(),
                    Content = new TextBlock
                    {
                        Text = "New child window!",
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    }
                };
                _settingWindow.Closed += (_, _) => _settingWindow = null;
                _settingWindow.AppWindow.ResizeClient(new SizeInt32(500, 500));
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

        private void TitleTabs_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (TitleTabs.SelectedItem is TabViewItem tab)
            {
                ContentText.Text = tab.Header?.ToString();
            }
        }

        private void TitleTabs_TabCloseRequested(
            TabView sender,
            TabViewTabCloseRequestedEventArgs args)
        {
            sender.TabItems.Remove(args.Tab);
        }

    }
}
