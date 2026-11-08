namespace Yamaori.Core;

/// <summary>要求について分かっていること（判断に要るものだけ）。</summary>
public sealed record AccessRequest(
    bool FromLoopback,          // つないできた相手がこの PC（127.0.0.1・::1）か
    bool HasProxyHeaders,       // X-Forwarded-For・Forwarded・X-Real-IP など、プロキシを通った印があるか
    string? TailscaleLogin,     // Tailscale-User-Login（tailscale serve が付ける）
    string Host,                // Host（ポートを除く）
    string Method,
    string? Origin,             // Origin（ブラウザが付ける）
    string ExpectedHost,        // 自分のホストの名前（プロキシ越しなら X-Forwarded-Host、なければ Host。ポートを除く）
    bool HasValidSession,       // ログインの Cookie が有効か
    bool HasValidToken);        // API のトークンが有効か

/// <summary>設定のうち、判断に要るもの。</summary>
public sealed record AccessSettings(
    string Listen,                          // local・lan・tailscale
    bool PasswordSet,
    IReadOnlyCollection<string> TailscaleLogins,
    bool LocalRequiresLogin,                // この PC からでもログインを求めるか（既定は求めない）
    bool AllowUnauthenticatedRemote);       // 認証なしで外から見せる（危ない。既定は切る）

public enum AccessResult
{
    Allow,
    NeedLogin,          // ログインかトークンが要る
    NeedPassword,       // パスワードがまだ決まっていないので、外からは通せない
    BadHost,            // Host が localhost でない（DNS の書き換えで localhost を狙うものを断る）
    BadOrigin,          // 書き換える要求が、ほかのサイトから来た
    TailscaleDenied,    // Tailscale の本人が、許した人に入っていない
}

/// <summary>
/// 要求を通すかを決める。外から見える入口なので、ここだけで決めて試験で全部の場合を確かめる。
/// </summary>
public static class AccessPolicy
{
    static readonly string[] LocalHosts = ["localhost", "127.0.0.1", "::1", "[::1]"];

    public static bool IsLocalHost(string host) => LocalHosts.Contains(host, StringComparer.OrdinalIgnoreCase);

    public static AccessResult Decide(AccessRequest r, AccessSettings s)
    {
        // 書き換える要求は、ほかのサイトから来たら断る（ログインの Cookie を悪用させない）
        // （tailscale serve 越しでは scheme と番号がずれるので、ホストの名前で比べる）
        // 正しいトークンを持つ要求は見ない。トークンはブラウザが勝手に付けないので、なりすまし（CSRF）にならない（拡張機能が使う）
        var writes = r.Method is not ("GET" or "HEAD" or "OPTIONS");
        if (writes && !r.HasValidToken && r.Origin != null && !SameHost(r.Origin, r.ExpectedHost))
            return AccessResult.BadOrigin;

        var authenticated = r.HasValidSession || r.HasValidToken;

        // tailscale serve 越し（この PC へつないでくるが、Tailscale の本人の情報が付く）
        if (r.FromLoopback && r.TailscaleLogin != null)
        {
            if (authenticated) return AccessResult.Allow;
            if (s.Listen == "tailscale" && s.TailscaleLogins.Contains(r.TailscaleLogin, StringComparer.OrdinalIgnoreCase)) return AccessResult.Allow;
            return s.Listen == "tailscale" ? AccessResult.TailscaleDenied : Remote(s);
        }

        // この PC から直接
        if (r.FromLoopback && !r.HasProxyHeaders)
        {
            if (!IsLocalHost(r.Host)) return authenticated ? AccessResult.Allow : AccessResult.BadHost;
            if (authenticated || !s.LocalRequiresLogin) return AccessResult.Allow;
            return AccessResult.NeedLogin;
        }

        // 外から（家の中の端末、リバースプロキシ越し）
        if (authenticated) return AccessResult.Allow;
        return Remote(s);
    }

    static bool SameHost(string origin, string host) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var u) && string.Equals(u.Host.Trim('[', ']'), host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase);

    static AccessResult Remote(AccessSettings s)
    {
        if (s.AllowUnauthenticatedRemote) return AccessResult.Allow;
        return s.PasswordSet ? AccessResult.NeedLogin : AccessResult.NeedPassword;
    }
}
