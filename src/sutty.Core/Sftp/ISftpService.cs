using sutty.Core.Models;

namespace sutty.Core.Sftp;

/// <summary>
/// SFTP 파일 시스템 접근 계약.
/// Production remote file operation contract implemented by the SSH.NET adapter.
/// </summary>
public interface ISftpService
{
    Task<IReadOnlyList<RemoteFileEntry>> ListDirectoryAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// 로컬 파일을 원격 디렉터리에 업로드한다.
    /// progress는 0.0~1.0, ct 취소 시 부분 업로드는 정리하고 OperationCanceledException을 던진다.
    /// </summary>
    Task UploadFileAsync(string localPath, string remoteDirectory, bool overwrite = false,
        IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// 원격 파일을 로컬 경로로 다운로드한다. 부분 다운로드는 임시 파일에 쓰며,
    /// 취소/실패 시 정리한다. overwrite=false이면 기존 로컬 파일을 절대 덮어쓰지 않는다.
    /// </summary>
    Task DownloadFileAsync(string remotePath, string localPath, bool overwrite = false,
        IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>파일 또는 디렉터리를 새 원격 경로로 이동/이름 변경한다. 기존 대상은 덮어쓰지 않는다.</summary>
    Task MoveAsync(string sourcePath, string destinationPath, CancellationToken ct = default);

    Task DeleteFileAsync(string path, CancellationToken ct = default);

    /// <summary>비어 있는 원격 디렉터리만 삭제한다.</summary>
    Task DeleteDirectoryAsync(string path, CancellationToken ct = default);

    Task CreateDirectoryAsync(string path, CancellationToken ct = default);
}
