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
            PhotoXmp.Write(i, o, f);
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
    public void 前からある_XMP_のほかの項目は残す()
    {
        var png = RealPng();
        // ほかのアプリが書いた XMP（タイトルと権利）がある PNG
        var other = """<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"><rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:rights>(c) me</dc:rights><dc:title>夕焼け</dc:title></rdf:Description></rdf:RDF></x:xmpmeta>""";
        var withOther = PngWithXmp(png, other);
        using var i = new MemoryStream(withOther);
        using var o = new MemoryStream();
        PhotoXmp.Write(i, o, new PhotoXmp.Fields(DateTimeOffset.Now, null, null, "wrld_x", "W", "p_0123456789abcdef0123", []));
        var text = System.Text.Encoding.UTF8.GetString(o.ToArray());
        Assert.Contains("<dc:rights>(c) me</dc:rights>", text, StringComparison.Ordinal);
        Assert.Contains("<dc:title>夕焼け</dc:title>", text, StringComparison.Ordinal);
        Assert.Contains("<vrc:WorldID>wrld_x</vrc:WorldID>", text, StringComparison.Ordinal);
        Assert.Contains("xmlns:vrc=", text, StringComparison.Ordinal);
        using (var bmp = SKBitmap.Decode(o.ToArray())) Assert.NotNull(bmp);
        var meta = PhotoMetaReader.Read(new MemoryStream(o.ToArray()), "a.png", DateTime.UtcNow, Tokyo);
        Assert.Equal("wrld_x", meta.WorldId);
    }

    /// <summary>IHDR のあとに XMP の iTXt を入れた PNG（試験用）。</summary>
    static byte[] PngWithXmp(byte[] png, string xmp)
    {
        var data = new List<byte>(System.Text.Encoding.Latin1.GetBytes("XML:com.adobe.xmp")) { 0, 0, 0, 0, 0 };
        data.AddRange(System.Text.Encoding.UTF8.GetBytes(xmp));
        var type = "iTXt"u8.ToArray();
        var crc = 0xFFFFFFFFu;
        foreach (var b in type.Concat(data))
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        crc ^= 0xFFFFFFFFu;
        var chunk = new List<byte>();
        chunk.AddRange(BitConverter.GetBytes(data.Count).Reverse());
        chunk.AddRange(type);
        chunk.AddRange(data);
        chunk.AddRange(BitConverter.GetBytes(crc).Reverse());
        // 署名 8 + IHDR（4+4+13+4）のあと
        return [.. png[..33], .. chunk, .. png[33..]];
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
        // 一緒にいた人を手で決めたので、時刻から当てた Carol は外れる
        Assert.DoesNotContain(catalog.ByPerson(), g => g.Name == "Carol");
        Assert.Equal(0, catalog.List(personId: "usr_00000000-0000-0000-0000-000000000004").Total);
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

    [Fact]
    public async Task 情報を書いた写真は_前に送った同じ場所へ送り直し_手元を消せる()
    {
        var pics = Directory.CreateDirectory(Path.Combine(_dir, "Pictures", "2026-11")).FullName;
        var path = Path.Combine(pics, "VRChat_2026-11-08_02-42-00.000_1920x1080.png");
        File.WriteAllBytes(path, RealPng());
        using var db = Database.Open(Path.Combine(_dir, "s.sqlite3"));
        var catalog = new PhotoCatalog(db, "d_test", Tokyo);
        var root = Path.Combine(_dir, "Pictures");
        catalog.Scan(root);
        var id = catalog.List().Items.Single().Id;
        var nas = Path.Combine(_dir, "nas");
        using var target = new Yamaori.Photos.FolderTarget(nas);
        var sync = new Yamaori.Photos.PhotoSync(catalog, target, [root], null, Tokyo);
        Assert.Equal(1, (await sync.RunAsync("keep", 30, default)).Uploaded);

        var edits = new PhotoEdits(db, Tokyo);
        edits.Save(id, new PhotoEditInput("wrld_custom", "W", null, null, null));
        Assert.Equal(2, edits.WriteToFiles(id).Files); // 手元と、目録にあるフォルダの送り先の写しの両方
        var r = await sync.RunAsync("after_upload", 30, default);
        Assert.Equal(1, r.Uploaded);
        Assert.Equal(1, r.Removed);
        var sent = Directory.GetFiles(Path.Combine(nas, "2026-11"));
        Assert.Single(sent); // 別の名前で増やさない
        Assert.Equal("wrld_custom", PhotoMetaReader.Read(File.OpenRead(sent[0]), Path.GetFileName(sent[0]), DateTime.UtcNow, Tokyo).WorldId);
        Assert.Equal(id, catalog.List().Items.Single().Id);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }
}
