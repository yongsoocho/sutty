using sutty.Core.Models;
using sutty.Core.Sftp;

namespace sutty.Core.Sessions;

/// <summary>
/// SSH 세션 하나(= 탭 하나)의 계약.
/// MockSshSession과 SSH.NET 기반 SshNetSession이 같은 계약을 구현한다.
/// StateChanged는 백그라운드 스레드에서 발생할 수 있으므로
/// UI에서는 DispatcherQueue로 마샬링해야 한다.
/// </summary>
public interface ISshSession
{
    Guid Id { get; }
    SshConnectionInfo Info { get; }
    SessionState State { get; }

    /// <summary>State가 Failed일 때 마지막 오류 메시지.</summary>
    string? LastError { get; }

    /// <summary>이 세션의 인증 정보를 사용하는 선택적 SFTP subsystem.</summary>
    ISftpService Sftp { get; }

    /// <summary>
    /// Optional SFTP subsystem state. This is independent from the SSH session state:
    /// a connected SSH session may have SFTP marked as unavailable.
    /// </summary>
    SftpConnectionState SftpState { get; }

    /// <summary>The most recent SFTP connection error, when SFTP is unavailable.</summary>
    string? LastSftpError { get; }

    event EventHandler<SessionState>? StateChanged;
    event EventHandler<SftpConnectionState>? SftpStateChanged;

    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>원격에서 명령 하나를 실행하고 출력을 돌려준다.</summary>
    Task<string> RunCommandAsync(string command, CancellationToken ct = default);

    Task DisconnectAsync();
}
