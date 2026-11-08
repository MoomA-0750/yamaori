using System.Text.Json;
using Yamaori.Core;

namespace Yamaori.Store;

/// <summary>拡張機能が vrchat.com のページから取ったワールドの情報。</summary>
public sealed record WorldDetailsInput(
    string? Name, string? AuthorId, string? AuthorName, string? Description, string? ImageUrl, string? ThumbnailUrl,
    string? ReleaseStatus, int? Capacity, IReadOnlyList<string>? Tags, IReadOnlyList<string>? Platforms, int? Visits, int? Favorites);

public sealed record MissingWorld(string Id, string? Name, DateTime? LastVisitAt, int VisitCount);

public sealed record FriendInput(string Id, string? DisplayName);

public sealed record FriendSnapshotResult(bool First, int Count, IReadOnlyList<string> Added, IReadOnlyList<string> Removed, IReadOnlyList<string> Renamed);

/// <summary>
/// 拡張機能から届いたものを入れる。ワールドの情報（出どころ extension。VRCX の情報より優先）と、フレンドの一覧の写し。
/// フレンドの一覧は前に届いた一覧と比べ、増えた・減った・名前を変えた人を出来事 friend として記録する。
/// 時刻は「前に見たときから今回までの間」としか分からないので、今回の時刻で記録し、前に見た時刻を data に入れる。
/// </summary>
public sealed class ExtensionStore(Database db)
{
    public const string Source = "extension";
    static readonly JsonSerializerOptions Json = new();

    public void SaveWorld(string worldId, WorldDetailsInput w)
    {
        w = Clean(w);
        var categories = WorldClassifier.Classify(w.Description, w.Tags);
        using var cmd = db.Command("""
            INSERT INTO worlds (id, name, author_id, author_name, description, image_url, thumbnail_url, release_status, capacity, tags, platforms,
                visits_total, favorites_total, categories, details_source, details_updated_at)
            VALUES ($id, $n, $a, $an, $d, $i, $t, $s, $c, $tags, $p, $v, $f, $cat, 'extension', $now)
            ON CONFLICT (id) DO UPDATE SET name = COALESCE($n, worlds.name), author_id = $a, author_name = $an, description = $d, image_url = $i,
                thumbnail_url = $t, release_status = $s, capacity = $c, tags = $tags, platforms = $p, visits_total = $v, favorites_total = $f,
                categories = $cat, details_source = 'extension', details_updated_at = $now
            """, null,
            ("$id", worldId), ("$n", w.Name), ("$a", w.AuthorId), ("$an", w.AuthorName), ("$d", w.Description), ("$i", w.ImageUrl), ("$t", w.ThumbnailUrl),
            ("$s", w.ReleaseStatus), ("$c", w.Capacity), ("$tags", w.Tags == null ? null : JsonSerializer.Serialize(w.Tags, Json)),
            ("$p", w.Platforms == null ? null : JsonSerializer.Serialize(w.Platforms, Json)), ("$v", w.Visits), ("$f", w.Favorites),
            ("$cat", JsonSerializer.Serialize(categories, Json)), ("$now", Database.Time(DateTime.UtcNow)));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 外から来た値を整える。画像は VRChat の置き場（https の vrchat.cloud・vrchat.com）のものだけ受け取り
    /// （ほかのアドレスだと、画面を開いたときにそこへ見に行き、開いた時刻を知らせてしまう）、文字と数の長さを絞る。
    /// </summary>
    public static WorldDetailsInput Clean(WorldDetailsInput w) => w with
    {
        Name = Cut(w.Name, 200),
        AuthorId = Cut(w.AuthorId, 100),
        AuthorName = Cut(w.AuthorName, 200),
        Description = Cut(w.Description, 4000),
        ImageUrl = VrchatImage(w.ImageUrl),
        ThumbnailUrl = VrchatImage(w.ThumbnailUrl),
        ReleaseStatus = Cut(w.ReleaseStatus, 50),
        Tags = w.Tags?.Take(100).Select(t => Cut(t, 100)!).ToList(),
        Platforms = w.Platforms?.Take(20).Select(t => Cut(t, 50)!).ToList(),
    };

    static string? Cut(string? s, int max) => s == null ? null : s.Length <= max ? s : s[..max];

    public static string? VrchatImage(string? url) =>
        url is { Length: <= 1000 } && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
        && (u.Host is "vrchat.cloud" or "vrchat.com" || u.Host.EndsWith(".vrchat.cloud", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".vrchat.com", StringComparison.OrdinalIgnoreCase))
            ? url : null;

    /// <summary>詳しい情報のないワールド（行った回数の多い順。まとめて取るときに使う）。</summary>
    public IReadOnlyList<MissingWorld> MissingWorlds(int limit = 20)
    {
        using var cmd = db.Command("""
            SELECT w.id, w.name, MAX(v.joined_at), COUNT(v.id) FROM worlds w LEFT JOIN visits v ON v.world_id = w.id
            WHERE w.details_source IS NULL AND w.id LIKE 'wrld_%'
            GROUP BY w.id ORDER BY COUNT(v.id) DESC, MAX(v.joined_at) DESC LIMIT $limit
            """, null, ("$limit", Math.Clamp(limit, 1, 200)));
        using var r = cmd.ExecuteReader();
        var list = new List<MissingWorld>();
        while (r.Read()) list.Add(new MissingWorld(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : Database.ParseTime(r.GetString(2)), r.GetInt32(3)));
        return list;
    }

    public FriendSnapshotResult SaveFriends(string accountId, IReadOnlyList<FriendInput> friends, DateTime? now = null)
    {
        var at = now ?? DateTime.UtcNow;
        var current = friends.Where(f => f.Id.StartsWith("usr_", StringComparison.Ordinal)).GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First().DisplayName, StringComparer.Ordinal);
        var previous = new Dictionary<string, string?>(StringComparer.Ordinal);
        using (var cmd = db.Command("SELECT user_id, display_name FROM friend_snapshots WHERE account_id = $a", null, ("$a", accountId)))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) previous[r.GetString(0)] = r.IsDBNull(1) ? null : r.GetString(1);
        var lastAt = db.Scalar("SELECT taken_at FROM friend_snapshot_meta WHERE account_id = $a", ("$a", accountId)) as string;
        var first = lastAt == null;

        var added = current.Keys.Where(k => !previous.ContainsKey(k)).ToList();
        var removed = previous.Keys.Where(k => !current.ContainsKey(k)).ToList();
        var renamed = current.Where(kv => previous.TryGetValue(kv.Key, out var old) && old != null && kv.Value != null && old != kv.Value).Select(kv => kv.Key).ToList();

        using var tx = db.Connection.BeginTransaction();
        void Event(string userId, string? name, string type, Dictionary<string, string?> data)
        {
            data["type"] = type;
            data["observed_since"] = lastAt;
            using var cmd = db.Command("""
                INSERT OR IGNORE INTO events (id, source, device_id, account_id, visit_id, at, kind, user_id, name, data)
                VALUES ($id, $s, NULL, $acc, NULL, $at, 'friend', $u, $n, $d)
                """, tx, ("$id", StableId.Of("activity", Source, accountId, userId, type, Database.Time(at))), ("$s", Source), ("$acc", accountId),
                ("$at", Database.Time(at)), ("$u", userId), ("$n", name), ("$d", JsonSerializer.Serialize(data, Json)));
            cmd.ExecuteNonQuery();
        }
        // 初めての一覧は、いつフレンドになったか分からないので出来事にしない
        if (!first)
        {
            foreach (var id in added) Event(id, current[id], "Friend", []);
            foreach (var id in removed) Event(id, previous[id], "Unfriend", []);
            foreach (var id in renamed) Event(id, current[id], "DisplayName", new() { ["previous_name"] = previous[id] });
        }
        using (var del = db.Command("DELETE FROM friend_snapshots WHERE account_id = $a", tx, ("$a", accountId))) del.ExecuteNonQuery();
        foreach (var (id, name) in current)
        {
            using var ins = db.Command("INSERT INTO friend_snapshots (account_id, user_id, display_name, seen_at) VALUES ($a, $u, $n, $at)", tx,
                ("$a", accountId), ("$u", id), ("$n", name), ("$at", Database.Time(at)));
            ins.ExecuteNonQuery();
        }
        using (var meta = db.Command("""
            INSERT INTO friend_snapshot_meta (account_id, taken_at, count) VALUES ($a, $at, $c)
            ON CONFLICT (account_id) DO UPDATE SET taken_at = $at, count = $c
            """, tx, ("$a", accountId), ("$at", Database.Time(at)), ("$c", current.Count)))
            meta.ExecuteNonQuery();
        tx.Commit();
        return new FriendSnapshotResult(first, current.Count, first ? [] : added, first ? [] : removed, first ? [] : renamed);
    }
}
