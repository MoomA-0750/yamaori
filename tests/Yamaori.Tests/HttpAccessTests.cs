namespace Yamaori.Tests;

/// <summary>入口の見張りを、HTTP の経路まで通して試す。</summary>
[Collection("http")]
public sealed class HttpAccessTests
{
    [Theory]
    [InlineData("GET", "/api/days")]
    [InlineData("GET", "/API/days")]
    [InlineData("GET", "/Api/settings")]
    [InlineData("GET", "/OPENAPI/v1.json")]
    [InlineData("POST", "/MCP")]
    [InlineData("POST", "/API/settings/shutdown")]
    [InlineData("GET", "/api//days")]
    public async Task 外からは_書き方を変えても_認証なしでAPIに届かない(string method, string path)
    {
        await using var h = new HttpHost();
        var c = await h.SendAsync(method, path);
        Assert.Equal(401, c.Response.StatusCode);
    }

    [Theory]
    [InlineData("/api/auth/state")]
    [InlineData("/API/AUTH/STATE")]
    public async Task ログインの画面に要る口は開いている(string path)
    {
        await using var h = new HttpHost();
        var c = await h.SendAsync("GET", path);
        Assert.Equal(200, c.Response.StatusCode);
    }

    [Fact]
    public async Task この_PC_からは認証なしで届く()
    {
        await using var h = new HttpHost();
        var c = await h.SendAsync("GET", "/api/days", loopback: true);
        Assert.Equal(200, c.Response.StatusCode);
    }
}
