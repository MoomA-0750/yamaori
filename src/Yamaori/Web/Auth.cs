using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Yamaori.Core;
using Yamaori.Store;

namespace Yamaori.Web;

/// <summary>
/// 入口の見張り（要求を通すかを <see cref="AccessPolicy"/> で決める）と、ログイン・パスワード・トークン・設定の API。
/// </summary>
public static class Auth
{
    public const string CookieName = "yamaori_session";
    const string AccessKey = "yamaori.access";

    // 認証なしでも開ける口（ログインの画面を出すため）。Host と Origin の見張りはここでも効かせる
    static readonly string[] OpenApiPaths = ["/api/auth/state", "/api/auth/login"];

    public static void UseAccess(WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path;
            // 経路の照合（大文字小文字を区別しない）を先に済ませてあるので、API などに当たる要求は書き方によらず見張る。
            // 当たらない要求（画面の静的なファイル）だけを、認証なしで通す
            var isApi = ctx.GetEndpoint() != null
                || path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
                || path.StartsWithSegments("/openapi", StringComparison.OrdinalIgnoreCase)
                || path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase);
            var isOpen = OpenApiPaths.Any(o => string.Equals(path.Value?.TrimEnd('/'), o, StringComparison.OrdinalIgnoreCase));
            var db = ctx.RequestServices.GetRequiredService<Database>();
            var store = new AuthStore(db);
            var token = store.FindToken(Bearer(ctx));
            var request = Describe(ctx, store, token != null);
            var settings = Settings(ctx, store);
            var result = AccessPolicy.Decide(request, settings);
            ctx.Items[AccessKey] = result;

            if (result is AccessResult.BadHost or AccessResult.BadOrigin)
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(new { error = result.ToString() }).ConfigureAwait(false);
                return;
            }
            // 画面（静的なファイル）は誰にでも出す。中身は API で取るので、ここでは記録は見えない
            var localAlways = result == AccessResult.NeedLogin && request is { FromLoopback: true, HasProxyHeaders: false, TailscaleLogin: null }
                && AccessPolicy.IsLocalHost(request.Host) && LocalAlways.Any(a => path.StartsWithSegments(a, StringComparison.OrdinalIgnoreCase));
            if (localAlways) result = AccessResult.Allow;
            if (!isApi || isOpen)
            {
                await next(ctx).ConfigureAwait(false);
                return;
            }
            if (result == AccessResult.Allow)
            {
                // 本人として通るか（ログイン・この PC・許した Tailscale の人）。トークンだけ・認証なしの公開だけで通るなら、できることを絞る
                var asUser = localAlways || AccessPolicy.Decide(request with { HasValidToken = false }, settings with { AllowUnauthenticatedRemote = false }) == AccessResult.Allow;
                var denied = asUser ? null : Forbidden(path, ctx.Request.Method,
                    AccessPolicy.Decide(request with { HasValidToken = false }, settings) == AccessResult.Allow ? null : token);
                if (denied == null)
                {
                    if (!asUser && token != null) ctx.Items[TokenKey] = token;
                    await next(ctx).ConfigureAwait(false);
                    return;
                }
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(new { error = denied }).ConfigureAwait(false);
                return;
            }
            ctx.Response.StatusCode = result == AccessResult.TailscaleDenied ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = result.ToString() }).ConfigureAwait(false);
        });
    }

    const string TokenKey = "yamaori.token";

    /// <summary>トークンで通った要求なら、そのトークン（同期の口で端末と照らし合わせる）。</summary>
    public static ApiToken? TokenOf(HttpContext ctx) => ctx.Items[TokenKey] as ApiToken;

    // この PC から直接なら、この PC でもログインを求める設定でも通す口（インストーラーが更新の前に止めるため。ほかのサイトからは Origin で断る）
    static readonly string[] LocalAlways = ["/api/settings/shutdown"];

    // 本人だけ（ログイン・この PC から）が触れる口。トークンや認証なしの公開では触れない（トークンを作り足して居座る、設定を変える、止める、を防ぐ）。
    // 人を忘れる口（/api/people/{id}/forget）も本人だけ（Forbidden で見る）
    static readonly string[] AdminPaths = ["/api/auth/tokens", "/api/auth/password", "/api/settings", "/api/people/forgotten"];

    // 範囲ごとに通す口（方法と道の頭）
    static readonly (string Scope, string Method, string Path, bool Prefix)[] ScopedPaths =
    [
        (TokenScopes.Extension, "GET", "/api/status", false),
        (TokenScopes.Extension, "PUT", "/api/worlds/", true),          // /api/worlds/{id}/details
        (TokenScopes.Extension, "GET", "/api/worlds/missing-details", false),
        (TokenScopes.Extension, "POST", "/api/worlds/save", false),
        (TokenScopes.Extension, "POST", "/api/friends/snapshot", false),
        (TokenScopes.Sync, "GET", "/api/status", false),
        (TokenScopes.Sync, "POST", "/api/sync/", true),
    ];

    /// <summary>本人でない要求が、この口を使えないなら理由を返す。token が null なら認証なしの公開（読むだけ）で通った要求。</summary>
    static string? Forbidden(PathString path, string method, ApiToken? token)
    {
        if (AdminPaths.Any(a => path.StartsWithSegments(a, StringComparison.OrdinalIgnoreCase))) return "AdminOnly";
        // ゴミ箱を空にする（ファイルを本当に消す）のは本人だけ
        if (path.StartsWithSegments("/api/photos/trash/empty", StringComparison.OrdinalIgnoreCase)) return "AdminOnly";
        // 人を忘れるのも本人だけ（忘れた人の一覧は、忘れたい相手の名前を並べたものなので、読むことも本人だけ）
        if (path.StartsWithSegments("/api/people", StringComparison.OrdinalIgnoreCase) && (path.Value ?? "").TrimEnd('/').EndsWith("/forget", StringComparison.OrdinalIgnoreCase)) return "AdminOnly";
        // 子の端末の口は、子の端末のトークンだけ（ほかのトークンが漏れても、子の記録を空で入れ替えて消させない）
        if (token != null && token.Scope != TokenScopes.Sync && path.StartsWithSegments("/api/sync", StringComparison.OrdinalIgnoreCase)) return "OutOfScope";
        if (token == null || token.Scope == TokenScopes.Full) return null;
        var p = path.Value ?? "";
        var ok = ScopedPaths.Any(x => x.Scope == token.Scope && string.Equals(x.Method, method, StringComparison.OrdinalIgnoreCase)
            && (x.Prefix ? p.StartsWith(x.Path, StringComparison.OrdinalIgnoreCase) && (x.Path != "/api/worlds/" || p.EndsWith("/details", StringComparison.OrdinalIgnoreCase))
                         : string.Equals(p.TrimEnd('/'), x.Path, StringComparison.OrdinalIgnoreCase)));
        return ok ? null : "OutOfScope";
    }

    static string? Bearer(HttpContext ctx)
    {
        var authz = ctx.Request.Headers.Authorization.FirstOrDefault();
        return authz != null && authz.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authz[7..].Trim() : null;
    }

    static AccessRequest Describe(HttpContext ctx, AuthStore store, bool hasValidToken)
    {
        var h = ctx.Request.Headers;
        var proxied = h.ContainsKey("X-Forwarded-For") || h.ContainsKey("Forwarded") || h.ContainsKey("X-Real-IP") || h.ContainsKey("X-Forwarded-Host");
        var forwardedHost = h["X-Forwarded-Host"].FirstOrDefault()?.Split(',')[0].Trim();
        var expectedHost = forwardedHost is { Length: > 0 } fh ? StripPort(fh) : ctx.Request.Host.Host;
        var expectedPort = proxied ? (int?)null : ctx.Request.Host.Port ?? (ctx.Request.IsHttps ? 443 : 80);
        return new AccessRequest(
            FromLoopback: ctx.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip),
            HasProxyHeaders: proxied,
            TailscaleLogin: h["Tailscale-User-Login"].FirstOrDefault() is { Length: > 0 } t ? t : null,
            Host: ctx.Request.Host.Host,
            Method: ctx.Request.Method,
            Origin: h.Origin.FirstOrDefault(),
            ExpectedHost: expectedHost,
            HasValidSession: store.ValidSession(ctx.Request.Cookies[CookieName]),
            HasValidToken: hasValidToken,
            ExpectedPort: expectedPort);
    }

    static string StripPort(string host) =>
        host.StartsWith('[') ? host[..(host.IndexOf(']') + 1)] : host.Split(':')[0];

    static AccessSettings Settings(HttpContext ctx, AuthStore store)
    {
        var o = ctx.RequestServices.GetRequiredService<IOptions<YamaoriOptions>>().Value;
        return new AccessSettings(o.Listen, store.PasswordSet, store.GetList("tailscale_logins"),
            store.GetBool("local_requires_login"), store.GetBool("allow_unauthenticated_remote"));
    }

    public sealed record AuthState(bool Allowed, string Reason, bool PasswordSet, bool LoggedIn, bool Local, string? TailscaleLogin, string Listen);
    public sealed record LoginInput(string Password);
    public sealed record PasswordInput(string? Current, string New);
    public sealed record TokenInput(string Name, string? Scope = null);
    public sealed record TokenCreated(ApiToken Token, string Secret);
    public sealed record SettingsView(string Listen, int Port, IReadOnlyList<string> TailscaleLogins, bool LocalRequiresLogin, bool AllowUnauthenticatedRemote, bool PasswordSet, string DeviceName);
    public sealed record SettingsInput(string? Listen, int? Port, IReadOnlyList<string>? TailscaleLogins, bool? LocalRequiresLogin, bool? AllowUnauthenticatedRemote, bool? ConfirmUnauthenticatedRemote, string? DeviceName);

    // ログインの失敗が続く相手を少し待たせる（相手のアドレスごと）。
    // X-Forwarded-For は相手が変えられるので、全体の失敗の数でも止める（10 分に 30 回を超えたら、だれのログインも 5 分止める）
    static readonly ConcurrentDictionary<string, (int Failures, DateTime Until)> Failures = new();
    static readonly Lock GlobalLock = new();
    static readonly SemaphoreSlim Verifying = new(2, 2);
    static readonly Queue<DateTime> GlobalFailures = new();
    static DateTime GlobalUntil = DateTime.MinValue;

    static bool GloballyLocked()
    {
        lock (GlobalLock) return GlobalUntil > DateTime.UtcNow;
    }

    static void CountGlobalFailure()
    {
        lock (GlobalLock)
        {
            var now = DateTime.UtcNow;
            GlobalFailures.Enqueue(now);
            while (GlobalFailures.Count > 0 && GlobalFailures.Peek() < now.AddMinutes(-10)) GlobalFailures.Dequeue();
            if (GlobalFailures.Count > 30) GlobalUntil = now.AddMinutes(5);
        }
    }

    public static void Map(WebApplication app)
    {
        var auth = app.MapGroup("/api/auth");

        auth.MapGet("/state", (HttpContext ctx, Database db, IOptions<YamaoriOptions> o) =>
        {
            var store = new AuthStore(db);
            var result = (AccessResult)ctx.Items[AccessKey]!;
            var local = ctx.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip) && !ctx.Request.Headers.ContainsKey("X-Forwarded-For");
            return new AuthState(result == AccessResult.Allow, result.ToString(), store.PasswordSet, store.ValidSession(ctx.Request.Cookies[CookieName]), local,
                ctx.Request.Headers["Tailscale-User-Login"].FirstOrDefault(), o.Value.Listen);
        }).WithSummary("ログインの状態（通るか、パスワードが決まっているか）");

        auth.MapPost("/login", Results<NoContent, UnauthorizedHttpResult, ProblemHttpResult> (HttpContext ctx, Database db, LoginInput input) =>
        {
            // 相手は、つないできたアドレスで分ける。X-Forwarded-For は、この PC のプロキシ（tailscale serve など）越しのときだけ使う
            var remote = ctx.Connection.RemoteIpAddress;
            var fromLoopback = remote != null && System.Net.IPAddress.IsLoopback(remote);
            var key = remote?.ToString() + (fromLoopback ? "|" + ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault() : "");
            // 全体の止めは、この PC から直接のログインには掛けない（外からの失敗で、本人が自分の PC から入れなくなるのを防ぐ）
            var direct = fromLoopback && !ctx.Request.Headers.ContainsKey("X-Forwarded-For");
            if ((Failures.TryGetValue(key, out var f) && f.Until > DateTime.UtcNow) || (!direct && GloballyLocked()))
                return TypedResults.Problem("ログインの失敗が続いたので、少し待ってから", statusCode: StatusCodes.Status429TooManyRequests);
            if ((input.Password ?? "").Length > 1024) return TypedResults.Unauthorized();
            // パスワードの検証は重い（PBKDF2 60 万回）ので、同時に走らせる数を絞る。空きがなければ待たせずに断る
            if (!Verifying.Wait(TimeSpan.FromSeconds(2)))
                return TypedResults.Problem("ログインが混み合っているので、少し待ってから", statusCode: StatusCodes.Status429TooManyRequests);
            var store = new AuthStore(db);
            bool ok;
            try { ok = store.VerifyPassword(input.Password ?? ""); }
            finally { Verifying.Release(); }
            if (!ok)
            {
                if (Failures.Count > 10_000) foreach (var old in Failures.Where(x => x.Value.Until < DateTime.UtcNow).Select(x => x.Key).ToList()) Failures.TryRemove(old, out _);
                var n = (f.Failures) + 1;
                Failures[key] = (n, n >= 5 ? DateTime.UtcNow.AddMinutes(Math.Min(30, n - 4)) : DateTime.MinValue);
                CountGlobalFailure();
                return TypedResults.Unauthorized();
            }
            Failures.TryRemove(key, out _);
            var token = store.CreateSession(ctx.Request.Headers.UserAgent.FirstOrDefault());
            ctx.Response.Cookies.Append(CookieName, token, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = ctx.Request.IsHttps || ctx.Request.Headers["X-Forwarded-Proto"] == "https",
                MaxAge = AuthStore.SessionLifetime,
                Path = "/",
            });
            return TypedResults.NoContent();
        }).WithSummary("パスワードでログインする（Cookie を返す）");

        auth.MapPost("/logout", (HttpContext ctx, Database db) =>
        {
            new AuthStore(db).DeleteSession(ctx.Request.Cookies[CookieName]);
            ctx.Response.Cookies.Delete(CookieName);
            return TypedResults.NoContent();
        }).WithSummary("ログアウトする");

        auth.MapPost("/password", Results<NoContent, BadRequest<string>, UnauthorizedHttpResult> (Database db, PasswordInput input) =>
        {
            var store = new AuthStore(db);
            if (store.PasswordSet && !store.VerifyPassword(input.Current ?? "")) return TypedResults.Unauthorized();
            if (input.New is not { Length: >= 8 }) return TypedResults.BadRequest("パスワードは 8 文字以上");
            store.SetPassword(input.New);
            return TypedResults.NoContent();
        }).WithSummary("パスワードを決める・変える（変えると、ほかのログインは切れる）");

        auth.MapGet("/tokens", (Database db) => new AuthStore(db).Tokens()).WithSummary("ほかのアプリ用の API のトークンの一覧");

        auth.MapPost("/tokens", Results<Ok<TokenCreated>, BadRequest<string>> (Database db, TokenInput input) =>
        {
            if (string.IsNullOrWhiteSpace(input.Name)) return TypedResults.BadRequest("名前を付ける");
            // 範囲は必ず選ぶ（黙って「全部」のトークンを作らない）
            if (input.Scope is not { } scope) return TypedResults.BadRequest($"範囲（scope）を選ぶ: {string.Join("・", TokenScopes.All)}");
            if (!TokenScopes.All.Contains(scope)) return TypedResults.BadRequest($"範囲は {string.Join("・", TokenScopes.All)}");
            var (token, secret) = new AuthStore(db).CreateToken(input.Name.Trim(), scope);
            return TypedResults.Ok(new TokenCreated(token, secret));
        }).WithSummary("API のトークンを作る（中身はこのときしか返さない。Authorization: Bearer で使う。範囲は必須: full（設定と子の端末の口以外の全部）・extension（拡張機能とスマホの保存）・sync（子の端末）") ;

        auth.MapDelete("/tokens/{id}", Results<NoContent, NotFound> (Database db, string id) =>
            new AuthStore(db).DeleteToken(id) ? TypedResults.NoContent() : TypedResults.NotFound())
            .WithSummary("API のトークンを消す");

        var settings = app.MapGroup("/api/settings");

        settings.MapGet("/", (Database db, IOptions<YamaoriOptions> o) => View(new AuthStore(db), o.Value))
            .WithSummary("設定（見せ方・認証）");

        settings.MapPut("/", Results<Ok<SettingsResult>, BadRequest<string>> (Database db, IOptions<YamaoriOptions> o, SettingsInput input) =>
        {
            var store = new AuthStore(db);
            var listen = input.Listen ?? o.Value.Listen;
            if (listen is not ("local" or "lan" or "tailscale")) return TypedResults.BadRequest("listen は local・lan・tailscale");
            if (input.Port is { } port && port is < 1024 or > 65535) return TypedResults.BadRequest("番号は 1024〜65535");
            if (input.DeviceName is { } dn && (dn.Trim().Length is 0 or > 64)) return TypedResults.BadRequest("端末の名前は 1〜64 文字");
            var open = input.AllowUnauthenticatedRemote ?? store.GetBool("allow_unauthenticated_remote");
            if (open && input.AllowUnauthenticatedRemote == true && input.ConfirmUnauthenticatedRemote != true)
                return TypedResults.BadRequest("認証なしで外へ開くには、confirmUnauthenticatedRemote: true が要る（記録がだれにでも見える）");
            if (listen == "lan" && !store.PasswordSet && !open) return TypedResults.BadRequest("家の中から見せるには、先にパスワードを決める");
            if (input.TailscaleLogins != null) store.Set("tailscale_logins", string.Join(",", input.TailscaleLogins.Select(x => x.Trim()).Where(x => x.Length > 0)));
            if (input.LocalRequiresLogin is { } lr)
            {
                if (lr && !store.PasswordSet) return TypedResults.BadRequest("この PC でもログインを求めるには、先にパスワードを決める");
                store.Set("local_requires_login", lr ? "true" : "false");
            }
            store.Set("allow_unauthenticated_remote", open ? "true" : "false");
            // 見せ方・番号・端末の名前は、起こし直したときに効く。データのフォルダの yamaori.json に書く
            var restart = WriteConfig(o.Value, input.Listen, input.Port, input.DeviceName);
            return TypedResults.Ok(new SettingsResult(View(store, o.Value) with { Listen = listen, Port = input.Port ?? o.Value.Port, DeviceName = input.DeviceName ?? o.Value.DeviceName }, restart));
        }).WithSummary("設定を変える（見せ方・番号は起こし直すと効く）");

        settings.MapPost("/shutdown", (IHostApplicationLifetime life) =>
        {
            // インストーラーが入れ替える前に呼ぶ。読み込みの途中でも区切って止まる（強制的に止めると、NAS との読み書きが片付くまでファイルが放されない）
            _ = Task.Run(async () => { await Task.Delay(200).ConfigureAwait(false); life.StopApplication(); });
            return TypedResults.Accepted((string?)null);
        }).WithSummary("Yamaori を止める（インストーラーが使う）");

        settings.MapPost("/restart", (IHostApplicationLifetime life) =>
        {
            // 新しい自分を起こしてから止まる（新しい方は、こちらが止まるのを待ってから動き出す）
            // 起こしたときと同じ引数で起こす（dotnet で起こしたときは、最初の引数の dll も渡す）
            var exe = Environment.ProcessPath;
            if (exe != null)
            {
                var all = Environment.GetCommandLineArgs();
                var viaDotnet = Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
                var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
                foreach (var a in viaDotnet ? all : all.Skip(1)) psi.ArgumentList.Add(a);
                System.Diagnostics.Process.Start(psi);
            }
            _ = Task.Run(async () => { await Task.Delay(300).ConfigureAwait(false); life.StopApplication(); });
            return TypedResults.Accepted((string?)null);
        }).WithSummary("Yamaori を起こし直す");
    }

    public sealed record SettingsResult(SettingsView Settings, bool RestartRequired);

    public sealed record PhotoSettingsView(IReadOnlyList<string> PhotoDirs, IReadOnlyList<string> EffectivePhotoDirs, string Kind, string Target,
        string? Username, bool PasswordSet, string Retention, int RetentionDays, double IntervalMinutes);
    public sealed record PhotoSettingsInput(IReadOnlyList<string>? PhotoDirs, string? Kind, string? Target, string? Username, string? Password,
        string? Retention, int? RetentionDays, double? IntervalMinutes, bool? ForgetHostKey = null);
    public sealed record ConnectionTest(bool Ok, string Message);

    static PhotoSettingsView PhotoView(YamaoriOptions o) => new(o.PhotoDirs, o.EffectivePhotoDirs, o.PhotoUpload.Kind, o.PhotoUpload.Target,
        o.PhotoUpload.Username, !string.IsNullOrEmpty(o.PhotoUpload.Password), o.PhotoUpload.Retention, o.PhotoUpload.RetentionDays, o.PhotoUpload.IntervalMinutes);

    public sealed record SyncSettingsView(string Role, string? ParentUrl, bool ParentTokenSet, double SyncMinutes);
    public sealed record SyncSettingsInput(string? Role, string? ParentUrl, string? ParentToken, double? SyncMinutes);

    /// <summary>端末の役割（ひとりで・子）。yamaori.json に書き、起こし直すと効く。</summary>
    public static void MapSyncSettings(WebApplication app)
    {
        var g = app.MapGroup("/api/settings/sync");
        g.MapGet("/", (IOptions<YamaoriOptions> o) => new SyncSettingsView(o.Value.Role, o.Value.ParentUrl, !string.IsNullOrEmpty(o.Value.ParentToken), o.Value.SyncMinutes))
            .WithSummary("端末の役割（ひとりで・子）");
        g.MapPut("/", Results<Ok<SyncSettingsView>, BadRequest<string>> (IOptions<YamaoriOptions> o, SyncSettingsInput input) =>
        {
            if (input.Role is { } r && r is not ("standalone" or "child")) return TypedResults.BadRequest("role は standalone・child");
            if (input.ParentUrl is { Length: > 0 } u && !(Uri.TryCreate(u.Trim(), UriKind.Absolute, out var pu) && (pu.Scheme == Uri.UriSchemeHttp || pu.Scheme == Uri.UriSchemeHttps)))
                return TypedResults.BadRequest("親のアドレスは http://… か https://… の形");
            if (input.SyncMinutes is { } sm && (!double.IsFinite(sm) || sm < 1 || sm > 1440)) return TypedResults.BadRequest("送る間隔は 1〜1440 分");
            var json = ConfigFile.Update(o.Value.DataDir, json =>
            {
                if (input.Role != null) json["Role"] = input.Role;
                if (input.ParentUrl != null) json["ParentUrl"] = input.ParentUrl.Trim();
                if (input.ParentToken is { Length: > 0 } t) json["ParentToken"] = Photos.Secrets.Protect(t.Trim());
                if (input.SyncMinutes is { } m) json["SyncMinutes"] = Math.Max(1, m);
            });
            return TypedResults.Ok(new SyncSettingsView((string?)json["Role"] ?? "standalone", (string?)json["ParentUrl"], json["ParentToken"] != null, (double?)json["SyncMinutes"] ?? 10));
        }).WithSummary("端末の役割を変える（起こし直すと効く。トークンは暗号化して置く）");
        g.MapPost("/test", async (IOptions<YamaoriOptions> o, SyncSettingsInput input, CancellationToken cancel) =>
        {
            var url = input.ParentUrl ?? o.Value.ParentUrl;
            // 置いてあるトークンは、置いてある親のアドレスへ試すときだけ使う（ほかのアドレスへ送らせない）
            var sameParent = string.Equals(url?.Trim().TrimEnd('/'), o.Value.ParentUrl?.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
            var token = input.ParentToken is { Length: > 0 } t ? t.Trim() : sameParent ? Photos.Secrets.Unprotect(o.Value.ParentToken) : null;
            if (string.IsNullOrEmpty(url)) return new ConnectionTest(false, "親のアドレスが決まっていない");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            if (token != null) http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            try
            {
                using var res = await http.GetAsync(url.TrimEnd('/') + "/api/status", cancel).ConfigureAwait(false);
                if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized) return new ConnectionTest(false, "親が断った（トークンを確かめる）");
                if (!res.IsSuccessStatusCode) return new ConnectionTest(false, $"親が {(int)res.StatusCode} を返した");
                var status = await res.Content.ReadFromJsonAsync<Api.Status>(cancel).ConfigureAwait(false);
                return new ConnectionTest(true, $"親（{status?.DeviceName}）につながった");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return new ConnectionTest(false, ex.Message); }
        }).WithSummary("親に試しにつなぐ");
    }

    /// <summary>写真の設定（見張るフォルダ・送り先・手元の残し方）。yamaori.json に書き、起こし直すと効く。</summary>
    public static void MapPhotoSettings(WebApplication app)
    {
        var g = app.MapGroup("/api/settings/photos");
        g.MapGet("/", (IOptions<YamaoriOptions> o) => PhotoView(o.Value)).WithSummary("写真の設定");

        g.MapPut("/", Results<Ok<PhotoSettingsView>, BadRequest<string>> (IOptions<YamaoriOptions> o, Database db, PhotoSettingsInput input) =>
        {
            if (input.Kind is { } k && k is not ("none" or "folder" or "webdav" or "sftp")) return TypedResults.BadRequest("kind は none・folder・webdav・sftp");
            if (input.Retention is { } r && r is not ("keep" or "days" or "after_upload")) return TypedResults.BadRequest("retention は keep・days・after_upload");
            if (input.RetentionDays is { } rd && rd is < 1 or > 3650) return TypedResults.BadRequest("残す日数は 1〜3650");
            if (input.IntervalMinutes is { } im && (!double.IsFinite(im) || im < 1 || im > 1440)) return TypedResults.BadRequest("送る間隔は 1〜1440 分");
            // 種類と送り先は、変えたあとの組み合わせで確かめる（種類だけ変えて、前の送り先が合わなくなるのを防ぐ）
            var kindToCheck = input.Kind ?? o.Value.PhotoUpload.Kind;
            var targetToCheck = (input.Target ?? o.Value.PhotoUpload.Target).Trim();
            if (kindToCheck != "none" && targetToCheck.Length > 0 && CheckTarget(kindToCheck, targetToCheck) is { } bad) return TypedResults.BadRequest(bad);
            if (input.PhotoDirs != null)
                foreach (var pd in input.PhotoDirs.Where(x => x.Trim().Length > 0))
                    if (CheckPhotoDir(pd.Trim(), o.Value.DataDir) is { } badDir) return TypedResults.BadRequest(badDir);
            var before = o.Value.PhotoUpload;
            ConfigFile.Update(o.Value.DataDir, json =>
            {
                if (input.PhotoDirs != null) json["PhotoDirs"] = new JsonArray(input.PhotoDirs.Where(d => d.Trim().Length > 0).Select(d => (JsonNode)d.Trim()).ToArray());
                var up = json["PhotoUpload"] as JsonObject ?? [];
                if (input.Kind != null) up["Kind"] = input.Kind;
                if (input.Target != null) up["Target"] = input.Target.Trim();
                if (input.Username != null) up["Username"] = input.Username;
                if (input.Password is { Length: > 0 } pw) up["Password"] = Photos.Secrets.Protect(pw);
                if (input.Retention != null) up["Retention"] = input.Retention;
                if (input.RetentionDays is { } d) up["RetentionDays"] = Math.Max(1, d);
                if (input.IntervalMinutes is { } m) up["IntervalMinutes"] = Math.Max(1, m);
                json["PhotoUpload"] = up;
            });
            // 送り先（種類・アドレス・ユーザー名）を変えたとき（と、覚え直すと選んだとき）だけ、SFTP の相手の鍵を忘れ、次につないだときに覚え直す。
            // 残す日数などを変えただけでは忘れない（覚え直す隙を作らない）
            var targetChanged = input.ForgetHostKey == true || (input.Kind != null && input.Kind != before.Kind)
                || (input.Target != null && !string.Equals(input.Target.Trim(), before.Target.Trim(), StringComparison.Ordinal))
                || (input.Username != null && input.Username != (before.Username ?? ""));
            if (targetChanged)
                using (var del = db.Command("DELETE FROM settings WHERE key LIKE $p", null, ("$p", Photos.PhotoSync.HostKeyPrefix + "%")))
                    del.ExecuteNonQuery();
            var path = ConfigFile.PathIn(o.Value.DataDir);
            // 書いた内容を読み直して返す（効くのは起こし直したあと）
            var next = new YamaoriOptions();
            new ConfigurationBuilder().AddJsonFile(path, optional: true).Build().Bind(next);
            next.DataDir = o.Value.DataDir;
            next.LogDir = o.Value.LogDir;
            return TypedResults.Ok(PhotoView(next));
        }).WithSummary("写真の設定を変える（起こし直すと効く。パスワードは暗号化して置く）");

        g.MapPost("/test", async (IOptions<YamaoriOptions> o, Database db, PhotoSettingsInput input, CancellationToken cancel) =>
        {
            var saved = o.Value.PhotoUpload;
            var kind = input.Kind ?? saved.Kind;
            var targetUrl = input.Target ?? saved.Target;
            // 置いてあるユーザー名とパスワードは、置いてある送り先へ試すときだけ使う（ほかの送り先へ送らせない）
            var sameTarget = kind == saved.Kind && string.Equals(targetUrl.Trim(), saved.Target.Trim(), StringComparison.OrdinalIgnoreCase);
            var opt = new PhotoUploadOptions
            {
                Kind = kind,
                Target = targetUrl,
                Username = input.Username ?? (sameTarget ? saved.Username : null),
                Password = input.Password is { Length: > 0 } pw ? Photos.Secrets.Protect(pw) : sameTarget ? saved.Password : null,
            };
            // SFTP は覚えている相手の鍵で確かめる（鍵が変わった相手へパスワードを送らない）
            using var target = Photos.PhotoSync.Create(opt, new AuthStore(db));
            if (target == null) return new ConnectionTest(false, "送り先が決まっていない");
            try
            {
                // ないはずのファイルの大きさを訊く（つながって、答えが返るか）
                await target.SizeAsync("yamaori-connection-test/none.png", cancel).ConfigureAwait(false);
                if (opt.Kind == "folder" && !Directory.Exists(opt.Target)) return new ConnectionTest(false, $"フォルダが見つからない: {opt.Target}");
                if (target is Photos.SftpTarget sftp && sftp.HostKey != null)
                    return new ConnectionTest(true, $"つながった（相手の鍵 {sftp.HostKey}{(sftp.SeenHostKey != null ? "。初めての相手なので、NAS に出ている鍵と同じか確かめる" : "。覚えている鍵と同じ")}）");
                return new ConnectionTest(true, "つながった");
            }
            catch (Exception ex)
            {
                return new ConnectionTest(false, ex.Message);
            }
        }).WithSummary("写真の送り先に試しにつなぐ");
    }

    static SettingsView View(AuthStore store, YamaoriOptions o) =>
        new(o.Listen, o.Port, store.GetList("tailscale_logins"), store.GetBool("local_requires_login"), store.GetBool("allow_unauthenticated_remote"), store.PasswordSet, o.DeviceName);

    /// <summary>yamaori.json に、変えた項目だけを書く。今の設定と変わったら true（起こし直しが要る）。</summary>
    /// <summary>送り先のアドレスの形を確かめる（おかしければ理由）。</summary>
    static string? CheckTarget(string kind, string target) => kind switch
    {
        "webdav" when !(Uri.TryCreate(target, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
            => "WebDAV の送り先は https://… の形",
        "sftp" when !(Uri.TryCreate(target, UriKind.Absolute, out var u) && u.Scheme == "sftp" && u.Host.Length > 0)
            => "SFTP の送り先は sftp://ホスト/フォルダ の形",
        "folder" when !Path.IsPathFullyQualified(target) => "フォルダの送り先は、ドライブか \\\\NAS から始まる道",
        _ => null,
    };

    /// <summary>見張る写真のフォルダを確かめる。ドライブそのもの（全部を読み、記録から開けてしまう）と、Yamaori のデータのフォルダは断る。</summary>
    static string? CheckPhotoDir(string dir, string dataDir)
    {
        if (!Path.IsPathFullyQualified(dir)) return $"写真のフォルダは、ドライブか \\\\NAS から始まる道: {dir}";
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        var root = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? "");
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase) || full.Length <= 1) return $"ドライブ全体は見張れない: {dir}";
        var data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDir));
        if (full.StartsWith(data, StringComparison.OrdinalIgnoreCase) || data.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return $"Yamaori のデータのフォルダと重なるフォルダは見張れない: {dir}";
        return null;
    }

    static bool WriteConfig(YamaoriOptions o, string? listen, int? port, string? deviceName)
    {
        if (listen == null && port == null && deviceName == null) return false;
        ConfigFile.Update(o.DataDir, json =>
        {
            if (listen != null) json["Listen"] = listen;
            if (port != null) json["Port"] = port;
            if (deviceName != null) json["DeviceName"] = deviceName;
        });
        return (listen != null && listen != o.Listen) || (port != null && port != o.Port) || (deviceName != null && deviceName != o.DeviceName);
    }
}
