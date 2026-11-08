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
var config = new ConfigurationBuilder()
    .AddJsonFile(Path.Combine(Path.GetFullPath(dataDir), "yamaori.json"), optional: true)
    .AddCommandLine(rest, switches)
    .Build();
var options = new YamaoriOptions();
config.Bind(options);

switch (command)
{
    case "run":
        // 同じ本人のもとで二つ起きないようにする
        using (var mutex = new Mutex(false, @"Local\Yamaori"))
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
    default:
        Console.Error.WriteLine($"知らないコマンド: {command}（run・ingest・summary・rebuild・reparse）");
        return 2;
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
