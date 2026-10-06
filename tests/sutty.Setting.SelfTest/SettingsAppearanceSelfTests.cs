using System.Xml.Linq;

internal static class SettingsAppearanceSelfTests
{
    private static readonly XNamespace Ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    public static void Run()
    {
        var views = Path.Combine(FindRepositoryRoot(), "src", "sutty.UI", "Views");
        var document = XDocument.Load(Path.Combine(views, "SettingsPanel.xaml"));
        var navigation = FindNamed(document, "SettingsNav");
        Assert(navigation.Name == Ui + "NavigationView" &&
               Attribute(navigation, "PaneDisplayMode") == "Auto" &&
               Attribute(navigation, "IsPaneOpen") == "False",
            "adaptive navigation does not initially cover narrow settings content");
        Assert(Attribute(navigation, "IsTitleBarAutoPaddingEnabled") == "False",
            "embedded settings navigation does not reserve a second title bar");
        Assert(Attribute(navigation, "DisplayModeChanged") == "SettingsNav_DisplayModeChanged" &&
               Attribute(navigation, "ItemInvoked") == "SettingsNav_ItemInvoked",
            "navigation wires resize and repeat-selection dismissal handlers");
        var footer = FindNamed(document, "SettingsPaneFooter");
        Assert(footer.Parent?.Name == Ui + "NavigationView.PaneFooter" &&
               Attribute(footer, "Visibility") == "{x:Bind SettingsNav.IsPaneOpen, Mode=OneWay}",
            "full-width save controls are hidden in the closed compact navigation rail");

        var navigationResources = navigation.Element(Ui + "NavigationView.Resources");
        Assert(navigationResources is not null, "navigation has local opaque pane resources");
        foreach (var key in new[] { "NavigationViewDefaultPaneBackground", "NavigationViewExpandedPaneBackground" })
        {
            var resource = navigationResources!.Elements(Ui + "StaticResource")
                .SingleOrDefault(element => (string?)element.Attribute(Xaml + "Key") == key);
            Assert(resource is not null && Attribute(resource, "ResourceKey") == "SidePanelBg",
                $"{key} aliases the mutable opaque side-panel brush");
        }

        var scrollViewer = FindNamed(document, "SettingsScrollViewer");
        Assert(scrollViewer.Name == Ui + "ScrollViewer" &&
               Attribute(scrollViewer, "HorizontalScrollMode") == "Disabled" &&
               Attribute(scrollViewer, "HorizontalScrollBarVisibility") == "Disabled" &&
               Attribute(scrollViewer, "HorizontalContentAlignment") == "Stretch",
            "settings content is measured at viewport width rather than horizontally scrolling");
        var content = FindNamed(document, "SettingsContentGrid");
        Assert(content.Name == Ui + "Grid" && content.Parent == scrollViewer &&
               Attribute(content, "MaxWidth") == "1040",
            "settings content remains bounded inside its constrained scroll viewport");

        var selector = FindNamed(document, "ThemeRadios");
        Assert(selector.Name == Ui + "ComboBox" && Attribute(selector, "MinWidth") == "0" &&
               Attribute(selector, "HorizontalAlignment") == "Stretch" &&
               Attribute(selector, "HorizontalContentAlignment") == "Stretch",
            "long theme names cannot impose a wider appearance pane");
        var themeLabel = selector.Element(Ui + "ComboBox.ItemTemplate")?
            .Descendants(Ui + "TextBlock").SingleOrDefault();
        Assert(themeLabel is not null && Attribute(themeLabel, "Text") == "{Binding}" &&
               Attribute(themeLabel, "TextTrimming") == "CharacterEllipsis" &&
               Attribute(themeLabel, "MaxLines") == "1",
            "selected and dropdown theme labels have bounded single-line ellipsis");

        var appearance = FindNamed(document, "AppearancePane");
        var statusLabels = new[] { "StatusGreen", "StatusAmber", "StatusRed" }
            .Select(role => appearance.Descendants(Ui + "TextBlock")
                .Single(element => Attribute(element, "Foreground") == $"{{ThemeResource {role}}}"))
            .ToArray();
        var statusGrid = statusLabels[0].Parent;
        Assert(statusGrid?.Name == Ui + "Grid" && statusLabels.All(label => label.Parent == statusGrid),
            "preview status labels share a separate grid instead of a fixed horizontal row");
        var columns = statusGrid!.Element(Ui + "Grid.ColumnDefinitions")?.Elements(Ui + "ColumnDefinition").ToArray();
        Assert(columns is { Length: 3 } && columns.All(column => Attribute(column, "Width") == "*"),
            "preview status labels have three equally constrained columns");
        Assert(statusLabels.Select(label => Attribute(label, "Grid.Column", "0")).SequenceEqual(["0", "1", "2"]) &&
               statusLabels.All(label => Attribute(label, "TextWrapping") == "Wrap" &&
                                        Attribute(label, "Text").Contains("Loc.T(", StringComparison.Ordinal)),
            "preview status labels wrap within their own columns in both languages");

        // These guards verify declared source structure, not WinUI layout or pointer behavior.
        var source = File.ReadAllText(Path.Combine(views, "SettingsPanel.xaml.cs"));
        var displayModeChanged = MethodBody(source, "SettingsNav_DisplayModeChanged");
        var itemInvoked = MethodBody(source, "SettingsNav_ItemInvoked");
        var closeOverlay = MethodBody(source, "CloseSettingsOverlay");
        Assert(displayModeChanged.Contains("IsPaneOpen = args.DisplayMode == NavigationViewDisplayMode.Expanded",
                   StringComparison.Ordinal),
            "display-mode changes open the pane only in expanded mode");
        Assert(itemInvoked.Contains("CloseSettingsOverlay();", StringComparison.Ordinal) &&
               closeOverlay.Contains("SettingsNav.DisplayMode != NavigationViewDisplayMode.Expanded", StringComparison.Ordinal) &&
               closeOverlay.Contains("IsPaneOpen = false", StringComparison.Ordinal),
            "item invocation dismisses an overlay without relying on selection changes");
        Console.WriteLine("Settings appearance/navigation source guards passed (not native WinUI acceptance).");
    }

    private static XElement FindNamed(XDocument document, string name) =>
        document.Descendants().Single(element => (string?)element.Attribute(Xaml + "Name") == name);

    private static string Attribute(XElement element, string name, string fallback = "") =>
        (string?)element.Attribute(name) ?? fallback;

    private static string MethodBody(string source, string name)
    {
        var start = source.IndexOf($"void {name}(", StringComparison.Ordinal);
        Assert(start >= 0, $"{name} exists");
        start = source.IndexOf('{', start);
        Assert(start >= 0, $"{name} has a body");
        var depth = 1;
        for (var index = start + 1; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            if (source[index] == '}' && --depth == 0) return source[(start + 1)..index];
        }
        throw new InvalidOperationException($"Settings appearance self-test failed: {name} has an unterminated body.");
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "src", "sutty.UI", "Views", "SettingsPanel.xaml")))
                    return directory.FullName;
        throw new DirectoryNotFoundException("Settings appearance source guards require the source repository.");
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"Settings appearance self-test failed: {name}.");
    }
}
