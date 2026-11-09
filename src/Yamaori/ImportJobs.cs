using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Yamaori.Store;
using Yamaori.Web;

namespace Yamaori;

/// <summary>
/// VRChatActivityTools（VAT）と VRCX の取り込みを裏で動かす（何年分もあると時間がかかるので、画面は様子を読みに来る）。一度に 1 つだけ。
/// 取り込む DB は、送られたファイルか、この PC の VRCX の DB の写し（VRCX が使っている間でも読めるよう、VACUUM INTO で写す）。
/// 終わったら、結果を settings の import_last:&lt;種類&gt; に残す。
/// </summary>
public sealed class ImportJobs(IOptions<YamaoriOptions> options, YamaoriState state, LiveHub hub, ILogger<ImportJobs> logger)
{
    readonly Lock _gate = new();
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record Status(bool Running, string? Kind, DateTime? StartedAt, DateTime? FinishedAt, bool? Ok, string? Message, bool NeedsReplace);

    Status _status = new(false, null, null, null, null, null, false);
    public Status Current { get { lock (_gate) return _status; } }

    public string TempDir => Path.Combine(options.Value.DataDir, "import-tmp");

    public YamaoriOptions Config()
    {
        var o = new YamaoriOptions();
        var path = ConfigFile.PathIn(options.Value.DataDir);
        if (File.Exists(path)) new ConfigurationBuilder().AddJsonFile(path, optional: true, reloadOnChange: false).Build().Bind(o);
        o.DataDir = options.Value.DataDir;
        return o;
    }

    /// <summary>送られてきた本文を、取り込み用の一時ファイルに書く。</summary>
    public async Task<string> SaveUploadAsync(Stream body, CancellationToken cancel)
    {
        Directory.CreateDirectory(TempDir);
        var path = Path.Combine(TempDir, $"upload-{Guid.NewGuid():N}.db");
        await using (var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            await body.CopyToAsync(f, cancel).ConfigureAwait(false);
        return path;
    }

    /// <summary>使われている SQLite のファイルを、壊さずに写す（読むだけで開く）。</summary>
    public string Snapshot(string source)
    {
        Directory.CreateDirectory(TempDir);
        var path = Path.Combine(TempDir, $"snapshot-{Guid.NewGuid():N}.db");
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "VACUUM INTO $t";
        cmd.Parameters.AddWithValue("$t", path);
        cmd.ExecuteNonQuery();
        return path;
    }

    /// <summary>取り込みを始める（もう動いていれば false）。file は取り込み終わったら消す一時ファイル。</summary>
    public bool Start(string kind, string file, bool replace, string? accountId = null)
    {
        lock (_gate)
        {
            if (_status.Running) { TryDelete(file); return false; }
            _status = new Status(true, kind, DateTime.UtcNow, null, null, null, false);
        }
        _ = Task.Run(() => Run(kind, file, replace, accountId));
        return true;
    }

    /// <summary>取り込みを、この呼び出しの中で終わるまで動かす（VRCX の連携から）。</summary>
    public Status RunNow(string kind, string file, bool replace)
    {
        lock (_gate)
        {
            if (_status.Running) { TryDelete(file); return _status; }
            _status = new Status(true, kind, DateTime.UtcNow, null, null, null, false);
        }
        Run(kind, file, replace, null);
        return Current;
    }

    void Run(string kind, string file, bool replace, string? accountId)
    {
        bool ok = false, needsReplace = false;
        string message;
        try
        {
            using var db = Database.Open(options.Value.DatabasePath);
            object result;
            if (kind == "vat")
            {
                var r = new VatImport(db, state.DeviceId).Import(file, accountId, replace);
                result = r;
                message = $"訪問 {r.Visits}・会った人 {r.Encounters}（うち ID が分かった {r.Resolved}）・出来事 {r.Events}・ログと重なって飛ばした行 {r.Skipped}";
            }
            else
            {
                var r = new VrcxImport(db, state.DeviceId).Import(file, replace);
                result = r;
                message = $"訪問 {r.Visits}・会った人 {r.Encounters}・出来事 {r.Events}（うちフレンドの記録 {r.FriendEvents}）・ワールドの情報 {r.Worlds}・お気に入り {r.Favorites}・メモ {r.Notes}・ログと重なって飛ばした訪問 {r.Skipped}";
            }
            ok = true;
            new AuthStore(db).Set("import_last:" + kind, JsonSerializer.Serialize(new { at = DateTime.UtcNow, message, result }, Json));
            logger.LogInformation("{Kind} を取り込んだ: {Message}", kind, message);
            hub.Publish(new LiveEvent("imported", DateTime.UtcNow, new Dictionary<string, string?> { ["kind"] = kind }));
        }
        catch (InvalidOperationException ex)
        {
            // 前に取り込んだものと別の DB（入れ替えてよいかは本人が決める）
            message = ex.Message;
            needsReplace = ex.Message.Contains("--replace", StringComparison.Ordinal);
            if (needsReplace) message = message.Replace("--replace を付ける", "「入れ替える」を選んでもう一度取り込む", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            message = $"取り込めなかった（{(kind == "vat" ? "VRChatActivityLog.db" : "VRCX.sqlite3")} か確かめる）: {ex.Message}";
            logger.LogWarning("{Kind} を取り込めなかった: {Message}", kind, ex.Message);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(file);
        }
        lock (_gate) _status = _status with { Running = false, FinishedAt = DateTime.UtcNow, Ok = ok, Message = message, NeedsReplace = needsReplace };
    }

    static void TryDelete(string file)
    {
        try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>前の取り込みの結果（時刻と言葉）。</summary>
    public static (DateTime? At, string? Message) Last(Database db, string kind)
    {
        if (new AuthStore(db).Get("import_last:" + kind) is not { } json) return (null, null);
        try
        {
            using var d = JsonDocument.Parse(json);
            return (d.RootElement.GetProperty("at").GetDateTime(), d.RootElement.GetProperty("message").GetString());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return (null, null); }
    }
}

/// <summary>
/// VRCX との連携: この PC の VRCX の DB が変わっていたら、決まった間隔で取り込む（VRCX は書き足していくので、続きが入る）。
/// 前に取り込んだものと別の DB になったら、勝手に入れ替えずに止まって記録に書く（本人が画面で決める）。
/// </summary>
public sealed class VrcxWorker(ImportJobs jobs, IOptions<YamaoriOptions> options, ILogger<VrcxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        var lastTry = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var o = jobs.Config();
                var path = o.EffectiveVrcxPath;
                if (o.VrcxAuto && File.Exists(path) && DateTime.UtcNow - lastTry >= TimeSpan.FromMinutes(Math.Clamp(o.VrcxMinutes, 5, 1440)))
                {
                    using var db = Database.Open(options.Value.DatabasePath);
                    var store = new AuthStore(db);
                    var mtime = Database.Time(File.GetLastWriteTimeUtc(path));
                    if (store.Get("vrcx_auto_mtime") != mtime && store.Get("vrcx_auto_blocked") != mtime)
                    {
                        lastTry = DateTime.UtcNow;
                        var s = jobs.RunNow("vrcx", jobs.Snapshot(path), replace: false);
                        if (s.Ok == true) store.Set("vrcx_auto_mtime", mtime);
                        else
                        {
                            // 同じ中身でもう一度試さない（DB が変わったら、また試す）
                            store.Set("vrcx_auto_blocked", mtime);
                            logger.LogWarning("VRCX を自動で取り込めなかった（設定の「取り込み」で確かめる）: {Message}", s.Message);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("VRCX を自動で取り込めなかった（次の回にやり直す）: {Message}", ex.Message);
            }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }
}
