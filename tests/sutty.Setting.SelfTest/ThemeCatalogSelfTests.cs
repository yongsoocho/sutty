using sutty.Setting;
using System.Xml.Linq;

internal static class ThemeCatalogSelfTests
{
    public static void Run()
    {
        var catalog = ThemeCatalog.Presets;
        Assert(catalog.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == catalog.Count,
            "unique persisted terminal ids");
        Assert(catalog.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == catalog.Count,
            "unique application theme names");
        VerifyContrastReferenceValues();
        VerifyNativeResourceCoverage(catalog[0]);

        foreach (var preset in catalog)
        {
            Assert(preset.AnsiColors.Count == 16 && preset.AnsiColors.All(IsRgb), $"{preset.Name} ANSI palette");
            Assert(new[] { preset.Background, preset.Foreground, preset.Accent, preset.SecondaryAccent, preset.Violet }
                .All(IsRgb), $"{preset.Name} UI palette");
            Assert(preset.Background != preset.Foreground, $"{preset.Name} visible terminal text");
            Assert(preset.Accent != preset.SecondaryAccent, $"{preset.Name} gradient stops");
            Assert(preset.Name.Length <= 64, $"{preset.Name} settings name length");
            VerifyApplicationPalette(preset);

            // Exercise the real settings save/load normalization, not just the catalog lookup.
            var result = SettingsService.Save(new AppSettings
            {
                Theme = preset.Name,
                TerminalTheme = preset.Id.ToLowerInvariant(),
            });
            Assert(result.Succeeded, $"{preset.Name} settings save");
            SettingsService.ResetForTests();
            var loaded = SettingsService.Load();
            Assert(loaded.Theme == preset.Name && loaded.TerminalTheme == preset.Id,
                $"{preset.Name} settings round trip and case normalization");
            Assert(ThemeCatalog.ResolveTerminal(ThemeCatalog.FollowApplication, loaded.Theme).Id == preset.Id,
                $"{preset.Name} follows named application palette");
        }

        string[] legacyIds =
        [
            "DeepField", "DeepFieldLight", "VSCodeDarkPlus", "VSCodeLightPlus", "VSCodeDarkModern",
            "VSCodeLightModern", "Dracula", "Monokai", "MonokaiDimmed", "AtomOneDark", "AtomOneLight",
            "GitHubDark", "GitHubLight", "GitHubDarkDimmed", "SolarizedDark", "SolarizedLight", "Nord",
            "TokyoNight", "TokyoNightStorm", "TokyoNightLight", "CatppuccinMocha", "CatppuccinMacchiato",
            "CatppuccinFrappe", "CatppuccinLatte", "GruvboxDark", "GruvboxLight", "NightOwl", "LightOwl",
            "Material", "MaterialPalenight", "AyuDark", "AyuLight", "AyuMirage", "Cobalt2", "SynthWave84", "Ubuntu",
        ];
        Assert(legacyIds.All(id => ThemeCatalog.NormalizeTerminalId(id) == id), "legacy terminal ids survive");
        Assert(ThemeCatalog.NormalizeTerminalId("untrusted-theme") == ThemeCatalog.FollowApplication,
            "unknown terminal ids fall back safely");
        Assert(ThemeCatalog.ResolveTerminal("unknown", "Dracula").Id == "Dracula",
            "unknown terminal ids follow the named app palette");
        Assert(ThemeCatalog.ResolveTerminal(null, "GitHub Light").Id == "GitHubLight",
            "missing terminal preference follows light palette");
        Assert(ThemeCatalog.ResolveTerminal("Nord", "Dracula").Id == "Nord", "independent terminal override");
        var dracula = ThemeCatalog.ResolveTerminal(ThemeCatalog.FollowApplication, "Dracula");
        var monokai = ThemeCatalog.ResolveTerminal(ThemeCatalog.FollowApplication, "Monokai");
        Assert(dracula.IsDark && monokai.IsDark && dracula.Background != monokai.Background &&
               !dracula.AnsiColors.SequenceEqual(monokai.AnsiColors),
            "dark-to-dark switch changes the actual background and ANSI palette");
        Assert(ThemeCatalog.FindApplication("unknown").Name == "Dark", "unknown application fallback");
        Assert(catalog.Count >= 50, "popular palette coverage");
        Console.WriteLine($"Theme catalog: {catalog.Count} named app/terminal palettes, persistence, and UI contrast passed.");
    }

    private static void VerifyApplicationPalette(ThemeDefinition preset)
    {
        var originalAnsi = preset.AnsiColors.ToArray();
        var palette = ApplicationPalette.Create(preset);
        var colors = palette.Colors;
        string[] surfaceKeys =
        [
            "AppBg", "PanelBg", "SidePanelBg", "CardBg", "CardBgHover", "InputBg", "PillBg",
            "ActiveTabBg", "SelectionBg", "SelectionBgHover",
            "StatusGreenBg", "StatusAmberBg", "StatusRedBg", "StatusIdleBg",
        ];
        string[] textKeys = ["TextPrimary", "TextMuted", "TextFaint", "TextPlaceholder"];
        string[] accentKeys = ["AccentBlue", "AccentTeal", "AccentViolet", "StatusGreen", "StatusAmber", "StatusRed", "StatusIdle"];
        string[] controlKeys =
        [
            "CardBorder", "ShellBorder", "InputBorder", "Divider", "OutputGuide", "TerminalBg", "TerminalFg",
            "AccentForeground", "GradientStart", "GradientEnd", "GradientHoverStart", "GradientHoverEnd",
            "ControlAccentFill", "ControlAccentFillHover",
        ];
        foreach (var key in surfaceKeys.Concat(textKeys).Concat(accentKeys).Concat(controlKeys))
            Assert(colors.TryGetValue(key, out var value) && IsRgb(value), $"{preset.Name} valid {key} role");
        Assert(colors.Values.All(IsRgb) && IsRgb(palette.RailTop) && IsRgb(palette.RailBottom),
            $"{preset.Name} valid derived RGB values");
        var green = Channels(colors["StatusGreen"]);
        var amber = Channels(colors["StatusAmber"]);
        var red = Channels(colors["StatusRed"]);
        Assert(green.G > green.R && green.G > green.B && amber.R > amber.G && amber.G > amber.B &&
               red.R > red.G && red.R > red.B, $"{preset.Name} semantic states preserve recognizable hues");

        var surfaces = surfaceKeys.Select(key => (Name: key, Color: colors[key]))
            .Append(("RailTop", palette.RailTop))
            .Append(("RailBottom", palette.RailBottom));
        foreach (var (surfaceName, surfaceColor) in surfaces)
        {
            foreach (var role in textKeys.Concat(accentKeys))
                AssertContrast(colors[role], surfaceColor, 4.5, $"{preset.Name} {role} on {surfaceName}");
        }
        AssertContrast(colors["InputBorder"], colors["InputBg"], 3, $"{preset.Name} input boundary");
        AssertContrast(colors["InputBorder"], colors["CardBgHover"], 3, $"{preset.Name} hovered input boundary");
        AssertContrast(colors["AccentForeground"], colors["ControlAccentFill"], 4.5, $"{preset.Name} checked control glyph");
        AssertContrast(colors["AccentForeground"], colors["ControlAccentFillHover"], 4.5, $"{preset.Name} hovered checked control glyph");
        VerifyGradient(colors, "GradientStart", "GradientEnd", preset.Name);
        VerifyGradient(colors, "GradientHoverStart", "GradientHoverEnd", preset.Name);

        Assert(colors["TerminalBg"].Equals(preset.Background, StringComparison.OrdinalIgnoreCase) &&
               colors["TerminalFg"].Equals(preset.Foreground, StringComparison.OrdinalIgnoreCase),
            $"{preset.Name} terminal and application follow the same source palette");
        Assert(preset.AnsiColors.SequenceEqual(originalAnsi), $"{preset.Name} UI derivation preserves terminal ANSI values");
        var repeated = ApplicationPalette.Create(preset);
        Assert(colors.OrderBy(pair => pair.Key).SequenceEqual(repeated.Colors.OrderBy(pair => pair.Key)) &&
               palette.RailTop == repeated.RailTop && palette.RailBottom == repeated.RailBottom,
            $"{preset.Name} deterministic derived colors");
    }

    private static void VerifyGradient(IReadOnlyDictionary<string, string> colors, string startKey, string endKey, string theme)
    {
        for (var step = 0; step <= 20; step++)
        {
            var background = ColorContrast.Mix(colors[startKey], colors[endKey], step / 20d);
            AssertContrast(colors["AccentForeground"], background, 4.5, $"{theme} {startKey} gradient sample {step}");
        }
    }

    private static void VerifyContrastReferenceValues()
    {
        Assert(Math.Abs(ColorContrast.Ratio("#000000", "#FFFFFF") - 21) < 0.000001,
            "black and white WCAG reference contrast");
        Assert(Math.Abs(ColorContrast.Ratio("#777777", "#777777") - 1) < 0.000001,
            "identical colors reference contrast");
        Assert(Math.Abs(ColorContrast.Ratio("#777777", "#FFFFFF") - 4.478089) < 0.000001,
            "sRGB gamma correction reference contrast");
        Assert(ColorContrast.Mix("#000000", "#FFFFFF", 0.5).Equals("#808080", StringComparison.OrdinalIgnoreCase),
            "sRGB gradient interpolation reference color");
    }

    private static void VerifyNativeResourceCoverage(ThemeDefinition preset)
    {
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var path = Path.Combine(FindRepositoryRoot(), "src", "sutty.UI", "Styles", "Palette.xaml");
        var document = XDocument.Load(path);
        var dictionaries = document.Descendants(ui + "ResourceDictionary")
            .Where(element => (string?)element.Attribute(xaml + "Key") is "Dark" or "Light").ToArray();
        Assert(dictionaries.Length == 2, "native dark/light theme dictionaries exist");
        string[] gradientRoles = ["GradientStart", "GradientEnd", "GradientHoverStart", "GradientHoverEnd"];
        var roles = ApplicationPalette.Create(preset).Colors.Keys.Except(gradientRoles).ToArray();
        var keySets = new List<string[]>();
        foreach (var dictionary in dictionaries)
        {
            var name = (string)dictionary.Attribute(xaml + "Key")!;
            var keyed = dictionary.Elements().Where(element => element.Attribute(xaml + "Key") is not null).ToArray();
            Assert(keyed.Select(element => (string)element.Attribute(xaml + "Key")!).Distinct().Count() == keyed.Length,
                $"native {name} has unique resource keys");
            var resources = keyed.ToDictionary(element => (string)element.Attribute(xaml + "Key")!, StringComparer.Ordinal);
            keySets.Add(resources.Keys.Order(StringComparer.Ordinal).ToArray());
            foreach (var role in roles)
                Assert(resources.TryGetValue(role, out var resource) && resource.Name == ui + "SolidColorBrush",
                    $"native {name} provides mutable {role} brush");
            foreach (var alias in keyed.Where(element => element.Name == ui + "StaticResource"))
            {
                var target = (string?)alias.Attribute("ResourceKey");
                Assert(target is not null && resources.ContainsKey(target),
                    $"native {name} alias {alias.Attribute(xaml + "Key")?.Value} points to a defined role");
            }
            foreach (var key in new[] { "AccentGradient", "AccentGradientHover", "AccentIndicator", "NavigationViewSelectionIndicatorForeground" })
                Assert(resources.TryGetValue(key, out var resource) && resource.Name == ui + "LinearGradientBrush" &&
                       resource.Elements(ui + "GradientStop").Count() == 2, $"native {name} mutable {key} gradient");
        }
        Assert(keySets[0].SequenceEqual(keySets[1]), "native dark/light dictionaries expose identical resource keys");
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "src", "sutty.UI", "Styles", "Palette.xaml")))
                    return directory.FullName;
        }
        throw new DirectoryNotFoundException("Theme self-tests require the source repository's Palette.xaml.");
    }

    private static void AssertContrast(string foreground, string background, double minimum, string name)
    {
        var ratio = ColorContrast.Ratio(foreground, background);
        Assert(ratio + 0.000001 >= minimum, $"{name} contrast {ratio:F3} >= {minimum:F1}");
    }

    private static bool IsRgb(string value) => value.Length == 7 && value[0] == '#' &&
        value.Skip(1).All(Uri.IsHexDigit);

    private static (byte R, byte G, byte B) Channels(string color) =>
        (Convert.ToByte(color[1..3], 16), Convert.ToByte(color[3..5], 16), Convert.ToByte(color[5..7], 16));

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"Theme self-test failed: {name}.");
    }
}
