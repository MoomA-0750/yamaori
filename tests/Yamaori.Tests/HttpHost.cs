using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Yamaori.Store;

namespace Yamaori.Tests;

/// <summary>
/// Yamaori を TestServer に載せて、HTTP の入口（認証・経路）まで通して試す。
/// 既定の相手は外（LAN）から来た要求。loopback: true でこの PC からの要求にする。
/// </summary>
[CollectionDefinition("http", DisableParallelization = true)]
public sealed class HttpCollection;

public sealed class HttpHost : IAsyncDisposable
{
    // 設定のファイルの見張りを切る（試験で何十も起こすと、ファイルの見張りの数の上限に当たる）
    static HttpHost() => Environment.SetEnvironmentVariable("DOTNET_hostBuilder__reloadConfigOnChange", "false");

    readonly string _dir = Path.Combine(Path.GetTempPath(), "yamaori-http-" + Guid.NewGuid().ToString("N"));
    public WebApplication App { get; }
    public TestServer Server { get; }
    public YamaoriOptions Options { get; }

    public HttpHost(string listen = "lan", string? password = "pw-test-1234", IDictionary<string, string?>? extra = null)
    {
        Directory.CreateDirectory(Path.Combine(_dir, "logs"));
        Directory.CreateDirectory(Path.Combine(_dir, "pics"));
        var values = new Dictionary<string, string?>
        {
            ["DataDir"] = _dir,
            ["LogDir"] = Path.Combine(_dir, "logs"),
            ["Listen"] = listen,
            ["Port"] = "47899",
            ["DeviceName"] = "test",
            ["PhotoDirs:0"] = Path.Combine(_dir, "pics"),
        };
        if (extra != null) foreach (var (k, v) in extra) values[k] = v;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        Options = new YamaoriOptions();
        config.Bind(Options);
        if (password != null)
            using (var db = Database.Open(Options.DatabasePath)) new AuthStore(db).SetPassword(password);
        App = Yamaori.Server.Build([], config, Options, b => b.WebHost.UseTestServer());
        App.StartAsync().GetAwaiter().GetResult();
        Server = App.GetTestServer();
    }

    public Database OpenDb() => Database.Open(Options.DatabasePath);

    /// <summary>要求を送る。headers は名前と値の組。</summary>
    public async Task<HttpContext> SendAsync(string method, string path, bool loopback = false, string? body = null,
        IEnumerable<(string, string)>? headers = null)
    {
        return await Server.SendAsync(c =>
        {
            c.Request.Method = method;
            var q = path.IndexOf('?');
            c.Request.Path = q < 0 ? path : path[..q];
            if (q >= 0) c.Request.QueryString = new QueryString(path[q..]);
            c.Request.Host = new HostString("localhost", 47899);
            c.Connection.RemoteIpAddress = loopback ? IPAddress.Loopback : IPAddress.Parse("192.168.1.50");
            foreach (var (k, v) in headers ?? []) c.Request.Headers[k] = v;
            if (body != null)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                c.Request.ContentType = "application/json";
                c.Request.ContentLength = bytes.Length;
                c.Request.Body = new MemoryStream(bytes);
                // TestServer は、組み立てた要求に本文があることを知らせない（知らせないと、API は本文がないとみなす）
                c.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new HasBody());
            }
        });
    }

    sealed class HasBody : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
