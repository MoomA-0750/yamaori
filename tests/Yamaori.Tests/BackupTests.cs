using Yamaori.Store;

namespace Yamaori.Tests;

public sealed class BackupTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("yamaori-backup-").FullName;

    [Fact]
    public void 毎日の写しは一日一つで_決めた日数分だけ残す()
    {
        var o = new YamaoriOptions { DataDir = _dir, BackupKeep = 3 };
        using (var db = Database.Open(o.DatabasePath)) new Queries(db).AddNote("day", "2026-11-08", "メモ", null);
        var day = new DateTime(2026, 11, 1);
        for (var i = 0; i < 5; i++) Assert.NotNull(BackupWorker.RunOnce(o, day.AddDays(i)));
        Assert.Null(BackupWorker.RunOnce(o, day.AddDays(4))); // 同じ日には取り直さない
        var files = Directory.GetFiles(o.BackupPath).Select(f => Path.GetFileName(f)!).Order().ToArray();
        Assert.Equal(["auto-20261103.sqlite3", "auto-20261104.sqlite3", "auto-20261105.sqlite3"], files);
        using (var copy = Database.Open(Path.Combine(o.BackupPath, "auto-20261105.sqlite3")))
            Assert.Equal(1L, copy.Scalar("SELECT COUNT(*) FROM notes"));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        // 途中で止まって壊れた今日の写しは、取り直す
        File.WriteAllText(Path.Combine(o.BackupPath, "auto-20261105.sqlite3"), "壊れた");
        Assert.NotNull(BackupWorker.RunOnce(o, day.AddDays(4)));
        using (var again = Database.Open(Path.Combine(o.BackupPath, "auto-20261105.sqlite3")))
            Assert.Equal(1L, again.Scalar("SELECT COUNT(*) FROM notes"));
        // 手で取った写しには触らない
        File.WriteAllText(Path.Combine(o.BackupPath, "yamaori-20200101-000000.sqlite3"), "");
        BackupWorker.RunOnce(o, day.AddDays(10));
        Assert.True(File.Exists(Path.Combine(o.BackupPath, "yamaori-20200101-000000.sqlite3")));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }
}
