using System.ComponentModel;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using sutty.Command;
using sutty.Core.Models;
using sutty.Core.Routing;
using sutty.Core.Security;
using sutty.UI.Helpers;

internal static class BastionConnectionTests
{
    public static void Run(Action<bool, string> assert, string scratch)
    {
        VerifyEligibility(assert);
        VerifyCredentialMapping(assert);
        VerifyLiveProfileLookup(assert, scratch);
        VerifyRouteDisplay(assert);
        VerifyOneTimeConnection(assert);
    }

    private static void VerifyOneTimeConnection(Action<bool, string> assert)
    {
        var connection = new SshConnectionInfo
        {
            Host = "destination.invalid", Username = "target-user", Password = "synthetic-target",
            SavedHostId = "original-host", CredentialId = "original-vault",
            SaveProfile = true, RememberCredential = true,
            Route = new ConnectionRoute { Type = ConnectionRouteType.SshJump,
                Host = "gateway.invalid", Password = "synthetic-gateway" },
        };
        BastionConnectionService.PrepareOneTimeConnection(connection);
        assert(connection.IsOneTimeBastion && !connection.SaveProfile && !connection.RememberCredential &&
               connection.SavedHostId is null && connection.CredentialId is null,
            "one-time Bastion cannot update the original profile or credential ownership");
        assert(connection.RoutePolicy.DisableDirect && connection.Host == "destination.invalid" &&
               connection.Password == "synthetic-target" && connection.Route.Host == "gateway.invalid" &&
               connection.Route.Password == "synthetic-gateway",
            "one-time Bastion preserves current target/gateway authentication and requires its route");
        var direct = new SshConnectionInfo { SavedHostId = "original-host", SaveProfile = true };
        assert(Rejects(() => BastionConnectionService.PrepareOneTimeConnection(direct)) &&
               direct.SavedHostId == "original-host" && direct.SaveProfile && !direct.IsOneTimeBastion,
            "one-time Bastion rejects Direct before changing ownership or persistence");
    }

    private static void VerifyEligibility(Action<bool, string> assert)
    {
        var accepted = new[] { Profile(), Profile(auth: "PublicKey"), Profile(auth: "Agent") };
        assert(BastionConnectionService.GetChoices(accepted).Count == 3,
            "Bastion picker accepts direct password, public-key, and agent profiles");
        var rejected = new[]
        {
            Profile(launchKind: "OpenSsh"), Profile(launchCommand: "ssh other.example"),
            Profile(route: new HostRouteProfile { Type = "SshJump", Host = "jump.example", Port = 22 }),
            Profile(route: new HostRouteProfile { Type = "Socks5" }),
            Profile(route: new HostRouteProfile { Type = "Unsupported" }),
            Profile(route: new HostRouteProfile { State = SavedRouteState.Corrupt }),
            Profile(route: new HostRouteProfile { State = SavedRouteState.Unsupported }),
            Profile(route: new HostRouteProfile { DisableDirect = true }),
            Profile(route: new HostRouteProfile { LegacyDisableDirect = true }),
            Profile(auth: "KeyboardInteractive"), Profile(auth: "0"), Profile(auth: "Unknown"),
            Profile(host: ""), Profile(host: "two hosts"), Profile(host: "bad\nhost"),
            Profile(host: new string('h', 256)), Profile(port: 0), Profile(port: 65_536),
            Profile(username: ""), Profile(username: "bad\nuser"), Profile(username: new string('u', 129)),
            Profile(id: ""), Profile(id: "bad/id"), Profile(id: new string('i', 129)),
            Profile(displayName: "bad\nname"), Profile(displayName: new string('n', 129)),
            Profile(auth: "PublicKey", keyPath: ""), Profile(auth: "PublicKey", keyPath: "bad\nkey"),
            Profile(auth: "PublicKey", keyPath: new string('k', 2_049)),
        };
        assert(BastionConnectionService.GetChoices(rejected).Count == 0,
            "Bastion picker excludes malformed, external, chained, unsupported, and policy-blocked sources");
        foreach (var profile in rejected)
        {
            var rejectedCredentialReads = 0;
            var service = new BastionConnectionService(_ => profile, _ =>
            {
                rejectedCredentialReads++;
                return new CredentialSecret("must-not-be-read");
            });
            assert(Rejects(() => service.Load("bastion-profile")) && rejectedCredentialReads == 0,
                "Bastion load revalidates every picker eligibility rule before reading credentials");
        }

        var ipv6 = BastionConnectionService.GetChoices([Profile(host: "::1", displayName: "Central")]).Single();
        assert(ipv6.ToString() == "Central (operator@[::1]:2222)",
            "Bastion choice exposes only its label and unambiguous SSH endpoint");
        var originalVersion = DateTimeOffset.UnixEpoch;
        var changedVersion = originalVersion.AddMinutes(1);
        var originalChoice = BastionConnectionService.GetChoices([Profile(updatedAtUtc: originalVersion)]).Single();
        var changedChoice = BastionConnectionService.GetChoices([Profile(updatedAtUtc: changedVersion)]).Single();
        assert(originalChoice.UpdatedAtUtc == originalVersion && changedChoice.UpdatedAtUtc == changedVersion &&
               originalChoice != changedChoice && originalChoice.ToString() == changedChoice.ToString(),
            "Bastion choices expose source changes even when labels and public endpoints remain identical");
    }

    private static void VerifyCredentialMapping(Action<bool, string> assert)
    {
        var secret = new CredentialSecret("primary-password", "primary-key-passphrase",
            "unrelated-route-password", "unrelated-route-passphrase");
        var credentialReads = 0;
        var source = Profile(route: new HostRouteProfile
        {
            Id = "source-route-id", Host = "unrelated.example", Port = 2022, Command = "unrelated-command",
        });
        var service = new BastionConnectionService(_ => source, id =>
        {
            assert(id == "source-vault-id", "Bastion reads only the selected profile's credential reference");
            credentialReads++;
            return secret;
        });
        var password = service.Load(source.Id);
        assert(password.Route.Type == ConnectionRouteType.SshJump && password.Route.Host == source.Host &&
               password.Route.Port == source.Port && password.Route.Username == source.Username &&
               password.Route.Password == secret.Password && password.Route.Passphrase == "" &&
               password.Route.PrivateKeyPath == "" && !password.CredentialUnavailable,
            "Bastion password snapshot maps primary endpoint and password without unrelated secrets");
        assert(password.Route.Id != source.Id && password.Route.Id != source.Route.Id &&
               password.Route.Command == "" && password.Route.ProxyDns,
            "Bastion snapshot never adopts source ownership, proxy commands, or tunnel configuration");
        assert(JsonSerializer.Serialize(password) == "{\"CredentialUnavailable\":false}" &&
               !password.ToString()!.Contains("primary", StringComparison.Ordinal),
            "Bastion draft serialization and default display never expose transient credentials");

        var next = service.Load(source.Id);
        password.Route.Host = "edited.example";
        password.Route.Password = "edited-password";
        assert(next.Route.Host == source.Host && next.Route.Password == secret.Password &&
               source.Route.Host == "unrelated.example" && source.Tunnels.Single().BindPort == 15432 &&
               credentialReads == 2,
            "Bastion selections own independent drafts and never mutate the source profile or vault value");

        source = Profile(auth: "PublicKey");
        var key = service.Load(source.Id);
        assert(key.Route.AuthMethod == SshAuthMethod.PublicKey && key.Route.PrivateKeyPath == source.PrivateKeyPath &&
               key.Route.Password == "" && key.Route.Passphrase == secret.PrivateKeyPassphrase,
            "Bastion key snapshot copies only the primary key path and key passphrase");
        source = Profile(auth: "Agent");
        var readsBeforeAgent = credentialReads;
        var agent = service.Load(source.Id);
        assert(agent.Route.AuthMethod == SshAuthMethod.Agent && agent.Route.Password == "" &&
               agent.Route.Passphrase == "" && agent.Route.PrivateKeyPath == "" &&
               !agent.CredentialUnavailable && credentialReads == readsBeforeAgent,
            "Bastion agent authentication neither reads nor copies saved secrets");

        source = Profile(credentialId: null);
        assert(!service.Load(source.Id).CredentialUnavailable && credentialReads == readsBeforeAgent,
            "Bastion without remembered credentials remains an editable draft without a vault read");
        var missing = new BastionConnectionService(_ => Profile(), _ => null).Load("bastion-profile");
        assert(missing.CredentialUnavailable && missing.Route.Password == "",
            "missing Bastion vault records produce an actionable unavailable status");
        foreach (var error in new Exception[]
        {
            new IOException(), new UnauthorizedAccessException(), new CryptographicException(),
            new Win32Exception(), new ArgumentException(), new ObjectDisposedException("vault"), new SecurityException(),
        })
        {
            var unavailable = new BastionConnectionService(_ => Profile(), _ => throw error).Load("bastion-profile");
            assert(unavailable.CredentialUnavailable && unavailable.Route.Password == "" && unavailable.Route.Passphrase == "",
                "unreadable Bastion vault data preserves the endpoint and requires credential input");
        }

        var resolved = RouteResolver.Resolve(next.Route, new ConnectionRoutePolicy
        {
            DisableDirect = true, AllowedRouteTypes = [ConnectionRouteType.SshJump],
        });
        assert(resolved.Type == ConnectionRouteType.SshJump && resolved.Host == "central.example",
            "Bastion snapshot satisfies strict SSH Jump routing without a direct fallback");
    }

    private static void VerifyLiveProfileLookup(Action<bool, string> assert, string scratch)
    {
        var previousDatabase = Db.PathOverride;
        Db.PathOverride = Path.Combine(scratch, "bastion-tests.db");
        try
        {
            var source = HostProfileStore.Save(new HostProfileDraft
            {
                DisplayName = "Central", Host = "old-central.example", Username = "operator", AuthMethod = "Agent",
            });
            var choice = BastionConnectionService.GetChoices(HostProfileStore.GetAll()).Single();
            var service = new BastionConnectionService(HostProfileStore.GetById,
                _ => throw new InvalidOperationException("Agent selection must not read credentials."));
            HostProfileStore.Save(new HostProfileDraft
            {
                DisplayName = "Central", Host = "new-central.example", Username = "operator", AuthMethod = "Agent",
            }, source.Id);
            assert(service.Load(choice.ProfileId).Route.Host == "new-central.example",
                "Bastion selection reads the current saved endpoint instead of stale picker metadata");
            HostProfileStore.Save(new HostProfileDraft
            {
                DisplayName = "Central", Host = "new-central.example", Username = "operator", AuthMethod = "Agent",
                Route = new HostRouteProfile { DisableDirect = true },
            }, source.Id);
            assert(Rejects(() => service.Load(choice.ProfileId)),
                "a source policy change blocks a previously offered Bastion selection");
            HostProfileStore.Delete(source.Id);
            assert(Rejects(() => service.Load(choice.ProfileId)),
                "a deleted Bastion never falls back to stale metadata or direct target routing");
            assert(Rejects(() => new BastionConnectionService(_ => Profile(id: "other-profile"), _ => null)
                    .Load("bastion-profile")),
                "a mismatched source identifier cannot substitute a different Bastion");
        }
        finally
        {
            Db.PathOverride = previousDatabase;
        }
    }

    private static void VerifyRouteDisplay(Action<bool, string> assert)
    {
        assert(SshRouteDisplay.FormatEndpoint(" central.example ", 2200) == "central.example:2200" &&
               SshRouteDisplay.FormatEndpoint("192.0.2.1", 22) == "192.0.2.1:22",
            "SSH route display includes an explicit port for DNS and IPv4 endpoints");
        assert(SshRouteDisplay.FormatEndpoint("2001:db8::10", 2222) == "[2001:db8::10]:2222" &&
               SshRouteDisplay.FormatEndpoint("[2001:db8::10]", 22) == "[2001:db8::10]:22" &&
               SshRouteDisplay.FormatEndpoint("fe80::1%12", 2022) == "[fe80::1%12]:2022",
            "SSH route display brackets raw IPv6 once and preserves zone identifiers");
        var connection = new SshConnectionInfo
        {
            Host = "desktop.internal", Port = 2222, DisplayName = "secret-display-name",
            Username = "secret-target-user", Password = "secret-target-password",
            Passphrase = "secret-target-passphrase", PrivateKeyPath = "secret-target-keypath",
            CredentialId = "secret-target-vault-id", SavedHostId = "secret-target-profile-id",
            JumpHost = "unrelated-legacy-jump",
            Route = new ConnectionRoute
            {
                Type = ConnectionRouteType.SshJump, Id = "secret-route-id", Host = "2001:db8::1", Port = 2200,
                Username = "secret-jump-user", Password = "secret-jump-password",
                Passphrase = "secret-jump-passphrase", PrivateKeyPath = "secret-jump-keypath",
                Command = "secret-proxy-command",
            },
        };
        var path = SshRouteDisplay.FormatPath(connection, "This PC");
        assert(path == "This PC \u2192 [2001:db8::1]:2200 \u2192 desktop.internal:2222" &&
               !path.Contains("secret", StringComparison.Ordinal) &&
               !path.Contains("unrelated", StringComparison.Ordinal),
            "SSH Jump path exposes only local, gateway, and target endpoints without authentication or ownership values");
        connection.Route.Type = ConnectionRouteType.Direct;
        assert(SshRouteDisplay.FormatPath(connection, "This PC") == "This PC \u2192 desktop.internal:2222",
            "direct route display excludes stale gateway endpoint data");
    }

    private static bool Rejects(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return true; }
        catch (ArgumentException) { return true; }
        return false;
    }

    private static HostProfile Profile(string id = "bastion-profile", string host = "central.example", int port = 2222,
        string username = "operator", string auth = "Password", string keyPath = @"C:\keys\central.key",
        string displayName = "Central", string launchKind = "SuttySsh", string launchCommand = "",
        string? credentialId = "source-vault-id", HostRouteProfile? route = null,
        DateTimeOffset updatedAtUtc = default) => new()
    {
        Id = id, DisplayName = displayName, Host = host, Port = port, Username = username,
        AuthMethod = auth, PrivateKeyPath = keyPath, LaunchKind = launchKind, LaunchCommand = launchCommand,
        CredentialId = credentialId, Route = route ?? new HostRouteProfile(),
        UpdatedAtUtc = updatedAtUtc,
        Tunnels = [new HostTunnelProfile { BindPort = 15432, DestinationHost = "db.internal", DestinationPort = 5432 }],
    };
}
