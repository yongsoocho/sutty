using sutty.Core.Models;
using sutty.Core.Commands;
using sutty.Core.Sftp;
using sutty.Core.Terminal;

namespace sutty.Core.Sessions;

/// <summary>
/// SSH 세션 하나(= 탭 하나)의 계약.
/// Production SSH transport, command, terminal, and SFTP state contract.
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

    /// <summary>State of the independent interactive PTY channel.</summary>
    TerminalState TerminalState { get; }

    /// <summary>The latest PTY error. A terminal failure does not close REPL or SFTP.</summary>
    string? LastTerminalError { get; }

    /// <summary>
    /// Whether this SSH engine can notify the server when the terminal viewport changes.
    /// Initial PTY dimensions are always applied when the channel opens.
    /// </summary>
    bool SupportsTerminalResize { get; }

    event EventHandler<SessionState>? StateChanged;
    event EventHandler<SftpConnectionState>? SftpStateChanged;
    event EventHandler<TerminalState>? TerminalStateChanged;
    event EventHandler<TerminalDataReceivedEventArgs>? TerminalDataReceived;

    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>Run one non-interactive command and preserve stdout, stderr and exit status.</summary>
    Task<CommandExecutionResult> ExecuteCommandAsync(
        string command, CancellationToken ct = default);

    /// <summary>Compatibility helper that combines stdout and stderr for simple callers.</summary>
    Task<string> RunCommandAsync(string command, CancellationToken ct = default);

    /// <summary>Allocate a persistent xterm-compatible PTY while preserving REPL exec channels.</summary>
    Task OpenTerminalAsync(TerminalSize size, CancellationToken ct = default);

    /// <summary>Write raw UTF-8/control bytes to the persistent PTY.</summary>
    Task SendTerminalInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>
    /// Ask the remote PTY to resize. Returns false when the terminal is closed or its
    /// channel is replaced while the window-change request is being sent.
    /// </summary>
    Task<bool> ResizeTerminalAsync(TerminalSize size, CancellationToken ct = default);

    /// <summary>Close only the interactive PTY channel.</summary>
    Task CloseTerminalAsync();

    Task DisconnectAsync();
}
