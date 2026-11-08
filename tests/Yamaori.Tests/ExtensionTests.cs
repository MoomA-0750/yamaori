using Yamaori.Core;
using Yamaori.Store;

namespace Yamaori.Tests;

public sealed class ExtensionTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("yamaori-ext-").FullName;

    [Theory]
    [InlineData("QV Pen と YamaPlayer があります", "動画プレイヤー,QvPen,YamaPlayer")]
    [InlineData("qvpen / iwaSync3 / mirror", "動画プレイヤー,QvPen,iwaSync,ミラー")]
    [InlineData("寝れるワールドです。ソファもあります", "睡眠,チェアー")]
    [InlineData("nothing here", "")]
    public void 説明文の言葉から分類を付ける(string description, string expected) =>
        Assert.Equal(expected, string.Join(",", WorldClassifier.Classify(description)));

    [Fact]
    public void タグからも分類を付ける() =>
        Assert.Contains("ゲーム", WorldClassifier.Classify(null, ["author_tag_game", "system_approved"]));

    [Fact]
    public void ワールドの情報を入れ_情報のないワールドから外れる()
    {
        using var db = Database.Open(Path.Combine(_dir, "y.sqlite3"));
        using (var cmd = db.Command("INSERT INTO worlds (id, name) VALUES ('wrld_a', 'A'), ('wrld_b', 'B')")) cmd.ExecuteNonQuery();
        var ext = new ExtensionStore(db);
        Assert.Equal(2, ext.MissingWorlds().Count);
        ext.SaveWorld("wrld_a", new WorldDetailsInput("A", "usr_x", "X", "QV Pen あり", null, "https://t", "public", 32, ["author_tag_chill"], ["standalonewindows", "android"], 1000, 50));
        Assert.Equal("wrld_b", Assert.Single(ext.MissingWorlds()).Id);
        var info = new Queries(db).World("wrld_a")!.Info;
        Assert.Equal(32, info.Capacity);
        Assert.Equal(["standalonewindows", "android"], info.Platforms);
        Assert.Contains("QvPen", info.Categories);
        Assert.Equal("extension", info.DetailsSource);
    }

    [Fact]
    public void フレンドの一覧を比べて増えた減った名前を変えた人を記録する()
    {
        using var db = Database.Open(Path.Combine(_dir, "y.sqlite3"));
        var ext = new ExtensionStore(db);
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var first = ext.SaveFriends("usr_me", [new("usr_a", "A"), new("usr_b", "B")], t0);
        Assert.True(first.First);
        Assert.Empty(first.Added); // 初めての一覧は出来事にしない
        var r = ext.SaveFriends("usr_me", [new("usr_a", "A2"), new("usr_c", "C")], t0.AddDays(3));
        Assert.Equal(["usr_c"], r.Added);
        Assert.Equal(["usr_b"], r.Removed);
        Assert.Equal(["usr_a"], r.Renamed);
        var events = new Queries(db).Events(kind: "friend").Items;
        Assert.Equal(3, events.Count);
        Assert.All(events, e => Assert.Equal("2026-01-01T00:00:00Z", e.Data["observed_since"]));
        Assert.Equal("A", events.Single(e => e.Data["type"] == "DisplayName").Data["previous_name"]);
        // 同じ一覧を二度送っても、出来事は増えない
        ext.SaveFriends("usr_me", [new("usr_a", "A2"), new("usr_c", "C")], t0.AddDays(4));
        Assert.Equal(3, new Queries(db).Events(kind: "friend").Total);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }
}
