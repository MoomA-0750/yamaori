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
        // メンバーのトークンは、記録を読む口には届かない
        Assert.Equal(403, (await hub.SendAsync("GET", "/api/days", headers: bearer)).Response.StatusCode);

        // 外すとトークンが使えなくなる。まとめ役は外せない
        string self;
        using (var db = hub.OpenDb()) self = (string)db.Scalar("SELECT id FROM devices WHERE is_self = 1")!;
        Assert.Equal(400, (await hub.SendAsync("DELETE", $"/api/settings/group/members/{self}", loopback: true)).Response.StatusCode);
        Assert.Equal(200, (await hub.SendAsync("DELETE", "/api/settings/group/members/d_laptop", loopback: true)).Response.StatusCode);
        Assert.Equal(401, (await hub.SendAsync("GET", "/api/sync/group", headers: bearer)).Response.StatusCode);
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
}
