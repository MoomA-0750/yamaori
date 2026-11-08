using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Yamaori;
using Yamaori.Store;

// yamaori                    常駐して見張る
// yamaori ingest             ログのフォルダを一度だけ読んで DB に入れ、まとめを出す
// yamaori summary            DB のまとめを出す
// yamaori rebuild            DB に残した出来事から、記録を全部組み立て直す
// 共通の引数: --log-dir <フォルダ> --data-dir <フォルダ> --database <ファイル>
// 出力を別のファイルへ向けたときも、日本語が化けないようにする
Console.OutputEncoding = System.Text.Encoding.UTF8;
var command = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "run";
var rest = command == "run" ? args : args[1..];

var config = new ConfigurationBuilder()
    .AddJsonFile(Path.Combine(new YamaoriOptions().DataDir, "yamaori.json"), optional: true)
    .AddCommandLine(rest, new Dictionary<string, string> { ["--log-dir"] = "LogDir", ["--data-dir"] = "DataDir", ["--database"] = "Database" })
    .Build();
var options = new YamaoriOptions();
config.Bind(options);

switch (command)
{
    case "run":
        var builder = Host.CreateApplicationBuilder(rest);
        builder.Configuration.AddConfiguration(config);
        builder.Services.Configure<YamaoriOptions>(config);
        builder.Services.AddSingleton<LogWatcher>();
        builder.Services.AddHostedService(s => s.GetRequiredService<LogWatcher>());
        await builder.Build().RunAsync().ConfigureAwait(false);
        return 0;
    case "ingest":
    {
        using var db = Database.Open(options.DatabasePath);
        var ingestor = new LogIngestor(db);
        foreach (var (file, r) in LogWatcher.IngestAll(ingestor, options.LogDir))
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
        var ingestor = new LogIngestor(db);
        foreach (var name in ingestor.LogFiles()) ingestor.Rebuild(name);
        Summary.Print(db);
        return 0;
    }
    default:
        Console.Error.WriteLine($"知らないコマンド: {command}（run・ingest・summary・rebuild）");
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
