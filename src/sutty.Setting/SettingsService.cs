using System.Text.Json;

namespace sutty.Setting;

/// <summary>
/// Result of persisting application settings. Save failures are returned to the caller
/// so UI event handlers can report them without allowing file-system exceptions to escape.
/// </summary>
public readonly record struct SettingsSaveResult(bool Succeeded, Exception? Error)
{
    public static SettingsSaveResult Success() => new(true, null);
    public static SettingsSaveResult Failure(Exception error) => new(false, error);
}

/// <summary>
/// %LOCALAPPDATA%\sutty\settings.json 에 설정을 저장/로드한다.
/// Current는 첫 접근 시 파일에서 읽어 캐시한다. 앱 시작 시 Load()를 한 번 호출할 것.
///
/// [저장소 선택: JSON vs SQLite]
/// 설정처럼 "객체 하나"를 통째로 읽고 쓰는 데이터는 JSON이 가장 단순하다
/// (스키마/마이그레이션/네이티브 DLL 불필요, 사람이 직접 열어 고칠 수 있음).
/// 접속 히스토리처럼 행이 늘어나는 목록 데이터가 생기면 그때 SQLite를 도입한다.
/// </summary>
public static class SettingsService
{
    private static AppSettings? _current;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object SaveGate = new();

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "sutty", "settings.json");

    public static AppSettings Current => _current ??= Load();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                if (loaded is not null)
                    return _current = loaded;
            }
        }
        catch (Exception)
        {
            // 파일이 손상됐으면 기본값으로 시작한다
        }
        return _current = new AppSettings();
    }

    /// <summary>
    /// Writes a complete JSON document to a same-directory temporary file, flushes it,
    /// then atomically replaces the previous file (or moves it into place on first save).
    /// Expected serialization and file-system failures are returned instead of thrown.
    /// </summary>
    public static SettingsSaveResult Save(AppSettings settings)
    {
        if (settings is null)
            return SettingsSaveResult.Failure(new ArgumentNullException(nameof(settings)));

        lock (SaveGate)
        {
            var directory = Path.GetDirectoryName(SettingsPath)!;
            var tempPath = Path.Combine(
                directory,
                $".{Path.GetFileName(SettingsPath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                Directory.CreateDirectory(directory);

                var json = JsonSerializer.Serialize(settings, JsonOptions);
                using (var stream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 16 * 1024,
                    FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(SettingsPath))
                    File.Replace(tempPath, SettingsPath, destinationBackupFileName: null);
                else
                    File.Move(tempPath, SettingsPath);

                _current = settings;
                return SettingsSaveResult.Success();
            }
            catch (Exception ex) when (IsExpectedSaveFailure(ex))
            {
                TryDeleteTemporaryFile(tempPath);
                return SettingsSaveResult.Failure(ex);
            }
        }
    }

    /// <summary>Current를 직접 수정한 뒤 저장할 때 쓰는 축약형.</summary>
    public static SettingsSaveResult Save() => Save(Current);

    private static bool IsExpectedSaveFailure(Exception exception) => exception is
        IOException or
        UnauthorizedAccessException or
        System.Security.SecurityException or
        JsonException or
        NotSupportedException;

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The original failure is more useful to the caller. A uniquely named temp
            // file is harmless and may be cleaned up by the user or a later maintenance pass.
        }
    }
}
