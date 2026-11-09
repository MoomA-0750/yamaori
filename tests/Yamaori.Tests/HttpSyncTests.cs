using Yamaori.Store;
using Yamaori.Web;

namespace Yamaori.Tests;

/// <summary>子の端末から親へ、HTTP まで通して送る（記録・メモ・消したメモ）。</summary>
[Collection("http")]
public sealed class HttpSyncTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("yamaori-httpsync-").FullName;
    static readonly TimeZoneInfo Tokyo = OperatingSystem.IsWindows() ? TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time") : TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
    const string FileName = "output_log_2026-11-08_02-36-41.txt";

    [Fact]
    public async Task 子の記録とメモと消したメモが_親に届く()
    {
        await using var parent = new HttpHost();
        string secret;
        using (var db = parent.OpenDb()) secret = new AuthStore(db).CreateToken("laptop", TokenScopes.Sync).Secret;

        var childOptions = new YamaoriOptions { DataDir = Path.Combine(_dir, "child") };
        string dev;
        using (var db = Database.Open(childOptions.DatabasePath))
        {
            dev = db.SelfDevice("laptop");
            var log = Path.Combine(_dir, FileName);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", FileName), log, true);
            new LogIngestor(db, dev).Ingest(log, closed: true, Tokyo);
        }
        var state = new YamaoriState { DeviceId = dev, DeviceName = "laptop", Listen = "local" };
        using var http = parent.Server.CreateClient();
        http.BaseAddress = new Uri("http://localhost:47899/");
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);

        Note note;
        using (var db = Database.Open(childOptions.DatabasePath)) note = new Queries(db).AddNote("day", "2026-11-08", "子で書いた", dev);
        var old = new List<string>();
        var r = await SyncWorker.PushAsync(http, childOptions, state, default, old.Add);
        Assert.Equal(1, r.Sources);
        Assert.Empty(old);
        using (var db = parent.OpenDb())
        {
            Assert.Equal(2L, db.Scalar("SELECT COUNT(*) FROM visits WHERE device_id = $d", ("$d", dev)));
            Assert.Equal("子で書いた", new Queries(db).Note(note.Id)!.Body);
        }

        using (var db = Database.Open(childOptions.DatabasePath)) new Queries(db).DeleteNote(note.Id);
        r = await SyncWorker.PushAsync(http, childOptions, state, default);
        Assert.Equal(1, r.Notes);
        using (var db = parent.OpenDb()) Assert.Null(new Queries(db).Note(note.Id));
        // 変わっていなければ何も送らない
        Assert.Equal(new SyncWorker.PushResult(0, 0), await SyncWorker.PushAsync(http, childOptions, state, default));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
