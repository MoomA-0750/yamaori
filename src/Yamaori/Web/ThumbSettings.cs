using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using Yamaori.Photos;
using Yamaori.Store;

namespace Yamaori.Web;

/// <summary>縮小を前もって作る設定と、その仕事（本人だけ。/api/settings の下）。</summary>
public static class ThumbSettings
{
    public sealed record ThumbJobView(bool Running, string? Mode, int Total, int Done, int Made, int Failed, DateTime? StartedAt, DateTime? FinishedAt, bool NowRequested);
    public sealed record RemoteThumbDir(string Local, string Remote);
    public sealed record RemoteThumbView(string Host, int Port, string User, bool KeySet, string? PublicKey, IReadOnlyList<RemoteThumbDir> Dirs, int Jobs);
    public sealed record ThumbSettingsView(string Night, bool Sidecar, RemoteThumbView Remote, ThumbJobView Job);
    public sealed record ThumbSettingsInput(string? Night, string? Host, int? Port, string? User, IReadOnlyList<RemoteThumbDir>? Dirs, int? Jobs);
    public sealed record ThumbMessage(bool Ok, string Text);

    static ThumbJobView Job(ThumbJob j) => new(j.Running, j.Mode, j.Total, j.Done, j.Made, j.Failed, j.StartedAt, j.FinishedAt, j.NowRequested);

    static ThumbSettingsView Read(YamaoriOptions o, RemoteThumbs remote, ThumbJob job)
    {
        // 起こし直さなくても効くものは yamaori.json から読み直す
        var r = remote.Current();
        var night = (string?)(ReadJson(o)?["ThumbNight"]) ?? o.ThumbNight;
        return new ThumbSettingsView(night, o.ThumbSidecar,
            new RemoteThumbView(r.Host, r.Port, r.User, !string.IsNullOrEmpty(r.PrivateKey), r.PublicKey, r.Dirs.Select(d => new RemoteThumbDir(d.Local, d.Remote)).ToList(), r.Jobs),
            Job(job));
    }

    static JsonObject? ReadJson(YamaoriOptions o)
    {
        try { return File.Exists(ConfigFile.PathIn(o.DataDir)) ? JsonNode.Parse(File.ReadAllText(ConfigFile.PathIn(o.DataDir)))?.AsObject() : null; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/settings/thumbs");
        g.MapGet("/", (Microsoft.Extensions.Options.IOptions<YamaoriOptions> o, RemoteThumbs remote, ThumbJob job) => Read(o.Value, remote, job))
            .WithSummary("縮小を前もって作る設定と、今の様子");

        g.MapPut("/", Results<Ok<ThumbSettingsView>, BadRequest<string>> (Microsoft.Extensions.Options.IOptions<YamaoriOptions> o, RemoteThumbs remote, ThumbJob job, ThumbSettingsInput input) =>
        {
            if (input.Night is { Length: > 0 } n && !ThumbWorker.TryParseWindow(n, out _, out _)) return TypedResults.BadRequest("夜の時間帯は 01:00-07:00 の形（空なら夜には作らない）");
            if (input.Port is { } p && p is < 1 or > 65535) return TypedResults.BadRequest("番号は 1〜65535");
            if (input.Jobs is { } j && j is < 1 or > 8) return TypedResults.BadRequest("同時に読む数は 1〜8");
            if (input.Host is { Length: > 253 } || input.User is { Length: > 64 }) return TypedResults.BadRequest("アドレスかユーザー名が長すぎる");
            if (input.Dirs is { } ds && ds.Any(d => !d.Remote.StartsWith('/') || d.Remote.Contains('\0', StringComparison.Ordinal) || d.Local.Length == 0))
                return TypedResults.BadRequest("NAS の上の道は / から始める。この端末から見た道も入れる");
            ConfigFile.Update(o.Value.DataDir, json =>
            {
                if (input.Night != null) json["ThumbNight"] = input.Night.Trim();
                var r = json["RemoteThumbs"] as JsonObject ?? [];
                if (input.Host != null) r["Host"] = input.Host.Trim();
                if (input.Port != null) r["Port"] = input.Port;
                if (input.User != null) r["User"] = input.User.Trim();
                if (input.Jobs != null) r["Jobs"] = input.Jobs;
                if (input.Dirs != null)
                    r["Dirs"] = new JsonArray(input.Dirs.Select(d => (JsonNode)new JsonObject { ["Local"] = d.Local.Trim(), ["Remote"] = d.Remote.Trim().TrimEnd('/') }).ToArray());
                json["RemoteThumbs"] = r;
            });
            return TypedResults.Ok(Read(o.Value, remote, job));
        }).WithSummary("縮小を作る設定を変える（夜の時間帯は起こし直すと効く。NAS の設定はすぐ効く）");

        g.MapPost("/now", (ThumbJob job) => { job.RequestNow(); return Job(job); }).WithSummary("この端末で、縮小を今すぐ全部作る");
        g.MapPost("/stop", (ThumbJob job) => { job.Stop(); return Job(job); }).WithSummary("この端末で縮小を作るのをやめる");

        g.MapPost("/remote/key", (RemoteThumbs remote) => new ThumbMessage(true, remote.NewKey()))
            .WithSummary("NAS に入るための鍵を作り直す（返す公開鍵を NAS のユーザーに登録する）");
        g.MapPost("/remote/forget-host-key", (RemoteThumbs remote, Database db) => { remote.ForgetHostKey(new AuthStore(db)); return new ThumbMessage(true, "NAS の鍵を忘れた。次につないだときの鍵を覚える"); })
            .WithSummary("覚えている NAS の鍵を忘れる（NAS を作り直したとき）");
        g.MapPost("/remote/test", async (RemoteThumbs remote, CancellationToken cancel) => await Try(() => remote.TestAsync(cancel)))
            .WithSummary("NAS に試しにつなぐ（Python と Pillow、写真のフォルダに書けるか）");
        g.MapPost("/remote/start", async (RemoteThumbs remote, CancellationToken cancel) => await Try(() => remote.StartAsync(cancel)))
            .WithSummary("NAS で縮小を今すぐ作り始める");
        g.MapGet("/remote/status", async Task<Results<Ok<IReadOnlyList<RemoteThumbs.RemoteThumbStatus>>, BadRequest<string>>> (RemoteThumbs remote, CancellationToken cancel) =>
        {
            try { return TypedResults.Ok(await remote.StatusAsync(cancel)); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or Renci.SshNet.Common.SshException or System.Net.Sockets.SocketException)
            { return TypedResults.BadRequest(ex.Message); }
        }).WithSummary("NAS で縮小を作っている様子");
    }

    static async Task<ThumbMessage> Try(Func<Task<string>> f)
    {
        try { return new ThumbMessage(true, await f()); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Renci.SshNet.Common.SshException or System.Net.Sockets.SocketException or UnauthorizedAccessException)
        { return new ThumbMessage(false, ex.Message); }
    }
}
