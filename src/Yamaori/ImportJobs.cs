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

    public sealed record ImportStatus(bool Running, string? Kind, DateTime? StartedAt, DateTime? FinishedAt, bool? Ok, string? Message, bool NeedsReplace);

    ImportStatus _status = new(false, null, null, null, null, null, false);
    public ImportStatus Current { get { lock (_gate) return _status; } }

    public string TempDir => Path.Combine(options.Value.DataDir, "import-tmp");

    public YamaoriOptions Config()
    {
        var o = new YamaoriOptions();
        var path = ConfigFile.PathIn(options.Value.DataDir);
        if (File.Exists(path)) new ConfigurationBuilder().AddJsonFile(path, optional: true, reloadOnChange: false).Build().Bind(o);
        o.DataDir = options.Value.DataDir;
        return o;
    }

    /// <summary>送れる大きさの上限（何年分もの VAT・VRCX の DB でも入る大きさ）。</summary>
    public const long MaxUpload = 8L << 30;

    /// <summary>
    /// 送られてきた本文を、取り込み用の一時ファイルに書く。先に <see cref="TryReserve"/> で取り込みの枠を取っておく
    /// （取り込みが動いている間に、大きな本文を何本も受け取ってディスクを埋めないため）。途中で切れたら、書きかけは消す。
    /// </summary>
    public async Task<string> SaveUploadAsync(Stream body, CancellationToken cancel)
    {
        Directory.CreateDirectory(TempDir);
        var path = Path.Combine(TempDir, $"upload-{Guid.NewGuid():N}.db");
        try
        {
            await using var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            var buffer = new byte[1 << 20];
            long total = 0;
            int n;
            while ((n = await body.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
            {
                total += n;
                if (total > MaxUpload) throw new InvalidDataException("大きすぎる");
                await f.WriteAsync(buffer.AsMemory(0, n), cancel).ConfigureAwait(false);
            }
            return path;
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    /// <summary>起きたときに、前の回の書きかけの一時ファイルを片付ける。</summary>
    public void CleanTemp()
    {
        try
        {
            if (Directory.Exists(TempDir)) foreach (var f in Directory.GetFiles(TempDir)) TryDelete(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>取り込みの枠を取る（もう動いているか取られていれば false）。取ったら、Start か Release を必ず呼ぶ。</summary>
    public bool TryReserve(string kind)
    {
        lock (_gate)
        {
            if (_status.Running) return false;
            _status = new ImportStatus(true, kind, DateTime.UtcNow, null, null, null, false);
            return true;
        }
    }

    /// <summary>取った枠を返す（本文を受け取れなかったときなど）。</summary>
    public void Release(string message)
    {
        lock (_gate) _status = _status with { Running = false, FinishedAt = DateTime.UtcNow, Ok = false, Message = message };
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

    /// <summary>
    /// 取り込みを始める。枠をまだ取っていなければ取る（取れなければ false）。file は取り込み終わったら消す一時ファイル。
    /// deviceId は、その記録を集めた端末（メンバーから送られたらその端末。ログとの重なりもその端末のログで見る）。
    /// </summary>
    public bool Start(string kind, string file, bool replace, string? accountId = null, string? deviceId = null, bool reserved = false)
    {
        if (!reserved && !TryReserve(kind)) { TryDelete(file); return false; }
        _ = Task.Run(() => Run(kind, file, replace, accountId, deviceId));
        return true;
    }

    /// <summary>取り込みを、この呼び出しの中で終わるまで動かす（VRCX の連携から）。ほかの取り込みが動いていれば null（次の回に試す）。</summary>
    public ImportStatus? RunNow(string kind, string file, bool replace)
    {
        if (!TryReserve(kind)) { TryDelete(file); return null; }
        Run(kind, file, replace, null, null);
        return Current;
    }

    void Run(string kind, string file, bool replace, string? accountId, string? deviceId)
    {
        bool ok = false, needsReplace = false;
        string message;
        try
        {
            using var db = Database.Open(options.Value.DatabasePath);
            object result;
            if (kind == "vat")
            {
                var r = new VatImport(db, deviceId ?? state.DeviceId).Import(file, accountId, replace);
                result = r;
                message = $"訪問 {r.Visits}・会った人 {r.Encounters}（うち ID が分かった {r.Resolved}）・出来事 {r.Events}・ログと重なって飛ばした行 {r.Skipped}";
            }
            else if (kind == "move")
            {
                var r = new MoveImport(db, deviceId ?? state.DeviceId).Import(file);
                result = r;
                message = $"取り込んだ訪問 {r.Visits}・会った人 {r.Encounters}・出来事 {r.Events}・メモ {r.Notes}・ワールド {r.Worlds}・お気に入り {r.Favorites}・"
                    + $"アルバム {r.Albums}（写真 {r.AlbumPhotos}）・写真の直し {r.Edits}・ゴミ箱 {r.Trash}・忘れた人 {r.Forgotten}・フレンドの写し {r.FriendAccounts} アカウント";
            }
            else
            {
                var r = new VrcxImport(db, deviceId ?? state.DeviceId).Import(file, replace);
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
            message = $"取り込めなかった（{kind switch { "vat" => "VRChatActivityLog.db か確かめる", "move" => "移す端末とまとめ役の版を確かめる", _ => "VRCX.sqlite3 か確かめる" }}）: {ex.Message}";
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

    /// <summary>
    /// グループのメンバーなら、取り込む DB をまとめ役へ送って、まとめ役で取り込んでもらう（記録はまとめ役が持つので）。
    /// 送れたら null、だめなら理由。file は送り終えたら消す。
    /// </summary>
    public async Task<string?> ForwardAsync(string kind, string file, bool replace, string? account, CancellationToken cancel)
    {
        try
        {
            var o = Config();
            if (o.ParentUrl == null || Photos.Secrets.Unprotect(o.ParentToken) is not { } token) return "まとめ役のアドレスかトークンがない（グループに入り直す）";
            using var http = new HttpClient { BaseAddress = new Uri(o.ParentUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(30) };
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            await using var f = new FileStream(file, FileMode.Open, FileAccess.Read);
            using var content = new StreamContent(f);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            var q = $"api/sync/import/{kind}?replace={(replace ? "true" : "false")}" + (account is { Length: > 0 } ? "&account=" + Uri.EscapeDataString(account) : "");
            using var res = await http.PostAsync(q, content, cancel).ConfigureAwait(false);
            if (res.IsSuccessStatusCode) return null;
            var body = (await res.Content.ReadAsStringAsync(cancel).ConfigureAwait(false)).Trim();
            return $"まとめ役が {(int)res.StatusCode} を返した{(res.StatusCode == System.Net.HttpStatusCode.Conflict ? "（ほかの取り込みが動いている）" : "")}: {(body.Length > 200 ? body[..200] : body)}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { return $"まとめ役へ送れなかった: {ex.Message}"; }
        finally { TryDelete(file); }
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
public sealed class VrcxWorker(ImportJobs jobs, IOptions<YamaoriOptions> options, DeviceRoles roles, ILogger<VrcxWorker> logger) : BackgroundService
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
                // グループの中では「VRCX と連携する」の役で、グループがなければ設定で動く
                var enabled = roles.InGroup ? roles.Has(GroupRoles.Vrcx) : o.VrcxAuto;
                if (enabled && File.Exists(path) && DateTime.UtcNow - lastTry >= TimeSpan.FromMinutes(Math.Clamp(o.VrcxMinutes, 5, 1440)))
                {
                    using var db = Database.Open(options.Value.DatabasePath);
                    var store = new AuthStore(db);
                    var mtime = Database.Time(File.GetLastWriteTimeUtc(path));
                    if (store.Get("vrcx_auto_mtime") != mtime && store.Get("vrcx_auto_blocked") != mtime)
                    {
                        lastTry = DateTime.UtcNow;
                        if (roles.IsMember)
                        {
                            // メンバーは、まとめ役へ送って取り込んでもらう
                            var problem = await jobs.ForwardAsync("vrcx", jobs.Snapshot(path), replace: false, null, stoppingToken).ConfigureAwait(false);
                            if (problem == null) { store.Set("vrcx_auto_mtime", mtime); logger.LogInformation("VRCX の DB をまとめ役へ送った"); }
                            else if (problem.Contains("ほかの取り込みが動いている", StringComparison.Ordinal)) lastTry = DateTime.MinValue;
                            else logger.LogWarning("VRCX の DB をまとめ役へ送れなかった（次の回にやり直す）: {Problem}", problem);
                            continue;
                        }
                        var s = jobs.RunNow("vrcx", jobs.Snapshot(path), replace: false);
                        if (s == null) { lastTry = DateTime.MinValue; continue; } // ほかの取り込みが動いていた。次の回に試す
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
