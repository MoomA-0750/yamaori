using Microsoft.Data.Sqlite;

namespace Yamaori.Tests;

/// <summary>画面からの取り込み（VRCX の DB を送る・この PC の VRCX を読む）を HTTP まで通して試す。</summary>
[Collection("http")]
public sealed class HttpImportTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("yamaori-httpimport-").FullName;

    string Vrcx(string worldName = "World A")
    {
        var path = Path.Combine(_dir, $"VRCX-{Guid.NewGuid():N}.sqlite3");
        using var c = new SqliteConnection($"Data Source={path};Pooling=false");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE gamelog_location (id INTEGER PRIMARY KEY, created_at TEXT, location TEXT, world_id TEXT, world_name TEXT, time INTEGER, group_name TEXT);
            CREATE TABLE usr11111111222233334444555555555555_friend_log_history (id INTEGER PRIMARY KEY, created_at TEXT, type TEXT, user_id TEXT, display_name TEXT, previous_display_name TEXT, trust_level TEXT, previous_trust_level TEXT, friend_number INTEGER);
            INSERT INTO gamelog_location (created_at, location, world_id, world_name, time) VALUES ('2024-05-01T12:00:00.000Z', 'wrld_a:100~region(jp)', 'wrld_a', '{worldName}', 1800000);
            """;
        cmd.ExecuteNonQuery();
        return path;
    }

    static async Task<string> WaitAsync(HttpHost h)
    {
        for (var i = 0; i < 100; i++)
        {
            var c = await h.SendAsync("GET", "/api/settings/import/", loopback: true);
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(c.Response.Body);
            var job = doc.RootElement.GetProperty("job");
            if (!job.GetProperty("running").GetBoolean() && job.GetProperty("finishedAt").ValueKind != System.Text.Json.JsonValueKind.Null)
                return job.GetProperty("ok").GetBoolean() + "|" + job.GetProperty("needsReplace").GetBoolean() + "|" + job.GetProperty("message").GetString();
            await Task.Delay(100);
        }
        throw new TimeoutException();
    }

    [Fact]
    public async Task VRCX_の_DB_を送って取り込み_別の_DB_は入れ替えると選ぶまで入れない()
    {
        await using var h = new HttpHost();
        var first = Convert.ToBase64String(File.ReadAllBytes(Vrcx()));
        var r = await h.Server.SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Path = "/api/settings/import/vrcx";
            c.Request.Host = new Microsoft.AspNetCore.Http.HostString("localhost", 47899);
            c.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
            c.Request.Body = new MemoryStream(Convert.FromBase64String(first));
        });
        Assert.Equal(200, r.Response.StatusCode);
        Assert.StartsWith("True|", await WaitAsync(h), StringComparison.Ordinal);
        using (var db = h.OpenDb()) Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM visits WHERE source = 'vrcx'"));

        // この PC の VRCX として、別の DB を読む → 入れ替えると選ぶまで入れない
        var other = Vrcx("World B");
        Assert.Equal(200, (await h.SendAsync("PUT", "/api/settings/import/", loopback: true, body: System.Text.Json.JsonSerializer.Serialize(new { vrcxPath = other }))).Response.StatusCode);
        File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddDays(1)); // いちばん古い訪問が同じでも、別のファイルとして扱わせる
        using (var c = new SqliteConnection($"Data Source={other};Pooling=false"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE gamelog_location SET created_at = '2023-01-01T00:00:00.000Z'";
            cmd.ExecuteNonQuery();
        }
        Assert.Equal(200, (await h.SendAsync("POST", "/api/settings/import/vrcx/local", loopback: true)).Response.StatusCode);
        Assert.StartsWith("False|True|", await WaitAsync(h), StringComparison.Ordinal);
        Assert.Equal(200, (await h.SendAsync("POST", "/api/settings/import/vrcx/local?replace=true", loopback: true)).Response.StatusCode);
        Assert.StartsWith("True|", await WaitAsync(h), StringComparison.Ordinal);
        using (var db = h.OpenDb()) Assert.Equal("World B", db.Scalar("SELECT world_name FROM visits WHERE source = 'vrcx'"));

        // 取り込みはトークンでは届かない（本人だけ）
        string secret;
        using (var db = h.OpenDb()) secret = new Yamaori.Store.AuthStore(db).CreateToken("t", Yamaori.Store.TokenScopes.Full).Secret;
        Assert.Equal(403, (await h.SendAsync("POST", "/api/settings/import/vrcx/local", headers: [("Authorization", "Bearer " + secret)])).Response.StatusCode);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
