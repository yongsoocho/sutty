using Microsoft.Data.Sqlite;
using sutty.Core.Terminal;
using System.Globalization;

namespace sutty.Command;

/// <summary>
/// The lifecycle marker for a local connection shortcut.  It intentionally
/// contains no process output, exit text, or credential diagnostics.
/// </summary>
public enum CommandLauncherLaunchOutcome
{
    LaunchRequested,
    Started,
    LaunchNotStarted,
    Exited,
    Failed,
    Cancelled,
}

/// <summary>
/// A user-pinned, direct local connection command.  <see cref="CommandText"/>
/// is the Core planner's canonical display form, never a resolved executable
/// path or a shell-expanded command line.
/// </summary>
public sealed class CommandLauncherFavorite
{
    public long Id { get; init; }
    public string DisplayName { get; init; } = "";
    public string CommandText { get; init; } = "";
    public LocalTerminalLaunchKind Kind { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public DateTimeOffset? LastLaunchedAtUtc { get; init; }
    public int LaunchCount { get; init; }
}

/// <summary>
/// A bounded, credential-free local-launch record.  A deleted favorite leaves
/// its old history snapshot available but clears <see cref="FavoriteId"/>.
/// </summary>
public sealed class CommandLauncherHistoryEntry
{
    public long Id { get; init; }
    public long? FavoriteId { get; init; }
    public string DisplayName { get; init; } = "";
    public string CommandText { get; init; } = "";
    public LocalTerminalLaunchKind Kind { get; init; }
    public DateTimeOffset LaunchedAtUtc { get; init; }
    public CommandLauncherLaunchOutcome Outcome { get; init; }
}

/// <summary>
/// Persists a small, local-only library of direct SSH and Multipass connection
/// shortcuts.  Input is accepted only as a Core planner-produced plan, so this
/// store never parses arbitrary shell text or persists a resolved executable,
/// output, token, password, passphrase, or private-key contents.
/// </summary>
public static class CommandLauncherStore
{
    public const int MaximumFavorites = 100;
    public const int MaximumHistoryEntries = 250;
    public const int DefaultFavoriteLimit = 6;
    public const int DefaultHistoryLimit = 8;
    public const int MaximumDisplayNameLength = 128;

    private static readonly object Gate = new();
    private static bool _initialized;
    private static string? _initializedDatabasePath;
    private static readonly char[] ShellSyntaxCharacters = ['&', '|', ';', '<', '>', '`', '$', '(', ')'];
    private static readonly HashSet<string> SensitiveArgumentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "pass", "passphrase", "pw", "token", "accesstoken", "apitoken",
        "apikey", "secret", "clientsecret", "credential", "credentials", "authorization",
        "auth", "cookie", "session", "sessiontoken", "privatekey", "keydata",
        "identityfilecontents",
    };
    private static readonly HashSet<string> CredentialPairArgumentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "user", "proxyuser", "u",
    };

    /// <summary>Raised after favorites or launch history changes.</summary>
    public static event EventHandler? Changed;

    public static void EnsureInitialized()
    {
        lock (Gate)
        {
            var databasePath = Db.DbPath;
            if (_initialized && string.Equals(
                    _initializedDatabasePath,
                    databasePath,
                    StringComparison.OrdinalIgnoreCase))
                return;

            using var connection = Db.Open();
            using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS command_launcher_favorites (
                    id                    INTEGER PRIMARY KEY AUTOINCREMENT,
                    display_name          TEXT    NOT NULL,
                    command_text          TEXT    NOT NULL,
                    command_identity      TEXT    NOT NULL COLLATE BINARY,
                    launch_kind           TEXT    NOT NULL,
                    created_at_utc        TEXT    NOT NULL,
                    updated_at_utc        TEXT    NOT NULL,
                    last_launched_at_utc  TEXT,
                    launch_count          INTEGER NOT NULL DEFAULT 0
                );
                CREATE UNIQUE INDEX IF NOT EXISTS ux_command_launcher_favorites_identity
                    ON command_launcher_favorites(command_identity);
                CREATE INDEX IF NOT EXISTS idx_command_launcher_favorites_recent
                    ON command_launcher_favorites(last_launched_at_utc DESC, created_at_utc DESC);

                CREATE TABLE IF NOT EXISTS command_launcher_history (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    favorite_id     INTEGER,
                    display_name    TEXT    NOT NULL,
                    command_text    TEXT    NOT NULL,
                    launch_kind     TEXT    NOT NULL,
                    launched_at_utc TEXT    NOT NULL,
                    outcome         TEXT    NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_command_launcher_history_recent
                    ON command_launcher_history(launched_at_utc DESC, id DESC);
                """;
            create.ExecuteNonQuery();
            _initialized = true;
            _initializedDatabasePath = databasePath;
        }
    }

    /// <summary>
    /// Pins a planner-validated command.  The runtime executable path is never
    /// written; callers retain the plan only long enough to launch it.
    /// </summary>
    public static CommandLauncherFavorite SaveFavorite(
        string? displayName,
        LocalTerminalLaunchPlan plan)
    {
        var command = NormalizePlan(plan);
        var name = NormalizeDisplayName(displayName, command.CommandText);
        EnsureInitialized();

        var now = DateTimeOffset.UtcNow;
        long id;
        using (var connection = Db.Open())
        using (var transaction = connection.BeginTransaction())
        {
            using (var count = connection.CreateCommand())
            {
                count.Transaction = transaction;
                count.CommandText = "SELECT COUNT(*) FROM command_launcher_favorites";
                if (Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture) >= MaximumFavorites)
                    throw new InvalidOperationException("The launcher favorite limit has been reached.");
            }

            try
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO command_launcher_favorites (
                        display_name, command_text, command_identity, launch_kind,
                        created_at_utc, updated_at_utc, last_launched_at_utc, launch_count)
                    VALUES (
                        $name, $command, $identity, $kind,
                        $created, $updated, NULL, 0);
                    SELECT last_insert_rowid();
                    """;
                insert.Parameters.AddWithValue("$name", name);
                insert.Parameters.AddWithValue("$command", command.CommandText);
                insert.Parameters.AddWithValue("$identity", command.Identity);
                insert.Parameters.AddWithValue("$kind", command.Kind.ToString());
                insert.Parameters.AddWithValue("$created", now.ToString("O"));
                insert.Parameters.AddWithValue("$updated", now.ToString("O"));
                id = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                throw new InvalidOperationException("That launcher command is already a favorite.", exception);
            }

            transaction.Commit();
        }

        var favorite = new CommandLauncherFavorite
        {
            Id = id,
            DisplayName = name,
            CommandText = command.CommandText,
            Kind = command.Kind,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        NotifyChanged();
        return favorite;
    }

    /// <summary>Edits a favorite without resetting its launch count or history.</summary>
    public static CommandLauncherFavorite UpdateFavorite(
        long id,
        string? displayName,
        LocalTerminalLaunchPlan plan)
    {
        if (id <= 0)
            throw new ArgumentOutOfRangeException(nameof(id));

        var command = NormalizePlan(plan);
        var name = NormalizeDisplayName(displayName, command.CommandText);
        EnsureInitialized();

        using (var connection = Db.Open())
        {
            try
            {
                using var update = connection.CreateCommand();
                update.CommandText = """
                    UPDATE command_launcher_favorites
                    SET display_name = $name,
                        command_text = $command,
                        command_identity = $identity,
                        launch_kind = $kind,
                        updated_at_utc = $updated
                    WHERE id = $id
                    """;
                update.Parameters.AddWithValue("$name", name);
                update.Parameters.AddWithValue("$command", command.CommandText);
                update.Parameters.AddWithValue("$identity", command.Identity);
                update.Parameters.AddWithValue("$kind", command.Kind.ToString());
                update.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
                update.Parameters.AddWithValue("$id", id);
                if (update.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("The launcher favorite no longer exists.");
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                throw new InvalidOperationException("That launcher command is already a favorite.", exception);
            }
        }

        var favorite = GetFavorite(id)
            ?? throw new InvalidOperationException("The launcher favorite no longer exists.");
        NotifyChanged();
        return favorite;
    }

    /// <summary>Returns the newest or most recently launched favorites for a compact launcher surface.</summary>
    public static List<CommandLauncherFavorite> GetFavorites(int limit = DefaultFavoriteLimit)
    {
        EnsureInitialized();
        limit = Math.Clamp(limit, 1, MaximumFavorites);

        using var connection = Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, display_name, command_text, launch_kind,
                   created_at_utc, updated_at_utc, last_launched_at_utc, launch_count
            FROM command_launcher_favorites
            ORDER BY COALESCE(last_launched_at_utc, created_at_utc) DESC,
                     display_name COLLATE NOCASE,
                     id DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<CommandLauncherFavorite>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (TryReadFavorite(reader, out var favorite))
                result.Add(favorite);
        }
        return result;
    }

    public static CommandLauncherFavorite? GetFavorite(long id)
    {
        if (id <= 0)
            return null;

        EnsureInitialized();
        using var connection = Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, display_name, command_text, launch_kind,
                   created_at_utc, updated_at_utc, last_launched_at_utc, launch_count
            FROM command_launcher_favorites
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() && TryReadFavorite(reader, out var favorite)
            ? favorite
            : null;
    }

    /// <summary>
    /// Atomically snapshots a favorite into history and increments its usage.
    /// The plan must still match the currently saved canonical command so a
    /// stale UI cannot launch one favorite while recording another.
    /// </summary>
    public static CommandLauncherHistoryEntry RecordFavoriteLaunch(
        long favoriteId,
        LocalTerminalLaunchPlan plan)
    {
        if (favoriteId <= 0)
            throw new ArgumentOutOfRangeException(nameof(favoriteId));

        var requested = NormalizePlan(plan);
        EnsureInitialized();

        CommandLauncherHistoryEntry entry;
        using (var connection = Db.Open())
        using (var transaction = connection.BeginTransaction())
        {
            var favorite = ReadFavoriteById(connection, transaction, favoriteId)
                ?? throw new InvalidOperationException("The launcher favorite no longer exists or is unsafe.");
            if (favorite.Kind != requested.Kind ||
                !string.Equals(favorite.CommandText, requested.CommandText, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The launcher favorite changed before it could be started.");
            }

            var now = DateTimeOffset.UtcNow;
            entry = InsertHistory(
                connection,
                transaction,
                favorite.Id,
                favorite.DisplayName,
                requested,
                now,
                CommandLauncherLaunchOutcome.LaunchRequested);

            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE command_launcher_favorites
                    SET launch_count = launch_count + 1,
                        last_launched_at_utc = $now,
                        updated_at_utc = $now
                    WHERE id = $id
                    """;
                update.Parameters.AddWithValue("$now", now.ToString("O"));
                update.Parameters.AddWithValue("$id", favoriteId);
                if (update.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("The launcher favorite no longer exists.");
            }

            PruneHistory(connection, transaction);
            transaction.Commit();
        }

        NotifyChanged();
        return entry;
    }

    /// <summary>Records a safe, non-favorited direct connection attempt.</summary>
    public static CommandLauncherHistoryEntry RecordAdHocLaunch(
        LocalTerminalLaunchPlan plan,
        string? displayName = null)
    {
        var command = NormalizePlan(plan);
        var name = NormalizeDisplayName(displayName, command.CommandText);
        EnsureInitialized();

        CommandLauncherHistoryEntry entry;
        using (var connection = Db.Open())
        using (var transaction = connection.BeginTransaction())
        {
            entry = InsertHistory(
                connection,
                transaction,
                null,
                name,
                command,
                DateTimeOffset.UtcNow,
                CommandLauncherLaunchOutcome.LaunchRequested);
            PruneHistory(connection, transaction);
            transaction.Commit();
        }

        NotifyChanged();
        return entry;
    }

    /// <summary>
    /// Updates only a finite lifecycle outcome.  There is deliberately no API
    /// for recording command output, exception messages, or process arguments.
    /// </summary>
    public static bool CompleteLaunch(long historyId, CommandLauncherLaunchOutcome outcome)
    {
        if (historyId <= 0 || !Enum.IsDefined(outcome))
            return false;

        EnsureInitialized();
        using var connection = Db.Open();
        using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE command_launcher_history
            SET outcome = $outcome
            WHERE id = $id
            """;
        update.Parameters.AddWithValue("$outcome", outcome.ToString());
        update.Parameters.AddWithValue("$id", historyId);
        var changed = update.ExecuteNonQuery() == 1;
        if (changed)
            NotifyChanged();
        return changed;
    }

    /// <summary>Returns compact, newest-first history.  The physical table is capped at 250 rows.</summary>
    public static List<CommandLauncherHistoryEntry> GetRecentHistory(int limit = DefaultHistoryLimit)
    {
        EnsureInitialized();
        limit = Math.Clamp(limit, 1, MaximumHistoryEntries);

        using var connection = Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, favorite_id, display_name, command_text, launch_kind, launched_at_utc, outcome
            FROM command_launcher_history
            ORDER BY launched_at_utc DESC, id DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<CommandLauncherHistoryEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (TryReadHistory(reader, out var entry))
                result.Add(entry);
        }
        return result;
    }

    /// <summary>Removes a favorite while preserving its bounded history snapshot.</summary>
    public static bool DeleteFavorite(long id)
    {
        if (id <= 0)
            return false;

        EnsureInitialized();
        bool deleted;
        using (var connection = Db.Open())
        using (var transaction = connection.BeginTransaction())
        {
            using (var detach = connection.CreateCommand())
            {
                detach.Transaction = transaction;
                detach.CommandText = "UPDATE command_launcher_history SET favorite_id = NULL WHERE favorite_id = $id";
                detach.Parameters.AddWithValue("$id", id);
                detach.ExecuteNonQuery();
            }

            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM command_launcher_favorites WHERE id = $id";
                delete.Parameters.AddWithValue("$id", id);
                deleted = delete.ExecuteNonQuery() == 1;
            }
            transaction.Commit();
        }

        if (deleted)
            NotifyChanged();
        return deleted;
    }

    public static bool DeleteHistory(long id)
    {
        if (id <= 0)
            return false;

        EnsureInitialized();
        using var connection = Db.Open();
        using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM command_launcher_history WHERE id = $id";
        delete.Parameters.AddWithValue("$id", id);
        var deleted = delete.ExecuteNonQuery() == 1;
        if (deleted)
            NotifyChanged();
        return deleted;
    }

    /// <summary>Clears only local launcher history; saved favorites remain untouched.</summary>
    public static void ClearHistory()
    {
        EnsureInitialized();
        using var connection = Db.Open();
        using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM command_launcher_history";
        if (delete.ExecuteNonQuery() > 0)
            NotifyChanged();
    }

    private static CommandLauncherHistoryEntry InsertHistory(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long? favoriteId,
        string displayName,
        SafeLauncherCommand command,
        DateTimeOffset launchedAtUtc,
        CommandLauncherLaunchOutcome outcome)
    {
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO command_launcher_history (
                favorite_id, display_name, command_text, launch_kind, launched_at_utc, outcome)
            VALUES ($favoriteId, $name, $command, $kind, $launched, $outcome);
            SELECT last_insert_rowid();
            """;
        insert.Parameters.AddWithValue("$favoriteId", favoriteId is { } id ? id : DBNull.Value);
        insert.Parameters.AddWithValue("$name", displayName);
        insert.Parameters.AddWithValue("$command", command.CommandText);
        insert.Parameters.AddWithValue("$kind", command.Kind.ToString());
        insert.Parameters.AddWithValue("$launched", launchedAtUtc.ToString("O"));
        insert.Parameters.AddWithValue("$outcome", outcome.ToString());
        var historyId = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
        return new CommandLauncherHistoryEntry
        {
            Id = historyId,
            FavoriteId = favoriteId,
            DisplayName = displayName,
            CommandText = command.CommandText,
            Kind = command.Kind,
            LaunchedAtUtc = launchedAtUtc,
            Outcome = outcome,
        };
    }

    private static void PruneHistory(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var prune = connection.CreateCommand();
        prune.Transaction = transaction;
        prune.CommandText = """
            DELETE FROM command_launcher_history
            WHERE id NOT IN (
                SELECT id
                FROM command_launcher_history
                ORDER BY launched_at_utc DESC, id DESC
                LIMIT $limit)
            """;
        prune.Parameters.AddWithValue("$limit", MaximumHistoryEntries);
        prune.ExecuteNonQuery();
    }

    private static CommandLauncherFavorite? ReadFavoriteById(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, display_name, command_text, launch_kind,
                   created_at_utc, updated_at_utc, last_launched_at_utc, launch_count
            FROM command_launcher_favorites
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() && TryReadFavorite(reader, out var favorite)
            ? favorite
            : null;
    }

    private static bool TryReadFavorite(SqliteDataReader reader, out CommandLauncherFavorite favorite)
    {
        favorite = null!;
        try
        {
            if (reader.GetInt64(0) <= 0 ||
                !TryReadKind(reader.GetString(3), out var kind) ||
                !TryReadDate(reader.GetString(4), out var created) ||
                !TryReadDate(reader.GetString(5), out var updated))
            {
                return false;
            }

            DateTimeOffset? last = null;
            if (!reader.IsDBNull(6))
            {
                if (!TryReadDate(reader.GetString(6), out var parsedLast))
                    return false;
                last = parsedLast;
            }

            var displayName = reader.GetString(1);
            var commandText = reader.GetString(2);
            if (!IsStoredValueSafe(displayName, MaximumDisplayNameLength) ||
                !IsCanonicalCommandSafe(commandText, kind))
            {
                return false;
            }

            var count = reader.GetInt32(7);
            if (count < 0)
                return false;
            favorite = new CommandLauncherFavorite
            {
                Id = reader.GetInt64(0),
                DisplayName = displayName,
                CommandText = commandText,
                Kind = kind,
                CreatedAtUtc = created,
                UpdatedAtUtc = updated,
                LastLaunchedAtUtc = last,
                LaunchCount = count,
            };
            return true;
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException)
        {
            return false;
        }
    }

    private static bool TryReadHistory(SqliteDataReader reader, out CommandLauncherHistoryEntry entry)
    {
        entry = null!;
        try
        {
            if (reader.GetInt64(0) <= 0 ||
                !TryReadKind(reader.GetString(4), out var kind) ||
                !TryReadDate(reader.GetString(5), out var launchedAtUtc) ||
                !Enum.TryParse<CommandLauncherLaunchOutcome>(reader.GetString(6), false, out var outcome) ||
                !Enum.IsDefined(outcome))
            {
                return false;
            }

            var displayName = reader.GetString(2);
            var commandText = reader.GetString(3);
            if (!IsStoredValueSafe(displayName, MaximumDisplayNameLength) ||
                !IsCanonicalCommandSafe(commandText, kind))
            {
                return false;
            }

            entry = new CommandLauncherHistoryEntry
            {
                Id = reader.GetInt64(0),
                FavoriteId = reader.IsDBNull(1) ? null : reader.GetInt64(1),
                DisplayName = displayName,
                CommandText = commandText,
                Kind = kind,
                LaunchedAtUtc = launchedAtUtc,
                Outcome = outcome,
            };
            return true;
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException)
        {
            return false;
        }
    }

    private static SafeLauncherCommand NormalizePlan(LocalTerminalLaunchPlan? plan)
    {
        if (plan is null || !Enum.IsDefined(plan.Kind))
            throw new ArgumentException("A validated local connection plan is required.", nameof(plan));

        var commandText = plan.CanonicalCommand?.Trim() ?? "";
        if (!IsCanonicalCommandSafe(commandText, plan.Kind) ||
            plan.Arguments.Count > LocalTerminalLaunchPlanner.MaximumArgumentCount ||
            plan.Arguments.Any(argument => !IsStoredValueSafe(argument, LocalTerminalLaunchPlanner.MaximumArgumentLength)))
        {
            throw new ArgumentException(
                "The launcher command is unsupported or contains sensitive data.", nameof(plan));
        }

        return new SafeLauncherCommand(
            plan.Kind,
            commandText,
            $"{plan.Kind}\u001f{commandText}");
    }

    private static string NormalizeDisplayName(string? displayName, string commandText)
    {
        var normalized = string.IsNullOrWhiteSpace(displayName)
            ? commandText
            : displayName.Trim();
        if (normalized.Length > MaximumDisplayNameLength && string.IsNullOrWhiteSpace(displayName))
            normalized = $"{normalized[..(MaximumDisplayNameLength - 1)]}…";
        if (!IsStoredValueSafe(normalized, MaximumDisplayNameLength))
            throw new ArgumentException("The launcher name is invalid or contains sensitive data.", nameof(displayName));
        return normalized;
    }

    private static bool IsCanonicalCommandSafe(string value, LocalTerminalLaunchKind kind)
    {
        if (!IsStoredValueSafe(value, LocalTerminalLaunchPlanner.MaximumCommandLength) ||
            value.IndexOfAny(ShellSyntaxCharacters) >= 0)
        {
            return false;
        }

        var firstSpace = value.IndexOf(' ');
        var executable = firstSpace < 0 ? value : value[..firstSpace];
        if (executable.Length is < 1 or > 255 ||
            executable.IndexOfAny(['\\', '/', ':', '"']) >= 0 ||
            !executable.Equals(Path.GetFileName(executable), StringComparison.Ordinal))
        {
            return false;
        }

        return kind switch
        {
            LocalTerminalLaunchKind.DirectExecutable => true,
            LocalTerminalLaunchKind.OpenSsh => value.StartsWith("ssh ", StringComparison.OrdinalIgnoreCase),
            LocalTerminalLaunchKind.MultipassConnect => value.StartsWith("multipass connect ", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static bool IsStoredValueSafe(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
            return false;
        return !ContainsCredentialLikeValue(value);
    }

    private static bool ContainsCredentialLikeValue(string value)
    {
        var lower = value.ToLowerInvariant();
        if ((lower.Contains("-----begin", StringComparison.Ordinal) && lower.Contains("private key", StringComparison.Ordinal)) ||
            lower.Contains("bearer ", StringComparison.Ordinal))
        {
            return true;
        }

        var commandTokens = value.Split([' ', '\t', '"', '\'', ','], StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < commandTokens.Length; index++)
        {
            var token = commandTokens[index].Trim();
            if (token.Length == 0)
                continue;

            if (ContainsCredentialUserInfo(token))
                return true;

            var separator = token.IndexOf('=');
            var name = separator >= 0 ? token[..separator] : token;
            name = name.TrimStart('-', '/');
            var normalized = string.Concat(name.Where(char.IsLetterOrDigit));
            if (SensitiveArgumentNames.Contains(normalized) ||
                string.Equals(normalized, "bearer", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (CredentialPairArgumentNames.Contains(normalized))
            {
                var possibleValue = separator >= 0
                    ? token[(separator + 1)..]
                    : index + 1 < commandTokens.Length
                        ? commandTokens[index + 1]
                        : "";
                if (possibleValue.Contains(':', StringComparison.Ordinal))
                    return true;
            }

            foreach (var part in token.Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                var normalizedPart = string.Concat(part.TrimStart('-', '/').Where(char.IsLetterOrDigit));
                if (SensitiveArgumentNames.Contains(normalizedPart))
                    return true;
            }
        }
        return false;
    }

    private static bool ContainsCredentialUserInfo(string value)
    {
        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            var authorityStart = scheme + 3;
            var authorityEnd = value.IndexOfAny(['/', '?', '#'], authorityStart);
            if (authorityEnd < 0)
                authorityEnd = value.Length;
            var at = value.IndexOf('@', authorityStart, authorityEnd - authorityStart);
            var colon = value.IndexOf(':', authorityStart, authorityEnd - authorityStart);
            return at >= authorityStart && colon >= authorityStart && colon < at;
        }

        // SSH permits user@host, which remains safe here.  A colon before @ in
        // a simple endpoint is credential-shaped and is never retained.
        if (value.Contains('\\') || value.Contains('/'))
            return false;
        var endpointAt = value.IndexOf('@');
        var endpointColon = value.IndexOf(':');
        return endpointAt > 0 && endpointColon > 0 && endpointColon < endpointAt;
    }

    private static bool TryReadKind(string value, out LocalTerminalLaunchKind kind) =>
        Enum.TryParse(value, false, out kind) && Enum.IsDefined(kind);

    private static bool TryReadDate(string value, out DateTimeOffset date) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out date);

    internal static void NotifyDatabaseReset() => NotifyChanged();

    private static void NotifyChanged()
    {
        if (Changed is not { } callbacks)
            return;

        foreach (EventHandler callback in callbacks.GetInvocationList())
        {
            try
            {
                callback(null, EventArgs.Empty);
            }
            catch (Exception error)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Command launcher observer failed: {error.GetType().Name}");
            }
        }
    }

    private sealed record SafeLauncherCommand(
        LocalTerminalLaunchKind Kind,
        string CommandText,
        string Identity);
}
