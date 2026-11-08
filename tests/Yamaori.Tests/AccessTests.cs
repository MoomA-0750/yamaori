using Yamaori.Core;

namespace Yamaori.Tests;

public class AccessTests
{
    static AccessRequest Local(string method = "GET") =>
        new(FromLoopback: true, HasProxyHeaders: false, TailscaleLogin: null, Host: "localhost", Method: method, Origin: null, ExpectedHost: "localhost", HasValidSession: false, HasValidToken: false);

    static AccessRequest Lan(string method = "GET") =>
        new(FromLoopback: false, HasProxyHeaders: false, TailscaleLogin: null, Host: "192.168.0.10", Method: method, Origin: null, ExpectedHost: "192.168.0.10", HasValidSession: false, HasValidToken: false);

    static AccessRequest ViaTailscale(string login) =>
        new(FromLoopback: true, HasProxyHeaders: true, TailscaleLogin: login, Host: "to-fu.tail1234.ts.net", Method: "GET", Origin: null, ExpectedHost: "to-fu.tail1234.ts.net", HasValidSession: false, HasValidToken: false);

    static AccessSettings Settings(string listen = "local", bool password = false, string[]? ts = null, bool localLogin = false, bool open = false) =>
        new(listen, password, ts ?? [], localLogin, open);

    [Fact]
    public void この_PC_からは既定でログインなしに通す() =>
        Assert.Equal(AccessResult.Allow, AccessPolicy.Decide(Local(), Settings()));

    [Fact]
    public void この_PC_からでもログインを求める設定にできる() =>
        Assert.Equal(AccessResult.NeedLogin, AccessPolicy.Decide(Local(), Settings(password: true, localLogin: true)));

    [Fact]
    public void localhost_でない_Host_は断る_DNSの書き換え対策() =>
        Assert.Equal(AccessResult.BadHost, AccessPolicy.Decide(Local() with { Host = "evil.example", ExpectedHost = "evil.example" }, Settings()));

    [Fact]
    public void プロキシ越しはこの_PC_からでも外として扱う() =>
        Assert.Equal(AccessResult.NeedPassword, AccessPolicy.Decide(Local() with { HasProxyHeaders = true }, Settings()));

    [Fact]
    public void 外からはパスワードを決めるまで通さない() =>
        Assert.Equal(AccessResult.NeedPassword, AccessPolicy.Decide(Lan(), Settings("lan")));

    [Fact]
    public void 外からはログインが要る()
    {
        Assert.Equal(AccessResult.NeedLogin, AccessPolicy.Decide(Lan(), Settings("lan", password: true)));
        Assert.Equal(AccessResult.Allow, AccessPolicy.Decide(Lan() with { HasValidSession = true }, Settings("lan", password: true)));
        Assert.Equal(AccessResult.Allow, AccessPolicy.Decide(Lan() with { HasValidToken = true }, Settings("lan", password: true)));
    }

    [Fact]
    public void 認証なしで外へ開く設定にしたときだけ_外からログインなしで通す() =>
        Assert.Equal(AccessResult.Allow, AccessPolicy.Decide(Lan(), Settings("lan", open: true)));

    [Fact]
    public void Tailscale_は許した人だけ通す()
    {
        var s = Settings("tailscale", ts: ["me@example.com"]);
        Assert.Equal(AccessResult.Allow, AccessPolicy.Decide(ViaTailscale("me@example.com"), s));
        Assert.Equal(AccessResult.TailscaleDenied, AccessPolicy.Decide(ViaTailscale("other@example.com"), s));
    }

    [Fact]
    public void Tailscale_の設定でなければ_Tailscale_の本人の情報は信じない()
    {
        Assert.Equal(AccessResult.NeedPassword, AccessPolicy.Decide(ViaTailscale("me@example.com"), Settings("local", ts: ["me@example.com"])));
        Assert.Equal(AccessResult.NeedLogin, AccessPolicy.Decide(ViaTailscale("me@example.com"), Settings("local", password: true)));
    }

    [Fact]
    public void 外から来た_Tailscale_の印は信じない() =>
        Assert.Equal(AccessResult.NeedPassword, AccessPolicy.Decide(Lan() with { TailscaleLogin = "me@example.com" }, Settings("tailscale", ts: ["me@example.com"])));

    [Fact]
    public void 書き換える要求はほかのサイトから来たら断る()
    {
        Assert.Equal(AccessResult.BadOrigin, AccessPolicy.Decide(Local("POST") with { Origin = "https://evil.example" }, Settings()));
        Assert.Equal(AccessResult.Allow, AccessPolicy.Decide(Local("POST") with { Origin = "http://localhost:47800" }, Settings()));
        Assert.Equal(AccessResult.Allow, AccessPolicy.Decide(Local("POST"), Settings())); // Origin のない（ブラウザでない）アプリ
        // 読むだけの要求は Origin を見ない（ブラウザが中身を渡さない）
        Assert.Equal(AccessResult.Allow, AccessPolicy.Decide(Local() with { Origin = "https://evil.example" }, Settings()));
    }

    [Fact]
    public void tailscale_serve_越しの書き換えは_ホストの名前が合えば通す()
    {
        var r = ViaTailscale("me@example.com") with { Method = "PUT", Origin = "https://to-fu.tail1234.ts.net" };
        Assert.Equal(AccessResult.Allow, AccessPolicy.Decide(r, Settings("tailscale", ts: ["me@example.com"])));
    }
}
