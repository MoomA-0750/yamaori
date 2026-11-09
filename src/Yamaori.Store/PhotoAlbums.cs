namespace Yamaori.Store;

public sealed record Album(string Id, string Name, string? Description, string? CoverPhotoId, int Count, DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>写真のアルバムとゴミ箱。アルバムは Yamaori の中だけのもので、写真のファイルは動かさない。</summary>
public sealed class PhotoAlbums(Database db)
{
    public const int MaxName = 200, MaxDescription = 5000, MaxBatch = 5000;

    const string AlbumColumns = """
        a.id, a.name, a.description,
        COALESCE(a.cover_photo_id, (SELECT ap.photo_id FROM album_photos ap JOIN photos p ON p.id = ap.photo_id
            WHERE ap.album_id = a.id AND NOT EXISTS (SELECT 1 FROM photo_trash t WHERE t.photo_id = p.id) ORDER BY p.taken_at DESC LIMIT 1)),
        (SELECT COUNT(*) FROM album_photos ap WHERE ap.album_id = a.id AND NOT EXISTS (SELECT 1 FROM photo_trash t WHERE t.photo_id = ap.photo_id)),
        a.created_at, a.updated_at
        """;

    static Album Read(Microsoft.Data.Sqlite.SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3), r.GetInt32(4), Database.ParseTime(r.GetString(5)), Database.ParseTime(r.GetString(6)));

    public IReadOnlyList<Album> List()
    {
        using var cmd = db.Command($"SELECT {AlbumColumns} FROM albums a ORDER BY a.updated_at DESC");
        using var r = cmd.ExecuteReader();
        var list = new List<Album>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    public Album? Get(string id)
    {
        using var cmd = db.Command($"SELECT {AlbumColumns} FROM albums a WHERE a.id = $id", null, ("$id", id));
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    public Album Create(string name, string? description = null)
    {
        var id = "a_" + Guid.NewGuid().ToString("N")[..20];
        var now = Database.Time(DateTime.UtcNow);
        using var cmd = db.Command("INSERT INTO albums (id, name, description, created_at, updated_at) VALUES ($id, $n, $d, $now, $now)", null,
            ("$id", id), ("$n", Clean(name, MaxName)), ("$d", description == null ? null : Clean(description, MaxDescription)), ("$now", now));
        cmd.ExecuteNonQuery();
        return Get(id)!;
    }

    public Album? Update(string id, string? name, string? description, string? coverPhotoId)
    {
        using var cmd = db.Command("""
            UPDATE albums SET name = COALESCE($n, name), description = CASE WHEN $setd THEN $d ELSE description END,
                cover_photo_id = CASE WHEN $setc THEN $c ELSE cover_photo_id END, updated_at = $now WHERE id = $id
            """, null, ("$id", id), ("$n", name == null ? null : Clean(name, MaxName)),
            ("$setd", description != null), ("$d", description is { Length: > 0 } d ? Clean(d, MaxDescription) : null),
            ("$setc", coverPhotoId != null), ("$c", coverPhotoId is { Length: > 0 } c ? c : null), ("$now", Database.Time(DateTime.UtcNow)));
        return cmd.ExecuteNonQuery() > 0 ? Get(id) : null;
    }

    /// <summary>アルバムを消す（写真は消さない）。</summary>
    public bool Delete(string id)
    {
        using var tx = db.Connection.BeginTransaction();
        using (var a = db.Command("DELETE FROM album_photos WHERE album_id = $id", tx, ("$id", id))) a.ExecuteNonQuery();
        using var b = db.Command("DELETE FROM albums WHERE id = $id", tx, ("$id", id));
        var n = b.ExecuteNonQuery();
        tx.Commit();
        return n > 0;
    }

    /// <summary>写真をアルバムに入れる。目録にある写真だけ。入れた数。</summary>
    public int Add(string albumId, IReadOnlyList<string> photoIds) => Batch(albumId, photoIds, """
        INSERT OR IGNORE INTO album_photos (album_id, photo_id, added_at) SELECT $a, $p, $now WHERE EXISTS (SELECT 1 FROM photos WHERE id = $p)
        """);

    public int Remove(string albumId, IReadOnlyList<string> photoIds) => Batch(albumId, photoIds, "DELETE FROM album_photos WHERE album_id = $a AND photo_id = $p");

    int Batch(string albumId, IReadOnlyList<string> photoIds, string sql)
    {
        if (db.Scalar("SELECT 1 FROM albums WHERE id = $id", ("$id", albumId)) == null) throw new KeyNotFoundException("アルバムが見つからない");
        var n = 0;
        var now = Database.Time(DateTime.UtcNow);
        using var tx = db.Connection.BeginTransaction();
        foreach (var p in photoIds.Distinct().Take(MaxBatch))
        {
            using var cmd = db.Command(sql, tx, ("$a", albumId), ("$p", p), ("$now", now));
            n += cmd.ExecuteNonQuery();
        }
        using (var touch = db.Command("UPDATE albums SET updated_at = $now WHERE id = $a", tx, ("$a", albumId), ("$now", now))) touch.ExecuteNonQuery();
        tx.Commit();
        return n;
    }

    /// <summary>その写真が入っているアルバム。</summary>
    public IReadOnlyList<(string Id, string Name)> AlbumsOf(string photoId)
    {
        using var cmd = db.Command("SELECT a.id, a.name FROM album_photos ap JOIN albums a ON a.id = ap.album_id WHERE ap.photo_id = $p ORDER BY a.name", null, ("$p", photoId));
        using var r = cmd.ExecuteReader();
        var list = new List<(string, string)>();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }

    // ---- ゴミ箱

    /// <summary>ゴミ箱に入れる（Yamaori の一覧から隠すだけ。ファイルはそのまま）。入れた数。</summary>
    public int Trash(IReadOnlyList<string> photoIds)
    {
        var n = 0;
        var now = Database.Time(DateTime.UtcNow);
        using var tx = db.Connection.BeginTransaction();
        foreach (var p in photoIds.Distinct().Take(MaxBatch))
        {
            using var cmd = db.Command("INSERT OR IGNORE INTO photo_trash (photo_id, trashed_at) SELECT $p, $now WHERE EXISTS (SELECT 1 FROM photos WHERE id = $p)", tx,
                ("$p", p), ("$now", now));
            n += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return n;
    }

    /// <summary>ゴミ箱から戻す（空にしたあとのものは戻せない）。戻した数。</summary>
    public int Restore(IReadOnlyList<string> photoIds)
    {
        var n = 0;
        using var tx = db.Connection.BeginTransaction();
        foreach (var p in photoIds.Distinct().Take(MaxBatch))
        {
            using var cmd = db.Command("DELETE FROM photo_trash WHERE photo_id = $p AND purged_at IS NULL", tx, ("$p", p));
            n += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return n;
    }

    public int TrashCount() => Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM photo_trash WHERE purged_at IS NULL"), System.Globalization.CultureInfo.InvariantCulture);

    public sealed record EmptyResult(int Photos, int Files, IReadOnlyList<string> Failed);

    /// <summary>
    /// ゴミ箱を空にする: ゴミ箱の写真のファイルを消す。戻せない。消すのは、見張っている写真のフォルダ（watchedRoots）の中の置き場所だけ
    /// （写真の送り先のフォルダに送った写しは消さない）。消す前に、目録のときと大きさ・時刻が同じか、置き場所が 2 つ以上あれば中身が同じかを確かめ、
    /// 違えば（差し替わった・名前と大きさが同じで中身の違う写真）その写真は消さずにゴミ箱に残す。
    /// 縮小は、この端末の thumbsDir と、写真のフォルダの .yamaori/thumbs から消す。
    /// </summary>
    public EmptyResult Empty(string? thumbsDir, IReadOnlyList<string> watchedRoots, IReadOnlyList<int>? thumbSizes = null)
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var roots = watchedRoots.Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r))).ToHashSet(cmp);
        var ids = new List<string>();
        using (var cmd = db.Command("SELECT photo_id FROM photo_trash WHERE purged_at IS NULL"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) ids.Add(r.GetString(0));
        int photos = 0, files = 0;
        var failed = new List<string>();
        foreach (var id in ids)
        {
            var locs = new List<(string Path, string Root, string Mtime, long Size)>();
            using (var cmd = db.Command("SELECT path, root, mtime, size FROM photo_locations WHERE photo_id = $id", null, ("$id", id)))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) locs.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3)));
            var mine = locs.Where(l => roots.Contains(Path.TrimEndingDirectorySeparator(l.Root))).ToList();
            // 確かめる: 目録のときのままか（差し替わっていないか）、置き場所どうしで中身が同じか
            var problem = (string?)null;
            var present = new List<(string Path, string Root, string Mtime, long Size)>();
            foreach (var l in mine)
            {
                var fi = new FileInfo(l.Path);
                if (!fi.Exists) continue;
                if (fi.Length != l.Size || Database.Time(fi.LastWriteTimeUtc) != l.Mtime) { problem = $"{l.Path}: 目録を作ったあとで変わっている（写真の目録を読み直してから、もう一度空にする）"; break; }
                present.Add(l);
            }
            if (problem == null && present.Count > 1)
            {
                try
                {
                    var hashes = present.Select(l => Convert.ToHexString(Hash(l.Path))).Distinct().Count();
                    if (hashes > 1) problem = $"{present[0].Path} ほか: 同じ名前と大きさで中身の違う写真がある（別の写真を消さないよう、消さない）";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problem = $"{present[0].Path}: {ex.Message}"; }
            }
            if (problem != null) { failed.Add(problem); continue; }
            var ok = true;
            foreach (var l in present)
            {
                try
                {
                    File.Delete(l.Path);
                    files++;
                    using var del = db.Command("DELETE FROM photo_locations WHERE path = $p", null, ("$p", l.Path));
                    del.ExecuteNonQuery();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ok = false;
                    failed.Add($"{l.Path}: {ex.Message}");
                }
            }
            if (!ok) continue;
            // 見張るフォルダにもうない置き場所（消えたもの）は目録から外す。送り先の写しの置き場所は残す
            foreach (var l in mine.Except(present))
            {
                using var del = db.Command("DELETE FROM photo_locations WHERE path = $p", null, ("$p", l.Path));
                del.ExecuteNonQuery();
            }
            // 縮小（この端末のものと、写真のフォルダの .yamaori に置いたもの）
            if (id.Length > 4)
            {
                var dirs = new List<string>();
                if (thumbsDir != null) dirs.Add(Path.Combine(thumbsDir, id[..4]));
                dirs.AddRange(mine.Select(l => Path.Combine(l.Root, ".yamaori", "thumbs", id[..4])).Distinct(cmp));
                foreach (var dir in dirs)
                    try
                    {
                        if (Directory.Exists(dir))
                            foreach (var t in Directory.GetFiles(dir, id + "_*.jpg")) File.Delete(t);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed.Add($"{dir}: 縮小を消せなかった: {ex.Message}"); }
            }
            using var tx = db.Connection.BeginTransaction();
            using (var a = db.Command("DELETE FROM album_photos WHERE photo_id = $id", tx, ("$id", id))) a.ExecuteNonQuery();
            using (var u = db.Command("UPDATE photo_trash SET purged_at = $now WHERE photo_id = $id", tx, ("$id", id), ("$now", Database.Time(DateTime.UtcNow)))) u.ExecuteNonQuery();
            tx.Commit();
            photos++;
        }
        return new EmptyResult(photos, files, failed);
    }

    static byte[] Hash(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return System.Security.Cryptography.SHA256.HashData(f);
    }

    static string Clean(string s, int max)
    {
        s = s.Trim();
        return s.Length > max ? s[..max] : s;
    }
}
