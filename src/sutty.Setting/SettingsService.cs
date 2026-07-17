using System.Text.Json;

namespace sutty.Setting;

/// <summary>
/// %LOCALAPPDATA%\sutty\settings.json 에 설정을 저장/로드한다.
/// Current는 첫 접근 시 파일에서 읽어 캐시한다.
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
}
