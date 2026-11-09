using System.Diagnostics;
using SkiaSharp;
using Yamaori.Core;
using Yamaori.Store;
using Yamaori.Web;

namespace Yamaori.Tests;

public sealed class ThumbTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("yamaori-thumb-").FullName;

    string Png(string folder, string name, int w = 640, int h = 360)
    {
        Directory.CreateDirectory(folder);
        using var bmp = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bmp)) canvas.Clear(SKColors.CornflowerBlue);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        var path = Path.Combine(folder, name);
        using (var f = File.Create(path)) data.SaveTo(f);
        return path;
    }

    [Fact]
    public void 写真の_ID_は_NAS_のスクリプトと同じ決め方()
    {
        // yamaori-thumbs.py の photo_id("VRChat_2026-11-08_02-38-21.263_1920x1080.png", 5) と同じ値
        Assert.Equal("p_3eb82ff6e4428fd42507", StableId.Of("photo", "VRChat_2026-11-08_02-38-21.263_1920x1080.png", 5L));
    }

    [Fact]
    public async Task 写真のフォルダに縮小があれば_写真を読まずにそれを使い_作ったものは置ける()
    {
        var root = Path.Combine(_dir, "photos");
        var id = "p_0123456789abcdef0123";
        var side = Thumbnails.SidecarPath(root, id, 320);
        Directory.CreateDirectory(Path.GetDirectoryName(side)!);
        File.WriteAllBytes(side, [1, 2, 3]);
        var thumbs = new Thumbnails(Path.Combine(_dir, "cache"), [root]);
        var got = await thumbs.GetAsync(id, Path.Combine(root, "missing.png"), 320, default);
        Assert.NotNull(got);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(got!));
        Assert.True(thumbs.Has(id, 320));

        // 作る側（NAS で動かす Yamaori）は、写真のフォルダにも置く
        var photo = Png(Path.Combine(root, "2026-11"), "VRChat_2026-11-08_02-38-21.263_640x360.png");
        var writer = new Thumbnails(Path.Combine(_dir, "cache2"), [root], writeSidecar: true);
        Assert.NotNull(await writer.GetAsync("p_ffff000000000000aaaa", photo, 320, default));
        Assert.True(File.Exists(Thumbnails.SidecarPath(root, "p_ffff000000000000aaaa", 320)));
    }

    [Fact]
    public void 写真のフォルダの_yamaori_の中は_写真の目録に入れない()
    {
        var root = Path.Combine(_dir, "photos");
        Png(Path.Combine(root, "2026-11"), "VRChat_2026-11-08_02-38-21.263_640x360.png");
        Png(Path.Combine(root, ".yamaori", "thumbs", "p_01"), "p_0123_320.png");
        using var db = Database.Open(Path.Combine(_dir, "y.sqlite3"));
        Assert.Equal(1, new PhotoCatalog(db, "d").Scan(root).Seen);
    }

    [Theory]
    [InlineData("01:00-07:00", 3, true)]
    [InlineData("01:00-07:00", 8, false)]
    [InlineData("23:00-05:00", 23, true)]
    [InlineData("23:00-05:00", 2, true)]
    [InlineData("23:00-05:00", 12, false)]
    [InlineData("", 3, false)]
    [InlineData("nope", 3, false)]
    public void 夜の時間帯(string window, int hour, bool inside) =>
        Assert.Equal(inside, ThumbWorker.InWindow(window, new DateTime(2026, 11, 9, hour, 30, 0)));

    [Fact]
    public void NAS_に入る鍵は_OpenSSH_の公開鍵の形()
    {
        var o = new YamaoriOptions { DataDir = _dir };
        var pub = new Yamaori.Photos.RemoteThumbs(Microsoft.Extensions.Options.Options.Create(o)).NewKey();
        Assert.StartsWith("ssh-rsa AAAA", pub, StringComparison.Ordinal);
        var r = new Yamaori.Photos.RemoteThumbs(Microsoft.Extensions.Options.Options.Create(o)).Current();
        Assert.Equal(pub, r.PublicKey);
        Assert.NotNull(Yamaori.Photos.Secrets.Unprotect(r.PrivateKey));
        if (!File.Exists("/usr/bin/ssh-keygen")) return;
        var f = Path.Combine(_dir, "k.pub");
        File.WriteAllText(f, pub + "\n");
        using var p = Process.Start(new ProcessStartInfo("/usr/bin/ssh-keygen", ["-l", "-f", f]) { RedirectStandardOutput = true })!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        Assert.Contains("3072", output, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }
}
