namespace sutty.Core.Sftp;

/// <summary>원격(리눅스) 경로 헬퍼. Windows의 Path.Combine은 '\'를 쓰므로 사용 금지.</summary>
public static class RemotePath
{
    public static string Combine(string directory, string name) =>
        directory == "/" ? "/" + name : directory.TrimEnd('/') + "/" + name;

    /// <summary>파일 경로에서 부모 디렉터리 경로를 얻는다. ("/var/log/syslog" → "/var/log")</summary>
    public static string GetDirectory(string fullPath)
    {
        var i = fullPath.LastIndexOf('/');
        return i <= 0 ? "/" : fullPath[..i];
    }
}
