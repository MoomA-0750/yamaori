using System.Globalization;
using Microsoft.Data.Sqlite;
using Yamaori.Core;

namespace Yamaori.Store;

public sealed record PhotoView(
    string Id, string FileName, long Size, DateTime TakenAt, string TakenSource, int? Width, int? Height,
    string? WorldId, string? WorldName, string? AuthorId, string? AuthorName, bool Available);

/// <summary>
/// 写真の目録。見張っているフォルダを読み、写真ごとにメタデータと置き場所を持つ。
/// 写真の ID はファイル名と大きさから決めるので、NAS へ移っても同じ写真になる（置き場所が増える・替わる）。
/// 訪問とのつなぎは読み出すときに撮った時刻で行う（訪問を作り直しても外れない）。
/// </summary>
public sealed class PhotoCatalog(Database db, string? deviceId = null, TimeZoneInfo? timeZone = null)
{
    readonly TimeZoneInfo _tz = timeZone ?? TimeZoneInfo.Local;

    public sealed record ScanResult(int Added, int Updated, int Removed, int Seen);

    /// <summary>フォルダを読み直す（変わっていないファイルは中を読まない）。見つからなくなった置き場所は消す。</summary>
    public ScanResult Scan(string root, CancellationToken cancel = default)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) return new ScanResult(0, 0, 0, 0);
        var known = new Dictionary<string, (string Mtime, long Size)>(StringComparer.Ordinal);
        using (var cmd = db.Command("SELECT path, mtime, size FROM photo_locations WHERE root = $r", null, ("$r", root)))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) known[r.GetString(0)] = (r.GetString(1), r.GetInt64(2));

        int added = 0, updated = 0, seen = 0;
        var present = new HashSet<string>(StringComparer.Ordinal);
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }); }
        catch (IOException) { return new ScanResult(0, 0, 0, 0); }
        foreach (var path in files)
        {
            cancel.ThrowIfCancellationRequested();
            if (!PhotoMetaReader.IsPhoto(path)) continue;
            // 写真のフォルダの中の .yamaori（NAS で作った縮小など）は写真ではない
            if (path.Contains(Path.DirectorySeparatorChar + ".yamaori" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            seen++;
            present.Add(path);
            FileInfo info;
            try { info = new FileInfo(path); } catch (IOException) { continue; }
            var mtime = Database.Time(info.LastWriteTimeUtc);
            if (known.TryGetValue(path, out var k) && k.Mtime == mtime && k.Size == info.Length) continue;
            try
            {
                Add(path, root, info);
                if (known.ContainsKey(path)) updated++; else added++;
            }
            catch (IOException) { /* 書き込み中など。次に読む */ }
            catch (UnauthorizedAccessException) { }
        }

        var removed = 0;
        foreach (var path in known.Keys.Where(p => !present.Contains(p)))
        {
            using var cmd = db.Command("DELETE FROM photo_locations WHERE path = $p", null, ("$p", path));
            removed += cmd.ExecuteNonQuery();
        }
        return new ScanResult(added, updated, removed, seen);
    }

    /// <summary>1 枚を目録に入れる（新しく撮られたときなど）。</summary>
    public string Add(string path, string root, FileInfo? info = null)
    {
        info ??= new FileInfo(path);
        PhotoMeta meta;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            meta = PhotoMetaReader.Read(fs, info.Name, info.LastWriteTimeUtc, _tz);
        var id = StableId.Of("photo", info.Name, info.Length);
        using var tx = db.Connection.BeginTransaction();
        using (var cmd = db.Command("""
            INSERT INTO photos (id, file_name, size, taken_at, taken_source, width, height, world_id, world_name, author_id, author_name, indexed_at)
            VALUES ($id, $f, $s, $t, $ts, $w, $h, $wid, $wn, $aid, $an, $now)
            ON CONFLICT (id) DO UPDATE SET taken_at = $t, taken_source = $ts, width = $w, height = $h, world_id = $wid, world_name = $wn, author_id = $aid, author_name = $an, indexed_at = $now
            """, tx,
            ("$id", id), ("$f", info.Name), ("$s", info.Length), ("$t", Database.Time(meta.TakenAt)), ("$ts", meta.TakenSource),
            ("$w", meta.Width), ("$h", meta.Height), ("$wid", meta.WorldId), ("$wn", meta.WorldName), ("$aid", meta.AuthorId), ("$an", meta.AuthorName),
            ("$now", Database.Time(DateTime.UtcNow))))
            cmd.ExecuteNonQuery();
        using (var cmd = db.Command("""
            INSERT INTO photo_locations (path, photo_id, root, mtime, size, device_id) VALUES ($p, $id, $r, $m, $s, $d)
            ON CONFLICT (path) DO UPDATE SET photo_id = $id, root = $r, mtime = $m, size = $s, device_id = $d
            """, tx,
            ("$p", path), ("$id", id), ("$r", Path.GetFullPath(root)), ("$m", Database.Time(info.LastWriteTimeUtc)), ("$s", info.Length), ("$d", deviceId)))
            cmd.ExecuteNonQuery();
        // 写真から分かったワールドも、ワールドの一覧に入れる（訪問の記録がない時期の写真でも、ワールドのページが開けるように）
        if (meta.WorldId != null)
        {
            using var w = db.Command("INSERT INTO worlds (id, name) VALUES ($w, $n) ON CONFLICT (id) DO UPDATE SET name = COALESCE(worlds.name, $n)", tx,
                ("$w", meta.WorldId), ("$n", meta.WorldName));
            w.ExecuteNonQuery();
        }
        tx.Commit();
        return id;
    }

    const string Columns = """
        p.id, p.file_name, p.size, p.taken_at, p.taken_source, p.width, p.height, p.world_id, p.world_name, p.author_id, p.author_name,
        EXISTS (SELECT 1 FROM photo_locations l WHERE l.photo_id = p.id)
        """;

    static PhotoView Read(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetInt64(2), Database.ParseTime(r.GetString(3)), r.GetString(4),
        r.IsDBNull(5) ? null : r.GetInt32(5), r.IsDBNull(6) ? null : r.GetInt32(6), Str(r, 7), Str(r, 8), Str(r, 9), Str(r, 10), r.GetInt64(11) != 0);

    /// <summary>
    /// 撮ったときにその人が同じインスタンスにいた（人の ID か、名前だけの人は name:名前）。
    /// その人の会った記録から写真を引く（記録は数件〜数百件なので、写真を 1 枚ずつ調べるより速い）。
    /// </summary>
    const string WithPerson = """
        p.id IN (SELECT p2.id FROM encounters e JOIN visits v ON v.id = e.visit_id
                 JOIN photos p2 ON p2.taken_at >= e.joined_at AND p2.taken_at < COALESCE(e.left_at, v.left_at, '9999')
                 WHERE e.user_id = $person OR (e.user_id IS NULL AND 'name:' || e.name = $person))
        """;

    public sealed record PhotoGroup(string Key, string? Name, int Count, string LatestPhotoId, DateTime LatestAt);

    sealed record Shot(string Id, string Taken, string? World);

    List<Shot> Shots()
    {
        using var cmd = db.Command("SELECT id, taken_at, world_id FROM photos ORDER BY taken_at");
        using var r = cmd.ExecuteReader();
        var list = new List<Shot>();
        while (r.Read()) list.Add(new Shot(r.GetString(0), r.GetString(1), Str(r, 2)));
        return list;
    }

    /// <summary>時刻の文字列（並べ替えが時刻の順）で、from 以上の最初の写真の位置。</summary>
    static int LowerBound(List<Shot> shots, string from)
    {
        int lo = 0, hi = shots.Count;
        while (lo < hi) { var mid = (lo + hi) / 2; if (string.CompareOrdinal(shots[mid].Taken, from) < 0) lo = mid + 1; else hi = mid; }
        return lo;
    }

    static IReadOnlyList<PhotoGroup> Group(IEnumerable<(string Key, Shot Shot)> pairs, Func<string, string?> name, int limit) =>
        pairs.GroupBy(x => x.Key)
            .Select(g =>
            {
                var latest = g.MaxBy(x => x.Shot.Taken, StringComparer.Ordinal).Shot;
                return new PhotoGroup(g.Key, name(g.Key), g.Select(x => x.Shot.Id).Distinct().Count(), latest.Id, Database.ParseTime(latest.Taken));
            })
            .OrderByDescending(g => g.Count).ThenByDescending(g => g.LatestAt)
            .Take(Math.Clamp(limit, 1, 1000)).ToList();

    /// <summary>
    /// ワールドごとの写真の数（多い順）。表紙はいちばん新しい写真。
    /// 写真のワールドは、写真のメタデータ、なければ撮った時刻にいた訪問のワールド（DB の中で 1 枚ずつ引くと遅いので、並べて突き合わせる）。
    /// </summary>
    public IReadOnlyList<PhotoGroup> ByWorld(int limit = 200)
    {
        var shots = Shots();
        var visits = new List<(string Joined, string Left, string World)>();
        using (var cmd = db.Command("SELECT joined_at, COALESCE(left_at, '9999'), world_id FROM visits ORDER BY joined_at"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) visits.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
        var pairs = new List<(string, Shot)>();
        var vi = -1;
        foreach (var s in shots)
        {
            if (s.World != null) { pairs.Add((s.World, s)); continue; }
            // 撮った時刻より前に入った最後の訪問
            while (vi + 1 < visits.Count && string.CompareOrdinal(visits[vi + 1].Joined, s.Taken) <= 0) vi++;
            if (vi >= 0 && string.CompareOrdinal(s.Taken, visits[vi].Left) < 0) pairs.Add((visits[vi].World, s));
        }
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var cmd = db.Command("SELECT id, name FROM worlds WHERE name IS NOT NULL"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) names[r.GetString(0)] = r.GetString(1);
        using (var cmd = db.Command("SELECT world_id, world_name FROM photos WHERE world_id IS NOT NULL AND world_name IS NOT NULL GROUP BY world_id"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) names.TryAdd(r.GetString(0), r.GetString(1));
        return Group(pairs, k => names.GetValueOrDefault(k), limit);
    }

    /// <summary>撮ったときに同じインスタンスにいた人ごとの写真の数（多い順）。表紙はいちばん新しい写真。</summary>
    public IReadOnlyList<PhotoGroup> ByPerson(int limit = 200)
    {
        var shots = Shots();
        var pairs = new List<(string, Shot)>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var cmd = db.Command("""
            SELECT COALESCE(e.user_id, 'name:' || e.name), e.name, e.joined_at, COALESCE(e.left_at, v.left_at, '9999')
            FROM encounters e JOIN visits v ON v.id = e.visit_id
            """))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var who = r.GetString(0);
                names[who] = r.GetString(1);
                var to = r.GetString(3);
                for (var i = LowerBound(shots, r.GetString(2)); i < shots.Count && string.CompareOrdinal(shots[i].Taken, to) < 0; i++)
                    pairs.Add((who, shots[i]));
            }
        // 見出しの名前は、最後に見た名前
        using (var cmd = db.Command("SELECT user_id, name FROM people WHERE name IS NOT NULL"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) names[r.GetString(0)] = r.GetString(1);
        return Group(pairs, k => names.GetValueOrDefault(k), limit);
    }

    /// <summary>写真の一覧（新しい順）。visitId を渡すと、その訪問の間に撮った写真（古い順）。personId を渡すと、その人が同じインスタンスにいたときの写真。</summary>
    public Page<PhotoView> List(DateTime? from = null, DateTime? to = null, string? worldId = null, string? visitId = null, int limit = 100, int offset = 0, string? personId = null)
    {
        var where = new List<string>();
        var args = new List<(string, object?)>();
        if (personId != null) { where.Add(WithPerson); args.Add(("$person", personId)); }
        if (from is { } f) { where.Add("p.taken_at >= $from"); args.Add(("$from", Database.Time(f))); }
        if (to is { } t) { where.Add("p.taken_at < $to"); args.Add(("$to", Database.Time(t))); }
        if (worldId != null) { where.Add("(p.world_id = $world OR (p.world_id IS NULL AND EXISTS (SELECT 1 FROM visits v WHERE v.world_id = $world AND p.taken_at >= v.joined_at AND p.taken_at < COALESCE(v.left_at, '9999'))))"); args.Add(("$world", worldId)); }
        if (visitId != null)
        {
            where.Add("EXISTS (SELECT 1 FROM visits v WHERE v.id = $visit AND p.taken_at >= v.joined_at AND p.taken_at < COALESCE(v.left_at, '9999'))");
            args.Add(("$visit", visitId));
        }
        var w = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        var total = Convert.ToInt32(db.Scalar($"SELECT COUNT(*) FROM photos p {w}", [.. args]), CultureInfo.InvariantCulture);
        args.Add(("$limit", Math.Clamp(limit, 1, 500)));
        args.Add(("$offset", Math.Max(0, offset)));
        var order = visitId != null ? "ASC" : "DESC";
        using var cmd = db.Command($"SELECT {Columns} FROM photos p {w} ORDER BY p.taken_at {order} LIMIT $limit OFFSET $offset", null, [.. args]);
        using var r = cmd.ExecuteReader();
        var items = new List<PhotoView>();
        while (r.Read()) items.Add(Read(r));
        return new Page<PhotoView>(items, total);
    }

    public PhotoView? Get(string id)
    {
        using var cmd = db.Command($"SELECT {Columns} FROM photos p WHERE p.id = $id", null, ("$id", id));
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    /// <summary>今読める置き場所（目録にある道だけを返す。任意の道は渡さない）。</summary>
    public string? FindFile(string id)
    {
        using var cmd = db.Command("SELECT path FROM photo_locations WHERE photo_id = $id ORDER BY (device_id IS NULL), path", null, ("$id", id));
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (File.Exists(r.GetString(0))) return r.GetString(0);
        return null;
    }

    static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    /// <summary>写真の ID と置き場所の一つ（新しい順）。縮小を前もって作るため。</summary>
    public IReadOnlyList<(string Id, string Path)> AllNewestFirst()
    {
        using var cmd = db.Command("""
            SELECT p.id, (SELECT l.path FROM photo_locations l WHERE l.photo_id = p.id ORDER BY (l.device_id IS NULL), l.path LIMIT 1)
            FROM photos p ORDER BY p.taken_at DESC
            """);
        using var r = cmd.ExecuteReader();
        var list = new List<(string, string)>();
        while (r.Read()) if (!r.IsDBNull(1)) list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }

    // ---- 送る

    public sealed record LocalPhoto(string PhotoId, string Path, string FileName, long Size, DateTime TakenAt, string? RemotePath = null, string? Mtime = null);

    static string RootsIn(IReadOnlyList<string> roots, List<(string, object?)> args)
    {
        for (var i = 0; i < roots.Count; i++) args.Add(($"$r{i}", Path.GetFullPath(roots[i])));
        return string.Join(", ", roots.Select((_, i) => $"$r{i}"));
    }

    /// <summary>手元のフォルダにあって、まだその送り先へ送っていない写真（古い順）。</summary>
    public IReadOnlyList<LocalPhoto> PendingUploads(IReadOnlyList<string> localRoots, string target, int limit = 200)
    {
        var args = new List<(string, object?)> { ("$t", target), ("$limit", limit) };
        var inRoots = RootsIn(localRoots, args);
        return ReadLocal($"""
            SELECT p.id, l.path, p.file_name, p.size, p.taken_at FROM photos p JOIN photo_locations l ON l.photo_id = p.id
            WHERE l.root IN ({inRoots}) AND NOT EXISTS (SELECT 1 FROM photo_uploads u WHERE u.photo_id = p.id AND u.target = $t)
            GROUP BY p.id ORDER BY p.taken_at LIMIT $limit
            """, args);
    }

    public void RecordUpload(string photoId, string target, string remotePath, long size)
    {
        using var cmd = db.Command("""
            INSERT INTO photo_uploads (photo_id, target, remote_path, size, uploaded_at) VALUES ($p, $t, $r, $s, $now)
            ON CONFLICT (photo_id, target) DO UPDATE SET remote_path = $r, size = $s, uploaded_at = $now
            """, null, ("$p", photoId), ("$t", target), ("$r", remotePath), ("$s", size), ("$now", Database.Time(DateTime.UtcNow)));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 手元から消してよい写真: その送り先へ同じ大きさで送った記録があり、手元のフォルダにあるもの。
    /// takenBefore を渡すと、それより前に撮ったものだけ（N 日残す）。
    /// </summary>
    public IReadOnlyList<LocalPhoto> RemovableLocal(IReadOnlyList<string> localRoots, string target, DateTime? takenBefore)
    {
        var args = new List<(string, object?)> { ("$t", target), ("$before", takenBefore is { } b ? Database.Time(b) : "9999") };
        var inRoots = RootsIn(localRoots, args);
        return ReadLocal($"""
            SELECT p.id, l.path, p.file_name, p.size, p.taken_at, u.remote_path, l.mtime FROM photos p
            JOIN photo_locations l ON l.photo_id = p.id
            JOIN photo_uploads u ON u.photo_id = p.id AND u.target = $t AND u.size = p.size
            WHERE l.root IN ({inRoots}) AND p.taken_at < $before
            ORDER BY p.taken_at
            """, args);
    }

    /// <summary>同じ写真（名前と大きさが同じ）のほかの置き場所。</summary>
    public IReadOnlyList<string> OtherLocations(string photoId, string path)
    {
        using var cmd = db.Command("SELECT path FROM photo_locations WHERE photo_id = $id AND path <> $p", null, ("$id", photoId), ("$p", path));
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>送った記録を消す（送り先から消えていたので、送り直す）。</summary>
    public void ForgetUpload(string photoId, string target)
    {
        using var cmd = db.Command("DELETE FROM photo_uploads WHERE photo_id = $p AND target = $t", null, ("$p", photoId), ("$t", target));
        cmd.ExecuteNonQuery();
    }

    public void RemoveLocation(string path)
    {
        using var cmd = db.Command("DELETE FROM photo_locations WHERE path = $p", null, ("$p", path));
        cmd.ExecuteNonQuery();
    }

    List<LocalPhoto> ReadLocal(string sql, List<(string, object?)> args)
    {
        using var cmd = db.Command(sql, null, [.. args]);
        using var r = cmd.ExecuteReader();
        var list = new List<LocalPhoto>();
        while (r.Read()) list.Add(new LocalPhoto(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), Database.ParseTime(r.GetString(4)),
            r.FieldCount > 5 && !r.IsDBNull(5) ? r.GetString(5) : null, r.FieldCount > 6 && !r.IsDBNull(6) ? r.GetString(6) : null));
        return list;
    }
}
