using Renci.SshNet;
using sutty.Core.Models;
using sutty.Core.Sftp;

namespace sutty.Core.Sessions;

/// <summary>
/// SSH.NET(Renci.SshNet) 기반 실제 SSH 세션.
/// SSH transport를 먼저 연결하고, 선택적 SFTP subsystem은 별도 상태로 연다.
/// </summary>
public sealed class SshNetSession : ISshSession
{
    private SshClient? _ssh;
    private SftpClient? _sftpClient;
    private readonly SshNetSftpService _sftpService;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    public Guid Id { get; } = Guid.NewGuid();
    public SshConnectionInfo Info { get; }
    public SessionState State { get; private set; } = SessionState.Idle;
    public string? LastError { get; private set; }
    public ISftpService Sftp => _sftpService;
    public SftpConnectionState SftpState { get; private set; } = SftpConnectionState.NotConnected;
    public string? LastSftpError { get; private set; }

    public event EventHandler<SessionState>? StateChanged;
    public event EventHandler<SftpConnectionState>? SftpStateChanged;

    public SshNetSession(SshConnectionInfo info)
    {
        Info = info;
        _sftpService = new SshNetSftpService(() => _sftpClient);
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _lifecycleGate.WaitAsync(ct);
        try
        {
            if (State is SessionState.Connecting or SessionState.Connected)
                return;

            LastError = null;
            LastSftpError = null;
            SetSftpState(SftpConnectionState.NotConnected);
            SetState(SessionState.Connecting);
            try
            {
                _ssh = new SshClient(BuildConnectionInfo());
                if (Info.KeepAliveSeconds > 0)
                    _ssh.KeepAliveInterval = TimeSpan.FromSeconds(Info.KeepAliveSeconds);
                await _ssh.ConnectAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                CleanUp();
                SetSftpState(SftpConnectionState.NotConnected);
                SetState(SessionState.Disconnected);
                throw;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                CleanUp();
                SetSftpState(SftpConnectionState.NotConnected);
                SetState(SessionState.Failed);
                return;
            }

            // SSH is usable as soon as its transport is connected. SFTP is an optional
            // subsystem and must not turn a working terminal into a failed session.
            SetSftpState(SftpConnectionState.Connecting);
            SetState(SessionState.Connected);

            try
            {
                _sftpClient = new SftpClient(BuildConnectionInfo());
                await _sftpClient.ConnectAsync(ct);
                SetSftpState(SftpConnectionState.Ready);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                CleanUp();
                SetSftpState(SftpConnectionState.NotConnected);
                SetState(SessionState.Disconnected);
                throw;
            }
            catch (Exception ex)
            {
                LastSftpError = ex.Message;
                CleanUpSftp();
                SetSftpState(SftpConnectionState.Unavailable);
            }
        }
        finally
        {
            _lifecycleGate.Release();
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
        await _lifecycleGate.WaitAsync();
        try
        {
            if (State is SessionState.Idle or SessionState.Disconnected or SessionState.Disconnecting)
                return;

            SetState(SessionState.Disconnecting);
            await Task.Run(CleanUp);
            SetSftpState(SftpConnectionState.NotConnected);
            SetState(SessionState.Disconnected);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void CleanUp()
    {
        CleanUpSftp();
        try { _ssh?.Disconnect(); } catch { /* 이미 끊겼으면 무시 */ }
        _ssh?.Dispose();
        _ssh = null;
    }

    private void CleanUpSftp()
    {
        try { _sftpClient?.Disconnect(); } catch { /* already disconnected */ }
        _sftpClient?.Dispose();
        _sftpClient = null;
    }

    private Renci.SshNet.ConnectionInfo BuildConnectionInfo()
    {
        if (string.IsNullOrWhiteSpace(Info.Username))
            throw new InvalidOperationException("Username을 입력하세요.");

        var user = Info.Username;
        var methods = new List<AuthenticationMethod>();

        switch (Info.AuthMethod)
        {
            case SshAuthMethod.Password:
                methods.Add(new PasswordAuthenticationMethod(user, Info.Password));
                // 서버가 password 대신 keyboard-interactive만 받는 경우가 흔해서 폴백 추가
                methods.Add(BuildKeyboardInteractive(user));
                break;

            case SshAuthMethod.PublicKey:
                methods.Add(new PrivateKeyAuthenticationMethod(user, LoadPrivateKey()));
                // 비밀번호도 입력돼 있으면 키 실패 시 폴백으로 시도
                if (!string.IsNullOrEmpty(Info.Password))
                    methods.Add(new PasswordAuthenticationMethod(user, Info.Password));
                break;

            case SshAuthMethod.KeyboardInteractive:
                methods.Add(BuildKeyboardInteractive(user));
                break;

            default:
                throw new NotSupportedException("SSH agent 인증은 아직 지원하지 않습니다.");
        }

        return new Renci.SshNet.ConnectionInfo(Info.Host, Info.Port, user, methods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    /// <summary>
    /// PEM / OpenSSH 형식의 RSA·ECDSA·Ed25519 키를 로드한다 (SSH.NET 지원 범위).
    /// PuTTY .ppk는 지원하지 않으므로 미리 안내한다.
    /// </summary>
    private PrivateKeyFile LoadPrivateKey()
    {
        var path = Info.PrivateKeyPath;

        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Private key 파일을 선택하세요.");
        if (!File.Exists(path))
            throw new FileNotFoundException($"키 파일을 찾을 수 없습니다: {path}");
        if (path.EndsWith(".ppk", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                "PuTTY .ppk 형식은 지원되지 않습니다. puttygen에서 'Export OpenSSH key'로 변환한 뒤 사용하세요.");

        try
        {
            return string.IsNullOrEmpty(Info.Passphrase)
                ? new PrivateKeyFile(path)
                : new PrivateKeyFile(path, Info.Passphrase);
        }
        catch (Renci.SshNet.Common.SshPassPhraseNullOrEmptyException)
        {
            throw new InvalidOperationException("이 키는 passphrase가 필요합니다. Key passphrase를 입력하세요.");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"키 파일을 읽을 수 없습니다 ({Path.GetFileName(path)}): {ex.Message}");
        }
    }

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

    private void SetSftpState(SftpConnectionState state)
    {
        if (SftpState == state)
            return;

        SftpState = state;
        SftpStateChanged?.Invoke(this, state);
    }
}
