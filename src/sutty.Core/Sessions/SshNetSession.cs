using Renci.SshNet;
using Renci.SshNet.Common;
using SshNet.Agent;
using sutty.Core.Commands;
using sutty.Core.Diagnostics;
using sutty.Core.Models;
using sutty.Core.Routing;
using sutty.Core.Security;
using sutty.Core.Sftp;
using sutty.Core.Terminal;
using System.Diagnostics;

namespace sutty.Core.Sessions;

/// <summary>
/// SSH.NET(Renci.SshNet) 기반 실제 SSH 세션.
/// SSH transport를 먼저 연결하고, 선택적 SFTP subsystem은 별도 상태로 연다.
/// </summary>
public sealed partial class SshNetSession : ISshSession
{
    private SshClient? _ssh;
    private SftpClient? _sftpClient;
    private ShellStream? _terminalStream;
    private readonly SshNetSftpService _sftpService;
    private readonly HostEndpointIdentity _hostEndpoint;
    private readonly ResolvedConnectionRoute _route;
    private readonly SshNetDiagnosticLoggerFactory _diagnosticLoggerFactory;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _terminalGate = new(1, 1);
    private readonly SemaphoreSlim _terminalWriteGate = new(1, 1);
    private readonly SemaphoreSlim _terminalResizeGate = new(1, 1);
    private readonly object _terminalReadGate = new();
    private CancellationTokenSource? _terminalLifetimeCts;
    private HostKeyTrustContext? _activeTrustContext;
    private string _password;
    private string _passphrase;
    private string _routePassword;
    private string _routePassphrase;

    public Guid Id { get; } = Guid.NewGuid();
    public SshConnectionInfo Info { get; }
    public SessionState State { get; private set; } = SessionState.Idle;
    public ConnectionCorrelationContext CorrelationContext { get; }
    public string? LastError { get; private set; }
    public ISftpService Sftp => _sftpService;
    public SftpConnectionState SftpState { get; private set; } = SftpConnectionState.NotConnected;
    public string? LastSftpError { get; private set; }
    public TerminalState TerminalState { get; private set; } = TerminalState.Closed;
    public string? LastTerminalError { get; private set; }

    // SSH.NET exposes the SSH window-change request through ShellStream.
    public bool SupportsTerminalResize => true;

    public event EventHandler<SessionState>? StateChanged;
    public event EventHandler<SftpConnectionState>? SftpStateChanged;
    public event EventHandler<TerminalState>? TerminalStateChanged;
    public event EventHandler<TerminalDataReceivedEventArgs>? TerminalDataReceived;

    public SshNetSession(SshConnectionInfo info)
    {
        Info = info;
        _password = info.Password;
        _passphrase = info.Passphrase;
        _routePassword = info.Route?.Password ?? "";
        _routePassphrase = info.Route?.Passphrase ?? "";
        _sftpService = new SshNetSftpService(
            () => _sftpClient,
            checkpointScope: $"{info.Username}@{info.Host}:{info.Port}",
            reconnectAsync: ReconnectSftpForTransferAsync);
        _hostEndpoint = HostEndpointIdentity.Create(info.Host, info.Port);
        _route = RouteResolver.Resolve(info.Route, info.RoutePolicy);
        CorrelationContext = ConnectionCorrelationContext.Create(info, _route);
        _diagnosticLoggerFactory = new SshNetDiagnosticLoggerFactory(
            Id,
            info.Title,
            _hostEndpoint.Value);
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
            var connectionStarted = Stopwatch.GetTimestamp();
            Log(
                ConnectionLogSeverity.Information,
                "SSH",
                "SSH 연결 시도를 시작했습니다.",
                "Starting SSH connection attempt.");
            Log(
                ConnectionLogSeverity.Verbose,
                "Configuration",
                $"대상={_hostEndpoint.Value}; 사용자={Info.Username}; 인증={Info.AuthMethod}; 경로={DescribeRoute()}; 제한시간=15초; keepalive={Info.KeepAliveSeconds}초",
                $"target={_hostEndpoint.Value}; user={Info.Username}; auth={Info.AuthMethod}; route={DescribeRoute()}; timeout=15s; keepalive={Info.KeepAliveSeconds}s");
            // TrustOnce belongs to one logical connection attempt. SSH and its optional
            // SFTP transport share this context, but a failed/disconnected reconnect must
            // start unknown again instead of inheriting an earlier in-memory decision.
            var trustContext = new HostKeyTrustContext();
            _activeTrustContext = trustContext;
            SetSftpState(SftpConnectionState.NotConnected);
            SetState(SessionState.Connecting);
            try
            {
                await PrepareConnectionRouteAsync(trustContext, ct);
                _ssh = await ConnectSshClientAsync(trustContext, ct);
                StartConfiguredForwardings(_ssh);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Log(
                    ConnectionLogSeverity.Warning,
                    "SSH",
                    "SSH 연결 시도가 취소되었습니다.",
                    "The SSH connection attempt was cancelled.");
                await CleanUpAsync();
                SetSftpState(SftpConnectionState.NotConnected);
                SetState(SessionState.Disconnected);
                throw;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                LogFailure("SSH", ex, ConnectionLogSeverity.Error);
                await CleanUpAsync();
                SetSftpState(SftpConnectionState.NotConnected);
                SetState(SessionState.Failed);
                return;
            }

            // SSH is usable as soon as its transport is connected. SFTP is an optional
            // subsystem and must not turn a working terminal into a failed session.
            SetSftpState(SftpConnectionState.Connecting);
            SetState(SessionState.Connected);
            Log(
                ConnectionLogSeverity.Information,
                "SSH",
                $"SSH 연결이 완료되었습니다 ({Stopwatch.GetElapsedTime(connectionStarted).TotalMilliseconds:N0} ms).",
                $"SSH connection established ({Stopwatch.GetElapsedTime(connectionStarted).TotalMilliseconds:N0} ms).");

            try
            {
                Log(
                    ConnectionLogSeverity.Verbose,
                    "SFTP",
                    "SFTP subsystem 연결을 시작합니다.",
                    "Starting SFTP subsystem connection.");
                _sftpClient = await ConnectSftpClientAsync(trustContext, ct);
                SetSftpState(SftpConnectionState.Ready);
                Log(
                    ConnectionLogSeverity.Information,
                    "SFTP",
                    "SFTP subsystem을 사용할 수 있습니다.",
                    "SFTP subsystem is ready.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Log(
                    ConnectionLogSeverity.Warning,
                    "SFTP",
                    "SFTP 연결 중 전체 세션 연결이 취소되었습니다.",
                    "The connection was cancelled while opening SFTP.");
                await CleanUpAsync();
                SetSftpState(SftpConnectionState.NotConnected);
                SetState(SessionState.Disconnected);
                throw;
            }
            catch (Exception ex)
            {
                LastSftpError = ex.Message;
                LogFailure("SFTP", ex, ConnectionLogSeverity.Warning);
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
                // CreateShellStream is synchronous in SSH.NET. Run the channel
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
            Log(
                ConnectionLogSeverity.Information,
                "SSH",
                "SSH 및 SFTP 연결을 종료했습니다.",
                "SSH and SFTP connections were closed.");
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
        StopConfiguredForwardings(_ssh);
        await Task.Run(() =>
        {
            var ssh = _ssh;
            _ssh = null;
            try { ssh?.Disconnect(); } catch { /* 이미 끊겼으면 무시 */ }
            ssh?.Dispose();
        });
        await CleanUpRouteAsync();
        _activeTrustContext = null;
        _password = "";
        _passphrase = "";
        _routePassword = "";
        _routePassphrase = "";
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
            var client = new SshClient(BuildConnectionInfo(ct));
            if (Info.KeepAliveSeconds > 0)
                client.KeepAliveInterval = TimeSpan.FromSeconds(Info.KeepAliveSeconds);
            var verifier = new SshNetHostKeyVerifier(_hostEndpoint, trustContext);
            client.HostKeyReceived += verifier.HandleHostKeyReceived;

            try
            {
                using var diagnosticCapture = _diagnosticLoggerFactory.BeginCapture();
                await Task.Run(() => client.ConnectAsync(ct), ct).ConfigureAwait(false);
                return client;
            }
            catch (Exception ex)
            {
                client.Dispose();
                if (await HandleHostKeyConnectFailureAsync(
                    verifier, _hostEndpoint, ex, mayPrompt, ct))
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
            var client = new SftpClient(BuildConnectionInfo(ct));
            var verifier = new SshNetHostKeyVerifier(_hostEndpoint, trustContext);
            client.HostKeyReceived += verifier.HandleHostKeyReceived;

            try
            {
                using var diagnosticCapture = _diagnosticLoggerFactory.BeginCapture();
                await Task.Run(() => client.ConnectAsync(ct), ct).ConfigureAwait(false);
                return client;
            }
            catch (Exception ex)
            {
                client.Dispose();
                if (await HandleHostKeyConnectFailureAsync(
                    verifier, _hostEndpoint, ex, mayPrompt, ct))
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
        HostEndpointIdentity endpoint,
        Exception connectError,
        bool mayPrompt,
        CancellationToken ct)
    {
        if (verifier.LastError is { } verificationError)
        {
            LogFailure("Host key", verificationError, ConnectionLogSeverity.Error);
            throw new System.Security.SecurityException(
                $"Host-key verification failed for {endpoint.Value}: {verificationError.Message}",
                verificationError);
        }

        var verification = verifier.LastVerification;
        if (verification?.State == HostKeyTrustState.Changed)
        {
            Log(
                ConnectionLogSeverity.Critical,
                "Host key",
                $"서버 호스트 키가 변경되었습니다: {verification.Endpoint.Value}",
                $"The server host key has changed: {verification.Endpoint.Value}",
                $"trusted={verification.TrustedKey?.Algorithm} {verification.TrustedKey?.Sha256Fingerprint}\n" +
                $"presented={verification.PresentedKey.Algorithm} {verification.PresentedKey.Sha256Fingerprint}");
            throw new HostKeyChangedException(
                verification.Endpoint,
                verification.TrustedKey!,
                verification.PresentedKey);
        }

        if (verification?.State != HostKeyTrustState.Unknown)
            return false;

        Log(
            ConnectionLogSeverity.Warning,
            "Host key",
            $"처음 보는 서버 호스트 키입니다: {verification.Endpoint.Value}",
            $"The server presented an unknown host key: {verification.Endpoint.Value}",
            $"algorithm={verification.PresentedKey.Algorithm}; fingerprint={verification.PresentedKey.Sha256Fingerprint}");

        if (!mayPrompt)
        {
            throw new System.Security.SecurityException(
                $"Host key for {endpoint.Value} remained untrusted after approval.",
                connectError);
        }

        if (Info.HostKeyPromptAsync is null)
        {
            throw new System.Security.SecurityException(
                $"Unknown host key for {endpoint.Value}: " +
                $"{verification.PresentedKey.Algorithm} {verification.PresentedKey.Sha256Fingerprint}. " +
                "Connection cancelled because no trust prompt is available.",
                connectError);
        }

        var decision = await Info.HostKeyPromptAsync(verification, ct);
        ct.ThrowIfCancellationRequested();
        if (decision == HostKeyDecision.Cancel || !verifier.ApplyLastDecision(decision))
        {
            Log(
                ConnectionLogSeverity.Warning,
                "Host key",
                "사용자가 서버 호스트 키를 신뢰하지 않아 연결을 취소했습니다.",
                "The connection was cancelled because the server host key was not trusted.");
            throw new System.Security.SecurityException(
                $"Host key for {endpoint.Value} was not trusted. Connection cancelled.",
                connectError);
        }

        Log(
            ConnectionLogSeverity.Information,
            "Host key",
            decision == HostKeyDecision.TrustAndSave
                ? "서버 호스트 키를 확인하고 저장했습니다. 새 연결로 다시 시도합니다."
                : "서버 호스트 키를 이번 연결에서만 신뢰합니다. 새 연결로 다시 시도합니다.",
            decision == HostKeyDecision.TrustAndSave
                ? "The server host key was verified and saved. Retrying with a fresh connection."
                : "The server host key is trusted for this connection only. Retrying with a fresh connection.");

        return true;
    }

    private Renci.SshNet.ConnectionInfo BuildConnectionInfo(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Info.Username))
            throw new InvalidOperationException("Username을 입력하세요.");

        var user = Info.Username;
        var methods = BuildAuthenticationMethods(
            user,
            Info.AuthMethod,
            _password,
            Info.PrivateKeyPath,
            _passphrase,
            ct);
        var (transportHost, transportPort) = GetTargetTransportEndpoint();

        var connectionInfo = _route.Type switch
        {
            ConnectionRouteType.Direct =>
                new Renci.SshNet.ConnectionInfo(transportHost, transportPort, user, methods.ToArray()),
            ConnectionRouteType.HttpConnect or ConnectionRouteType.Socks4 or ConnectionRouteType.Socks5 =>
                new Renci.SshNet.ConnectionInfo(
                    Info.Host,
                    Info.Port,
                    user,
                    _route.Type switch
                    {
                        ConnectionRouteType.HttpConnect => ProxyTypes.Http,
                        ConnectionRouteType.Socks4 => ProxyTypes.Socks4,
                        ConnectionRouteType.Socks5 => ProxyTypes.Socks5,
                        _ => ProxyTypes.None,
                    },
                    _route.Host,
                    _route.Port,
                    _route.Username,
                    _route.Password,
                    methods.ToArray()),
            ConnectionRouteType.SshJump or ConnectionRouteType.ExternalProxyCommand =>
                new Renci.SshNet.ConnectionInfo(
                    transportHost,
                    transportPort,
                    user,
                    methods.ToArray()),
            _ => throw new NotSupportedException(
                $"The {_route.Type} route is not available in this build."),
        };

        connectionInfo.Timeout = TimeSpan.FromSeconds(15);
        connectionInfo.LoggerFactory = _diagnosticLoggerFactory;
        return connectionInfo;
    }

    /// <summary>
    /// PEM, OpenSSH, PKCS#8, PuTTY PPK v2/v3 형식의 키를 로드한다.
    /// </summary>
    private PrivateKeyFile LoadPrivateKey() => LoadPrivateKey(
        Info.PrivateKeyPath,
        _passphrase);

    private static PrivateKeyFile LoadPrivateKey(string path, string passphrase)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Private key 파일을 선택하세요.");
        if (!File.Exists(path))
            throw new FileNotFoundException($"키 파일을 찾을 수 없습니다: {path}");

        try
        {
            return string.IsNullOrEmpty(passphrase)
                ? new PrivateKeyFile(path)
                : new PrivateKeyFile(path, passphrase);
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

    private List<AuthenticationMethod> BuildAuthenticationMethods(
        string user,
        SshAuthMethod method,
        string password,
        string privateKeyPath,
        string passphrase,
        CancellationToken ct)
    {
        var methods = new List<AuthenticationMethod>();
        switch (method)
        {
            case SshAuthMethod.Password:
                methods.Add(new PasswordAuthenticationMethod(user, password));
                methods.Add(BuildKeyboardInteractive(user, password, ct));
                break;
            case SshAuthMethod.PublicKey:
                methods.Add(new PrivateKeyAuthenticationMethod(
                    user,
                    LoadPrivateKey(privateKeyPath, passphrase)));
                if (!string.IsNullOrEmpty(password))
                    methods.Add(new PasswordAuthenticationMethod(user, password));
                break;
            case SshAuthMethod.Agent:
                IPrivateKeySource[] keys;
                try
                {
                    keys = new SshAgent(TimeSpan.FromSeconds(3))
                        .RequestIdentities()
                        .Cast<IPrivateKeySource>()
                        .ToArray();
                }
                catch (Exception ex) when (ex is SshAgentException or TimeoutException or IOException)
                {
                    throw new InvalidOperationException(
                        "Windows SSH Agent에 연결할 수 없습니다. OpenSSH Authentication Agent 서비스를 시작하고 ssh-add로 키를 등록하세요.",
                        ex);
                }
                if (keys.Length == 0)
                    throw new InvalidOperationException(
                        "Windows OpenSSH Agent에 사용할 수 있는 키가 없습니다. ssh-add로 키를 추가하세요.");
                methods.Add(new PrivateKeyAuthenticationMethod(user, keys));
                break;
            case SshAuthMethod.KeyboardInteractive:
                methods.Add(BuildKeyboardInteractive(user, password, ct));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(method));
        }
        return methods;
    }

    // Password prompts are filled automatically. OTP and additional MFA prompts are
    // delegated to the UI and can occur more than once during one authentication.
    private KeyboardInteractiveAuthenticationMethod BuildKeyboardInteractive(
        string user,
        string password,
        CancellationToken ct)
    {
        var kbi = new KeyboardInteractiveAuthenticationMethod(user);
        kbi.AuthenticationPrompt += (_, e) =>
        {
            var unanswered = new List<Renci.SshNet.Common.AuthenticationPrompt>();
            foreach (var prompt in e.Prompts)
            {
                if (!string.IsNullOrEmpty(password) &&
                    prompt.Request.Contains("password", StringComparison.OrdinalIgnoreCase))
                {
                    prompt.Response = password;
                }
                else
                {
                    unanswered.Add(prompt);
                }
            }

            if (unanswered.Count == 0)
                return;
            if (Info.KeyboardInteractivePromptAsync is null)
                return;

            ct.ThrowIfCancellationRequested();
            var challenge = new KeyboardInteractiveChallenge(
                e.Instruction ?? "",
                e.Language ?? "",
                unanswered.Select(prompt => new KeyboardInteractivePrompt(
                    prompt.Request,
                    prompt.IsEchoed)).ToArray());
            var answers = Info.KeyboardInteractivePromptAsync(challenge, ct)
                .GetAwaiter()
                .GetResult();
            if (answers is null)
                throw new OperationCanceledException("Keyboard-interactive authentication was cancelled.", ct);
            if (answers.Count != unanswered.Count)
                throw new InvalidOperationException(
                    "Keyboard-interactive response count did not match the server prompts.");
            for (var index = 0; index < unanswered.Count; index++)
                unanswered[index].Response = answers[index] ?? "";
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

    private string DescribeRoute() => _route.Type == ConnectionRouteType.Direct
        ? "direct"
        : $"{_route.Type}({_route.Host}:{_route.Port})";

    private void LogFailure(
        string category,
        Exception error,
        ConnectionLogSeverity severity)
    {
        var summary = ConnectionFailureDetails.Summarize(error);
        Log(
            severity,
            category,
            summary.Korean,
            summary.English,
            ConnectionFailureDetails.Format(error));
    }

    private void Log(
        ConnectionLogSeverity severity,
        string category,
        string messageKo,
        string messageEn,
        string? detail = null) =>
        ConnectionLogStore.Append(
            Id,
            Info.Title,
            _hostEndpoint.Value,
            severity,
            category,
            messageKo,
            messageEn,
            detail);
}
