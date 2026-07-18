namespace sutty.Command;

/// <summary>접속 기록 한 건 (append-only 로그의 행 하나 또는 TOP 집계 결과).</summary>
public sealed class HostHistoryEntry
{
    public long Id { get; set; }
    public string Alias { get; set; } = "";
    public string Hostname { get; set; } = "";
    public DateTime? ConnectedAt { get; set; }

    /// <summary>TOP 집계 조회일 때만 채워짐 (이 호스트의 총 접속 횟수).</summary>
    public int ConnectionCount { get; set; }

    /// <summary>샘플/데모 항목이면 true → 연결 시 mock 세션 사용.</summary>
    public bool IsMock { get; set; }
}

/// <summary>
/// SSH 접속 기록을 SQLite(connection_log)에 append-only로 저장한다.
/// - 접속할 때마다 새 행 추가 (같은 서버라도 기존 행은 그대로)
/// - 보관 기한(기본 60일)이 지난 행은 Purge로 삭제
/// - GetTop: 접속 횟수가 많은 호스트 TOP N 집계 (History 상단 고정용)
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
                        id           INTEGER PRIMARY KEY AUTOINCREMENT,
                        alias        TEXT    NOT NULL,
                        hostname     TEXT    NOT NULL,
                        connected_at TEXT    NOT NULL,
                        is_mock      INTEGER NOT NULL DEFAULT 0
                    );
                    CREATE INDEX IF NOT EXISTS idx_log_hostname ON connection_log(hostname);
                    CREATE INDEX IF NOT EXISTS idx_log_connected ON connection_log(connected_at);
                    """;
                create.ExecuteNonQuery();
            }

            // 비어 있으면 데모 기록을 심는다 (횟수 차이를 줘서 TOP 정렬이 보이게)
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

    /// <summary>접속할 때마다 호출 — 항상 새 행을 추가한다 (append-only).</summary>
    public static void Append(string alias, string hostname, bool isMock = false)
    {
        if (string.IsNullOrWhiteSpace(hostname)) return;

        EnsureInitialized();
        using var conn = Db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO connection_log (alias, hostname, connected_at, is_mock)
            VALUES ($alias, $host, $now, $mock)
            """;
        cmd.Parameters.AddWithValue("$alias", string.IsNullOrWhiteSpace(alias) ? hostname : alias);
        cmd.Parameters.AddWithValue("$host", hostname);
        cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("o"));
        cmd.Parameters.AddWithValue("$mock", isMock ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    /// <summary>보관 기한이 지난 기록 삭제. (retentionDays일보다 오래된 행)</summary>
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

    /// <summary>접속 횟수 TOP N 호스트 (동률이면 최근 접속 순). alias는 가장 최근 기록의 것.</summary>
    public static List<HostHistoryEntry> GetTop(int topN)
    {
        if (topN <= 0) return [];

        EnsureInitialized();
        using var conn = Db.Open();
        using var cmd = conn.CreateCommand();
        // SQLite 특성: MAX()를 쓰면 bare 컬럼(alias, is_mock)은 그 최대값 행에서 온다
        cmd.CommandText = """
            SELECT hostname, alias, is_mock, COUNT(*) AS cnt, MAX(connected_at) AS last
            FROM connection_log
            GROUP BY hostname
            ORDER BY cnt DESC, last DESC
            LIMIT $top
            """;
        cmd.Parameters.AddWithValue("$top", topN);

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
                ConnectedAt = DateTime.Parse(reader.GetString(4)),
            });
        }
        return result;
    }

    /// <summary>최근 접속 기록 (최신순, append-only 로그 그대로).</summary>
    public static List<HostHistoryEntry> GetRecent(int limit = 200)
    {
        EnsureInitialized();
        using var conn = Db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, alias, hostname, connected_at, is_mock
            FROM connection_log
            ORDER BY connected_at DESC, id DESC
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
            });
        }
        return result;
    }

    private static void Seed(Microsoft.Data.Sqlite.SqliteConnection conn, string alias, string hostname, DateTime at)
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
