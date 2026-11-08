using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Yamaori.Store;

/// <summary>
/// Yamaori の DB（SQLite 1 ファイル）。表の版は PRAGMA user_version で持ち、開くときに上げる。
/// 時刻は UTC の「yyyy-MM-ddTHH:mm:ssZ」の文字列で持つ（並べ替えがそのまま時刻の順になる）。
/// </summary>
public sealed class Database : IDisposable
{
    public SqliteConnection Connection { get; }

    Database(SqliteConnection connection) => Connection = connection;

    public static Database Open(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir != null) Directory.CreateDirectory(dir);
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        c.Open();
        Exec(c, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=OFF;");
        var db = new Database(c);
        db.Migrate();
        return db;
    }

    void Migrate()
    {
        var version = Convert.ToInt32(Scalar("PRAGMA user_version"), CultureInfo.InvariantCulture);
        for (var v = version; v < Migrations.Length; v++)
        {
            using var tx = Connection.BeginTransaction();
            Exec(Connection, Migrations[v], tx);
            Exec(Connection, $"PRAGMA user_version={v + 1}", tx);
            tx.Commit();
        }
    }

    /// <summary>表の版ごとの SQL。足すときは末尾に足し、前のものは変えない。</summary>
    static readonly string[] Migrations =
    [
        """
        CREATE TABLE log_files (
            name TEXT PRIMARY KEY,         -- output_log_….txt（パスでなく名前）
            path TEXT NOT NULL,
            length INTEGER NOT NULL,       -- 前に読んだときのファイルの長さ
            offset INTEGER NOT NULL,       -- 次に読む位置
            time_zone TEXT NOT NULL,       -- ログの時刻を UTC に直すタイムゾーン
            last_entry_local TEXT,         -- 最後の件の時刻（ローカル）
            closed INTEGER NOT NULL DEFAULT 0
        );
        -- ログから読んだ出来事のそのまま。読み方を直したら、ここから組み立て直す
        CREATE TABLE log_events (
            log_file TEXT NOT NULL,
            offset INTEGER NOT NULL,
            at_local TEXT NOT NULL,
            kind TEXT NOT NULL,
            data TEXT NOT NULL,            -- JSON
            PRIMARY KEY (log_file, offset)
        );
        CREATE TABLE sessions (
            log_file TEXT PRIMARY KEY,
            started_at TEXT,
            ended_at TEXT,
            end_reason TEXT,
            self_id TEXT,
            self_name TEXT,
            vr INTEGER,
            hmd TEXT
        );
        CREATE TABLE worlds (
            id TEXT PRIMARY KEY,
            name TEXT
        );
        CREATE TABLE visits (
            id TEXT PRIMARY KEY,
            source TEXT NOT NULL,          -- log:<ファイル名>、vat:…、vrcx:…
            world_id TEXT NOT NULL,
            world_name TEXT,
            instance TEXT NOT NULL,
            instance_number TEXT NOT NULL,
            access TEXT NOT NULL,
            owner_id TEXT,
            group_id TEXT,
            region TEXT NOT NULL,
            joined_at TEXT NOT NULL,
            left_at TEXT,
            left_reason TEXT
        );
        CREATE INDEX visits_joined ON visits (joined_at);
        CREATE INDEX visits_world ON visits (world_id);
        CREATE INDEX visits_source ON visits (source);
        CREATE TABLE encounters (
            id TEXT PRIMARY KEY,
            source TEXT NOT NULL,
            visit_id TEXT NOT NULL,
            user_id TEXT,
            name TEXT NOT NULL,
            joined_at TEXT NOT NULL,
            left_at TEXT
        );
        CREATE INDEX encounters_visit ON encounters (visit_id);
        CREATE INDEX encounters_user ON encounters (user_id);
        CREATE INDEX encounters_source ON encounters (source);
        CREATE TABLE events (
            id TEXT PRIMARY KEY,
            source TEXT NOT NULL,
            visit_id TEXT,
            at TEXT NOT NULL,
            kind TEXT NOT NULL,            -- video・avatar・notification・screenshot
            user_id TEXT,
            name TEXT,
            data TEXT NOT NULL             -- JSON
        );
        CREATE INDEX events_at ON events (at);
        CREATE INDEX events_visit ON events (visit_id);
        CREATE INDEX events_source ON events (source);
        -- 会った人の一覧（名前は最後に見たもの）
        CREATE VIEW people AS
            SELECT user_id,
                   (SELECT e2.name FROM encounters e2 WHERE e2.user_id = e.user_id ORDER BY e2.joined_at DESC LIMIT 1) AS name,
                   MIN(joined_at) AS first_met_at,
                   MAX(COALESCE(left_at, joined_at)) AS last_met_at,
                   COUNT(DISTINCT visit_id) AS visit_count
            FROM encounters e
            WHERE user_id IS NOT NULL
            GROUP BY user_id;
        """,
    ];

    public object? Scalar(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(sql, null, args);
        return cmd.ExecuteScalar();
    }

    public SqliteCommand Command(string sql, SqliteTransaction? tx = null, params (string Name, object? Value)[] args)
    {
        var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    static void Exec(SqliteConnection c, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }

    public static string Time(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    public static string? Time(DateTime? utc) => utc is { } t ? Time(t) : null;
    public static DateTime ParseTime(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
    public static string Local(DateTime local) => local.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
    public static DateTime ParseLocal(string s) => DateTime.ParseExact(s, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    public void Dispose() => Connection.Dispose();
}
