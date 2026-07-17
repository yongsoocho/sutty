using Renci.SshNet;
using sutty.Core.Models;

namespace sutty.Core.Sftp;

/// <summary>SSH.NET SftpClient를 감싸는 실제 SFTP 서비스. 클라이언트는 세션이 소유한다.</summary>
public sealed class SshNetSftpService : ISftpService
{
    private readonly Func<SftpClient?> _clientProvider;

    public SshNetSftpService(Func<SftpClient?> clientProvider)
        => _clientProvider = clientProvider;

    private SftpClient Client =>
        _clientProvider() is { IsConnected: true } client
            ? client
            : throw new InvalidOperationException("SFTP 채널이 연결되어 있지 않습니다.");

    public Task<IReadOnlyList<RemoteFileEntry>> ListDirectoryAsync(string path, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<RemoteFileEntry>>(() =>
            Client.ListDirectory(path)
                .Where(f => f.Name is not "." and not "..")
                .Select(f => new RemoteFileEntry
                {
                    Name = f.Name,
                    FullPath = f.FullName,
                    IsDirectory = f.IsDirectory,
                    Size = f.Length,
                    Modified = f.LastWriteTime,
                })
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(), ct);

    public Task UploadFileAsync(string localPath, string remoteDirectory,
        IProgress<double>? progress = null, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var client = Client;
            var remotePath = RemotePath.Combine(remoteDirectory, Path.GetFileName(localPath));

            using var local = File.OpenRead(localPath);
            long total = local.Length;

            try
            {
                // 직접 청크 단위로 써야 진행도 보고와 취소가 모두 가능하다
                using var remote = client.Open(remotePath, FileMode.Create, FileAccess.Write);
                var buffer = new byte[81920];
                int read;
                while ((read = local.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    remote.Write(buffer, 0, read);
                    progress?.Report(total == 0 ? 1.0 : (double)local.Position / total);
                }
            }
            catch (OperationCanceledException)
            {
                // 취소되면 부분 업로드된 파일을 정리
                try { client.DeleteFile(remotePath); } catch { /* 없으면 무시 */ }
                throw;
            }
        }, ct);
}
