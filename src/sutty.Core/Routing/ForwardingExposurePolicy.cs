using System.Net;

namespace sutty.Core.Routing;

/// <summary>Classifies forwarding bind addresses before a listener is opened.</summary>
public static class ForwardingExposurePolicy
{
    public static bool IsExternalBind(string? bindHost)
    {
        var host = bindHost?.Trim() ?? "";
        if (host.Length == 0)
            return true;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return false;

        var unwrapped = host.Length > 2 && host[0] == '[' && host[^1] == ']'
            ? host[1..^1]
            : host;
        return !IPAddress.TryParse(unwrapped, out var address) || !IPAddress.IsLoopback(address);
    }
}
