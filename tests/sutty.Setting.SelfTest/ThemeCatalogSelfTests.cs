using sutty.Setting;

internal static class ThemeCatalogSelfTests
{
    public static void Run()
    {
        var catalog = ThemeCatalog.Presets;
        Assert(catalog.Count >= 30, "popular palette coverage");
        Assert(catalog.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == catalog.Count,
            "unique persisted terminal ids");
        Assert(catalog.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == catalog.Count,
            "unique application theme names");

        foreach (var preset in catalog)
        {
            Assert(preset.AnsiColors.Count == 16 && preset.AnsiColors.All(IsRgb), $"{preset.Name} ANSI palette");
            Assert(new[] { preset.Background, preset.Foreground, preset.Accent, preset.SecondaryAccent, preset.Violet }
                .All(IsRgb), $"{preset.Name} UI palette");
            Assert(preset.Background != preset.Foreground, $"{preset.Name} visible terminal text");
            Assert(preset.Accent != preset.SecondaryAccent, $"{preset.Name} gradient stops");
            Assert(preset.Name.Length <= 64, $"{preset.Name} settings name length");

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

        string[] legacyIds = ["DeepField", "Ubuntu", "AtomOneDark", "Dracula", "GitHubDark", "GitHubLight", "SolarizedDark", "SolarizedLight"];
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
        Console.WriteLine($"Theme catalog: {catalog.Count} named app/terminal palettes and persistence passed.");
    }

    private static bool IsRgb(string value) => value.Length == 7 && value[0] == '#' &&
        value.Skip(1).All(Uri.IsHexDigit);

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"Theme self-test failed: {name}.");
    }
}
