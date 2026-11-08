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

    // ログインの失敗が続く相手を少し待たせる（相手のアドレスごと）。
    // X-Forwarded-For は相手が変えられるので、全体の失敗の数でも止める（10 分に 30 回を超えたら、だれのログインも 5 分止める）
    static readonly ConcurrentDictionary<string, (int Failures, DateTime Until)> Failures = new();
    static readonly Lock GlobalLock = new();
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
            var key = ctx.Connection.RemoteIpAddress?.ToString() + "|" + ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if ((Failures.TryGetValue(key, out var f) && f.Until > DateTime.UtcNow) || GloballyLocked())
                return TypedResults.Problem("ログインの失敗が続いたので、少し待ってから", statusCode: StatusCodes.Status429TooManyRequests);
            var store = new AuthStore(db);
            if (!store.VerifyPassword(input.Password ?? ""))
            {
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
        string? Retention, int? RetentionDays, double? IntervalMinutes);
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
            if (input.ParentUrl is { Length: > 0 } u && !Uri.TryCreate(u, UriKind.Absolute, out _)) return TypedResults.BadRequest("親のアドレスは http://… の形");
            var path = Path.Combine(o.Value.DataDir, "yamaori.json");
            JsonObject json;
            try { json = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? [] : []; }
            catch (JsonException) { json = []; }
            if (input.Role != null) json["Role"] = input.Role;
            if (input.ParentUrl != null) json["ParentUrl"] = input.ParentUrl.Trim();
            if (input.ParentToken is { Length: > 0 } t) json["ParentToken"] = Photos.Secrets.Protect(t.Trim());
            if (input.SyncMinutes is { } m) json["SyncMinutes"] = Math.Max(1, m);
            Directory.CreateDirectory(o.Value.DataDir);
            File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return TypedResults.Ok(new SyncSettingsView((string?)json["Role"] ?? "standalone", (string?)json["ParentUrl"], json["ParentToken"] != null, (double?)json["SyncMinutes"] ?? 10));
        }).WithSummary("端末の役割を変える（起こし直すと効く。トークンは暗号化して置く）");
        g.MapPost("/test", async (IOptions<YamaoriOptions> o, SyncSettingsInput input, CancellationToken cancel) =>
        {
            var url = input.ParentUrl ?? o.Value.ParentUrl;
            var token = input.ParentToken is { Length: > 0 } t ? t.Trim() : Photos.Secrets.Unprotect(o.Value.ParentToken);
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

        g.MapPut("/", Results<Ok<PhotoSettingsView>, BadRequest<string>> (IOptions<YamaoriOptions> o, PhotoSettingsInput input) =>
        {
            if (input.Kind is { } k && k is not ("none" or "folder" or "webdav" or "sftp")) return TypedResults.BadRequest("kind は none・folder・webdav・sftp");
            if (input.Retention is { } r && r is not ("keep" or "days" or "after_upload")) return TypedResults.BadRequest("retention は keep・days・after_upload");
            var path = Path.Combine(o.Value.DataDir, "yamaori.json");
            JsonObject json;
            try { json = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? [] : []; }
            catch (JsonException) { json = []; }
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
            Directory.CreateDirectory(o.Value.DataDir);
            File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            // 書いた内容を読み直して返す（効くのは起こし直したあと）
            var next = new YamaoriOptions();
            new ConfigurationBuilder().AddJsonFile(path, optional: true).Build().Bind(next);
            next.DataDir = o.Value.DataDir;
            next.LogDir = o.Value.LogDir;
            return TypedResults.Ok(PhotoView(next));
        }).WithSummary("写真の設定を変える（起こし直すと効く。パスワードは暗号化して置く）");

        g.MapPost("/test", async (IOptions<YamaoriOptions> o, PhotoSettingsInput input, CancellationToken cancel) =>
        {
            var opt = new PhotoUploadOptions
            {
                Kind = input.Kind ?? o.Value.PhotoUpload.Kind,
                Target = input.Target ?? o.Value.PhotoUpload.Target,
                Username = input.Username ?? o.Value.PhotoUpload.Username,
                Password = input.Password is { Length: > 0 } pw ? Photos.Secrets.Protect(pw) : o.Value.PhotoUpload.Password,
            };
            using var target = Photos.PhotoSync.Create(opt);
            if (target == null) return new ConnectionTest(false, "送り先が決まっていない");
            try
            {
                // ないはずのファイルの大きさを訊く（つながって、答えが返るか）
                await target.SizeAsync("yamaori-connection-test/none.png", cancel).ConfigureAwait(false);
                if (opt.Kind == "folder" && !Directory.Exists(opt.Target)) return new ConnectionTest(false, $"フォルダが見つからない: {opt.Target}");
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
