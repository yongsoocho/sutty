using sutty.Core.Models;

namespace sutty.Core.Sftp;

/// <summary>
/// 리눅스 서버 파일 시스템을 흉내 내는 가짜 SFTP 서비스.
/// 실제 연결 없이 UI(파일 트리)를 개발/테스트하기 위한 용도.
/// </summary>
public sealed class MockSftpService : ISftpService
{
    private readonly Dictionary<string, List<RemoteFileEntry>> _dirs = new();

    public MockSftpService(string username = "admin")
    {
        BuildSampleTree(string.IsNullOrWhiteSpace(username) ? "admin" : username);
    }

    public async Task<IReadOnlyList<RemoteFileEntry>> ListDirectoryAsync(string path, CancellationToken ct = default)
    {
        await Task.Delay(25, ct); // 네트워크 왕복 흉내
        var key = path == "/" ? "/" : path.TrimEnd('/');
        return _dirs.TryGetValue(key, out var entries)
            ? entries
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : Array.Empty<RemoteFileEntry>();
    }

    // ── 샘플 트리 구성 ──

    private void BuildSampleTree(string user)
    {
        _dirs["/"] = [];

        AddDir("/", "bin");
        AddFile("/bin", "bash", 1_396_520);
        AddFile("/bin", "ls", 142_144);
        AddFile("/bin", "ssh", 773_704);

        AddDir("/", "etc");
        AddFile("/etc", "hostname", 12);
        AddFile("/etc", "hosts", 221);
        AddFile("/etc", "passwd", 1_843);
        AddDir("/etc", "ssh");
        AddFile("/etc/ssh", "ssh_config", 1_650);
        AddFile("/etc/ssh", "sshd_config", 3_287);
        AddDir("/etc", "nginx");
        AddFile("/etc/nginx", "nginx.conf", 1_490);
        AddDir("/etc/nginx", "sites-enabled");
        AddFile("/etc/nginx/sites-enabled", "default", 2_412);

        AddDir("/", "home");
        var home = "/home/" + user;
        AddDir("/home", user);
        AddFile(home, ".bashrc", 3_771);
        AddFile(home, ".profile", 807);
        AddFile(home, "notes.txt", 1_204, ageDays: 1);
        AddDir(home, ".ssh");
        AddFile(home + "/.ssh", "authorized_keys", 415);
        AddFile(home + "/.ssh", "id_ed25519.pub", 98);
        AddFile(home + "/.ssh", "known_hosts", 2_208);
        AddDir(home, "projects");
        AddFile(home + "/projects", "deploy.sh", 1_562);
        AddDir(home + "/projects", "sutty");
        AddFile(home + "/projects/sutty", "README.md", 4_120);

        AddDir("/", "opt");
        AddDir("/", "tmp");

        AddDir("/", "var");
        AddDir("/var", "log");
        AddFile("/var/log", "auth.log", 88_213, ageDays: 0);
        AddFile("/var/log", "syslog", 402_118, ageDays: 0);
        AddDir("/var/log", "nginx");
        AddFile("/var/log/nginx", "access.log", 1_204_552, ageDays: 0);
        AddFile("/var/log/nginx", "error.log", 18_226, ageDays: 0);
        AddDir("/var", "www");
        AddDir("/var/www", "html");
        AddFile("/var/www/html", "index.html", 10_671);
    }

    private void AddDir(string parent, string name)
    {
        var full = Combine(parent, name);
        Ensure(parent).Add(new RemoteFileEntry
        {
            Name = name,
            FullPath = full,
            IsDirectory = true,
            Modified = DateTime.Now.AddDays(-14),
        });
        Ensure(full);
    }

    private void AddFile(string parent, string name, long size, int ageDays = 7)
    {
        Ensure(parent).Add(new RemoteFileEntry
        {
            Name = name,
            FullPath = Combine(parent, name),
            Size = size,
            Modified = DateTime.Now.AddDays(-ageDays),
        });
    }

    private List<RemoteFileEntry> Ensure(string path)
    {
        if (!_dirs.TryGetValue(path, out var list))
        {
            list = [];
            _dirs[path] = list;
        }
        return list;
    }

    private static string Combine(string parent, string name) => RemotePath.Combine(parent, name);

    /// <summary>가짜 업로드: 약 3초 동안 진행도를 보고하고, 완료되면 mock 트리에 파일을 추가한다.</summary>
    public async Task UploadFileAsync(string localPath, string remoteDirectory,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        long size = 0;
        try { size = new FileInfo(localPath).Length; } catch { /* 크기 모름 → 0 */ }

        const int steps = 25;
        for (int i = 1; i <= steps; i++)
        {
            await Task.Delay(120, ct);
            progress?.Report(i / (double)steps);
        }

        var dir = remoteDirectory == "/" ? "/" : remoteDirectory.TrimEnd('/');
        var name = Path.GetFileName(localPath);
        Ensure(dir).RemoveAll(e => !e.IsDirectory && e.Name == name); // 같은 이름은 덮어쓰기
        AddFile(dir, name, size, ageDays: 0);
    }
}
