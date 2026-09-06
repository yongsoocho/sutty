using sutty.Command;

var scratch = Path.Combine(Path.GetTempPath(), $"sutty-command-self-test-{Guid.NewGuid():N}");
Directory.CreateDirectory(scratch);
Db.PathOverride = Path.Combine(scratch, "sutty.db");

try
{
    Assert(SuttyLaunchRequestParser.Parse("").Action == SuttyLaunchAction.Default,
        "empty launch arguments open the default workspace");
    Assert(SuttyLaunchRequestParser.Parse("--version").Action == SuttyLaunchAction.ShowVersion,
        "version launch argument");
    var launchById = SuttyLaunchRequestParser.Parse("--host saved-host-42");
    Assert(launchById.Action == SuttyLaunchAction.OpenSavedHost &&
           launchById.SavedHostReference == "saved-host-42",
        "command line opens a Saved Host id");
    var launchByName = SuttyLaunchRequestParser.Parse("--host=\"Production API\"");
    Assert(launchByName.Action == SuttyLaunchAction.OpenSavedHost &&
           launchByName.SavedHostReference == "Production API",
        "command line accepts a quoted exact Saved Host name");
    Assert(SuttyLaunchRequestParser.Parse("--host server --password secret").Action ==
           SuttyLaunchAction.Invalid,
        "command line rejects credential arguments");
    Assert(SuttyLaunchRequestParser.Parse("--host \"unfinished").Action ==
           SuttyLaunchAction.Invalid,
        "command line rejects unterminated quotes");

    Assert(!File.Exists(Db.PathOverride), "no bundled local database");
    Assert(HostHistoryStore.GetRecent().Count == 0, "new history store has no bundled rows");

    var commandChanges = 0;
    EventHandler commandChanged = (_, _) => commandChanges++;
    CommandStore.Changed += commandChanged;
    var commandTemplate = CommandStore.Add("Health", "uptime");
    Assert(commandChanges == 1 && CommandStore.GetAll().Single().Id == commandTemplate.Id,
        "adding a command notifies all command views");
    CommandStore.IncrementUsage(commandTemplate.Id);
    Assert(commandChanges == 2 && CommandStore.GetAll().Single().UsageCount == 1,
        "command usage reorder notifies all command views");
    CommandStore.Delete(commandTemplate.Id);
    Assert(commandChanges == 3 && CommandStore.GetAll().Count == 0,
        "deleting a command notifies all command views");
    CommandStore.Changed -= commandChanged;

    HostHistoryStore.SetPinned(
        "legacy.example",
        "Legacy host",
        true,
        "operator",
        2222,
        "PublicKey",
        @"C:\keys\legacy.key",
        ["legacy"]);

    SharingTests.SeedPreviousHostSchema();
    HostProfileStore.EnsureInitialized();
    SharingTests.VerifyPreviousHostMigration(Assert);
    var migrated = HostProfileStore.GetAll().Single();
    Assert(migrated.Host == "legacy.example", "legacy saved host migration");
    Assert(migrated.IsFavorite, "legacy favorite migration");
    using (var connection = Db.Open())
    using (var migration = connection.CreateCommand())
    {
        migration.CommandText = "SELECT COUNT(*) FROM storage_migrations";
        Assert(Convert.ToInt32(migration.ExecuteScalar()) == 1, "legacy migration is versioned");
    }

    var created = HostProfileStore.Save(new HostProfileDraft
    {
        DisplayName = "Production API",
        Host = "api.example",
        Port = 22,
        Username = "admin",
        AuthMethod = "Password",
        Tags = ["api", "production"],
        GroupName = "Platform",
        Environment = HostEnvironments.Production,
        IsFavorite = true,
        CredentialId = "opaque-reference",
        Route = new HostRouteProfile
        {
            Id = "corp-jump",
            Type = "SshJump",
            Host = "jump.example",
            Port = 22,
            Username = "gateway",
            AuthMethod = "Agent",
            ProxyDns = true,
            DisableDirect = true,
        },
        Tunnels =
        [
            new HostTunnelProfile
            {
                Type = "Local",
                BindHost = "127.0.0.1",
                BindPort = 15432,
                DestinationHost = "db.internal",
                DestinationPort = 5432,
            },
        ],
    });
    Assert(created.Id.Length == 32, "saved profile id");
    Assert(created.CredentialId == "opaque-reference", "opaque credential reference");
    Assert(HostProfileStore.GetById(created.Id)?.DisplayName == "Production API",
        "saved profile reload");
    Assert(created.Route.Type == "SshJump" && created.Route.Host == "jump.example",
        "saved profile route reload");
    Assert(created.Route.State == SavedRouteState.Valid && created.Route.CanConnect,
        "valid saved route is connectable");
    Assert(created.Route.DisableDirect, "saved profile strict route policy reload");
    Assert(created.Tunnels.Single().DestinationPort == 5432,
        "saved profile tunnel reload");

    using (var connection = Db.Open())
    using (var legacyRoute = connection.CreateCommand())
    {
        legacyRoute.CommandText = "UPDATE host_profiles SET route_json = $route WHERE id = $id";
        legacyRoute.Parameters.AddWithValue("$id", created.Id);
        legacyRoute.Parameters.AddWithValue("$route", """
            {"id":"legacy-proxy","type":"Socks5","host":"proxy.example","port":1080,"username":"proxy-user","authMethod":"Password","proxyDns":true,"enterpriseMode":true}
            """);
        Assert(legacyRoute.ExecuteNonQuery() == 1, "legacy strict route fixture update");
    }

    var migratedStrictRoute = HostProfileStore.GetById(created.Id)!;
    Assert(migratedStrictRoute.Route.Type == "Socks5" &&
           migratedStrictRoute.Route.DisableDirect,
        "legacy route flag migrates to strict route policy");

    var updated = HostProfileStore.Save(new HostProfileDraft
    {
        DisplayName = "Production API updated",
        Host = "api.example",
        Port = 2202,
        Username = "admin",
        AuthMethod = "PublicKey",
        PrivateKeyPath = @"C:\keys\api.key",
        Tags = ["api"],
        GroupName = "Platform",
        Environment = HostEnvironments.Production,
        CredentialId = "opaque-reference-2",
        Route = migratedStrictRoute.Route,
        Tunnels = created.Tunnels,
    }, created.Id);
    Assert(updated.Port == 2202 && updated.AuthMethod == "PublicKey", "saved profile update");
    using (var connection = Db.Open())
    using (var storedRoute = connection.CreateCommand())
    {
        storedRoute.CommandText = "SELECT route_json FROM host_profiles WHERE id = $id";
        storedRoute.Parameters.AddWithValue("$id", created.Id);
        var routeJson = Convert.ToString(storedRoute.ExecuteScalar()) ?? "";
        Assert(routeJson.Contains("\"disableDirect\":true", StringComparison.Ordinal),
            "strict route policy uses the current persisted field");
        Assert(!routeJson.Contains("\"enterpriseMode\"", StringComparison.Ordinal),
            "legacy route field is not written back");
        Assert(!routeJson.Contains("\"state\"", StringComparison.Ordinal) &&
               !routeJson.Contains("\"errorCode\"", StringComparison.Ordinal) &&
               !routeJson.Contains("\"sourceType\"", StringComparison.Ordinal),
            "derived route diagnostics are not persisted");
    }

    using (var connection = Db.Open())
    using (var unsupportedRoute = connection.CreateCommand())
    {
        unsupportedRoute.CommandText = "UPDATE host_profiles SET route_json = $route WHERE id = $id";
        unsupportedRoute.Parameters.AddWithValue("$id", created.Id);
        unsupportedRoute.Parameters.AddWithValue("$route", """
            {"id":"retired-route","type":"RetiredGateway","host":"gateway.example","port":22}
            """);
        Assert(unsupportedRoute.ExecuteNonQuery() == 1, "unsupported saved route fixture update");
    }

    var blockedUnsupportedRoute = HostProfileStore.GetById(created.Id)!;
    Assert(blockedUnsupportedRoute.Route.Type == "Direct" &&
           blockedUnsupportedRoute.Route.DisableDirect &&
           blockedUnsupportedRoute.Route.State == SavedRouteState.Unsupported &&
           blockedUnsupportedRoute.Route.ErrorCode == SavedRouteErrorCodes.Unsupported &&
           blockedUnsupportedRoute.Route.SourceType == "RetiredGateway" &&
           !blockedUnsupportedRoute.Route.CanConnect,
        "unsupported saved route fails closed with a recoverable diagnostic");
    AssertThrows<ArgumentException>(() => HostProfileStore.Save(new HostProfileDraft
    {
        DisplayName = updated.DisplayName,
        Host = updated.Host,
        Port = updated.Port,
        Username = updated.Username,
        AuthMethod = updated.AuthMethod,
        PrivateKeyPath = updated.PrivateKeyPath,
        Tags = updated.Tags,
        GroupName = updated.GroupName,
        Environment = updated.Environment,
        CredentialId = updated.CredentialId,
        Route = blockedUnsupportedRoute.Route,
        Tunnels = updated.Tunnels,
    }, updated.Id), "an invalid saved route cannot be written back without repair");

    using (var connection = Db.Open())
    using (var corruptRoute = connection.CreateCommand())
    {
        corruptRoute.CommandText = "UPDATE host_profiles SET route_json = $route WHERE id = $id";
        corruptRoute.Parameters.AddWithValue("$id", created.Id);
        corruptRoute.Parameters.AddWithValue("$route", "{not-json");
        Assert(corruptRoute.ExecuteNonQuery() == 1, "corrupt saved route fixture update");
    }

    var blockedCorruptRoute = HostProfileStore.GetById(created.Id)!;
    Assert(blockedCorruptRoute.Route.Type == "Direct" &&
           blockedCorruptRoute.Route.DisableDirect &&
           blockedCorruptRoute.Route.State == SavedRouteState.Corrupt &&
           blockedCorruptRoute.Route.ErrorCode == SavedRouteErrorCodes.Corrupt &&
           !blockedCorruptRoute.Route.CanConnect,
        "corrupt saved route fails closed with a distinct diagnostic");

    HostProfileStore.SetFavorite(created.Id, true);
    HostProfileStore.MarkConnected(created.Id);
    var marked = HostProfileStore.GetById(created.Id)!;
    Assert(marked.IsFavorite && marked.LastConnectedAtUtc is not null, "favorite and last connected");

    var openSsh = HostProfileImportService.ParseOpenSsh("""
        Host app-prod
          HostName 10.10.0.12
          User deploy
          Port 2222
          IdentityFile ~/.ssh/id_ed25519
          ProxyJump gateway@jump.example:2200
          LocalForward 127.0.0.1:15432 db.internal:5432
          DynamicForward 1080

        Host *.internal
          User ignored
        """);
    var importedOpenSsh = openSsh.Profiles.Single();
    Assert(importedOpenSsh.Host == "10.10.0.12" && importedOpenSsh.Port == 2222,
        "OpenSSH host import");
    Assert(importedOpenSsh.Route.Type == "SshJump" && importedOpenSsh.Route.Port == 2200,
        "OpenSSH ProxyJump import");
    Assert(importedOpenSsh.Tunnels?.Count() == 2,
        "OpenSSH local and dynamic forwarding import");
    Assert(openSsh.Warnings.Any(item => item.Contains("Wildcard", StringComparison.Ordinal)),
        "OpenSSH wildcard hosts are reported and skipped");

    var openSshDefaults = HostProfileImportService.ParseOpenSsh("""
        Host *
          User shared-user
          Port 2200
        Host * !blocked
          IdentityFile C:\Keys\shared.ppk
        Host prod-api
          HostName prod.example
        Host blocked
          HostName blocked.example
          PreferredAuthentications password
        """);
    var defaultedProfile = openSshDefaults.Profiles.Single(item => item.DisplayName == "prod-api");
    var negatedProfile = openSshDefaults.Profiles.Single(item => item.DisplayName == "blocked");
    Assert(defaultedProfile.Username == "shared-user" && defaultedProfile.Port == 2200 &&
           defaultedProfile.PrivateKeyPath == @"C:\Keys\shared.ppk",
        "OpenSSH wildcard defaults and Windows key paths are preserved");
    Assert(negatedProfile.AuthMethod == "Password" && negatedProfile.PrivateKeyPath.Length == 0,
        "OpenSSH negated wildcard blocks are respected");

    var putty = HostProfileImportService.ParsePuttySession("Legacy API",
        new Dictionary<string, object?>
        {
            ["HostName"] = "legacy.example",
            ["PortNumber"] = 2201,
            ["UserName"] = "operator",
            ["PublicKeyFile"] = @"C:\keys\legacy.ppk",
            ["ProxyMethod"] = 2,
            ["ProxyHost"] = "proxy.example",
            ["ProxyPort"] = 1080,
            ["PortForwardings"] = "L15432=db.internal:5432,D1080=",
        });
    Assert(putty?.Route.Type == "Socks5" && putty.Tunnels?.Count() == 2,
        "Windows saved-session route and forwarding import");

    var secureCrt = HostProfileImportService.ParseSecureCrtSession("Database", """
        S:"Hostname"=db.example
        D:"[SSH2] Port"=00000016
        S:"Username"=dba
        S:"Identity Filename V2"=C:\keys\db.pem
        """);
    Assert(secureCrt?.Host == "db.example" && secureCrt.Port == 22 &&
           secureCrt.AuthMethod == "PublicKey",
        "INI saved-session host and key import");

    var siteManagerXml = HostProfileImportService.ParseSftpSiteManagerXml("""
        <?xml version="1.0" encoding="UTF-8"?>
        <SftpSiteManager>
          <Servers>
            <Folder>
              <Name>Production</Name>
              <Server>
                <Host>sftp.example</Host>
                <Port>2222</Port>
                <Protocol>1</Protocol>
                <Logontype>1</Logontype>
                <User>deployer</User>
                <Pass>must-not-be-imported</Pass>
                <Keyfile>C:\keys\deploy.ppk</Keyfile>
                <Name>Deploy target</Name>
              </Server>
            </Folder>
            <Server>
              <Host>ftp.example</Host>
              <Port>21</Port>
              <Protocol>0</Protocol>
              <Name>FTP-only</Name>
            </Server>
          </Servers>
        </SftpSiteManager>
        """);
    var importedSiteManager = siteManagerXml.Profiles.Single();
    Assert(importedSiteManager.Host == "sftp.example" && importedSiteManager.Port == 2222 &&
           importedSiteManager.AuthMethod == "PublicKey" &&
           importedSiteManager.GroupName == "SFTP Site Manager import / Production",
        "SFTP site-manager import preserves hierarchy and key path");
    Assert(siteManagerXml.Warnings.Any(item => item.Contains("non-SFTP", StringComparison.Ordinal)),
        "FTP entries are never guessed as SSH profiles");

    HostHistoryStore.Append(
        "Production API", "api.example", "admin", 2202, "PublicKey", @"C:\keys\api.key",
        ["api"], "Success", null, 125);
    HostHistoryStore.Append(
        "Production API", "api.example", "admin", 2202, "PublicKey", @"C:\keys\api.key",
        ["api"], "Failed", "SSH.CONNECT.FAILED", 250);
    HostHistoryStore.Append(
        "Production API", "api.example", "admin", 2202, "PublicKey", @"C:\keys\api.key",
        ["api"], "Success", null, 80);
    HostHistoryStore.Append(
        "Build host", "build.example", "builder", 22, "Password", "",
        ["build"], "Success", null, 50);
    HostHistoryStore.Append(
        "Agent host", "agent.example", "agent-user", 22, "Agent", "",
        [], "Failed", "AUTHENTICATION_FAILED", 75);
    HostHistoryStore.Append(
        "MFA host", "mfa.example", "mfa-user", 22, "KeyboardInteractive", "",
        [], "Cancelled", "CONNECTION_CANCELLED", 60);

    var recent = HostHistoryStore.GetRecent(10);
    Assert(recent.Count == 6, "append-only duplicate history");
    Assert(recent.Any(item => item.Outcome == "Failed" && item.ErrorCode == "SSH.CONNECT.FAILED"),
        "failure outcome and diagnostic code");
    Assert(recent.Any(item => item.AuthMethod == "Agent") &&
           recent.Any(item => item.AuthMethod == "KeyboardInteractive"),
        "connection history preserves all four authentication types");
    Assert(recent.All(item => item.DurationMilliseconds is >= 0), "connection duration");

    var frequent = HostHistoryStore.GetMostFrequent(4);
    Assert(frequent[0].Hostname == "api.example", "frequent host ordering");
    Assert(frequent[0].ConnectionCount == 2, "frequent host counts successful attempts");

    Assert(HostProfileStore.Delete(created.Id), "saved profile delete");
    Assert(HostProfileStore.GetById(created.Id) is null, "deleted profile stays deleted");

    SharingTests.Run(Assert);
    Console.WriteLine("Saved-host and connection-history self-tests passed.");
}
finally
{
    Db.PathOverride = null;
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Directory.Delete(scratch, recursive: true);
}

static void Assert(bool condition, string name)
{
    if (!condition)
        throw new InvalidOperationException($"Self-test failed: {name}.");
}

static void AssertThrows<TException>(Action action, string name)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Self-test failed: {name}.");
}
