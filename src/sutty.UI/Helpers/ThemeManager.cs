using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using sutty.Setting;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.UI;

namespace sutty.UI.Helpers;

public sealed record ThemePreset(
    string Name,
    bool IsDark,
    string RailTop,
    string RailBottom,
    IReadOnlyDictionary<string, string> Colors);

public static class ThemeManager
{
    public static IReadOnlyList<ThemePreset> Presets { get; } = ThemeCatalog.Presets
        .Select(definition =>
        {
            var palette = ApplicationPalette.Create(definition);
            return new ThemePreset(definition.Name, definition.IsDark,
                palette.RailTop, palette.RailBottom, palette.Colors);
        }).ToArray();

    public static ThemePreset Find(string name) =>
        Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Presets[0];

    public static bool IsDark(string name) => Find(name).IsDark;

    public static void Apply(string name, FrameworkElement root)
    {
        var preset = Find(name);
        // Existing control aliases may still hold a brush from either dictionary.
        // Mutate both in place, including dark-to-dark switches, rather than replacing brushes.
        foreach (var themeKey in new[] { "Dark", "Light" })
        {
            var dict = FindPaletteThemeDictionary(themeKey);
            if (dict is null) continue;
            foreach (var (key, hex) in preset.Colors)
                SetBrush(dict, key, hex);
            ApplyDerivedControlBrushes(dict, preset);
            ApplyAccentBrushes(dict, preset);
            SetBrush(dict, "RailBg", preset.RailTop);
        }

        root.RequestedTheme = preset.IsDark ? ElementTheme.Dark : ElementTheme.Light;
    }

    private static void ApplyDerivedControlBrushes(ResourceDictionary dict, ThemePreset preset)
    {
        var c = preset.Colors;
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundFocused", "ComboBoxBackground" })
            SetBrush(dict, key, c["InputBg"]);
        foreach (var key in new[] { "TextControlBackgroundPointerOver", "ComboBoxBackgroundPointerOver", "ComboBoxBackgroundPressed" })
            SetBrush(dict, key, c["CardBg"]);
        foreach (var key in new[] { "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "ComboBoxBorderBrush" })
            SetBrush(dict, key, c["InputBorder"]);
        SetBrush(dict, "TextControlBorderBrushFocused", c["AccentBlue"]);
        foreach (var key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused", "ComboBoxForeground" })
            SetBrush(dict, key, c["TextPrimary"]);
        foreach (var key in new[] { "TextControlPlaceholderForeground", "TextControlPlaceholderForegroundPointerOver", "TextControlPlaceholderForegroundFocused" })
            SetBrush(dict, key, c["TextPlaceholder"]);
        SetBrush(dict, "ComboBoxDropDownBackground", c["CardBg"]);

        SetBrush(dict, "TabViewItemHeaderBackgroundSelected", c["ActiveTabBg"]);
        SetBrush(dict, "TabViewItemHeaderDragBackground", c["ActiveTabBg"]);
        SetBrush(dict, "TabViewItemHeaderBackgroundPointerOver", c["PillBg"]);
        SetBrush(dict, "TabViewItemHeaderForeground", c["TextMuted"]);
        SetBrush(dict, "TabViewItemHeaderForegroundSelected", c["TextPrimary"]);
        SetBrush(dict, "TabViewItemHeaderForegroundPointerOver", c["TextPrimary"]);
        SetBrush(dict, "TabViewItemIconForeground", c["TextMuted"]);
        SetBrush(dict, "TabViewItemIconForegroundSelected", c["AccentBlue"]);
        SetBrush(dict, "TabViewButtonBackgroundPointerOver", c["CardBgHover"]);
        SetBrush(dict, "TabViewButtonBackgroundPressed", c["PillBg"]);
        SetBrush(dict, "TabViewButtonForeground", c["TextMuted"]);
        SetBrush(dict, "TabViewButtonForegroundPointerOver", c["TextPrimary"]);
        SetBrush(dict, "TabViewButtonForegroundPressed", c["AccentTeal"]);

        SetBrush(dict, "NavigationViewItemBackgroundPointerOver", c["CardBgHover"]);
        SetBrush(dict, "NavigationViewItemBackgroundPressed", c["PillBg"]);
        SetBrush(dict, "NavigationViewItemBackgroundSelected", c["SelectionBg"]);
        SetBrush(dict, "NavigationViewItemBackgroundSelectedPointerOver", c["SelectionBgHover"]);
        SetBrush(dict, "NavigationViewItemBackgroundSelectedPressed", c["SelectionBg"]);
        SetBrush(dict, "NavigationViewItemForeground", c["TextFaint"]);
        SetBrush(dict, "NavigationViewItemForegroundPointerOver", c["TextPrimary"]);
        SetBrush(dict, "NavigationViewItemForegroundSelected", c["AccentTeal"]);
        SetBrush(dict, "NavigationViewItemForegroundSelectedPointerOver", c["AccentTeal"]);
    }

    private static void ApplyAccentBrushes(ResourceDictionary dict, ThemePreset preset)
    {
        var c = preset.Colors;
        SetGradient(dict, "AccentGradient", c["GradientStart"], c["GradientEnd"]);
        SetGradient(dict, "AccentGradientHover", c["GradientHoverStart"], c["GradientHoverEnd"]);
        SetGradient(dict, "AccentIndicator", c["GradientStart"], c["GradientEnd"]);
        SetGradient(dict, "NavigationViewSelectionIndicatorForeground", c["GradientStart"], c["GradientEnd"]);
        // Selection tint is an opaque surface, so its contrast matches the tested palette.
        SetBrush(dict, "AccentTint", c["SelectionBg"]);
    }

    private static void SetBrush(ResourceDictionary dict, string key, string hex)
    {
        if (dict.TryGetValue(key, out var value) && value is SolidColorBrush brush)
            brush.Color = Parse(hex);
    }

    private static void SetGradient(ResourceDictionary dict, string key, string start, string end)
    {
        if (dict.TryGetValue(key, out var value) && value is LinearGradientBrush gradient && gradient.GradientStops.Count >= 2)
        {
            gradient.GradientStops[0].Color = Parse(start);
            gradient.GradientStops[^1].Color = Parse(end);
        }
    }

    private static ResourceDictionary? FindPaletteThemeDictionary(string themeKey)
    {
        foreach (var merged in Application.Current.Resources.MergedDictionaries)
        {
            if (merged.ThemeDictionaries.TryGetValue(themeKey, out var themed) &&
                themed is ResourceDictionary rd && rd.ContainsKey("PanelBg"))
                return rd;
        }
        return null;
    }

    private static Color Parse(string hex) => Color.FromArgb(255,
        Convert.ToByte(hex.Substring(1, 2), 16),
        Convert.ToByte(hex.Substring(3, 2), 16),
        Convert.ToByte(hex.Substring(5, 2), 16));
}
