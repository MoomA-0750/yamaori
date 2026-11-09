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

    [Fact]
    public void 人の一覧に_フレンドかどうかを出し_絞り込める()
    {
        using var db = Database.Open(Path.Combine(_dir, "f.sqlite3"));
        void Exec(string sql) { using var cmd = db.Command(sql); cmd.ExecuteNonQuery(); }
        foreach (var u in new[] { "usr_a", "usr_b", "usr_c", "usr_d", "usr_e" })
            Exec($"INSERT INTO encounters (id, source, device_id, visit_id, user_id, name, joined_at) VALUES ('e_{u}', 'x', 'd', 'v', '{u}', '{u}', '2026-01-01T00:00:00Z')");
        var ext = new ExtensionStore(db);
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        ext.SaveFriends("usr_me", [new("usr_a", "A"), new("usr_b", "B")], t0);
        ext.SaveFriends("usr_me", [new("usr_a", "A")], t0.AddDays(1)); // b が外れた
        // d はログでフレンド申請を承諾した、e は申請のやり取りだけ
        Exec("""INSERT INTO events (id, source, at, kind, user_id, name, data) VALUES ('n1', 'x', '2026-01-03T00:00:00Z', 'notification', 'usr_d', 'D', '{"type":"friendRequest","direction":"accepted"}')""");
        Exec("""INSERT INTO events (id, source, at, kind, user_id, name, data) VALUES ('n2', 'x', '2026-01-03T00:00:00Z', 'notification', 'usr_e', 'E', '{"type":"friendRequest","direction":"received"}')""");
        var q = new Queries(db);
        string? F(string id) => q.Person(id)!.Person.Friend;
        Assert.Equal("friend", F("usr_a"));
        Assert.Equal("former", F("usr_b"));
        Assert.Null(F("usr_c"));
        Assert.Equal("friend", F("usr_d"));
        Assert.Equal("requested", F("usr_e"));
        Assert.Equal(["usr_a", "usr_d"], q.People(kind: "friend").Items.Select(p => p.UserId).Order().ToArray());
        Assert.Equal(3, q.People(kind: "not_friend").Total);
    }

    [Theory]
    [InlineData("https://api.vrchat.cloud/api/1/image/file_ab/41/256", true)]
    [InlineData("https://assets.vrchat.com/a.png", true)]
    [InlineData("http://api.vrchat.cloud/api/1/image/file_ab/41/256", false)]
    [InlineData("https://evil.example/track.png", false)]
    [InlineData("https://vrchat.cloud.evil.example/a.png", false)]
    [InlineData("javascript:alert(1)", false)]
    public void 画像は_VRChat_の置き場のものだけ受け取る(string url, bool kept)
    {
        Assert.Equal(kept ? url : null, ExtensionStore.VrchatImage(url));
    }

    [Fact]
    public void 長すぎる値は切る()
    {
        var w = ExtensionStore.Clean(new WorldDetailsInput(new string('a', 1000), null, null, new string('d', 10000), null, null, null, null,
            Enumerable.Range(0, 500).Select(i => "t" + i).ToList(), null, null, null));
        Assert.Equal(200, w.Name!.Length);
        Assert.Equal(4000, w.Description!.Length);
        Assert.Equal(100, w.Tags!.Count);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }
}
