using System.Text;
using Microsoft.Data.Sqlite;
using Yamaori.Store;

namespace Yamaori.Tests;

/// <summary>VRCX の連携: 入れたばかりの VRCX・VRChat のノート・新しい通知の表と、まとめ役へ送る写しの絞り方と指紋。</summary>
public sealed class VrcxSnapshotTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("yamaori-vrcxsnap-").FullName;
    const string Me = "usr_1e59de6d-cc66-45db-b610-27ebf15a5e05";
    const string Prefix = "usr1e59de6dcc6645dbb61027ebf15a5e05";
    const string Secret = "SECRET-CREDENTIAL-MARKER-7f3a";

    /// <summary>入れたばかりの VRCX（tofu の 2026-11-23 と同じ形。gamelog は空、フレンドの一覧・ノート・フィード・ログイン情報がある）。</summary>
    string FreshVrcx()
    {
        var path = Path.Combine(_dir, "VRCX.sqlite3");
        Exec(path, $"""
            CREATE TABLE configs (key TEXT PRIMARY KEY, value TEXT);
            INSERT INTO configs VALUES ('config:savedcredentials', '{Secret}'), ('config:lastuserloggedin', '{Me}'),
              ('config:friendloginit_{Me}', 'true'), ('config:vrcx_databaseversion', '18'), ('config:vrcx_tabledensity', 'standard');
            CREATE TABLE cookies (key TEXT PRIMARY KEY, value TEXT);
            INSERT INTO cookies VALUES ('default', '{Secret}');
            CREATE TABLE gamelog_location (id INTEGER PRIMARY KEY, created_at TEXT, location TEXT, world_id TEXT, world_name TEXT, time INTEGER, group_name TEXT, UNIQUE(created_at, location));
            CREATE TABLE gamelog_join_leave (id INTEGER PRIMARY KEY, created_at TEXT, type TEXT, display_name TEXT, location TEXT, user_id TEXT, time INTEGER, UNIQUE(created_at, type, display_name));
            CREATE TABLE gamelog_video_play (id INTEGER PRIMARY KEY, created_at TEXT, video_url TEXT, video_name TEXT, video_id TEXT, location TEXT, display_name TEXT, user_id TEXT, UNIQUE(created_at, video_url));
            CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
            INSERT INTO memos VALUES ('usr_alice', '2026-11-23T06:00:00.000Z', 'VRCX のメモ');
            CREATE TABLE {Prefix}_friend_log_current (user_id TEXT PRIMARY KEY, display_name TEXT, trust_level TEXT, friend_number INTEGER);
            INSERT INTO {Prefix}_friend_log_current VALUES ('usr_alice', 'Alice', 'Known User', 1), ('usr_bob', 'Bob', 'User', 2);
            CREATE TABLE {Prefix}_friend_log_history (id INTEGER PRIMARY KEY, created_at TEXT, type TEXT, user_id TEXT, display_name TEXT, previous_display_name TEXT, trust_level TEXT, previous_trust_level TEXT, friend_number INTEGER);
            CREATE TABLE {Prefix}_notes (user_id TEXT PRIMARY KEY, display_name TEXT, note TEXT, created_at TEXT);
            INSERT INTO {Prefix}_notes VALUES ('usr_alice', 'Alice', 'VRChat のノート', '2026-11-23T06:01:00.000Z'), ('usr_bob', 'Bob', '', '2026-11-23T06:02:00.000Z');
            CREATE TABLE {Prefix}_notifications_v2 (id TEXT PRIMARY KEY, created_at TEXT, updated_at TEXT, expires_at TEXT, type TEXT, link TEXT, link_text TEXT, message TEXT, title TEXT, image_url TEXT, seen INTEGER, sender_user_id TEXT, sender_username TEXT, data TEXT, responses TEXT, details TEXT);
            INSERT INTO {Prefix}_notifications_v2 (id, created_at, type, message, title, sender_user_id, sender_username) VALUES ('ntf_1', '2026-11-23T06:03:00.000Z', 'group.announcement', 'お知らせ', '題', 'usr_bob', 'Bob');
            CREATE TABLE {Prefix}_feed_gps (id INTEGER PRIMARY KEY, created_at TEXT, user_id TEXT, display_name TEXT, location TEXT, world_name TEXT, previous_location TEXT, time INTEGER, group_name TEXT);
            INSERT INTO {Prefix}_feed_gps (created_at, user_id, display_name, location) VALUES ('2026-11-23T06:04:00.000Z', 'usr_alice', 'Alice', 'wrld_secret:1');
            """);
        return path;
    }

    [Fact]
    public void 入れたばかりの_VRCX_でもアカウントが分かり_フレンドの一覧とノートと新しい通知が入る()
    {
        var vrcx = FreshVrcx();
        using var db = Database.Open(Path.Combine(_dir, "y.sqlite3"));
        var r = new VrcxImport(db, "d_test").Import(vrcx);
        Assert.Equal([Me], r.Accounts);
        Assert.Equal(2L, Count(db, "SELECT COUNT(*) FROM friend_snapshots"));
        Assert.Equal(1, r.VrchatNotes); // 空のノートは入れない
        var notes = new Queries(db, TimeZoneInfo.Utc).Notes("person", "usr_alice").Select(n => n.Body).Order().ToList();
        Assert.Equal(["VRCX のメモ", "VRChat のノート"], notes); // メモとノートは別の行
        Assert.Equal("vrchat-note", db.Scalar("SELECT source FROM notes WHERE body = 'VRChat のノート'"));
        Assert.Equal(1L, Count(db, "SELECT COUNT(*) FROM events WHERE kind = 'notification'"));
        Assert.Equal(0L, Count(db, "SELECT COUNT(*) FROM events WHERE data LIKE '%wrld_secret%'")); // フレンドの位置は入れない
        Assert.Equal(r.VrchatNotes, new VrcxImport(db, "d_test").Import(vrcx).VrchatNotes); // 入れ直しても増えない
        Assert.Equal(1L, Count(db, "SELECT COUNT(*) FROM notes WHERE source = 'vrchat-note'"));
    }

    [Fact]
    public void Yamaori_で直したノートは_次の取り込みで戻さない()
    {
        var vrcx = FreshVrcx();
        using var db = Database.Open(Path.Combine(_dir, "y.sqlite3"));
        new VrcxImport(db, "d_test").Import(vrcx);
        var q = new Queries(db, TimeZoneInfo.Utc);
        var id = (string)db.Scalar("SELECT id FROM notes WHERE source = 'vrchat-note'")!;
        q.UpdateNote(id, "直した");
        new VrcxImport(db, "d_test").Import(vrcx);
        Assert.Equal("直した", q.Note(id)!.Body);
    }

    [Fact]
    public void 遊び始めてアカウントや最初の訪問が分かっても_同じ_DB_の続きとして取り込む()
    {
        var vrcx = FreshVrcx();
        using var db = Database.Open(Path.Combine(_dir, "y.sqlite3"));
        // 前の版は、入れたばかりの VRCX ではアカウントが分からず "@" を残していた
        new VrcxImport(db, "d_test").Import(vrcx);
        new AuthStore(db).Set("import_origin:vrcx", "@");
        Exec(vrcx, "INSERT INTO gamelog_location (created_at, location, world_id, world_name, time) VALUES ('2026-11-24T10:00:00.000Z', 'wrld_a:1~region(jp)', 'wrld_a', 'A', 60000)");
        Assert.Equal(1, new VrcxImport(db, "d_test").Import(vrcx).Visits);
        Assert.Equal($"{Me}@2026-11-24T10:00:00.000Z", new AuthStore(db).Get("import_origin:vrcx"));
        // 別のアカウントの DB は止める
        new AuthStore(db).Set("import_origin:vrcx", "usr_other@2026-11-24T10:00:00.000Z");
        Assert.Throws<InvalidOperationException>(() => new VrcxImport(db, "d_test").Import(vrcx));
    }

    [Theory]
    [InlineData("@", "usr_a@2024", true)]
    [InlineData("usr_a@", "usr_a@2024", true)]
    [InlineData("usr_a@2024", "usr_a@2024", true)]
    [InlineData("usr_a@2024", "usr_a@2023", false)]
    [InlineData("usr_a@2024", "usr_b@2024", false)]
    [InlineData("usr_a@2024", "@2024", false)]
    public void 同じ_DB_の続きかの見分け(string before, string now, bool same) => Assert.Equal(same, VrcxImport.SameDb(before, now));

    [Fact]
    public void 写しには取り込む表だけが入り_ログイン情報もフィードも入らない()
    {
        var vrcx = FreshVrcx();
        var copy = Path.Combine(_dir, "copy.db");
        VrcxSnapshot.Narrow(vrcx, copy);
        SqliteConnection.ClearAllPools();
        Assert.DoesNotContain(Secret, Encoding.UTF8.GetString(File.ReadAllBytes(copy)));
        var tables = Strings(copy, "SELECT name FROM sqlite_master WHERE type = 'table'");
        Assert.DoesNotContain("cookies", tables);
        Assert.DoesNotContain(tables, t => t.Contains("feed", StringComparison.Ordinal));
        Assert.Contains($"{Prefix}_notes", tables);
        Assert.Contains($"{Prefix}_notifications_v2", tables);
        Assert.Equal(["config:friendloginit_" + Me, "config:lastuserloggedin", "config:vrcx_databaseversion"], Strings(copy, "SELECT key FROM configs ORDER BY key"));

        // 写しを取り込んでも、元を取り込んだのと同じになる
        using var a = Database.Open(Path.Combine(_dir, "a.sqlite3"));
        using var b = Database.Open(Path.Combine(_dir, "b.sqlite3"));
        Assert.Equal(new VrcxImport(a, "d").Import(vrcx) with { Accounts = [] }, new VrcxImport(b, "d").Import(copy) with { Accounts = [] });
        Assert.Equal(Count(a, "SELECT COUNT(*) FROM friend_snapshots"), Count(b, "SELECT COUNT(*) FROM friend_snapshots"));
    }

    [Fact]
    public void 指紋はフィードやログイン情報では変わらず_取り込む中身で変わる()
    {
        var vrcx = FreshVrcx();
        var fp = VrcxSnapshot.Fingerprint(vrcx);
        Exec(vrcx, $"INSERT INTO {Prefix}_feed_gps (created_at, user_id, location) VALUES ('2026-11-23T07:00:00.000Z', 'usr_bob', 'wrld_x:1'); UPDATE cookies SET value = 'x'; UPDATE configs SET value = 'compact' WHERE key = 'config:vrcx_tabledensity'");
        Assert.Equal(fp, VrcxSnapshot.Fingerprint(vrcx));
        Assert.Equal(fp, VrcxSnapshot.Narrow(vrcx, Path.Combine(_dir, "c.db")));

        Exec(vrcx, "UPDATE memos SET memo = '書き換えた'");
        var fp2 = VrcxSnapshot.Fingerprint(vrcx);
        Assert.NotEqual(fp, fp2);
        Exec(vrcx, "INSERT INTO gamelog_location (created_at, location, world_id, world_name, time) VALUES ('2026-11-24T10:00:00.000Z', 'wrld_a:1', 'wrld_a', 'A', 0)");
        var fp3 = VrcxSnapshot.Fingerprint(vrcx);
        Assert.NotEqual(fp2, fp3);
        Exec(vrcx, "UPDATE gamelog_location SET time = 60000"); // 出たときに VRCX が time を書く
        Assert.NotEqual(fp3, VrcxSnapshot.Fingerprint(vrcx));
    }

    [Fact]
    public void 写してよい表の見分け()
    {
        Assert.True(VrcxSnapshot.Allowed("gamelog_location"));
        Assert.True(VrcxSnapshot.Allowed(Prefix + "_notifications_v2"));
        Assert.True(VrcxSnapshot.Allowed("_123abc_notes"));
        Assert.False(VrcxSnapshot.Allowed("cookies"));
        Assert.False(VrcxSnapshot.Allowed("configs"));
        Assert.False(VrcxSnapshot.Allowed(Prefix + "_feed_gps"));
        Assert.False(VrcxSnapshot.Allowed(Prefix + "_moderation"));
        Assert.False(VrcxSnapshot.Allowed("x;drop_notes"));
    }

    [Fact]
    public void 今の形の_ID_は表の頭から組み立て直せる()
    {
        Assert.Equal(Me, VrcxImport.FromPrefix(Prefix));
        Assert.Null(VrcxImport.FromPrefix("usrabc"));
    }

    static void Exec(string path, string sql)
    {
        using var c = new SqliteConnection($"Data Source={path};Pooling=false");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static List<string> Strings(string path, string sql)
    {
        using var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=false");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    static long Count(Database db, string sql) => (long)db.Scalar(sql)!;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }
}
