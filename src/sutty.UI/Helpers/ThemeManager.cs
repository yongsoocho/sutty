using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.UI;

namespace sutty.UI.Helpers;

/// <summary>테마 프리셋 하나. Colors의 키는 Palette.xaml의 팔레트 키와 동일.</summary>
public sealed record ThemePreset(
    string Name,
    bool IsDark,
    string RailTop,
    string RailBottom,
    IReadOnlyDictionary<string, string> Colors);

/// <summary>
/// VS Code처럼 이름 있는 테마(Dracula, GitHub 등)를 적용한다.
/// 원리: Palette.xaml의 ThemeDictionaries 안 SolidColorBrush들의 Color를
/// 런타임에 통째로 바꿔치기 → ThemeResource로 참조하는 모든 UI가 즉시 갱신된다.
/// 프리셋은 반드시 "모든 팔레트 키"를 정의해야 한다 (이전 테마 색이 남지 않게).
/// </summary>
public static class ThemeManager
{
    // ── 팔레트 코어 키 목록 ──
    private static readonly string[] CoreKeys =
    [
        "AppBg", "PanelBg", "SidePanelBg", "CardBg", "CardBgHover", "CardBorder",
        "InputBg", "PillBg", "TextPrimary", "TextMuted", "TextFaint",
        "AccentBlue", "AccentTeal", "AccentViolet", "TerminalBg", "TerminalFg",
    ];

    public static IReadOnlyList<ThemePreset> Presets { get; } =
    [
        // 기본 다크 = "Sutty Deep Field" 디자인 원본 (1a) — Palette.xaml Dark 딕셔너리와 동일 값 유지
        new("Dark", true, "#0E1216", "#0E1216", new Dictionary<string, string>
        {
            ["AppBg"] = "#0B0E11", ["PanelBg"] = "#0C1015", ["SidePanelBg"] = "#10141A",
            ["CardBg"] = "#151B22", ["CardBgHover"] = "#161C22", ["CardBorder"] = "#1E242B",
            ["InputBg"] = "#0C1015", ["PillBg"] = "#12171D",
            ["TextPrimary"] = "#E8EDF2", ["TextMuted"] = "#8C99A8", ["TextFaint"] = "#6E7C8B",
            ["AccentBlue"] = "#2BC7B5", ["AccentTeal"] = "#5BDCCB", ["AccentViolet"] = "#8B7CF6",
            ["TerminalBg"] = "#0C1015", ["TerminalFg"] = "#8C99A8",
        }),

        // 라이트 = "Deep Field Light" 디자인 원본 (1c)
        new("Light", false, "#E4E9EE", "#E4E9EE", new Dictionary<string, string>
        {
            ["AppBg"] = "#EDF0F3", ["PanelBg"] = "#FFFFFF", ["SidePanelBg"] = "#F7F9FA",
            ["CardBg"] = "#FFFFFF", ["CardBgHover"] = "#EDF0F3", ["CardBorder"] = "#D8DEE5",
            ["InputBg"] = "#FFFFFF", ["PillBg"] = "#E6EAEF",
            ["TextPrimary"] = "#1C2530", ["TextMuted"] = "#5A6875", ["TextFaint"] = "#93A0AC",
            ["AccentBlue"] = "#0FA396", ["AccentTeal"] = "#0B7C72", ["AccentViolet"] = "#6A5AE0",
            ["TerminalBg"] = "#F7F9FA", ["TerminalFg"] = "#48545F",
        }),

        new("Dracula", true, "#2B2D3A", "#191A21", new Dictionary<string, string>
        {
            ["AppBg"] = "#191A21", ["PanelBg"] = "#1E1F29", ["SidePanelBg"] = "#282A36",
            ["CardBg"] = "#343746", ["CardBgHover"] = "#3C3F51", ["CardBorder"] = "#44475A",
            ["InputBg"] = "#21222C", ["PillBg"] = "#2E3040",
            ["TextPrimary"] = "#F8F8F2", ["TextMuted"] = "#9DA6C9", ["TextFaint"] = "#6272A4",
            ["AccentBlue"] = "#BD93F9", ["AccentTeal"] = "#8BE9FD", ["AccentViolet"] = "#FF79C6",
            ["TerminalBg"] = "#1E1F29", ["TerminalFg"] = "#F8F8F2",
        }),

        new("GitHub Dark", true, "#161B22", "#010409", new Dictionary<string, string>
        {
            ["AppBg"] = "#010409", ["PanelBg"] = "#0D1117", ["SidePanelBg"] = "#161B22",
            ["CardBg"] = "#161B22", ["CardBgHover"] = "#1F2630", ["CardBorder"] = "#30363D",
            ["InputBg"] = "#0D1117", ["PillBg"] = "#21262D",
            ["TextPrimary"] = "#E6EDF3", ["TextMuted"] = "#8B949E", ["TextFaint"] = "#6E7681",
            ["AccentBlue"] = "#58A6FF", ["AccentTeal"] = "#3FB950", ["AccentViolet"] = "#BC8CFF",
            ["TerminalBg"] = "#0D1117", ["TerminalFg"] = "#C9D1D9",
        }),

        new("GitHub Light", false, "#EAEEF2", "#D8DEE4", new Dictionary<string, string>
        {
            ["AppBg"] = "#EAEEF2", ["PanelBg"] = "#F6F8FA", ["SidePanelBg"] = "#FFFFFF",
            ["CardBg"] = "#FFFFFF", ["CardBgHover"] = "#F3F5F8", ["CardBorder"] = "#D0D7DE",
            ["InputBg"] = "#FFFFFF", ["PillBg"] = "#EFF2F5",
            ["TextPrimary"] = "#1F2328", ["TextMuted"] = "#57606A", ["TextFaint"] = "#8C959F",
            ["AccentBlue"] = "#0969DA", ["AccentTeal"] = "#1A7F37", ["AccentViolet"] = "#8250DF",
            ["TerminalBg"] = "#F6F8FA", ["TerminalFg"] = "#1F2328",
        }),

        new("Atom One Dark", true, "#2C313A", "#1B1F25", new Dictionary<string, string>
        {
            ["AppBg"] = "#1B1F25", ["PanelBg"] = "#21252B", ["SidePanelBg"] = "#282C34",
            ["CardBg"] = "#2C313A", ["CardBgHover"] = "#333842", ["CardBorder"] = "#3E4451",
            ["InputBg"] = "#1E222A", ["PillBg"] = "#2A2F38",
            ["TextPrimary"] = "#D7DAE0", ["TextMuted"] = "#9DA5B4", ["TextFaint"] = "#5C6370",
            ["AccentBlue"] = "#61AFEF", ["AccentTeal"] = "#56B6C2", ["AccentViolet"] = "#C678DD",
            ["TerminalBg"] = "#1E222A", ["TerminalFg"] = "#ABB2BF",
        }),

        new("Solarized Dark", true, "#073642", "#00212B", new Dictionary<string, string>
        {
            ["AppBg"] = "#00212B", ["PanelBg"] = "#002B36", ["SidePanelBg"] = "#073642",
            ["CardBg"] = "#073642", ["CardBgHover"] = "#0A4050", ["CardBorder"] = "#1A5A6A",
            ["InputBg"] = "#002B36", ["PillBg"] = "#0A4050",
            ["TextPrimary"] = "#EEE8D5", ["TextMuted"] = "#93A1A1", ["TextFaint"] = "#586E75",
            ["AccentBlue"] = "#268BD2", ["AccentTeal"] = "#2AA198", ["AccentViolet"] = "#6C71C4",
            ["TerminalBg"] = "#002B36", ["TerminalFg"] = "#93A1A1",
        }),
    ];

    public static ThemePreset Find(string name) =>
        Presets.FirstOrDefault(p => p.Name == name) ?? Presets[0];

    public static bool IsDark(string name) => Find(name).IsDark;

    /// <summary>프리셋을 적용한다. root는 RequestedTheme을 바꿀 창의 루트 요소.</summary>
    public static void Apply(string name, FrameworkElement root)
    {
        var preset = Find(name);
        var themeKey = preset.IsDark ? "Dark" : "Light";
        var dict = FindPaletteThemeDictionary(themeKey);

        if (dict is not null)
        {
            foreach (var key in CoreKeys)
                if (preset.Colors.TryGetValue(key, out var hex))
                    SetBrush(dict, key, hex);

            ApplyDerivedControlBrushes(dict, preset);
            ApplyRail(dict, preset);
        }

        root.RequestedTheme = preset.IsDark ? ElementTheme.Dark : ElementTheme.Light;
    }

    // TextBox/ComboBox/TabView 오버라이드는 코어 색에서 파생시킨다
    private static void ApplyDerivedControlBrushes(ResourceDictionary dict, ThemePreset p)
    {
        var c = p.Colors;

        SetBrush(dict, "TextControlBackground", c["InputBg"]);
        SetBrush(dict, "TextControlBackgroundPointerOver", c["CardBgHover"]);
        SetBrush(dict, "TextControlBackgroundFocused", c["CardBgHover"]);
        SetBrush(dict, "TextControlBorderBrush", c["CardBorder"]);
        SetBrush(dict, "TextControlBorderBrushPointerOver", c["CardBorder"]);
        SetBrush(dict, "TextControlBorderBrushFocused", c["AccentBlue"]);
        SetBrush(dict, "TextControlForeground", c["TextPrimary"]);
        SetBrush(dict, "TextControlForegroundPointerOver", c["TextPrimary"]);
        SetBrush(dict, "TextControlForegroundFocused", c["TextPrimary"]);
        SetBrush(dict, "TextControlPlaceholderForeground", c["TextFaint"]);
        SetBrush(dict, "TextControlPlaceholderForegroundPointerOver", c["TextFaint"]);
        SetBrush(dict, "TextControlPlaceholderForegroundFocused", c["TextFaint"]);

        SetBrush(dict, "ComboBoxBackground", c["InputBg"]);
        SetBrush(dict, "ComboBoxBackgroundPointerOver", c["CardBgHover"]);
        SetBrush(dict, "ComboBoxBackgroundPressed", c["CardBgHover"]);
        SetBrush(dict, "ComboBoxBorderBrush", c["CardBorder"]);
        SetBrush(dict, "ComboBoxForeground", c["TextPrimary"]);
        SetBrush(dict, "ComboBoxDropDownBackground", c["CardBg"]);

        SetBrush(dict, "TabViewItemHeaderBackgroundSelected", c["CardBg"]);
        SetBrush(dict, "TabViewItemHeaderBackgroundPointerOver", c["PillBg"]);
        SetBrush(dict, "TabViewItemHeaderForeground", c["TextMuted"]);
        SetBrush(dict, "TabViewItemHeaderForegroundSelected", c["TextPrimary"]);
        SetBrush(dict, "TabViewItemHeaderForegroundPointerOver", c["TextPrimary"]);
        SetBrush(dict, "TabViewItemIconForeground", c["TextMuted"]);
        SetBrush(dict, "TabViewItemIconForegroundSelected", c["AccentBlue"]);
    }

    private static void ApplyRail(ResourceDictionary dict, ThemePreset p)
    {
        if (!dict.TryGetValue("RailBg", out var value)) return;

        if (value is LinearGradientBrush gradient && gradient.GradientStops.Count >= 2)
        {
            gradient.GradientStops[0].Color = Parse(p.RailTop);
            gradient.GradientStops[^1].Color = Parse(p.RailBottom);
        }
        else if (value is SolidColorBrush solid)
        {
            solid.Color = Parse(p.RailTop);
        }
    }

    private static void SetBrush(ResourceDictionary dict, string key, string hex)
    {
        if (dict.TryGetValue(key, out var value) && value is SolidColorBrush brush)
            brush.Color = Parse(hex);
    }

    private static ResourceDictionary? FindPaletteThemeDictionary(string themeKey)
    {
        foreach (var merged in Application.Current.Resources.MergedDictionaries)
        {
            if (merged.ThemeDictionaries.TryGetValue(themeKey, out var themed) &&
                themed is ResourceDictionary rd &&
                rd.ContainsKey("PanelBg"))
            {
                return rd;
            }
        }
        return null;
    }

    private static Color Parse(string hex)
    {
        hex = hex.TrimStart('#');
        return Color.FromArgb(
            255,
            Convert.ToByte(hex[..2], 16),
            Convert.ToByte(hex[2..4], 16),
            Convert.ToByte(hex[4..6], 16));
    }
}
