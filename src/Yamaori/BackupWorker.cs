using Microsoft.Extensions.Options;
using Yamaori.Store;

namespace Yamaori;

/// <summary>
/// 毎日 1 回、DB の写しを取り、決まった日数分だけ残す。メモ・トークン・拡張機能で取った情報・写真を送った記録は、ログから組み立て直せないので。
/// 写しは auto-yyyyMMdd.sqlite3 で、手で取った写し（yamaori backup）には触らない。
/// </summary>
public sealed class BackupWorker(IOptions<YamaoriOptions> options, ILogger<BackupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (o.BackupKeep <= 0) return;
        // 起きた直後はログを読み込むので、少し待つ
        try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (RunOnce(o, DateTime.Now) is { } made) logger.LogInformation("DB の写しを取った: {File}", made);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("DB の写しを取れなかった（次の回にやり直す）: {Message}", ex.Message);
            }
            try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>今日の写しがまだなければ取り、古い写しを消す。取ったらその道。</summary>
    public static string? RunOnce(YamaoriOptions o, DateTime now)
    {
        var dir = o.BackupPath;
        var file = Path.Combine(dir, $"auto-{now:yyyyMMdd}.sqlite3");
        if (File.Exists(file)) return null;
        using (var db = Database.Open(o.DatabasePath)) db.Backup(file);
        if (Directory.Exists(dir))
            foreach (var old in Directory.GetFiles(dir, "auto-????????.sqlite3").Order(StringComparer.Ordinal).SkipLast(Math.Max(1, o.BackupKeep)))
                File.Delete(old);
        return file;
    }
}
