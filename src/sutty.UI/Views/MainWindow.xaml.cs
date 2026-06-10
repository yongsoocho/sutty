using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using sutty.UI.ViewModels;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace sutty.UI.Views
{
    /// <summary>
    /// The application's main window.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        public MainViewModel ViewModel { get; }

        public MainWindow()
        {
            ViewModel = new MainViewModel();
            InitializeComponent();

            // Extend the app content into the title bar so the area at the top
            // becomes transparent and the Mica backdrop shows through it.
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
        }

        private int _newTabCount;

        // Fired when the "+" button at the right of the tab strip is clicked.
        private void CenterTabs_AddTabButtonClick(TabView sender, object args)
        {
            var newTab = new TabViewItem
            {
                Header = $"New Tab {++_newTabCount}",
                IconSource = new SymbolIconSource { Symbol = Symbol.Document },
                Content = new Grid
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"Page {_newTabCount}",
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
                        }
                    }
                }
            };

            sender.TabItems.Add(newTab);
            sender.SelectedItem = newTab;
        }

        // Fired when a tab's close (X) button is clicked.
        private void CenterTabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
        {
            sender.TabItems.Remove(args.Tab);
        }
    }
}
