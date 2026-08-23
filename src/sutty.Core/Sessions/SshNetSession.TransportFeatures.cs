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
    private readonly List<ForwardedPort> _configuredForwardedPorts = [];
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

    private void StartConfiguredForwardings(SshClient ssh)
    {
        if (Info.PortForwardings.Count == 0)
            return;
        if (Info.PortForwardings.Count > 32)
            throw new InvalidOperationException("At most 32 port-forwarding rules are supported per session.");

        try
        {
            foreach (var rule in Info.PortForwardings)
            {
                ValidateForwardingRule(rule);
                if (ForwardingExposurePolicy.IsExternalBind(rule.BindHost))
                {
                    Log(
                        ConnectionLogSeverity.Warning,
                        "Port forwarding exposure",
                        $"{rule.BindHost}:{rule.BindPort} 포워딩이 루프백 외부 주소에 바인드됩니다.",
                        $"Forwarding {rule.BindHost}:{rule.BindPort} binds beyond loopback.");
                }
                ForwardedPort port = rule.Type switch
                {
                    SshPortForwardingType.Local => new ForwardedPortLocal(
                        rule.BindHost,
                        (uint)rule.BindPort,
                        rule.DestinationHost,
                        (uint)rule.DestinationPort),
                    SshPortForwardingType.Remote => new ForwardedPortRemote(
                        rule.BindHost,
                        (uint)rule.BindPort,
                        rule.DestinationHost,
                        (uint)rule.DestinationPort),
                    SshPortForwardingType.Dynamic => new ForwardedPortDynamic(
                        rule.BindHost,
                        (uint)rule.BindPort),
                    _ => throw new ArgumentOutOfRangeException(nameof(rule.Type)),
                };
                port.Exception += ForwardedPort_Exception;
                ssh.AddForwardedPort(port);
                port.Start();
                _configuredForwardedPorts.Add(port);
                Log(
                    ConnectionLogSeverity.Information,
                    "Port forwarding",
                    $"{DescribeForwarding(rule)} 포워딩을 시작했습니다.",
                    $"Started {DescribeForwarding(rule)} forwarding.");
            }
        }
        catch
        {
            StopConfiguredForwardings(ssh);
            throw;
        }
    }

    private static void ValidateForwardingRule(SshPortForwardingRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (string.IsNullOrWhiteSpace(rule.BindHost) || rule.BindHost.Any(char.IsControl))
            throw new InvalidOperationException("A valid forwarding bind host is required.");
        if (rule.BindPort is < 1 or > 65_535)
            throw new InvalidOperationException("A forwarding bind port must be between 1 and 65535.");
        if (rule.Type == SshPortForwardingType.Dynamic)
            return;
        if (string.IsNullOrWhiteSpace(rule.DestinationHost) ||
            rule.DestinationHost.Any(char.IsControl))
            throw new InvalidOperationException("A valid forwarding destination host is required.");
        if (rule.DestinationPort is < 1 or > 65_535)
            throw new InvalidOperationException("A forwarding destination port must be between 1 and 65535.");
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

    private static string DescribeForwarding(SshPortForwardingRule rule) => rule.Type switch
    {
        SshPortForwardingType.Dynamic =>
            $"dynamic {rule.BindHost}:{rule.BindPort}",
        _ =>
            $"{rule.Type.ToString().ToLowerInvariant()} {rule.BindHost}:{rule.BindPort}" +
            $" -> {rule.DestinationHost}:{rule.DestinationPort}",
    };

    private void StopConfiguredForwardings(SshClient? ssh)
    {
        foreach (var port in _configuredForwardedPorts.AsEnumerable().Reverse())
        {
            port.Exception -= ForwardedPort_Exception;
            try { if (port.IsStarted) port.Stop(); } catch { }
            try { ssh?.RemoveForwardedPort(port); } catch { }
            port.Dispose();
        }
        _configuredForwardedPorts.Clear();
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
