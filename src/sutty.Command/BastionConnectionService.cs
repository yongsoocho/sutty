using System.ComponentModel;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using sutty.Core.Models;
using sutty.Core.Routing;
using sutty.Core.Security;

namespace sutty.Command;

public sealed record BastionProfileChoice(
    string ProfileId, string DisplayName, string Host, int Port, string Username)
{
    public DateTimeOffset UpdatedAtUtc { get; init; }

    public override string ToString()
    {
        var address = Host.Contains(':') && !Host.StartsWith('[') ? $"[{Host}]" : Host;
        return $"{DisplayName} ({Username}@{address}:{Port})";
    }
}

/// <summary>A transient, independent route draft; it does not own the source profile or vault record.</summary>
public sealed class BastionConnectionDraft
{
    [JsonIgnore]
    public ConnectionRoute Route { get; init; } = new();

    public bool CredentialUnavailable { get; init; }
}

public sealed class BastionConnectionService(
    Func<string, HostProfile?> readProfile,
    Func<string, CredentialSecret?> readCredential)
{
    public static void PrepareOneTimeConnection(SshConnectionInfo connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Route.Type != ConnectionRouteType.SshJump)
            throw new InvalidOperationException("A one-time Bastion connection requires SSH Jump.");

        connection.IsOneTimeBastion = true;
        connection.SaveProfile = false;
        connection.RememberCredential = false;
        connection.SavedHostId = null;
        connection.CredentialId = null;
        connection.RoutePolicy.DisableDirect = true;
    }

    private readonly Func<string, HostProfile?> _readProfile =
        readProfile ?? throw new ArgumentNullException(nameof(readProfile));
    private readonly Func<string, CredentialSecret?> _readCredential =
        readCredential ?? throw new ArgumentNullException(nameof(readCredential));

    public static IReadOnlyList<BastionProfileChoice> GetChoices(IEnumerable<HostProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        return profiles.Where(IsEligible)
            .Select(profile => new BastionProfileChoice(
                profile.Id,
                string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.Host.Trim() : profile.DisplayName.Trim(),
                profile.Host.Trim(), profile.Port, profile.Username.Trim())
            {
                UpdatedAtUtc = profile.UpdatedAtUtc,
            })
            .OrderBy(choice => choice.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public BastionConnectionDraft Load(string profileId)
    {
        if (!IsValidId(profileId))
            throw new ArgumentException("A valid saved-host id is required.", nameof(profileId));

        // Re-read on explicit selection so a stale picker cannot bypass a changed route policy.
        var profile = _readProfile(profileId);
        if (!IsEligible(profile) || !string.Equals(profile!.Id, profileId, StringComparison.Ordinal))
            throw new InvalidOperationException("The saved Bastion is unavailable or no longer supports a direct SSH connection.");

        var auth = ParseSupportedAuth(profile.AuthMethod)!.Value;
        CredentialSecret? credential = null;
        var unavailable = false;
        if (auth != SshAuthMethod.Agent && !string.IsNullOrWhiteSpace(profile.CredentialId))
        {
            try
            {
                credential = _readCredential(profile.CredentialId);
                unavailable = credential is null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                          CryptographicException or Win32Exception or ArgumentException or
                                          ObjectDisposedException or SecurityException)
            {
                unavailable = true;
            }
        }

        return new BastionConnectionDraft
        {
            CredentialUnavailable = unavailable,
            Route = new ConnectionRoute
            {
                Id = "sshjump",
                Type = ConnectionRouteType.SshJump,
                Host = profile.Host.Trim(),
                Port = profile.Port,
                Username = profile.Username.Trim(),
                AuthMethod = auth,
                Password = auth == SshAuthMethod.Password ? credential?.Password ?? "" : "",
                PrivateKeyPath = auth == SshAuthMethod.PublicKey ? profile.PrivateKeyPath.Trim() : "",
                Passphrase = auth == SshAuthMethod.PublicKey ? credential?.PrivateKeyPassphrase ?? "" : "",
                ProxyDns = true,
            },
        };
    }

    private static bool IsEligible(HostProfile? profile)
    {
        if (profile is null || profile.IsExternalCommand || !string.IsNullOrWhiteSpace(profile.LaunchCommand) ||
            !IsValidId(profile.Id) || !IsValidText(profile.Host, 255) || profile.Host.Any(char.IsWhiteSpace) ||
            profile.Port is < 1 or > 65_535 || !IsValidText(profile.Username, 128) ||
            (!string.IsNullOrWhiteSpace(profile.DisplayName) && !IsValidText(profile.DisplayName, 128)) ||
            profile.Route is not { State: SavedRouteState.Valid, DisableDirect: false } route ||
            route.LegacyDisableDirect == true ||
            !string.Equals(route.Type?.Trim(), "Direct", StringComparison.OrdinalIgnoreCase))
            return false;

        var auth = ParseSupportedAuth(profile.AuthMethod);
        return auth is not null &&
               (auth != SshAuthMethod.PublicKey || IsValidText(profile.PrivateKeyPath, 2_048));
    }

    private static bool IsValidId(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool IsValidText(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maxLength && !value.Any(char.IsControl);

    private static SshAuthMethod? ParseSupportedAuth(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "password" => SshAuthMethod.Password,
        "publickey" => SshAuthMethod.PublicKey,
        "agent" => SshAuthMethod.Agent,
        _ => null,
    };
}
