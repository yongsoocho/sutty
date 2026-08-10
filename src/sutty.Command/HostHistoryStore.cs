using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace sutty.Command;

/// <summary>접속 기록 한 건 또는 사용자가 고정한 호스트의 집계 결과.</summary>
public sealed class HostHistoryEntry
{
    public long Id { get; set; }
    public string Alias { get; set; } = "";
    public string Hostname { get; set; } = "";
    public DateTime? ConnectedAt { get; set; }

    /// <summary>고정 호스트 집계 조회일 때 채워짐 (이 호스트의 총 접속 횟수).</summary>
    public int ConnectionCount { get; set; }

    /// <summary>샘플/데모 항목이면 true → 연결 시 mock 세션 사용.</summary>
    public bool IsMock { get; set; }

    /// <summary>Whether this host was explicitly pinned by the user.</summary>
    public bool IsPinned { get; set; }

    // 비밀번호와 passphrase를 제외한, Home 폼을 다시 채우기 위한 연결 초안.
    public string Username { get; set; } = "";
    public int Port { get; set; } = 22;
    public string AuthMethod { get; set; } = "Password";
    public string PrivateKeyPath { get; set; } = "";
    public List<string> Tags { get; set; } = [];
}

/// <summary>
/// SSH 접속 기록을 SQLite(connection_log)에 append-only로 저장한다.
/// 비밀번호와 key passphrase는 저장하지 않으며, 재사용 가능한 비밀 아닌 연결 초안만 보관한다.
/// </summary>
public static class HostHistoryStore
{
    private static readonly object Gate = new();
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_initialized) return;

            using var conn = Db.Open();
            using (var create = conn.CreateCommand())
            {
                create.CommandText = """
                    CREATE TABLE IF NOT EXISTS connection_log (
                        id               INTEGER PRIMARY KEY AUTOINCREMENT,
                        alias            TEXT    NOT NULL,
                        hostname         TEXT    NOT NULL,
                        connected_at     TEXT    NOT NULL,
                        is_mock          INTEGER NOT NULL DEFAULT 0,
                        username         TEXT    NOT NULL DEFAULT '',
                        port             INTEGER NOT NULL DEFAULT 22,
                        auth_method      TEXT    NOT NULL DEFAULT 'Password',
                        private_key_path TEXT    NOT NULL DEFAULT '',
                        tags_json        TEXT    NOT NULL DEFAULT '[]'
                    );
                    CREATE INDEX IF NOT EXISTS idx_log_hostname ON connection_log(hostname);
                    CREATE INDEX IF NOT EXISTS idx_log_connected ON connection_log(connected_at);

                    CREATE TABLE IF NOT EXISTS host_pins (
                        hostname         TEXT PRIMARY KEY COLLATE NOCASE,
                        alias            TEXT    NOT NULL,
                        pinned_at        TEXT    NOT NULL,
                        is_mock          INTEGER NOT NULL DEFAULT 0,
                        username         TEXT    NOT NULL DEFAULT '',
                        port             INTEGER NOT NULL DEFAULT 0,
                        auth_method      TEXT    NOT NULL DEFAULT '',
                        private_key_path TEXT    NOT NULL DEFAULT '',
                        tags_json        TEXT    NOT NULL DEFAULT ''
                    );
                    CREATE INDEX IF NOT EXISTS idx_pins_pinned_at ON host_pins(pinned_at);
                    """;
                create.ExecuteNonQuery();
            }

            // Existing installations keep every row. SQLite ADD COLUMN supplies safe
            // defaults for old records, and a partially completed migration is resumable.
            EnsureColumn(conn, "connection_log", "username", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(conn, "connection_log", "port", "INTEGER NOT NULL DEFAULT 22");
            EnsureColumn(conn, "connection_log", "auth_method", "TEXT NOT NULL DEFAULT 'Password'");
            EnsureColumn(conn, "connection_log", "private_key_path", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(conn, "connection_log", "tags_json", "TEXT NOT NULL DEFAULT '[]'");

            EnsureColumn(conn, "host_pins", "username", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(conn, "host_pins", "port", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(conn, "host_pins", "auth_method", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(conn, "host_pins", "private_key_path", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(conn, "host_pins", "tags_json", "TEXT NOT NULL DEFAULT ''");

            // 비어 있으면 History/Mock 연결을 체험할 수 있는 데모 기록을 심는다.
            using (var count = conn.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM connection_log";
                if (Convert.ToInt64(count.ExecuteScalar()) == 0)
                {
                    Seed(conn, "Dev Scheduler", "10.0.0.15", DateTime.Now.AddHours(-3));
                    Seed(conn, "Dev Scheduler", "10.0.0.15", DateTime.Now.AddDays(-1));
                    Seed(conn, "Dev Scheduler", "10.0.0.15", DateTime.Now.AddDays(-3));
                    Seed(conn, "Web Server us-1", "10.0.1.22", DateTime.Now.AddHours(-2));
                    Seed(conn, "Web Server us-1", "10.0.1.22", DateTime.Now.AddDays(-2));
                    Seed(conn, "Postgresql Replica-1", "10.0.2.10", DateTime.Now.AddDays(-1));
                    Seed(conn, "Web Server us-0", "10.0.1.20", DateTime.Now.AddDays(-5));
                }
            }

            _initialized = true;
        }
    }

    /// <summary>접속 시도마다 비밀을 제외한 연결 초안을 새 행으로 보관한다.</summary>
    public static void Append(
        string alias,
        string hostname,
        bool isMock = false,
        string username = "",
        int port = 22,
        string authMethod = "Password",
        string privateKeyPath = "",
        IEnumerable<string>? tags = null)
    {
        if (string.IsNullOrWhiteSpace(hostname)) return;

        var host = hostname.Trim();
        var auth = NormalizeAuthMethod(authMethod);

        EnsureInitialized();
        using var conn = Db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO connection_log (
                alias, hostname, connected_at, is_mock,
                username, port, auth_method, private_key_path, tags_json)
            VALUES (
                $alias, $host, $now, $mock,
                $username, $port, $auth, $keyPath, $tags);

            UPDATE host_pins
            SET alias = $alias,
                is_mock = $mock,
                username = $username,
                port = $port,
                auth_method = $auth,
                private_key_path = $keyPath,
                tags_json = $tags
            WHERE hostname = $host COLLATE NOCASE
            """;
        cmd.Parameters.AddWithValue("$alias", string.IsNullOrWhiteSpace(alias) ? host : alias.Trim());
        cmd.Parameters.AddWithValue("$host", host);
        cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("o"));
        cmd.Parameters.AddWithValue("$mock", isMock ? 1 : 0);
        cmd.Parameters.AddWithValue("$username", username?.Trim() ?? "");
        cmd.Parameters.AddWithValue("$port", NormalizePort(port));
        cmd.Parameters.AddWithValue("$auth", auth);
        cmd.Parameters.AddWithValue("$keyPath",
            auth == "PublicKey" ? privateKeyPath?.Trim() ?? "" : "");
        cmd.Parameters.AddWithValue("$tags", SerializeTags(tags));
        cmd.ExecuteNonQuery();
    }

    /// <summary>보관 기한이 지난 기록 삭제. 사용자가 고정한 초안은 별도 테이블에 남는다.</summary>
    public static void Purge(int retentionDays)
    {
        if (retentionDays <= 0) return;

        EnsureInitialized();
        using var conn = Db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM connection_log WHERE connected_at < $cutoff";
        cmd.Parameters.AddWithValue("$cutoff", DateTime.Now.AddDays(-retentionDays).ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Returns hosts explicitly pinned by the user, most recently pinned first.</summary>
    public static List<HostHistoryEntry> GetPinned()
    {
        EnsureInitialized();
        using var conn = Db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT p.hostname,
                   p.alias,
                   p.is_mock,
                   COALESCE(stats.cnt, 0) AS cnt,
                   stats.last,
                   COALESCE(NULLIF(p.username, ''), latest.username, '') AS username,
                   CASE
                       WHEN p.port BETWEEN 1 AND 65535 THEN p.port
                       ELSE COALESCE(latest.port, 22)
                   END AS port,
                   COALESCE(NULLIF(p.auth_method, ''), NULLIF(latest.auth_method, ''), 'Password') AS auth_method,
                   COALESCE(NULLIF(p.private_key_path, ''), latest.private_key_path, '') AS private_key_path,
                   COALESCE(NULLIF(p.tags_json, ''), NULLIF(latest.tags_json, ''), '[]') AS tags_json
            FROM host_pins AS p
            LEFT JOIN (
                SELECT hostname COLLATE NOCASE AS hostname_key,
                       COUNT(*) AS cnt,
                       MAX(connected_at) AS last
                FROM connection_log
                GROUP BY hostname COLLATE NOCASE
            ) AS stats
              ON stats.hostname_key = p.hostname COLLATE NOCASE
            LEFT JOIN connection_log AS latest
              ON latest.id = (
                  SELECT l.id
                  FROM connection_log AS l
                  WHERE l.hostname = p.hostname COLLATE NOCASE
                  ORDER BY l.connected_at DESC, l.id DESC
                  LIMIT 1
              )
            ORDER BY p.pinned_at DESC
            """;

        var result = new List<HostHistoryEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new HostHistoryEntry
            {
                Hostname = reader.GetString(0),
                Alias = reader.GetString(1),
                IsMock = reader.GetInt32(2) != 0,
                ConnectionCount = reader.GetInt32(3),
                ConnectedAt = reader.IsDBNull(4) ? null : DateTime.Parse(reader.GetString(4)),
                Username = reader.GetString(5),
                Port = NormalizePort(reader.GetInt32(6)),
                AuthMethod = NormalizeAuthMethod(reader.GetString(7)),
                PrivateKeyPath = reader.GetString(8),
                Tags = DeserializeTags(reader.GetString(9)),
                IsPinned = true,
            });
        }
        return result;
    }

    /// <summary>Adds or removes a user-managed pin for a host and its non-secret draft.</summary>
    public static void SetPinned(
        string hostname,
        string alias,
        bool isMock,
        bool isPinned,
        string username = "",
        int port = 22,
        string authMethod = "Password",
        string privateKeyPath = "",
        IEnumerable<string>? tags = null)
    {
        if (string.IsNullOrWhiteSpace(hostname)) return;

        var host = hostname.Trim();
        var auth = NormalizeAuthMethod(authMethod);

        EnsureInitialized();
        using var conn = Db.Open();
        using var cmd = conn.CreateCommand();

        if (isPinned)
        {
            cmd.CommandText = """
                INSERT INTO host_pins (
                    hostname, alias, pinned_at, is_mock,
                    username, port, auth_method, private_key_path, tags_json)
                VALUES (
                    $host, $alias, $now, $mock,
                    $username, $port, $auth, $keyPath, $tags)
                ON CONFLICT(hostname) DO UPDATE SET
                    alias = excluded.alias,
                    pinned_at = excluded.pinned_at,
                    is_mock = excluded.is_mock,
                    username = excluded.username,
                    port = excluded.port,
                    auth_method = excluded.auth_method,
                    private_key_path = excluded.private_key_path,
                    tags_json = excluded.tags_json
                """;
            cmd.Parameters.AddWithValue("$host", host);
            cmd.Parameters.AddWithValue("$alias", string.IsNullOrWhiteSpace(alias) ? host : alias.Trim());
            cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("o"));
            cmd.Parameters.AddWithValue("$mock", isMock ? 1 : 0);
            cmd.Parameters.AddWithValue("$username", username?.Trim() ?? "");
            cmd.Parameters.AddWithValue("$port", NormalizePort(port));
            cmd.Parameters.AddWithValue("$auth", auth);
            cmd.Parameters.AddWithValue("$keyPath",
                auth == "PublicKey" ? privateKeyPath?.Trim() ?? "" : "");
            cmd.Parameters.AddWithValue("$tags", SerializeTags(tags));
        }
        else
        {
            cmd.CommandText = "DELETE FROM host_pins WHERE hostname = $host COLLATE NOCASE";
            cmd.Parameters.AddWithValue("$host", host);
        }

        cmd.ExecuteNonQuery();
    }

    /// <summary>최근 접속 기록 (최신순, append-only 로그 그대로).</summary>
    public static List<HostHistoryEntry> GetRecent(int limit = 200)
    {
        EnsureInitialized();
        using var conn = Db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT l.id,
                   l.alias,
                   l.hostname,
                   l.connected_at,
                   l.is_mock,
                   EXISTS (
                       SELECT 1
                       FROM host_pins AS p
                       WHERE p.hostname = l.hostname COLLATE NOCASE
                   ) AS is_pinned,
                   l.username,
                   l.port,
                   l.auth_method,
                   l.private_key_path,
                   l.tags_json
            FROM connection_log AS l
            ORDER BY l.connected_at DESC, l.id DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", limit);

        var result = new List<HostHistoryEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new HostHistoryEntry
            {
                Id = reader.GetInt64(0),
                Alias = reader.GetString(1),
                Hostname = reader.GetString(2),
                ConnectedAt = DateTime.Parse(reader.GetString(3)),
                IsMock = reader.GetInt32(4) != 0,
                IsPinned = reader.GetInt32(5) != 0,
                Username = reader.GetString(6),
                Port = NormalizePort(reader.GetInt32(7)),
                AuthMethod = NormalizeAuthMethod(reader.GetString(8)),
                PrivateKeyPath = reader.GetString(9),
                Tags = DeserializeTags(reader.GetString(10)),
            });
        }
        return result;
    }

    private static void EnsureColumn(
        SqliteConnection conn,
        string table,
        string column,
        string definition)
    {
        var exists = false;
        using (var inspect = conn.CreateCommand())
        {
            inspect.CommandText = $"PRAGMA table_info(\"{table}\")";
            using var reader = inspect.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (exists) return;

        // table/column/definition are private compile-time constants from the calls above.
        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}";
        alter.ExecuteNonQuery();
    }

    private static int NormalizePort(int port) => port is >= 1 and <= 65535 ? port : 22;

    private static string NormalizeAuthMethod(string? authMethod) =>
        string.Equals(authMethod, "PublicKey", StringComparison.OrdinalIgnoreCase)
            ? "PublicKey"
            : "Password";

    private static List<string> NormalizeTags(IEnumerable<string>? tags) =>
        tags?.Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Where(tag => tag.Length <= 32)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList()
        ?? [];

    private static string SerializeTags(IEnumerable<string>? tags) =>
        JsonSerializer.Serialize(NormalizeTags(tags));

    private static List<string> DeserializeTags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            return NormalizeTags(JsonSerializer.Deserialize<List<string>>(json));
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void Seed(SqliteConnection conn, string alias, string hostname, DateTime at)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO connection_log (alias, hostname, connected_at, is_mock)
            VALUES ($alias, $host, $at, 1)
            """;
        cmd.Parameters.AddWithValue("$alias", alias);
        cmd.Parameters.AddWithValue("$host", hostname);
        cmd.Parameters.AddWithValue("$at", at.ToString("o"));
        cmd.ExecuteNonQuery();
    }
}
