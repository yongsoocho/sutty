using sutty.Core.Models;
using sutty.Core.Routing;
using System;
using System.Globalization;

namespace sutty.UI.Helpers;

/// <summary>Formats public route endpoints without authentication values or transport bridges.</summary>
internal static class SshRouteDisplay
{
    public static string FormatEndpoint(string host, int port)
    {
        var name = host?.Trim() ?? "";
        if (name.Contains(':') && !(name.StartsWith('[') && name.EndsWith(']')))
            name = $"[{name}]";
        return $"{name}:{port.ToString(CultureInfo.InvariantCulture)}";
    }

    public static string FormatPath(SshConnectionInfo info, string localLabel)
    {
        ArgumentNullException.ThrowIfNull(info);
        var target = FormatEndpoint(info.Host, info.Port);
        return info.Route is { Type: ConnectionRouteType.SshJump } route
            ? $"{localLabel} \u2192 {FormatEndpoint(route.Host, route.Port)} \u2192 {target}"
            : $"{localLabel} \u2192 {target}";
    }
}
