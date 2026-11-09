using Yamaori.Store;

namespace Yamaori.Tests;

public sealed class MoveImportTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("yamaori-move-").FullName;

    static void Exec(Database db, string sql)
    {
        using var cmd = db.Command(sql);
        cmd.ExecuteNonQuery();
    }

    static long Count(Database db, string sql) => (long)db.Scalar(sql)!;

    /// <summary>メンバーの端末の DB（取り込んだ訪問・ログの訪問・アルバム・写真の直し・ゴミ箱・お気に入り・忘れた人・フレンドの写し・メモ）を作り、写しの道を返す。</summary>
    string Member()
    {
        var path = Path.Combine(_dir, "member.sqlite3");
        using (var db = Database.Open(path))
        {
            Exec(db, """
                INSERT INTO visits (id, source, device_id, world_id, instance, instance_number, access, region, joined_at, left_at) VALUES
                  ('v_vat', 'vat:abc', NULL, 'wrld_a', '1', '1', 'public', 'jp', '2020-01-01T10:00:00Z', '2020-01-01T11:00:00Z'),
                  ('v_log', 'log:d_member/output_log.txt', 'd_member', 'wrld_b', '1', '1', 'public', 'jp', '2026-11-01T10:00:00Z', '2026-11-01T11:00:00Z');
                INSERT INTO encounters (id, source, device_id, visit_id, user_id, name, joined_at) VALUES
                  ('e1', 'vat:abc', NULL, 'v_vat', 'usr_alice', 'Alice', '2020-01-01T10:05:00Z'),
                  ('e2', 'vat:abc', NULL, 'v_vat', 'usr_bob', 'Bob', '2020-01-01T10:06:00Z'),
                  ('e3', 'log:d_member/output_log.txt', 'd_member', 'v_log', 'usr_alice', 'Alice', '2026-11-01T10:05:00Z');
                INSERT INTO worlds (id, name, platforms) VALUES ('wrld_a', 'World A', '["android"]'), ('wrld_c', 'World C', NULL);
                INSERT INTO world_favorites (world_id, list_name, source, added_at) VALUES ('wrld_a', 'saved', 'yamaori', '2026-11-01T00:00:00Z');
                INSERT INTO albums (id, name, created_at, updated_at) VALUES ('a_1', '夏', '2026-11-01T00:00:00Z', '2026-11-01T00:00:00Z'), ('a_2', '冬', '2026-11-01T00:00:00Z', '2026-11-01T00:00:00Z');
                INSERT INTO album_photos (album_id, photo_id, added_at) VALUES ('a_1', 'p_1', '2026-11-01T00:00:00Z'), ('a_2', 'p_2', '2026-11-01T00:00:00Z');
                INSERT INTO photo_edits (photo_id, world_id, world_name, edited_at) VALUES ('p_1', 'wrld_a', 'World A', '2026-11-05T00:00:00Z'), ('p_2', 'wrld_c', 'World C', '2026-11-01T00:00:00Z');
                INSERT INTO photo_trash (photo_id, trashed_at) VALUES ('p_3', '2026-11-02T00:00:00Z');
                INSERT INTO friend_snapshot_meta (account_id, taken_at, count) VALUES ('usr_me', '2026-11-05T00:00:00Z', 1);
                INSERT INTO friend_snapshots (account_id, user_id, display_name, seen_at) VALUES ('usr_me', 'usr_alice', 'Alice', '2026-11-05T00:00:00Z');
                INSERT INTO notes (id, target_type, target_id, body, device_id, created_at, updated_at) VALUES
                  ('n_1', 'world', 'wrld_a', '良い', 'd_member', '2026-11-01T00:00:00Z', '2026-11-01T00:00:00Z'),
                  ('n_2', 'world', 'wrld_c', '消した', 'd_member', '2026-11-01T00:00:00Z', '2026-11-01T00:00:00Z');
                INSERT INTO forgotten_people (key, label, forgotten_at) VALUES ('usr_bob', 'Bob', '2026-11-01T00:00:00Z');
                """);
        }
        var copy = Path.Combine(_dir, "copy.sqlite3");
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"VACUUM INTO '{copy}'";
            cmd.ExecuteNonQuery();
        }
        return copy;
    }

    [Fact]
    public void 手元の記録をまとめ役へ足し込み_二度送っても増えない()
    {
        var copy = Member();
        using var hub = Database.Open(Path.Combine(_dir, "hub.sqlite3"));
        Exec(hub, """
            INSERT INTO worlds (id, name, platforms) VALUES ('wrld_a', NULL, '["standalonewindows"]');
            INSERT INTO albums (id, name, created_at, updated_at) VALUES ('a_hub', '夏', '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z');
            INSERT INTO photo_edits (photo_id, world_id, world_name, edited_at) VALUES ('p_1', 'wrld_x', 'Old', '2026-11-01T00:00:00Z'), ('p_2', 'wrld_y', 'Newer', '2026-11-09T00:00:00Z');
            INSERT INTO note_deletions (id, device_id, deleted_at) VALUES ('n_2', 'd_member', '2026-11-03T00:00:00Z');
            INSERT INTO encounters (id, source, device_id, visit_id, user_id, name, joined_at) VALUES ('h1', 'log:d_hub/x', 'd_hub', 'v_h', 'usr_bob', 'Bob', '2026-11-01T00:00:00Z');
            """);

        var r = new MoveImport(hub, "d_member").Import(copy);

        // ログの訪問はふだんの送り方で届くので移さない。取り込んだ訪問はその端末の記録として入る
        Assert.Equal(1, r.Visits);
        Assert.Equal("d_member", hub.Scalar("SELECT device_id FROM visits WHERE id = 'v_vat'"));
        Assert.Equal(0L, Count(hub, "SELECT count(*) FROM visits WHERE id = 'v_log'"));
        // 忘れた人は、まとめ役でも忘れる（まとめ役にあった分も、送られてきた分も）
        Assert.Equal(1, r.Forgotten);
        Assert.Equal(0L, Count(hub, "SELECT count(*) FROM encounters WHERE user_id = 'usr_bob'"));
        Assert.Equal(1L, Count(hub, "SELECT count(*) FROM encounters WHERE user_id = 'usr_alice'"));
        // ワールドは、空いているところだけ埋める
        Assert.Equal("World A", hub.Scalar("SELECT name FROM worlds WHERE id = 'wrld_a'"));
        Assert.Equal("[\"standalonewindows\"]", hub.Scalar("SELECT platforms FROM worlds WHERE id = 'wrld_a'"));
        Assert.Equal(1L, Count(hub, "SELECT count(*) FROM worlds WHERE id = 'wrld_c'"));
        // アルバムは同じ名前があればそこへ
        Assert.Equal(1, r.Albums);
        Assert.Equal(1L, Count(hub, "SELECT count(*) FROM album_photos WHERE album_id = 'a_hub' AND photo_id = 'p_1'"));
        Assert.Equal(1L, Count(hub, "SELECT count(*) FROM albums a JOIN album_photos p ON p.album_id = a.id WHERE a.name = '冬' AND p.photo_id = 'p_2'"));
        // 写真の直しは新しい方
        Assert.Equal("wrld_a", hub.Scalar("SELECT world_id FROM photo_edits WHERE photo_id = 'p_1'"));
        Assert.Equal("wrld_y", hub.Scalar("SELECT world_id FROM photo_edits WHERE photo_id = 'p_2'"));
        Assert.Equal(1L, Count(hub, "SELECT count(*) FROM photo_trash WHERE photo_id = 'p_3'"));
        Assert.Equal(1L, Count(hub, "SELECT count(*) FROM world_favorites WHERE world_id = 'wrld_a' AND list_name = 'saved'"));
        Assert.Equal(1, r.FriendAccounts);
        Assert.Equal(1L, Count(hub, "SELECT count(*) FROM friend_snapshots WHERE account_id = 'usr_me'"));
        // メモは、まとめ役で消したものは戻さない
        Assert.Equal(1, r.Notes);
        Assert.Equal(0L, Count(hub, "SELECT count(*) FROM notes WHERE id = 'n_2'"));

        var again = new MoveImport(hub, "d_member").Import(copy);
        Assert.Equal(new MoveResult(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), again);
        Assert.Equal(2L, Count(hub, "SELECT count(*) FROM albums"));
    }

    [Fact]
    public void 表の版が違う写しは断る()
    {
        var copy = Member();
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={copy};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 3";
            cmd.ExecuteNonQuery();
        }
        using var hub = Database.Open(Path.Combine(_dir, "hub.sqlite3"));
        var ex = Assert.Throws<InvalidDataException>(() => new MoveImport(hub, "d_member").Import(copy));
        Assert.Contains("同じ版", ex.Message);
        Assert.Equal(0L, Count(hub, "SELECT count(*) FROM visits"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
