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
    public Task UploadFileAsync(string localPath, string remoteDirectory, bool overwrite = false,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var dest = Path.Combine(remoteDirectory, Path.GetFileName(localPath));
        return CopyFileSafelyAsync(localPath, dest, overwrite, progress, ct);
    }

    public Task DownloadFileAsync(string remotePath, string localPath, bool overwrite = false,
        IProgress<double>? progress = null, CancellationToken ct = default)
        => CopyFileSafelyAsync(remotePath, localPath, overwrite, progress, ct);

    public Task MoveAsync(string sourcePath, string destinationPath, CancellationToken ct = default)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
                throw new IOException($"Path already exists: {destinationPath}");
            if (Directory.Exists(sourcePath)) Directory.Move(sourcePath, destinationPath);
            else File.Move(sourcePath, destinationPath);
        }, ct);

    public Task DeleteFileAsync(string path, CancellationToken ct = default)
        => Task.Run(() => { ct.ThrowIfCancellationRequested(); File.Delete(path); }, ct);

    public Task DeleteDirectoryAsync(string path, CancellationToken ct = default)
        => Task.Run(() => { ct.ThrowIfCancellationRequested(); Directory.Delete(path, recursive: false); }, ct);

    public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(path) || Directory.Exists(path))
                throw new IOException($"Path already exists: {path}");
            Directory.CreateDirectory(path);
        }, ct);

    private static Task CopyFileSafelyAsync(string sourcePath, string destinationPath, bool overwrite,
        IProgress<double>? progress, CancellationToken ct) => Task.Run(() =>
    {
        if (Directory.Exists(destinationPath))
            throw new IOException($"A directory already exists at the destination: {destinationPath}");
        if (!overwrite && File.Exists(destinationPath))
            throw new IOException($"File already exists: {destinationPath}");

        progress?.Report(0.0);
        var temporaryPath = destinationPath + $".sutty-{Guid.NewGuid():N}.part";
        try
        {
            using (var source = File.OpenRead(sourcePath))
            using (var destination = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var total = source.Length;
                var buffer = new byte[81920];
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    destination.Write(buffer, 0, read);
                    progress?.Report(total == 0 ? 1.0 : (double)source.Position / total);
                }
                destination.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destinationPath, overwrite);
            progress?.Report(1.0);
        }
        catch
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            throw;
        }
    }, ct);
}
