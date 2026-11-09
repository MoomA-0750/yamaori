using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Yamaori.Store;

namespace Yamaori.Web;

/// <summary>VRChatActivityTools（VAT）からの移行と、VRCX との連携（本人だけ。/api/settings の下）。</summary>
public static class ImportSettings
{
    public sealed record LastImport(DateTime? At, string? Message);
    public sealed record VrcxView(string Path, bool PathSet, bool Exists, DateTime? Modified, bool Auto, double Minutes);
    public sealed record ImportView(ImportJobs.ImportStatus Job, LastImport Vat, LastImport Vrcx, VrcxView VrcxLocal, LastImport Move);
    public sealed record ImportSettingsInput(string? VrcxPath, bool? VrcxAuto, double? VrcxMinutes);
    public sealed record ImportStarted(bool Started, string Message);

    // VAT・VRCX の DB は何年分もあると大きいので、送れる大きさを広げる（この口だけ）
    const long MaxUpload = ImportJobs.MaxUpload;

    /// <summary>枠を取ってから本文を受け取り、取り込みを始める（枠が取れなければ本文を読まずに断る）。</summary>
    static async Task<ImportStarted> ReceiveAndStart(HttpRequest req, ImportJobs jobs, string kind, bool replace, string? account, string? deviceId, CancellationToken cancel)
    {
        if (!jobs.TryReserve(kind)) return new ImportStarted(false, "ほかの取り込みが動いている");
        string file;
        try { file = await jobs.SaveUploadAsync(req.Body, cancel); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or Microsoft.AspNetCore.Http.BadHttpRequestException)
        {
            jobs.Release($"受け取れなかった: {ex.Message}");
            return new ImportStarted(false, $"受け取れなかった: {ex.Message}");
        }
        jobs.Start(kind, file, replace, account, deviceId, reserved: true);
        return new ImportStarted(true, "取り込み始めた");
    }

    static ImportView View(ImportJobs jobs, Database db)
    {
        var o = jobs.Config();
        var path = o.EffectiveVrcxPath;
        var exists = File.Exists(path);
        var (va, vm) = ImportJobs.Last(db, "vat");
        var (xa, xm) = ImportJobs.Last(db, "vrcx");
        var (ma, mm) = ImportJobs.Last(db, "move");
        return new ImportView(jobs.Current, new LastImport(va, vm), new LastImport(xa, xm),
            new VrcxView(path, !string.IsNullOrWhiteSpace(o.VrcxPath), exists, exists ? File.GetLastWriteTimeUtc(path) : null, o.VrcxAuto, o.VrcxMinutes),
            new LastImport(ma, mm));
    }

    static async Task<ImportStarted> Forward(ImportJobs jobs, string kind, string file, bool replace, string? account, CancellationToken cancel) =>
        await jobs.ForwardAsync(kind, file, replace, account, cancel) is { } problem
            ? new ImportStarted(false, problem)
            : new ImportStarted(true, "まとめ役へ送った。取り込みの様子は、まとめ役の画面で見る");

    public static void Map(WebApplication app)
    {
        // まとめ役で、メンバーから送られた DB を取り込む（グループのメンバーのトークンで）
        app.MapPost("/api/sync/import/{kind}", async Task<Results<Ok<ImportStarted>, BadRequest<string>, Conflict<string>, ProblemHttpResult>> (HttpContext ctx, Database db, YamaoriState state, ImportJobs jobs, string kind, bool? replace, string? account, CancellationToken cancel) =>
        {
            if (kind is not ("vat" or "vrcx" or "move")) return TypedResults.BadRequest("vat・vrcx・move のどれか");
            if (account is { Length: > 0 } a && !a.StartsWith("usr_", StringComparison.Ordinal)) return TypedResults.BadRequest("アカウントは usr_…");
            // グループのメンバーで、端末に結び付いたトークンだけ。VRCX は「VRCX と連携する」の役の端末だけ
            var groups = new GroupStore(db);
            if (Auth.BearerToken(ctx, new AuthStore(db)) is not { DeviceId: { } device } || !groups.IsHub || groups.Info(state.DeviceId)?.Members.All(m => m.DeviceId != device) != false)
                return TypedResults.Problem("グループのメンバーの端末からだけ受け取る", statusCode: StatusCodes.Status403Forbidden);
            if (kind == "vrcx" && !groups.RolesOf(device).Contains(GroupRoles.Vrcx))
                return TypedResults.Problem("この端末には「VRCX と連携する」の役がない（まとめ役の画面で付ける）", statusCode: StatusCodes.Status403Forbidden);
            // 前の取り込みとの入れ替えは、まとめ役の画面で本人が決める
            if (replace == true) return TypedResults.BadRequest("前の取り込みとの入れ替えは、まとめ役の画面で行う");
            var r = await ReceiveAndStart(ctx.Request, jobs, kind, false, account, device, cancel);
            return r.Started ? TypedResults.Ok(r) : TypedResults.Conflict(r.Message);
        }).WithMetadata(new RequestSizeLimitAttribute(MaxUpload))
          .WithSummary("メンバーの端末から、VAT・VRCX の DB か、その端末の Yamaori の DB の写し（move。手元に持っていた記録を移す）を受け取って取り込む（まとめ役で。その端末の記録として入れる）");

        var g = app.MapGroup("/api/settings/import");
        g.MapGet("/", (ImportJobs jobs, Database db) => View(jobs, db)).WithSummary("取り込みの様子（動いているもの・前の結果・この PC の VRCX）");

        g.MapPut("/", Results<Ok<ImportView>, BadRequest<string>> (ImportJobs jobs, Database db, ImportSettingsInput input) =>
        {
            if (input.VrcxMinutes is { } m && (!double.IsFinite(m) || m < 5 || m > 1440)) return TypedResults.BadRequest("取り込む間隔は 5〜1440 分");
            if (input.VrcxPath is { Length: > 0 } p && !Path.IsPathFullyQualified(p.Trim())) return TypedResults.BadRequest("VRCX の DB の場所は、ドライブか / から始まる道");
            ConfigFile.Update(jobs.Config().DataDir, json =>
            {
                if (input.VrcxPath != null) json["VrcxPath"] = input.VrcxPath.Trim();
                if (input.VrcxAuto != null) json["VrcxAuto"] = input.VrcxAuto;
                if (input.VrcxMinutes != null) json["VrcxMinutes"] = input.VrcxMinutes;
            });
            // 設定を変えたら、止まっていた自動の取り込みをもう一度試す
            new AuthStore(db).Set("vrcx_auto_blocked", null);
            return TypedResults.Ok(View(jobs, db));
        }).WithSummary("VRCX との連携の設定（DB の場所・決まった間隔で取り込むか）");

        g.MapPost("/vat", async Task<Results<Ok<ImportStarted>, BadRequest<string>>> (HttpRequest req, ImportJobs jobs, DeviceRoles roles, bool? replace, string? account, CancellationToken cancel) =>
        {
            if (account is { Length: > 0 } a && !a.StartsWith("usr_", StringComparison.Ordinal)) return TypedResults.BadRequest("アカウントは usr_…");
            if (roles.IsMember) return TypedResults.Ok(await Forward(jobs, "vat", await jobs.SaveUploadAsync(req.Body, cancel), replace ?? false, account, cancel));
            return TypedResults.Ok(await ReceiveAndStart(req, jobs, "vat", replace ?? false, account, null, cancel));
        }).WithMetadata(new RequestSizeLimitAttribute(MaxUpload))
          .WithSummary("VRChatActivityTools の DB（VRChatActivityLog.db）を本文で送って取り込む（移行。replace で前の取り込みと入れ替える）");

        g.MapPost("/vrcx", async Task<Ok<ImportStarted>> (HttpRequest req, ImportJobs jobs, DeviceRoles roles, bool? replace, CancellationToken cancel) =>
        {
            if (roles.IsMember) return TypedResults.Ok(await Forward(jobs, "vrcx", await jobs.SaveUploadAsync(req.Body, cancel), replace ?? false, null, cancel));
            return TypedResults.Ok(await ReceiveAndStart(req, jobs, "vrcx", replace ?? false, null, null, cancel));
        }).WithMetadata(new RequestSizeLimitAttribute(MaxUpload))
          .WithSummary("VRCX の DB（VRCX.sqlite3）を本文で送って取り込む");

        g.MapPost("/move", async Task<Results<Ok<ImportStarted>, BadRequest<string>>> (ImportJobs jobs, DeviceRoles roles, Database db, CancellationToken cancel) =>
        {
            if (!roles.IsMember) return TypedResults.BadRequest("グループのメンバーの端末でだけ使う（まとめ役へ移すため）");
            string snapshot;
            try { snapshot = jobs.Snapshot(jobs.Config().DatabasePath); }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException) { return TypedResults.BadRequest($"この端末の DB を写せない: {ex.Message}"); }
            var r = await Forward(jobs, "move", snapshot, false, null, cancel);
            if (r.Started) new AuthStore(db).Set("import_last:move", System.Text.Json.JsonSerializer.Serialize(new { at = DateTime.UtcNow, message = "まとめ役へ送った（移した結果は、まとめ役の画面の「取り込み」で見る）" }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
            return TypedResults.Ok(r);
        }).WithSummary("この端末が手元に持っていた記録（取り込んだ訪問・アルバム・写真の直し・ゴミ箱・お気に入り・忘れた人・フレンドの写し・メモ）を、まとめ役へ移す（メンバーで。手元は消さない）");

        g.MapPost("/vrcx/local", async Task<Results<Ok<ImportStarted>, BadRequest<string>>> (ImportJobs jobs, DeviceRoles roles, bool? replace, CancellationToken cancel) =>
        {
            var path = jobs.Config().EffectiveVrcxPath;
            if (!File.Exists(path)) return TypedResults.BadRequest($"VRCX の DB が見つからない: {path}");
            string snapshot;
            try { snapshot = jobs.Snapshot(path); }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException) { return TypedResults.BadRequest($"VRCX の DB を読めない: {ex.Message}"); }
            if (roles.IsMember) return TypedResults.Ok(await Forward(jobs, "vrcx", snapshot, replace ?? false, null, cancel));
            return TypedResults.Ok(jobs.Start("vrcx", snapshot, replace ?? false) ? new ImportStarted(true, "取り込み始めた") : new ImportStarted(false, "ほかの取り込みが動いている"));
        }).WithSummary("この PC の VRCX の DB を取り込む（VRCX を使っている間でもよい）");
    }
}
