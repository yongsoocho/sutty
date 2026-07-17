using sutty.Core.Models;

namespace sutty.Core.Sftp;

/// <summary>
/// SFTP 파일 시스템 접근 계약.
/// 지금은 MockSftpService가 구현하고, 실제 SFTP 클라이언트(SSH.NET 등)로 교체 예정.
/// </summary>
public interface ISftpService
{
    Task<IReadOnlyList<RemoteFileEntry>> ListDirectoryAsync(string path, CancellationToken ct = default);
}
