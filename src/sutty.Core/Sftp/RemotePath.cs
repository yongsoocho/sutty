namespace sutty.Core.Sftp;

/// <summary>원격(리눅스) 경로 헬퍼. Windows의 Path.Combine은 '\'를 쓰므로 사용 금지.</summary>
public static class RemotePath
{
    public static string Combine(string directory, string name)
    {
        var parent = Normalize(directory);
        // POSIX permits '\\' in a file name; only '/' is a remote separator.
        var child = name.Trim('/');
        return parent == "/" ? "/" + child : parent + "/" + child;
    }

    /// <summary>절대 POSIX 경로로 정규화하며 '.', '..' 구간을 해석한다.</summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";

        var parts = new List<string>();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        return parts.Count == 0 ? "/" : "/" + string.Join('/', parts);
    }

    /// <summary>파일 경로에서 부모 디렉터리 경로를 얻는다. ("/var/log/syslog" → "/var/log")</summary>
    public static string GetDirectory(string fullPath)
    {
        var normalized = Normalize(fullPath);
        var i = normalized.LastIndexOf('/');
        return i <= 0 ? "/" : normalized[..i];
    }

    public static string GetName(string fullPath)
    {
        var normalized = Normalize(fullPath);
        return normalized == "/" ? "/" : normalized[(normalized.LastIndexOf('/') + 1)..];
    }
}
