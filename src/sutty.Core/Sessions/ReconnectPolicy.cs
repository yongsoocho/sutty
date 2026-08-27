using sutty.Core.Models;
using sutty.Core.Routing;

namespace sutty.Core.Sessions;

public enum ReconnectDisposition
{
    Unavailable,
    OpenSavedHost,
    ReviewCredentials,
}

/// <summary>
/// Defines the safe boundary for a user-requested reconnect. A reconnect always creates a
/// new SSH session; it never replays commands or copies transient authentication values from
/// the previous session.
/// </summary>
public static class ReconnectPolicy
{
    public static ReconnectDisposition GetDisposition(
        SessionState state,
        SshConnectionInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        if (state is not SessionState.Failed and not SessionState.Disconnected)
            return ReconnectDisposition.Unavailable;

        return string.IsNullOrWhiteSpace(info.SavedHostId)
            ? ReconnectDisposition.ReviewCredentials
            : ReconnectDisposition.OpenSavedHost;
    }

    /// <summary>
    /// Copies only reusable, non-secret connection settings. UI prompt delegates and every
    /// authentication value are deliberately omitted so the next attempt performs fresh trust
    /// and credential decisions.
    /// </summary>
    public static SshConnectionInfo CreateCredentialFreeDraft(SshConnectionInfo source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var route = source.Route ?? new ConnectionRoute();
        var routePolicy = source.RoutePolicy ?? new ConnectionRoutePolicy();

        return new SshConnectionInfo
        {
            Host = source.Host,
            Port = source.Port,
            DisplayName = source.DisplayName,
            Username = source.Username,
            AuthMethod = source.AuthMethod,
            PrivateKeyPath = source.PrivateKeyPath,
            JumpHost = source.JumpHost,
            KeepAliveSeconds = source.KeepAliveSeconds,
            Compression = source.Compression,
            X11Forwarding = source.X11Forwarding,
            Tags = source.Tags is null ? [] : [.. source.Tags],
            PortForwardings = source.PortForwardings is null
                ? []
                : source.PortForwardings.Select(CloneForwarding).ToList(),
            Route = new ConnectionRoute
            {
                Id = route.Id,
                Type = route.Type,
                Host = route.Host,
                Port = route.Port,
                Username = route.Username,
                AuthMethod = route.AuthMethod,
                PrivateKeyPath = route.PrivateKeyPath,
                Command = route.Command,
                ProxyDns = route.ProxyDns,
            },
            RoutePolicy = new ConnectionRoutePolicy
            {
                DisableDirect = routePolicy.DisableDirect,
                RequiredRouteId = routePolicy.RequiredRouteId,
                AllowedRouteTypes = routePolicy.AllowedRouteTypes is null
                    ? []
                    : [.. routePolicy.AllowedRouteTypes],
            },
            SavedHostId = source.SavedHostId,
            SaveProfile = false,
            RememberCredential = false,
            CredentialId = source.CredentialId,
            GroupName = source.GroupName,
            Environment = source.Environment,
            IsFavorite = source.IsFavorite,
        };
    }

    private static SshPortForwardingRule CloneForwarding(SshPortForwardingRule source) => new()
    {
        Type = source.Type,
        BindHost = source.BindHost,
        BindPort = source.BindPort,
        DestinationHost = source.DestinationHost,
        DestinationPort = source.DestinationPort,
    };
}
