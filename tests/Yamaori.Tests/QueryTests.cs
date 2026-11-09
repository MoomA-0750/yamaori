using Yamaori.Store;

namespace Yamaori.Tests;

public sealed class QueryTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("yamaori-q-").FullName;
    static readonly TimeZoneInfo Tokyo = OperatingSystem.IsWindows() ? TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time") : TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
    const string FileName = "output_log_2026-11-08_02-36-41.txt";

    Database Open()
    {
        var log = Path.Combine(_dir, FileName);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", FileName), log, true);
        var db = Database.Open(Path.Combine(_dir, "q.sqlite3"));
        new LogIngestor(db, "d_test").Ingest(log, closed: true, Tokyo);
        return db;
    }

    [Fact]
    public void 日と訪問と人を読める()
    {
        using var db = Open();
        var q = new Queries(db, Tokyo);
        var day = Assert.Single(q.Days());
        Assert.Equal("2026-11-08", day.Date);
        Assert.Equal(2, day.VisitCount);
        Assert.Equal(8, day.Minutes); // 02:36:53–02:40:48（3 分 55 秒）と 02:40:53–02:45:15（4 分 22 秒）
        var visits = q.Day(new DateOnly(2026, 11, 8));
        Assert.Equal(["Cushion Room", "Evermore - 彼方"], visits.Select(v => v.WorldName));
        var detail = q.Visit(visits[0].Id)!;
        Assert.Equal(2, detail.People.Count); // Alice が 2 回
        Assert.Contains(detail.Events, e => e.Kind == "video");
        Assert.Equal(2, q.People().Total);
        Assert.Equal("Alice", q.People(q: "ali").Items.Single().Name);
        Assert.Equal("Evermore - 彼方", q.Worlds(q: "Ever").Items.Single().Name);
    }

    [Fact]
    public void 今いる訪問も_日とワールドの時間に数える()
    {
        using var db = Database.Open(Path.Combine(_dir, "open.sqlite3"));
        var joined = DateTime.UtcNow.AddMinutes(-90);
        using (var cmd = db.Command("INSERT INTO visits (id, source, device_id, world_id, instance, instance_number, access, region, joined_at) VALUES ('v_open', 'x', 'd', 'wrld_open', '1', '1', 'public', 'jp', $j)", null, ("$j", Database.Time(joined))))
            cmd.ExecuteNonQuery();
        using (var cmd = db.Command("INSERT INTO worlds (id, name) VALUES ('wrld_open', 'Open')")) cmd.ExecuteNonQuery();
        var q = new Queries(db, Tokyo);
        Assert.InRange(Assert.Single(q.Days()).Minutes, 89, 91);
        Assert.InRange(q.Worlds().Items.Single().Minutes, 89, 91);
    }

    [Fact]
    public void メモを書いて直して消せ_付け先がなければ断る()
    {
        using var db = Open();
        var q = new Queries(db, Tokyo);
        var visit = q.Visits().Items[0];
        Assert.True(q.TargetExists("visit", visit.Id));
        Assert.False(q.TargetExists("visit", "v_nothing"));
        Assert.False(q.TargetExists("day", "2026/11/08"));
        var n = q.AddNote("visit", visit.Id, "たのしかった", "d_test");
        Assert.Equal(1, q.Visits().Items[0].NoteCount);
        Assert.Equal("とても", q.UpdateNote(n.Id, "とても")!.Body);
        Assert.True(q.DeleteNote(n.Id));
        Assert.Null(q.Note(n.Id));
    }

    [Fact]
    public void 日ごとの_Markdown_に日記と訪問のメモが入る()
    {
        using var db = Open();
        var q = new Queries(db, Tokyo);
        q.AddNote("day", "2026-11-08", "# いい日\n雨だった", null);
        q.AddNote("visit", q.Day(new DateOnly(2026, 11, 8))[0].Id, "クッションに埋もれた", null);
        var (file, md) = Assert.Single(new MarkdownExport(q, Tokyo).Days());
        Assert.Equal("2026-11-08.md", file);
        Assert.Contains("雨だった", md);
        Assert.Contains("- 02:36–02:40 Cushion Room", md);
        Assert.Contains("    クッションに埋もれた", md);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }
}
