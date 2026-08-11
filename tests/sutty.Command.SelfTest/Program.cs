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
    });
    Assert(created.Id.Length == 32, "saved profile id");
    Assert(created.CredentialId == "opaque-reference", "opaque credential reference");
    Assert(HostProfileStore.GetById(created.Id)?.DisplayName == "Production API",
        "saved profile reload");

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
    }, created.Id);
    Assert(updated.Port == 2202 && updated.AuthMethod == "PublicKey", "saved profile update");
    HostProfileStore.SetFavorite(created.Id, true);
    HostProfileStore.MarkConnected(created.Id);
    var marked = HostProfileStore.GetById(created.Id)!;
    Assert(marked.IsFavorite && marked.LastConnectedAtUtc is not null, "favorite and last connected");

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
