using sutty.Setting;
using System.Text.Json;

internal static class SettingsResetSelfTests
{
    public static void Run(string scratch)
    {
        var settingsPath = SettingsService.PathOverride;
        Assert(settingsPath == Path.Combine(scratch, "settings.json"), "reset uses temporary settings only");
        var customized = new AppSettings
        {
            Theme = "Dracula", Language = "en", TerminalFontSize = 27,
            TerminalTheme = "Monokai", MainWindowWidth = 1700,
            ExternalEditorExecutable = @"C:\fixture\editor.exe", RecentConnectionTags = ["fixture"],
        };
        Assert(SettingsService.Save(customized).Succeeded, "custom settings fixture save");
        var before = File.ReadAllText(settingsPath!);
        var currentBefore = SettingsService.Current;
        var invalidFile = Path.Combine(scratch, "existing-directory");
        Directory.CreateDirectory(invalidFile);
        SettingsService.PathOverride = invalidFile;
        try
        {
            var failure = SettingsService.ResetToDefaults();
            Assert(!failure.Succeeded && ReferenceEquals(SettingsService.Current, currentBefore),
                "failed reset preserves current settings object");
            Assert(File.ReadAllText(settingsPath!) == before, "failed reset preserves settings on disk");
        }
        finally
        {
            SettingsService.PathOverride = settingsPath;
        }

        Assert(SettingsService.ResetToDefaults().Succeeded, "atomic defaults reset");
        Assert(!ReferenceEquals(SettingsService.Current, currentBefore), "reset replaces stale settings snapshot");
        // Every persisted field, including fields without an editor, returns to its default.
        var defaults = JsonSerializer.Serialize(new AppSettings(), SettingsJsonContext.Default.AppSettings);
        Assert(JsonSerializer.Serialize(SettingsService.Current, SettingsJsonContext.Default.AppSettings) == defaults,
            "reset restores every settings field");
        SettingsService.ResetForTests();
        Assert(JsonSerializer.Serialize(SettingsService.Load(), SettingsJsonContext.Default.AppSettings) == defaults,
            "default settings survive restart");
        Assert(!Directory.EnumerateFiles(scratch, "*.tmp").Any(), "reset cleans atomic temporary files");
        Console.WriteLine("Temporary settings reset, failure preservation, and persistence tests passed.");
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"Settings reset self-test failed: {name}.");
    }
}
