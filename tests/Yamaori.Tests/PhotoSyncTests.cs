using System.Net;
using Yamaori.Photos;
using Yamaori.Store;

namespace Yamaori.Tests;

public sealed class PhotoSyncTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("yamaori-sync-").FullName;
    static readonly TimeZoneInfo Tokyo = OperatingSystem.IsWindows() ? TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time") : TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    (Database Db, PhotoCatalog Catalog, string Local) Setup(params string[] names)
    {
        var local = Directory.CreateDirectory(Path.Combine(_dir, "Pictures")).FullName;
        foreach (var n in names) File.WriteAllBytes(Path.Combine(local, n), [1, 2, 3, 4, 5]);
        var db = Database.Open(Path.Combine(_dir, "y.sqlite3"));
        var catalog = new PhotoCatalog(db, "d_test", Tokyo);
        catalog.Scan(local);
        return (db, catalog, local);
    }

    [Fact]
    public async Task フォルダへ送り_全部残す設定では手元を消さない()
    {
        var (db, catalog, local) = Setup("VRChat_2026-11-08_02-38-21.263_1920x1080.png", "VRChat_2026-10-31_23-59-00.000_1920x1080.png");
        using var _ = db;
        var nas = Path.Combine(_dir, "nas");
        using var target = new FolderTarget(nas);
        var sync = new PhotoSync(catalog, target, [local], null, Tokyo);
        var r = await sync.RunAsync("keep", 30, default);
        Assert.Equal(new PhotoSync.Result(2, 0, 0, 0), r);
        Assert.True(File.Exists(Path.Combine(nas, "2026-11", "VRChat_2026-11-08_02-38-21.263_1920x1080.png")));
        Assert.True(File.Exists(Path.Combine(nas, "2026-10", "VRChat_2026-10-31_23-59-00.000_1920x1080.png")));
        Assert.Equal(2, Directory.GetFiles(local).Length);
        // 二度目は何もしない
        Assert.Equal(new PhotoSync.Result(0, 0, 0, 0), await sync.RunAsync("keep", 30, default));
    }

    [Fact]
    public async Task 送ったら消す設定では_届いたものだけ消し_送り先の置き場所で読める()
    {
        var (db, catalog, local) = Setup("VRChat_2026-11-08_02-38-21.263_1920x1080.png");
        using var _ = db;
        var nas = Path.Combine(_dir, "nas");
        using var target = new FolderTarget(nas);
        var r = await new PhotoSync(catalog, target, [local], null, Tokyo).RunAsync("after_upload", 30, default);
        Assert.Equal(1, r.Removed);
        Assert.Empty(Directory.GetFiles(local));
        var id = catalog.List().Items.Single().Id;
        Assert.Equal(Path.Combine(nas, "2026-11", "VRChat_2026-11-08_02-38-21.263_1920x1080.png"), catalog.FindFile(id));
    }

    [Fact]
    public async Task N日残す設定では古いものだけ消す()
    {
        var recent = $"VRChat_{DateTime.Now.AddDays(-2):yyyy-MM-dd_HH-mm-ss}.000_1920x1080.png";
        var (db, catalog, local) = Setup(recent, "VRChat_2020-01-01_00-00-00.000_1920x1080.png");
        using var _ = db;
        using var target = new FolderTarget(Path.Combine(_dir, "nas"));
        var r = await new PhotoSync(catalog, target, [local], null, Tokyo).RunAsync("days", 30, default);
        Assert.Equal(1, r.Removed);
        Assert.Equal(recent, Path.GetFileName(Assert.Single(Directory.GetFiles(local))));
    }

    [Fact]
    public async Task 送り先で大きさが合わなければ記録せず消さない()
    {
        var (db, catalog, local) = Setup("VRChat_2026-11-08_02-38-21.263_1920x1080.png");
        using var _ = db;
        using var target = new WebDavTarget("https://nas.example/dav/photos", "me", "pw", new FakeDav(truncate: true));
        var r = await new PhotoSync(catalog, target, [local], null, Tokyo).RunAsync("after_upload", 30, default);
        Assert.Equal(1, r.Failed);
        Assert.Equal(0, r.Removed);
        Assert.Single(Directory.GetFiles(local));
    }

    [Fact]
    public async Task WebDAV_へ送る()
    {
        var (db, catalog, local) = Setup("VRChat_2026-11-08_02-38-21.263_1920x1080.png");
        using var _ = db;
        var dav = new FakeDav(truncate: false);
        using var target = new WebDavTarget("https://nas.example/dav/photos", "me", "pw", dav);
        var r = await new PhotoSync(catalog, target, [local], null, Tokyo).RunAsync("keep", 30, default);
        Assert.Equal(1, r.Uploaded);
        Assert.Contains("MKCOL /dav/photos/2026-11/", dav.Log);
        Assert.Contains("PUT /dav/photos/2026-11/VRChat_2026-11-08_02-38-21.263_1920x1080.png", dav.Log);
        Assert.All(dav.Auth, a => Assert.Equal("Basic", a));
    }

    /// <summary>WebDAV の偽の相手（受け取ったものを覚え、HEAD で大きさを返す）。</summary>
    sealed class FakeDav(bool truncate) : HttpMessageHandler
    {
        readonly Dictionary<string, long> _files = [];
        public List<string> Log { get; } = [];
        public List<string?> Auth { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken cancel)
        {
            var path = Uri.UnescapeDataString(req.RequestUri!.AbsolutePath);
            Log.Add($"{req.Method} {path}");
            Auth.Add(req.Headers.Authorization?.Scheme);
            if (req.Method == HttpMethod.Put)
            {
                var body = await req.Content!.ReadAsByteArrayAsync(cancel);
                _files[path] = truncate ? body.Length - 1 : body.Length;
                return new HttpResponseMessage(HttpStatusCode.Created);
            }
            if (req.Method == HttpMethod.Head)
            {
                if (!_files.TryGetValue(path, out var len)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                var res = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                res.Content.Headers.ContentLength = len;
                return res;
            }
            return new HttpResponseMessage(HttpStatusCode.Created); // MKCOL
        }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }
}
