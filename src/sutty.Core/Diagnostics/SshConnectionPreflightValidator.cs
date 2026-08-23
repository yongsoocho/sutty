using sutty.Core.Models;
using sutty.Core.Routing;
using sutty.Core.Security;

namespace sutty.Core.Diagnostics;

/// <summary>
/// Validates the non-network prerequisites for an SSH connection. Successful validation
/// is intentionally silent, and failures preserve their typed exception contract so that
/// Connection Doctor can classify them without inspecting messages or secret values.
/// </summary>
public static class SshConnectionPreflightValidator
{
    public const int MaximumUsernameLength = 128;

    public static void Validate(SshConnectionInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        _ = HostEndpointIdentity.Create(info.Host, info.Port);

        var username = info.Username ?? "";
        if (string.IsNullOrWhiteSpace(username) ||
            username.Length > MaximumUsernameLength ||
            username.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"The SSH username must contain 1 to {MaximumUsernameLength} non-control characters.",
                nameof(info.Username));
        }

        if (!Enum.IsDefined(info.AuthMethod))
        {
            throw new ArgumentOutOfRangeException(
                nameof(info.AuthMethod),
                "The SSH authentication method is not supported.");
        }

        if (info.Route is { } route && !Enum.IsDefined(route.Type))
        {
            throw new ArgumentOutOfRangeException(
                nameof(info.Route),
                "The SSH connection route type is not supported.");
        }

        if (info.AuthMethod == SshAuthMethod.PublicKey)
        {
            if (string.IsNullOrWhiteSpace(info.PrivateKeyPath))
            {
                throw new ArgumentException(
                    "A private-key file must be selected for public-key authentication.",
                    nameof(info.PrivateKeyPath));
            }

            if (!File.Exists(info.PrivateKeyPath))
            {
                // Do not attach the sensitive path to Message or FileName.
                throw new FileNotFoundException("The selected private-key file does not exist.");
            }
        }

        // Resolution validates the route and active policy. Discard the resolved route so
        // transient proxy credentials are never returned from this preflight API.
        _ = RouteResolver.Resolve(info.Route, info.RoutePolicy);
    }
}
