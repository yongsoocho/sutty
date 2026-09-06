using Renci.SshNet;
using Renci.SshNet.Common;
using sutty.Core.Diagnostics;
using sutty.Core.Models;
using sutty.Core.Routing;
using sutty.Core.Security;
using sutty.Core.Sftp;

namespace sutty.Core.Sessions;

public sealed partial class SshNetSession
{
    private readonly SessionTunnelManager _tunnels;
    private readonly object _portForwardingDiagnosticGate = new();
    private bool _portForwardingRuntimeFailed;
    private SshClient? _jumpClient;
    private ForwardedPortLocal? _jumpForward;
    private ProxyCommandBridge? _proxyCommandBridge;
    private int _routeTransportPort;

    private async Task PrepareConnectionRouteAsync(
        HostKeyTrustContext trustContext,
        CancellationToken ct)
    {
        switch (_route.Type)
        {
            case ConnectionRouteType.Direct:
            case ConnectionRouteType.HttpConnect:
            case ConnectionRouteType.Socks4:
            case ConnectionRouteType.Socks5:
                return;

            case ConnectionRouteType.SshJump:
                if (_jumpClient is { IsConnected: true } &&
                    _jumpForward is { IsStarted: true })
                    return;

                await CleanUpRouteAsync().ConfigureAwait(false);
                _jumpClient = await ConnectJumpClientAsync(trustContext, ct).ConfigureAwait(false);
                var forward = new ForwardedPortLocal(
                    "127.0.0.1",
                    0,
                    Info.Host,
                    (uint)Info.Port);
                _jumpClient.AddForwardedPort(forward);
                try
                {
                    forward.Start();
                    _jumpForward = forward;
                    _routeTransportPort = checked((int)forward.BoundPort);
                    Log(
                        ConnectionLogSeverity.Information,
                        "SSH jump",
                        $"점프 호스트 {_route.Host}:{_route.Port}를 통해 대상 연결 경로를 열었습니다.",
                        $"Opened the target route through jump host {_route.Host}:{_route.Port}.");
                }
                catch
                {
                    _jumpClient.RemoveForwardedPort(forward);
                    forward.Dispose();
                    await CleanUpRouteAsync().ConfigureAwait(false);
                    throw;
                }
                return;

            case ConnectionRouteType.ExternalProxyCommand:
                if (_proxyCommandBridge is not null)
                    return;
                _proxyCommandBridge = new ProxyCommandBridge(
                    _route.Command,
                    Info.Host,
                    Info.Port,
                    Info.Username);
                _routeTransportPort = _proxyCommandBridge.Port;
                Log(
                    ConnectionLogSeverity.Information,
                    "ProxyCommand",
                    "ProxyCommand 로컬 브리지를 시작했습니다.",
                    "Started the local ProxyCommand bridge.");
                return;

            default:
                throw new NotSupportedException(
                    $"The {_route.Type} route is not available in this build.");
        }
    }

    private (string Host, int Port) GetTargetTransportEndpoint() => _route.Type switch
    {
        ConnectionRouteType.SshJump or ConnectionRouteType.ExternalProxyCommand
            when _routeTransportPort is > 0 and <= 65_535 => ("127.0.0.1", _routeTransportPort),
        ConnectionRouteType.SshJump or ConnectionRouteType.ExternalProxyCommand =>
            throw new InvalidOperationException("The selected SSH route is not ready."),
        _ => (Info.Host, Info.Port),
    };

    private async Task<SshClient> ConnectJumpClientAsync(
        HostKeyTrustContext trustContext,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_route.Username))
            throw new InvalidOperationException("Jump host username is required.");

        var endpoint = HostEndpointIdentity.Create(_route.Host, _route.Port);
        var mayPrompt = true;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var methods = BuildAuthenticationMethods(
                _route.Username,
                _route.AuthMethod,
                _routePassword,
                _route.PrivateKeyPath,
                _routePassphrase,
                ct);
            var connectionInfo = new Renci.SshNet.ConnectionInfo(
                _route.Host,
                _route.Port,
                _route.Username,
                methods.ToArray())
            {
                Timeout = TimeSpan.FromSeconds(15),
                LoggerFactory = _diagnosticLoggerFactory,
            };
            var client = new SshClient(connectionInfo);
            if (Info.KeepAliveSeconds > 0)
                client.KeepAliveInterval = TimeSpan.FromSeconds(Info.KeepAliveSeconds);
            var verifier = new SshNetHostKeyVerifier(endpoint, trustContext);
            client.HostKeyReceived += verifier.HandleHostKeyReceived;

            try
            {
                using var diagnosticCapture = _diagnosticLoggerFactory.BeginCapture();
                await Task.Run(() => client.ConnectAsync(ct), ct).ConfigureAwait(false);
                TryMarkHostKeyUsed(verifier, endpoint);
                return client;
            }
            catch (Exception error)
            {
                client.Dispose();
                if (await HandleHostKeyConnectFailureAsync(
                    verifier,
                    endpoint,
                    error,
                    mayPrompt,
                    ct).ConfigureAwait(false))
                {
                    mayPrompt = false;
                    continue;
                }
                throw;
            }
        }
    }

    public IReadOnlyList<TunnelSnapshot> Tunnels => _tunnels.Snapshot();
    public event EventHandler? TunnelsChanged
    {
        add => _tunnels.Changed += value;
        remove => _tunnels.Changed -= value;
    }

    public async Task<Guid> AddTunnelAsync(SshPortForwardingRule rule, CancellationToken ct = default)
    {
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State != SessionState.Connected)
                throw new InvalidOperationException("Connect the SSH session before adding a tunnel.");
            return _tunnels.Add(rule);
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task StartTunnelAsync(Guid id, bool allowExternalBind = false,
        CancellationToken ct = default)
    {
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State != SessionState.Connected)
                throw new InvalidOperationException("Connect the SSH session before starting a tunnel.");
            await _tunnels.StartAsync(id, allowExternalBind, ct).ConfigureAwait(false);
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task StopTunnelAsync(Guid id, CancellationToken ct = default)
    {
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try { await _tunnels.StopAsync(id, ct).ConfigureAwait(false); }
        finally { _lifecycleGate.Release(); }
    }

    private async Task StartConfiguredForwardingsAsync(CancellationToken ct)
    {
        foreach (var tunnel in _tunnels.Snapshot())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Initial external bindings already passed the connection workflow's
                // default-cancel exposure confirmation. Every runtime start asks again.
                await _tunnels.StartAsync(tunnel.Id, allowExternalBind: true, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                // One unavailable bind or server-rejected forward must not tear down
                // a healthy SSH terminal. The failed row remains available for retry.
                ForwardedPort_Exception(this, new ExceptionEventArgs(error));
            }
        }
    }

    private ITunnelListener CreateTunnelListener(TunnelDefinition rule)
    {
        var ssh = _ssh;
        if (ssh is not { IsConnected: true })
            throw new InvalidOperationException("SSH session is not connected.");
        ForwardedPort port = rule.Type switch
        {
            SshPortForwardingType.Local => new ForwardedPortLocal(rule.BindHost,
                (uint)rule.BindPort, rule.DestinationHost, (uint)rule.DestinationPort),
            SshPortForwardingType.Remote => new ForwardedPortRemote(rule.BindHost,
                (uint)rule.BindPort, rule.DestinationHost, (uint)rule.DestinationPort),
            SshPortForwardingType.Dynamic => new ForwardedPortDynamic(rule.BindHost, (uint)rule.BindPort),
            _ => throw new ArgumentOutOfRangeException(nameof(rule.Type)),
        };
        return new SshNetTunnelListener(ssh, port, ForwardedPort_Exception);
    }

    private sealed class SshNetTunnelListener : ITunnelListener
    {
        private readonly SshClient _client;
        private readonly ForwardedPort _port;
        private readonly EventHandler<ExceptionEventArgs> _diagnosticHandler;
        private volatile bool _disposed;
        public bool IsStarted => !_disposed && _port.IsStarted;
        public event EventHandler? Failed;
        public event EventHandler? Closing;

        public SshNetTunnelListener(SshClient client, ForwardedPort port,
            EventHandler<ExceptionEventArgs> diagnosticHandler)
        {
            _client = client;
            _port = port;
            _diagnosticHandler = diagnosticHandler;
            _port.Exception += OnException;
            _port.Closing += OnClosing;
        }

        public void Start()
        {
            _client.AddForwardedPort(_port);
            _port.Start();
        }

        private void OnException(object? sender, ExceptionEventArgs e)
        {
            Failed?.Invoke(this, EventArgs.Empty);
            _diagnosticHandler(sender, e);
        }

        private void OnClosing(object? sender, EventArgs e) => Closing?.Invoke(this, e);

        public void Dispose()
        {
            if (_disposed) return;
            _port.Exception -= OnException;
            _port.Closing -= OnClosing;
            try { if (_port.IsStarted) _port.Stop(); }
            finally
            {
                try { _client.RemoveForwardedPort(_port); }
                finally { _port.Dispose(); _disposed = true; }
            }
        }
    }

    private void ForwardedPort_Exception(object? sender, ExceptionEventArgs e)
    {
        var diagnosis = ConnectionExceptionClassifier.Classify(
            e.Exception,
            ConnectionDiagnosticStage.PortForwarding,
            _route.Type);
        lock (_portForwardingDiagnosticGate)
        {
            _portForwardingRuntimeFailed = true;
            LastDiagnostic = diagnosis;
            RecordDiagnostic(diagnosis);
        }
        LogFailure("Port forwarding", e.Exception, ConnectionLogSeverity.Error);
    }

    private void BeginPortForwardingDiagnostics()
    {
        lock (_portForwardingDiagnosticGate)
            _portForwardingRuntimeFailed = false;
    }

    private void RecordPortForwardingStarted()
    {
        lock (_portForwardingDiagnosticGate)
        {
            if (!_portForwardingRuntimeFailed)
            {
                RecordDiagnostic(ConnectionDiagnosticResult.Succeeded(
                    ConnectionDiagnosticStage.PortForwarding));
            }
        }
    }

    private async Task CleanUpRouteAsync()
    {
        _routeTransportPort = 0;
        var forward = _jumpForward;
        _jumpForward = null;
        var jump = _jumpClient;
        _jumpClient = null;
        if (forward is not null)
        {
            try { if (forward.IsStarted) forward.Stop(); } catch { }
            try { jump?.RemoveForwardedPort(forward); } catch { }
            forward.Dispose();
        }
        if (jump is not null)
        {
            await Task.Run(() =>
            {
                try { jump.Disconnect(); } catch { }
                jump.Dispose();
            }).ConfigureAwait(false);
        }

        var bridge = _proxyCommandBridge;
        _proxyCommandBridge = null;
        if (bridge is not null)
            await bridge.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<SftpClient?> ReconnectSftpForTransferAsync(CancellationToken ct)
    {
        if (State != SessionState.Connected)
            throw new InvalidOperationException("SSH session is not connected.");
        var trustContext = _activeTrustContext
            ?? throw new InvalidOperationException("SSH host-key trust context is unavailable.");

        SetSftpState(SftpConnectionState.Connecting);
        var previous = _sftpClient;
        _sftpClient = null;
        try { previous?.Disconnect(); } catch { }
        previous?.Dispose();

        RecordDiagnostic(ConnectionDiagnosticResult.Running(
            ConnectionDiagnosticStage.SftpSubsystem));
        try
        {
            await PrepareConnectionRouteAsync(trustContext, ct).ConfigureAwait(false);
            var replacement = await ConnectSftpClientAsync(trustContext, ct).ConfigureAwait(false);
            if (State != SessionState.Connected)
            {
                replacement.Dispose();
                throw new OperationCanceledException("The SSH session closed during SFTP reconnect.");
            }
            _sftpClient = replacement;
            LastSftpError = null;
            LastSftpDiagnostic = null;
            SetSftpState(SftpConnectionState.Ready);
            RecordDiagnostic(ConnectionDiagnosticResult.Succeeded(
                ConnectionDiagnosticStage.SftpSubsystem));
            return replacement;
        }
        catch (OperationCanceledException error)
        {
            LastSftpDiagnostic = ConnectionExceptionClassifier.Classify(
                error,
                ConnectionDiagnosticStage.SftpSubsystem,
                _route.Type);
            RecordSftpDiagnostic(LastSftpDiagnostic);
            SetSftpState(SftpConnectionState.Unavailable);
            throw;
        }
        catch (Exception error)
        {
            LastSftpError = error.Message;
            LastSftpDiagnostic = ConnectionExceptionClassifier.Classify(
                error,
                ConnectionDiagnosticStage.SftpSubsystem,
                _route.Type);
            RecordSftpDiagnostic(LastSftpDiagnostic);
            SetSftpState(SftpConnectionState.Unavailable);
            throw;
        }
    }
}
