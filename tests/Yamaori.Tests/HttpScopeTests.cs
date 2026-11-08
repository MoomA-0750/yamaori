using System.Text.Json;
using Yamaori.Store;

namespace Yamaori.Tests;

/// <summary>トークンの範囲、本人だけの口、認証なしの公開（読むだけ）、Origin の番号を、HTTP まで通して試す。</summary>
[Collection("http")]
public sealed class HttpScopeTests
{
    static (string, string)[] Bearer(string secret) => [("Authorization", "Bearer " + secret)];

    static string Token(HttpHost h, string scope)
    {
        using var db = h.OpenDb();
        return new AuthStore(db).CreateToken("t-" + scope, scope).Secret;
    }

    static string Projection(string deviceId) => JsonSerializer.Serialize(new
    {
        deviceId,
        deviceName = "laptop",
        source = $"log:{deviceId}/output_log_2026-11-08_02-33-56.txt",
        projection = new { deviceId, session = (object?)null, visits = Array.Empty<object>(), encounters = Array.Empty<object>(), events = Array.Empty<object>() },
    });

    [Fact]
    public async Task 拡張機能のトークンは_拡張機能の口だけ通る()
    {
        await using var h = new HttpHost();
        var t = Bearer(Token(h, TokenScopes.Extension));
        Assert.Equal(204, (await h.SendAsync("PUT", "/api/worlds/wrld_00000000-0000-0000-0000-000000000001/details", body: """{"name":"A"}""", headers: t)).Response.StatusCode);
        Assert.Equal(200, (await h.SendAsync("GET", "/api/status", headers: t)).Response.StatusCode);
        Assert.Equal(403, (await h.SendAsync("GET", "/api/days", headers: t)).Response.StatusCode);
        Assert.Equal(403, (await h.SendAsync("DELETE", "/api/notes/n_x", headers: t)).Response.StatusCode);
        Assert.Equal(403, (await h.SendAsync("POST", "/api/sync/notes", body: "[]", headers: t)).Response.StatusCode);
    }

    [Theory]
    [InlineData(TokenScopes.Full)]
    [InlineData(TokenScopes.Extension)]
    [InlineData(TokenScopes.Sync)]
    public async Task どのトークンでも_設定とトークンと止めることには触れない(string scope)
    {
        await using var h = new HttpHost();
        var t = Bearer(Token(h, scope));
        Assert.Equal(403, (await h.SendAsync("GET", "/api/auth/tokens", headers: t)).Response.StatusCode);
        Assert.Equal(403, (await h.SendAsync("POST", "/api/auth/tokens", body: """{"name":"x"}""", headers: t)).Response.StatusCode);
        Assert.Equal(403, (await h.SendAsync("POST", "/api/auth/password", body: """{"new":"aaaaaaaaaa"}""", headers: t)).Response.StatusCode);
        Assert.Equal(403, (await h.SendAsync("PUT", "/api/settings", body: """{"listen":"lan"}""", headers: t)).Response.StatusCode);
        Assert.Equal(403, (await h.SendAsync("POST", "/API/Settings/shutdown", headers: t)).Response.StatusCode);
    }

    [Fact]
    public async Task 全部のトークンは_記録を読んで書ける()
    {
        await using var h = new HttpHost();
        var t = Bearer(Token(h, TokenScopes.Full));
        Assert.Equal(200, (await h.SendAsync("GET", "/api/days", headers: t)).Response.StatusCode);
        Assert.Equal(204, (await h.SendAsync("PUT", "/api/worlds/wrld_00000000-0000-0000-0000-000000000002/details", body: """{"name":"B"}""", headers: t)).Response.StatusCode);
    }

    [Fact]
    public async Task この_PC_からは_本人の口に届く()
    {
        await using var h = new HttpHost();
        Assert.Equal(200, (await h.SendAsync("GET", "/api/auth/tokens", loopback: true)).Response.StatusCode);
    }

    [Fact]
    public async Task 子の端末のトークンは_最初に送った端末に結び付き_ほかの端末と親そのものは入れ替えられない()
    {
        await using var h = new HttpHost();
        var t = Bearer(Token(h, TokenScopes.Sync));
        Assert.Equal(204, (await h.SendAsync("POST", "/api/sync/projection", body: Projection("d_aaaaaaaaaaaaaaaa"), headers: t)).Response.StatusCode);
        Assert.Equal(204, (await h.SendAsync("POST", "/api/sync/projection", body: Projection("d_aaaaaaaaaaaaaaaa"), headers: t)).Response.StatusCode);
        Assert.Equal(403, (await h.SendAsync("POST", "/api/sync/projection", body: Projection("d_bbbbbbbbbbbbbbbb"), headers: t)).Response.StatusCode);
        Assert.Equal(403, (await h.SendAsync("GET", "/api/days", headers: t)).Response.StatusCode);

        string self;
        using (var db = h.OpenDb()) self = (string)db.Scalar("SELECT id FROM devices WHERE is_self = 1")!;
        var full = Bearer(Token(h, TokenScopes.Full));
        Assert.Equal(400, (await h.SendAsync("POST", "/api/sync/projection", body: Projection(self), headers: full)).Response.StatusCode);
    }

    [Fact]
    public async Task 子の端末のトークンは_ほかの端末のメモを書き換えない()
    {
        await using var h = new HttpHost();
        var t = Bearer(Token(h, TokenScopes.Sync));
        await h.SendAsync("POST", "/api/sync/projection", body: Projection("d_aaaaaaaaaaaaaaaa"), headers: t);
        using (var db = h.OpenDb())
        using (var cmd = db.Command("INSERT INTO notes (id, target_type, target_id, body, device_id, created_at, updated_at) VALUES ('n_parent', 'day', '2026-11-08', 'parent', NULL, '2026-11-08T00:00:00Z', '2026-11-08T00:00:00Z')"))
            cmd.ExecuteNonQuery();
        var notes = """
            [{"id":"n_parent","targetType":"day","targetId":"2026-11-08","body":"overwritten","deviceId":"d_aaaaaaaaaaaaaaaa","createdAt":"2026-11-08T00:00:00Z","updatedAt":"2026-11-09T00:00:00Z"},
             {"id":"n_child","targetType":"day","targetId":"2026-11-08","body":"child","deviceId":"d_aaaaaaaaaaaaaaaa","createdAt":"2026-11-08T00:00:00Z","updatedAt":"2026-11-09T00:00:00Z"},
             {"id":"n_other","targetType":"day","targetId":"2026-11-08","body":"other","deviceId":"d_bbbbbbbbbbbbbbbb","createdAt":"2026-11-08T00:00:00Z","updatedAt":"2026-11-09T00:00:00Z"}]
            """;
        Assert.Equal(200, (await h.SendAsync("POST", "/api/sync/notes", body: notes, headers: t)).Response.StatusCode);
        using var db2 = h.OpenDb();
        Assert.Equal("parent", db2.Scalar("SELECT body FROM notes WHERE id = 'n_parent'"));
        Assert.Equal("child", db2.Scalar("SELECT body FROM notes WHERE id = 'n_child'"));
        Assert.Null(db2.Scalar("SELECT body FROM notes WHERE id = 'n_other'"));
    }

    [Fact]
    public async Task 認証なしの公開は_読むことだけ()
    {
        await using var h = new HttpHost();
        using (var db = h.OpenDb()) new AuthStore(db).Set("allow_unauthenticated_remote", "true");
        Assert.Equal(200, (await h.SendAsync("GET", "/api/days")).Response.StatusCode);
        Assert.Equal(401, (await h.SendAsync("PUT", "/api/worlds/wrld_00000000-0000-0000-0000-000000000002/details", body: """{"name":"B"}""")).Response.StatusCode);
        Assert.Equal(403, (await h.SendAsync("GET", "/api/auth/tokens")).Response.StatusCode);
        Assert.Equal(403, (await h.SendAsync("GET", "/api/settings")).Response.StatusCode);
    }

    [Fact]
    public async Task 同じ_PC_のほかの番号のサイトからの書き換えは断る()
    {
        await using var h = new HttpHost(password: null);
        var other = await h.SendAsync("PUT", "/api/worlds/wrld_00000000-0000-0000-0000-000000000003/details", loopback: true, body: """{"name":"C"}""",
            headers: [("Origin", "http://localhost:3000")]);
        Assert.Equal(403, other.Response.StatusCode);
        var same = await h.SendAsync("PUT", "/api/worlds/wrld_00000000-0000-0000-0000-000000000003/details", loopback: true, body: """{"name":"C"}""",
            headers: [("Origin", "http://localhost:47899")]);
        Assert.Equal(204, same.Response.StatusCode);
    }

    [Fact]
    public async Task この_PC_でもログインを求める設定でも_この_PC_から止める口だけは通る()
    {
        await using var h = new HttpHost();
        using (var db = h.OpenDb()) new AuthStore(db).Set("local_requires_login", "true");
        Assert.Equal(401, (await h.SendAsync("GET", "/api/days", loopback: true)).Response.StatusCode);
        Assert.Equal(401, (await h.SendAsync("GET", "/api/settings", loopback: true)).Response.StatusCode);
        Assert.Equal(403, (await h.SendAsync("POST", "/api/settings/shutdown", loopback: true, headers: [("Origin", "http://localhost:3000")])).Response.StatusCode);
        Assert.Equal(401, (await h.SendAsync("POST", "/api/settings/shutdown")).Response.StatusCode); // 外からは通さない
        // 最後に本当に止める（試験の後片付けで止まっていてよい）
        Assert.Equal(202, (await h.SendAsync("POST", "/api/settings/shutdown", loopback: true)).Response.StatusCode);
    }

    [Fact]
    public async Task 結び付く前の子の端末のトークンは_メモの端末に結び付き_黙って捨てない()
    {
        await using var h = new HttpHost();
        var t = Bearer(Token(h, TokenScopes.Sync));
        var notes = """[{"id":"n_c","targetType":"day","targetId":"2026-11-08","body":"child","deviceId":"d_cccccccccccccccc","createdAt":"2026-11-08T00:00:00Z","updatedAt":"2026-11-09T00:00:00Z"}]""";
        Assert.Equal(200, (await h.SendAsync("POST", "/api/sync/notes", body: notes, headers: t)).Response.StatusCode);
        using var db = h.OpenDb();
        Assert.Equal("child", db.Scalar("SELECT body FROM notes WHERE id = 'n_c'"));
        // 結び付いたので、ほかの端末の記録は入れ替えられない
        Assert.Equal(403, (await h.SendAsync("POST", "/api/sync/projection", body: Projection("d_dddddddddddddddd"), headers: t)).Response.StatusCode);
    }
}
