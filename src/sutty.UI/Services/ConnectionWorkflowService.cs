using Microsoft.Data.Sqlite;
using sutty.Command;
using sutty.Core.Models;
using sutty.Core.Routing;
using sutty.Core.Security;
using sutty.UI.ViewModels;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace sutty.UI.Services;

public sealed record SavedHostConnectionDraft(
    SshConnectionInfo Draft,
    HostRouteProfile RouteProfile,
    bool SavedDataReadFailed);

/// <summary>
/// Loads credential-free Saved Host metadata and its optional encrypted-vault secret into a
/// short-lived connection draft. Dialogs, navigation, and connection lifetime stay with the
/// caller; persistence and secret lookup no longer live inside MainWindow code-behind.
/// </summary>
public sealed class ConnectionWorkflowService
{
    public SavedHostConnectionDraft LoadSavedHostDraft(HostInfoModel host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var alias = host.Alias;
        var hostname = host.Hostname;
        var username = host.Username;
        var port = host.Port;
        var authMethodName = host.AuthMethod;
        var privateKeyPath = host.PrivateKeyPath;
        var tags = host.Tags ?? [];
        var profileId = host.ProfileId;
        var credentialId = host.CredentialId;
        var groupName = host.GroupName;
        var environment = host.Environment;
        var favorite = host.IsPinned;
        var routeProfile = host.Route ?? new HostRouteProfile();
        var tunnelProfiles = host.Tunnels ?? [];
        CredentialSecret? credential = null;
        var savedDataReadFailed = false;

        if (!string.IsNullOrWhiteSpace(profileId))
        {
            try
            {
                if (HostProfileStore.GetById(profileId) is { } profile)
                {
                    alias = profile.DisplayName;
                    hostname = profile.Host;
                    username = profile.Username;
                    port = profile.Port;
                    authMethodName = profile.AuthMethod;
                    privateKeyPath = profile.PrivateKeyPath;
                    tags = profile.Tags;
                    credentialId = profile.CredentialId;
                    groupName = profile.GroupName;
                    environment = profile.Environment;
                    favorite = profile.IsFavorite;
                    routeProfile = profile.Route;
                    tunnelProfiles = profile.Tunnels;
                }

                if (!string.IsNullOrWhiteSpace(credentialId) &&
                    !LocalCredentialVault.Default.TryRead(credentialId, out credential))
                {
                    savedDataReadFailed = true;
                }
            }
            catch (Exception error) when (error is IOException or
                                          UnauthorizedAccessException or
                                          CryptographicException or
                                          Win32Exception or
                                          SqliteException or
                                          ArgumentException)
            {
                savedDataReadFailed = true;
                Debug.WriteLine($"Saved Host data load failed: {error.GetType().Name}");
            }
        }

        var authMethod = Enum.TryParse<SshAuthMethod>(authMethodName, true, out var parsed) &&
                         Enum.IsDefined(parsed)
            ? parsed
            : SshAuthMethod.Password;
        var draft = new SshConnectionInfo
        {
            Host = hostname,
            Port = port is >= 1 and <= 65_535 ? port : 22,
            DisplayName = alias,
            Username = username,
            AuthMethod = authMethod,
            PrivateKeyPath = authMethod == SshAuthMethod.PublicKey ? privateKeyPath : "",
            Password = authMethod is SshAuthMethod.Password or SshAuthMethod.KeyboardInteractive
                ? credential?.Password ?? ""
                : "",
            Passphrase = authMethod == SshAuthMethod.PublicKey
                ? credential?.PrivateKeyPassphrase ?? ""
                : "",
            Tags = [.. tags],
            SavedHostId = profileId,
            SaveProfile = !string.IsNullOrWhiteSpace(profileId),
            RememberCredential = credential is not null,
            CredentialId = credentialId,
            GroupName = groupName,
            Environment = environment,
            IsFavorite = favorite,
            Route = RestoreRoute(routeProfile, credential),
            RoutePolicy = new ConnectionRoutePolicy
            {
                DisableDirect = routeProfile.DisableDirect,
            },
            PortForwardings = tunnelProfiles.Select(RestoreTunnel).ToList(),
        };

        return new SavedHostConnectionDraft(draft, routeProfile, savedDataReadFailed);
    }

    private static ConnectionRoute RestoreRoute(
        HostRouteProfile profile,
        CredentialSecret? credential)
    {
        var type = Enum.TryParse<ConnectionRouteType>(profile.Type, out var parsedType) &&
                   Enum.IsDefined(parsedType)
            ? parsedType
            : ConnectionRouteType.Direct;
        var auth = Enum.TryParse<SshAuthMethod>(profile.AuthMethod, out var parsedAuth) &&
                   Enum.IsDefined(parsedAuth)
            ? parsedAuth
            : SshAuthMethod.Password;
        return new ConnectionRoute
        {
            Id = profile.Id,
            Type = type,
            Host = profile.Host,
            Port = profile.Port,
            Username = profile.Username,
            Password = credential?.RoutePassword ?? "",
            AuthMethod = auth,
            PrivateKeyPath = profile.PrivateKeyPath,
            Passphrase = credential?.RoutePrivateKeyPassphrase ?? "",
            Command = profile.Command,
            ProxyDns = profile.ProxyDns,
        };
    }

    private static SshPortForwardingRule RestoreTunnel(HostTunnelProfile profile)
    {
        var type = Enum.TryParse<SshPortForwardingType>(profile.Type, out var parsed) &&
                   Enum.IsDefined(parsed)
            ? parsed
            : SshPortForwardingType.Local;
        return new SshPortForwardingRule
        {
            Type = type,
            BindHost = profile.BindHost,
            BindPort = profile.BindPort,
            DestinationHost = profile.DestinationHost,
            DestinationPort = profile.DestinationPort,
        };
    }
}
