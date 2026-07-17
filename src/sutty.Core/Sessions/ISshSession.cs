using sutty.Core.Models;
using sutty.Core.Sftp;

namespace sutty.Core.Sessions;

/// <summary>
/// SSH 세션 하나(= 탭 하나)의 계약.
/// 지금은 MockSshSession이 구현하고, 실제 SSH 클라이언트로 교체 예정.
/// StateChanged는 백그라운드 스레드에서 발생할 수 있으므로
/// UI에서는 DispatcherQueue로 마샬링해야 한다.
/// </summary>
public interface ISshSession
{
    Guid Id { get; }
    SshConnectionInfo Info { get; }
    SessionState State { get; }

    /// <summary>이 세션과 같은 연결을 공유하는 SFTP 채널.</summary>
    ISftpService Sftp { get; }

    event EventHandler<SessionState>? StateChanged;

    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync();
}
