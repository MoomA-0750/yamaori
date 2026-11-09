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

    static bool Healthy(string path)
    {
        try
        {
            using var c = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check";
            return cmd.ExecuteScalar() as string == "ok";
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { return false; }
    }

    /// <summary>今日の写しがまだなければ取り、古い写しを消す。取ったらその道。</summary>
    public static string? RunOnce(YamaoriOptions o, DateTime now)
    {
        var dir = o.BackupPath;
        var file = Path.Combine(dir, $"auto-{now:yyyyMMdd}.sqlite3");
        // 今日の写しが、確かめて読めるものならそれでよい（途中で止まって壊れた写しなら取り直す）
        if (File.Exists(file) && Healthy(file)) return null;
        // 別の名前に取って確かめてから、今日の名前に置き換える
        var tmp = file + ".tmp";
        using (var db = Database.Open(o.DatabasePath)) db.Backup(tmp);
        if (!Healthy(tmp)) { File.Delete(tmp); throw new IOException("取った写しを確かめたら壊れていた"); }
        File.Move(tmp, file, overwrite: true);
        if (Directory.Exists(dir))
            foreach (var old in Directory.GetFiles(dir, "auto-????????.sqlite3").Order(StringComparer.Ordinal).SkipLast(Math.Max(1, o.BackupKeep)))
                File.Delete(old);
        return file;
    }
}
