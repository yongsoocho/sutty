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
          "TerminalTheme": "untrusted-theme",
          "TerminalCursorStyle": "giant-block",
          "TerminalCursorBlink": false,
          "TerminalScrollbackLines": 999999,
          "TerminalScreenReaderMode": true,
          "LoadLocalShellProfile": false,
          "EnableStructuredTextHighlighting": true,
          "EnableSeverityHighlighting": true,
          "EnableCommandSuggestions": true,
          "AcceptSuggestionWithTab": true,
          "DefaultSshPort": 70000,
          "DefaultKeepAliveSeconds": 30,
          "SftpRetryEnabled": false,
          "SftpRetryCount": 99,
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
    Assert(!loaded.SftpRetryEnabled, "SFTP retry toggle load");
    Assert(loaded.SftpRetryCount == 10, "SFTP retry-count upper bound");
    Assert(loaded.HistoryTopHostCount == 16, "frequent-host setting normalization");
    Assert(loaded.RecentConnectionTags.SequenceEqual(["prod"]), "recent-tag deduplication");
    Assert(loaded.EnableStructuredTextHighlighting, "structured highlighting setting load");
    Assert(loaded.EnableSeverityHighlighting, "severity highlighting setting load");
    Assert(loaded.EnableCommandSuggestions, "suggestion setting load");
    Assert(loaded.TerminalTheme == "FollowApplication", "terminal theme allowlist normalization");
    Assert(loaded.TerminalCursorStyle == "underline", "terminal cursor normalization");
    Assert(loaded.TerminalScrollbackLines == 50_000, "terminal scrollback upper bound");
    Assert(!loaded.TerminalCursorBlink, "terminal cursor-blink setting load");
    Assert(loaded.TerminalScreenReaderMode, "terminal accessibility setting load");
    Assert(!loaded.LoadLocalShellProfile, "local shell-profile setting load");

    loaded.HistoryTopHostCount = 4;
    loaded.RightPanelWidth = 420;
    loaded.TerminalTheme = "AtomOneDark";
    loaded.TerminalCursorStyle = "bar";
    loaded.TerminalScrollbackLines = 12_000;
    loaded.SftpRetryEnabled = true;
    loaded.SftpRetryCount = 3;
    var saved = SettingsService.Save(loaded);
    Assert(saved.Succeeded, "atomic setting save");
    Assert(!Directory.EnumerateFiles(scratch, "*.tmp").Any(), "setting temp cleanup");

    var json = JsonNode.Parse(File.ReadAllText(SettingsService.SettingsPath))!.AsObject();
    Assert(json["HistoryTopHostCount"]?.GetValue<int>() == 4, "PascalCase setting compatibility");
    Assert(json["RightPanelWidth"]?.GetValue<int>() == 420, "panel-width persistence");
    Assert(json["TerminalTheme"]?.GetValue<string>() == "AtomOneDark", "terminal-theme persistence");
    Assert(json["TerminalCursorStyle"]?.GetValue<string>() == "bar", "terminal-cursor persistence");
    Assert(json["TerminalScrollbackLines"]?.GetValue<int>() == 12_000, "terminal-scrollback persistence");
    Assert(json["SftpRetryEnabled"]?.GetValue<bool>() == true, "SFTP retry toggle persistence");
    Assert(json["SftpRetryCount"]?.GetValue<int>() == 3, "SFTP retry count persistence");

    File.WriteAllText(SettingsService.SettingsPath, "{broken");
    SettingsService.ResetForTests();
    var recovered = SettingsService.Load();
    Assert(recovered.HistoryRetentionDays == 60, "corrupt setting fallback");
    Assert(recovered.SftpRetryEnabled && recovered.SftpRetryCount == 3,
        "corrupt setting fallback keeps SFTP retry defaults");

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
