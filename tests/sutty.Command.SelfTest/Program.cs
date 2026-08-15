using sutty.Command;

var scratch = Path.Combine(Path.GetTempPath(), $"sutty-command-self-test-{Guid.NewGuid():N}");
Directory.CreateDirectory(scratch);
Db.PathOverride = Path.Combine(scratch, "sutty.db");

try
{
    Assert(!File.Exists(Db.PathOverride), "no bundled local database");
    Assert(HostHistoryStore.GetRecent().Count == 0, "new history store has no bundled rows");

    HostHistoryStore.SetPinned(
        "legacy.example",
        "Legacy host",
        true,
        "operator",
        2222,
        "PublicKey",
        @"C:\keys\legacy.key",
        ["legacy"]);

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
            EnterpriseMode = true,
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
    Assert(created.Tunnels.Single().DestinationPort == 5432,
        "saved profile tunnel reload");

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
        Route = created.Route,
        Tunnels = created.Tunnels,
    }, created.Id);
    Assert(updated.Port == 2202 && updated.AuthMethod == "PublicKey", "saved profile update");
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

    var recent = HostHistoryStore.GetRecent(10);
    Assert(recent.Count == 4, "append-only duplicate history");
    Assert(recent.Any(item => item.Outcome == "Failed" && item.ErrorCode == "SSH.CONNECT.FAILED"),
        "failure outcome and diagnostic code");
    Assert(recent.All(item => item.DurationMilliseconds is >= 0), "connection duration");

    var frequent = HostHistoryStore.GetMostFrequent(4);
    Assert(frequent[0].Hostname == "api.example", "frequent host ordering");
    Assert(frequent[0].ConnectionCount == 2, "frequent host counts successful attempts");

    Assert(HostProfileStore.Delete(created.Id), "saved profile delete");
    Assert(HostProfileStore.GetById(created.Id) is null, "deleted profile stays deleted");

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
