using Renci.SshNet;
using sutty.Core.Models;
using sutty.Core.Sftp;

namespace sutty.Core.Sessions;

/// <summary>
/// SSH.NET(Renci.SshNet) 기반 실제 SSH 세션.
/// 연결 시 SSH 채널과 SFTP 채널을 함께 연다.
/// </summary>
public sealed class SshNetSession : ISshSession
{
    private SshClient? _ssh;
    private SftpClient? _sftpClient;
    private readonly SshNetSftpService _sftpService;

    public Guid Id { get; } = Guid.NewGuid();
    public SshConnectionInfo Info { get; }
    public SessionState State { get; private set; } = SessionState.Idle;
    public string? LastError { get; private set; }
    public ISftpService Sftp => _sftpService;

    public event EventHandler<SessionState>? StateChanged;

    public SshNetSession(SshConnectionInfo info)
    {
        Info = info;
        _sftpService = new SshNetSftpService(() => _sftpClient);
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (State is SessionState.Connecting or SessionState.Connected)
            return;

        SetState(SessionState.Connecting);
        try
        {
            await Task.Run(() =>
            {
                _ssh = new SshClient(BuildConnectionInfo());
                if (Info.KeepAliveSeconds > 0)
                    _ssh.KeepAliveInterval = TimeSpan.FromSeconds(Info.KeepAliveSeconds);
                _ssh.Connect();

                // SFTP는 별도 채널로 연다 (같은 인증 정보 재사용)
                _sftpClient = new SftpClient(BuildConnectionInfo());
                _sftpClient.Connect();
            }, ct);

            SetState(SessionState.Connected);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            CleanUp();
            SetState(SessionState.Failed);
        }
    }

    public async Task<string> RunCommandAsync(string command, CancellationToken ct = default)
    {
        if (_ssh is not { IsConnected: true })
            return "";

        return await Task.Run(() =>
        {
            using var cmd = _ssh.CreateCommand(command);
            var output = cmd.Execute();
            return string.IsNullOrWhiteSpace(output) ? cmd.Error : output;
        }, ct);
    }

    public async Task DisconnectAsync()
    {
        if (State is SessionState.Idle or SessionState.Disconnected or SessionState.Disconnecting)
            return;

        SetState(SessionState.Disconnecting);
        await Task.Run(CleanUp);
        SetState(SessionState.Disconnected);
    }

    private void CleanUp()
    {
        try { _sftpClient?.Disconnect(); } catch { /* 이미 끊겼으면 무시 */ }
        try { _ssh?.Disconnect(); } catch { /* 이미 끊겼으면 무시 */ }
        _sftpClient?.Dispose();
        _ssh?.Dispose();
        _sftpClient = null;
        _ssh = null;
    }

    private Renci.SshNet.ConnectionInfo BuildConnectionInfo()
    {
        var user = Info.Username;
        AuthenticationMethod auth = Info.AuthMethod switch
        {
            SshAuthMethod.Password => new PasswordAuthenticationMethod(user, Info.Password),
            SshAuthMethod.PublicKey => new PrivateKeyAuthenticationMethod(user, LoadPrivateKey()),
            SshAuthMethod.KeyboardInteractive => BuildKeyboardInteractive(user),
            _ => throw new NotSupportedException("SSH agent 인증은 아직 지원하지 않습니다."),
        };

        return new Renci.SshNet.ConnectionInfo(Info.Host, Info.Port, user, auth)
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    private PrivateKeyFile LoadPrivateKey() =>
        string.IsNullOrEmpty(Info.Passphrase)
            ? new PrivateKeyFile(Info.PrivateKeyPath)
            : new PrivateKeyFile(Info.PrivateKeyPath, Info.Passphrase);

    // 2FA/OTP 프롬프트 중 password 요청에는 입력된 비밀번호로 응답
    private KeyboardInteractiveAuthenticationMethod BuildKeyboardInteractive(string user)
    {
        var kbi = new KeyboardInteractiveAuthenticationMethod(user);
        kbi.AuthenticationPrompt += (_, e) =>
        {
            foreach (var prompt in e.Prompts)
            {
                if (prompt.Request.Contains("password", StringComparison.OrdinalIgnoreCase))
                    prompt.Response = Info.Password;
            }
        };
        return kbi;
    }

    private void SetState(SessionState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }
}
