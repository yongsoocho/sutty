using sutty.Core.Models;

namespace sutty.Core.Sftp;

/// <summary>
/// 세션이 없을 때 파일 패널에 "내 컴퓨터"를 보여주기 위한 로컬 파일 시스템 어댑터.
/// 경로 "/"는 드라이브 목록(C:\, D:\ …)으로 매핑된다.
/// </summary>
public sealed class LocalFileService : ISftpService
{
    public Task<IReadOnlyList<RemoteFileEntry>> ListDirectoryAsync(string path, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<RemoteFileEntry>>(() =>
        {
            if (path == "/")
            {
                return DriveInfo.GetDrives()
                    .Where(d => d.IsReady)
                    .Select(d => new RemoteFileEntry
                    {
                        Name = d.Name.TrimEnd('\\'),
                        FullPath = d.Name,
                        IsDirectory = true,
                    })
                    .ToList();
            }

            var dir = new DirectoryInfo(path);
            if (!dir.Exists)
                return Array.Empty<RemoteFileEntry>();

            var list = new List<RemoteFileEntry>();
            try
            {
                foreach (var d in dir.EnumerateDirectories())
                {
                    if ((d.Attributes & FileAttributes.Hidden) != 0) continue;
                    list.Add(new RemoteFileEntry
                    {
                        Name = d.Name,
                        FullPath = d.FullName,
                        IsDirectory = true,
                        Modified = d.LastWriteTime,
                    });
                }
                foreach (var f in dir.EnumerateFiles())
                {
                    if ((f.Attributes & FileAttributes.Hidden) != 0) continue;
                    list.Add(new RemoteFileEntry
                    {
                        Name = f.Name,
                        FullPath = f.FullName,
                        Size = f.Length,
                        Modified = f.LastWriteTime,
                    });
                }
            }
            catch (UnauthorizedAccessException)
            {
                // 접근 불가 폴더는 조용히 비워 둔다
            }

            return list
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, ct);

    /// <summary>로컬 모드의 "업로드"는 로컬 복사.</summary>
    public async Task UploadFileAsync(string localPath, string remoteDirectory,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var dest = Path.Combine(remoteDirectory, Path.GetFileName(localPath));
        await Task.Run(() => File.Copy(localPath, dest, overwrite: true), ct);
        progress?.Report(1.0);
    }
}
