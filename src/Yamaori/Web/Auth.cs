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
            var path = ctx.Request.Path.Value ?? "/";
            var isApi = path.StartsWith("/api/", StringComparison.Ordinal) || path.StartsWith("/openapi", StringComparison.Ordinal) || path.StartsWith("/mcp", StringComparison.Ordinal);
            var db = ctx.RequestServices.GetRequiredService<Database>();
            var store = new AuthStore(db);
            var result = AccessPolicy.Decide(Describe(ctx, store), Settings(ctx, store));
            ctx.Items[AccessKey] = result;

            if (result is AccessResult.BadHost or AccessResult.BadOrigin)
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(new { error = result.ToString() }).ConfigureAwait(false);
                return;
            }
            // 画面（静的なファイル）は誰にでも出す。中身は API で取るので、ここでは記録は見えない
            if (result == AccessResult.Allow || !isApi || OpenApiPaths.Contains(path))
            {
                await next(ctx).ConfigureAwait(false);
                return;
            }
            ctx.Response.StatusCode = result == AccessResult.TailscaleDenied ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = result.ToString() }).ConfigureAwait(false);
        });
    }

    static AccessRequest Describe(HttpContext ctx, AuthStore store)
    {
        var h = ctx.Request.Headers;
        var proxied = h.ContainsKey("X-Forwarded-For") || h.ContainsKey("Forwarded") || h.ContainsKey("X-Real-IP") || h.ContainsKey("X-Forwarded-Host");
        var forwardedHost = h["X-Forwarded-Host"].FirstOrDefault()?.Split(',')[0].Trim();
        var expectedHost = forwardedHost is { Length: > 0 } fh ? StripPort(fh) : ctx.Request.Host.Host;
        string? bearer = null;
        var authz = h.Authorization.FirstOrDefault();
        if (authz != null && authz.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) bearer = authz[7..].Trim();
        return new AccessRequest(
            FromLoopback: ctx.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip),
            HasProxyHeaders: proxied,
            TailscaleLogin: h["Tailscale-User-Login"].FirstOrDefault() is { Length: > 0 } t ? t : null,
            Host: ctx.Request.Host.Host,
            Method: ctx.Request.Method,
            Origin: h.Origin.FirstOrDefault(),
            ExpectedHost: expectedHost,
            HasValidSession: store.ValidSession(ctx.Request.Cookies[CookieName]),
            HasValidToken: store.ValidToken(bearer));
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
    public sealed record TokenInput(string Name);
    public sealed record TokenCreated(ApiToken Token, string Secret);
    public sealed record SettingsView(string Listen, int Port, IReadOnlyList<string> TailscaleLogins, bool LocalRequiresLogin, bool AllowUnauthenticatedRemote, bool PasswordSet, string DeviceName);
    public sealed record SettingsInput(string? Listen, int? Port, IReadOnlyList<string>? TailscaleLogins, bool? LocalRequiresLogin, bool? AllowUnauthenticatedRemote, bool? ConfirmUnauthenticatedRemote, string? DeviceName);

    // ログインの失敗が続く相手を少し待たせる（相手のアドレスごと）
    static readonly ConcurrentDictionary<string, (int Failures, DateTime Until)> Failures = new();

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
            var key = ctx.Connection.RemoteIpAddress?.ToString() + "|" + ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (Failures.TryGetValue(key, out var f) && f.Until > DateTime.UtcNow)
                return TypedResults.Problem("ログインの失敗が続いたので、少し待ってから", statusCode: StatusCodes.Status429TooManyRequests);
            var store = new AuthStore(db);
            if (!store.VerifyPassword(input.Password ?? ""))
            {
                var n = (f.Failures) + 1;
                Failures[key] = (n, n >= 5 ? DateTime.UtcNow.AddMinutes(Math.Min(30, n - 4)) : DateTime.MinValue);
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
            var (token, secret) = new AuthStore(db).CreateToken(input.Name.Trim());
            return TypedResults.Ok(new TokenCreated(token, secret));
        }).WithSummary("API のトークンを作る（中身はこのときしか返さない。Authorization: Bearer で使う）");

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

    static SettingsView View(AuthStore store, YamaoriOptions o) =>
        new(o.Listen, o.Port, store.GetList("tailscale_logins"), store.GetBool("local_requires_login"), store.GetBool("allow_unauthenticated_remote"), store.PasswordSet, o.DeviceName);

    /// <summary>yamaori.json に、変えた項目だけを書く。今の設定と変わったら true（起こし直しが要る）。</summary>
    static bool WriteConfig(YamaoriOptions o, string? listen, int? port, string? deviceName)
    {
        if (listen == null && port == null && deviceName == null) return false;
        var path = Path.Combine(o.DataDir, "yamaori.json");
        JsonObject json;
        try { json = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? [] : []; }
        catch (JsonException) { json = []; }
        if (listen != null) json["Listen"] = listen;
        if (port != null) json["Port"] = port;
        if (deviceName != null) json["DeviceName"] = deviceName;
        Directory.CreateDirectory(o.DataDir);
        File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return (listen != null && listen != o.Listen) || (port != null && port != o.Port) || (deviceName != null && deviceName != o.DeviceName);
    }
}
