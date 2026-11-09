using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yamaori;
using Yamaori.Store;

// yamaori                    常駐して見張る
// yamaori ingest             ログのフォルダを一度だけ読んで DB に入れ、まとめを出す
// yamaori summary            DB のまとめを出す
// yamaori rebuild            DB に残した出来事から、記録を全部組み立て直す
// yamaori reparse            残した生のログから、出来事を全部読み直す（読み方を直したあと）
// yamaori export --out <dir> 日記を日ごとの Markdown に書き出す
// yamaori import-vat --file <VRChatActivityLog.db>  VRChatActivityTools の記録を取り込む
// yamaori import-vrcx [--file <VRCX.sqlite3>]        VRCX の記録を取り込む
// yamaori set-password                               パスワードを決める（標準入力から）
// yamaori backup [--out <ファイル>]                  DB の写しを作る（動いている間でもよい）
// yamaori resync                                     子の端末: 次の回に、記録とメモを全部親へ送り直す
// 共通の引数: --log-dir <フォルダ> --data-dir <フォルダ> --database <ファイル>
var command = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "run";
// Windows では窓を出さない形（WinExe）で作るので、コマンドのときは呼んだ側のコンソールにつなぐ
if (command != "run") Native.AttachParentConsole();
// 出力を別のファイルへ向けたときも、日本語が化けないようにする
Console.OutputEncoding = System.Text.Encoding.UTF8;
var rest = command == "run" ? args : args[1..];

var switches = new Dictionary<string, string> { ["--log-dir"] = "LogDir", ["--data-dir"] = "DataDir", ["--database"] = "Database", ["--archive-dir"] = "ArchiveDir", ["--device-name"] = "DeviceName" };
// データのフォルダは引数で変えられるので、先にそれを決めてから、そこの yamaori.json を読む（引数の方が強い）
var dataDir = new ConfigurationBuilder().AddCommandLine(rest, switches).Build()["DataDir"] ?? new YamaoriOptions().DataDir;
// 読めない yamaori.json は退けて既定で起きる（窓がないので、黙って起きなくなるのを防ぐ）
var brokenConfig = ConfigFile.Recover(Path.GetFullPath(dataDir));
if (brokenConfig != null) Console.Error.WriteLine($"yamaori.json を読めなかったので {brokenConfig} へ退けた。既定の設定で起きる");
var config = new ConfigurationBuilder()
    .AddJsonFile(Path.Combine(Path.GetFullPath(dataDir), "yamaori.json"), optional: true)
    // コンテナ（TrueNAS のアプリなど）では環境変数でも決められる: YAMAORI_ThumbSidecar=true、YAMAORI_PhotoUpload__Kind=none など
    .AddEnvironmentVariables("YAMAORI_")
    .AddCommandLine(rest, switches)
    .Build();
var options = new YamaoriOptions();
config.Bind(options);

switch (command)
{
    case "run":
        // 同じデータのフォルダで二つ起きないようにする（データのフォルダが違えば、別々に動ける）
        var mutexName = @"Local\Yamaori-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(options.DataDir).ToLowerInvariant())), 0, 8);
        using (var mutex = new Mutex(false, mutexName))
        {
            // 起こし直しのときは、前のものが止まるのを少し待つ
            bool first;
            try { first = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { first = true; }
            if (!first) return 1;
            await Server.Build(rest, config, options).RunAsync().ConfigureAwait(false);
            return 0;
        }
    case "ingest":
    {
        using var db = Database.Open(options.DatabasePath);
        var ingestor = new LogIngestor(db, db.SelfDevice(options.DeviceName));
        foreach (var (file, r) in LogWatcher.IngestAll(ingestor, new LogArchive(options.ArchivePath), options.LogDir))
            Console.WriteLine($"{file}: {(r.Changed ? $"{r.NewEvents.Count} 件" : "変わりなし")}");
        Summary.Print(db);
        return 0;
    }
    case "backup":
    {
        // yamaori backup [--out <ファイル>]  記録の DB の写しを作る（動いている間でもよい）。既定はデータのフォルダの backups
        var outFile = new ConfigurationBuilder().AddCommandLine(rest).Build()["out"]
            ?? Path.Combine(options.DataDir, "backups", $"yamaori-{DateTime.Now:yyyyMMdd-HHmmss}.sqlite3");
        using var db = Database.Open(options.DatabasePath);
        db.Backup(outFile);
        Console.WriteLine($"写しを作った: {outFile}（戻すときは Yamaori を止めて、yamaori.sqlite3 をこれに置き換える）");
        return 0;
    }
    case "summary":
    {
        using var db = Database.Open(options.DatabasePath);
        Summary.Print(db);
        return 0;
    }
    case "rebuild":
    {
        using var db = Database.Open(options.DatabasePath);
        var ingestor = new LogIngestor(db, db.SelfDevice(options.DeviceName));
        foreach (var name in ingestor.LogFiles()) ingestor.Rebuild(name);
        Summary.Print(db);
        return 0;
    }
    case "reparse":
    {
        using var db = Database.Open(options.DatabasePath);
        var ingestor = new LogIngestor(db, db.SelfDevice(options.DeviceName));
        foreach (var (name, open, closed) in new LogArchive(options.ArchivePath).List())
        {
            using var stream = open();
            var r = ingestor.Reparse(name, stream, closed);
            Console.WriteLine($"{name}: {r.NewEvents.Count} 件");
        }
        Summary.Print(db);
        return 0;
    }
    case "import-vat":
    {
        // yamaori import-vat --file <VRChatActivityLog.db> [--account usr_…]  VRChatActivityTools の記録を読むだけで取り込む
        var cl = new ConfigurationBuilder().AddCommandLine(rest).Build();
        var file = cl["file"];
        if (file == null || !File.Exists(file)) { Console.Error.WriteLine("--file に VRChatActivityLog.db を渡す"); return 2; }
        using var db = Database.Open(options.DatabasePath);
        VatImport.Result r;
        try { r = new VatImport(db, db.SelfDevice(options.DeviceName)).Import(file, cl["account"], replace: cl["replace"] != null || rest.Contains("--replace")); }
        catch (InvalidOperationException ex) { Console.Error.WriteLine(ex.Message); return 3; }
        Console.WriteLine($"訪問 {r.Visits}・会った人 {r.Encounters}（うち ID が分かった {r.Resolved}）・出来事 {r.Events}・ログと重なって飛ばした行 {r.Skipped}");
        Console.WriteLine($"本人とみなして会った人から外した名前: {string.Join("、", r.SelfNames)}");
        return 0;
    }
    case "import-vrcx":
    {
        // yamaori import-vrcx [--file <VRCX.sqlite3>]  VRCX の記録を読むだけで取り込む
        var cl = new ConfigurationBuilder().AddCommandLine(rest).Build();
        var file = cl["file"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCX", "VRCX.sqlite3");
        if (!File.Exists(file)) { Console.Error.WriteLine($"VRCX の DB が見つからない: {file}（--file で渡す）"); return 2; }
        using var db = Database.Open(options.DatabasePath);
        VrcxImport.Result r;
        try { r = new VrcxImport(db, db.SelfDevice(options.DeviceName)).Import(file, replace: rest.Contains("--replace")); }
        catch (InvalidOperationException ex) { Console.Error.WriteLine(ex.Message); return 3; }
        Console.WriteLine($"訪問 {r.Visits}・会った人 {r.Encounters}・出来事 {r.Events}（うちフレンドの記録 {r.FriendEvents}）・ワールドの情報 {r.Worlds}・お気に入り {r.Favorites}・メモ {r.Notes}・ログと重なって飛ばした訪問 {r.Skipped}");
        Console.WriteLine($"アカウント: {string.Join("、", r.Accounts)}");
        return 0;
    }
    case "set-password":
    {
        // yamaori set-password  パスワードを標準入力から読んで決める（サーバーやコンテナで、最初に決めるため）
        var pw = Console.IsInputRedirected ? Console.In.ReadLine() : ReadHidden("新しいパスワード（8 文字以上）: ");
        if (pw is not { Length: >= 8 }) { Console.Error.WriteLine("パスワードは 8 文字以上"); return 2; }
        using var db = Database.Open(options.DatabasePath);
        new AuthStore(db).SetPassword(pw);
        Console.WriteLine("パスワードを決めた");
        return 0;
    }
    case "resync":
    {
        // yamaori resync  子の端末で、親へ送った印を消す（親で忘れるのをやめたあと・親の DB を戻したあとに、全部送り直す）
        using var db = Database.Open(options.DatabasePath);
        using var cmd = db.Command("DELETE FROM sync_state");
        var n = cmd.ExecuteNonQuery();
        Console.WriteLine($"親へ送った印を消した（{n} 件）。次に送る回に、記録とメモを全部送り直す（消したメモの印も送り直す）");
        return 0;
    }
    case "export":
    {
        // yamaori export --out <フォルダ>  日ごとの Markdown を書き出す
        var outDir = new ConfigurationBuilder().AddCommandLine(rest).Build()["out"] ?? Path.Combine(options.DataDir, "export");
        Directory.CreateDirectory(outDir);
        using var db = Database.Open(options.DatabasePath);
        var count = 0;
        foreach (var (file, content) in new MarkdownExport(new Queries(db)).Days())
        {
            File.WriteAllText(Path.Combine(outDir, file), content);
            count++;
        }
        Console.WriteLine($"{count} 日分を {outDir} に書き出した");
        return 0;
    }
    default:
        Console.Error.WriteLine($"知らないコマンド: {command}（run・ingest・summary・rebuild・reparse・export・backup・resync・import-vat・import-vrcx・set-password）");
        return 2;
}

static string ReadHidden(string prompt)
{
    Console.Write(prompt);
    var sb = new System.Text.StringBuilder();
    while (true)
    {
        var k = Console.ReadKey(intercept: true);
        if (k.Key == ConsoleKey.Enter) break;
        if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
        sb.Append(k.KeyChar);
    }
    Console.WriteLine();
    return sb.ToString();
}

static class Summary
{
    public static void Print(Database db)
    {
        using var cmd = db.Command("""
            SELECT v.joined_at, v.left_at, v.left_reason, COALESCE(v.world_name, v.world_id), v.access, v.region,
                   (SELECT COUNT(DISTINCT COALESCE(e.user_id, e.name)) FROM encounters e WHERE e.visit_id = v.id),
                   (SELECT COUNT(*) FROM events x WHERE x.visit_id = v.id AND x.kind = 'video'),
                   (SELECT COUNT(*) FROM events x WHERE x.visit_id = v.id AND x.kind = 'screenshot')
            FROM visits v ORDER BY v.joined_at
            """);
        using var r = cmd.ExecuteReader();
        Console.WriteLine("入った (ローカル)        出た                 わけ      人  動画 写真  ワールド");
        while (r.Read())
        {
            var joined = Database.ParseTime(r.GetString(0)).ToLocalTime();
            var left = r.IsDBNull(1) ? "（いる）           " : Database.ParseTime(r.GetString(1)).ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "     ";
            var reason = r.IsDBNull(2) ? "" : r.GetString(2);
            Console.WriteLine($"{joined:yyyy-MM-dd HH:mm:ss}  {left}  {reason,-8}  {r.GetInt64(6),3} {r.GetInt64(7),4} {r.GetInt64(8),4}  {r.GetString(3)} [{r.GetString(4)} {r.GetString(5)}]");
        }
        foreach (var (label, sql) in new[]
        {
            ("出来事", "SELECT kind || ' ' || COUNT(*) FROM events GROUP BY kind"),
            ("通知", "SELECT k || ' ' || COUNT(*) FROM (SELECT json_extract(data, '$.direction') || ' ' || json_extract(data, '$.type') AS k FROM events WHERE kind = 'notification') GROUP BY k"),
            ("端末", "SELECT name || ' (' || id || ')' FROM devices"),
            ("アカウント", "SELECT COALESCE(a.name, '?') || ' (' || a.id || ') 訪問 ' || (SELECT COUNT(*) FROM visits v WHERE v.account_id = a.id) FROM accounts a"),
            ("起動", "SELECT log_file || ' ' || COALESCE(self_name, '?') || ' vr=' || COALESCE(vr, '?') || ' ' || COALESCE(hmd, '') || ' end=' || COALESCE(end_reason, '（動いている）') FROM sessions ORDER BY log_file"),
        })
        {
            using var c = db.Command(sql);
            using var rr = c.ExecuteReader();
            var items = new List<string>();
            while (rr.Read()) items.Add(rr.GetString(0));
            Console.WriteLine($"{label}: {string.Join(" / ", items)}");
        }
        Console.WriteLine($"会った人: {db.Scalar("SELECT COUNT(*) FROM people")} 人");
    }
}

static partial class Native
{
    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

    public static void AttachParentConsole()
    {
        if (OperatingSystem.IsWindows()) AttachConsole(-1);
    }
}
