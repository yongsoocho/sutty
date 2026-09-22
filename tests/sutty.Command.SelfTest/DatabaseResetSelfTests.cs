using Microsoft.Data.Sqlite;
using sutty.Command;

internal static class DatabaseResetSelfTests
{
    public static void Run(Action<bool, string> assert, string scratch)
    {
        assert(string.Equals(Path.GetFullPath(Db.PathOverride!), Path.Combine(Path.GetFullPath(scratch), "sutty.db"),
                StringComparison.OrdinalIgnoreCase), "database reset fixture is confined to temporary storage");
        var keepFile = Path.Combine(scratch, "settings-reset-sentinel.json");
        File.WriteAllText(keepFile, "keep separate settings and recovery files");
        var oldCommand = CommandStore.Add("Before reset", "echo before");
        HostProfileStore.Save(new HostProfileDraft { DisplayName = "Reset fixture", Host = "reset.example" });
        HostHistoryStore.Append("Reset fixture", "reset.example");
        HostHistoryStore.SetPinned("reset.example", "Reset fixture", true);
        Execute("""
            CREATE TABLE "reset ""quoted" (id INTEGER PRIMARY KEY, payload TEXT);
            INSERT INTO "reset ""quoted" VALUES (1, 'fixture');
            CREATE TRIGGER reset_failure BEFORE DELETE ON host_profiles
                BEGIN SELECT RAISE(ABORT, 'fixture reset failure'); END;
            """);

        var beforeFailure = Counts();
        var migrationMarkers = MigrationMarkers();
        assert(migrationMarkers.Count > 0, "reset fixture includes completed migration metadata");
        var commandEvents = 0;
        var launcherEvents = 0;
        EventHandler commandChanged = (_, _) => commandEvents++;
        EventHandler launcherChanged = (_, _) => launcherEvents++;
        CommandStore.Changed += commandChanged;
        CommandLauncherStore.Changed += launcherChanged;
        try
        {
            var rejected = false;
            try { LocalDatabaseMaintenance.Reset(); }
            catch (SqliteException) { rejected = true; }
            assert(rejected && SameCounts(beforeFailure, Counts()),
                "database reset failure rolls back every earlier table delete");
            assert(commandEvents == 0 && launcherEvents == 0, "failed database reset emits no store change");
            Execute("DROP TRIGGER reset_failure");
            var beforeSchema = Schema();

            LocalDatabaseMaintenance.Reset();
            assert(Counts().All(entry => entry.Value == 0), "database reset empties every user-data table");
            assert(migrationMarkers.SequenceEqual(MigrationMarkers()), "database reset preserves completed migration markers");
            assert(beforeSchema.SequenceEqual(Schema()), "database reset preserves schema and indexes");
            assert(commandEvents == 1 && launcherEvents == 1, "database reset refreshes both command store observers");
            assert(File.ReadAllText(keepFile) == "keep separate settings and recovery files",
                "database reset leaves unrelated files intact");
            assert(CommandStore.GetAll().Count == 0 && HostProfileStore.GetAll().Count == 0 &&
                   HostHistoryStore.GetRecent().Count == 0 && CommandLauncherStore.GetFavorites().Count == 0 &&
                   CommandLauncherStore.GetRecentHistory().Count == 0,
                "existing initialized repositories remain usable after reset");

            var afterCommand = CommandStore.Add("After reset", "echo after");
            assert(afterCommand.Id > oldCommand.Id, "database reset cannot reuse stale command identifiers");
            HostHistoryStore.Append("New activity", "after-reset.example");
            assert(HostHistoryStore.GetRecent().Count == 1, "new runtime activity can be recorded after reset");
            assert(migrationMarkers.SequenceEqual(MigrationMarkers()), "new activity cannot reactivate legacy migrations after reset");
            LocalDatabaseMaintenance.Reset();
            LocalDatabaseMaintenance.Reset();
            assert(Counts().All(entry => entry.Value == 0), "repeated and empty database resets succeed");
        }
        finally
        {
            CommandStore.Changed -= commandChanged;
            CommandLauncherStore.Changed -= launcherChanged;
        }
        Console.WriteLine("Temporary SQLite reset, rollback, schema preservation, and observer tests passed.");
    }

    private static void Execute(string sql)
    {
        using var connection = Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static Dictionary<string, long> Counts()
    {
        using var connection = Db.Open();
        using var inventory = connection.CreateCommand();
        inventory.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT GLOB 'sqlite_*' AND name <> 'storage_migrations' ORDER BY name";
        var tables = new List<string>();
        using (var reader = inventory.ExecuteReader())
            while (reader.Read()) tables.Add(reader.GetString(0));
        var counts = new Dictionary<string, long>();
        foreach (var table in tables)
        {
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM \"" + table.Replace("\"", "\"\"") + "\"";
            counts[table] = Convert.ToInt64(count.ExecuteScalar());
        }
        return counts;
    }

    private static List<string> Schema()
    {
        using var connection = Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type || ':' || name || ':' || COALESCE(sql, '') FROM sqlite_schema ORDER BY type, name";
        using var reader = command.ExecuteReader();
        var entries = new List<string>();
        while (reader.Read()) entries.Add(reader.GetString(0));
        return entries;
    }

    private static List<string> MigrationMarkers()
    {
        using var connection = Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id || ':' || applied_at_utc FROM storage_migrations ORDER BY id";
        using var reader = command.ExecuteReader();
        var entries = new List<string>();
        while (reader.Read()) entries.Add(reader.GetString(0));
        return entries;
    }

    private static bool SameCounts(Dictionary<string, long> first, Dictionary<string, long> second) =>
        first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out var count) && count == pair.Value);
}
