using SkiaSharp;
using Yamaori.Core;
using Yamaori.Store;

namespace Yamaori.Tests;

public sealed class PhotoEditTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("yamaori-edit-").FullName;
    static readonly TimeZoneInfo Tokyo = OperatingSystem.IsWindows() ? TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time") : TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    static byte[] RealPng(int w = 64, int h = 36)
    {
        using var bmp = new SKBitmap(w, h);
        using (var c = new SKCanvas(bmp)) c.Clear(SKColors.Coral);
        using var img = SKImage.FromBitmap(bmp);
        return img.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    [Fact]
    public void PNG_に_VRChat_と同じ形の_XMP_を書き_読み戻せ_画像は壊れない()
    {
        var png = RealPng();
        var fields = new PhotoXmp.Fields(new DateTimeOffset(2021, 6, 27, 23, 10, 56, TimeSpan.FromHours(9)), "MoomA", "usr_me", "wrld_x", "Cushion & Room", "p_0123456789abcdef0123",
            [new PhotoPerson("usr_a", "Alice|A"), new PhotoPerson(null, "名前だけ")]);
        byte[] Write(byte[] input, PhotoXmp.Fields f)
        {
            using var i = new MemoryStream(input);
            using var o = new MemoryStream();
            PhotoXmp.Write(i, o, PhotoXmp.Build(f));
            return o.ToArray();
        }
        var once = Write(png, fields);
        var twice = Write(once, fields with { WorldName = "別の名前" });
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(System.Text.Encoding.Latin1.GetString(twice), "XML:com.adobe.xmp")); // 前の XMP は入れ替わる
        using (var bmp = SKBitmap.Decode(twice)) Assert.Equal(64, bmp.Width); // 画像として読める
        var meta = PhotoMetaReader.Read(new MemoryStream(twice), "VRChat_1920x1080_2021-06-27_23-10-56.123.png", DateTime.UtcNow, Tokyo);
        Assert.Equal("xmp", meta.TakenSource);
        Assert.Equal(new DateTime(2021, 6, 27, 14, 10, 56, DateTimeKind.Utc), meta.TakenAt);
        Assert.Equal("wrld_x", meta.WorldId);
        Assert.Equal("別の名前", meta.WorldName);
        Assert.Equal("usr_me", meta.AuthorId);
        Assert.Equal("MoomA", meta.AuthorName);
        Assert.Equal("p_0123456789abcdef0123", meta.YamaoriId);
        Assert.Equal([new PhotoPerson("usr_a", "Alice|A"), new PhotoPerson(null, "名前だけ")], meta.Players!);
    }

    [Fact]
    public void 撮った時刻から当て_直した情報で絞り込め_ファイルに書いても同じ写真のまま()
    {
        var log = Path.Combine(_dir, "output_log_2026-11-08_02-36-41.txt");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "output_log_2026-11-08_02-36-41.txt"), log);
        var pics = Directory.CreateDirectory(Path.Combine(_dir, "Pictures", "2026-11")).FullName;
        var path = Path.Combine(pics, "VRChat_2026-11-08_02-42-00.000_1920x1080.png"); // Evermore、Carol がいた
        File.WriteAllBytes(path, RealPng());
        using var db = Database.Open(Path.Combine(_dir, "y.sqlite3"));
        new LogIngestor(db, "d_test").Ingest(log, closed: true, Tokyo);
        var catalog = new PhotoCatalog(db, "d_test", Tokyo);
        catalog.Scan(Path.Combine(_dir, "Pictures"));
        var id = catalog.List().Items.Single().Id;

        var edits = new PhotoEdits(db, Tokyo);
        var info = edits.Info(id)!;
        Assert.True(info.CanWrite);
        Assert.Equal("wrld_00000000-0000-0000-0000-00000000000b", info.Suggestion.WorldId);
        Assert.Contains(info.Suggestion.People, p => p.Name == "Carol");

        Assert.True(edits.Save(id, new PhotoEditInput("wrld_custom", "手で直したワールド", null, null, [new PhotoPerson(null, "Dave")])));
        Assert.Equal("手で直したワールド", catalog.Get(id)!.WorldName);
        Assert.True(catalog.Get(id)!.Edited);
        Assert.Equal(1, catalog.List(worldId: "wrld_custom").Total);
        Assert.Equal(1, catalog.List(personId: "name:Dave").Total);
        Assert.Contains(catalog.ByPerson(), g => g.Key == "name:Dave");
        Assert.Throws<ArgumentException>(() => edits.Save(id, new PhotoEditInput("bad", null, null, null, null)));

        var before = new FileInfo(path).Length;
        var r = edits.WriteToFiles(id);
        Assert.Equal(1, r.Files);
        Assert.NotEqual(before, new FileInfo(path).Length);
        // 読み直しても、別の DB で読んでも、同じ写真の ID
        Assert.Equal(new PhotoCatalog.ScanResult(0, 0, 0, 1), catalog.Scan(Path.Combine(_dir, "Pictures")));
        using var other = Database.Open(Path.Combine(_dir, "other.sqlite3"));
        new PhotoCatalog(other, "d_other", Tokyo).Scan(Path.Combine(_dir, "Pictures"));
        var again = new PhotoCatalog(other).List().Items.Single();
        Assert.Equal(id, again.Id);
        Assert.Equal("wrld_custom", again.WorldId); // ファイルから読める
        Assert.NotNull(edits.Get(id)!.WrittenAt);

        Assert.True(edits.Clear(id));
        Assert.Null(edits.Get(id));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }
}
