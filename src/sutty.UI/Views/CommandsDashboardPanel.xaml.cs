using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace sutty.UI.Views;

/// <summary>Global saved-command library with an explicit entry to multi-session tools.</summary>
public sealed partial class CommandsDashboardPanel : UserControl
{
    public event EventHandler<string>? RunRequested;

    public event EventHandler? PowerToolsRequested;

    public CommandsDashboardPanel()
    {
        InitializeComponent();
        CommandLibrary.RunRequested += (_, command) => RunRequested?.Invoke(this, command);
    }

    public void RefreshLanguage()
    {
        Bindings.Update();
        CommandLibrary.RefreshLanguage();
    }

    public void SetPowerToolsAvailable(bool available)
    {
        PowerToolsButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        PowerToolsButton.IsEnabled = available;
    }

    private void PowerTools_Click(object sender, RoutedEventArgs e) =>
        PowerToolsRequested?.Invoke(this, EventArgs.Empty);
}
