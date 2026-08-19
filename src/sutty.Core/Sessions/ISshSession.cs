using sutty.Core.Models;
using sutty.Core.Commands;
using sutty.Core.Sftp;
using sutty.Core.Terminal;
using sutty.Core.Routing;

namespace sutty.Core.Sessions;

/// <summary>
/// SSH 세션 하나(= 탭 하나)의 계약.
/// Production SSH transport, command, terminal, and SFTP state contract.
/// StateChanged는 백그라운드 스레드에서 발생할 수 있으므로
/// UI에서는 DispatcherQueue로 마샬링해야 한다.
/// </summary>
public interface ISshSession : IInteractiveTerminal
{
    Guid Id { get; }
    SshConnectionInfo Info { get; }
    SessionState State { get; }
    ConnectionCorrelationContext CorrelationContext { get; }

    /// <summary>
    /// Credential-free snapshot of the primary SSH transport's initial successful
    /// handshake for the current connection. Null before connection, after failure,
    /// and after disconnect.
    /// </summary>
    SshNegotiatedConnectionInfo? NegotiatedInfo { get; }

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

    /// <summary>Run one non-interactive command and preserve stdout, stderr and exit status.</summary>
    Task<CommandExecutionResult> ExecuteCommandAsync(
        string command, CancellationToken ct = default);

    /// <summary>Compatibility helper that combines stdout and stderr for simple callers.</summary>
    Task<string> RunCommandAsync(string command, CancellationToken ct = default);

    Task DisconnectAsync();
}
