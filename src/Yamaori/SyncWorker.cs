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
public sealed class SyncWorker(IOptions<YamaoriOptions> options, YamaoriState state, ILogger<SyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (o.Role != "child" || string.IsNullOrEmpty(o.ParentUrl)) return;
        logger.LogInformation("子の端末として、記録を {Parent} へ送る", o.ParentUrl);
        using var http = new HttpClient { BaseAddress = new Uri(o.ParentUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(2) };
        if (Photos.Secrets.Unprotect(o.ParentToken) is { } token) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // 起きた直後はログを読み終えるのを少し待つ
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var r = await PushAsync(http, o, state, stoppingToken).ConfigureAwait(false);
                if (r.Sources + r.Notes > 0) logger.LogInformation("親へ送った: 出どころ {Sources}・メモ {Notes}", r.Sources, r.Notes);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                logger.LogWarning("親 {Parent} へ送れなかった（次の回に送る）: {Message}", o.ParentUrl, ex.Message);
            }
            try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, o.SyncMinutes)), stoppingToken).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    public sealed record PushResult(int Sources, int Notes);

    public static async Task<PushResult> PushAsync(HttpClient http, YamaoriOptions o, YamaoriState state, CancellationToken cancel)
    {
        var parent = http.BaseAddress!.ToString();
        using var db = Database.Open(o.DatabasePath);
        var sync = new SyncStore(db);
        int sources = 0, notes = 0;
        foreach (var source in sync.LocalSources(state.DeviceId))
        {
            var payload = new SyncProjection(state.DeviceId, state.DeviceName, source, sync.Read(state.DeviceId, source));
            var hash = SyncStore.Hash(payload);
            if (sync.PushedHash(parent, source) == hash) continue;
            using var res = await http.PostAsJsonAsync("api/sync/projection", payload, Web, cancel).ConfigureAwait(false);
            res.EnsureSuccessStatusCode();
            sync.MarkPushed(parent, source, hash);
            sources++;
        }
        var pending = sync.NotesToPush(parent, state.DeviceId);
        if (pending.Count > 0)
        {
            using var res = await http.PostAsJsonAsync("api/sync/notes", pending, Web, cancel).ConfigureAwait(false);
            res.EnsureSuccessStatusCode();
            sync.MarkPushed(parent, "notes", sync.NotesMark(parent, pending));
            notes = pending.Count;
        }
        return new PushResult(sources, notes);
    }

    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict };
}
