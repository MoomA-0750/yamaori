using System.Net;
using Yamaori.Store;
using Yamaori.Web;

namespace Yamaori;

/// <summary>常駐するときの組み立て。ログの見張りと、WebUI・API を一つのプロセスで動かす。</summary>
public static class Server
{
    /// <param name="configure">試験で、組み立てを差し替える（TestServer に載せるなど）。</param>
    public static WebApplication Build(string[] args, IConfiguration config, YamaoriOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        builder.Configuration.AddConfiguration(config);
        builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(options.DataDir, "yamaori.log")));
        // 要求 1 つごとの行（ASP.NET・MCP の内部）は、画面（コンテナの記録）にも警告以上だけ出す
        builder.Logging.AddFilter("Microsoft.AspNetCore", Microsoft.Extensions.Logging.LogLevel.Warning);
        builder.Logging.AddFilter("ModelContextProtocol", Microsoft.Extensions.Logging.LogLevel.Warning);
        builder.Services.Configure<YamaoriOptions>(config);

        // 既定はこの PC からだけ。lan のときだけ外のアドレスでも待つ（tailscale serve は localhost へつなぐので local のままでよい）
        builder.WebHost.ConfigureKestrel(k =>
        {
            if (options.Listen == "lan") k.ListenAnyIP(options.Port);
            else
            {
                k.Listen(IPAddress.Loopback, options.Port);
                k.Listen(IPAddress.IPv6Loopback, options.Port);
            }
        });

        configure?.Invoke(builder);

        string deviceId;
        using (var db = Database.Open(options.DatabasePath)) deviceId = db.SelfDevice(options.DeviceName);
        builder.Services.AddSingleton(new YamaoriState { DeviceId = deviceId, DeviceName = options.DeviceName, Listen = options.Listen });
        builder.Services.AddSingleton<LiveHub>();
        builder.Services.AddSingleton<LogWatcher>();
        builder.Services.AddHostedService(s => s.GetRequiredService<LogWatcher>());
        builder.Services.AddHostedService<PhotoWatcher>();
        builder.Services.AddHostedService<SyncWorker>();
        builder.Services.AddHostedService<BackupWorker>();
        builder.Services.AddSingleton<ThumbJob>();
        builder.Services.AddSingleton<Photos.RemoteThumbs>();
        builder.Services.AddHostedService<ThumbWorker>();
        builder.Services.AddSingleton<ImportJobs>();
        builder.Services.AddHostedService<VrcxWorker>();
        builder.Services.AddSingleton(new Thumbnails(Path.Combine(options.DataDir, "thumbs"), options.EffectivePhotoDirs, options.ThumbSidecar));
        // 要求ごとに DB へつなぐ（見張りとは別の接続）
        builder.Services.AddScoped(_ => Database.Open(options.DatabasePath));
        builder.Services.AddScoped(s => new Queries(s.GetRequiredService<Database>()));
        builder.Services.AddScoped(s => new PhotoCatalog(s.GetRequiredService<Database>(), deviceId));
        builder.Services.AddOpenApi();
        // AI のエージェント向けの口（/mcp）。要求ごとに完結させる（つなぎっぱなしの状態を持たない）
        builder.Services.AddMcpServer().WithHttpTransport(o => o.Stateless = true).WithTools<McpTools>();
        builder.Services.ConfigureHttpJsonOptions(j =>
        {
            j.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            // 数は数として受ける（定義でも number になる）
            j.SerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict;
        });

        var app = builder.Build();
        if (ConfigFile.LastRecovered is { } broken) app.Logger.LogWarning("yamaori.json を読めなかったので {Broken} へ退けた。既定の設定で起きている", broken);
        var hub = app.Services.GetRequiredService<LiveHub>();
        app.Services.GetRequiredService<LogWatcher>().Live += (file, e) =>
        {
            if (LiveHub.From(file, e) is { } live) hub.Publish(live);
        };
        // 入口の見張りは、どの API に当たるかを知ってから決める
        app.UseRouting();
        Auth.UseAccess(app);
        // 画面（web/ をビルドしたもの）。index.html は毎回確かめさせ、名前にハッシュの付いたものは長く持たせる
        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = c =>
                c.Context.Response.Headers.CacheControl = c.File.Name.EndsWith(".html", StringComparison.Ordinal) ? "no-cache" : "public, max-age=31536000, immutable",
        });
        app.MapOpenApi();
        Auth.Map(app);
        Auth.MapPhotoSettings(app);
        Auth.MapSyncSettings(app);
        ThumbSettings.Map(app);
        ImportSettings.Map(app);
        Api.Map(app);
        app.MapMcp("/mcp");
        return app;
    }
}
