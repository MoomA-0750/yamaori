using Microsoft.Extensions.Options;
using Yamaori.Photos;
using Yamaori.Store;
using Yamaori.Web;

namespace Yamaori;

/// <summary>縮小を前もって作る仕事の様子（画面に出す）。</summary>
public sealed class ThumbJob
{
    readonly Lock _gate = new();
    CancellationTokenSource? _now;

    public bool Running { get; private set; }
    /// <summary>night（夜の時間帯）・now（今すぐ）・null。</summary>
    public string? Mode { get; private set; }
    public int Total { get; private set; }
    public int Done { get; private set; }
    public int Made { get; private set; }
    public int Failed { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public DateTime? FinishedAt { get; private set; }

    /// <summary>「今すぐ作る」を頼まれた（夜の時間帯でなくても、終わるまで作る）。</summary>
    public bool NowRequested { get; private set; }

    public void RequestNow() { lock (_gate) NowRequested = true; }

    public void Stop()
    {
        lock (_gate)
        {
            NowRequested = false;
            _now?.Cancel();
        }
    }

    internal CancellationToken Begin(string mode, int total, CancellationToken stopping)
    {
        lock (_gate)
        {
            _now = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            Running = true; Mode = mode; Total = total; Done = 0; Made = 0; Failed = 0; StartedAt = DateTime.UtcNow; FinishedAt = null;
            return _now.Token;
        }
    }

    internal void Step(bool made, bool failed) { lock (_gate) { Done++; if (made) Made++; if (failed) Failed++; } }

    internal void End(bool finished)
    {
        lock (_gate)
        {
            Running = false; FinishedAt = DateTime.UtcNow;
            if (finished) NowRequested = false;
            _now?.Dispose(); _now = null;
        }
    }
}

/// <summary>
/// 写真の縮小（一覧に出す 320）を前もって作る。夜の時間帯（ThumbNight）と、「今すぐ作る」を頼まれたときだけ動く。
/// 新しい写真から順に、手元にないものだけ作る（写真のフォルダに NAS で作った縮小があれば、それを写す）。
/// NAS の上で作る設定があれば、夜の時間帯の始まりに NAS でも作らせる。
/// </summary>
public sealed class ThumbWorker(IOptions<YamaoriOptions> options, Thumbnails thumbs, ThumbJob job, RemoteThumbs remote, ILogger<ThumbWorker> logger) : BackgroundService
{
    public const int PregenSize = 320;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        // 起きた直後はログと写真の目録を読むので少し待つ（「今すぐ作る」を頼まれたら待たない）
        try { for (var i = 0; i < 120 && !job.NowRequested; i++) await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        DateOnly? remoteRunOn = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var night = InWindow(o.ThumbNight, DateTime.Now);
            try
            {
                // 夜の時間帯の始まりに、NAS の上でも作らせる（一晩に 1 回）
                if (night && remote.Configured && remoteRunOn != DateOnly.FromDateTime(DateTime.Now))
                {
                    remoteRunOn = DateOnly.FromDateTime(DateTime.Now);
                    var r = await remote.StartAsync(stoppingToken).ConfigureAwait(false);
                    logger.LogInformation("NAS で縮小を作り始めた: {Message}", r);
                }
                if (night || job.NowRequested) await RunAsync(job.NowRequested ? "now" : "night", o, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("縮小を前もって作れなかった（次の回にやり直す）: {Message}", ex.Message);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(job.NowRequested ? 1 : 60), stoppingToken).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    async Task RunAsync(string mode, YamaoriOptions o, CancellationToken stopping)
    {
        List<(string Id, string Path)> todo;
        using (var db = Database.Open(o.DatabasePath))
            todo = new PhotoCatalog(db).AllNewestFirst().Where(p => !thumbs.Has(p.Id, PregenSize)).ToList();
        if (todo.Count == 0) { if (mode == "now") job.End(finished: true); return; }
        var cancel = job.Begin(mode, todo.Count, stopping);
        var finished = false;
        try
        {
            await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancel }, async (p, ct) =>
            {
                // 夜の分は、時間帯が終わったらやめる（今すぐの分は終わるまで）
                if (mode == "night" && !job.NowRequested && !InWindow(o.ThumbNight, DateTime.Now)) throw new OperationCanceledException();
                string? made = null;
                try { made = File.Exists(p.Path) ? await thumbs.GetAsync(p.Id, p.Path, PregenSize, ct).ConfigureAwait(false) : null; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                job.Step(made != null, made == null);
            }).ConfigureAwait(false);
            finished = true;
            logger.LogInformation("縮小を前もって作った: {Made} 枚（読めなかった {Failed} 枚）", job.Made, job.Failed);
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested) { }
        finally { job.End(finished); }
    }

    /// <summary>"01:00-07:00" の形を読む。読めなければ false。</summary>
    public static bool TryParseWindow(string? window, out TimeOnly from, out TimeOnly to)
    {
        from = to = default;
        if (string.IsNullOrWhiteSpace(window)) return false;
        var parts = window.Split('-', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2
            && TimeOnly.TryParseExact(parts[0], ["H:mm", "HH:mm"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out from)
            && TimeOnly.TryParseExact(parts[1], ["H:mm", "HH:mm"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out to)
            && from != to;
    }

    /// <summary>その時間帯に入っているか（日をまたぐ形も読む）。空か読めなければ false。</summary>
    public static bool InWindow(string? window, DateTime now)
    {
        if (!TryParseWindow(window, out var from, out var to)) return false;
        var t = TimeOnly.FromDateTime(now);
        return from <= to ? t >= from && t < to : t >= from || t < to;
    }
}
