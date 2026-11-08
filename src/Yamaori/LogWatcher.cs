using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yamaori.Core.Log;
using Yamaori.Store;

namespace Yamaori;

/// <summary>
/// VRChat のログのフォルダを見張り、書き足された分を DB に入れる。
/// いちばん新しいログは、VRChat が動いている間は「閉じていない」として読む（最後の訪問はまだ続いている）。
/// </summary>
public sealed class LogWatcher(IOptions<YamaoriOptions> options, ILogger<LogWatcher> logger) : BackgroundService
{
    /// <summary>今新しく読んだ出来事（あとで SSE に流す）。</summary>
    public event Action<string, LogEvent>? Live;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        using var db = Database.Open(o.DatabasePath);
        var ingestor = new LogIngestor(db, db.SelfDevice(o.DeviceName));
        var archive = new LogArchive(o.ArchivePath);
        logger.LogInformation("ログ {LogDir} を見張る。DB は {Db}、生のログは {Archive} に残す", o.LogDir, o.DatabasePath, o.ArchivePath);
        var first = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var (file, r) in IngestAll(ingestor, archive, o.LogDir))
                {
                    if (r.Changed && r.NewEvents.Count > 0) logger.LogInformation("{File}: {Count} 件", file, r.NewEvents.Count);
                    // 起こした直後の読み直しの分は流さない
                    if (!first) foreach (var e in r.NewEvents) Live?.Invoke(file, e);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "ログを読めなかった");
            }
            first = false;
            await Task.Delay(TimeSpan.FromSeconds(o.PollSeconds), stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>フォルダのログを古い順に全部、写して残し、読む（読み終えたものは長さを見るだけ）。</summary>
    public static IEnumerable<(string File, LogIngestor.Result Result)> IngestAll(LogIngestor ingestor, LogArchive? archive, string logDir)
    {
        if (!Directory.Exists(logDir)) yield break;
        var files = new DirectoryInfo(logDir).GetFiles("output_log_*.txt").OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
        var running = VrchatRunning();
        for (var i = 0; i < files.Count; i++)
        {
            var newest = i == files.Count - 1;
            var closed = !newest || !running;
            // 先に写す（読むのに失敗しても、生のログは残るように）
            archive?.Sync(files[i].FullName, closed);
            yield return (files[i].Name, ingestor.Ingest(files[i].FullName, closed));
        }
    }

    static bool VrchatRunning()
    {
        var ps = Process.GetProcessesByName("VRChat");
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }
}
