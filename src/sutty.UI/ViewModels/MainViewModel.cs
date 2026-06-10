using CommunityToolkit.Mvvm.ComponentModel;

namespace sutty.UI.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        [ObservableProperty]
        private string _greeting = "Welcome to sutty";
    }
}
