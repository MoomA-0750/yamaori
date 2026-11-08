using Microsoft.Extensions.Options;
using Yamaori.Store;
using Yamaori.Web;

namespace Yamaori;

/// <summary>
/// 写真のフォルダを見張って目録に入れる。決まった間隔で全部を読み直し（変わっていないものは中を読まない）、
/// この PC のフォルダは新しいファイルができたら少し待ってすぐ入れる（VRChat が書き終えるのを待つ）。
/// </summary>
public sealed class PhotoWatcher(IOptions<YamaoriOptions> options, YamaoriState state, LiveHub hub, Thumbnails thumbs, ILogger<PhotoWatcher> logger) : BackgroundService
{
    readonly SemaphoreSlim _wake = new(0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        var dirs = o.EffectivePhotoDirs;
        logger.LogInformation("写真のフォルダ {Dirs} を見張る", string.Join("、", dirs));
        var watchers = new List<FileSystemWatcher>();
        foreach (var dir in dirs.Where(Directory.Exists))
        {
            try
            {
                var w = new FileSystemWatcher(dir) { IncludeSubdirectories = true, EnableRaisingEvents = true };
                w.Created += (_, _) => _wake.Release();
                w.Renamed += (_, _) => _wake.Release();
                watchers.Add(w);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
            {
                logger.LogInformation("{Dir} の変化は見張れない（間隔ごとに読み直す）: {Message}", dir, ex.Message);
            }
        }
        try
        {
            var lastUpload = DateTime.MinValue;
            while (!stoppingToken.IsCancellationRequested)
            {
                ScanAll(o, dirs, stoppingToken);
                if (o.PhotoUpload.Kind != "none" && DateTime.UtcNow - lastUpload >= TimeSpan.FromMinutes(o.PhotoUpload.IntervalMinutes))
                {
                    lastUpload = DateTime.UtcNow;
                    await UploadAsync(o, dirs, stoppingToken).ConfigureAwait(false);
                }
                // 次に読み直す時刻か、次に送る時刻の早い方まで待つ
                var wait = TimeSpan.FromMinutes(o.PhotoScanMinutes);
                if (o.PhotoUpload.Kind != "none")
                {
                    var untilUpload = lastUpload + TimeSpan.FromMinutes(o.PhotoUpload.IntervalMinutes) - DateTime.UtcNow;
                    if (untilUpload < wait) wait = untilUpload < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : untilUpload;
                }
                var woke = await _wake.WaitAsync(wait, stoppingToken).ConfigureAwait(false);
                if (woke)
                {
                    // 書き終えるのを待ち、続けて来た知らせはまとめる
                    await Task.Delay(TimeSpan.FromSeconds(4), stoppingToken).ConfigureAwait(false);
                    while (_wake.CurrentCount > 0) await _wake.WaitAsync(stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            foreach (var w in watchers) w.Dispose();
        }
    }

    async Task UploadAsync(YamaoriOptions o, IReadOnlyList<string> dirs, CancellationToken cancel)
    {
        using var db = Database.Open(o.DatabasePath);
        var store = new AuthStore(db);
        using var target = Photos.PhotoSync.Create(o.PhotoUpload, store);
        if (target == null) return;
        var sync = new Photos.PhotoSync(new PhotoCatalog(db, state.DeviceId), target, dirs, thumbs);
        try
        {
            var r = await sync.RunAsync(o.PhotoUpload.Retention, o.PhotoUpload.RetentionDays, cancel, m => logger.LogWarning("{Message}", m)).ConfigureAwait(false);
            Photos.PhotoSync.RememberHostKey(target, store);
            if (r.Uploaded + r.AlreadyThere + r.Failed + r.Removed > 0)
                logger.LogInformation("写真を送った {Uploaded}・もうあった {Already}・送れなかった {Failed}・手元から消した {Removed}", r.Uploaded, r.AlreadyThere, r.Failed, r.Removed);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or Renci.SshNet.Common.SshException or System.Net.Sockets.SocketException)
        {
            logger.LogWarning("写真の送り先 {Target} につながらなかった: {Message}", target.Key, ex.Message);
        }
    }

    void ScanAll(YamaoriOptions o, IReadOnlyList<string> dirs, CancellationToken cancel)
    {
        using var db = Database.Open(o.DatabasePath);
        var catalog = new PhotoCatalog(db, state.DeviceId);
        foreach (var dir in dirs)
        {
            try
            {
                var r = catalog.Scan(dir, cancel);
                if (r.Added + r.Updated + r.Removed > 0)
                {
                    logger.LogInformation("写真 {Dir}: 足した {Added}・直した {Updated}・なくなった {Removed}（全部で {Seen}）", dir, r.Added, r.Updated, r.Removed, r.Seen);
                    hub.Publish(new LiveEvent("photos", DateTime.UtcNow, new Dictionary<string, string?> { ["added"] = r.Added.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("写真のフォルダ {Dir} を読めなかった: {Message}", dir, ex.Message);
            }
        }
    }
}
