using sutty.Setting;
using System.Text.Json.Nodes;

var scratch = Path.Combine(Path.GetTempPath(), $"sutty-setting-self-test-{Guid.NewGuid():N}");
Directory.CreateDirectory(scratch);
SettingsService.PathOverride = Path.Combine(scratch, "settings.json");
WorkspaceStateStore.PathOverride = Path.Combine(scratch, "workspace.json");

try
{
    Assert(new AppSettings().TerminalMode == "Terminal", "fresh terminal default");
    Assert(new AppSettings().AutoCloseDisconnectedTabs, "ended shell tabs close by default");
    Assert(new AppSettings { TerminalMode = "Repl" }.TerminalMode == "Repl",
        "legacy Repl preference preserved");

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
          "SftpVerificationMode": "unsupported",
          "SftpConflictPolicy": "unexpected",
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
          "RightPanelWidth": 316,
          "RestoreWorkspaceOnStartup": true,
          "ConfirmWorkspaceRestore": true
        }
        """);

    var loaded = SettingsService.Load();
    Assert(loaded.Language == "en", "legacy PascalCase setting load");
    Assert(loaded.TerminalMode == "Terminal", "legacy terminal-mode migration");
    Assert(loaded.DefaultSshPort == 65_535, "port range normalization");
    Assert(!loaded.SftpRetryEnabled, "SFTP retry toggle load");
    Assert(loaded.SftpRetryCount == 10, "SFTP retry-count upper bound");
    Assert(loaded.SftpVerificationMode == "Sha256", "SFTP verification-mode normalization");
    Assert(loaded.SftpConflictPolicy == "Ask", "SFTP conflict-policy normalization");
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
    Assert(loaded.AutoCloseDisconnectedTabs, "legacy settings without the tab-close preference retain the default enabled behavior");
    Assert(loaded.RestoreWorkspaceOnStartup && loaded.ConfirmWorkspaceRestore,
        "workspace restore settings load");

    loaded.HistoryTopHostCount = 4;
    loaded.RightPanelWidth = 420;
    loaded.TerminalTheme = "AtomOneDark";
    loaded.TerminalCursorStyle = "bar";
    loaded.TerminalScrollbackLines = 12_000;
    loaded.SftpRetryEnabled = true;
    loaded.SftpRetryCount = 3;
    loaded.SftpVerificationMode = "SizeOnly";
    loaded.SftpConflictPolicy = "Rename";
    loaded.AutoCloseDisconnectedTabs = false;
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
    Assert(json["SftpVerificationMode"]?.GetValue<string>() == "SizeOnly",
        "SFTP verification mode persistence");
    Assert(json["SftpConflictPolicy"]?.GetValue<string>() == "Rename",
        "SFTP conflict policy persistence");
    Assert(json["AutoCloseDisconnectedTabs"]?.GetValue<bool>() == false && !SettingsService.Load().AutoCloseDisconnectedTabs,
        "keeping ended tabs is persisted and loaded as false");

    var workspaceTabs = Enumerable.Range(0, 20)
        .Select(index => index % 2 == 0
            ? new WorkspaceTabState
            {
                Kind = WorkspaceTabKinds.LocalTerminal,
            }
            : new WorkspaceTabState
            {
                Kind = WorkspaceTabKinds.SavedHost,
                SavedHostId = $"host-{index}",
            })
        .ToList();
    workspaceTabs.Insert(0, new WorkspaceTabState
    {
        Kind = WorkspaceTabKinds.SavedHost,
        SavedHostId = "../../unsafe",
    });
    var workspaceSaved = WorkspaceStateStore.Save(new WorkspaceSnapshot
    {
        Tabs = workspaceTabs,
        SelectedIndex = 99,
    });
    Assert(workspaceSaved.Succeeded, "workspace atomic save");
    var workspace = WorkspaceStateStore.Load();
    Assert(workspace.Tabs.Count == 16, "workspace tab limit");
    Assert(workspace.Tabs.All(tab => tab.Kind != WorkspaceTabKinds.SavedHost ||
                                     !tab.SavedHostId.Contains("..", StringComparison.Ordinal)),
        "workspace invalid Saved Host id rejection");
    Assert(workspace.SelectedIndex == 15, "workspace selected-index normalization");
    workspace.Tabs.Insert(0, new WorkspaceTabState { Kind = "UnknownTabKind" });
    Assert(WorkspaceStateStore.Save(workspace).Succeeded, "unknown workspace kind save");
    workspace = WorkspaceStateStore.Load();
    Assert(workspace.Tabs.All(tab => tab.Kind != "UnknownTabKind"),
        "unknown workspace kind rejection");
    Assert(workspace.Tabs.All(tab => tab.LocalShell ==
                                     (tab.Kind == WorkspaceTabKinds.LocalTerminal
                                         ? WorkspaceLocalShells.PowerShell
                                         : "")),
        "workspace default local shell and Saved Host shell clearing");

    File.WriteAllText(WorkspaceStateStore.WorkspacePath, """
        {
          "Tabs": [
            { "Kind": "LocalTerminal" },
            { "Kind": "LocalTerminal", "LocalShell": "CommandPrompt" },
            { "Kind": "LocalTerminal", "LocalShell": "commandprompt" },
            { "Kind": "LocalTerminal", "LocalShell": "cmd.exe /c injected" },
            { "Kind": "LocalTerminal", "LocalShell": null },
            { "Kind": "SavedHost", "SavedHostId": "host-1", "LocalShell": "CommandPrompt" }
          ],
          "SelectedIndex": 1
        }
        """);
    workspace = WorkspaceStateStore.Load();
    Assert(workspace.Tabs.Select(tab => tab.LocalShell).SequenceEqual([
        WorkspaceLocalShells.PowerShell,
        WorkspaceLocalShells.CommandPrompt,
        WorkspaceLocalShells.CommandPrompt,
        WorkspaceLocalShells.PowerShell,
        WorkspaceLocalShells.PowerShell,
        "",
    ]), "workspace local-shell migration and allowlist");
    Assert(WorkspaceStateStore.Save(workspace).Succeeded, "workspace local-shell save");
    workspace = WorkspaceStateStore.Load();
    Assert(workspace.Tabs[1].LocalShell == WorkspaceLocalShells.CommandPrompt &&
           workspace.Tabs[0].LocalShell == WorkspaceLocalShells.PowerShell &&
           workspace.SelectedIndex == 1,
        "workspace shell selection round trip");
    var workspaceJson = File.ReadAllText(WorkspaceStateStore.WorkspacePath);
    var workspaceTabJson = JsonNode.Parse(workspaceJson)!["Tabs"]!.AsArray();
    Assert(workspaceTabJson.All(tab => tab!.AsObject().All(property =>
            property.Key is "Kind" or "SavedHostId" or "LocalShell")),
        "workspace tab schema excludes credentials and arbitrary commands");
    Assert(WorkspaceStateStore.Clear().Succeeded && !File.Exists(WorkspaceStateStore.WorkspacePath),
        "workspace clear");

    ThemeCatalogSelfTests.Run();
    SettingsResetSelfTests.Run(scratch);
    ShellStateSelfTests.Run();
    MultiSessionSelectionSelfTests.Run();
    await LocalFileBrowserSelfTests.RunAsync(scratch);

    File.WriteAllText(SettingsService.SettingsPath, "{broken");
    SettingsService.ResetForTests();
    var recovered = SettingsService.Load();
    Assert(recovered.HistoryRetentionDays == 60, "corrupt setting fallback");
    Assert(recovered.SftpRetryEnabled && recovered.SftpRetryCount == 3 &&
           recovered.SftpVerificationMode == "Sha256" &&
           recovered.SftpConflictPolicy == "Ask",
        "corrupt setting fallback keeps SFTP retry defaults");

    Console.WriteLine("Settings load, normalization, and persistence self-tests passed.");
}
finally
{
    SettingsService.PathOverride = null;
    SettingsService.ResetForTests();
    WorkspaceStateStore.PathOverride = null;
    Directory.Delete(scratch, recursive: true);
}

static void Assert(bool condition, string name)
{
    if (!condition)
        throw new InvalidOperationException($"Self-test failed: {name}.");
}
