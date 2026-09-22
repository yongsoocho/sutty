using Microsoft.Data.Sqlite;

namespace sutty.Command;

/// <summary>Explicit, user-confirmed maintenance of Sutty's local SQLite data.</summary>
public static class LocalDatabaseMaintenance
{
    /// <summary>
    /// Deletes user rows in one write transaction while preserving tables, indexes,
    /// triggers, schema migration markers, and monotonic SQLite ids. The caller must obtain explicit confirmation.
    /// Separate settings, workspace, trust, vault, and recovery files are not touched.
    /// </summary>
    public static void Reset()
    {
        using (var connection = Db.Open())
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            using (var defer = connection.CreateCommand())
            {
                defer.Transaction = transaction;
                defer.CommandText = "PRAGMA defer_foreign_keys = ON";
                defer.ExecuteNonQuery();
            }

            var tables = new List<string>();
            using (var inventory = connection.CreateCommand())
            {
                inventory.Transaction = transaction;
                // Preserve migration metadata so later activity cannot be replayed as
                // a legacy Saved Host migration on the next application start.
                inventory.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT GLOB 'sqlite_*' AND name COLLATE NOCASE <> 'storage_migrations' ORDER BY name";
                using var reader = inventory.ExecuteReader();
                while (reader.Read()) tables.Add(reader.GetString(0));
            }

            foreach (var table in tables)
            {
                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = $"DELETE FROM {QuoteIdentifier(table)}";
                delete.ExecuteNonQuery();
            }

            // A trigger must not silently repopulate another table during a reset.
            // Any failure rolls the entire transaction back, including earlier deletes.
            foreach (var table in tables)
            {
                using var verify = connection.CreateCommand();
                verify.Transaction = transaction;
                verify.CommandText = $"SELECT EXISTS(SELECT 1 FROM {QuoteIdentifier(table)} LIMIT 1)";
                if (Convert.ToInt64(verify.ExecuteScalar()) != 0)
                    throw new InvalidOperationException("Database reset could not clear every application table.");
            }

            transaction.Commit();
        }

        CommandStore.NotifyDatabaseReset();
        CommandLauncherStore.NotifyDatabaseReset();
    }

    private static string QuoteIdentifier(string value) => '"' + value.Replace("\"", "\"\"") + '"';
}
