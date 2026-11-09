using System.Text.Json.Nodes;
using Yamaori;

namespace Yamaori.Tests;

public class ConfigFileTests
{
    static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "yamaori-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void 書き足しても_ほかの項目は残る()
    {
        var d = TempDir();
        File.WriteAllText(Path.Combine(d, "yamaori.json"), """{ "Port": 47801, "ParentToken": "plain:x" }""");
        ConfigFile.Update(d, j => j["Listen"] = "lan");
        var j = JsonNode.Parse(File.ReadAllText(Path.Combine(d, "yamaori.json")))!.AsObject();
        Assert.Equal(47801, (int)j["Port"]!);
        Assert.Equal("plain:x", (string)j["ParentToken"]!);
        Assert.Equal("lan", (string)j["Listen"]!);
        Assert.False(File.Exists(Path.Combine(d, "yamaori.json.tmp")));
    }

    [Fact]
    public void 壊れた設定は退けて_既定で起きる()
    {
        var d = TempDir();
        File.WriteAllText(Path.Combine(d, "yamaori.json"), """{ "Port": 478""");
        var broken = ConfigFile.Recover(d);
        Assert.NotNull(broken);
        Assert.False(File.Exists(Path.Combine(d, "yamaori.json")));
        Assert.Equal("""{ "Port": 478""", File.ReadAllText(broken!));
        // 読める設定には触らない
        File.WriteAllText(Path.Combine(d, "yamaori.json"), """{ "Port": 47800 }""");
        Assert.Null(ConfigFile.Recover(d));
        Assert.True(File.Exists(Path.Combine(d, "yamaori.json")));
    }

    [Fact]
    public void 戻せない秘密は_落ちずに読めないと分かる()
    {
        Assert.Null(Yamaori.Photos.Secrets.Unprotect("dpapi:@@not-base64@@"));
        Assert.Equal("x", Yamaori.Photos.Secrets.Unprotect("plain:x"));
        Assert.False(Yamaori.Photos.Secrets.Unreadable("plain:x"));
        Assert.False(Yamaori.Photos.Secrets.Unreadable(null));
    }
}
