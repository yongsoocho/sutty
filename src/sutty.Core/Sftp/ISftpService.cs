using sutty.Core.Models;

namespace sutty.Core.Sftp;

/// <summary>
/// SFTP 파일 시스템 접근 계약.
/// 지금은 MockSftpService가 구현하고, 실제 SFTP 클라이언트(SSH.NET 등)로 교체 예정.
/// </summary>
public interface ISftpService
{
    Task<IReadOnlyList<RemoteFileEntry>> ListDirectoryAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// 로컬 파일을 원격 디렉터리에 업로드한다.
    /// progress는 0.0~1.0, ct 취소 시 부분 업로드는 정리하고 OperationCanceledException을 던진다.
    /// </summary>
    Task UploadFileAsync(string localPath, string remoteDirectory,
        IProgress<double>? progress = null, CancellationToken ct = default);
}
