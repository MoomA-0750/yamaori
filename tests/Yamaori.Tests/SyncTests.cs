using Yamaori.Store;

namespace Yamaori.Tests;

public sealed class SyncTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("yamaori-sync2-").FullName;
    static readonly TimeZoneInfo Tokyo = OperatingSystem.IsWindows() ? TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time") : TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
    const string FileName = "output_log_2026-11-08_02-36-41.txt";

    Database Child(out string deviceId)
    {
        var log = Path.Combine(_dir, FileName);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", FileName), log, true);
        var db = Database.Open(Path.Combine(_dir, "child.sqlite3"));
        deviceId = db.SelfDevice("laptop");
        new LogIngestor(db, deviceId).Ingest(log, closed: true, Tokyo);
        return db;
    }

    [Fact]
    public void 子の記録を親へ渡すと同じ訪問になり_端末が分かる()
    {
        using var child = Child(out var dev);
        using var parent = Database.Open(Path.Combine(_dir, "parent.sqlite3"));
        parent.SelfDevice("desktop");
        var c = new SyncStore(child);
        var source = Assert.Single(c.LocalSources(dev));
        new SyncStore(parent).Apply(new SyncProjection(dev, "laptop", source, c.Read(dev, source)));

        string Visits(Database db)
        {
            using var cmd = db.Command("SELECT group_concat(id || '|' || world_id || '|' || joined_at || '|' || COALESCE(left_at, '') || '|' || COALESCE(account_id, ''), ';') FROM (SELECT * FROM visits ORDER BY joined_at)");
            return (string)cmd.ExecuteScalar()!;
        }
        Assert.Equal(Visits(child), Visits(parent));
        Assert.Equal((long)child.Scalar("SELECT COUNT(*) FROM encounters")!, (long)parent.Scalar("SELECT COUNT(*) FROM encounters")!);
        Assert.Equal((long)child.Scalar("SELECT COUNT(*) FROM events")!, (long)parent.Scalar("SELECT COUNT(*) FROM events")!);
        var devices = new Queries(parent).Devices();
        Assert.Equal(2, devices.Count);
        Assert.Equal(2, devices.Single(d => d.Name == "laptop").VisitCount);
        Assert.Single(new Queries(parent).Accounts()); // 起動の記録（アカウント）も渡る
    }

    [Fact]
    public void 変わっていなければ同じハッシュで_送らずに済む()
    {
        using var child = Child(out var dev);
        var c = new SyncStore(child);
        var source = c.LocalSources(dev)[0];
        var h1 = SyncStore.Hash(new SyncProjection(dev, "laptop", source, c.Read(dev, source)));
        c.MarkPushed("http://desktop/", source, h1);
        Assert.Equal(h1, c.PushedHash("http://desktop/", source));
        Assert.Equal(h1, SyncStore.Hash(new SyncProjection(dev, "laptop", source, c.Read(dev, source))));
    }

    [Fact]
    public void ほかの端末の出どころは受け取らない()
    {
        using var child = Child(out var dev);
        using var parent = Database.Open(Path.Combine(_dir, "parent.sqlite3"));
        var c = new SyncStore(child);
        var source = c.LocalSources(dev)[0];
        Assert.Throws<ArgumentException>(() => new SyncStore(parent).Apply(new SyncProjection("d_other", "x", source, c.Read(dev, source))));
        Assert.Throws<ArgumentException>(() => new SyncStore(parent).Apply(new SyncProjection(dev, "x", "vat", c.Read(dev, source))));
    }

    [Fact]
    public void メモは後から直した方を残す()
    {
        using var child = Child(out var dev);
        using var parent = Database.Open(Path.Combine(_dir, "parent.sqlite3"));
        var c = new SyncStore(child);
        var note = new Queries(child).AddNote("day", "2026-11-08", "子で書いた", dev);
        var pending = c.NotesToPush("http://desktop/", dev);
        Assert.Single(pending);
        Assert.Equal(1, new SyncStore(parent).ApplyNotes(pending));
        c.MarkPushed("http://desktop/", "notes", c.NotesMark("http://desktop/", pending));
        Assert.Empty(c.NotesToPush("http://desktop/", dev));

        // 同じ秒にあとから書いたメモも、次に送る
        var later = new Queries(child).AddNote("day", "2026-11-08", "同じ秒に書いた", dev);
        using (var cmd = child.Command("UPDATE notes SET updated_at = (SELECT updated_at FROM notes WHERE id = $a) WHERE id = $b", null, ("$a", note.Id), ("$b", later.Id))) cmd.ExecuteNonQuery();
        Assert.Equal(later.Id, Assert.Single(c.NotesToPush("http://desktop/", dev)).Id);

        // 親であとから直した → 子の古い版を送っても上書きしない
        using (var cmd = parent.Command("UPDATE notes SET body = '親で直した', updated_at = '2099-01-01T00:00:00Z' WHERE id = $id", null, ("$id", note.Id))) cmd.ExecuteNonQuery();
        Assert.Equal(0, new SyncStore(parent).ApplyNotes(pending));
        Assert.Equal("親で直した", new Queries(parent).Note(note.Id)!.Body);
    }

    [Fact]
    public void 子で消したメモは親でも消え_消したあとに親で直したものは残す()
    {
        using var child = Child(out var dev);
        using var parent = Database.Open(Path.Combine(_dir, "parent.sqlite3"));
        var c = new SyncStore(child);
        var q = new Queries(child);
        var a = q.AddNote("day", "2026-11-08", "消すメモ", dev);
        var b = q.AddNote("day", "2026-11-08", "親で直すメモ", dev);
        new SyncStore(parent).ApplyNotes(c.NotesToPush("http://desktop/", dev));
        Assert.Equal(2L, parent.Scalar("SELECT COUNT(*) FROM notes"));

        Assert.True(q.DeleteNote(a.Id));
        Assert.True(q.DeleteNote(b.Id));
        using (var cmd = parent.Command("UPDATE notes SET body = '親で直した', updated_at = '2099-01-01T00:00:00Z' WHERE id = $id", null, ("$id", b.Id))) cmd.ExecuteNonQuery();
        var deletions = c.DeletionsToPush("http://desktop/", dev);
        Assert.Equal(2, deletions.Count);
        // ほかの端末に結び付いたトークンからは消さない
        Assert.Equal(0, new SyncStore(parent).ApplyNoteDeletions(deletions, onlyDevice: "d_other"));
        Assert.Equal(1, new SyncStore(parent).ApplyNoteDeletions(deletions, onlyDevice: dev));
        Assert.Null(new Queries(parent).Note(a.Id));
        Assert.Equal("親で直した", new Queries(parent).Note(b.Id)!.Body);

        // 古い子・戻した子が、消す前のメモを送り直しても入れない
        Assert.Equal(0, new SyncStore(parent).ApplyNotes([new SyncNote(a.Id, "day", "2026-11-08", "消すメモ", dev, a.CreatedAt, a.UpdatedAt)]));
        Assert.Null(new Queries(parent).Note(a.Id));

        c.MarkPushed("http://desktop/", SyncStore.DeletionsKey, c.DeletionsMark("http://desktop/", deletions));
        Assert.Empty(c.DeletionsToPush("http://desktop/", dev));
        // 消した印に本文は残さない
        Assert.Equal(0L, child.Scalar("SELECT COUNT(*) FROM note_deletions WHERE id NOT IN ($a, $b)", ("$a", a.Id), ("$b", b.Id)));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }
}
