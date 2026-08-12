using sutty.Setting;
using System.Text.Json.Nodes;

var scratch = Path.Combine(Path.GetTempPath(), $"sutty-setting-self-test-{Guid.NewGuid():N}");
Directory.CreateDirectory(scratch);
SettingsService.PathOverride = Path.Combine(scratch, "settings.json");

try
{
    File.WriteAllText(SettingsService.SettingsPath, """
        {
          "Theme": "Dark",
          "Language": "en",
          "TerminalFontFamily": "Cascadia Mono",
          "TerminalFontSize": 13,
          "EnableStructuredTextHighlighting": true,
          "EnableSeverityHighlighting": true,
          "EnableCommandSuggestions": true,
          "AcceptSuggestionWithTab": true,
          "DefaultSshPort": 70000,
          "DefaultKeepAliveSeconds": 30,
          "LastAuthMethod": "PublicKey",
          "RecentPrivateKeyPaths": [],
          "RecentConnectionTags": ["prod", "prod"],
          "TerminalMode": "Raw",
          "HistoryRetentionDays": 60,
          "HistoryTopHostCount": 99,
          "MainWindowWidth": 1360,
          "MainWindowHeight": 850,
          "SettingWindowWidth": 520,
          "SettingWindowHeight": 660,
          "RightPanelWidth": 316
        }
        """);

    var loaded = SettingsService.Load();
    Assert(loaded.Language == "en", "legacy PascalCase setting load");
    Assert(loaded.TerminalMode == "Terminal", "legacy terminal-mode migration");
    Assert(loaded.DefaultSshPort == 65_535, "port range normalization");
    Assert(loaded.HistoryTopHostCount == 16, "frequent-host setting normalization");
    Assert(loaded.RecentConnectionTags.SequenceEqual(["prod"]), "recent-tag deduplication");
    Assert(loaded.EnableStructuredTextHighlighting, "structured highlighting setting load");
    Assert(loaded.EnableSeverityHighlighting, "severity highlighting setting load");
    Assert(loaded.EnableCommandSuggestions, "suggestion setting load");

    loaded.HistoryTopHostCount = 4;
    loaded.RightPanelWidth = 420;
    var saved = SettingsService.Save(loaded);
    Assert(saved.Succeeded, "atomic setting save");
    Assert(!Directory.EnumerateFiles(scratch, "*.tmp").Any(), "setting temp cleanup");

    var json = JsonNode.Parse(File.ReadAllText(SettingsService.SettingsPath))!.AsObject();
    Assert(json["HistoryTopHostCount"]?.GetValue<int>() == 4, "PascalCase setting compatibility");
    Assert(json["RightPanelWidth"]?.GetValue<int>() == 420, "panel-width persistence");

    File.WriteAllText(SettingsService.SettingsPath, "{broken");
    SettingsService.ResetForTests();
    var recovered = SettingsService.Load();
    Assert(recovered.HistoryRetentionDays == 60, "corrupt setting fallback");

    Console.WriteLine("Settings load, normalization, and persistence self-tests passed.");
}
finally
{
    SettingsService.PathOverride = null;
    SettingsService.ResetForTests();
    Directory.Delete(scratch, recursive: true);
}

static void Assert(bool condition, string name)
{
    if (!condition)
        throw new InvalidOperationException($"Self-test failed: {name}.");
}
