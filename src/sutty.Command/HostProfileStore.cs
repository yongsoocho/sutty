using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace sutty.Command;

public static class HostEnvironments
{
    public const string Unclassified = "Unclassified";
    public const string Development = "Development";
    public const string Staging = "Staging";
    public const string Production = "Production";

    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "development" or "dev" => Development,
        "staging" or "stage" => Staging,
        "production" or "prod" => Production,
        _ => Unclassified,
    };
}

/// <summary>
/// A reusable, non-secret connection profile. Credential values live in the encrypted
/// local vault and are referenced only by <see cref="CredentialId"/>.
/// </summary>
public sealed class HostProfile
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; } = 22;
    public string Username { get; init; } = "";
    public string AuthMethod { get; init; } = "Password";
    public string PrivateKeyPath { get; init; } = "";
    public List<string> Tags { get; init; } = [];
    public string GroupName { get; init; } = "";
    public string Environment { get; init; } = HostEnvironments.Unclassified;
    public bool IsFavorite { get; init; }
    public string? CredentialId { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public DateTimeOffset? LastConnectedAtUtc { get; init; }
}

public sealed class HostProfileDraft
{
    public string DisplayName { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";
    public string AuthMethod { get; set; } = "Password";
    public string PrivateKeyPath { get; set; } = "";
    public IEnumerable<string>? Tags { get; set; }
    public string GroupName { get; set; } = "";
    public string Environment { get; set; } = HostEnvironments.Unclassified;
    public bool IsFavorite { get; set; }
    public string? CredentialId { get; set; }
}

/// <summary>
/// SQLite repository for saved hosts. Connection attempts remain append-only in
/// <see cref="HostHistoryStore"/>; this table represents explicit user-managed profiles.
/// </summary>
public static class HostProfileStore
{
    private const int MaxProfiles = 10_000;
    private const string LegacyPinsMigration = "host_profiles_from_legacy_pins_v1";
    private static readonly object Gate = new();
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_initialized) return;

            using var connection = Db.Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    CREATE TABLE IF NOT EXISTS host_profiles (
                        id                TEXT PRIMARY KEY,
                        display_name      TEXT NOT NULL,
                        host              TEXT NOT NULL,
                        port              INTEGER NOT NULL,
                        username          TEXT NOT NULL DEFAULT '',
                        auth_method       TEXT NOT NULL DEFAULT 'Password',
                        private_key_path  TEXT NOT NULL DEFAULT '',
                        tags_json         TEXT NOT NULL DEFAULT '[]',
                        group_name        TEXT NOT NULL DEFAULT '',
                        environment       TEXT NOT NULL DEFAULT 'Unclassified',
                        is_favorite       INTEGER NOT NULL DEFAULT 0,
                        credential_id     TEXT,
                        created_at_utc    TEXT NOT NULL,
                        updated_at_utc    TEXT NOT NULL,
                        last_connected_at_utc TEXT,
                        CHECK (port BETWEEN 1 AND 65535),
                        CHECK (is_favorite IN (0, 1))
                    );
                    CREATE INDEX IF NOT EXISTS idx_host_profiles_host
                        ON host_profiles(host COLLATE NOCASE, port);
                    CREATE INDEX IF NOT EXISTS idx_host_profiles_order
                        ON host_profiles(is_favorite DESC, last_connected_at_utc DESC, display_name COLLATE NOCASE);

                    CREATE TABLE IF NOT EXISTS storage_migrations (
                        id             TEXT PRIMARY KEY,
                        applied_at_utc TEXT NOT NULL
                    );
                    """;
                command.ExecuteNonQuery();
            }

            MigrateLegacyPins(connection);
            _initialized = true;
        }
    }

    public static HostProfile Save(HostProfileDraft draft, string? existingId = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        EnsureInitialized();

        var normalized = Normalize(draft);
        var id = string.IsNullOrWhiteSpace(existingId)
            ? Guid.NewGuid().ToString("N")
            : NormalizeId(existingId);

        using var connection = Db.Open();
        using var transaction = connection.BeginTransaction();

        if (string.IsNullOrWhiteSpace(existingId))
        {
            using var count = connection.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM host_profiles";
            if (Convert.ToInt32(count.ExecuteScalar()) >= MaxProfiles)
                throw new InvalidOperationException($"A maximum of {MaxProfiles} saved hosts is supported.");
        }

        var now = DateTimeOffset.UtcNow;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO host_profiles (
                    id, display_name, host, port, username, auth_method,
                    private_key_path, tags_json, group_name, environment,
                    is_favorite, credential_id, created_at_utc, updated_at_utc,
                    last_connected_at_utc)
                VALUES (
                    $id, $displayName, $host, $port, $username, $authMethod,
                    $privateKeyPath, $tags, $groupName, $environment,
                    $favorite, $credentialId, $now, $now, NULL)
                ON CONFLICT(id) DO UPDATE SET
                    display_name = excluded.display_name,
                    host = excluded.host,
                    port = excluded.port,
                    username = excluded.username,
                    auth_method = excluded.auth_method,
                    private_key_path = excluded.private_key_path,
                    tags_json = excluded.tags_json,
                    group_name = excluded.group_name,
                    environment = excluded.environment,
                    is_favorite = excluded.is_favorite,
                    credential_id = excluded.credential_id,
                    updated_at_utc = excluded.updated_at_utc
                """;
            AddDraftParameters(command, id, normalized, now);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return GetById(id) ?? throw new InvalidOperationException("The saved host could not be reloaded.");
    }

    public static HostProfile? GetById(string id)
    {
        EnsureInitialized();
        id = NormalizeId(id);
        using var connection = Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM host_profiles WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public static List<HostProfile> GetAll(string? query = null, int limit = 1_000)
    {
        EnsureInitialized();
        limit = Math.Clamp(limit, 1, MaxProfiles);
        query = query?.Trim() ?? "";

        using var connection = Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM host_profiles
            WHERE $query = ''
               OR display_name LIKE $pattern ESCAPE '\'
               OR host LIKE $pattern ESCAPE '\'
               OR username LIKE $pattern ESCAPE '\'
               OR group_name LIKE $pattern ESCAPE '\'
               OR tags_json LIKE $pattern ESCAPE '\'
            ORDER BY is_favorite DESC,
                     CASE WHEN last_connected_at_utc IS NULL THEN 1 ELSE 0 END,
                     last_connected_at_utc DESC,
                     display_name COLLATE NOCASE
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$query", query);
        command.Parameters.AddWithValue("$pattern", $"%{EscapeLike(query)}%");
        command.Parameters.AddWithValue("$limit", limit);

        var profiles = new List<HostProfile>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            profiles.Add(Read(reader));
        return profiles;
    }

    public static bool Delete(string id)
    {
        EnsureInitialized();
        id = NormalizeId(id);
        using var connection = Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM host_profiles WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    public static void SetFavorite(string id, bool favorite)
    {
        EnsureInitialized();
        id = NormalizeId(id);
        using var connection = Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE host_profiles
            SET is_favorite = $favorite, updated_at_utc = $now
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$favorite", favorite ? 1 : 0);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public static void MarkConnected(string id, DateTimeOffset? connectedAtUtc = null)
    {
        EnsureInitialized();
        id = NormalizeId(id);
        var now = connectedAtUtc ?? DateTimeOffset.UtcNow;
        using var connection = Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE host_profiles
            SET last_connected_at_utc = $connected, updated_at_utc = $connected
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$connected", now.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static void MigrateLegacyPins(SqliteConnection connection)
    {
        using (var applied = connection.CreateCommand())
        {
            applied.CommandText = "SELECT 1 FROM storage_migrations WHERE id = $id";
            applied.Parameters.AddWithValue("$id", LegacyPinsMigration);
            if (applied.ExecuteScalar() is not null) return;
        }

        using var transaction = connection.BeginTransaction();

        var legacy = new List<HostProfileDraft>();
        if (TableExists(connection, "host_pins", transaction))
        {
            using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = """
                    SELECT alias, hostname, username, port, auth_method,
                           private_key_path, tags_json
                    FROM host_pins
                    LIMIT 10000
                    """;
            using (var reader = read.ExecuteReader())
            {
                while (reader.Read())
                {
                    legacy.Add(new HostProfileDraft
                    {
                        DisplayName = reader.GetString(0),
                        Host = reader.GetString(1),
                        Username = reader.GetString(2),
                        Port = reader.GetInt32(3) is >= 1 and <= 65535 ? reader.GetInt32(3) : 22,
                        AuthMethod = reader.GetString(4),
                        PrivateKeyPath = reader.GetString(5),
                        Tags = DeserializeTags(reader.GetString(6)),
                        IsFavorite = true,
                    });
                }
            }
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var item in legacy)
        {
            HostProfileDraft normalized;
            try
            {
                normalized = Normalize(item);
            }
            catch (ArgumentException)
            {
                continue;
            }

            var identity = $"{normalized.Host.ToLowerInvariant()}:{normalized.Port}:{normalized.Username.ToLowerInvariant()}";
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
            var id = $"legacy_{hash[..24]}";

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO host_profiles (
                    id, display_name, host, port, username, auth_method,
                    private_key_path, tags_json, group_name, environment,
                    is_favorite, credential_id, created_at_utc, updated_at_utc,
                    last_connected_at_utc)
                VALUES (
                    $id, $displayName, $host, $port, $username, $authMethod,
                    $privateKeyPath, $tags, $groupName, $environment,
                    1, NULL, $now, $now, NULL)
                """;
            AddDraftParameters(insert, id, normalized, now);
            insert.ExecuteNonQuery();
        }

        using (var complete = connection.CreateCommand())
        {
            complete.Transaction = transaction;
            complete.CommandText = """
                INSERT INTO storage_migrations (id, applied_at_utc)
                VALUES ($id, $now)
                """;
            complete.Parameters.AddWithValue("$id", LegacyPinsMigration);
            complete.Parameters.AddWithValue("$now", now.ToString("O"));
            complete.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static bool TableExists(
        SqliteConnection connection,
        string table,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }

    private static void AddDraftParameters(
        SqliteCommand command,
        string id,
        HostProfileDraft draft,
        DateTimeOffset now)
    {
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$displayName", draft.DisplayName);
        command.Parameters.AddWithValue("$host", draft.Host);
        command.Parameters.AddWithValue("$port", draft.Port);
        command.Parameters.AddWithValue("$username", draft.Username);
        command.Parameters.AddWithValue("$authMethod", draft.AuthMethod);
        command.Parameters.AddWithValue("$privateKeyPath", draft.PrivateKeyPath);
        command.Parameters.AddWithValue("$tags", SerializeTags(draft.Tags));
        command.Parameters.AddWithValue("$groupName", draft.GroupName);
        command.Parameters.AddWithValue("$environment", draft.Environment);
        command.Parameters.AddWithValue("$favorite", draft.IsFavorite ? 1 : 0);
        command.Parameters.AddWithValue("$credentialId", (object?)draft.CredentialId ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
    }

    private static HostProfileDraft Normalize(HostProfileDraft draft)
    {
        var host = draft.Host?.Trim() ?? "";
        if (host.Length is < 1 or > 255 || host.Any(char.IsControl))
            throw new ArgumentException("A valid host name or address is required.", nameof(draft));

        var port = draft.Port is >= 1 and <= 65535 ? draft.Port : 22;
        var displayName = string.IsNullOrWhiteSpace(draft.DisplayName)
            ? host
            : draft.DisplayName.Trim();
        if (displayName.Length > 128 || displayName.Any(char.IsControl))
            throw new ArgumentException("The saved host name is invalid.", nameof(draft));

        var username = draft.Username?.Trim() ?? "";
        if (username.Length > 128 || username.Any(char.IsControl))
            throw new ArgumentException("The user name is invalid.", nameof(draft));

        var authMethod = string.Equals(draft.AuthMethod, "PublicKey", StringComparison.OrdinalIgnoreCase)
            ? "PublicKey"
            : "Password";
        var keyPath = authMethod == "PublicKey" ? draft.PrivateKeyPath?.Trim() ?? "" : "";
        if (keyPath.Length > 2_048 || keyPath.Any(char.IsControl))
            throw new ArgumentException("The private-key path is invalid.", nameof(draft));

        var group = draft.GroupName?.Trim() ?? "";
        if (group.Length > 64 || group.Any(char.IsControl))
            throw new ArgumentException("The group name is invalid.", nameof(draft));

        var credentialId = string.IsNullOrWhiteSpace(draft.CredentialId)
            ? null
            : NormalizeId(draft.CredentialId);

        return new HostProfileDraft
        {
            DisplayName = displayName,
            Host = host,
            Port = port,
            Username = username,
            AuthMethod = authMethod,
            PrivateKeyPath = keyPath,
            Tags = NormalizeTags(draft.Tags),
            GroupName = group,
            Environment = HostEnvironments.Normalize(draft.Environment),
            IsFavorite = draft.IsFavorite,
            CredentialId = credentialId,
        };
    }

    private static string NormalizeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("A saved-host id is required.", nameof(id));
        id = id.Trim();
        if (id.Length > 128 || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("The saved-host id contains unsupported characters.", nameof(id));
        return id;
    }

    private static List<string> NormalizeTags(IEnumerable<string>? tags) =>
        tags?.Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Where(tag => tag.Length <= 32 && !tag.Any(char.IsControl))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToList()
        ?? [];

    private static string SerializeTags(IEnumerable<string>? tags) =>
        JsonSerializer.Serialize(NormalizeTags(tags), CommandJsonContext.Default.ListString);

    private static List<string> DeserializeTags(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        try
        {
            return NormalizeTags(JsonSerializer.Deserialize(value, CommandJsonContext.Default.ListString));
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static HostProfile Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        DisplayName = reader.GetString(1),
        Host = reader.GetString(2),
        Port = reader.GetInt32(3),
        Username = reader.GetString(4),
        AuthMethod = reader.GetString(5),
        PrivateKeyPath = reader.GetString(6),
        Tags = DeserializeTags(reader.GetString(7)),
        GroupName = reader.GetString(8),
        Environment = HostEnvironments.Normalize(reader.GetString(9)),
        IsFavorite = reader.GetInt32(10) != 0,
        CredentialId = reader.IsDBNull(11) ? null : reader.GetString(11),
        CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(12)),
        UpdatedAtUtc = DateTimeOffset.Parse(reader.GetString(13)),
        LastConnectedAtUtc = reader.IsDBNull(14) ? null : DateTimeOffset.Parse(reader.GetString(14)),
    };

    private const string SelectColumns = """
        id, display_name, host, port, username, auth_method,
        private_key_path, tags_json, group_name, environment,
        is_favorite, credential_id, created_at_utc, updated_at_utc,
        last_connected_at_utc
        """;
}
