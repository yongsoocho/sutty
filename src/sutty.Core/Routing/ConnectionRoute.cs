using sutty.Core.Models;

namespace sutty.Core.Routing;

public enum ConnectionRouteType
{
    Direct,
    HttpConnect,
    Socks4,
    Socks5,
    SshJump,
    AuditedGateway,
    ExternalProxyCommand,
}

/// <summary>
/// A route to the target. Proxy credentials are transient and must not be written to host
/// profiles or settings JSON.
/// </summary>
public sealed class ConnectionRoute
{
    public string Id { get; set; } = "direct";
    public ConnectionRouteType Type { get; set; } = ConnectionRouteType.Direct;
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public SshAuthMethod AuthMethod { get; set; } = SshAuthMethod.Password;
    public string PrivateKeyPath { get; set; } = "";
    public string Passphrase { get; set; } = "";
    public string Command { get; set; } = "";
    public bool ProxyDns { get; set; } = true;

    public string DisplayName => Type switch
    {
        ConnectionRouteType.Direct => "DIRECT",
        ConnectionRouteType.HttpConnect => "HTTP CONNECT",
        ConnectionRouteType.Socks4 => "SOCKS4",
        ConnectionRouteType.Socks5 => "SOCKS5",
        ConnectionRouteType.SshJump => "SSH JUMP",
        ConnectionRouteType.AuditedGateway => "AUDITED",
        ConnectionRouteType.ExternalProxyCommand => "PROXY COMMAND",
        _ => Type.ToString().ToUpperInvariant(),
    };
}

/// <summary>Policy applied before any network client is created.</summary>
public sealed class ConnectionRoutePolicy
{
    public bool EnterpriseMode { get; set; }
    public bool DisableDirect { get; set; }
    public string RequiredRouteId { get; set; } = "";
    public List<ConnectionRouteType> AllowedRouteTypes { get; set; } = [];
}

/// <summary>An immutable, validated route used by both terminal SSH and SFTP.</summary>
public sealed record ResolvedConnectionRoute(
    string Id,
    ConnectionRouteType Type,
    string Host,
    int Port,
    string Username,
    string Password,
    SshAuthMethod AuthMethod,
    string PrivateKeyPath,
    string Passphrase,
    string Command,
    bool ProxyDns)
{
    public string DisplayName => new ConnectionRoute { Type = Type }.DisplayName;
}

public sealed class RoutePolicyViolationException : InvalidOperationException
{
    public RoutePolicyViolationException(string message) : base(message) { }
}

public static class RouteResolver
{
    public static ResolvedConnectionRoute Resolve(
        ConnectionRoute? requested,
        ConnectionRoutePolicy? policy)
    {
        requested ??= new ConnectionRoute();
        policy ??= new ConnectionRoutePolicy();

        var routeId = string.IsNullOrWhiteSpace(requested.Id)
            ? requested.Type.ToString().ToLowerInvariant()
            : requested.Id.Trim();
        if (routeId.Length > 128 || routeId.Any(char.IsControl))
            throw new RoutePolicyViolationException("The route id is invalid.");

        if ((policy.EnterpriseMode || policy.DisableDirect) &&
            requested.Type == ConnectionRouteType.Direct)
        {
            throw new RoutePolicyViolationException(
                "Direct connections are disabled by the active connection policy.");
        }

        if (!string.IsNullOrWhiteSpace(policy.RequiredRouteId) &&
            !string.Equals(routeId, policy.RequiredRouteId.Trim(), StringComparison.Ordinal))
        {
            throw new RoutePolicyViolationException(
                "The selected route does not match the route required by policy.");
        }

        if (policy.AllowedRouteTypes.Count > 0 &&
            !policy.AllowedRouteTypes.Contains(requested.Type))
        {
            throw new RoutePolicyViolationException(
                $"The {requested.Type} route is not allowed by policy.");
        }

        if (requested.Type is not ConnectionRouteType.Direct and
            not ConnectionRouteType.ExternalProxyCommand)
        {
            var host = requested.Host?.Trim() ?? "";
            if (host.Length is < 1 or > 255 || host.Any(char.IsControl))
                throw new RoutePolicyViolationException("A valid proxy or gateway host is required.");
            if (requested.Port is < 1 or > 65_535)
                throw new RoutePolicyViolationException("A valid proxy or gateway port is required.");
        }

        if (requested.Type == ConnectionRouteType.ExternalProxyCommand)
        {
            var command = requested.Command?.Trim() ?? "";
            if (command.Length is < 1 or > 4_096 || command.Any(ch => ch is '\0' or '\r' or '\n'))
                throw new RoutePolicyViolationException("A valid single-line ProxyCommand is required.");
        }

        return new ResolvedConnectionRoute(
            routeId,
            requested.Type,
            requested.Host?.Trim() ?? "",
            requested.Port,
            requested.Username?.Trim() ?? "",
            requested.Password ?? "",
            requested.AuthMethod,
            requested.PrivateKeyPath?.Trim() ?? "",
            requested.Passphrase ?? "",
            requested.Command?.Trim() ?? "",
            requested.ProxyDns);
    }
}

/// <summary>
/// Correlation metadata shared by terminal and SFTP operations. It intentionally excludes all
/// credential values and can be forwarded to an approved audit sink later.
/// </summary>
public sealed record AuditContext(
    string CorrelationId,
    DateTimeOffset StartedAtUtc,
    string TargetHost,
    int TargetPort,
    string TargetUsername,
    string RouteId,
    ConnectionRouteType RouteType)
{
    public static AuditContext Create(
        SshConnectionInfo info,
        ResolvedConnectionRoute route) => new(
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            info.Host,
            info.Port,
            info.Username,
            route.Id,
            route.Type);
}
