using Microsoft.Data.Sqlite;

namespace sutty.Command;

/// <summary>
/// %LOCALAPPDATA%\sutty\sutty.db (SQLite)에 명령어 playbook을 저장한다.
/// 설정(단일 객체)은 JSON, 행이 늘어나는 명령어 목록은 SQLite — 용도에 맞게 분리.
/// </summary>
public static class CommandStore
{
    private static readonly object Gate = new();
    private static bool _initialized;

    private static SqliteConnection Open() => Db.Open();

    public static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_initialized) return;

            using var conn = Open();
            using (var create = conn.CreateCommand())
            {
                create.CommandText = """
                    CREATE TABLE IF NOT EXISTS commands (
                        id           INTEGER PRIMARY KEY AUTOINCREMENT,
                        name         TEXT    NOT NULL,
                        command_text TEXT    NOT NULL,
                        usage_count  INTEGER NOT NULL DEFAULT 0,
                        created_at   TEXT    NOT NULL,
                        last_used_at TEXT
                    );
                    """;
                create.ExecuteNonQuery();
            }

            // 비어 있으면 예시 몇 개를 심어 준다
            using (var count = conn.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM commands";
                if (Convert.ToInt64(count.ExecuteScalar()) == 0)
                {
                    InsertInternal(conn, "Create LV", "lvcreate -n $1 -L $2");
                    InsertInternal(conn, "Find big files", "du -ah $1 | sort -rh | head -n 20");
                    InsertInternal(conn, "Service status", "systemctl status $1");
                }
            }

            _initialized = true;
        }
    }

    /// <summary>자주 쓰는 순(usage_count DESC) → 이름순으로 반환.</summary>
    public static List<CommandTemplate> GetAll()
    {
        EnsureInitialized();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, command_text, usage_count, created_at, last_used_at
            FROM commands
            ORDER BY usage_count DESC, name COLLATE NOCASE
            """;

        var result = new List<CommandTemplate>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new CommandTemplate
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                CommandText = reader.GetString(2),
                UsageCount = reader.GetInt32(3),
                CreatedAt = DateTime.Parse(reader.GetString(4)),
                LastUsedAt = reader.IsDBNull(5) ? null : DateTime.Parse(reader.GetString(5)),
            });
        }
        return result;
    }

    public static CommandTemplate Add(string name, string commandText)
    {
        EnsureInitialized();
        using var conn = Open();
        var id = InsertInternal(conn, name, commandText);
        return new CommandTemplate
        {
            Id = id,
            Name = name,
            CommandText = commandText,
            CreatedAt = DateTime.Now,
        };
    }

    public static void Delete(long id)
    {
        EnsureInitialized();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM commands WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>실행할 때마다 호출 — 다음에 목록 상단으로 올라온다.</summary>
    public static void IncrementUsage(long id)
    {
        EnsureInitialized();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE commands SET usage_count = usage_count + 1, last_used_at = $now WHERE id = $id";
        cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("o"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private static long InsertInternal(SqliteConnection conn, string name, string commandText)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO commands (name, command_text, created_at) VALUES ($name, $text, $now);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$text", commandText);
        cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("o"));
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
