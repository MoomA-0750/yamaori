using System.Text.Json;
using Yamaori.Core;

namespace Yamaori.Store;

public sealed record PhotoEdit(string? WorldId, string? WorldName, string? AuthorId, string? AuthorName, IReadOnlyList<PhotoPerson> People, DateTime EditedAt, DateTime? WrittenAt);


/// <summary>撮った時刻から当てた情報（その時刻にいた訪問のワールド・アカウント・同じインスタンスにいた人）。</summary>
public sealed record PhotoSuggestion(string? VisitId, string? WorldId, string? WorldName, string? AuthorId, string? AuthorName, IReadOnlyList<PhotoPerson> People);

/// <summary>写真の情報: 写真から読んだもの（File）、手で直したもの（Edit）、撮った時刻から当てたもの（Suggestion）。</summary>
public sealed record PhotoInfo(PhotoView Photo, PhotoMeta? File, PhotoEdit? Edit, PhotoSuggestion Suggestion, bool CanWrite);

/// <summary>People を渡すと（空でも）、一緒にいた人は時刻から当てた人の代わりにそれを使う。渡さなければ、時刻から当てた人のまま。</summary>
public sealed record PhotoEditInput(string? WorldId, string? WorldName, string? AuthorId, string? AuthorName, IReadOnlyList<PhotoPerson>? People);

/// <summary>
/// 写真の情報を手で直す（メタデータのない昔の写真など）。直したものは写真の行とは別に持ち、目録を読み直しても消えない。
/// 写真のファイル（PNG）にも、VRChat と同じ形の XMP で書ける（<see cref="PhotoXmp"/>）。書いたあとも同じ写真の ID を使う。
/// </summary>
public sealed class PhotoEdits(Database db, TimeZoneInfo? timeZone = null)
{
    readonly TimeZoneInfo _tz = timeZone ?? TimeZoneInfo.Local;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const int MaxPeople = 200, MaxText = 300;

    public PhotoEdit? Get(string photoId)
    {
        using var cmd = db.Command("SELECT world_id, world_name, author_id, author_name, people, edited_at, written_at FROM photo_edits WHERE photo_id = $id", null, ("$id", photoId));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        return new PhotoEdit(S(0), S(1), S(2), S(3), JsonSerializer.Deserialize<List<PhotoPerson>>(r.GetString(4), Json) ?? [],
            Database.ParseTime(r.GetString(5)), S(6) is { } w ? Database.ParseTime(w) : null);
    }

    public PhotoInfo? Info(string photoId)
    {
        var photo = new PhotoCatalog(db, null, _tz).Get(photoId);
        if (photo == null) return null;
        var path = new PhotoCatalog(db).FindFile(photoId);
        PhotoMeta? file = null;
        if (path != null)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                file = PhotoMetaReader.Read(fs, Path.GetFileName(path), File.GetLastWriteTimeUtc(path), _tz);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var canWrite = path != null && Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase);
        return new PhotoInfo(photo, file, Get(photoId), Suggest(photo.TakenAt), canWrite);
    }

    /// <summary>撮った時刻にいた訪問から当てる。</summary>
    public PhotoSuggestion Suggest(DateTime takenAt)
    {
        var t = Database.Time(takenAt);
        string? visitId = null, worldId = null, worldName = null, accountId = null;
        using (var cmd = db.Command("""
            SELECT v.id, v.world_id, COALESCE(v.world_name, w.name), v.account_id FROM visits v LEFT JOIN worlds w ON w.id = v.world_id
            WHERE v.joined_at <= $t AND $t < COALESCE(v.left_at, '9999') ORDER BY v.joined_at DESC LIMIT 1
            """, null, ("$t", t)))
        using (var r = cmd.ExecuteReader())
            if (r.Read()) { visitId = r.GetString(0); worldId = r.GetString(1); worldName = r.IsDBNull(2) ? null : r.GetString(2); accountId = r.IsDBNull(3) ? null : r.GetString(3); }
        var people = new List<PhotoPerson>();
        string? authorName = null;
        if (visitId != null)
        {
            using (var cmd = db.Command("""
                SELECT DISTINCT e.user_id, e.name FROM encounters e JOIN visits v ON v.id = e.visit_id
                WHERE e.visit_id = $v AND e.joined_at <= $t AND $t < COALESCE(e.left_at, v.left_at, '9999') ORDER BY e.name
                """, null, ("$v", visitId), ("$t", t)))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) people.Add(new PhotoPerson(r.IsDBNull(0) ? null : r.GetString(0), r.GetString(1)));
            if (accountId != null)
                authorName = db.Scalar("SELECT name FROM account_names WHERE id = $a ORDER BY last_seen_at DESC LIMIT 1", ("$a", accountId)) as string
                    ?? db.Scalar("SELECT value FROM settings WHERE key = 'account_name:' || $a", ("$a", accountId)) as string;
        }
        return new PhotoSuggestion(visitId, worldId, worldName, accountId, authorName, people);
    }

    /// <summary>直した情報を置く（空の項目は持たない）。目録にない写真なら false。</summary>
    public bool Save(string photoId, PhotoEditInput input)
    {
        if (db.Scalar("SELECT 1 FROM photos WHERE id = $id", ("$id", photoId)) == null) return false;
        static string? C(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim() is var t && t.Length > MaxText ? t[..MaxText] : s.Trim();
        var worldId = C(input.WorldId);
        if (worldId != null && !worldId.StartsWith("wrld_", StringComparison.Ordinal)) throw new ArgumentException("ワールドの ID は wrld_…");
        var authorId = C(input.AuthorId);
        if (authorId != null && !authorId.StartsWith("usr_", StringComparison.Ordinal)) throw new ArgumentException("撮った人の ID は usr_…");
        var people = (input.People ?? []).Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => new PhotoPerson(C(p.Id) is { } id && id.StartsWith("usr_", StringComparison.Ordinal) ? id : null, C(p.Name)!))
            .DistinctBy(p => p.Id ?? "name:" + p.Name).Take(MaxPeople).ToList();
        using var cmd = db.Command("""
            INSERT INTO photo_edits (photo_id, world_id, world_name, author_id, author_name, people, people_set, edited_at) VALUES ($id, $w, $wn, $a, $an, $p, $ps, $now)
            ON CONFLICT (photo_id) DO UPDATE SET world_id = $w, world_name = $wn, author_id = $a, author_name = $an, people = $p, people_set = $ps, edited_at = $now
            """, null, ("$id", photoId), ("$ps", input.People != null ? 1 : 0), ("$w", worldId), ("$wn", C(input.WorldName)), ("$a", authorId), ("$an", C(input.AuthorName)),
            ("$p", JsonSerializer.Serialize(people, Json)), ("$now", Database.Time(DateTime.UtcNow)));
        cmd.ExecuteNonQuery();
        // 直したワールドも、ワールドの一覧に入れる（ワールドのページが開けるように）
        if (worldId != null)
        {
            using var w = db.Command("INSERT INTO worlds (id, name) VALUES ($w, $n) ON CONFLICT (id) DO UPDATE SET name = COALESCE(worlds.name, $n)", null, ("$w", worldId), ("$n", C(input.WorldName)));
            w.ExecuteNonQuery();
        }
        return true;
    }

    public bool Clear(string photoId)
    {
        using var cmd = db.Command("DELETE FROM photo_edits WHERE photo_id = $id", null, ("$id", photoId));
        return cmd.ExecuteNonQuery() > 0;
    }

    public sealed record WriteResult(int Files, IReadOnlyList<string> Skipped);

    /// <summary>
    /// 今の情報（直したものが先、なければ写真から読んだもの）を、写真のファイルの全部の置き場所（この PC と NAS）に XMP で書く。PNG だけ。
    /// 別の名前に書いてから置き換え、目録の大きさと時刻も書いたあとのものにする（同じ写真のまま）。
    /// </summary>
    public WriteResult WriteToFiles(string photoId)
    {
        var photo = new PhotoCatalog(db, null, _tz).Get(photoId) ?? throw new KeyNotFoundException("写真が見つからない");
        var edit = Get(photoId);
        var people = edit?.People ?? [];
        var local = TimeZoneInfo.ConvertTimeFromUtc(photo.TakenAt, _tz);
        var fields = new PhotoXmp.Fields(new DateTimeOffset(local, _tz.GetUtcOffset(photo.TakenAt)), photo.AuthorName, photo.AuthorId, photo.WorldId, photo.WorldName, photoId, people);
        var paths = new List<string>();
        using (var cmd = db.Command("SELECT path FROM photo_locations WHERE photo_id = $id", null, ("$id", photoId)))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) paths.Add(r.GetString(0));
        int files = 0;
        var skipped = new List<string>();
        foreach (var path in paths)
        {
            if (!File.Exists(path)) { skipped.Add($"{path}: 見つからない"); continue; }
            if (!Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)) { skipped.Add($"{path}: PNG でない（書けるのは PNG だけ）"); continue; }
            var tmp = path + ".yamaori-tmp";
            try
            {
                var mtime = File.GetLastWriteTimeUtc(path);
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                    PhotoXmp.Write(input, output, fields);
                File.Move(tmp, path, overwrite: true);
                // 撮った時刻の手がかりにファイルの時刻を使う写真もあるので、時刻は元のままにする
                File.SetLastWriteTimeUtc(path, mtime);
                var info = new FileInfo(path);
                using var up = db.Command("UPDATE photo_locations SET size = $s, mtime = $m WHERE path = $p", null,
                    ("$s", info.Length), ("$m", Database.Time(info.LastWriteTimeUtc)), ("$p", path));
                up.ExecuteNonQuery();
                files++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                try { File.Delete(tmp); } catch (IOException) { }
                skipped.Add($"{path}: {ex.Message}");
            }
        }
        if (files > 0)
        {
            // 写真の大きさを書いたあとのものにする（送り先へは、前に送った同じ場所へ送り直される）
            var written = paths.Where(File.Exists).Select(p => new FileInfo(p).Length).FirstOrDefault();
            if (written > 0)
                using (var sz = db.Command("UPDATE photos SET size = $s WHERE id = $id", null, ("$s", written), ("$id", photoId))) sz.ExecuteNonQuery();
            // 直した情報がなくても、書いた印は残す（どの写真に書いたか分かるように）
            using var cmd = db.Command("""
                INSERT INTO photo_edits (photo_id, world_id, world_name, author_id, author_name, people, edited_at, written_at) VALUES ($id, $w, $wn, $a, $an, '[]', $now, $now)
                ON CONFLICT (photo_id) DO UPDATE SET written_at = $now
                """, null, ("$id", photoId), ("$w", photo.WorldId), ("$wn", photo.WorldName), ("$a", photo.AuthorId), ("$an", photo.AuthorName), ("$now", Database.Time(DateTime.UtcNow)));
            cmd.ExecuteNonQuery();
        }
        return new WriteResult(files, skipped);
    }
}
