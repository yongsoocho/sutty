using System.Globalization;
using System.Net;

namespace sutty.Core.Security;

/// <summary>
/// Canonical SSH endpoint identity. The serialized form is always <c>[host]:port</c>,
/// including the default SSH port, so DNS names, IPv4, and IPv6 have one unambiguous key.
/// </summary>
public sealed record HostEndpointIdentity
{
    private HostEndpointIdentity(string host, int port)
    {
        Host = host;
        Port = port;
    }

    public string Host { get; }
    public int Port { get; }
    public string Value => $"[{Host}]:{Port}";

    public static HostEndpointIdentity Create(string host, int port)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), port, "SSH port must be between 1 and 65535.");

        return new HostEndpointIdentity(NormalizeHost(host), port);
    }

    public static HostEndpointIdentity Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var trimmed = value.Trim();
        if (!trimmed.StartsWith("[", StringComparison.Ordinal))
            throw new FormatException("Host identity must use the canonical [host]:port form.");

        var separator = trimmed.LastIndexOf("]:", StringComparison.Ordinal);
        if (separator <= 1 || separator + 2 >= trimmed.Length)
            throw new FormatException("Host identity must use the canonical [host]:port form.");

        var host = trimmed[1..separator];
        var portText = trimmed[(separator + 2)..];
        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            throw new FormatException("Host identity contains an invalid port.");

        var identity = Create(host, port);
        if (!string.Equals(identity.Value, trimmed, StringComparison.Ordinal))
            throw new FormatException("Host identity is not normalized.");

        return identity;
    }

    public override string ToString() => Value;

    private static string NormalizeHost(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        var candidate = host.Trim();
        if (candidate.Length >= 2 && candidate[0] == '[' && candidate[^1] == ']')
            candidate = candidate[1..^1];

        if (candidate.Length == 0)
            throw new ArgumentException("SSH host cannot be empty.", nameof(host));

        if (IPAddress.TryParse(candidate, out var address))
            return address.ToString().ToLowerInvariant();

        candidate = candidate.TrimEnd('.');
        if (candidate.Length == 0 ||
            candidate.IndexOfAny(['[', ']', ':', '/', '\\']) >= 0 ||
            candidate.Any(char.IsWhiteSpace) ||
            candidate.Any(char.IsControl))
        {
            throw new ArgumentException("SSH host contains invalid characters.", nameof(host));
        }

        try
        {
            // IDNA keeps equivalent Unicode and ASCII DNS names under the same trust entry.
            return new IdnMapping().GetAscii(candidate).ToLowerInvariant();
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException("SSH host is not a valid DNS name or IP address.", nameof(host), ex);
        }
    }
}
