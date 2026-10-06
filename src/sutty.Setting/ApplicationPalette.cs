using System.Collections.ObjectModel;
using System.Globalization;

namespace sutty.Setting;

public sealed record ApplicationPalette(
    string RailTop,
    string RailBottom,
    IReadOnlyDictionary<string, string> Colors)
{
    public static ApplicationPalette Create(ThemeDefinition theme)
    {
        var bg = theme.Background;
        var fg = theme.Foreground;
        var colors = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AppBg"] = ColorContrast.Mix(bg, theme.IsDark ? "#000000" : fg, theme.IsDark ? .14 : .035),
            ["PanelBg"] = bg,
            ["SidePanelBg"] = ColorContrast.Mix(bg, fg, theme.IsDark ? .035 : .015),
            ["CardBg"] = ColorContrast.Mix(bg, fg, theme.IsDark ? .055 : .01),
            ["CardBgHover"] = ColorContrast.Mix(bg, fg, theme.IsDark ? .085 : .055),
            ["ActiveTabBg"] = ColorContrast.Mix(bg, fg, theme.IsDark ? .065 : .01),
            ["InputBg"] = ColorContrast.Mix(bg, "#000000", theme.IsDark ? .08 : 0),
            ["PillBg"] = ColorContrast.Mix(bg, fg, theme.IsDark ? .07 : .045),
            ["CardBorder"] = ColorContrast.Mix(bg, fg, .22),
            ["ShellBorder"] = ColorContrast.Mix(bg, fg, .15),
            ["Divider"] = ColorContrast.Mix(bg, fg, .13),
            ["OutputGuide"] = ColorContrast.Mix(bg, fg, .16),
            ["TerminalBg"] = bg,
            ["TerminalFg"] = fg,
        };

        // Deep Field keeps its blue-to-teal signature, without legacy terminal overrides.
        if (theme.Id == "DeepField")
        {
            colors["AppBg"] = "#0B0E11";
            colors["SidePanelBg"] = "#10141A";
            colors["CardBg"] = "#12171D";
            colors["CardBgHover"] = "#161C22";
            colors["ActiveTabBg"] = "#151B22";
            colors["InputBg"] = "#0C1015";
            colors["PillBg"] = "#12171D";
        }
        else if (theme.Id == "DeepFieldLight")
        {
            colors["AppBg"] = "#EDF0F3";
            colors["SidePanelBg"] = "#F7F9FA";
            colors["CardBg"] = "#FFFFFF";
            colors["CardBgHover"] = "#E4E9EE";
            colors["ActiveTabBg"] = "#FFFFFF";
            colors["PillBg"] = "#EDF0F3";
        }

        var rail = theme.Id switch
        {
            "DeepField" => "#0E1216",
            "DeepFieldLight" => "#E4E9EE",
            _ => colors["AppBg"],
        };
        var start = theme.Id switch
        {
            "DeepField" => "#2E7CD6",
            "DeepFieldLight" => "#2570C8",
            _ => theme.Accent,
        };
        var end = theme.Id == "DeepField" ? "#14A79C" : theme.SecondaryAccent;
        var buttonText = theme.IsDark ? "#000000" : "#FFFFFF";
        colors["AccentForeground"] = buttonText;
        colors["GradientStart"] = ColorContrast.Ensure(start, [buttonText], 4.5);
        colors["GradientEnd"] = ColorContrast.Ensure(end, [buttonText], 4.5);
        colors["GradientHoverStart"] = ColorContrast.Ensure(
            ColorContrast.Mix(colors["GradientStart"], theme.IsDark ? "#FFFFFF" : "#000000", .08), [buttonText], 4.5);
        colors["GradientHoverEnd"] = ColorContrast.Ensure(
            ColorContrast.Mix(colors["GradientEnd"], theme.IsDark ? "#FFFFFF" : "#000000", .08), [buttonText], 4.5);
        NormalizeGradient("GradientStart", "GradientEnd");
        NormalizeGradient("GradientHoverStart", "GradientHoverEnd");

        // Opaque selection fills avoid accent text washing out over an alpha gradient.
        colors["SelectionBg"] = ColorContrast.Mix(rail, start, theme.IsDark ? .12 : .08);
        colors["SelectionBgHover"] = ColorContrast.Mix(rail, start, theme.IsDark ? .16 : .12);
        var green = theme.IsDark ? "#66D9A6" : "#18794E";
        var amber = theme.IsDark ? "#F6C76A" : "#9A6700";
        var red = theme.IsDark ? "#FF7B86" : "#CF222E";
        colors["StatusGreenBg"] = ColorContrast.Mix(colors["PanelBg"], green, .08);
        colors["StatusAmberBg"] = ColorContrast.Mix(colors["PanelBg"], amber, .08);
        colors["StatusRedBg"] = ColorContrast.Mix(colors["PanelBg"], red, .08);
        colors["StatusIdleBg"] = colors["PillBg"];
        string[] surfaces =
        [
            colors["AppBg"], colors["PanelBg"], colors["SidePanelBg"], colors["CardBg"],
            colors["CardBgHover"], colors["ActiveTabBg"], colors["InputBg"], colors["PillBg"],
            colors["SelectionBg"], colors["SelectionBgHover"], colors["StatusGreenBg"],
            colors["StatusAmberBg"], colors["StatusRedBg"], colors["StatusIdleBg"], rail,
        ];
        var primary = theme.Id == "DeepField" ? "#E8EDF2" : theme.Id == "DeepFieldLight" ? "#1C2530" : fg;
        colors["TextPrimary"] = ColorContrast.Ensure(primary, surfaces, 7);
        colors["TextMuted"] = ColorContrast.Ensure(ColorContrast.Mix(bg, primary, .74), surfaces, 5);
        colors["TextFaint"] = ColorContrast.Ensure(ColorContrast.Mix(bg, primary, .59), surfaces, 4.5);
        colors["TextPlaceholder"] = colors["TextFaint"];
        colors["StatusIdle"] = colors["TextFaint"];
        colors["AccentBlue"] = ColorContrast.Ensure(theme.Accent, surfaces, 4.5);
        colors["AccentTeal"] = ColorContrast.Ensure(theme.SecondaryAccent, surfaces, 4.5);
        colors["AccentViolet"] = ColorContrast.Ensure(theme.Violet, surfaces, 4.5);
        // Semantic states must remain recognizable, independent of terminal ANSI mappings.
        colors["StatusGreen"] = ColorContrast.Ensure(green, surfaces, 4.5);
        colors["StatusAmber"] = ColorContrast.Ensure(amber, surfaces, 4.5);
        colors["StatusRed"] = ColorContrast.Ensure(red, surfaces, 4.5);
        colors["InputBorder"] = ColorContrast.Ensure(colors["CardBorder"],
            [colors["InputBg"], colors["CardBg"], colors["CardBgHover"]], 3);
        colors["ControlAccentFill"] = colors["GradientStart"];
        colors["ControlAccentFillHover"] = colors["GradientHoverStart"];

        return new(rail, rail, new ReadOnlyDictionary<string, string>(colors));

        void NormalizeGradient(string first, string second)
        {
            var a = colors[first];
            var b = colors[second];
            var target = theme.IsDark ? "#FFFFFF" : "#000000";
            for (var adjustment = 0; adjustment <= 255; adjustment++)
            {
                var x = ColorContrast.Mix(a, target, adjustment / 255d);
                var y = ColorContrast.Mix(b, target, adjustment / 255d);
                // Leave a margin for quantized interpolation between sampled pixels.
                if (!Enumerable.Range(0, 101).All(step =>
                        ColorContrast.Ratio(buttonText, ColorContrast.Mix(x, y, step / 100d)) >= 4.6)) continue;
                colors[first] = x;
                colors[second] = y;
                return;
            }
        }
    }
}

public static class ColorContrast
{
    public static double Ratio(string first, string second)
    {
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    public static string Mix(string first, string second, double amount)
    {
        var a = Parse(first);
        var b = Parse(second);
        amount = Math.Clamp(amount, 0, 1);
        return $"#{Channel(a.R, b.R):X2}{Channel(a.G, b.G):X2}{Channel(a.B, b.B):X2}";
        byte Channel(byte x, byte y) => (byte)Math.Round(x + (y - x) * amount);
    }

    public static string Ensure(string color, IReadOnlyList<string> backgrounds, double minimum)
    {
        if (backgrounds.All(bg => Ratio(color, bg) >= minimum)) return color;
        var target = new[] { "#000000", "#FFFFFF" }
            .MaxBy(candidate => backgrounds.Min(bg => Ratio(candidate, bg)))!;
        // Search quantized sRGB values, so the returned hex actually meets the threshold.
        for (var step = 1; step <= 255; step++)
        {
            var candidate = Mix(color, target, step / 255.0);
            if (backgrounds.All(bg => Ratio(candidate, bg) >= minimum)) return candidate;
        }
        throw new InvalidOperationException("Palette surfaces cannot share a readable foreground.");
    }

    private static double Luminance(string hex)
    {
        var color = Parse(hex);
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        static double Linear(byte channel)
        {
            var value = channel / 255.0;
            return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }
    }

    private static (byte R, byte G, byte B) Parse(string hex)
    {
        if (hex.Length != 7 || hex[0] != '#') throw new ArgumentException("Expected #RRGGBB.", nameof(hex));
        return (byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }
}
