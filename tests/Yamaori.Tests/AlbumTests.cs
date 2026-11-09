using Yamaori.Store;

namespace Yamaori.Tests;

public sealed class AlbumTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("yamaori-album-").FullName;
    static readonly TimeZoneInfo Tokyo = OperatingSystem.IsWindows() ? TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time") : TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    (Database Db, PhotoCatalog Catalog, string Local, string Nas) Setup()
    {
        var local = Directory.CreateDirectory(Path.Combine(_dir, "Pictures", "2026-11")).FullName;
        var nas = Directory.CreateDirectory(Path.Combine(_dir, "nas", "2026-11")).FullName;
        foreach (var n in new[] { "VRChat_2026-11-08_02-38-21.263_1920x1080.png", "VRChat_2026-11-08_02-39-00.000_1920x1080.png", "VRChat_2026-11-08_02-40-00.000_1920x1080.png" })
        {
            File.WriteAllBytes(Path.Combine(local, n), [1, 2, 3, (byte)n.Length]);
            File.WriteAllBytes(Path.Combine(nas, n), [1, 2, 3, (byte)n.Length]); // NAS にも同じ写真がある
        }
        var db = Database.Open(Path.Combine(_dir, "y.sqlite3"));
        var catalog = new PhotoCatalog(db, "d_test", Tokyo);
        catalog.Scan(Path.Combine(_dir, "Pictures"));
        catalog.Scan(Path.Combine(_dir, "nas"));
        return (db, catalog, local, nas);
    }

    [Fact]
    public void アルバムに入れて外し_写真は消さない()
    {
        var (db, catalog, _, _) = Setup();
        using var _db = db;
        var ids = catalog.List().Items.Select(p => p.Id).ToList();
        var albums = new PhotoAlbums(db);
        var a = albums.Create("  夏の思い出  ");
        Assert.Equal("夏の思い出", a.Name);
        Assert.Equal(2, albums.Add(a.Id, [ids[0], ids[1], ids[1], "p_nope"]));
        Assert.Equal(2, albums.Get(a.Id)!.Count);
        Assert.Equal(ids[0], albums.Get(a.Id)!.CoverPhotoId); // 表紙は新しい写真
        Assert.Equal(2, catalog.List(albumId: a.Id).Total);
        Assert.Equal(1, albums.Remove(a.Id, [ids[0]]));
        Assert.Single(catalog.List(albumId: a.Id).Items);
        Assert.Equal(a.Id, Assert.Single(albums.AlbumsOf(ids[1])).Id);
        Assert.True(albums.Delete(a.Id));
        Assert.Equal(3, catalog.List().Total);
        Assert.Throws<KeyNotFoundException>(() => albums.Add(a.Id, [ids[0]]));
    }

    [Fact]
    public void ゴミ箱は隠すだけで戻せ_空にするとこの_PC_と_NAS_から消え_また見つかっても出さない()
    {
        var (db, catalog, local, nas) = Setup();
        using var _db = db;
        var ids = catalog.List().Items.Select(p => p.Id).ToList();
        var albums = new PhotoAlbums(db);
        var a = albums.Create("a");
        albums.Add(a.Id, [ids[0]]);
        Assert.Equal(2, albums.Trash([ids[0], ids[1]]));
        Assert.Equal(1, catalog.List().Total);
        Assert.Equal(2, catalog.List(trash: true).Total);
        Assert.Equal(0, albums.Get(a.Id)!.Count); // ゴミ箱の写真はアルバムでも数えない
        Assert.Equal(6, Directory.GetFiles(local).Length + Directory.GetFiles(nas).Length); // ファイルはそのまま

        Assert.Equal(1, albums.Restore([ids[1]]));
        Assert.Equal(2, catalog.List().Total);

        var side = Path.Combine(_dir, "nas", ".yamaori", "thumbs", ids[0][..4], ids[0] + "_320.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(side)!);
        File.WriteAllBytes(side, [1]);
        var r = albums.Empty(Path.Combine(_dir, "thumbs"), [Path.Combine(_dir, "Pictures"), Path.Combine(_dir, "nas")]);
        Assert.False(File.Exists(side)); // NAS に置いた縮小も消す
        Assert.Equal(1, r.Photos);
        Assert.Equal(2, r.Files); // この PC と NAS の両方
        Assert.Empty(r.Failed);
        Assert.Equal(4, Directory.GetFiles(local).Length + Directory.GetFiles(nas).Length);
        Assert.Equal(0, catalog.List(trash: true).Total);
        Assert.Equal(0, albums.Restore([ids[0]])); // 空にしたものは戻せない
        Assert.Null(catalog.Get(ids[0]));

        // 同じ写真がまた見つかっても（送り先から戻ってきたなど）、一覧には出さない
        var name = catalog.List().Items.Select(p => p.FileName).First();
        File.WriteAllBytes(Path.Combine(local, "VRChat_2026-11-08_02-40-00.000_1920x1080.png"), [1, 2, 3, (byte)"VRChat_2026-11-08_02-40-00.000_1920x1080.png".Length]);
        catalog.Scan(Path.Combine(_dir, "Pictures"));
        Assert.Equal(2, catalog.List().Total);
        Assert.NotNull(name);
    }

    [Fact]
    public void ゴミ箱を空にしても_送り先の写しと_差し替わったものと_中身の違う同じ名前の写真は消さない()
    {
        var (db, catalog, local, nas) = Setup();
        using var _db = db;
        var albums = new PhotoAlbums(db);
        var ids = catalog.List().Items.Select(p => p.Id).ToList();
        // 1 枚目: 見張っているのは Pictures だけ（nas は写真の送り先として目録に入った）→ nas の写しは消さない
        albums.Trash([ids[0]]);
        var r = albums.Empty(null, [Path.Combine(_dir, "Pictures")]);
        Assert.Equal(1, r.Photos);
        Assert.Equal(3, Directory.GetFiles(nas).Length);
        Assert.Equal(2, Directory.GetFiles(local).Length);

        // 2 枚目: 目録のあとで差し替わった → 消さない
        var p2 = catalog.List().Items.First(p => p.Id == ids[1]);
        var path2 = Path.Combine(local, p2.FileName);
        File.SetLastWriteTimeUtc(path2, DateTime.UtcNow.AddMinutes(5));
        albums.Trash([ids[1]]);
        r = albums.Empty(null, [Path.Combine(_dir, "Pictures"), Path.Combine(_dir, "nas")]);
        Assert.Equal(0, r.Photos);
        Assert.Single(r.Failed);
        Assert.True(File.Exists(path2));

        // 3 枚目: NAS の同じ名前・同じ大きさのファイルが別の中身 → 消さない
        var p3 = catalog.List().Items.First(p => p.Id == ids[2]);
        var nas3 = Path.Combine(nas, p3.FileName);
        var bytes = File.ReadAllBytes(nas3);
        bytes[0] ^= 0xFF;
        var mtime = File.GetLastWriteTimeUtc(nas3);
        File.WriteAllBytes(nas3, bytes);
        File.SetLastWriteTimeUtc(nas3, mtime);
        albums.Restore([ids[1]]);
        albums.Trash([ids[2]]);
        r = albums.Empty(null, [Path.Combine(_dir, "Pictures"), Path.Combine(_dir, "nas")]);
        Assert.Equal(0, r.Photos);
        Assert.True(File.Exists(nas3));
        Assert.True(File.Exists(Path.Combine(local, p3.FileName)));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }
}
