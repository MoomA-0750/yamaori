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
        // 常駐の見張りと Web の要求が別々の接続で同時に使うので、書き込みが重なったら少し待つ
        Exec(c, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=OFF; PRAGMA busy_timeout=5000;");
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

    /// <summary>表の版ごとの SQL。足すときは末尾に足し、前のものは変えない（公開前の 2026-11-08 に、端末を入れるため 1 版目だけ書き換えた）。</summary>
    static readonly string[] Migrations =
    [
        """
        -- 記録を集めた端末。is_self はこの DB を持つ端末
        CREATE TABLE devices (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            is_self INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL
        );
        CREATE TABLE log_files (
            device_id TEXT NOT NULL,
            name TEXT NOT NULL,            -- output_log_….txt（パスでなく名前）
            path TEXT NOT NULL,
            length INTEGER NOT NULL,       -- 前に読んだときのファイルの長さ
            offset INTEGER NOT NULL,       -- 次に読む位置
            time_zone TEXT NOT NULL,       -- ログの時刻を UTC に直すタイムゾーン
            last_entry_local TEXT,         -- 最後の件の時刻（ローカル）
            closed INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (device_id, name)
        );
        -- ログから読んだ出来事のそのまま。読み方を直したら、ここから組み立て直す
        CREATE TABLE log_events (
            device_id TEXT NOT NULL,
            log_file TEXT NOT NULL,
            offset INTEGER NOT NULL,
            at_local TEXT NOT NULL,
            kind TEXT NOT NULL,
            data TEXT NOT NULL,            -- JSON
            PRIMARY KEY (device_id, log_file, offset)
        );
        CREATE TABLE sessions (
            device_id TEXT NOT NULL,
            log_file TEXT NOT NULL,
            started_at TEXT,
            ended_at TEXT,
            end_reason TEXT,
            self_id TEXT,
            self_name TEXT,
            vr INTEGER,
            hmd TEXT,
            PRIMARY KEY (device_id, log_file)
        );
        CREATE TABLE worlds (
            id TEXT PRIMARY KEY,
            name TEXT
        );
        CREATE TABLE visits (
            id TEXT PRIMARY KEY,
            source TEXT NOT NULL,          -- log:<端末>/<ファイル名>、vat:…、vrcx:…
            device_id TEXT,                -- どの端末の記録か
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
            device_id TEXT,
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
            device_id TEXT,
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
        // 2 版目: どのアカウントで行ったか（複数のアカウントで遊ぶ人のため）。入れたあと rebuild で埋まる
        """
        ALTER TABLE visits ADD COLUMN account_id TEXT;
        ALTER TABLE events ADD COLUMN account_id TEXT;
        CREATE INDEX visits_account ON visits (account_id);
        -- 遊んだアカウントの一覧（名前は最後にログインしたときのもの）
        CREATE VIEW accounts AS
            SELECT self_id AS id,
                   (SELECT s2.self_name FROM sessions s2 WHERE s2.self_id = s.self_id ORDER BY s2.started_at DESC LIMIT 1) AS name,
                   MIN(started_at) AS first_seen_at,
                   MAX(COALESCE(ended_at, started_at)) AS last_seen_at
            FROM sessions s
            WHERE self_id IS NOT NULL
            GROUP BY self_id;
        -- 会った人の名前の移り変わり（表示名を変えた人も、ID で同じ人として束ねる）
        CREATE VIEW person_names AS
            SELECT user_id, name, MIN(joined_at) AS first_seen_at, MAX(COALESCE(left_at, joined_at)) AS last_seen_at
            FROM encounters
            WHERE user_id IS NOT NULL
            GROUP BY user_id, name;
        -- 自分のアカウントの名前の移り変わり
        CREATE VIEW account_names AS
            SELECT self_id AS id, self_name AS name, MIN(started_at) AS first_seen_at, MAX(COALESCE(ended_at, started_at)) AS last_seen_at
            FROM sessions
            WHERE self_id IS NOT NULL AND self_name IS NOT NULL
            GROUP BY self_id, self_name;
        """,
        // 3 版目: メモ・日記。訪問・ワールド・人・日のどれかに付く（1 つに何件でも）
        """
        CREATE TABLE notes (
            id TEXT PRIMARY KEY,
            target_type TEXT NOT NULL,     -- visit・world・person・day
            target_id TEXT NOT NULL,       -- 訪問の ID、wrld_…、usr_…、yyyy-MM-dd
            body TEXT NOT NULL,            -- Markdown
            device_id TEXT,                -- 書いた端末
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        CREATE INDEX notes_target ON notes (target_type, target_id);
        CREATE INDEX notes_updated ON notes (updated_at);
        """,
        // 4 版目: 認証（パスワード・ログインの Cookie・API のトークン）と設定
        """
        CREATE TABLE settings (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        -- ログインの Cookie。中身そのものは持たず、SHA-256 だけを持つ
        CREATE TABLE web_sessions (
            hash TEXT PRIMARY KEY,
            created_at TEXT NOT NULL,
            expires_at TEXT NOT NULL,
            last_seen_at TEXT NOT NULL,
            user_agent TEXT
        );
        -- ほかのアプリ用の API のトークン。中身そのものは持たず、SHA-256 だけを持つ
        CREATE TABLE api_tokens (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            hash TEXT NOT NULL UNIQUE,
            created_at TEXT NOT NULL,
            last_used_at TEXT
        );
        """,
        // 5 版目: 写真の目録。写真そのもの（photos）と置き場所（photo_locations）を分け、NAS へ移っても同じ写真として扱う
        """
        CREATE TABLE photos (
            id TEXT PRIMARY KEY,           -- ファイル名と大きさから決める
            file_name TEXT NOT NULL,
            size INTEGER NOT NULL,
            taken_at TEXT NOT NULL,        -- 撮った時刻（XMP、なければファイル名、なければファイルの時刻）
            taken_source TEXT NOT NULL,    -- xmp・file_name・file_time
            width INTEGER,
            height INTEGER,
            world_id TEXT,                 -- XMP の vrc:WorldID（2025.3.1 以降の VRChat）
            world_name TEXT,
            author_id TEXT,                -- 撮った人（XMP の vrc:AuthorID）
            author_name TEXT,
            indexed_at TEXT NOT NULL
        );
        CREATE INDEX photos_taken ON photos (taken_at);
        CREATE INDEX photos_world ON photos (world_id);
        CREATE TABLE photo_locations (
            path TEXT PRIMARY KEY,
            photo_id TEXT NOT NULL,
            root TEXT NOT NULL,            -- 見張っているフォルダ
            mtime TEXT NOT NULL,
            size INTEGER NOT NULL,
            device_id TEXT
        );
        CREATE INDEX photo_locations_photo ON photo_locations (photo_id);
        CREATE INDEX photo_locations_root ON photo_locations (root);
        """,
        // 6 版目: ワールドの詳しい情報（VRCX の取り込み、あとで拡張機能）と、ワールドのお気に入り。取り込んだメモの出どころ
        """
        ALTER TABLE worlds ADD COLUMN author_id TEXT;
        ALTER TABLE worlds ADD COLUMN author_name TEXT;
        ALTER TABLE worlds ADD COLUMN description TEXT;
        ALTER TABLE worlds ADD COLUMN image_url TEXT;
        ALTER TABLE worlds ADD COLUMN thumbnail_url TEXT;
        ALTER TABLE worlds ADD COLUMN release_status TEXT;
        ALTER TABLE worlds ADD COLUMN capacity INTEGER;
        ALTER TABLE worlds ADD COLUMN tags TEXT;              -- JSON の配列
        ALTER TABLE worlds ADD COLUMN details_source TEXT;    -- vrcx・extension
        ALTER TABLE worlds ADD COLUMN details_updated_at TEXT;
        CREATE TABLE world_favorites (
            world_id TEXT NOT NULL,
            list_name TEXT NOT NULL,         -- お気に入りの一覧の名前（VRCX のグループの名前など）
            source TEXT NOT NULL,            -- vrcx・extension・yamaori
            added_at TEXT,
            PRIMARY KEY (world_id, list_name, source)
        );
        ALTER TABLE notes ADD COLUMN source TEXT;   -- 取り込んだメモの出どころ（vrcx など）。Yamaori で書いたものは NULL
        """,
        // 7 版目: 拡張機能から。ワールドの対応機種・タグ・人数の上限など、フレンドの一覧の写し（前回と比べて増減を出す）
        """
        ALTER TABLE worlds ADD COLUMN platforms TEXT;         -- JSON の配列（standalonewindows・android・ios）
        ALTER TABLE worlds ADD COLUMN visits_total INTEGER;   -- vrchat.com に出ている訪問数
        ALTER TABLE worlds ADD COLUMN favorites_total INTEGER;
        ALTER TABLE worlds ADD COLUMN categories TEXT;        -- JSON の配列。説明文の言葉から決まりで付ける分類（Essential Assets など）
        CREATE TABLE friend_snapshots (
            account_id TEXT NOT NULL,
            user_id TEXT NOT NULL,
            display_name TEXT,
            seen_at TEXT NOT NULL,
            PRIMARY KEY (account_id, user_id)
        );
        CREATE TABLE friend_snapshot_meta (
            account_id TEXT PRIMARY KEY,
            taken_at TEXT NOT NULL,
            count INTEGER NOT NULL
        );
        """,
    ];

    /// <summary>この DB を持つ端末の ID。初めてなら作る（名前は変わっていれば直す）。</summary>
    public string SelfDevice(string name)
    {
        if (Scalar("SELECT id FROM devices WHERE is_self = 1") is string id)
        {
            using var u = Command("UPDATE devices SET name = $n WHERE id = $id", null, ("$n", name), ("$id", id));
            u.ExecuteNonQuery();
            return id;
        }
        id = "d_" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
        using var cmd = Command("INSERT INTO devices (id, name, is_self, created_at) VALUES ($id, $n, 1, $at)", null,
            ("$id", id), ("$n", name), ("$at", Time(DateTime.UtcNow)));
        cmd.ExecuteNonQuery();
        return id;
    }

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
