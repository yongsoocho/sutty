using Microsoft.Data.Sqlite;
using sutty.Core.Terminal;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace sutty.Command;

public static partial class HostProfileStore
{
    private const string CommandFavoritesMigration = "host_profiles_from_command_favorites_v1";

    /// <summary>Saves one external terminal favorite in the same repository as SSH hosts.</summary>
    public static HostProfile SaveCommandFavorite(LocalTerminalLaunchPlan plan, string? displayName = null)
    {
        var command = CommandLauncherStore.NormalizePlan(plan);
        var name = CommandLauncherStore.NormalizeDisplayName(displayName, command.CommandText);
        EnsureInitialized();
        lock (Gate)
        {
            // Canonical command + kind retains case-sensitive arguments and deduplicates
            // both legacy imports and repeated clicks on the one-line favorite button.
            var existing = GetAll(limit: MaxProfiles).FirstOrDefault(profile =>
                profile.LaunchKind == command.Kind.ToString() && profile.LaunchCommand == command.CommandText);
            var draft = existing is null ? new HostProfileDraft() : ToDraft(existing);
            draft.LaunchKind = command.Kind.ToString();
            draft.LaunchCommand = command.CommandText;
            draft.DisplayName = existing is not null && string.IsNullOrWhiteSpace(displayName)
                ? existing.DisplayName : name;
            draft.IsFavorite = true;
            var id = existing?.Id ?? CommandProfileId(command.Identity);
            if (existing is null && GetById(id) is not null)
                id = Guid.NewGuid().ToString("N"); // A portable SSH id may use the same prefix.
            return Save(draft, id);
        }
    }

    /// <summary>Revalidates persisted data and resolves PATH only after explicit user launch.</summary>
    public static LocalTerminalLaunchPlan CreateCommandLaunchPlan(HostProfile profile,
        ILocalTerminalExecutableResolver? resolver = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.IsExternalCommand) throw new ArgumentException("This is a Sutty SSH host.");
        var command = CommandLauncherStore.ValidateStoredCommand(profile.LaunchCommand, profile.LaunchKind);
        return LocalTerminalLaunchPlanner.Create(command.CommandText, resolver);
    }

    private static HostProfileDraft NormalizeCommandDraft(HostProfileDraft draft)
    {
        var command = CommandLauncherStore.ValidateStoredCommand(draft.LaunchCommand, draft.LaunchKind);
        return new HostProfileDraft
        {
            LaunchKind = command.Kind.ToString(), LaunchCommand = command.CommandText,
            DisplayName = CommandLauncherStore.NormalizeDisplayName(draft.DisplayName, command.CommandText),
            Host = "", Port = 22, AuthMethod = "Password",
            Tags = NormalizeTags(draft.Tags), GroupName = LimitClean(draft.GroupName, 64, "group name"),
            Environment = HostEnvironments.Normalize(draft.Environment), IsFavorite = draft.IsFavorite,
            // External processes own authentication; never bind Sutty credentials, routes or tunnels.
            CredentialId = null, AuthenticationAlias = "", Route = new(), Tunnels = [],
        };
    }

    private static string CommandProfileId(string identity) => "command_" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..32];

    private static void MigrateCommandFavorites(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using (var applied = connection.CreateCommand())
        {
            applied.Transaction = transaction;
            applied.CommandText = "SELECT 1 FROM storage_migrations WHERE id = $id";
            applied.Parameters.AddWithValue("$id", CommandFavoritesMigration);
            if (applied.ExecuteScalar() is not null) return;
        }
        var rows = new List<(long Id, string Name, string Command, string Kind, string Created, string Updated, string? Last)>();
        if (TableExists(connection, "command_launcher_favorites", transaction))
        {
            using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = "SELECT id, display_name, command_text, launch_kind, created_at_utc, updated_at_utc, last_launched_at_utc FROM command_launcher_favorites";
            using var reader = read.ExecuteReader();
            while (reader.Read()) rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        foreach (var row in rows)
        {
            HostProfileDraft draft;
            CommandLauncherStore.SafeLauncherCommand command;
            try
            {
                command = CommandLauncherStore.ValidateStoredCommand(row.Command, row.Kind);
                draft = NormalizeCommandDraft(new HostProfileDraft
                { LaunchKind = row.Kind, LaunchCommand = row.Command, DisplayName = row.Name, IsFavorite = true });
                _ = DateTimeOffset.Parse(row.Created);
                _ = DateTimeOffset.Parse(row.Updated);
                if (row.Last is not null) _ = DateTimeOffset.Parse(row.Last);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or FormatException)
            {
                Debug.WriteLine($"Legacy command favorite {row.Id} was retained for review: {error.GetType().Name}.");
                continue; // Keep the original row, including unsupported data and usage counts.
            }
            using var existing = connection.CreateCommand();
            existing.Transaction = transaction;
            existing.CommandText = "SELECT id FROM host_profiles WHERE launch_kind = $kind AND launch_command = $command";
            existing.Parameters.AddWithValue("$kind", draft.LaunchKind);
            existing.Parameters.AddWithValue("$command", draft.LaunchCommand);
            if (existing.ExecuteScalar() is not null) continue;
            using var count = connection.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM host_profiles";
            if (Convert.ToInt32(count.ExecuteScalar()) >= MaxProfiles)
            {
                Debug.WriteLine($"Legacy command favorite {row.Id} retained: saved-host limit reached.");
                continue;
            }
            var id = CommandProfileId(command.Identity);
            using var collision = connection.CreateCommand();
            collision.Transaction = transaction;
            collision.CommandText = "SELECT 1 FROM host_profiles WHERE id = $id";
            collision.Parameters.AddWithValue("$id", id);
            if (collision.ExecuteScalar() is not null) id = Guid.NewGuid().ToString("N");
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO host_profiles
                    (id, display_name, host, port, username, auth_method, is_favorite,
                     launch_kind, launch_command, created_at_utc, updated_at_utc, last_connected_at_utc)
                VALUES ($id, $name, '', 22, '', 'Password', 1, $kind, $command, $created, $updated, $last)
                """;
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$name", draft.DisplayName);
            insert.Parameters.AddWithValue("$kind", draft.LaunchKind);
            insert.Parameters.AddWithValue("$command", draft.LaunchCommand);
            insert.Parameters.AddWithValue("$created", row.Created);
            insert.Parameters.AddWithValue("$updated", row.Updated);
            insert.Parameters.AddWithValue("$last", (object?)row.Last ?? DBNull.Value);
            insert.ExecuteNonQuery();
        }
        using var complete = connection.CreateCommand();
        complete.Transaction = transaction;
        complete.CommandText = "INSERT INTO storage_migrations(id, applied_at_utc) VALUES ($id, $now)";
        complete.Parameters.AddWithValue("$id", CommandFavoritesMigration);
        complete.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        complete.ExecuteNonQuery();
        transaction.Commit();
        // The old table is archival only. The marker prevents deleted/unfavorited rows
        // from reappearing. New versions never create, read for UI, or write that library.
    }

    private static void NotifyChanged()
    {
        foreach (EventHandler handler in Changed?.GetInvocationList() ?? [])
        {
            try { handler(null, EventArgs.Empty); }
            catch { /* Closed observers cannot fail a durable saved-host mutation. */ }
        }
    }
}
