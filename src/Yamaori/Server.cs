using System.Net;
using Yamaori.Store;
using Yamaori.Web;

namespace Yamaori;

/// <summary>常駐するときの組み立て。ログの見張りと、WebUI・API を一つのプロセスで動かす。</summary>
public static class Server
{
    public static WebApplication Build(string[] args, IConfiguration config, YamaoriOptions options)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        builder.Configuration.AddConfiguration(config);
        builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(options.DataDir, "yamaori.log")));
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

        string deviceId;
        using (var db = Database.Open(options.DatabasePath)) deviceId = db.SelfDevice(options.DeviceName);
        builder.Services.AddSingleton(new YamaoriState { DeviceId = deviceId, DeviceName = options.DeviceName, Listen = options.Listen });
        builder.Services.AddSingleton<LiveHub>();
        builder.Services.AddSingleton<LogWatcher>();
        builder.Services.AddHostedService(s => s.GetRequiredService<LogWatcher>());
        // 要求ごとに DB へつなぐ（見張りとは別の接続）
        builder.Services.AddScoped(_ => Database.Open(options.DatabasePath));
        builder.Services.AddScoped(s => new Queries(s.GetRequiredService<Database>()));
        builder.Services.AddOpenApi();
        builder.Services.ConfigureHttpJsonOptions(j => j.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

        var app = builder.Build();
        var hub = app.Services.GetRequiredService<LiveHub>();
        app.Services.GetRequiredService<LogWatcher>().Live += (file, e) =>
        {
            if (LiveHub.From(file, e) is { } live) hub.Publish(live);
        };
        app.MapOpenApi();
        Api.Map(app);
        return app;
    }
}
