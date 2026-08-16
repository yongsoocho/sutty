using System.Text.Json;

namespace sutty.Setting;

/// <summary>
/// One restorable tab. SSH entries retain only an opaque Saved Host id; connection
/// secrets and ad-hoc host details are deliberately excluded from this schema.
/// </summary>
public sealed class WorkspaceTabState
{
    public string Kind { get; set; } = WorkspaceTabKinds.LocalTerminal;
    public string SavedHostId { get; set; } = "";
}

public static class WorkspaceTabKinds
{
    public const string LocalTerminal = "LocalTerminal";
    public const string SavedHost = "SavedHost";
}

/// <summary>A bounded snapshot of the tabs that were open when Sutty last ran.</summary>
public sealed class WorkspaceSnapshot
{
    public int Version { get; set; } = 1;
    public List<WorkspaceTabState> Tabs { get; set; } = [];
    public int SelectedIndex { get; set; } = -1;
    public DateTimeOffset SavedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public readonly record struct WorkspaceSaveResult(bool Succeeded, Exception? Error)
{
    public static WorkspaceSaveResult Success() => new(true, null);
    public static WorkspaceSaveResult Failure(Exception error) => new(false, error);
}

/// <summary>
/// Stores the last workspace in a small atomic JSON document under LocalAppData.
/// The store is intentionally independent from settings.json because tab changes are
/// more frequent than preference changes and must never carry credentials.
/// </summary>
public static class WorkspaceStateStore
{
    private const int MaximumTabs = 16;
    private static readonly object SaveGate = new();

    internal static string? PathOverride { get; set; }

    public static string WorkspacePath => PathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "sutty", "workspace.json");

    public static WorkspaceSnapshot Load()
    {
        try
        {
            if (!File.Exists(WorkspacePath))
                return new WorkspaceSnapshot();

            var snapshot = JsonSerializer.Deserialize(
                File.ReadAllText(WorkspacePath),
                SettingsJsonContext.Default.WorkspaceSnapshot);
            return Normalize(snapshot);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                      System.Security.SecurityException or JsonException or
                                      NotSupportedException)
        {
            return new WorkspaceSnapshot();
        }
    }

    public static WorkspaceSaveResult Save(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (SaveGate)
        {
            var normalized = Normalize(snapshot);
            var directory = Path.GetDirectoryName(WorkspacePath)!;
            var temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(WorkspacePath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                Directory.CreateDirectory(directory);
                var json = JsonSerializer.Serialize(
                    normalized,
                    SettingsJsonContext.Default.WorkspaceSnapshot);
                using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    16 * 1024,
                    FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(WorkspacePath))
                    File.Replace(temporaryPath, WorkspacePath, destinationBackupFileName: null);
                else
                    File.Move(temporaryPath, WorkspacePath);
                return WorkspaceSaveResult.Success();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                          System.Security.SecurityException or JsonException or
                                          NotSupportedException)
            {
                TryDelete(temporaryPath);
                return WorkspaceSaveResult.Failure(error);
            }
        }
    }

    public static WorkspaceSaveResult Clear()
    {
        lock (SaveGate)
        {
            try
            {
                if (File.Exists(WorkspacePath))
                    File.Delete(WorkspacePath);
                return WorkspaceSaveResult.Success();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                          System.Security.SecurityException)
            {
                return WorkspaceSaveResult.Failure(error);
            }
        }
    }

    private static WorkspaceSnapshot Normalize(WorkspaceSnapshot? snapshot)
    {
        var tabs = new List<WorkspaceTabState>(MaximumTabs);
        foreach (var tab in snapshot?.Tabs ?? [])
        {
            if (tabs.Count >= MaximumTabs || tab is null)
                break;

            var kind = tab.Kind switch
            {
                var value when string.Equals(
                    value,
                    WorkspaceTabKinds.LocalTerminal,
                    StringComparison.OrdinalIgnoreCase) => WorkspaceTabKinds.LocalTerminal,
                var value when string.Equals(
                    value,
                    WorkspaceTabKinds.SavedHost,
                    StringComparison.OrdinalIgnoreCase) => WorkspaceTabKinds.SavedHost,
                _ => "",
            };
            if (kind.Length == 0)
                continue;
            var savedHostId = NormalizeSavedHostId(tab.SavedHostId);
            if (kind == WorkspaceTabKinds.SavedHost && savedHostId.Length == 0)
                continue;

            tabs.Add(new WorkspaceTabState
            {
                Kind = kind,
                SavedHostId = kind == WorkspaceTabKinds.SavedHost ? savedHostId : "",
            });
        }

        var selectedIndex = tabs.Count == 0
            ? -1
            : Math.Clamp(snapshot?.SelectedIndex ?? 0, 0, tabs.Count - 1);
        var savedAt = snapshot?.SavedAtUtc ?? default;
        if (savedAt == default || savedAt > DateTimeOffset.UtcNow.AddMinutes(5))
            savedAt = DateTimeOffset.UtcNow;

        return new WorkspaceSnapshot
        {
            Version = 1,
            Tabs = tabs,
            SelectedIndex = selectedIndex,
            SavedAtUtc = savedAt,
        };
    }

    private static string NormalizeSavedHostId(string? value)
    {
        var normalized = value?.Trim() ?? "";
        return normalized.Length is > 0 and <= 128 &&
               normalized.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            ? normalized
            : "";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A uniquely named temporary file is harmless; preserve the original failure.
        }
    }
}
