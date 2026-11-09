using Yamaori.Store;

namespace Yamaori.Tests;

public sealed class StoreTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("yamaori-test-").FullName;
    // 組み立て直すときは DB に残したタイムゾーンの ID から引くので、OS にあるものを使う
    static readonly TimeZoneInfo Tokyo = OperatingSystem.IsWindows() ? TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time") : TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
    const string FileName = "output_log_2026-11-08_02-36-41.txt";
    static byte[] Fixture() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", FileName));

    string LogPath => Path.Combine(_dir, FileName);

    List<string> Visits(Database db)
    {
        using var cmd = db.Command("SELECT id || '|' || COALESCE(world_name,'') || '|' || joined_at || '|' || COALESCE(left_at,'-') || '|' || COALESCE(left_reason,'-') FROM visits ORDER BY joined_at");
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    long Count(Database db, string table) => (long)db.Scalar($"SELECT COUNT(*) FROM {table}")!;

    [Fact]
    public void 書き足されるログを少しずつ読んでも_一度に読んだのと同じになる()
    {
        var bytes = Fixture();
        using var whole = Database.Open(Path.Combine(_dir, "whole.sqlite3"));
        File.WriteAllBytes(LogPath, bytes);
        new LogIngestor(whole, "d_test").Ingest(LogPath, closed: true, Tokyo);
        var expected = Visits(whole);
        Assert.Equal(2, expected.Count);

        using var db = Database.Open(Path.Combine(_dir, "step.sqlite3"));
        var ingestor = new LogIngestor(db, "d_test");
        File.WriteAllBytes(LogPath, []);
        for (var cut = 0; cut <= bytes.Length; cut += 97)
        {
            File.WriteAllBytes(LogPath, bytes[..cut]);
            ingestor.Ingest(LogPath, closed: false, Tokyo);
        }
        File.WriteAllBytes(LogPath, bytes);
        ingestor.Ingest(LogPath, closed: false, Tokyo);
        Assert.EndsWith("|-|-", Visits(db)[^1]); // まだ閉じていない
        ingestor.Ingest(LogPath, closed: true, Tokyo);

        Assert.Equal(expected, Visits(db));
        Assert.Equal(Count(whole, "encounters"), Count(db, "encounters"));
        Assert.Equal(Count(whole, "events"), Count(db, "events"));
        Assert.Equal(Count(whole, "log_events"), Count(db, "log_events"));
    }

    [Fact]
    public void 開き直しても続きから読み_二重にならない()
    {
        File.WriteAllBytes(LogPath, Fixture());
        var path = Path.Combine(_dir, "y.sqlite3");
        using (var db = Database.Open(path)) new LogIngestor(db, "d_test").Ingest(LogPath, closed: false, Tokyo);
        using (var db = Database.Open(path))
        {
            var r = new LogIngestor(db, "d_test").Ingest(LogPath, closed: true, Tokyo);
            Assert.Empty(r.NewEvents); // 閉じたときに読むのは最後の 1 件（Destroying）だけで、出来事にならない
            Assert.Equal(1L, (long)db.Scalar("SELECT COUNT(*) FROM visits WHERE left_reason = 'log_end'")!);
            Assert.Equal(2L, (long)db.Scalar("SELECT COUNT(*) FROM visits")!);
        }
    }

    [Fact]
    public void 出来事から組み立て直しても同じ()
    {
        File.WriteAllBytes(LogPath, Fixture());
        using var db = Database.Open(Path.Combine(_dir, "r.sqlite3"));
        var ingestor = new LogIngestor(db, "d_test");
        ingestor.Ingest(LogPath, closed: true, Tokyo);
        var before = Visits(db);
        File.Delete(LogPath); // 生のログがなくても組み立て直せる
        ingestor.Rebuild(FileName);
        Assert.Equal(before, Visits(db));
        Assert.Equal(2L, (long)db.Scalar("SELECT COUNT(*) FROM people")!);
        Assert.Equal("Evermore - 彼方", db.Scalar("SELECT name FROM worlds WHERE id = 'wrld_00000000-0000-0000-0000-00000000000b'"));
    }

    [Fact]
    public void 忘れた人は_組み立て直しても戻らず_忘れるのをやめると戻る()
    {
        File.WriteAllBytes(LogPath, Fixture());
        using var db = Database.Open(Path.Combine(_dir, "f.sqlite3"));
        var ingestor = new LogIngestor(db, "d_test");
        ingestor.Ingest(LogPath, closed: true, Tokyo);
        var who = (string)db.Scalar("SELECT user_id FROM encounters WHERE user_id IS NOT NULL GROUP BY user_id ORDER BY COUNT(*) DESC LIMIT 1")!;
        long Met() => (long)db.Scalar("SELECT COUNT(*) FROM encounters WHERE user_id = $u", ("$u", who))!;
        Assert.True(Met() > 0);
        var others = (long)db.Scalar("SELECT COUNT(*) FROM encounters WHERE user_id IS NOT $u", ("$u", who))!;

        var forget = new ForgetStore(db);
        Assert.True(forget.Forget(who, "誰か") > 0);
        Assert.Equal(0, Met());
        ingestor.Rebuild(FileName);
        Assert.Equal(0, Met());
        Assert.Equal(others, (long)db.Scalar("SELECT COUNT(*) FROM encounters WHERE user_id IS NOT $u", ("$u", who))!);

        Assert.True(forget.Unforget(who));
        ingestor.Rebuild(FileName);
        Assert.True(Met() > 0);
    }

    [Fact]
    public void 名前だけの人を忘れても_同じ名前で_ID_の分かる人は残し_忘れた人へのメモは入れない()
    {
        using var db = Database.Open(Path.Combine(_dir, "n.sqlite3"));
        new ForgetStore(db).Forget("name:Alice", "Alice");
        void Exec(string sql) { using var cmd = db.Command(sql); cmd.ExecuteNonQuery(); }
        Exec("INSERT INTO encounters (id, source, device_id, visit_id, user_id, name, joined_at) VALUES ('e1', 'x', 'd', 'v', NULL, 'Alice', '2026-01-01T00:00:00Z')");
        Exec("INSERT INTO encounters (id, source, device_id, visit_id, user_id, name, joined_at) VALUES ('e2', 'x', 'd', 'v', 'usr_other', 'Alice', '2026-01-01T00:00:00Z')");
        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM encounters"));
        Assert.Equal("e2", db.Scalar("SELECT id FROM encounters"));
        Exec("INSERT INTO notes (id, target_type, target_id, body, created_at, updated_at) VALUES ('n1', 'person', 'name:Alice', 'x', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z')");
        Assert.Equal(0L, db.Scalar("SELECT COUNT(*) FROM notes"));
    }

    [Fact]
    public void 版_9_の_DB_を上げても中身は残り_上げる前の写しを取り_新しい版の_DB_は開かない()
    {
        var path = Path.Combine(_dir, "m.sqlite3");
        Database.CreateAtVersion(path, 9);
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO api_tokens (id, name, hash, created_at) VALUES ('t_old', 'extension', 'h', '2026-11-01T00:00:00Z');
                INSERT INTO notes (id, target_type, target_id, body, created_at, updated_at) VALUES ('n_old', 'day', '2026-11-01', 'メモ', '2026-11-01T00:00:00Z', '2026-11-01T00:00:00Z');
                INSERT INTO sync_state (parent, source, hash, pushed_at) VALUES ('http://p/', 'notes', 'x', '2026-11-01T00:00:00Z');
                INSERT INTO visits (id, source, device_id, world_id, instance, instance_number, access, owner_id, region, joined_at)
                    VALUES ('v_old', 'log:d/x', 'd', 'wrld_x', '1~private(usr_owner)', '1', 'private', 'usr_owner', 'jp', '2026-11-01T00:00:00Z');
                """;
            cmd.ExecuteNonQuery();
        }
        using (var db = Database.Open(path))
        {
            Assert.Equal((long)Database.SchemaVersion, db.Scalar("PRAGMA user_version"));
            Assert.Equal("full", db.Scalar("SELECT scope FROM api_tokens WHERE id = 't_old'"));
            Assert.Equal("メモ", db.Scalar("SELECT body FROM notes WHERE id = 'n_old'"));
            Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM sync_state"));
            // 上げたあとの表でも、忘れることが効く
            new ForgetStore(db).Forget("usr_owner", "持ち主");
            Assert.Equal(DBNull.Value, db.Scalar("SELECT owner_id FROM visits WHERE id = 'v_old'"));
            Assert.DoesNotContain("usr_owner", (string)db.Scalar("SELECT instance FROM visits WHERE id = 'v_old'")!);
        }
        using (var bak = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}.bak-v9;Pooling=False"))
        {
            bak.Open();
            using var cmd = bak.CreateCommand();
            cmd.CommandText = "PRAGMA user_version";
            Assert.Equal(9L, cmd.ExecuteScalar());
        }

        using (var db = Database.Open(path))
        using (var cmd = db.Command("PRAGMA user_version = 999")) cmd.ExecuteNonQuery();
        Assert.Throws<InvalidOperationException>(() => Database.Open(path));
    }

    [Fact]
    public void 忘れた人は_通知の受け手とインスタンスの持ち主にも残らない()
    {
        using var db = Database.Open(Path.Combine(_dir, "o.sqlite3"));
        new ForgetStore(db).Forget("usr_bob", "Bob");
        void Exec(string sql) { using var cmd = db.Command(sql); cmd.ExecuteNonQuery(); }
        Exec("""INSERT INTO events (id, source, device_id, at, kind, data) VALUES ('ev1', 'x', 'd', '2026-01-01T00:00:00Z', 'notification', '{"receiver_id":"usr_bob","type":"invite"}')""");
        Exec("""INSERT INTO events (id, source, device_id, at, kind, data) VALUES ('ev2', 'x', 'd', '2026-01-01T00:00:00Z', 'notification', '{"receiver_id":"usr_carol","type":"invite"}')""");
        Assert.Equal("ev2", db.Scalar("SELECT group_concat(id) FROM events"));
        Exec("INSERT INTO visits (id, source, device_id, world_id, instance, instance_number, access, owner_id, region, joined_at) VALUES ('v1', 'x', 'd', 'wrld_x', '1~private(usr_bob)~region(jp)', '1', 'private', 'usr_bob', 'jp', '2026-01-01T00:00:00Z')");
        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM visits"));
        Assert.Equal(DBNull.Value, db.Scalar("SELECT owner_id FROM visits"));
        Assert.Equal("1~private(forgotten)~region(jp)", db.Scalar("SELECT instance FROM visits"));
    }

    [Fact]
    public void 動いている間でも_DB_の写しを作れる()
    {
        File.WriteAllBytes(LogPath, Fixture());
        using var db = Database.Open(Path.Combine(_dir, "b.sqlite3"));
        new LogIngestor(db, "d_test").Ingest(LogPath, closed: true, Tokyo);
        var copy = Path.Combine(_dir, "backups", "copy.sqlite3");
        db.Backup(copy);
        using var c = Database.Open(copy);
        Assert.Equal(Visits(db), Visits(c));
    }

    [Fact]
    public void 読んだときのタイムゾーンが見つからなければ_組み立て直さずに止める()
    {
        File.WriteAllBytes(LogPath, Fixture());
        using var db = Database.Open(Path.Combine(_dir, "tz.sqlite3"));
        var ingestor = new LogIngestor(db, "d_test");
        ingestor.Ingest(LogPath, closed: true, Tokyo);
        var before = Visits(db);
        using (var cmd = db.Command("UPDATE log_files SET time_zone = 'Nowhere/Zone'")) cmd.ExecuteNonQuery();
        Assert.Throws<InvalidOperationException>(() => ingestor.Rebuild(FileName));
        Assert.Equal(before, Visits(db));
    }

    [Fact]
    public void 残すフォルダがログのフォルダと同じでも_元のログを消さない()
    {
        var bytes = Fixture();
        File.WriteAllBytes(LogPath, bytes);
        var archive = new LogArchive(Path.GetDirectoryName(LogPath)!);
        archive.Sync(LogPath, closed: true);
        Assert.Equal(bytes, File.ReadAllBytes(LogPath));
    }

    [Fact]
    public void 生のログを少しずつ写し_閉じたら固め_そこから読み直せる()
    {
        var bytes = Fixture();
        var archive = new LogArchive(Path.Combine(_dir, "archive"));
        File.WriteAllBytes(LogPath, []);
        for (var cut = 0; cut <= bytes.Length; cut += 501)
        {
            File.WriteAllBytes(LogPath, bytes[..cut]);
            archive.Sync(LogPath, closed: false);
        }
        File.WriteAllBytes(LogPath, bytes);
        archive.Sync(LogPath, closed: false);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(archive.Dir, FileName)));

        archive.Sync(LogPath, closed: true);
        Assert.False(File.Exists(Path.Combine(archive.Dir, FileName)));
        var (name, open, closed) = Assert.Single(archive.List());
        Assert.Equal(FileName, name);
        Assert.True(closed);

        using var db = Database.Open(Path.Combine(_dir, "a.sqlite3"));
        var ingestor = new LogIngestor(db, "d_test");
        ingestor.Ingest(LogPath, closed: true, Tokyo);
        var before = Visits(db);
        File.Delete(LogPath); // VRChat がログを消しても
        using (var s = open()) ingestor.Reparse(name, s, closed);
        Assert.Equal(before, Visits(db));
        Assert.Equal(2L, Count(db, "visits"));
    }

    [Fact]
    public void 固めたあとで元のログが伸びたら_続きも写す()
    {
        var bytes = Fixture();
        var archive = new LogArchive(Path.Combine(_dir, "archive"));
        var cut = bytes.Length / 2;
        File.WriteAllBytes(LogPath, bytes[..cut]);
        archive.Sync(LogPath, closed: true);
        File.WriteAllBytes(LogPath, bytes); // 閉じたと見たあとで書き足された
        archive.Sync(LogPath, closed: true);
        var (_, open, closed) = Assert.Single(archive.List());
        Assert.True(closed);
        using var s = open();
        using var m = new MemoryStream();
        s.CopyTo(m);
        Assert.Equal(bytes, m.ToArray());
        // もう伸びていなければ、何もしない
        var gz = Path.Combine(archive.Dir, FileName + ".gz");
        var at = File.GetLastWriteTimeUtc(gz);
        archive.Sync(LogPath, closed: true);
        Assert.Equal(at, File.GetLastWriteTimeUtc(gz));
    }

    [Fact]
    public void 読み直しは_前の記録を消すことと一緒に入れる()
    {
        File.WriteAllBytes(LogPath, Fixture());
        using var db = Database.Open(Path.Combine(_dir, "r.sqlite3"));
        var ingestor = new LogIngestor(db, "d_test");
        ingestor.Ingest(LogPath, closed: true, Tokyo);
        var before = Visits(db);
        // 読めない中身で読み直そうとして落ちても、前の記録は残る
        Assert.ThrowsAny<Exception>(() => ingestor.Reparse(FileName, new ThrowingStream(), closed: true));
        Assert.Equal(before, Visits(db));
        Assert.True((long)db.Scalar("SELECT COUNT(*) FROM log_events")! > 0);
    }

    sealed class ThrowingStream : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("読めない");
        public override int Read(Span<byte> buffer) => throw new IOException("読めない");
    }

    [Fact]
    public void 端末が違えば同じ名前のログでもぶつからない()
    {
        File.WriteAllBytes(LogPath, Fixture());
        using var db = Database.Open(Path.Combine(_dir, "d.sqlite3"));
        new LogIngestor(db, "d_desktop").Ingest(LogPath, closed: true, Tokyo);
        new LogIngestor(db, "d_laptop").Ingest(LogPath, closed: true, Tokyo);
        Assert.Equal(4L, Count(db, "visits"));
        Assert.Equal(2L, (long)db.Scalar("SELECT COUNT(DISTINCT device_id) FROM visits")!);
        Assert.Equal(2L, (long)db.Scalar("SELECT COUNT(*) FROM sessions")!);
        // 通知はアカウントに届くものなので、端末をまたいで重ねる
        Assert.Equal(2L, (long)db.Scalar("SELECT COUNT(*) FROM events WHERE kind = 'notification'")!);
    }

    [Fact]
    public void この端末のIDは一度決めたら変わらない()
    {
        var path = Path.Combine(_dir, "self.sqlite3");
        string id;
        using (var db = Database.Open(path)) id = db.SelfDevice("to-fu");
        using (var db = Database.Open(path))
        {
            Assert.Equal(id, db.SelfDevice("to-fu-renamed"));
            Assert.Equal("to-fu-renamed", db.Scalar("SELECT name FROM devices WHERE id = $id", ("$id", id)));
        }
    }

    [Fact]
    public void 名前を変えた人も同じ人として束ね_名前の移り変わりが残る()
    {
        // 同じ ID の人が、1 本目と 2 本目のログで違う名前で出る
        var bytes = Fixture();
        var first = Path.Combine(_dir, "output_log_2026-11-08_02-36-41.txt");
        var second = Path.Combine(_dir, "output_log_2026-11-09_02-36-41.txt");
        File.WriteAllBytes(first, bytes);
        File.WriteAllText(second, System.Text.Encoding.UTF8.GetString(bytes).Replace("2026.11.08", "2026.11.09").Replace("Carol (usr_", "Carol2 (usr_"));
        using var db = Database.Open(Path.Combine(_dir, "n.sqlite3"));
        var ingestor = new LogIngestor(db, "d_test");
        ingestor.Ingest(first, closed: true, Tokyo);
        ingestor.Ingest(second, closed: true, Tokyo);
        const string carol = "usr_00000000-0000-0000-0000-000000000004";
        Assert.Equal("Carol2", db.Scalar("SELECT name FROM people WHERE user_id = $u", ("$u", carol)));
        Assert.Equal(2L, (long)db.Scalar("SELECT visit_count FROM people WHERE user_id = $u", ("$u", carol))!);
        Assert.Equal(2L, (long)db.Scalar("SELECT COUNT(*) FROM person_names WHERE user_id = $u", ("$u", carol))!);
        Assert.Equal(4L, (long)db.Scalar("SELECT COUNT(*) FROM visits WHERE account_id = 'usr_00000000-0000-0000-0000-000000000001'")!);
        Assert.Equal("自分。", db.Scalar("SELECT name FROM accounts"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }
}
