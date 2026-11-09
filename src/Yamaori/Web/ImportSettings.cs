using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Yamaori.Store;

namespace Yamaori.Web;

/// <summary>VRChatActivityTools（VAT）からの移行と、VRCX との連携（本人だけ。/api/settings の下）。</summary>
public static class ImportSettings
{
    public sealed record LastImport(DateTime? At, string? Message);
    public sealed record VrcxView(string Path, bool PathSet, bool Exists, DateTime? Modified, bool Auto, double Minutes);
    public sealed record ImportView(ImportJobs.ImportStatus Job, LastImport Vat, LastImport Vrcx, VrcxView VrcxLocal);
    public sealed record ImportSettingsInput(string? VrcxPath, bool? VrcxAuto, double? VrcxMinutes);
    public sealed record ImportStarted(bool Started, string Message);

    // VAT・VRCX の DB は何年分もあると大きいので、送れる大きさを広げる（この口だけ）
    const long MaxUpload = 8L << 30;

    static ImportView View(ImportJobs jobs, Database db)
    {
        var o = jobs.Config();
        var path = o.EffectiveVrcxPath;
        var exists = File.Exists(path);
        var (va, vm) = ImportJobs.Last(db, "vat");
        var (xa, xm) = ImportJobs.Last(db, "vrcx");
        return new ImportView(jobs.Current, new LastImport(va, vm), new LastImport(xa, xm),
            new VrcxView(path, !string.IsNullOrWhiteSpace(o.VrcxPath), exists, exists ? File.GetLastWriteTimeUtc(path) : null, o.VrcxAuto, o.VrcxMinutes));
    }

    static async Task<ImportStarted> Forward(ImportJobs jobs, string kind, string file, bool replace, string? account, CancellationToken cancel) =>
        await jobs.ForwardAsync(kind, file, replace, account, cancel) is { } problem
            ? new ImportStarted(false, problem)
            : new ImportStarted(true, "まとめ役へ送った。取り込みの様子は、まとめ役の画面で見る");

    public static void Map(WebApplication app)
    {
        // まとめ役で、メンバーから送られた DB を取り込む（メンバーのトークンで）
        app.MapPost("/api/sync/import/{kind}", async Task<Results<Ok<ImportStarted>, BadRequest<string>, Conflict<string>>> (HttpRequest req, ImportJobs jobs, string kind, bool? replace, string? account, CancellationToken cancel) =>
        {
            if (kind is not ("vat" or "vrcx")) return TypedResults.BadRequest("vat か vrcx");
            if (account is { Length: > 0 } a && !a.StartsWith("usr_", StringComparison.Ordinal)) return TypedResults.BadRequest("アカウントは usr_…");
            var file = await jobs.SaveUploadAsync(req.Body, cancel);
            return jobs.Start(kind, file, replace ?? false, account) ? TypedResults.Ok(new ImportStarted(true, "取り込み始めた")) : TypedResults.Conflict("ほかの取り込みが動いている");
        }).WithMetadata(new RequestSizeLimitAttribute(MaxUpload))
          .WithSummary("メンバーの端末から、VAT・VRCX の DB を受け取って取り込む（まとめ役で）");

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
            var file = await jobs.SaveUploadAsync(req.Body, cancel);
            if (roles.IsMember) return TypedResults.Ok(await Forward(jobs, "vat", file, replace ?? false, account, cancel));
            return TypedResults.Ok(jobs.Start("vat", file, replace ?? false, account) ? new ImportStarted(true, "取り込み始めた") : new ImportStarted(false, "ほかの取り込みが動いている"));
        }).WithMetadata(new RequestSizeLimitAttribute(MaxUpload))
          .WithSummary("VRChatActivityTools の DB（VRChatActivityLog.db）を本文で送って取り込む（移行。replace で前の取り込みと入れ替える）");

        g.MapPost("/vrcx", async Task<Ok<ImportStarted>> (HttpRequest req, ImportJobs jobs, DeviceRoles roles, bool? replace, CancellationToken cancel) =>
        {
            var file = await jobs.SaveUploadAsync(req.Body, cancel);
            if (roles.IsMember) return TypedResults.Ok(await Forward(jobs, "vrcx", file, replace ?? false, null, cancel));
            return TypedResults.Ok(jobs.Start("vrcx", file, replace ?? false) ? new ImportStarted(true, "取り込み始めた") : new ImportStarted(false, "ほかの取り込みが動いている"));
        }).WithMetadata(new RequestSizeLimitAttribute(MaxUpload))
          .WithSummary("VRCX の DB（VRCX.sqlite3）を本文で送って取り込む");

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
