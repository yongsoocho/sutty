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
            ["AppBg"] = "#0B0E11", ["PanelBg"] = "#0A0D10", ["SidePanelBg"] = "#10141A",
            ["CardBg"] = "#12171D", ["CardBgHover"] = "#161C22", ["CardBorder"] = "#242D37",
            ["ActiveTabBg"] = "#151B22", ["ShellBorder"] = "#1E242B",
            ["InputBg"] = "#0C1015", ["InputBorder"] = "#232C36", ["PillBg"] = "#12171D",
            ["Divider"] = "#171D23", ["OutputGuide"] = "#1E2830", ["TextPlaceholder"] = "#4F5D6B",
            ["TextPrimary"] = "#E8EDF2", ["TextMuted"] = "#8C99A8", ["TextFaint"] = "#6E7C8B",
            ["AccentBlue"] = "#2BC7B5", ["AccentTeal"] = "#5BDCCB", ["AccentViolet"] = "#78A9EE",
            ["GradientStart"] = "#2E7CD6", ["GradientEnd"] = "#14A79C",
            ["GradientHoverStart"] = "#4E9AF0", ["GradientHoverEnd"] = "#2BC7B5",
            ["TerminalBg"] = "#0A0D10", ["TerminalFg"] = "#9FB0C0",
        }),

        // 라이트 = "Deep Field Light" 디자인 원본 (1c)
        new("Light", false, "#E4E9EE", "#E4E9EE", new Dictionary<string, string>
        {
            ["AppBg"] = "#EDF0F3", ["PanelBg"] = "#FFFFFF", ["SidePanelBg"] = "#F7F9FA",
            ["CardBg"] = "#FFFFFF", ["CardBgHover"] = "#D9E0E7", ["CardBorder"] = "#D8DEE5",
            ["ActiveTabBg"] = "#FFFFFF", ["ShellBorder"] = "#D8DEE5",
            ["InputBg"] = "#FFFFFF", ["InputBorder"] = "#D2D9E0", ["PillBg"] = "#EDF0F3",
            ["Divider"] = "#E6EAEF", ["OutputGuide"] = "#E1E6EB", ["TextPlaceholder"] = "#93A0AC",
            ["TextPrimary"] = "#1C2530", ["TextMuted"] = "#5A6875", ["TextFaint"] = "#93A0AC",
            ["AccentBlue"] = "#0FA396", ["AccentTeal"] = "#0B7C72", ["AccentViolet"] = "#2570C8",
            ["GradientStart"] = "#2570C8", ["GradientEnd"] = "#0FA396",
            ["GradientHoverStart"] = "#2E7CD6", ["GradientHoverEnd"] = "#14A79C",
            ["TerminalBg"] = "#FFFFFF", ["TerminalFg"] = "#48545F",
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

            ApplyRoleBrushes(dict, preset);
            ApplyDerivedControlBrushes(dict, preset);
            ApplyAccentBrushes(dict, preset);
            ApplyRail(dict, preset);
        }

        root.RequestedTheme = preset.IsDark ? ElementTheme.Dark : ElementTheme.Light;
    }

    // TextBox/ComboBox/TabView 오버라이드는 코어 색에서 파생시킨다
    private static void ApplyDerivedControlBrushes(ResourceDictionary dict, ThemePreset p)
    {
        var c = p.Colors;
        var inputBorder = Role(p, "InputBorder", "CardBorder");
        var placeholder = Role(p, "TextPlaceholder", "TextFaint");
        var activeTab = Role(p, "ActiveTabBg", "CardBg");
        var shellBorder = Role(p, "ShellBorder", "CardBorder");

        SetBrush(dict, "TextControlBackground", c["InputBg"]);
        SetBrush(dict, "TextControlBackgroundPointerOver", c["InputBg"]);
        SetBrush(dict, "TextControlBackgroundFocused", c["InputBg"]);
        SetBrush(dict, "TextControlBorderBrush", inputBorder);
        SetBrush(dict, "TextControlBorderBrushPointerOver", inputBorder);
        SetBrush(dict, "TextControlBorderBrushFocused", c["AccentBlue"]);
        SetBrush(dict, "TextControlForeground", c["TextPrimary"]);
        SetBrush(dict, "TextControlForegroundPointerOver", c["TextPrimary"]);
        SetBrush(dict, "TextControlForegroundFocused", c["TextPrimary"]);
        SetBrush(dict, "TextControlPlaceholderForeground", placeholder);
        SetBrush(dict, "TextControlPlaceholderForegroundPointerOver", placeholder);
        SetBrush(dict, "TextControlPlaceholderForegroundFocused", placeholder);

        SetBrush(dict, "ComboBoxBackground", c["InputBg"]);
        SetBrush(dict, "ComboBoxBackgroundPointerOver", c["InputBg"]);
        SetBrush(dict, "ComboBoxBackgroundPressed", c["InputBg"]);
        SetBrush(dict, "ComboBoxBorderBrush", inputBorder);
        SetBrush(dict, "ComboBoxForeground", c["TextPrimary"]);
        SetBrush(dict, "ComboBoxDropDownBackground", c["CardBg"]);

        SetBrush(dict, "TabViewItemHeaderBackgroundSelected", activeTab);
        SetBrush(dict, "TabViewItemHeaderDragBackground", activeTab);
        SetBrush(dict, "TabViewItemHeaderBackgroundPointerOver", c["PillBg"]);
        SetBrush(dict, "TabViewItemHeaderForeground", c["TextMuted"]);
        SetBrush(dict, "TabViewItemHeaderForegroundSelected", c["TextPrimary"]);
        SetBrush(dict, "TabViewItemHeaderForegroundPointerOver", c["TextPrimary"]);
        SetBrush(dict, "TabViewItemIconForeground", c["TextMuted"]);
        SetBrush(dict, "TabViewItemIconForegroundSelected", c["AccentBlue"]);
        SetBrush(dict, "TabViewItemSeparator", shellBorder);

        SetBrush(dict, "NavigationViewItemBackgroundPointerOver", c["CardBgHover"]);
        SetBrush(dict, "NavigationViewItemBackgroundPressed", c["PillBg"]);
        SetBrush(dict, "NavigationViewItemBackgroundSelected", activeTab);
        SetBrush(dict, "NavigationViewItemBackgroundSelectedPointerOver", c["CardBgHover"]);
        SetBrush(dict, "NavigationViewItemBackgroundSelectedPressed", c["PillBg"]);
        SetBrush(dict, "NavigationViewItemForeground", c["TextFaint"]);
        SetBrush(dict, "NavigationViewItemForegroundPointerOver", c["TextPrimary"]);
        SetBrush(dict, "NavigationViewItemForegroundSelected", c["AccentTeal"]);
        SetBrush(dict, "NavigationViewItemForegroundSelectedPointerOver", c["AccentTeal"]);
    }

    private static void ApplyRoleBrushes(ResourceDictionary dict, ThemePreset p)
    {
        SetBrush(dict, "ActiveTabBg", Role(p, "ActiveTabBg", "CardBg"));
        SetBrush(dict, "ShellBorder", Role(p, "ShellBorder", "CardBorder"));
        SetBrush(dict, "InputBorder", Role(p, "InputBorder", "CardBorder"));
        SetBrush(dict, "Divider", Role(p, "Divider", "CardBorder"));
        SetBrush(dict, "OutputGuide", Role(p, "OutputGuide", "CardBorder"));
        SetBrush(dict, "TextPlaceholder", Role(p, "TextPlaceholder", "TextFaint"));
    }

    private static void ApplyAccentBrushes(ResourceDictionary dict, ThemePreset p)
    {
        var start = Role(p, "GradientStart", "AccentBlue");
        var end = Role(p, "GradientEnd", "AccentTeal");
        var hoverStart = Role(p, "GradientHoverStart", "GradientStart", "AccentBlue");
        var hoverEnd = Role(p, "GradientHoverEnd", "GradientEnd", "AccentTeal");

        ApplyAccentBrushesToDictionary(dict, p.IsDark, start, end, hoverStart, hoverEnd);

        // Button.Resources의 pressed/hover 별칭은 컨트롤 생성 시점의 테마
        // 그라디언트를 보관한다. 비활성 딕셔너리도 같은 값으로 갱신해 두면
        // 시작 테마와 설정 테마가 달라도 런타임 전환 색이 남지 않는다.
        var inactiveTheme = FindPaletteThemeDictionary(p.IsDark ? "Light" : "Dark");
        if (inactiveTheme is not null && !ReferenceEquals(inactiveTheme, dict))
            ApplyAccentBrushesToDictionary(inactiveTheme, p.IsDark, start, end, hoverStart, hoverEnd);
    }

    private static void ApplyAccentBrushesToDictionary(
        ResourceDictionary dict,
        bool isDark,
        string start,
        string end,
        string hoverStart,
        string hoverEnd)
    {
        SetGradient(dict, "AccentGradient", start, end);
        SetGradient(dict, "AccentGradientHover", hoverStart, hoverEnd);
        SetGradient(dict, "AccentIndicator", start, end);
        SetGradient(dict, "NavigationViewSelectionIndicatorForeground", start, end);

        var alpha = isDark ? (byte)0x38 : (byte)0x24;
        SetGradient(dict, "AccentTint", start, end, alpha);
        SetGradient(dict, "NavigationViewItemBackgroundSelected", start, end, alpha);
        SetGradient(dict, "NavigationViewItemBackgroundSelectedPointerOver", start, end, alpha);
        SetGradient(dict, "NavigationViewItemBackgroundSelectedPressed", start, end, alpha);
    }

    private static string Role(ThemePreset p, string role, string fallback) =>
        p.Colors.TryGetValue(role, out var value) ? value : p.Colors[fallback];

    private static string Role(ThemePreset p, string role, string fallbackRole, string fallbackCore) =>
        p.Colors.TryGetValue(role, out var value)
            ? value
            : p.Colors.TryGetValue(fallbackRole, out var fallback)
                ? fallback
                : p.Colors[fallbackCore];

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

    private static void SetGradient(
        ResourceDictionary dict,
        string key,
        string start,
        string end,
        byte alpha = 255)
    {
        if (!dict.TryGetValue(key, out var value) ||
            value is not LinearGradientBrush gradient ||
            gradient.GradientStops.Count < 2)
        {
            return;
        }

        var startColor = Parse(start);
        var endColor = Parse(end);
        gradient.GradientStops[0].Color = Color.FromArgb(alpha, startColor.R, startColor.G, startColor.B);
        gradient.GradientStops[^1].Color = Color.FromArgb(alpha, endColor.R, endColor.G, endColor.B);
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
