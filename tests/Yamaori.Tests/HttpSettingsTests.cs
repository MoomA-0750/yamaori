namespace Yamaori.Tests;

/// <summary>設定に危ない値を入れられないことを、HTTP まで通して試す。</summary>
[Collection("http")]
public sealed class HttpSettingsTests
{
    [Theory]
    [InlineData("PUT", "/api/settings", """{"port":70000}""")]
    [InlineData("PUT", "/api/settings", """{"port":-1}""")]
    [InlineData("PUT", "/api/settings", """{"deviceName":"   "}""")]
    [InlineData("PUT", "/api/settings/sync", """{"parentUrl":"ftp://parent/"}""")]
    [InlineData("PUT", "/api/settings/sync", """{"syncMinutes":1e300}""")]
    [InlineData("PUT", "/api/settings/photos", """{"photoDirs":["/"]}""")]
    [InlineData("PUT", "/api/settings/photos", """{"photoDirs":["relative/dir"]}""")]
    [InlineData("PUT", "/api/settings/photos", """{"kind":"webdav","target":"file:///etc"}""")]
    [InlineData("PUT", "/api/settings/photos", """{"kind":"sftp","target":"https://nas/"}""")]
    [InlineData("PUT", "/api/settings/photos", """{"intervalMinutes":0}""")]
    public async Task おかしな値は断る(string method, string path, string body)
    {
        await using var h = new HttpHost();
        var c = await h.SendAsync(method, path, loopback: true, body: body);
        Assert.Equal(400, c.Response.StatusCode);
    }

    [Fact]
    public async Task データのフォルダを写真のフォルダにはできない()
    {
        await using var h = new HttpHost();
        var dir = System.Text.Json.JsonSerializer.Serialize(h.Options.DataDir);
        var c = await h.SendAsync("PUT", "/api/settings/photos", loopback: true, body: $$"""{"photoDirs":[{{dir}}]}""");
        Assert.Equal(400, c.Response.StatusCode);
    }

    [Fact]
    public async Task まともな値は受ける()
    {
        await using var h = new HttpHost();
        var c = await h.SendAsync("PUT", "/api/settings/photos", loopback: true, body: """{"kind":"sftp","target":"sftp://nas.local/photo/VRChat","intervalMinutes":10}""");
        Assert.Equal(200, c.Response.StatusCode);
    }

    [Fact]
    public async Task 種類だけ変えても_前の送り先と合わなければ断る()
    {
        await using var h = new HttpHost(extra: new Dictionary<string, string?> { ["PhotoUpload:Kind"] = "folder", ["PhotoUpload:Target"] = "/srv/nas/VRChat" });
        Assert.Equal(400, (await h.SendAsync("PUT", "/api/settings/photos", loopback: true, body: """{"kind":"webdav"}""")).Response.StatusCode);
        Assert.Equal(200, (await h.SendAsync("PUT", "/api/settings/photos", loopback: true, body: """{"kind":"webdav","target":"https://nas.local/dav"}""")).Response.StatusCode);
    }
}
