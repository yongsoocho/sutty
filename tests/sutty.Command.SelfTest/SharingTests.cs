using sutty.Command;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class SharingTests
{
    public static void SeedPreviousHostSchema()
    {
        // Previous Alpha schema, before authentication_alias existed. This is deliberately
        // created before HostProfileStore's first initialization to exercise in-place upgrade.
        using var connection = Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE host_profiles (
                id TEXT PRIMARY KEY, display_name TEXT NOT NULL, host TEXT NOT NULL, port INTEGER NOT NULL,
                username TEXT NOT NULL DEFAULT '', auth_method TEXT NOT NULL DEFAULT 'Password',
                private_key_path TEXT NOT NULL DEFAULT '', tags_json TEXT NOT NULL DEFAULT '[]',
                group_name TEXT NOT NULL DEFAULT '', environment TEXT NOT NULL DEFAULT 'Unclassified',
                is_favorite INTEGER NOT NULL DEFAULT 0, credential_id TEXT,
                route_json TEXT NOT NULL DEFAULT '{}', tunnels_json TEXT NOT NULL DEFAULT '[]',
                created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL, last_connected_at_utc TEXT
            );
            INSERT INTO host_profiles (id, display_name, host, port, username, auth_method, private_key_path,
                credential_id, route_json, tunnels_json, created_at_utc, updated_at_utc)
            VALUES ('previous-alpha', 'Previous saved host', 'previous.example', 2222, 'previous-user', 'PublicKey',
                'C:\keys\previous.key', 'previous-local-vault-reference',
                '{"type":"Socks5","host":"previous-proxy.example","port":1080,"disableDirect":true}',
                '[{"type":"Dynamic","bindHost":"127.0.0.1","bindPort":19080}]', $now, $now);
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public static void VerifyPreviousHostMigration(Action<bool, string> assert)
    {
        var previous = HostProfileStore.GetById("previous-alpha")!;
        assert(previous.AuthenticationAlias == "" && previous.CredentialId == "previous-local-vault-reference" &&
               previous.PrivateKeyPath == @"C:\keys\previous.key" && previous.Route.Type == "Socks5" &&
               previous.Route.DisableDirect && previous.Tunnels.Count == 1 && previous.Port == 2222,
            "previous Alpha host table gains empty alias without losing credential refs, key paths, route or tunnels");
        HostProfileStore.SetAuthenticationAlias(previous.Id, "upgraded-alias");
        assert(HostProfileStore.GetById(previous.Id)!.CredentialId == "previous-local-vault-reference",
            "writing new alias after migration keeps original local credential binding");
        HostProfileStore.Delete(previous.Id);
    }

    public static void Run(Action<bool, string> assert)
    {
        var secret = $"synthetic-{Guid.NewGuid():N}";
        var localKey = $@"C:\local-only\{Guid.NewGuid():N}.key";
        var host = HostProfileStore.Save(new HostProfileDraft
        {
            DisplayName = "Share source", Host = "share.example", Username = "Operator", AuthMethod = "PublicKey",
            PrivateKeyPath = localKey, CredentialId = secret, AuthenticationAlias = "team-dev",
            Tags = ["dev"], GroupName = "Team", Environment = HostEnvironments.Development,
            Route = new HostRouteProfile { Type = "SshJump", Host = "jump-share.example", Port = 22,
                AuthMethod = "PublicKey", PrivateKeyPath = localKey, Username = "jump", DisableDirect = true },
            Tunnels = [new HostTunnelProfile { BindPort = 18080, DestinationHost = "internal.example", DestinationPort = 80 }],
        });
        assert(host.AuthenticationAlias == "team-dev", "logical authentication alias round-trips through SQLite");
        var olderDraft = HostProfileStore.ToDraft(host);
        olderDraft.AuthenticationAlias = null;
        host = HostProfileStore.Save(olderDraft, host.Id);
        assert(host.AuthenticationAlias == "team-dev", "older edit surfaces preserve authentication alias");
        var copied = HostProfileStore.Duplicate(host.Id, "Share source copy");
        assert(copied.Id != host.Id && copied.CredentialId is null && copied.PrivateKeyPath == localKey &&
               copied.Route.PrivateKeyPath == localKey && copied.Tunnels.Count == 1, "duplicate copies local definitions without vault reference ownership collision");
        assert(HostProfileStore.GetById(host.Id)!.CredentialId == secret, "duplication never changes source credential binding");
        HostProfileStore.Delete(copied.Id);

        var command = CommandStore.Add("Shared health", "uptime\nprintf '%s' \"$1\"");
        var json = DefinitionSharingService.Export([host], [command]);
        assert(!json.Contains(secret) && !json.Contains("local-only") && !json.Contains("credentialId") &&
               !json.Contains("privateKeyPath") && !json.Contains("usageCount") && !json.Contains("lastConnected") &&
               !json.Contains("fingerprint") && json.Contains("schemaVersion") && json.Contains("team-dev"),
            "allowlisted export strips all local credential refs, key paths, history and trust");
        var empty = DefinitionSharingService.Export([], []);
        assert(DefinitionSharingService.PreviewJson(empty).Hosts.Count == 0, "empty selection exports no hosts");
        var exportDirectory = Path.Combine(Path.GetTempPath(), $"sutty-share-save-{Guid.NewGuid():N}");
        Directory.CreateDirectory(exportDirectory);
        try
        {
            var filePath = Path.Combine(exportDirectory, "reviewed.json");
            File.WriteAllText(filePath, "previous-export");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Throws<OperationCanceledException>(() => DefinitionSharingService.SaveFileAsync(filePath, json, cancelled.Token).GetAwaiter().GetResult(), assert,
                "cancelled export preserves previous destination");
            assert(File.ReadAllText(filePath) == "previous-export" && Directory.GetFiles(exportDirectory).Length == 1,
                "cancelled export cleans temporary content and preserves previous file");
            DefinitionSharingService.SaveFileAsync(filePath, json).GetAwaiter().GetResult();
            assert(File.ReadAllText(filePath) == json && DefinitionSharingService.PreviewFile(filePath).Hosts.Count == 1,
                "saved export is exactly the reviewed JSON and can be read back");
        }
        finally { Directory.Delete(exportDirectory, recursive: true); }

        var preview = DefinitionSharingService.PreviewJson(json);
        assert(preview.Hosts.Single().Kind == ImportChangeKind.Duplicate &&
               preview.Commands.Single().Kind == ImportChangeKind.Duplicate &&
               preview.Hosts.All(row => row.Choice == ImportChoice.Skip), "import preview detects duplicates and defaults every row to skip");
        var unchanged = DefinitionSharingService.Apply(preview);
        assert(unchanged.Added == 0 && unchanged.Updated == 0 && unchanged.Skipped == 2, "unselected import is a no-op");

        var node = JsonNode.Parse(json)!;
        node["hosts"]![0]!["displayName"] = "Changed share";
        node["hosts"]![0]!["credentialId"] = secret;
        node["hosts"]![0]!["privateKeyPath"] = localKey;
        node["hosts"]![0]!["password"] = secret;
        node["hosts"]![0]!["route"]!["privateKeyPath"] = localKey;
        node["hosts"]![0]!["route"]!["command"] = secret;
        preview = DefinitionSharingService.PreviewJson(node.ToJsonString());
        assert(preview.Hosts[0].Kind == ImportChangeKind.Change && preview.Warnings.Count > 0 &&
               preview.Hosts[0].Draft.CredentialId is null && preview.Hosts[0].Draft.PrivateKeyPath == "" &&
               preview.Hosts[0].Draft.Route.PrivateKeyPath == "" && preview.Hosts[0].Draft.Route.Command == "",
            "incoming forbidden fields are ignored with a warning and cannot cross credential boundaries");
        preview.Hosts[0].Choice = ImportChoice.Update;
        var applied = DefinitionSharingService.Apply(preview);
        host = HostProfileStore.GetById(host.Id)!;
        assert(applied.Updated == 1 && host.DisplayName == "Changed share" && host.CredentialId == secret &&
               host.PrivateKeyPath == localKey && host.Route.PrivateKeyPath == localKey,
            "explicit compatible update preserves this PC's credential and jump key bindings");

        node["hosts"]![0]!["route"]!["host"] = "changed-gateway.example";
        preview = DefinitionSharingService.PreviewJson(node.ToJsonString());
        preview.Hosts[0].Choice = ImportChoice.Update;
        DefinitionSharingService.Apply(preview);
        host = HostProfileStore.GetById(host.Id)!;
        assert(host.CredentialId is null && host.Route.PrivateKeyPath == "",
            "changed jump route clears bundled vault binding so old proxy passwords cannot be sent to a new gateway");
        var rebound = HostProfileStore.ToDraft(host);
        rebound.CredentialId = secret;
        rebound.PrivateKeyPath = localKey;
        HostProfileStore.Save(rebound, host.Id);
        node["hosts"]![0]!["username"] = "operator";
        preview = DefinitionSharingService.PreviewJson(node.ToJsonString());
        preview.Hosts[0].Choice = ImportChoice.Update;
        DefinitionSharingService.Apply(preview);
        host = HostProfileStore.GetById(host.Id)!;
        assert(host.CredentialId is null && host.PrivateKeyPath == "", "case-sensitive SSH username change clears local authentication binding");
        preview = DefinitionSharingService.PreviewJson(node.ToJsonString());
        preview.Hosts[0].Choice = ImportChoice.Copy;
        applied = DefinitionSharingService.Apply(preview);
        assert(applied.Added == 1 && HostProfileStore.GetAll().Any(item => item.Id != host.Id && item.DisplayName.EndsWith("(copy)")),
            "explicit duplicate-as-copy creates a different local identity");

        node["hosts"]![0]!["id"] = Guid.NewGuid().ToString("N");
        node["hosts"]![0]!["host"] = "new-share.example";
        preview = DefinitionSharingService.PreviewJson(node.ToJsonString());
        preview.Hosts[0].Choice = ImportChoice.Add;
        var targetId = node["hosts"]![0]!["id"]!.GetValue<string>();
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            Throws<OperationCanceledException>(() => DefinitionSharingService.Apply(preview, cancellation.Token), assert, "cancelled import does not write");
        }
        assert(HostProfileStore.GetById(targetId) is null, "cancel before import leaves storage unchanged");
        applied = DefinitionSharingService.Apply(preview);
        assert(applied.Added == 1 && HostProfileStore.GetById(targetId)!.CredentialId is null,
            "selected shared add retains portable profile id with no foreign credential reference");

        preview = DefinitionSharingService.PreviewJson(node.ToJsonString());
        preview.Hosts[0].Choice = ImportChoice.Update;
        HostProfileStore.SetAuthenticationAlias(targetId, "locally-edited");
        applied = DefinitionSharingService.Apply(preview);
        assert(applied.Failed == 1 && HostProfileStore.GetById(targetId)!.AuthenticationAlias == "locally-edited",
            "stale import preview cannot overwrite a local edit");

        node["hosts"]![0]!["route"] = null;
        preview = DefinitionSharingService.PreviewJson(node.ToJsonString());
        assert(preview.Hosts[0].Kind == ImportChangeKind.Invalid && !preview.Hosts[0].Draft.Route.CanConnect,
            "null route is invalid and cannot downgrade to direct");
        node["hosts"]![0]!.AsObject().Remove("route");
        assert(DefinitionSharingService.PreviewJson(node.ToJsonString()).Hosts[0].Kind == ImportChangeKind.Invalid,
            "omitted route is not implicitly direct");
        node["hosts"]![0]!["route"] = new JsonObject { ["type"] = "ExternalProxyCommand", ["command"] = secret };
        preview = DefinitionSharingService.PreviewJson(node.ToJsonString());
        assert(preview.Hosts[0].Kind == ImportChangeKind.Invalid && preview.Hosts[0].Draft.Route.Command.Length == 0,
            "external executable commands never import as active routes");
        preview.Hosts[0].Choice = ImportChoice.Copy;
        assert(DefinitionSharingService.Apply(preview).Failed == 1, "UI choice tampering cannot activate invalid route");
        node["hosts"]![0]!["route"] = new JsonObject { ["type"] = "SshJump", ["host"] = "gateway.example", ["port"] = 0 };
        assert(DefinitionSharingService.PreviewJson(node.ToJsonString()).Hosts[0].Kind == ImportChangeKind.Invalid,
            "invalid indirect route remains blocked");
        node["schemaVersion"] = 2;
        Throws<InvalidDataException>(() => DefinitionSharingService.PreviewJson(node.ToJsonString()), assert, "future schemas are rejected");
        Throws<InvalidDataException>(() => DefinitionSharingService.PreviewJson("{\"schemaVersion\":1,\"schemaVersion\":1,\"hosts\":[],\"commands\":[]}"), assert,
            "duplicate JSON fields cannot make review ambiguous");
        Throws<InvalidDataException>(() => DefinitionSharingService.PreviewJson(new string(' ', DefinitionSharingService.MaxFileBytes + 1)), assert,
            "oversized sharing files are rejected before parsing");

        var proxyHost = HostProfileStore.Save(new HostProfileDraft
        {
            DisplayName = "Local proxy", Host = "proxy-export.example",
            Route = new HostRouteProfile { Type = "ExternalProxyCommand", Command = secret },
        });
        var proxyJson = DefinitionSharingService.Export([proxyHost], []);
        assert(!proxyJson.Contains(secret) && DefinitionSharingService.PreviewJson(proxyJson).Hosts[0].Kind == ImportChangeKind.Invalid,
            "proxy export omits local command while retaining a blocked indirect route definition");

        var openSsh = HostProfileImportService.ParseOpenSsh("Host imported-new\n HostName importer.example\n User dev\n Compression yes\n");
        preview = DefinitionSharingService.Preview(openSsh);
        assert(preview.Hosts[0].Kind == ImportChangeKind.Add && preview.Warnings.Any(item => item.Contains("Compression")),
            "legacy import previews new hosts and unsupported fields");
        var badPutty = HostProfileImportService.ParsePuttySession("bad proxy", new Dictionary<string, object?>
        { ["HostName"] = "bad-route.example", ["ProxyMethod"] = 4 });
        assert(badPutty is not null && !badPutty.Route.CanConnect, "unsupported PuTTY proxy does not fall back to Direct");
        var multiJump = HostProfileImportService.ParseOpenSsh("Host multi-hop\n ProxyJump a.example,b.example\n");
        assert(DefinitionSharingService.Preview(multiJump).Hosts[0].Kind == ImportChangeKind.Invalid,
            "unsupported multi-hop import cannot silently truncate route");
        var invalidPort = HostProfileImportService.ParseOpenSsh("Host bad-port\n Port not-a-port\n");
        assert(DefinitionSharingService.Preview(invalidPort).Hosts[0].Kind == ImportChangeKind.Invalid,
            "malformed source port appears invalid instead of silently becoming port 22");
        var external = HostProfileImportService.ParseOpenSsh("Host exec-proxy\n ProxyCommand local.exe %h\n");
        assert(DefinitionSharingService.Preview(external).Hosts[0].Kind == ImportChangeKind.Invalid &&
               HostProfileImportService.SaveUnique(external).Failed == 1, "legacy bulk import also blocks unapproved local execution");

        var commandsOnly = JsonNode.Parse(DefinitionSharingService.Export([], [command]))!;
        commandsOnly["commands"]![0]!["commandText"] = "date";
        preview = DefinitionSharingService.PreviewJson(commandsOnly.ToJsonString());
        preview.Commands[0].Choice = ImportChoice.Update;
        assert(DefinitionSharingService.Apply(preview).Updated == 1 && CommandStore.GetAll().Single(item => item.Id == command.Id).CommandText == "date",
            "selected command update changes template text without executing it");
        Console.WriteLine("Host import, duplication, and definition-sharing self-tests passed.");
    }

    private static void Throws<T>(Action action, Action<bool, string> assert, string name) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        assert(false, name);
    }
}
