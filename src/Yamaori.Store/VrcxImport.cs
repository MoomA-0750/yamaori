using System.Globalization;
using Microsoft.Data.Sqlite;
using Yamaori.Core;

namespace Yamaori.Store;

/// <summary>
/// VRCX の DB（%AppData%\VRCX\VRCX.sqlite3）を読むだけで取り込む。表の定義は vrcx-team/VRCX の src/services/database/ による。
/// <list type="bullet">
/// <item>訪問（gamelog_location）: 入った時刻と、その場にいた長さ（time、ミリ秒）。time が 0 なら推定（次に入った時刻、3 時間まで）</item>
/// <item>会った人（gamelog_join_leave）: 訪問のインスタンスと時刻で訪問につなぐ。VRCX のアカウント本人は外す</item>
/// <item>動画（gamelog_video_play）、通知（&lt;userId&gt;_notifications。ログと同じ ID の決め方で重ねない）</item>
/// <item>フレンドの増減と名前・ランクの変化（&lt;userId&gt;_friend_log_history）→ 出来事 friend。ログでは取れないもの</item>
/// <item>ローカルのお気に入りのワールド（favorite_world）と、その詳しい情報（cache_world）→ worlds・world_favorites</item>
/// <item>ワールドのメモ（world_memos）と人のメモ（memos）→ Yamaori のメモ（出どころ vrcx。取り込み直すと書き換わる）</item>
/// </list>
/// 同じ端末のログの記録がある期間の訪問は飛ばす（ログを正とする）。表の名前の頭の &lt;userId&gt; は、ユーザー ID から - と _ を除いた形。
/// </summary>
public sealed class VrcxImport(Database db, string deviceId)
{
    public const string Source = "vrcx";
    static readonly TimeSpan MaxGap = TimeSpan.FromHours(3);

    public sealed record Result(int Visits, int Encounters, int Events, int FriendEvents, int Worlds, int Favorites, int Notes, int Skipped, IReadOnlyList<string> Accounts);

    /// <param name="replace">前に取り込んだものと別の DB でも、入れ替えて取り込む。</param>
    public Result Import(string vrcxPath, bool replace = false)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = vrcxPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        var tables = Strings(c, "SELECT name FROM sqlite_master WHERE type = 'table'").ToHashSet(StringComparer.Ordinal);
        // アカウントごとの表の頭（usr_1e59… → usr1e59…）
        var prefixes = tables.Where(t => t.EndsWith("_friend_log_history", StringComparison.Ordinal)).Select(t => t[..^"_friend_log_history".Length]).ToList();
        var userIds = AllUserIds(c, tables);
        string? AccountOf(string prefix) => userIds.FirstOrDefault(id => Normalize(id) == prefix);
        var accounts = prefixes.Select(AccountOf).OfType<string>().ToList();
        var selfIds = accounts.ToHashSet(StringComparer.Ordinal);
        var account = accounts.Count == 1 ? accounts[0] : null;

        // DB の見分け: アカウントと、いちばん古い訪問（VRCX は書き足していくので、続きの DB でも同じ）
        var oldest = tables.Contains("gamelog_location") ? Rows(c, "SELECT MIN(created_at) FROM gamelog_location", r => Str(r, 0)).FirstOrDefault() : null;
        var origin = $"{string.Join(",", accounts.Order(StringComparer.Ordinal))}@{oldest}";
        ImportGuard.CheckOrigin(db, Source, origin, replace);
        var covered = LogCoveredRanges();
        var vatVisits = ImportGuard.VisitsOf(db, VatImport.Source);
        var visits = new List<Visit>();
        var encounters = new List<Encounter>();
        var events = new List<ActivityEvent>();
        var skipped = 0;

        // ---- 訪問
        if (tables.Contains("gamelog_location"))
        {
            var rows = Rows(c, "SELECT id, created_at, location, world_id, world_name, time FROM gamelog_location ORDER BY created_at",
                r => (Id: r.GetInt64(0), At: Utc(r.GetString(1)), Location: Str(r, 2) ?? "", World: Str(r, 3), Name: Str(r, 4), Ms: r.IsDBNull(5) ? 0L : r.GetInt64(5)))
                .Where(x => x.At != null && x.World != null && x.Location.StartsWith("wrld_", StringComparison.Ordinal)).ToList();
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var at = row.At!.Value;
                if (covered.Any(cv => at >= cv.From && at <= cv.To)) { skipped++; continue; }
                // VAT から入れた同じ訪問（同じワールドに 5 分以内に入った）は入れない
                if (ImportGuard.SameVisit(vatVisits, row.World!, at)) { skipped++; continue; }
                DateTime left;
                string reason;
                if (row.Ms > 0) { left = at.AddMilliseconds(row.Ms); reason = "left"; }
                else
                {
                    var next = i + 1 < rows.Count ? rows[i + 1].At : null;
                    left = next is { } n && n - at <= MaxGap ? n : at;
                    reason = "estimated";
                }
                left = ImportGuard.ClipToCovered(at, left, covered);
                var colon = row.Location.IndexOf(':', StringComparison.Ordinal);
                var instance = colon > 0 ? row.Location[(colon + 1)..] : "";
                var info = InstanceInfo.Parse(instance);
                visits.Add(new Visit(StableId.Of("visit", Source, row.Id), Source, row.World!, row.Name, instance, info.Number, info.Access,
                    info.OwnerId, info.GroupId, info.Region, at, left, reason, account));
            }
        }

        // 訪問の場所と時刻から、どの訪問か（会った人・動画をつなぐ）
        var byLocation = visits.GroupBy(v => v.WorldId + ":" + v.Instance).ToDictionary(g => g.Key, g => g.OrderBy(v => v.JoinedAt).ToList(), StringComparer.Ordinal);
        Visit? FindVisit(string? location, DateTime at)
        {
            if (location != null && byLocation.TryGetValue(location, out var list))
                return list.LastOrDefault(v => v.JoinedAt <= at.AddSeconds(5) && at <= (v.LeftAt ?? DateTime.MaxValue).AddSeconds(5));
            return null;
        }

        // ---- 会った人
        if (tables.Contains("gamelog_join_leave"))
        {
            var open = new Dictionary<(string Visit, string User), Encounter>();
            foreach (var r in Rows(c, "SELECT id, created_at, type, display_name, location, user_id, time FROM gamelog_join_leave ORDER BY created_at, id",
                r => (Id: r.GetInt64(0), At: Utc(r.GetString(1)), Type: Str(r, 2), Name: Str(r, 3), Location: Str(r, 4), User: Str(r, 5))))
            {
                if (r.At is not { } at || r.Name == null) continue;
                if (r.User != null && selfIds.Contains(r.User)) continue;
                if (FindVisit(r.Location, at) is not { } v) continue;
                var key = (v.Id, r.User ?? "name:" + r.Name);
                if (r.Type == "OnPlayerJoined")
                {
                    if (!open.ContainsKey(key)) open[key] = new Encounter(StableId.Of("encounter", Source, r.Id), v.Id, r.User, r.Name, at, null);
                }
                else if (r.Type == "OnPlayerLeft" && open.Remove(key, out var e))
                    encounters.Add(e with { LeftAt = at });
            }
            foreach (var ((visitId, _), e) in open)
                encounters.Add(e with { LeftAt = visits.First(v => v.Id == visitId).LeftAt });
        }

        // ---- 動画
        if (tables.Contains("gamelog_video_play"))
            foreach (var r in Rows(c, "SELECT id, created_at, video_url, video_name, location, display_name, user_id FROM gamelog_video_play",
                r => (Id: r.GetInt64(0), At: Utc(r.GetString(1)), Url: Str(r, 2), Title: Str(r, 3), Location: Str(r, 4), Name: Str(r, 5), User: Str(r, 6))))
            {
                if (r.At is not { } at || r.Url == null) continue;
                if (covered.Any(cv => at >= cv.From && at <= cv.To)) continue;
                var data = new Dictionary<string, string?> { ["url"] = r.Url };
                if (r.Title != null) data["title"] = r.Title;
                events.Add(new ActivityEvent(StableId.Of("activity", Source, "video", r.Id), Source, FindVisit(r.Location, at)?.Id, at, "video", r.User, r.Name, data, account));
            }

        // ---- アカウントごとの表（通知・フレンドの記録）
        var friendEvents = 0;
        foreach (var prefix in prefixes)
        {
            var acc = AccountOf(prefix);
            if (tables.Contains(prefix + "_notifications"))
                foreach (var r in Rows(c, $"SELECT id, created_at, type, sender_user_id, sender_username, message, world_id, world_name FROM \"{prefix}_notifications\"",
                    r => (Id: Str(r, 0), At: Utc(r.GetString(1)), Type: Str(r, 2), Sender: Str(r, 3), Name: Str(r, 4), Message: Str(r, 5), World: Str(r, 6), WorldName: Str(r, 7))))
                {
                    if (r.Id == null || r.At is not { } at || r.Type == null) continue;
                    var data = new Dictionary<string, string?> { ["direction"] = "received", ["type"] = r.Type, ["notification_id"] = r.Id, ["message"] = r.Message };
                    if (r.World != null) data["location"] = r.World;
                    if (r.WorldName != null) data["world_name"] = r.WorldName;
                    events.Add(new ActivityEvent(StableId.Of("activity", "notification", "received", r.Id), Source, null, at, "notification", r.Sender, r.Name, data, acc));
                }
            if (tables.Contains(prefix + "_friend_log_history"))
                foreach (var r in Rows(c, $"SELECT id, created_at, type, user_id, display_name, previous_display_name, trust_level, previous_trust_level FROM \"{prefix}_friend_log_history\"",
                    r => (Id: r.GetInt64(0), At: Utc(r.GetString(1)), Type: Str(r, 2), User: Str(r, 3), Name: Str(r, 4), Prev: Str(r, 5), Trust: Str(r, 6), PrevTrust: Str(r, 7))))
                {
                    if (r.At is not { } at || r.Type == null || r.User == null) continue;
                    var data = new Dictionary<string, string?> { ["type"] = r.Type };
                    if (r.Prev != null) data["previous_name"] = r.Prev;
                    if (r.Trust != null) data["trust_level"] = r.Trust;
                    if (r.PrevTrust != null) data["previous_trust_level"] = r.PrevTrust;
                    events.Add(new ActivityEvent(StableId.Of("activity", Source, prefix, "friend", r.Id), Source, null, at, "friend", r.User, r.Name, data, acc));
                    friendEvents++;
                }
        }

        using var tx = db.Connection.BeginTransaction();
        ProjectionWriter.Replace(db, Source, new Projection(deviceId, null, visits, encounters, events), tx);
        ImportGuard.RememberOrigin(db, Source, origin, tx);
        var worlds = ImportWorlds(c, tables, tx);
        var favorites = ImportFavorites(c, tables, tx);
        var notes = ImportMemos(c, tables, tx);
        tx.Commit();
        return new Result(visits.Count, encounters.Count, events.Count, friendEvents, worlds, favorites, notes, skipped, accounts);
    }

    int ImportWorlds(SqliteConnection c, HashSet<string> tables, SqliteTransaction tx)
    {
        if (!tables.Contains("cache_world")) return 0;
        var n = 0;
        foreach (var r in Rows(c, "SELECT id, author_id, author_name, description, image_url, name, release_status, thumbnail_image_url, updated_at FROM cache_world",
            r => (Id: Str(r, 0), Author: Str(r, 1), AuthorName: Str(r, 2), Description: Str(r, 3), Image: Str(r, 4), Name: Str(r, 5), Status: Str(r, 6), Thumb: Str(r, 7), Updated: Str(r, 8))))
        {
            if (r.Id == null) continue;
            // 拡張機能で取った情報（より新しく詳しい）があれば上書きしない
            using var cmd = db.Command("""
                INSERT INTO worlds (id, name, author_id, author_name, description, image_url, thumbnail_url, release_status, categories, details_source, details_updated_at)
                VALUES ($id, $n, $a, $an, $d, $i, $t, $s, $cat, 'vrcx', $u)
                ON CONFLICT (id) DO UPDATE SET name = COALESCE(worlds.name, $n),
                    author_id = $a, author_name = $an, description = $d, image_url = $i, thumbnail_url = $t, release_status = $s, categories = $cat, details_source = 'vrcx', details_updated_at = $u
                WHERE worlds.details_source IS NULL OR worlds.details_source = 'vrcx'
                """, tx,
                ("$cat", System.Text.Json.JsonSerializer.Serialize(WorldClassifier.Classify(r.Description))),
                ("$id", r.Id), ("$n", r.Name), ("$a", r.Author), ("$an", r.AuthorName), ("$d", r.Description), ("$i", ExtensionStore.VrchatImage(r.Image)), ("$t", ExtensionStore.VrchatImage(r.Thumb)), ("$s", r.Status),
                ("$u", r.Updated is { } u && Utc(u) is { } ut ? Database.Time(ut) : null));
            n += cmd.ExecuteNonQuery();
        }
        return n;
    }

    int ImportFavorites(SqliteConnection c, HashSet<string> tables, SqliteTransaction tx)
    {
        if (!tables.Contains("favorite_world")) return 0;
        using (var del = db.Command("DELETE FROM world_favorites WHERE source = 'vrcx'", tx)) del.ExecuteNonQuery();
        var n = 0;
        foreach (var r in Rows(c, "SELECT world_id, group_name, created_at FROM favorite_world", r => (World: Str(r, 0), Group: Str(r, 1), At: Str(r, 2))))
        {
            if (r.World == null) continue;
            using var cmd = db.Command("INSERT OR IGNORE INTO world_favorites (world_id, list_name, source, added_at) VALUES ($w, $g, 'vrcx', $at)", tx,
                ("$w", r.World), ("$g", r.Group ?? ""), ("$at", r.At is { } a && Utc(a) is { } t ? Database.Time(t) : null));
            n += cmd.ExecuteNonQuery();
            using var w = db.Command("INSERT OR IGNORE INTO worlds (id) VALUES ($w)", tx, ("$w", r.World));
            w.ExecuteNonQuery();
        }
        return n;
    }

    int ImportMemos(SqliteConnection c, HashSet<string> tables, SqliteTransaction tx)
    {
        var n = 0;
        void Upsert(string type, string target, string body, string? at)
        {
            var time = at is { } a && Utc(a) is { } t ? Database.Time(t) : Database.Time(DateTime.UtcNow);
            using var cmd = db.Command("""
                INSERT INTO notes (id, target_type, target_id, body, device_id, created_at, updated_at, source) VALUES ($id, $t, $i, $b, NULL, $at, $at, 'vrcx')
                ON CONFLICT (id) DO UPDATE SET body = $b, updated_at = $at WHERE notes.source = 'vrcx'
                """, tx, ("$id", StableId.Of("note", Source, type, target)), ("$t", type), ("$i", target), ("$b", body), ("$at", time));
            n += cmd.ExecuteNonQuery();
        }
        if (tables.Contains("world_memos"))
            foreach (var r in Rows(c, "SELECT world_id, edited_at, memo FROM world_memos", r => (Id: Str(r, 0), At: Str(r, 1), Memo: Str(r, 2))))
                if (r.Id != null && !string.IsNullOrWhiteSpace(r.Memo)) Upsert("world", r.Id, r.Memo, r.At);
        if (tables.Contains("memos"))
            foreach (var r in Rows(c, "SELECT user_id, edited_at, memo FROM memos", r => (Id: Str(r, 0), At: Str(r, 1), Memo: Str(r, 2))))
                if (r.Id != null && !string.IsNullOrWhiteSpace(r.Memo)) Upsert("person", r.Id, r.Memo, r.At);
        return n;
    }

    /// <summary>VRCX の DB に出てくるユーザー ID（アカウントの表の頭と引き合わせるため）。</summary>
    static HashSet<string> AllUserIds(SqliteConnection c, HashSet<string> tables)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (tables.Contains("gamelog_join_leave"))
            foreach (var id in Strings(c, "SELECT DISTINCT user_id FROM gamelog_join_leave WHERE user_id LIKE 'usr_%'")) ids.Add(id);
        foreach (var t in tables.Where(t => t.EndsWith("_notifications", StringComparison.Ordinal)))
            foreach (var id in Strings(c, $"SELECT DISTINCT receiver_user_id FROM \"{t}\" WHERE receiver_user_id LIKE 'usr_%'")) ids.Add(id);
        return ids;
    }

    static string Normalize(string userId)
    {
        var p = userId.Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
        return char.IsDigit(p[0]) ? "_" + p : p;
    }

    List<(DateTime From, DateTime To)> LogCoveredRanges()
    {
        using var cmd = db.Command("SELECT started_at, ended_at FROM sessions WHERE device_id = $d AND started_at IS NOT NULL", null, ("$d", deviceId));
        using var r = cmd.ExecuteReader();
        var list = new List<(DateTime, DateTime)>();
        while (r.Read()) list.Add((Database.ParseTime(r.GetString(0)), r.IsDBNull(1) ? DateTime.UtcNow : Database.ParseTime(r.GetString(1))));
        return list;
    }

    /// <summary>VRCX の時刻（JavaScript の toJSON の形、UTC）。</summary>
    static DateTime? Utc(string? s) =>
        s != null && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) is { Length: > 0 } s ? s : null;

    static List<string> Strings(SqliteConnection c, string sql) => Rows(c, sql, r => r.GetString(0));

    static List<T> Rows<T>(SqliteConnection c, string sql, Func<SqliteDataReader, T> map)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }
}
