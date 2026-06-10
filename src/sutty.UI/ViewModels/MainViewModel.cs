// ============================================================================
// STUDY NOTE — MainViewModel.cs
// A "ViewModel" in the MVVM (Model-View-ViewModel) pattern holds the data and
// logic for a view (here, MainWindow) while staying free of UI code. The View
// (XAML) binds to properties on this class; when a property changes, the UI is
// notified and refreshes automatically.
// ============================================================================

// Imports the CommunityToolkit.Mvvm source-generator attributes/base classes
// ([ObservableProperty], ObservableObject). This is the MVVM Toolkit package
// referenced in the .csproj.
using CommunityToolkit.Mvvm.ComponentModel;

// Full name of this class becomes `sutty.UI.ViewModels.MainViewModel`.
namespace sutty.UI.ViewModels
{
    // `partial` is REQUIRED here: the MVVM Toolkit's source generator creates a
    // second half of this class at compile time (the generated public property
    // and change-notification code) and merges it with what we write.
    //
    // `: ObservableObject` gives us INotifyPropertyChanged for free — the
    // interface the XAML binding system listens to so the UI updates when a
    // property value changes.
    public partial class MainViewModel : ObservableObject
    {
        // [ObservableProperty] tells the generator to create a PUBLIC property
        // named `Greeting` (PascalCase) that wraps this private `_greeting`
        // field and raises a change notification on every set.
        //
        // Naming rule: the field `_greeting` (or `greeting`) becomes the public
        // property `Greeting`. We never write the property by hand — it is
        // generated. The XAML used to bind to it via {x:Bind ViewModel.Greeting}.
        [ObservableProperty]
        private string _greeting = "Welcome to sutty";
    }
}
