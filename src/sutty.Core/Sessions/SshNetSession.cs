using Renci.SshNet;
using Renci.SshNet.Common;
using sutty.Core.Commands;
using sutty.Core.Models;
using sutty.Core.Security;
using sutty.Core.Sftp;
using sutty.Core.Terminal;
using System.Diagnostics;

namespace sutty.Core.Sessions;

/// <summary>
/// SSH.NET(Renci.SshNet) 기반 실제 SSH 세션.
/// SSH transport를 먼저 연결하고, 선택적 SFTP subsystem은 별도 상태로 연다.
/// </summary>
public sealed class SshNetSession : ISshSession
{
    private SshClient? _ssh;
    private SftpClient? _sftpClient;
    private ShellStream? _terminalStream;
    private readonly SshNetSftpService _sftpService;
    private readonly HostEndpointIdentity _hostEndpoint;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _terminalGate = new(1, 1);
    private readonly SemaphoreSlim _terminalWriteGate = new(1, 1);
    private readonly SemaphoreSlim _terminalResizeGate = new(1, 1);
    private readonly object _terminalReadGate = new();
    private CancellationTokenSource? _terminalLifetimeCts;

    public Guid Id { get; } = Guid.NewGuid();
    public SshConnectionInfo Info { get; }
    public SessionState State { get; private set; } = SessionState.Idle;
    public string? LastError { get; private set; }
    public ISftpService Sftp => _sftpService;
    public SftpConnectionState SftpState { get; private set; } = SftpConnectionState.NotConnected;
    public string? LastSftpError { get; private set; }
    public TerminalState TerminalState { get; private set; } = TerminalState.Closed;
    public string? LastTerminalError { get; private set; }

    // SSH.NET 2025.1 exposes the SSH window-change request through ShellStream.
    public bool SupportsTerminalResize => true;

    public event EventHandler<SessionState>? StateChanged;
    public event EventHandler<SftpConnectionState>? SftpStateChanged;
    public event EventHandler<TerminalState>? TerminalStateChanged;
    public event EventHandler<TerminalDataReceivedEventArgs>? TerminalDataReceived;

    public SshNetSession(SshConnectionInfo info)
    {
        Info = info;
        _sftpService = new SshNetSftpService(() => _sftpClient);
        _hostEndpoint = HostEndpointIdentity.Create(info.Host, info.Port);
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
            // TrustOnce belongs to one logical connection attempt. SSH and its optional
            // SFTP transport share this context, but a failed/disconnected reconnect must
            // start unknown again instead of inheriting an earlier in-memory decision.
            var trustContext = new HostKeyTrustContext();
            SetSftpState(SftpConnectionState.NotConnected);
            SetState(SessionState.Connecting);
            try
            {
                _ssh = await ConnectSshClientAsync(trustContext, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await CleanUpAsync();
                SetSftpState(SftpConnectionState.NotConnected);
                SetState(SessionState.Disconnected);
                throw;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                await CleanUpAsync();
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
                _sftpClient = await ConnectSftpClientAsync(trustContext, ct);
                SetSftpState(SftpConnectionState.Ready);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await CleanUpAsync();
                SetSftpState(SftpConnectionState.NotConnected);
                SetState(SessionState.Disconnected);
                throw;
            }
            catch (Exception ex)
            {
                LastSftpError = ex.Message;
                await CleanUpSftpAsync();
                SetSftpState(SftpConnectionState.Unavailable);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<CommandExecutionResult> ExecuteCommandAsync(
        string command, CancellationToken ct = default)
    {
        if (_ssh is not { IsConnected: true } ssh)
            throw new InvalidOperationException("SSH is not connected.");

        var startedAt = DateTimeOffset.UtcNow;
        var started = Stopwatch.GetTimestamp();
        using var cmd = ssh.CreateCommand(command);
        try
        {
            await cmd.ExecuteAsync(ct);
            return new CommandExecutionResult(
                command,
                cmd.Result ?? "",
                cmd.Error ?? "",
                cmd.ExitStatus,
                cmd.ExitSignal,
                startedAt,
                Stopwatch.GetElapsedTime(started));
        }
        catch (OperationCanceledException)
        {
            // SSH.NET sends a termination signal when supported. Never relabel a cancelled
            // command as a successful result or replay it after reconnect.
            throw;
        }
    }

    public async Task<string> RunCommandAsync(string command, CancellationToken ct = default)
        => (await ExecuteCommandAsync(command, ct)).CombinedOutput;

    public async Task OpenTerminalAsync(TerminalSize size, CancellationToken ct = default)
    {
        await _terminalGate.WaitAsync(ct);
        try
        {
            lock (_terminalReadGate)
            {
                if (_terminalStream is { CanWrite: true } && TerminalState == TerminalState.Open)
                    return;
            }

            // A failed/closed ShellStream may still be present. Retire it before a new
            // channel is created so callbacks and writes can never cross generations.
            CloseTerminalCore();
            if (_ssh is not { IsConnected: true } ssh)
                throw new InvalidOperationException("SSH is not connected.");

            LastTerminalError = null;
            SetTerminalState(TerminalState.Opening);
            var requested = size.Clamp();
            ShellStream? stream = null;

            try
            {
                // CreateShellStream is synchronous in SSH.NET 2025.1. Run the channel
                // allocation off the UI thread; cancellation is checked both before and
                // immediately after the public API returns.
                stream = await Task.Run(
                    () => ssh.CreateShellStream(
                        "xterm-256color",
                        requested.Columns,
                        requested.Rows,
                        requested.PixelWidth,
                        requested.PixelHeight,
                        bufferSize: 64 * 1024),
                    ct);
                ct.ThrowIfCancellationRequested();

                lock (_terminalReadGate)
                {
                    // Subscribe and publish atomically relative to callbacks/close. Any
                    // already-buffered startup output is drained immediately afterwards.
                    stream.DataReceived += TerminalStream_DataReceived;
                    stream.ErrorOccurred += TerminalStream_ErrorOccurred;
                    stream.Closed += TerminalStream_Closed;
                    _terminalLifetimeCts = new CancellationTokenSource();
                    _terminalStream = stream;
                    SetTerminalState(TerminalState.Open);
                }

                // Output can arrive between channel creation and event subscription.
                // Drain the ShellStream buffer once so that startup prompts are not lost.
                DrainTerminalOutput(stream);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                DisposeTerminalCandidate(stream);
                SetTerminalState(TerminalState.Closed);
                throw;
            }
            catch (Exception ex)
            {
                DisposeTerminalCandidate(stream);
                LastTerminalError = ex.Message;
                SetTerminalState(TerminalState.Failed);
            }
        }
        finally
        {
            _terminalGate.Release();
        }
    }

    public async Task SendTerminalInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (data.IsEmpty)
            return;

        ShellStream stream;
        CancellationToken lifetimeToken;
        lock (_terminalReadGate)
        {
            if (_terminalStream is not { CanWrite: true } current ||
                _terminalLifetimeCts is not { IsCancellationRequested: false } lifetime ||
                TerminalState != TerminalState.Open)
                throw new InvalidOperationException("Interactive terminal is not open.");

            stream = current;
            lifetimeToken = lifetime.Token;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetimeToken);
        await _terminalWriteGate.WaitAsync(linkedCts.Token);
        try
        {
            lock (_terminalReadGate)
            {
                if (!ReferenceEquals(stream, _terminalStream) || lifetimeToken.IsCancellationRequested)
                    throw new OperationCanceledException(lifetimeToken);
            }

            try
            {
                await stream.WriteAsync(data, linkedCts.Token);
                await stream.FlushAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Close/replace disposes the stream before waiting for this writer. A
                // stale writer must not turn the next terminal generation into Failed.
                FailTerminalStream(stream, ex);
                throw;
            }
        }
        finally
        {
            _terminalWriteGate.Release();
        }
    }

    public async Task<bool> ResizeTerminalAsync(TerminalSize size, CancellationToken ct = default)
    {
        var requested = size.Clamp();
        ShellStream stream;
        CancellationToken lifetimeToken;
        lock (_terminalReadGate)
        {
            if (_terminalStream is not { CanWrite: true } current ||
                _terminalLifetimeCts is not { IsCancellationRequested: false } lifetime ||
                TerminalState != TerminalState.Open)
                return false;

            stream = current;
            lifetimeToken = lifetime.Token;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetimeToken);
        await _terminalResizeGate.WaitAsync(linkedCts.Token);
        try
        {
            lock (_terminalReadGate)
            {
                if (!ReferenceEquals(stream, _terminalStream) || lifetimeToken.IsCancellationRequested)
                    return false;
            }

            try
            {
                // Keep the synchronous SSH.NET packet send off the UI thread. Terminal
                // close does not wait for this gate; disposing a stale stream aborts the
                // request and is handled below, so disconnect cannot deadlock on resize.
                await Task.Run(
                    () => stream.ChangeWindowSize(
                        requested.Columns,
                        requested.Rows,
                        requested.PixelWidth,
                        requested.PixelHeight),
                    linkedCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (SshException) when (!IsCurrentTerminalStream(stream))
            {
                return false;
            }

            return IsCurrentTerminalStream(stream) && TerminalState == TerminalState.Open;
        }
        finally
        {
            _terminalResizeGate.Release();
        }
    }

    public async Task CloseTerminalAsync()
    {
        await _terminalGate.WaitAsync();
        try
        {
            CloseTerminalCore();
            SetTerminalState(TerminalState.Closed);
        }
        finally
        {
            _terminalGate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (State is SessionState.Idle or SessionState.Disconnected or SessionState.Disconnecting)
                return;

            SetState(SessionState.Disconnecting);
            await CloseTerminalAsync();
            await CleanUpAsync();
            SetSftpState(SftpConnectionState.NotConnected);
            SetState(SessionState.Disconnected);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task CleanUpAsync()
    {
        CloseTerminalCore();
        await CleanUpSftpAsync();
        await Task.Run(() =>
        {
            var ssh = _ssh;
            _ssh = null;
            try { ssh?.Disconnect(); } catch { /* 이미 끊겼으면 무시 */ }
            ssh?.Dispose();
        });
    }

    private Task CleanUpSftpAsync() => _sftpService.ShutdownAsync(() =>
    {
        // Clear the provider while holding the service's operation gate. No queued
        // operation can acquire a client that teardown is about to dispose.
        var sftp = _sftpClient;
        _sftpClient = null;
        try { sftp?.Disconnect(); } catch { /* already disconnected */ }
        sftp?.Dispose();
    });

    private void TerminalStream_DataReceived(object? sender, ShellDataEventArgs e)
    {
        if (sender is ShellStream stream && IsCurrentTerminalStream(stream))
            DrainTerminalOutput(stream);
    }

    private void DrainTerminalOutput(ShellStream stream)
    {
        lock (_terminalReadGate)
        {
            if (!ReferenceEquals(stream, _terminalStream))
                return;

            List<byte[]> chunks = [];
            try
            {
                while (ReferenceEquals(stream, _terminalStream) && stream.DataAvailable)
                {
                    var available = Math.Clamp(stream.Length, 1L, 16 * 1024L);
                    var buffer = new byte[(int)available];
                    var read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                        break;
                    if (read != buffer.Length)
                        Array.Resize(ref buffer, read);
                    chunks.Add(buffer);
                }
            }
            catch (ObjectDisposedException)
            {
                // Disconnect raced the final data callback; the channel is already closed.
            }

            // Stream replacement takes the same lock, so no old output can be published
            // after a new channel has become current.
            foreach (var chunk in chunks)
                TerminalDataReceived?.Invoke(this, new TerminalDataReceivedEventArgs(chunk));
        }
    }

    private void TerminalStream_ErrorOccurred(object? sender, ExceptionEventArgs e)
    {
        if (sender is ShellStream stream)
            RetireTerminalStream(stream, TerminalState.Failed, e.Exception, closeStream: true);
    }

    private void TerminalStream_Closed(object? sender, EventArgs e)
    {
        if (sender is ShellStream stream)
            RetireTerminalStream(stream, TerminalState.Closed, error: null, closeStream: false);
    }

    private void CloseTerminalCore()
    {
        ShellStream? stream;
        CancellationTokenSource? lifetime;
        lock (_terminalReadGate)
        {
            stream = _terminalStream;
            lifetime = _terminalLifetimeCts;
            _terminalStream = null;
            _terminalLifetimeCts = null;
            if (stream is not null)
            {
                stream.DataReceived -= TerminalStream_DataReceived;
                stream.ErrorOccurred -= TerminalStream_ErrorOccurred;
                stream.Closed -= TerminalStream_Closed;
            }
        }

        // Cancel first and dispose the stream without waiting for _terminalWriteGate.
        // This aborts a blocked WriteAsync/FlushAsync so disconnect cannot deadlock.
        try { lifetime?.Cancel(); } catch (ObjectDisposedException) { }
        if (stream is not null)
        {
            try { stream.Close(); } catch { /* transport may already be closed */ }
            stream.Dispose();
        }
        lifetime?.Dispose();
    }

    private bool IsCurrentTerminalStream(ShellStream stream)
    {
        lock (_terminalReadGate)
            return ReferenceEquals(stream, _terminalStream);
    }

    private void DisposeTerminalCandidate(ShellStream? stream)
    {
        if (stream is null)
            return;

        if (IsCurrentTerminalStream(stream))
        {
            CloseTerminalCore();
            return;
        }

        try { stream.Close(); } catch { /* channel never fully opened or was cancelled */ }
        stream.Dispose();
    }

    private void FailTerminalStream(ShellStream stream, Exception error)
        => RetireTerminalStream(stream, TerminalState.Failed, error, closeStream: true);

    private void RetireTerminalStream(
        ShellStream stream,
        TerminalState finalState,
        Exception? error,
        bool closeStream)
    {
        CancellationTokenSource? lifetime;
        lock (_terminalReadGate)
        {
            if (!ReferenceEquals(stream, _terminalStream))
                return;

            lifetime = _terminalLifetimeCts;
            _terminalStream = null;
            _terminalLifetimeCts = null;
            stream.DataReceived -= TerminalStream_DataReceived;
            stream.ErrorOccurred -= TerminalStream_ErrorOccurred;
            stream.Closed -= TerminalStream_Closed;
            if (error is not null)
                LastTerminalError = error.Message;
            SetTerminalState(finalState);
        }

        try { lifetime?.Cancel(); } catch (ObjectDisposedException) { }
        if (closeStream)
        {
            try { stream.Close(); } catch { /* failed transport is already closing */ }
        }
        stream.Dispose();
        lifetime?.Dispose();
    }

    private async Task<SshClient> ConnectSshClientAsync(
        HostKeyTrustContext trustContext,
        CancellationToken ct)
    {
        var mayPrompt = true;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var client = new SshClient(BuildConnectionInfo());
            if (Info.KeepAliveSeconds > 0)
                client.KeepAliveInterval = TimeSpan.FromSeconds(Info.KeepAliveSeconds);
            var verifier = new SshNetHostKeyVerifier(_hostEndpoint, trustContext);
            client.HostKeyReceived += verifier.HandleHostKeyReceived;

            try
            {
                await client.ConnectAsync(ct);
                return client;
            }
            catch (Exception ex)
            {
                client.Dispose();
                if (await HandleHostKeyConnectFailureAsync(verifier, ex, mayPrompt, ct))
                {
                    mayPrompt = false;
                    continue;
                }
                throw;
            }
        }
    }

    private async Task<SftpClient> ConnectSftpClientAsync(
        HostKeyTrustContext trustContext,
        CancellationToken ct)
    {
        var mayPrompt = true;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var client = new SftpClient(BuildConnectionInfo());
            var verifier = new SshNetHostKeyVerifier(_hostEndpoint, trustContext);
            client.HostKeyReceived += verifier.HandleHostKeyReceived;

            try
            {
                await client.ConnectAsync(ct);
                return client;
            }
            catch (Exception ex)
            {
                client.Dispose();
                if (await HandleHostKeyConnectFailureAsync(verifier, ex, mayPrompt, ct))
                {
                    mayPrompt = false;
                    continue;
                }
                throw;
            }
        }
    }

    /// <summary>
    /// SSH.NET host-key callbacks are synchronous, so unknown keys fail closed first.
    /// This method runs after that handshake unwinds, awaits UI asynchronously, applies
    /// the decision to the shared connection context, then allows one fresh-client retry.
    /// Changed keys and verifier/storage errors never prompt.
    /// </summary>
    private async Task<bool> HandleHostKeyConnectFailureAsync(
        SshNetHostKeyVerifier verifier,
        Exception connectError,
        bool mayPrompt,
        CancellationToken ct)
    {
        if (verifier.LastError is { } verificationError)
        {
            throw new System.Security.SecurityException(
                $"Host-key verification failed for {_hostEndpoint.Value}: {verificationError.Message}",
                verificationError);
        }

        var verification = verifier.LastVerification;
        if (verification?.State == HostKeyTrustState.Changed)
        {
            throw new HostKeyChangedException(
                verification.Endpoint,
                verification.TrustedKey!,
                verification.PresentedKey);
        }

        if (verification?.State != HostKeyTrustState.Unknown)
            return false;

        if (!mayPrompt)
        {
            throw new System.Security.SecurityException(
                $"Host key for {_hostEndpoint.Value} remained untrusted after approval.",
                connectError);
        }

        if (Info.HostKeyPromptAsync is null)
        {
            throw new System.Security.SecurityException(
                $"Unknown host key for {_hostEndpoint.Value}: " +
                $"{verification.PresentedKey.Algorithm} {verification.PresentedKey.Sha256Fingerprint}. " +
                "Connection cancelled because no trust prompt is available.",
                connectError);
        }

        var decision = await Info.HostKeyPromptAsync(verification, ct);
        ct.ThrowIfCancellationRequested();
        if (decision == HostKeyDecision.Cancel || !verifier.ApplyLastDecision(decision))
        {
            throw new System.Security.SecurityException(
                $"Host key for {_hostEndpoint.Value} was not trusted. Connection cancelled.",
                connectError);
        }

        return true;
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
    /// 레거시 .ppk 컨테이너는 지원하지 않으므로 미리 안내한다.
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
                "레거시 .ppk 형식은 지원되지 않습니다. 키를 OpenSSH 형식으로 내보낸 뒤 사용하세요.");

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

    private void SetTerminalState(TerminalState state)
    {
        if (TerminalState == state)
            return;

        TerminalState = state;
        TerminalStateChanged?.Invoke(this, state);
    }
}
