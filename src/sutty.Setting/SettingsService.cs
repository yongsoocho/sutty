using System.Text.Json;

namespace sutty.Setting;

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

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        _current = settings;
    }

    /// <summary>Current를 직접 수정한 뒤 저장할 때 쓰는 축약형.</summary>
    public static void Save() => Save(Current);
}
