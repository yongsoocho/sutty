using Microsoft.UI.Xaml.Controls;
using System;

namespace sutty.UI.Views;

/// <summary>Global saved-command library for the selected session.</summary>
public sealed partial class CommandsDashboardPanel : UserControl
{
    public event EventHandler<string>? RunRequested;

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

}
