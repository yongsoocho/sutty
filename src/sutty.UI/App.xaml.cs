// ============================================================================
// STUDY NOTE — App.xaml.cs
// This is the application's entry-point class (the code-behind for App.xaml).
// It runs before any window exists and is responsible for creating and showing
// the first window. Think of it as the WinUI equivalent of Main()/WinMain().
// ============================================================================

// `using` directives below import namespaces so we can write short type names
// (e.g. `Window` instead of `Microsoft.UI.Xaml.Window`). Most of these are
// added automatically by the project template; only a few are actually used
// here (Microsoft.UI.Xaml for Application/Window, sutty.UI.Views for MainWindow).
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using sutty.UI.Views;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

// Declares where the App class lives. Its full name is `sutty.UI.App`, which is
// also referenced by the x:Class attribute in App.xaml.
namespace sutty.UI
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    // `partial` because the XAML compiler generates the other half (App.g.cs)
    // which contains InitializeComponent(). We inherit from the WinUI
    // `Application` base class, which owns the app lifecycle and resources.
    public partial class App : Application
    {
        // Holds a reference to the main window so it is NOT garbage-collected
        // while the app is running. `Window?` = nullable: it is null until the
        // app launches and we create the window in OnLaunched.
        private Window? _window;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            // Generated from App.xaml: loads application-level resources
            // (the merged resource dictionaries / themes) so they are available
            // to every window. Must run before any UI is created.
            InitializeComponent();
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        // `override` because Application defines a virtual OnLaunched method that
        // the framework calls automatically once startup is ready.
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            // 설정을 가장 먼저 로드해 둔다 (UI가 기본값으로 참조하기 전에).
            sutty.Setting.SettingsService.Load();

            // Create the main window (its constructor sets the title bar, icon,
            // navigation, etc. — see MainWindow.xaml.cs).
            _window = new MainWindow();

            // Show the window and give it focus. Without Activate() the window
            // is created but never appears on screen.
            _window.Activate();
        }
    }
}
