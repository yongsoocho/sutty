using Renci.SshNet.Common;
using sutty.Core.Routing;
using sutty.Core.Security;
using System.Net.Sockets;
using System.Security;

namespace sutty.Core.Diagnostics;

/// <summary>The ordered user-visible stages of the Connection Doctor.</summary>
public enum ConnectionDiagnosticStage
{
    InputValidation = 1,
    DnsAndTcp = 2,
    ProxyOrJumpRoute = 3,
    SshHandshake = 4,
    HostKey = 5,
    Authentication = 6,
    Pty = 7,
    SftpSubsystem = 8,
    PortForwarding = 9,
}

public enum ConnectionDiagnosticStatus
{
    NotStarted,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Skipped,
}

/// <summary>
/// A single Connection Doctor outcome. TechnicalDetail is deliberately a bounded,
/// structured summary and never contains an exception message.
/// </summary>
public sealed record ConnectionDiagnosticResult
{
    private ConnectionDiagnosticResult(
        ConnectionDiagnosticStage stage,
        ConnectionDiagnosticStatus status,
        string errorCode,
        string userActionKo,
        string userActionEn,
        string technicalDetail)
    {
        DiagnosticContract.ValidateOutcome(stage, status, errorCode);
        Stage = stage;
        Status = status;
        ErrorCode = errorCode;
        UserActionKo = userActionKo;
        UserActionEn = userActionEn;
        TechnicalDetail = technicalDetail;
    }

    public ConnectionDiagnosticStage Stage { get; }
    public ConnectionDiagnosticStatus Status { get; }
    public string ErrorCode { get; }
    public string UserActionKo { get; }
    public string UserActionEn { get; }
    public string TechnicalDetail { get; }

    public static ConnectionDiagnosticResult NotStarted(ConnectionDiagnosticStage stage) =>
        CreateNonFailure(stage, ConnectionDiagnosticStatus.NotStarted);

    public static ConnectionDiagnosticResult Running(ConnectionDiagnosticStage stage) =>
        CreateNonFailure(stage, ConnectionDiagnosticStatus.Running);

    public static ConnectionDiagnosticResult Succeeded(ConnectionDiagnosticStage stage) =>
        CreateNonFailure(stage, ConnectionDiagnosticStatus.Succeeded);

    public static ConnectionDiagnosticResult Skipped(ConnectionDiagnosticStage stage) =>
        CreateNonFailure(stage, ConnectionDiagnosticStatus.Skipped);

    internal static ConnectionDiagnosticResult Failure(
        ConnectionDiagnosticStage stage,
        string errorCode,
        string userActionKo,
        string userActionEn,
        string technicalDetail) => new(
            stage,
            ConnectionDiagnosticStatus.Failed,
            errorCode,
            userActionKo,
            userActionEn,
            technicalDetail);

    internal static ConnectionDiagnosticResult Cancelled(
        ConnectionDiagnosticStage stage,
        string technicalDetail) => new(
            stage,
            ConnectionDiagnosticStatus.Cancelled,
            ConnectionDiagnosticErrorCodes.ConnectionCancelled,
            "취소가 의도된 것인지 확인한 뒤 필요하면 다시 시도하세요.",
            "Confirm that cancellation was intentional, then retry if needed.",
            technicalDetail);

    private static ConnectionDiagnosticResult CreateNonFailure(
        ConnectionDiagnosticStage stage,
        ConnectionDiagnosticStatus status) => new(
            stage,
            status,
            ConnectionDiagnosticErrorCodes.None,
            "",
            "",
            "");
}

/// <summary>Stable, locale-independent Connection Doctor error codes.</summary>
public static class ConnectionDiagnosticErrorCodes
{
    public const string None = "NONE";
    public const string InputInvalid = "INPUT_INVALID";
    public const string DnsLookupFailed = "DNS_LOOKUP_FAILED";
    public const string TcpConnectionRefused = "TCP_CONNECTION_REFUSED";
    public const string TcpTimedOut = "TCP_TIMED_OUT";
    public const string TcpUnreachable = "TCP_UNREACHABLE";
    public const string TcpFailed = "TCP_FAILED";
    public const string RoutePolicyBlocked = "ROUTE_POLICY_BLOCKED";
    public const string RouteSocks5Refused = "ROUTE_SOCKS5_REFUSED";
    public const string RouteProxyRefused = "ROUTE_PROXY_REFUSED";
    public const string RouteJumpRefused = "ROUTE_JUMP_REFUSED";
    public const string RouteAuthenticationFailed = "ROUTE_AUTHENTICATION_FAILED";
    public const string RouteTimedOut = "ROUTE_TIMED_OUT";
    public const string RouteFailed = "ROUTE_FAILED";
    public const string SshHandshakeTimedOut = "SSH_HANDSHAKE_TIMED_OUT";
    public const string SshHandshakeFailed = "SSH_HANDSHAKE_FAILED";
    public const string HostKeyChanged = "HOST_KEY_CHANGED";
    public const string HostKeyRejected = "HOST_KEY_REJECTED";
    public const string AuthenticationFailed = "AUTHENTICATION_FAILED";
    public const string AuthenticationTimedOut = "AUTHENTICATION_TIMED_OUT";
    public const string AuthenticationKeyFileMissing = "AUTH_KEY_FILE_MISSING";
    public const string AuthenticationKeyFileDenied = "AUTH_KEY_FILE_ACCESS_DENIED";
    public const string PtyRequestFailed = "PTY_REQUEST_FAILED";
    public const string SftpSubsystemUnavailable = "SFTP_SUBSYSTEM_UNAVAILABLE";
    public const string PortForwardingFailed = "PORT_FORWARDING_FAILED";
    public const string ConnectionCancelled = "CONNECTION_CANCELLED";
    public const string UnexpectedFailure = "CONNECTION_UNEXPECTED_FAILURE";

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        None,
        InputInvalid,
        DnsLookupFailed,
        TcpConnectionRefused,
        TcpTimedOut,
        TcpUnreachable,
        TcpFailed,
        RoutePolicyBlocked,
        RouteSocks5Refused,
        RouteProxyRefused,
        RouteJumpRefused,
        RouteAuthenticationFailed,
        RouteTimedOut,
        RouteFailed,
        SshHandshakeTimedOut,
        SshHandshakeFailed,
        HostKeyChanged,
        HostKeyRejected,
        AuthenticationFailed,
        AuthenticationTimedOut,
        AuthenticationKeyFileMissing,
        AuthenticationKeyFileDenied,
        PtyRequestFailed,
        SftpSubsystemUnavailable,
        PortForwardingFailed,
        ConnectionCancelled,
        UnexpectedFailure,
    };

    public static bool IsKnown(string? value) =>
        value is not null && Known.Contains(value);

    internal static string NormalizeKnown(string? value, string parameterName)
    {
        var normalized = (value ?? "").Trim().ToUpperInvariant();
        if (!Known.Contains(normalized))
            throw new ArgumentException("The diagnostic error code is not recognized.", parameterName);
        return normalized;
    }
}

/// <summary>Maps connection exceptions to stable, actionable Connection Doctor outcomes.</summary>
public static class ConnectionExceptionClassifier
{
    public static ConnectionDiagnosticResult Classify(
        Exception error,
        ConnectionDiagnosticStage? stageHint = null,
        ConnectionRouteType routeType = ConnectionRouteType.Direct)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (stageHint is { } stage && !Enum.IsDefined(stage))
            throw new ArgumentOutOfRangeException(nameof(stageHint));
        if (!Enum.IsDefined(routeType))
            throw new ArgumentOutOfRangeException(nameof(routeType));

        if (Find<OperationCanceledException>(error) is { } cancelled)
        {
            return ConnectionDiagnosticResult.Cancelled(
                stageHint ?? ConnectionDiagnosticStage.SshHandshake,
                TypeDetail(cancelled));
        }

        if (stageHint is ConnectionDiagnosticStage.Pty)
            return Failure(ConnectionDiagnosticStage.Pty, ConnectionDiagnosticErrorCodes.PtyRequestFailed, error);
        if (stageHint is ConnectionDiagnosticStage.PortForwarding)
            return Failure(ConnectionDiagnosticStage.PortForwarding, ConnectionDiagnosticErrorCodes.PortForwardingFailed, error);
        if (stageHint is ConnectionDiagnosticStage.InputValidation)
            return Failure(ConnectionDiagnosticStage.InputValidation, ConnectionDiagnosticErrorCodes.InputInvalid, error);
        if (Find<HostKeyChangedException>(error) is not null)
            return Failure(ConnectionDiagnosticStage.HostKey, ConnectionDiagnosticErrorCodes.HostKeyChanged, error);
        if (Find<SecurityException>(error) is not null)
            return Failure(ConnectionDiagnosticStage.HostKey, ConnectionDiagnosticErrorCodes.HostKeyRejected, error);
        if (stageHint is ConnectionDiagnosticStage.HostKey)
        {
            var code = Find<HostKeyChangedException>(error) is null
                ? ConnectionDiagnosticErrorCodes.HostKeyRejected
                : ConnectionDiagnosticErrorCodes.HostKeyChanged;
            return Failure(ConnectionDiagnosticStage.HostKey, code, error);
        }
        if (stageHint is ConnectionDiagnosticStage.SftpSubsystem)
            return Failure(ConnectionDiagnosticStage.SftpSubsystem, ConnectionDiagnosticErrorCodes.SftpSubsystemUnavailable, error);
        if (stageHint is ConnectionDiagnosticStage.Authentication)
            return ClassifyAuthentication(error);
        if (stageHint is ConnectionDiagnosticStage.ProxyOrJumpRoute)
            return ClassifyRoute(error, routeType);

        if (Find<RoutePolicyViolationException>(error) is not null ||
            Find<ProxyException>(error) is not null)
        {
            return ClassifyRoute(error, routeType);
        }
        if (Find<SshAuthenticationException>(error) is not null ||
            Find<FileNotFoundException>(error) is not null ||
            Find<UnauthorizedAccessException>(error) is not null)
        {
            return ClassifyAuthentication(error);
        }
        if (Find<ArgumentException>(error) is not null || Find<FormatException>(error) is not null)
            return Failure(ConnectionDiagnosticStage.InputValidation, ConnectionDiagnosticErrorCodes.InputInvalid, error);
        if (Find<SocketException>(error) is { } socket)
            return ClassifySocket(socket);
        if (IsTimeout(error))
        {
            return Failure(
                stageHint ?? ConnectionDiagnosticStage.SshHandshake,
                stageHint == ConnectionDiagnosticStage.DnsAndTcp
                    ? ConnectionDiagnosticErrorCodes.TcpTimedOut
                    : ConnectionDiagnosticErrorCodes.SshHandshakeTimedOut,
                error);
        }
        if (Find<SshConnectionException>(error) is not null || Find<SshException>(error) is not null)
            return Failure(ConnectionDiagnosticStage.SshHandshake, ConnectionDiagnosticErrorCodes.SshHandshakeFailed, error);

        return Failure(
            stageHint ?? ConnectionDiagnosticStage.SshHandshake,
            ConnectionDiagnosticErrorCodes.UnexpectedFailure,
            error);
    }

    private static ConnectionDiagnosticResult ClassifyAuthentication(Exception error)
    {
        if (Find<FileNotFoundException>(error) is not null)
        {
            return Failure(
                ConnectionDiagnosticStage.Authentication,
                ConnectionDiagnosticErrorCodes.AuthenticationKeyFileMissing,
                error);
        }
        if (Find<UnauthorizedAccessException>(error) is not null)
        {
            return Failure(
                ConnectionDiagnosticStage.Authentication,
                ConnectionDiagnosticErrorCodes.AuthenticationKeyFileDenied,
                error);
        }
        if (IsTimeout(error))
        {
            return Failure(
                ConnectionDiagnosticStage.Authentication,
                ConnectionDiagnosticErrorCodes.AuthenticationTimedOut,
                error);
        }

        return Failure(
            ConnectionDiagnosticStage.Authentication,
            ConnectionDiagnosticErrorCodes.AuthenticationFailed,
            error);
    }

    private static ConnectionDiagnosticResult ClassifyRoute(
        Exception error,
        ConnectionRouteType routeType)
    {
        if (Find<RoutePolicyViolationException>(error) is not null)
        {
            return Failure(
                ConnectionDiagnosticStage.ProxyOrJumpRoute,
                ConnectionDiagnosticErrorCodes.RoutePolicyBlocked,
                error);
        }
        if (Find<SshAuthenticationException>(error) is not null)
        {
            return Failure(
                ConnectionDiagnosticStage.ProxyOrJumpRoute,
                ConnectionDiagnosticErrorCodes.RouteAuthenticationFailed,
                error);
        }
        if (IsTimeout(error) ||
            Find<SocketException>(error) is { SocketErrorCode: SocketError.TimedOut })
        {
            return Failure(
                ConnectionDiagnosticStage.ProxyOrJumpRoute,
                ConnectionDiagnosticErrorCodes.RouteTimedOut,
                error);
        }
        if (Find<SocketException>(error) is { SocketErrorCode: SocketError.ConnectionRefused })
        {
            var code = routeType switch
            {
                ConnectionRouteType.Socks5 => ConnectionDiagnosticErrorCodes.RouteSocks5Refused,
                ConnectionRouteType.SshJump => ConnectionDiagnosticErrorCodes.RouteJumpRefused,
                _ => ConnectionDiagnosticErrorCodes.RouteProxyRefused,
            };
            return Failure(ConnectionDiagnosticStage.ProxyOrJumpRoute, code, error);
        }

        return Failure(
            ConnectionDiagnosticStage.ProxyOrJumpRoute,
            ConnectionDiagnosticErrorCodes.RouteFailed,
            error);
    }

    private static ConnectionDiagnosticResult ClassifySocket(SocketException socket)
    {
        var code = socket.SocketErrorCode switch
        {
            SocketError.HostNotFound or SocketError.NoData => ConnectionDiagnosticErrorCodes.DnsLookupFailed,
            SocketError.ConnectionRefused => ConnectionDiagnosticErrorCodes.TcpConnectionRefused,
            SocketError.TimedOut => ConnectionDiagnosticErrorCodes.TcpTimedOut,
            SocketError.NetworkUnreachable or SocketError.HostUnreachable => ConnectionDiagnosticErrorCodes.TcpUnreachable,
            _ => ConnectionDiagnosticErrorCodes.TcpFailed,
        };
        return Failure(ConnectionDiagnosticStage.DnsAndTcp, code, socket);
    }

    private static ConnectionDiagnosticResult Failure(
        ConnectionDiagnosticStage stage,
        string code,
        Exception error)
    {
        var (ko, en) = UserAction(code);
        return ConnectionDiagnosticResult.Failure(stage, code, ko, en, TechnicalDetail(error));
    }

    private static (string Korean, string English) UserAction(string code) => code switch
    {
        ConnectionDiagnosticErrorCodes.InputInvalid =>
            ("호스트, 포트, 사용자 이름과 선택한 인증 설정을 확인하세요.",
             "Check the host, port, username, and selected authentication settings."),
        ConnectionDiagnosticErrorCodes.DnsLookupFailed =>
            ("주소와 DNS 설정을 확인하세요.", "Check the address and DNS settings."),
        ConnectionDiagnosticErrorCodes.TcpConnectionRefused =>
            ("SSH 서비스가 실행 중인지와 포트·방화벽 설정을 확인하세요.",
             "Check that the SSH service is running and verify the port and firewall."),
        ConnectionDiagnosticErrorCodes.TcpTimedOut or ConnectionDiagnosticErrorCodes.TcpUnreachable or
        ConnectionDiagnosticErrorCodes.TcpFailed =>
            ("네트워크, VPN, 방화벽과 대상 서버 상태를 확인하세요.",
             "Check the network, VPN, firewall, and target server status."),
        ConnectionDiagnosticErrorCodes.RoutePolicyBlocked =>
            ("선택한 Proxy·Jump 경로와 Direct 연결 금지 정책을 확인하세요.",
             "Check the selected proxy or jump route and the direct-connection policy."),
        ConnectionDiagnosticErrorCodes.RouteSocks5Refused or
        ConnectionDiagnosticErrorCodes.RouteProxyRefused or
        ConnectionDiagnosticErrorCodes.RouteJumpRefused =>
            ("Proxy·Jump 주소, 포트, 서비스 상태와 인증 정보를 확인하세요.",
             "Check the proxy or jump address, port, service status, and authentication."),
        ConnectionDiagnosticErrorCodes.RouteAuthenticationFailed =>
            ("Proxy 또는 Jump Host 인증 정보를 확인하세요.",
             "Check the proxy or jump-host authentication settings."),
        ConnectionDiagnosticErrorCodes.RouteTimedOut or ConnectionDiagnosticErrorCodes.RouteFailed =>
            ("Proxy·Jump 경로 설정과 중간 서버 상태를 확인하세요.",
             "Check the proxy or jump route and the intermediate server."),
        ConnectionDiagnosticErrorCodes.SshHandshakeTimedOut or
        ConnectionDiagnosticErrorCodes.SshHandshakeFailed =>
            ("서버의 SSH 버전, 암호화 정책과 연결 제한을 확인하세요.",
             "Check the server SSH version, cryptographic policy, and connection limits."),
        ConnectionDiagnosticErrorCodes.HostKeyChanged =>
            ("자동 교체하지 말고 관리자에게 새 지문을 별도 경로로 확인하세요.",
             "Do not replace the key automatically; verify the new fingerprint out of band."),
        ConnectionDiagnosticErrorCodes.HostKeyRejected =>
            ("표시된 호스트 키 지문을 별도 경로로 확인한 뒤 신뢰 여부를 결정하세요.",
             "Verify the displayed host-key fingerprint out of band before trusting it."),
        ConnectionDiagnosticErrorCodes.AuthenticationKeyFileMissing =>
            ("개인키 파일 위치를 다시 선택하세요.", "Select the private-key file again."),
        ConnectionDiagnosticErrorCodes.AuthenticationKeyFileDenied =>
            ("개인키 파일 읽기 권한을 확인하세요.", "Check read permission for the private-key file."),
        ConnectionDiagnosticErrorCodes.AuthenticationTimedOut or
        ConnectionDiagnosticErrorCodes.AuthenticationFailed =>
            ("사용자 이름, 비밀번호, 개인키, Agent 및 OTP 설정을 확인하세요.",
             "Check the username, password, private key, Agent, and OTP settings."),
        ConnectionDiagnosticErrorCodes.PtyRequestFailed =>
            ("서버 계정의 PTY 허용 정책과 셸 설정을 확인하세요.",
             "Check the server account PTY policy and shell configuration."),
        ConnectionDiagnosticErrorCodes.SftpSubsystemUnavailable =>
            ("서버의 SFTP subsystem 설정과 계정 권한을 확인하세요.",
             "Check the server SFTP subsystem configuration and account permissions."),
        ConnectionDiagnosticErrorCodes.PortForwardingFailed =>
            ("Bind 주소·포트 충돌과 서버 forwarding 정책을 확인하세요.",
             "Check bind-address or port conflicts and the server forwarding policy."),
        _ =>
            ("연결 설정을 확인한 뒤 다시 시도하세요.", "Review the connection settings and retry."),
    };

    private static string TechnicalDetail(Exception error)
    {
        if (Find<SocketException>(error) is { } socket)
        {
            return $"Exception=SocketException;SocketError={socket.SocketErrorCode};" +
                   $"NativeError={socket.NativeErrorCode}";
        }
        if (Find<SshConnectionException>(error) is { } connection)
            return $"Exception=SshConnectionException;DisconnectReason={connection.DisconnectReason}";
        if (Find<RoutePolicyViolationException>(error) is { } routePolicy)
        {
            var policyCode = routePolicy.Code.All(character =>
                character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')
                ? routePolicy.Code
                : "UNSPECIFIED";
            return $"Exception=RoutePolicyViolationException;PolicyCode={policyCode}";
        }

        return TypeDetail(FindFirst(error));
    }

    private static string TypeDetail(Exception error) =>
        $"Exception={error.GetType().Name}";

    private static bool IsTimeout(Exception error) =>
        Find<TimeoutException>(error) is not null ||
        Find<SshOperationTimeoutException>(error) is not null;

    private static Exception FindFirst(Exception error)
    {
        if (error is AggregateException aggregate)
            return aggregate.Flatten().InnerExceptions.FirstOrDefault() ?? error;
        return error;
    }

    private static TException? Find<TException>(Exception? error)
        where TException : Exception
    {
        var inspected = 0;
        while (error is not null && inspected++ < 16)
        {
            if (error is TException match)
                return match;
            if (error is AggregateException aggregate)
            {
                foreach (var inner in aggregate.Flatten().InnerExceptions)
                {
                    if (Find<TException>(inner) is { } aggregateMatch)
                        return aggregateMatch;
                }
                return null;
            }
            error = error.InnerException;
        }
        return null;
    }
}

internal static class DiagnosticContract
{
    internal static void ValidateOutcome(
        ConnectionDiagnosticStage stage,
        ConnectionDiagnosticStatus status,
        string errorCode)
    {
        if (!Enum.IsDefined(stage))
            throw new ArgumentOutOfRangeException(nameof(stage));
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (!ConnectionDiagnosticErrorCodes.IsKnown(errorCode))
            throw new ArgumentException("The diagnostic error code is not recognized.", nameof(errorCode));

        var mustHaveNoError = status is ConnectionDiagnosticStatus.NotStarted or
            ConnectionDiagnosticStatus.Running or
            ConnectionDiagnosticStatus.Succeeded or
            ConnectionDiagnosticStatus.Skipped;
        if (mustHaveNoError && errorCode != ConnectionDiagnosticErrorCodes.None)
            throw new ArgumentException("A non-failure status cannot contain an error code.", nameof(errorCode));
        if (status == ConnectionDiagnosticStatus.Failed && errorCode == ConnectionDiagnosticErrorCodes.None)
            throw new ArgumentException("A failed status requires an error code.", nameof(errorCode));
        if (status == ConnectionDiagnosticStatus.Cancelled &&
            errorCode != ConnectionDiagnosticErrorCodes.ConnectionCancelled)
        {
            throw new ArgumentException("A cancelled status requires CONNECTION_CANCELLED.", nameof(errorCode));
        }
    }
}
