using Microsoft.Data.Sqlite;
using Yamaori.Store;

namespace Yamaori.Tests;

/// <summary>VRCX の表の定義（vrcx-team/VRCX の src/services/database/index.js）どおりの見本で確かめる。</summary>
public sealed class VrcxImportTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("yamaori-vrcx-").FullName;
    const string Me = "usr_11111111-2222-3333-4444-555555555555";
    const string Prefix = "usr11111111222233334444555555555555";

    string Vrcx()
    {
        var path = Path.Combine(_dir, "VRCX.sqlite3");
        using var c = new SqliteConnection($"Data Source={path};Pooling=false");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE gamelog_location (id INTEGER PRIMARY KEY, created_at TEXT, location TEXT, world_id TEXT, world_name TEXT, time INTEGER, group_name TEXT, UNIQUE(created_at, location));
            CREATE TABLE gamelog_join_leave (id INTEGER PRIMARY KEY, created_at TEXT, type TEXT, display_name TEXT, location TEXT, user_id TEXT, time INTEGER, UNIQUE(created_at, type, display_name));
            CREATE TABLE gamelog_video_play (id INTEGER PRIMARY KEY, created_at TEXT, video_url TEXT, video_name TEXT, video_id TEXT, location TEXT, display_name TEXT, user_id TEXT, UNIQUE(created_at, video_url));
            CREATE TABLE {Prefix}_friend_log_history (id INTEGER PRIMARY KEY, created_at TEXT, type TEXT, user_id TEXT, display_name TEXT, previous_display_name TEXT, trust_level TEXT, previous_trust_level TEXT, friend_number INTEGER);
            CREATE TABLE {Prefix}_notifications (id TEXT PRIMARY KEY, created_at TEXT, type TEXT, sender_user_id TEXT, sender_username TEXT, receiver_user_id TEXT, message TEXT, world_id TEXT, world_name TEXT, image_url TEXT, invite_message TEXT, request_message TEXT, response_message TEXT, expired INTEGER);
            CREATE TABLE cache_world (id TEXT PRIMARY KEY, added_at TEXT, author_id TEXT, author_name TEXT, created_at TEXT, description TEXT, image_url TEXT, name TEXT, release_status TEXT, thumbnail_image_url TEXT, updated_at TEXT, version INTEGER);
            CREATE TABLE favorite_world (id INTEGER PRIMARY KEY, created_at TEXT, world_id TEXT, group_name TEXT);
            CREATE TABLE world_memos (world_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
            CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);

            INSERT INTO gamelog_location (created_at, location, world_id, world_name, time) VALUES
              ('2024-05-01T12:00:00.000Z', 'wrld_a:100~region(jp)', 'wrld_a', 'World A', 1800000),
              ('2024-05-01T12:31:00.000Z', 'wrld_b:200~friends({Me})~region(jp)', 'wrld_b', 'World B', 0);
            INSERT INTO gamelog_join_leave (created_at, type, display_name, location, user_id, time) VALUES
              ('2024-05-01T12:00:02.000Z', 'OnPlayerJoined', 'MoomA', 'wrld_a:100~region(jp)', '{Me}', 0),
              ('2024-05-01T12:00:03.000Z', 'OnPlayerJoined', 'Alice', 'wrld_a:100~region(jp)', 'usr_alice', 0),
              ('2024-05-01T12:10:00.000Z', 'OnPlayerLeft', 'Alice', 'wrld_a:100~region(jp)', 'usr_alice', 597000),
              ('2024-05-01T12:31:05.000Z', 'OnPlayerJoined', 'Bob', 'wrld_b:200~friends({Me})~region(jp)', 'usr_bob', 0);
            INSERT INTO gamelog_video_play (created_at, video_url, video_name, location, display_name, user_id) VALUES
              ('2024-05-01T12:05:00.000Z', 'https://youtu.be/x', 'song', 'wrld_a:100~region(jp)', 'Alice', 'usr_alice');
            INSERT INTO {Prefix}_friend_log_history (created_at, type, user_id, display_name, previous_display_name, trust_level) VALUES
              ('2024-05-01T12:20:00.000Z', 'Friend', 'usr_alice', 'Alice', NULL, 'Known User'),
              ('2024-06-01T00:00:00.000Z', 'DisplayName', 'usr_alice', 'Alice2', 'Alice', NULL);
            INSERT INTO {Prefix}_notifications (id, created_at, type, sender_user_id, sender_username, receiver_user_id, message) VALUES
              ('not_1', '2024-05-01T12:15:00.000Z', 'invite', 'usr_bob', 'Bob', '{Me}', 'おいで');
            INSERT INTO cache_world (id, author_id, author_name, description, image_url, name, release_status, thumbnail_image_url, updated_at) VALUES
              ('wrld_fav', 'usr_author', 'Author', 'QV Pen あり', 'https://img/a.png', 'Fav World', 'public', 'https://img/a_t.png', '2024-01-01T00:00:00.000Z');
            INSERT INTO favorite_world (created_at, world_id, group_name) VALUES ('2024-01-02T00:00:00.000Z', 'wrld_fav', '行きたい');
            INSERT INTO world_memos (world_id, edited_at, memo) VALUES ('wrld_fav', '2024-01-03T00:00:00.000Z', 'よさそう');
            INSERT INTO memos (user_id, edited_at, memo) VALUES ('usr_alice', '2024-05-02T00:00:00.000Z', '絵がうまい');
            """;
        cmd.ExecuteNonQuery();
        return path;
    }

    [Fact]
    public void 訪問と会った人と動画とフレンドの記録とワールドの情報とメモを取り込む()
    {
        var vrcx = Vrcx();
        using var db = Database.Open(Path.Combine(_dir, "y.sqlite3"));
        var imp = new VrcxImport(db, "d_test");
        var r = imp.Import(vrcx);
        Assert.Equal([Me], r.Accounts);
        Assert.Equal(2, r.Visits);
        Assert.Equal(2, r.Encounters); // 本人は外す
        Assert.Equal(2, r.FriendEvents);
        Assert.Equal(Count(db, "SELECT COUNT(*) FROM visits"), imp.Import(vrcx).Visits); // 入れ直しても二重にならない

        var q = new Queries(db, TimeZoneInfo.Utc);
        var visits = q.Visits().Items.OrderBy(v => v.JoinedAt).ToList();
        Assert.Equal(new DateTime(2024, 5, 1, 12, 30, 0, DateTimeKind.Utc), visits[0].LeftAt); // time（30 分）
        Assert.Equal("left", visits[0].LeftReason);
        Assert.Equal("estimated", visits[1].LeftReason);
        Assert.Equal("friends", visits[1].Access);
        Assert.Equal(Me, visits[0].AccountId);
        var a = q.Visit(visits[0].Id)!;
        Assert.Equal(new DateTime(2024, 5, 1, 12, 10, 0, DateTimeKind.Utc), a.People.Single().LeftAt);
        Assert.Equal("song", a.Events.Single(e => e.Kind == "video").Data["title"]);

        var friend = q.Events(kind: "friend").Items;
        Assert.Contains(friend, e => e.Data["type"] == "Friend" && e.UserId == "usr_alice");
        Assert.Contains(friend, e => e.Data["type"] == "DisplayName" && e.Data["previous_name"] == "Alice");

        var w = db.Scalar("SELECT author_name || '|' || description || '|' || details_source FROM worlds WHERE id = 'wrld_fav'");
        Assert.Equal("Author|QV Pen あり|vrcx", w);
        Assert.Equal("行きたい", db.Scalar("SELECT list_name FROM world_favorites WHERE world_id = 'wrld_fav'"));
        Assert.Equal("よさそう", q.Notes("world", "wrld_fav").Single().Body);
        Assert.Equal("絵がうまい", q.Notes("person", "usr_alice").Single().Body);
        Assert.Equal(1L, Count(db, "SELECT COUNT(*) FROM notes")  - 1); // 入れ直してもメモは増えない（2 件のまま）
    }

    [Fact]
    public void 取り込んだメモを_Yamaori_で直したら_次の取り込みで戻さない()
    {
        var vrcx = Vrcx();
        using var db = Database.Open(Path.Combine(_dir, "y.sqlite3"));
        var imp = new VrcxImport(db, "d_test");
        imp.Import(vrcx);
        var q = new Queries(db, TimeZoneInfo.Utc);
        var note = q.Notes("world", "wrld_fav").Single();
        q.UpdateNote(note.Id, "Yamaori で直した");
        imp.Import(vrcx);
        Assert.Equal("Yamaori で直した", q.Note(note.Id)!.Body);
    }

    [Fact]
    public void 別の_VRCX_の_DB_は_入れ替えてよいと言われるまで取り込まない()
    {
        var vrcx = Vrcx();
        using var db = Database.Open(Path.Combine(_dir, "y.sqlite3"));
        var imp = new VrcxImport(db, "d_test");
        imp.Import(vrcx);
        using (var c = new SqliteConnection($"Data Source={vrcx};Pooling=false"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO gamelog_location (created_at, location, world_id, world_name, time) VALUES ('2023-01-01T00:00:00.000Z', 'wrld_c:1~region(jp)', 'wrld_c', 'C', 60000)";
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Assert.Throws<InvalidOperationException>(() => imp.Import(vrcx));
        Assert.Equal(2L, Count(db, "SELECT COUNT(*) FROM visits"));
        Assert.Equal(3, imp.Import(vrcx, replace: true).Visits);
    }

    [Fact]
    public void VAT_で取り込んだ期間は_VRCX_から二重に入れない()
    {
        var vrcx = Vrcx();
        using var db = Database.Open(Path.Combine(_dir, "y.sqlite3"));
        using (var cmd = db.Command("""
            INSERT INTO visits (id, source, device_id, world_id, instance, instance_number, access, region, joined_at, left_at)
            VALUES ('v_vat', 'vat', 'd_test', 'wrld_a', 'wrld_a:100', '100', 'public', 'jp', '2024-05-01T11:59:00Z', '2024-05-01T12:29:00Z')
            """)) cmd.ExecuteNonQuery();
        var r = new VrcxImport(db, "d_test").Import(vrcx);
        Assert.Equal(1, r.Visits);
        Assert.Equal(1, r.Skipped);
    }

    [Fact]
    public void VAT_の訪問と同じ時間でも_別のワールドの訪問は入れる()
    {
        var vrcx = Vrcx();
        using var db = Database.Open(Path.Combine(_dir, "y.sqlite3"));
        using (var cmd = db.Command("""
            INSERT INTO visits (id, source, device_id, world_id, instance, instance_number, access, region, joined_at, left_at)
            VALUES ('v_vat', 'vat', 'd_test', 'wrld_other', 'wrld_other:1', '1', 'public', 'jp', '2024-05-01T11:00:00Z', '2024-05-01T13:00:00Z')
            """)) cmd.ExecuteNonQuery();
        var r = new VrcxImport(db, "d_test").Import(vrcx);
        Assert.Equal(2, r.Visits);
        Assert.Equal(0, r.Skipped);
    }

    [Fact]
    public void ログの記録の前に入った訪問は_ログの始まりで切り_同じ時間を二重に数えない()
    {
        var vrcx = Vrcx();
        using var db = Database.Open(Path.Combine(_dir, "y.sqlite3"));
        // 12:10 からこの端末のログがある（World A の訪問は 12:00〜12:30）
        using (var cmd = db.Command("""
            INSERT INTO sessions (device_id, log_file, started_at, ended_at) VALUES ('d_test', 'x', '2024-05-01T12:10:00Z', '2024-05-01T12:20:00Z')
            """)) cmd.ExecuteNonQuery();
        new VrcxImport(db, "d_test").Import(vrcx);
        var a = new Queries(db, TimeZoneInfo.Utc).Visits().Items.Single(v => v.WorldId == "wrld_a");
        Assert.Equal(new DateTime(2024, 5, 1, 12, 10, 0, DateTimeKind.Utc), a.LeftAt);
    }

    static long Count(Database db, string sql) => (long)db.Scalar(sql)!;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }
}
