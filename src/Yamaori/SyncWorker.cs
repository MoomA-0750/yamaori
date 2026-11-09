using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Yamaori.Store;
using Yamaori.Web;

namespace Yamaori;

/// <summary>
/// 子の端末で動く。決まった間隔で、この端末のログの記録とメモのうち変わったものを親へ送る。
/// 親につながらない間は送らずに次の回に回す（送ったという印を付けないので、つながったときにまとめて送る）。
/// </summary>
public sealed class SyncWorker(IOptions<YamaoriOptions> options, YamaoriState state, DeviceRoles roles, ILogger<SyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (o.Role != "child" || string.IsNullOrEmpty(o.ParentUrl)) return;
        logger.LogInformation("子の端末として、記録を {Parent} へ送る", o.ParentUrl);
        using var http = new HttpClient { BaseAddress = new Uri(o.ParentUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(2) };
        if (Photos.Secrets.Unprotect(o.ParentToken) is { } token) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        else if (Photos.Secrets.Unreadable(o.ParentToken))
            logger.LogWarning("親のトークンを読めない（ほかの PC かユーザーで置いたもの）。設定の「この端末の役割」でトークンを入れ直す");
        // 起きた直後はログを読み終えるのを少し待つ
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        var warnedOldParent = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // グループのメンバーなら、まとめ役から自分の役を読む（まとめ役で変えた役が、次の回から効く）
                if (o.GroupId != null) await FetchRolesAsync(http, o, stoppingToken).ConfigureAwait(false);
                var r = await PushAsync(http, o, state, stoppingToken, v =>
                {
                    if (warnedOldParent) return;
                    warnedOldParent = true;
                    logger.LogWarning("親の Yamaori（{Version}）がこの端末より古い。消したメモなど、新しい分は親を更新するまで送らない（親は子と同じか新しい版にする）", v);
                }, sendLogs: roles.Has(GroupRoles.Logs)).ConfigureAwait(false);
                if (r.Sources + r.Notes > 0) logger.LogInformation("親へ送った: 出どころ {Sources}・メモ {Notes}", r.Sources, r.Notes);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                logger.LogWarning("親 {Parent} へ送れなかった（次の回に送る）: {Message}", o.ParentUrl, ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "親へ送る支度で失敗した（次の回に送る）");
            }
            try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, o.SyncMinutes)), stoppingToken).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    public sealed record PushResult(int Sources, int Notes);

    async Task FetchRolesAsync(HttpClient http, YamaoriOptions o, CancellationToken cancel)
    {
        try
        {
            var g = await http.GetFromJsonAsync<GroupApi.GroupSync>("api/sync/group", Web, cancel).ConfigureAwait(false);
            if (g == null) return;
            using var db = Database.Open(o.DatabasePath);
            new AuthStore(db).Set(GroupApi.RolesKey, string.Join(',', g.Roles));
            roles.Invalidate();
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            logger.LogWarning("まとめ役に断られた（グループから外されたかもしれない）。設定の「グループ」で確かめる");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { /* 古いまとめ役（グループを知らない） */ }
    }

    public static async Task<PushResult> PushAsync(HttpClient http, YamaoriOptions o, YamaoriState state, CancellationToken cancel, Action<string>? onOldParent = null, bool sendLogs = true)
    {
        var parent = http.BaseAddress!.ToString();
        using var db = Database.Open(o.DatabasePath);
        var sync = new SyncStore(db);
        int sources = 0, notes = 0;
        foreach (var source in sendLogs ? sync.LocalSources(state.DeviceId) : [])
        {
            var payload = new SyncProjection(state.DeviceId, state.DeviceName, source, sync.Read(state.DeviceId, source));
            var hash = SyncStore.Hash(payload);
            if (sync.PushedHash(parent, source) == hash) continue;
            using var res = await http.PostAsJsonAsync("api/sync/projection", payload, Web, cancel).ConfigureAwait(false);
            await EnsureAsync(res, cancel).ConfigureAwait(false);
            sync.MarkPushed(parent, source, hash);
            sources++;
        }
        // 親が古い（やり取りの版が低い）なら、親の知らない送り方は使わずに知らせる（親は子と同じか新しい版にする）
        var status = await http.GetFromJsonAsync<Api.Status>("api/status", Web, cancel).ConfigureAwait(false);
        var protocol = status?.SyncProtocol ?? 0;
        if (protocol < Api.SyncProtocol) onOldParent?.Invoke(status?.Version ?? "?");
        var pending = sync.NotesToPush(parent, state.DeviceId);
        if (pending.Count > 0)
        {
            using var res = await http.PostAsJsonAsync("api/sync/notes", pending, Web, cancel).ConfigureAwait(false);
            await EnsureAsync(res, cancel).ConfigureAwait(false);
            sync.MarkPushed(parent, "notes", sync.NotesMark(parent, pending));
            notes = pending.Count;
        }
        if (protocol >= 2)
        {
            var deletions = sync.DeletionsToPush(parent, state.DeviceId);
            if (deletions.Count > 0)
            {
                using var res = await http.PostAsJsonAsync("api/sync/notes/deletions", deletions, Web, cancel).ConfigureAwait(false);
                await EnsureAsync(res, cancel).ConfigureAwait(false);
                sync.MarkPushed(parent, SyncStore.DeletionsKey, sync.DeletionsMark(parent, deletions));
                notes += deletions.Count;
            }
        }
        return new PushResult(sources, notes);
    }

    /// <summary>断られたら、理由の分かる言葉で投げる（記録のファイルに残る）。</summary>
    static async Task EnsureAsync(HttpResponseMessage res, CancellationToken cancel)
    {
        if (res.IsSuccessStatusCode) return;
        var body = "";
        try { body = (await res.Content.ReadAsStringAsync(cancel).ConfigureAwait(false)).Trim(); } catch (HttpRequestException) { }
        if (body.Length > 300) body = body[..300];
        var hint = res.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => "トークンを確かめる",
            System.Net.HttpStatusCode.Forbidden => "親の設定で「子の端末」の範囲のトークンを作り、この端末に入れ直す",
            _ => "",
        };
        throw new HttpRequestException($"親が {(int)res.StatusCode} を返した。{hint} {body}".Trim(), null, res.StatusCode);
    }

    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict };
}
