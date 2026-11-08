using System.Text;
using Yamaori.Core;
using Yamaori.Store;

namespace Yamaori.Tests;

public sealed class PhotoTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("yamaori-photo-").FullName;
    static readonly TimeZoneInfo Tokyo = OperatingSystem.IsWindows() ? TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time") : TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    /// <summary>メタデータを読むのに足りる PNG の頭（署名・IHDR・XMP）。画像としては壊れているが、読み方の試験には足りる。</summary>
    static byte[] Png(int w, int h, string? xmp)
    {
        var b = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13 };
        b.AddRange("IHDR"u8.ToArray());
        b.AddRange(BitConverter.GetBytes(w).Reverse());
        b.AddRange(BitConverter.GetBytes(h).Reverse());
        b.AddRange(new byte[] { 8, 6, 0, 0, 0, 0, 0, 0, 0 });
        if (xmp != null) b.AddRange(Encoding.UTF8.GetBytes("iTXtXML:com.adobe.xmp\0\0\0\0\0" + xmp));
        return [.. b];
    }

    const string Xmp = """
        <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF><rdf:Description>
        <xmp:CreatorTool>VRChat</xmp:CreatorTool><xmp:Author>自分。</xmp:Author>
        <xmp:CreateDate>2026-11-08T02:38:21.2636119+09:00</xmp:CreateDate></rdf:Description>
        <rdf:Description><vrc:WorldID>wrld_00000000-0000-0000-0000-00000000000a</vrc:WorldID>
        <vrc:WorldDisplayName>Cushion &amp; Room</vrc:WorldDisplayName><vrc:AuthorID>usr_me</vrc:AuthorID></rdf:Description>
        </rdf:RDF></x:xmpmeta>
        """;

    [Fact]
    public void XMP_から撮った時刻とワールドと撮った人を読む()
    {
        var m = PhotoMetaReader.Read(new MemoryStream(Png(7680, 4320, Xmp)), "VRChat_2026-11-08_02-38-21.263_7680x4320.png", DateTime.UtcNow, Tokyo);
        Assert.Equal("xmp", m.TakenSource);
        Assert.Equal(new DateTime(2026, 11, 7, 17, 38, 21, DateTimeKind.Utc), m.TakenAt.AddTicks(-(m.TakenAt.Ticks % TimeSpan.TicksPerSecond)));
        Assert.Equal((7680, 4320), (m.Width, m.Height));
        Assert.Equal("wrld_00000000-0000-0000-0000-00000000000a", m.WorldId);
        Assert.Equal("Cushion & Room", m.WorldName);
        Assert.Equal(("usr_me", "自分。"), (m.AuthorId, m.AuthorName));
    }

    [Fact]
    public void XMP_がなければファイル名から読む()
    {
        var m = PhotoMetaReader.Read(new MemoryStream(Png(1920, 1080, null)), "VRChat_2021-06-27_23-10-56.500_1920x1080.png", DateTime.UtcNow, Tokyo);
        Assert.Equal("file_name", m.TakenSource);
        Assert.Equal(new DateTime(2021, 6, 27, 14, 10, 56, 500, DateTimeKind.Utc), m.TakenAt);
        Assert.Null(m.WorldId);
        var jpg = PhotoMetaReader.Read(new MemoryStream([1, 2, 3]), "VRChat_1920x1080_2021-06-27_23-10-56.jpg", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), Tokyo);
        Assert.Equal("file_time", jpg.TakenSource); // 古い形のファイル名は読まず、ファイルの時刻で
    }

    [Fact]
    public void 目録に入れ_訪問につなぎ_消えたら置き場所を外す()
    {
        var log = Path.Combine(_dir, "output_log_2026-11-08_02-36-41.txt");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "output_log_2026-11-08_02-36-41.txt"), log);
        var pics = Directory.CreateDirectory(Path.Combine(_dir, "Pictures", "2026-11")).FullName;
        var a = Path.Combine(pics, "VRChat_2026-11-08_02-38-21.263_7680x4320.png");
        File.WriteAllBytes(a, Png(7680, 4320, Xmp));
        File.WriteAllBytes(Path.Combine(pics, "VRChat_2026-11-08_02-42-00.000_1920x1080.png"), Png(1920, 1080, null));
        File.WriteAllText(Path.Combine(pics, "memo.txt"), "写真でない");

        using var db = Database.Open(Path.Combine(_dir, "y.sqlite3"));
        new LogIngestor(db, "d_test").Ingest(log, closed: true, Tokyo);
        var catalog = new PhotoCatalog(db, "d_test", Tokyo);
        var root = Path.Combine(_dir, "Pictures");
        Assert.Equal(new PhotoCatalog.ScanResult(2, 0, 0, 2), catalog.Scan(root));
        Assert.Equal(new PhotoCatalog.ScanResult(0, 0, 0, 2), catalog.Scan(root)); // 変わっていなければ読まない

        var q = new Queries(db, Tokyo);
        var visits = q.Day(new DateOnly(2026, 11, 8));
        var first = catalog.List(visitId: visits[0].Id);
        Assert.Equal("VRChat_2026-11-08_02-38-21.263_7680x4320.png", Assert.Single(first.Items).FileName);
        Assert.Single(catalog.List(visitId: visits[1].Id).Items);
        Assert.Equal(1, visits[0].ScreenshotCount); // ログにスクショの行はないが、目録の写真を数える
        Assert.Equal(1, catalog.List(worldId: "wrld_00000000-0000-0000-0000-00000000000a").Total);

        var id = first.Items[0].Id;
        Assert.Equal(a, catalog.FindFile(id));
        // NAS へ移った（同じ名前と大きさ）→ 同じ写真の置き場所が替わる
        var nas = Directory.CreateDirectory(Path.Combine(_dir, "nas")).FullName;
        File.Move(a, Path.Combine(nas, Path.GetFileName(a)));
        Assert.Equal(1, catalog.Scan(root).Removed);
        Assert.False(catalog.Get(id)!.Available);
        catalog.Scan(nas);
        Assert.True(catalog.Get(id)!.Available);
        Assert.Equal(Path.Combine(nas, Path.GetFileName(a)), catalog.FindFile(id));
        Assert.Equal(2, catalog.List().Total);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }
}
