using Microsoft.Data.Sqlite;
using Yamaori.Store;

namespace Yamaori.Tests;

public sealed class VatImportTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("yamaori-vat-").FullName;
    static readonly TimeZoneInfo Tokyo = OperatingSystem.IsWindows() ? TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time") : TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    /// <summary>VAT と同じ形の DB を作る。</summary>
    string Vat(params (int Type, string At, string? NotificationId, string? UserId, string? UserName, string? WorldId, string? WorldName, string? Url)[] rows)
    {
        var path = Path.Combine(_dir, "VRChatActivityLog.db");
        using var c = new SqliteConnection($"Data Source={path};Pooling=false");
        c.Open();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE ActivityLogs (ID INTEGER PRIMARY KEY AUTOINCREMENT, ActivityType INTEGER NOT NULL, Timestamp TEXT, NotificationID TEXT, UserID TEXT, UserName TEXT, WorldID TEXT, WorldName TEXT, Message TEXT, Url TEXT);
                """;
            cmd.ExecuteNonQuery();
        }
        foreach (var r in rows)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO ActivityLogs (ActivityType, Timestamp, NotificationID, UserID, UserName, WorldID, WorldName, Url) VALUES ($t, $at, $n, $u, $un, $w, $wn, $url)";
            cmd.Parameters.AddWithValue("$t", r.Type);
            cmd.Parameters.AddWithValue("$at", r.At);
            cmd.Parameters.AddWithValue("$n", (object?)r.NotificationId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$u", (object?)r.UserId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$un", (object?)r.UserName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$w", (object?)r.WorldId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$wn", (object?)r.WorldName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$url", (object?)r.Url ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        return path;
    }

    [Fact]
    public void 訪問と会った人と動画と通知を取り込み_出た時刻を推定する()
    {
        var vat = Vat(
            (0, "2022-01-01 20:00:00", null, null, null, "wrld_a:100~region(jp)", "World A", null),
            (1, "2022-01-01 20:00:05", null, null, "Bob", null, null, null),
            (13, "2022-01-01 20:30:00", null, null, null, null, null, "https://youtu.be/x"),
            (0, "2022-01-01 21:00:00", null, null, null, "wrld_b:200~private(usr_x)~region(jp)", "World B", null),
            (1, "2022-01-01 21:10:00", null, null, "Bob", null, null, null),
            (1, "2022-01-01 21:20:00", null, null, "Carol", null, null, null),
            (7, "2022-01-01 21:30:00", "frq_1", "usr_bob", "Bob", null, null, null),
            // 6 時間あいて次の日（前の訪問は最後の出来事の時刻で閉じる）
            (0, "2022-01-02 03:30:00", null, null, null, "wrld_a:300~region(jp)", "World A", null));
        using var db = Database.Open(Path.Combine(_dir, "y.sqlite3"));
        var r = new VatImport(db, "d_test", Tokyo).Import(vat, "usr_me");
        Assert.Equal(3, r.Visits);
        var q = new Queries(db, Tokyo);
        var visits = q.Visits().Items.OrderBy(v => v.JoinedAt).ToList();
        Assert.Equal("2022-01-01T11:00:00Z|2022-01-01T12:00:00Z", $"{visits[0].JoinedAt:yyyy-MM-ddTHH:mm:ssZ}|{visits[0].LeftAt:yyyy-MM-ddTHH:mm:ssZ}");
        Assert.Equal(new DateTime(2022, 1, 1, 12, 20, 0, DateTimeKind.Utc), visits[1].LeftAt); // 21:20 に会った人の時刻で閉じる（21:30 の通知では延ばさない）
        Assert.All(visits, v => Assert.Equal("estimated", v.LeftReason));
        Assert.All(visits, v => Assert.Equal("usr_me", v.AccountId));
        Assert.Equal("invite", visits[1].Access);
        // Bob は通知で ID が分かるので、ID 付きで入る
        var b = q.Visit(visits[1].Id)!;
        Assert.Equal("usr_bob", b.People.Single(p => p.Name == "Bob").UserId);
        Assert.Null(b.People.Single(p => p.Name == "Carol").UserId);
        Assert.Equal(1, q.Visit(visits[0].Id)!.Events.Count(e => e.Kind == "video"));
        Assert.Equal("friendRequest", b.Events.Single(e => e.Kind == "notification").Data["type"]);
    }

    [Fact]
    public void 入れ直しても二重にならず_ログの記録がある期間は飛ばす()
    {
        // ログの記録（見本のログ、2026-11-08 02:36:41 から）がある期間に、VAT にも同じ訪問がある
        var log = Path.Combine(_dir, "output_log_2026-11-08_02-36-41.txt");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "output_log_2026-11-08_02-36-41.txt"), log);
        var vat = Vat(
            (0, "2026-11-08 01:00:00", null, null, null, "wrld_z:1~region(jp)", "Before", null),
            (0, "2026-11-08 02:36:53", null, null, null, "wrld_00000000-0000-0000-0000-00000000000a:87271~region(jp)", "Cushion Room", null),
            (1, "2026-11-08 02:37:05", null, null, "Alice", null, null, null),
            (7, "2026-11-08 02:40:00", "frq_00000000-0000-0000-0000-0000000000b1", "usr_00000000-0000-0000-0000-000000000003", "Bob 7ad1", null, null, null));
        using var db = Database.Open(Path.Combine(_dir, "y.sqlite3"));
        new LogIngestor(db, "d_test").Ingest(log, closed: true, Tokyo);
        var imp = new VatImport(db, "d_test", Tokyo);
        var r1 = imp.Import(vat);
        var r2 = imp.Import(vat);
        Assert.Equal(1, r1.Visits);
        Assert.Equal(3, r1.Skipped);
        Assert.Equal(r1 with { SelfNames = [] }, r2 with { SelfNames = [] });
        Assert.Equal(r1.SelfNames, r2.SelfNames);
        Assert.Equal(3L, (long)db.Scalar("SELECT COUNT(*) FROM visits")!); // ログの 2 つと、VAT の「Before」
        Assert.Equal(2L, (long)db.Scalar("SELECT COUNT(*) FROM events WHERE kind = 'notification'")!); // ログと同じ通知は重ならない
        // 名前しかない VAT の会った人も、ログが知っている ID に引き当てる（ここでは飛ばした期間なので確かめない）
    }

    [Fact]
    public void 名前はログが知っている_ID_に引き当てる()
    {
        var log = Path.Combine(_dir, "output_log_2026-11-08_02-36-41.txt");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "output_log_2026-11-08_02-36-41.txt"), log);
        var vat = Vat(
            (0, "2025-01-01 20:00:00", null, null, null, "wrld_z:1~region(jp)", "Old", null),
            (1, "2025-01-01 20:01:00", null, null, "Alice", null, null, null));
        using var db = Database.Open(Path.Combine(_dir, "y.sqlite3"));
        new LogIngestor(db, "d_test").Ingest(log, closed: true, Tokyo);
        var r = new VatImport(db, "d_test", Tokyo).Import(vat);
        Assert.Equal(1, r.Resolved);
        var person = new Queries(db, Tokyo).Person("usr_00000000-0000-0000-0000-000000000002")!;
        Assert.Equal(2, person.Person.VisitCount); // ログの訪問と、VAT の訪問
    }

    [Fact]
    public void ほとんどの訪問に出てくる名前は本人とみなして外す()
    {
        var rows = new List<(int, string, string?, string?, string?, string?, string?, string?)>();
        for (var i = 0; i < 30; i++)
        {
            var t = new DateTime(2022, 1, 1, 0, 0, 0).AddHours(i);
            rows.Add((0, $"{t:yyyy-MM-dd HH:mm:ss}", null, null, null, $"wrld_a:{i}~region(jp)", "A", null));
            rows.Add((1, $"{t.AddSeconds(5):yyyy-MM-dd HH:mm:ss}", null, null, "Me", null, null, null));
            if (i % 5 == 0) rows.Add((1, $"{t.AddSeconds(9):yyyy-MM-dd HH:mm:ss}", null, null, "Friend", null, null, null));
        }
        var vat = Vat([.. rows]);
        using var db = Database.Open(Path.Combine(_dir, "y.sqlite3"));
        var r = new VatImport(db, "d_test", Tokyo).Import(vat);
        Assert.Equal(["Me"], r.SelfNames);
        Assert.Equal(6, r.Encounters); // Friend だけ
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }
}
