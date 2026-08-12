using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using sutty.Core.Terminal;
using sutty.Setting;
using sutty.UI.Helpers;
using System.Linq;
using Windows.UI;

namespace sutty.UI.Controls;

/// <summary>
/// Attached highlighting behavior for selectable TextBlocks. Keeping the original TextBlock
/// preserves selection, wrapping, typography, and x:Bind behavior in REPL data templates.
/// </summary>
public static class TerminalHighlight
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text",
        typeof(string),
        typeof(TerminalHighlight),
        new PropertyMetadata(string.Empty, OnTextChanged));

    private static readonly DependencyProperty IsThemeHookedProperty =
        DependencyProperty.RegisterAttached(
            "IsThemeHooked",
            typeof(bool),
            typeof(TerminalHighlight),
            new PropertyMetadata(false));

    public static string GetText(DependencyObject element) =>
        (string?)element.GetValue(TextProperty) ?? string.Empty;

    public static void SetText(DependencyObject element, string value) =>
        element.SetValue(TextProperty, value);

    public static void Refresh(DependencyObject root)
    {
        if (root is TextBlock textBlock &&
            textBlock.ReadLocalValue(TextProperty) != DependencyProperty.UnsetValue)
        {
            Rebuild(textBlock);
        }

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
            Refresh(VisualTreeHelper.GetChild(root, index));
    }

    private static void OnTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not TextBlock textBlock)
            return;

        textBlock.Text = args.NewValue as string ?? string.Empty;
        if (!(bool)textBlock.GetValue(IsThemeHookedProperty))
        {
            textBlock.SetValue(IsThemeHookedProperty, true);
            textBlock.ActualThemeChanged += OnActualThemeChanged;
        }

        Rebuild(textBlock);
    }

    private static void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        if (sender is TextBlock textBlock)
            Rebuild(textBlock);
    }

    private static void Rebuild(TextBlock textBlock)
    {
        textBlock.TextHighlighters.Clear();

        var settings = SettingsService.Current;
        var spans = TerminalTextClassifier.Classify(
            textBlock.Text,
            settings.EnableStructuredTextHighlighting,
            settings.EnableSeverityHighlighting);

        foreach (var group in spans.GroupBy(span => span.Kind))
        {
            var highlighter = CreateHighlighter(textBlock, group.Key);
            foreach (var span in group)
            {
                highlighter.Ranges.Add(new TextRange
                {
                    StartIndex = span.Start,
                    Length = span.Length,
                });
            }

            textBlock.TextHighlighters.Add(highlighter);
        }
    }

    private static TextHighlighter CreateHighlighter(
        FrameworkElement scope,
        TerminalTextHighlightKind kind)
    {
        var highlighter = new TextHighlighter
        {
            Foreground = ThemeResources.Brush(scope, kind switch
            {
                TerminalTextHighlightKind.Property => "AccentBlue",
                TerminalTextHighlightKind.String => "AccentTeal",
                TerminalTextHighlightKind.Number => "AccentViolet",
                TerminalTextHighlightKind.Keyword => "StatusAmber",
                TerminalTextHighlightKind.Comment => "TextFaint",
                TerminalTextHighlightKind.Warning => "StatusAmber",
                TerminalTextHighlightKind.Critical => "StatusRed",
                _ => "TerminalFg",
            }),
        };

        if (kind == TerminalTextHighlightKind.Warning)
            highlighter.Background = Translucent(scope, "StatusAmber", 38);
        else if (kind == TerminalTextHighlightKind.Critical)
            highlighter.Background = Translucent(scope, "StatusRed", 48);

        return highlighter;
    }

    private static SolidColorBrush Translucent(
        FrameworkElement scope,
        string resourceKey,
        byte alpha)
    {
        var color = ThemeResources.Brush(scope, resourceKey) is SolidColorBrush source
            ? source.Color
            : Color.FromArgb(255, 214, 72, 72);
        return new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
    }
}
