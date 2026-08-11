using Microsoft.Data.Sqlite;

namespace sutty.Command;

/// <summary>sutty 로컬 SQLite 파일 하나를 공유한다 (commands + host_history).</summary>
internal static class Db
{
    public static string DbPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "sutty", "sutty.db");

    public static SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        return conn;
    }
}
