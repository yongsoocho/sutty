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
/// Persists bounded local launch history. Favorites live in HostProfileStore.
/// Input is accepted only as a Core planner-produced plan, so this
/// store never parses arbitrary shell text or persists a resolved executable,
/// output, token, password, passphrase, or private-key contents.
/// </summary>
public static class CommandLauncherStore
{
    public const int MaximumHistoryEntries = 250;
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

    /// <summary>Raised after launch history changes.</summary>
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

    internal static SafeLauncherCommand NormalizePlan(LocalTerminalLaunchPlan? plan)
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

        return ValidateStoredCommand(commandText, plan.Kind.ToString());
    }

    internal static SafeLauncherCommand ValidateStoredCommand(string text, string kindText)
    {
        if (!Enum.TryParse<LocalTerminalLaunchKind>(kindText, false, out var kind) || !Enum.IsDefined(kind))
            throw new ArgumentException("The external launch type is unsupported.");
        var definition = LocalTerminalLaunchPlanner.ValidateCommand(text);
        if (definition.Kind != kind || !IsCanonicalCommandSafe(definition.CanonicalCommand, kind))
            throw new ArgumentException("The command is invalid or contains sensitive data.");
        return new SafeLauncherCommand(kind, definition.CanonicalCommand, $"{kind}\u001f{definition.CanonicalCommand}");
    }

    internal static string NormalizeDisplayName(string? displayName, string commandText)
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

    internal static bool IsCanonicalCommandSafe(string value, LocalTerminalLaunchKind kind)
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

    internal sealed record SafeLauncherCommand(
        LocalTerminalLaunchKind Kind,
        string CommandText,
        string Identity);
}
