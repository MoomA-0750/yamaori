using System.Text.Json;
using Yamaori.Store;

namespace Yamaori.Tests;

/// <summary>端末のグループ: 作る・合言葉で入る・役を読む・外す、を HTTP まで通して試す。</summary>
[Collection("http")]
public sealed class HttpGroupTests
{
    static async Task<JsonElement> Json(Microsoft.AspNetCore.Http.HttpContext c)
    {
        using var doc = await JsonDocument.ParseAsync(c.Response.Body);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task 合言葉で入り_役を読み_外すとトークンが使えなくなる()
    {
        await using var hub = new HttpHost();
        // グループを作る・合言葉を作る（本人だけ）
        Assert.Equal(200, (await hub.SendAsync("POST", "/api/settings/group/create", loopback: true, body: """{"name":"家の端末"}""")).Response.StatusCode);
        Assert.Equal(401, (await hub.SendAsync("POST", "/api/settings/group/invite")).Response.StatusCode);
        var invite = await Json(await hub.SendAsync("POST", "/api/settings/group/invite", loopback: true));
        var code = invite.GetProperty("code").GetString()!;
        Assert.Matches("^[2-9A-Z]{5}-[2-9A-Z]{5}$", code);

        // 認証なしで挨拶が読める
        var hello = await Json(await hub.SendAsync("GET", "/api/group/hello"));
        Assert.True(hello.GetProperty("isHub").GetBoolean());

        // 違う合言葉は断る
        Assert.Equal(403, (await hub.SendAsync("POST", "/api/group/accept", body: """{"code":"AAAAA-AAAAA","deviceId":"d_laptop","deviceName":"laptop"}""")).Response.StatusCode);
        // 正しい合言葉（区切りや小文字でもよい）で入る
        var accepted = await hub.SendAsync("POST", "/api/group/accept", body: JsonSerializer.Serialize(new { code = code.ToLowerInvariant().Replace("-", " "), deviceId = "d_laptop", deviceName = "laptop" }));
        Assert.Equal(200, accepted.Response.StatusCode);
        var joined = await Json(accepted);
        var token = joined.GetProperty("token").GetString()!;
        Assert.Equal("家の端末", joined.GetProperty("groupName").GetString());
        // 同じ合言葉は二度使えない
        Assert.Equal(403, (await hub.SendAsync("POST", "/api/group/accept", body: JsonSerializer.Serialize(new { code, deviceId = "d_other", deviceName = "other" }))).Response.StatusCode);

        // メンバーのトークンで役を読む
        (string, string)[] bearer = [("Authorization", "Bearer " + token)];
        var sync = await Json(await hub.SendAsync("GET", "/api/sync/group", headers: bearer));
        Assert.Equal(["logs", "photos"], sync.GetProperty("roles").EnumerateArray().Select(x => x.GetString()!).ToArray());
        // まとめ役で役を変える
        Assert.Equal(200, (await hub.SendAsync("PUT", "/api/settings/group/members/d_laptop", loopback: true, body: """{"roles":["logs","vrcx","hub"]}""")).Response.StatusCode);
        sync = await Json(await hub.SendAsync("GET", "/api/sync/group", headers: bearer));
        Assert.Equal(["logs", "vrcx"], sync.GetProperty("roles").EnumerateArray().Select(x => x.GetString()!).ToArray()); // まとめ役にはできない
        // メンバーは VRCX の DB をまとめ役へ送れる（まとめ役で取り込む）
        var junk = await hub.Server.SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Path = "/api/sync/import/vrcx";
            c.Request.Host = new Microsoft.AspNetCore.Http.HostString("localhost", 47899);
            c.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.168.1.50");
            c.Request.Headers.Authorization = "Bearer " + token;
            c.Request.Body = new MemoryStream([1, 2, 3]);
        });
        Assert.Equal(200, junk.Response.StatusCode);
        // 入れ替えはメンバーからはできない。グループのメンバーでないトークンからも受け取らない
        Assert.Equal(400, (await hub.SendAsync("POST", "/api/sync/import/vrcx?replace=true", body: "{}", headers: bearer)).Response.StatusCode);
        string stray;
        using (var db = hub.OpenDb()) stray = new AuthStore(db).CreateToken("stray", TokenScopes.Sync).Secret;
        Assert.Equal(403, (await hub.SendAsync("POST", "/api/sync/import/vat", body: "{}", headers: [("Authorization", "Bearer " + stray)])).Response.StatusCode);
        // もう入っている端末は、別の合言葉でも入り直せない（前のトークンを奪わせない）
        var again = (await Json(await hub.SendAsync("POST", "/api/settings/group/invite", loopback: true))).GetProperty("code").GetString()!;
        Assert.Equal(403, (await hub.SendAsync("POST", "/api/group/accept", body: JsonSerializer.Serialize(new { code = again, deviceId = "d_laptop", deviceName = "x" }))).Response.StatusCode);
        // メンバーのトークンは、記録を読む口には届かない
        Assert.Equal(403, (await hub.SendAsync("GET", "/api/days", headers: bearer)).Response.StatusCode);

        // 外すとトークンが使えなくなる。まとめ役は外せない
        string self;
        using (var db = hub.OpenDb()) self = (string)db.Scalar("SELECT id FROM devices WHERE is_self = 1")!;
        Assert.Equal(400, (await hub.SendAsync("DELETE", $"/api/settings/group/members/{self}", loopback: true)).Response.StatusCode);
        Assert.Equal(200, (await hub.SendAsync("DELETE", "/api/settings/group/members/d_laptop", loopback: true)).Response.StatusCode);
        Assert.Equal(401, (await hub.SendAsync("GET", "/api/sync/group", headers: bearer)).Response.StatusCode);
    }

    [Theory]
    [InlineData("https://nas.example", true)]
    [InlineData("http://truenas.zonkey-musical.ts.net:47800", true)]
    [InlineData("http://100.73.248.17:47800", true)]
    [InlineData("http://127.0.0.1:47800", true)]
    [InlineData("http://192.168.1.101:47800", false)]
    [InlineData("http://100.200.0.1:47800", false)]
    public void まとめ役のアドレスは_https_か_Tailscale_の中だけ(string url, bool ok) =>
        Assert.Equal(ok, Yamaori.Web.GroupApi.SafeHubUrl(new Uri(url)));

    [Fact]
    public void VRCX_と連携する役はグループで_1_台だけ()
    {
        var path = Path.Combine(Path.GetTempPath(), "yamaori-group-" + Guid.NewGuid().ToString("N") + ".sqlite3");
        using var db = Database.Open(path);
        var g = new GroupStore(db);
        g.Create("g", "d_hub", "hub");
        var a = g.Accept(g.Invite().Code, "d_a", "a", null, "d_hub");
        var b = g.Accept(g.Invite().Code, "d_b", "b", null, "d_hub");
        Assert.NotNull(a);
        Assert.NotNull(b);
        g.SetRoles("d_a", ["logs", "vrcx"]);
        g.SetRoles("d_b", ["vrcx"]);
        Assert.DoesNotContain("vrcx", g.RolesOf("d_a"));
        Assert.Contains("vrcx", g.RolesOf("d_b"));
        // 渡したトークンは、その端末に結び付いて使える
        Assert.Equal("d_a", new AuthStore(db).FindToken(a!.Token)!.DeviceId);
    }

    [Fact]
    public void 合言葉は時間が切れると使えない()
    {
        var path = Path.Combine(Path.GetTempPath(), "yamaori-group-" + Guid.NewGuid().ToString("N") + ".sqlite3");
        using var db = Database.Open(path);
        var g = new GroupStore(db);
        g.Create("g", "d_hub", "hub");
        var invite = g.Invite();
        using (var cmd = db.Command("UPDATE group_invites SET expires_at = '2000-01-01T00:00:00Z'")) cmd.ExecuteNonQuery();
        Assert.Null(g.Accept(invite.Code, "d_x", "x", null, "d_hub"));
        // まとめ役そのものは入れない
        Assert.Null(g.Accept(g.Invite().Code, "d_hub", "hub", null, "d_hub"));
    }
    [Fact]
    public async Task メンバーは手元の記録の写しをまとめ役へ移せる()
    {
        await using var hub = new HttpHost();
        Assert.Equal(200, (await hub.SendAsync("POST", "/api/settings/group/create", loopback: true, body: """{"name":"家"}""")).Response.StatusCode);
        var code = (await Json(await hub.SendAsync("POST", "/api/settings/group/invite", loopback: true))).GetProperty("code").GetString()!;
        var token = (await Json(await hub.SendAsync("POST", "/api/group/accept", body: JsonSerializer.Serialize(new { code, deviceId = "d_laptop", deviceName = "laptop" })))).GetProperty("token").GetString()!;

        // メンバーの DB の写し（VAT から取り込んだ訪問とアルバム）
        var dir = Directory.CreateTempSubdirectory("yamaori-move-http-").FullName;
        var copy = Path.Combine(dir, "copy.sqlite3");
        using (var member = Database.Open(Path.Combine(dir, "member.sqlite3")))
        {
            using (var cmd = member.Command("""
                INSERT INTO visits (id, source, device_id, world_id, instance, instance_number, access, region, joined_at, left_at)
                VALUES ('v_vat', 'vat:abc', NULL, 'wrld_a', '1', '1', 'public', 'jp', '2020-01-01T10:00:00Z', '2020-01-01T11:00:00Z');
                INSERT INTO albums (id, name, created_at, updated_at) VALUES ('a_1', '夏', '2026-11-01T00:00:00Z', '2026-11-01T00:00:00Z');
                """)) cmd.ExecuteNonQuery();
            using var vac = member.Command("VACUUM INTO $f", null, ("$f", copy));
            vac.ExecuteNonQuery();
        }
        var sent = await hub.Server.SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Path = "/api/sync/import/move";
            c.Request.Host = new Microsoft.AspNetCore.Http.HostString("localhost", 47899);
            c.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.168.1.50");
            c.Request.Headers.Authorization = "Bearer " + token;
            c.Request.Body = new MemoryStream(File.ReadAllBytes(copy));
        });
        Assert.Equal(200, sent.Response.StatusCode);
        for (var i = 0; i < 100; i++)
        {
            var job = (await Json(await hub.SendAsync("GET", "/api/settings/import/", loopback: true))).GetProperty("job");
            if (!job.GetProperty("running").GetBoolean() && job.GetProperty("finishedAt").ValueKind != JsonValueKind.Null)
            {
                Assert.True(job.GetProperty("ok").GetBoolean(), job.GetProperty("message").GetString());
                break;
            }
            await Task.Delay(100);
        }
        using (var db = hub.OpenDb())
        {
            Assert.Equal("d_laptop", db.Scalar("SELECT device_id FROM visits WHERE id = 'v_vat'"));
            Assert.Equal(1L, db.Scalar("SELECT count(*) FROM albums WHERE name = '夏'"));
        }
        // まとめ役で「移す」は使わない（メンバーの端末で使う）
        Assert.Equal(400, (await hub.SendAsync("POST", "/api/settings/import/move", loopback: true)).Response.StatusCode);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}
