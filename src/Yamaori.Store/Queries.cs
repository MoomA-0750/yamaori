using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Yamaori.Store;

// 読み出しの形。API・WebUI・MCP が同じものを使う。時刻は UTC。

public sealed record VisitSummary(
    string Id, string WorldId, string? WorldName, string InstanceNumber, string Access, string Region,
    DateTime JoinedAt, DateTime? LeftAt, string? LeftReason, string? AccountId, string? DeviceId,
    int PeopleCount, int VideoCount, int ScreenshotCount, int NoteCount);

public sealed record EncounterView(string? UserId, string Name, DateTime JoinedAt, DateTime? LeftAt);

public sealed record EventView(string Id, DateTime At, string Kind, string? UserId, string? Name, IReadOnlyDictionary<string, string?> Data);

public sealed record VisitDetail(VisitSummary Visit, string Instance, string? OwnerId, string? GroupId,
    IReadOnlyList<EncounterView> People, IReadOnlyList<EventView> Events, IReadOnlyList<Note> Notes);

public sealed record DaySummary(string Date, int VisitCount, int Minutes, int WorldCount, int PeopleCount, int NoteCount);

public sealed record WorldSummary(string Id, string? Name, int VisitCount, int Minutes, DateTime? FirstVisitAt, DateTime? LastVisitAt, int NoteCount);

/// <summary>ワールドの詳しい情報（VRCX の取り込みや、拡張機能で入る）。Favorites はお気に入りの一覧の名前。</summary>
public sealed record WorldInfo(string? AuthorId, string? AuthorName, string? Description, string? ImageUrl, string? ThumbnailUrl,
    string? ReleaseStatus, int? Capacity, string? DetailsSource, DateTime? DetailsUpdatedAt, IReadOnlyList<string> Favorites,
    IReadOnlyList<string> Categories, IReadOnlyList<string> Tags, IReadOnlyList<string> Platforms, int? VisitsTotal, int? FavoritesTotal);

public sealed record WorldDetail(WorldSummary World, WorldInfo Info, IReadOnlyList<VisitSummary> Visits, IReadOnlyList<Note> Notes);

/// <summary>Friend: friend（フレンド）・former（前はフレンドだった）・requested（フレンド申請のやり取りがあった）・null（分からない）。</summary>
public sealed record PersonSummary(string UserId, string? Name, DateTime? FirstMetAt, DateTime? LastMetAt, int VisitCount, int NoteCount, string? Friend = null);

public sealed record NameSpan(string Name, DateTime? FirstSeenAt, DateTime? LastSeenAt);

public sealed record PersonDetail(PersonSummary Person, IReadOnlyList<NameSpan> Names, IReadOnlyList<VisitSummary> Visits, IReadOnlyList<Note> Notes,
    IReadOnlyList<EventView> Friendship);

public sealed record AccountView(string Id, string? Name, DateTime? FirstSeenAt, DateTime? LastSeenAt, int VisitCount, IReadOnlyList<NameSpan> Names);

public sealed record DeviceView(string Id, string Name, bool IsSelf, DateTime CreatedAt, int VisitCount);

public sealed record Note(string Id, string TargetType, string TargetId, string Body, string? DeviceId, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record Page<T>(IReadOnlyList<T> Items, int Total);

/// <summary>記録の読み出し。日の区切りは timeZone（既定はこの PC のタイムゾーン）。</summary>
public sealed class Queries(Database db, TimeZoneInfo? timeZone = null)
{
    readonly TimeZoneInfo _tz = timeZone ?? TimeZoneInfo.Local;

    const string VisitColumns = """
        v.id, v.world_id, v.world_name, v.instance_number, v.access, v.region, v.joined_at, v.left_at, v.left_reason, v.account_id, v.device_id,
        (SELECT COUNT(DISTINCT COALESCE(e.user_id, e.name)) FROM encounters e WHERE e.visit_id = v.id),
        (SELECT COUNT(*) FROM events x WHERE x.visit_id = v.id AND x.kind = 'video'),
        MAX((SELECT COUNT(*) FROM events x WHERE x.visit_id = v.id AND x.kind = 'screenshot'),
            (SELECT COUNT(*) FROM photos ph WHERE ph.taken_at >= v.joined_at AND ph.taken_at < COALESCE(v.left_at, '9999') AND NOT EXISTS (SELECT 1 FROM photo_trash t WHERE t.photo_id = ph.id))),
        (SELECT COUNT(*) FROM notes n WHERE n.target_type = 'visit' AND n.target_id = v.id)
        """;

    static VisitSummary ReadVisit(SqliteDataReader r, int o = 0) => new(
        r.GetString(o), r.GetString(o + 1), Str(r, o + 2), r.GetString(o + 3), r.GetString(o + 4), r.GetString(o + 5),
        Database.ParseTime(r.GetString(o + 6)), Time(r, o + 7), Str(r, o + 8), Str(r, o + 9), Str(r, o + 10),
        r.GetInt32(o + 11), r.GetInt32(o + 12), r.GetInt32(o + 13), r.GetInt32(o + 14));

    /// <summary>訪問の一覧（新しい順）。from・to はその時刻の間に入った訪問。</summary>
    public Page<VisitSummary> Visits(DateTime? from = null, DateTime? to = null, string? worldId = null, string? userId = null,
        string? accountId = null, string? deviceId = null, int limit = 50, int offset = 0)
    {
        var where = new List<string>();
        var args = new List<(string, object?)>();
        if (from is { } f) { where.Add("v.joined_at >= $from"); args.Add(("$from", Database.Time(f))); }
        if (to is { } t) { where.Add("v.joined_at < $to"); args.Add(("$to", Database.Time(t))); }
        if (worldId != null) { where.Add("v.world_id = $world"); args.Add(("$world", worldId)); }
        if (userId != null && userId.StartsWith(NamePrefix, StringComparison.Ordinal))
        {
            where.Add("v.id IN (SELECT visit_id FROM encounters WHERE user_id IS NULL AND name = $user)");
            args.Add(("$user", userId[NamePrefix.Length..]));
        }
        else if (userId != null) { where.Add("v.id IN (SELECT visit_id FROM encounters WHERE user_id = $user)"); args.Add(("$user", userId)); }
        if (accountId != null) { where.Add("v.account_id = $account"); args.Add(("$account", accountId)); }
        if (deviceId != null) { where.Add("v.device_id = $device"); args.Add(("$device", deviceId)); }
        var w = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        var total = Convert.ToInt32(db.Scalar($"SELECT COUNT(*) FROM visits v {w}", [.. args]), CultureInfo.InvariantCulture);
        args.Add(("$limit", Math.Clamp(limit, 1, 500)));
        args.Add(("$offset", Math.Max(0, offset)));
        var items = List($"SELECT {VisitColumns} FROM visits v {w} ORDER BY v.joined_at DESC LIMIT $limit OFFSET $offset", r => ReadVisit(r), [.. args]);
        return new Page<VisitSummary>(items, total);
    }

    public VisitDetail? Visit(string id)
    {
        var head = List($"SELECT {VisitColumns}, v.instance, v.owner_id, v.group_id FROM visits v WHERE v.id = $id",
            r => (Visit: ReadVisit(r), Instance: r.GetString(15), Owner: Str(r, 16), Group: Str(r, 17)), ("$id", id));
        if (head.Count == 0) return null;
        var h = head[0];
        var people = List("SELECT user_id, name, joined_at, left_at FROM encounters WHERE visit_id = $id ORDER BY joined_at",
            r => new EncounterView(Str(r, 0), r.GetString(1), Database.ParseTime(r.GetString(2)), Time(r, 3)), ("$id", id));
        var events = List("SELECT id, at, kind, user_id, name, data FROM events WHERE visit_id = $id ORDER BY at",
            ReadEvent, ("$id", id));
        return new VisitDetail(h.Visit, h.Instance, h.Owner, h.Group, people, events, Notes("visit", id));
    }

    static EventView ReadEvent(SqliteDataReader r) => new(r.GetString(0), Database.ParseTime(r.GetString(1)), r.GetString(2), Str(r, 3), Str(r, 4),
        JsonSerializer.Deserialize<Dictionary<string, string?>>(r.GetString(5)) ?? []);

    /// <summary>出来事の一覧（新しい順）。kind で絞れる（video・avatar・notification・screenshot）。</summary>
    public Page<EventView> Events(string? kind = null, DateTime? from = null, DateTime? to = null, int limit = 100, int offset = 0)
    {
        var where = new List<string>();
        var args = new List<(string, object?)>();
        if (kind != null) { where.Add("kind = $kind"); args.Add(("$kind", kind)); }
        if (from is { } f) { where.Add("at >= $from"); args.Add(("$from", Database.Time(f))); }
        if (to is { } t) { where.Add("at < $to"); args.Add(("$to", Database.Time(t))); }
        var w = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        var total = Convert.ToInt32(db.Scalar($"SELECT COUNT(*) FROM events {w}", [.. args]), CultureInfo.InvariantCulture);
        args.Add(("$limit", Math.Clamp(limit, 1, 500)));
        args.Add(("$offset", Math.Max(0, offset)));
        return new Page<EventView>(List($"SELECT id, at, kind, user_id, name, data FROM events {w} ORDER BY at DESC LIMIT $limit OFFSET $offset", ReadEvent, [.. args]), total);
    }

    /// <summary>日ごとのまとめ（新しい順）。日は timeZone で区切り、入った時刻の日に数える。</summary>
    public IReadOnlyList<DaySummary> Days(DateOnly? from = null, DateOnly? to = null)
    {
        var visits = List("SELECT v.id, v.world_id, v.joined_at, v.left_at FROM visits v",
            r => (Id: r.GetString(0), World: r.GetString(1), Joined: Database.ParseTime(r.GetString(2)), Left: Time(r, 3)));
        var people = List("SELECT visit_id, COALESCE(user_id, 'name:' || name) FROM encounters", r => (Visit: r.GetString(0), Person: r.GetString(1)))
            .ToLookup(x => x.Visit, x => x.Person);
        var dayNotes = List("SELECT target_id, COUNT(*) FROM notes WHERE target_type = 'day' GROUP BY target_id", r => (Day: r.GetString(0), Count: r.GetInt32(1)))
            .ToDictionary(x => x.Day, x => x.Count);
        var result = new List<DaySummary>();
        foreach (var g in visits.GroupBy(v => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(v.Joined, _tz))))
        {
            if (from is { } f && g.Key < f) continue;
            if (to is { } t && g.Key > t) continue;
            // 足してから丸める（訪問ごとに丸めると、その日のページの合計とずれる）
            // 今いる訪問（出た時刻がまだない）は、今までの時間を数える（ログが閉じれば出た時刻が入るので、出た時刻のない訪問は今いるものだけ）
            var now = DateTime.UtcNow;
            var minutes = (int)Math.Round(g.Sum(v => ((v.Left ?? now) - v.Joined).TotalMinutes));
            var key = g.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            result.Add(new DaySummary(key, g.Count(), minutes, g.Select(v => v.World).Distinct().Count(),
                g.SelectMany(v => people[v.Id]).Distinct().Count(), dayNotes.GetValueOrDefault(key)));
        }
        foreach (var (day, count) in dayNotes)
            if (result.All(d => d.Date != day) && DateOnly.TryParse(day, CultureInfo.InvariantCulture, out var dd) && (from == null || dd >= from) && (to == null || dd <= to))
                result.Add(new DaySummary(day, 0, 0, 0, 0, count));
        return result.OrderByDescending(d => d.Date, StringComparer.Ordinal).ToList();
    }

    /// <summary>その日（timeZone）の訪問（古い順）。</summary>
    public IReadOnlyList<VisitSummary> Day(DateOnly date)
    {
        var start = TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue), _tz);
        var end = TimeZoneInfo.ConvertTimeToUtc(date.AddDays(1).ToDateTime(TimeOnly.MinValue), _tz);
        return Visits(start, end, limit: 500).Items.Reverse().ToList();
    }

    const string WorldColumns = """
        w.id, w.name,
        (SELECT COUNT(*) FROM visits v WHERE v.world_id = w.id),
        (SELECT COALESCE(SUM((julianday(COALESCE(v.left_at, 'now')) - julianday(v.joined_at)) * 1440), 0) FROM visits v WHERE v.world_id = w.id),
        (SELECT MIN(joined_at) FROM visits v WHERE v.world_id = w.id),
        (SELECT MAX(joined_at) FROM visits v WHERE v.world_id = w.id),
        (SELECT COUNT(*) FROM notes n WHERE n.target_type = 'world' AND n.target_id = w.id)
        """;

    static WorldSummary ReadWorld(SqliteDataReader r) => new(r.GetString(0), Str(r, 1), r.GetInt32(2), (int)Math.Round(r.GetDouble(3)), Time(r, 4), Time(r, 5), r.GetInt32(6));

    /// <summary>ワールドの一覧。sort は last（最後に行った順）・visits（回数）・minutes（いた時間）・name。</summary>
    public Page<WorldSummary> Worlds(string? q = null, string sort = "last", int limit = 50, int offset = 0, string? list = null)
    {
        var where = new List<string>();
        if (q != null) where.Add("(w.name LIKE $q OR w.id = $exact)");
        // list: お気に入り・保存の一覧の名前（"*" ならどれかに入っているもの）
        if (list == "*") where.Add("w.id IN (SELECT world_id FROM world_favorites)");
        else if (list != null) where.Add("w.id IN (SELECT world_id FROM world_favorites WHERE list_name = $list)");
        var w = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        var args = new List<(string, object?)> { ("$q", $"%{q}%"), ("$exact", q), ("$list", list) };
        var total = Convert.ToInt32(db.Scalar($"SELECT COUNT(*) FROM worlds w {w}", [.. args]), CultureInfo.InvariantCulture);
        var order = sort switch
        {
            "visits" => "3 DESC", "minutes" => "4 DESC", "name" => "2 COLLATE NOCASE",
            "saved" => "(SELECT MAX(added_at) FROM world_favorites f WHERE f.world_id = w.id) DESC",
            _ => "6 DESC",
        };
        args.Add(("$limit", Math.Clamp(limit, 1, 500)));
        args.Add(("$offset", Math.Max(0, offset)));
        return new Page<WorldSummary>(List($"SELECT {WorldColumns} FROM worlds w {w} ORDER BY {order} LIMIT $limit OFFSET $offset", ReadWorld, [.. args]), total);
    }

    public WorldDetail? World(string id)
    {
        var w = List($"SELECT {WorldColumns} FROM worlds w WHERE w.id = $id", ReadWorld, ("$id", id));
        if (w.Count == 0) return null;
        // 訪問は新しい方から 100 件（続きは Visits(worldId) で読む。全部の数は World.VisitCount）
        var info = List("""
            SELECT author_id, author_name, description, image_url, thumbnail_url, release_status, capacity, details_source, details_updated_at,
                   categories, tags, platforms, visits_total, favorites_total FROM worlds WHERE id = $id
            """,
            r => new WorldInfo(Str(r, 0), Str(r, 1), Str(r, 2), Str(r, 3), Str(r, 4), Str(r, 5), r.IsDBNull(6) ? null : r.GetInt32(6), Str(r, 7), Time(r, 8),
                List("SELECT DISTINCT list_name FROM world_favorites WHERE world_id = $id ORDER BY list_name", x => x.GetString(0), ("$id", id)),
                JsonList(Str(r, 9)), JsonList(Str(r, 10)), JsonList(Str(r, 11)), r.IsDBNull(12) ? null : r.GetInt32(12), r.IsDBNull(13) ? null : r.GetInt32(13)), ("$id", id))[0];
        return new WorldDetail(w[0], info, Visits(worldId: id, limit: 100).Items, Notes("world", id));
    }

    /// <summary>名前だけの人（ID が分からない人。VAT の取り込みなど）の ID の頭。「name:名前」の形にする。</summary>
    public const string NamePrefix = "name:";

    // ID の分かる人（people）と、名前だけの人を合わせた一覧
    /// <summary>
    /// フレンドかどうかの手がかり（新しいものが勝つ。同じ時刻ならフレンドの方）:
    /// フレンドの一覧の写し（拡張機能・VRCX の今の一覧）、フレンドの記録（VRCX の増減・拡張機能で見た増減）、
    /// ログの通知（フレンド申請を承諾した）。
    /// </summary>
    const string FriendState = """
        friend_ev AS (
            SELECT s.account_id AS acc, s.user_id, m.taken_at AS at, 1 AS f FROM friend_snapshots s JOIN friend_snapshot_meta m ON m.account_id = s.account_id
            UNION ALL
            SELECT COALESCE(account_id, ''), user_id, at, CASE json_extract(data, '$.type') WHEN 'Friend' THEN 1 ELSE 0 END FROM events
                WHERE kind = 'friend' AND json_extract(data, '$.type') IN ('Friend', 'Unfriend') AND user_id IS NOT NULL
            UNION ALL
            SELECT COALESCE(account_id, ''), user_id, at, 1 FROM events
                WHERE kind = 'notification' AND json_extract(data, '$.type') = 'friendRequest' AND json_extract(data, '$.direction') = 'accepted' AND user_id IS NOT NULL
        ),
        -- 自分のアカウントごとに今の状態を決め、どれかのアカウントで今フレンドならフレンド
        friend_state AS (
            SELECT user_id, MAX(f) AS f FROM (
                SELECT user_id, f, ROW_NUMBER() OVER (PARTITION BY acc, user_id ORDER BY at DESC, f DESC) AS rn FROM friend_ev)
            WHERE rn = 1 GROUP BY user_id
        ),
        friend_requested AS (
            SELECT DISTINCT user_id FROM events WHERE kind = 'notification' AND json_extract(data, '$.type') = 'friendRequest' AND user_id IS NOT NULL
        )
        """;

    const string AllPeople = "WITH " + FriendState + ", " + """
        all_people AS (
            SELECT user_id, name, first_met_at, last_met_at, visit_count FROM people
            UNION ALL
            SELECT 'name:' || name, name, MIN(joined_at), MAX(COALESCE(left_at, joined_at)), COUNT(DISTINCT visit_id)
            FROM encounters WHERE user_id IS NULL GROUP BY name
        )
        """;

    const string PersonColumns = """
        p.user_id, p.name, p.first_met_at, p.last_met_at, p.visit_count,
        (SELECT COUNT(*) FROM notes n WHERE n.target_type = 'person' AND n.target_id = p.user_id),
        COALESCE((SELECT CASE fs.f WHEN 1 THEN 'friend' ELSE 'former' END FROM friend_state fs WHERE fs.user_id = p.user_id),
                 (SELECT 'requested' FROM friend_requested fr WHERE fr.user_id = p.user_id))
        """;

    static PersonSummary ReadPerson(SqliteDataReader r) => new(r.GetString(0), Str(r, 1), Time(r, 2), Time(r, 3), r.GetInt32(4), r.GetInt32(5), Str(r, 6));

    /// <summary>
    /// 会った人の一覧。sort は last（最後に会った順）・visits（会った回数）・name。名前の検索は前の名前も見る。
    /// kind は all（既定）・id（ID の分かる人だけ）・name（名前だけの人だけ）・friend（今のフレンドだけ）・not_friend（フレンドでない人だけ）。名前だけの人の ID は「name:名前」。
    /// </summary>
    public Page<PersonSummary> People(string? q = null, string sort = "last", int limit = 50, int offset = 0, string kind = "all")
    {
        var where = new List<string>();
        if (q != null) where.Add("(p.user_id IN (SELECT user_id FROM person_names WHERE name LIKE $q) OR p.user_id = $exact OR (p.user_id LIKE 'name:%' AND p.name LIKE $q))");
        if (kind == "id") where.Add("p.user_id NOT LIKE 'name:%'");
        if (kind == "name") where.Add("p.user_id LIKE 'name:%'");
        if (kind == "friend") where.Add("p.user_id IN (SELECT user_id FROM friend_state WHERE f = 1)");
        if (kind == "not_friend") where.Add("p.user_id NOT IN (SELECT user_id FROM friend_state WHERE f = 1)");
        var w = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        var args = new List<(string, object?)> { ("$q", $"%{q}%"), ("$exact", q) };
        var total = Convert.ToInt32(db.Scalar($"{AllPeople} SELECT COUNT(*) FROM all_people p {w}", [.. args]), CultureInfo.InvariantCulture);
        var order = sort switch { "visits" => "5 DESC", "name" => "2 COLLATE NOCASE", _ => "4 DESC" };
        args.Add(("$limit", Math.Clamp(limit, 1, 500)));
        args.Add(("$offset", Math.Max(0, offset)));
        return new Page<PersonSummary>(List($"{AllPeople} SELECT {PersonColumns} FROM all_people p {w} ORDER BY {order} LIMIT $limit OFFSET $offset", ReadPerson, [.. args]), total);
    }

    public PersonDetail? Person(string userId)
    {
        var p = List($"{AllPeople} SELECT {PersonColumns} FROM all_people p WHERE p.user_id = $id", ReadPerson, ("$id", userId));
        if (p.Count == 0) return null;
        var names = userId.StartsWith(NamePrefix, StringComparison.Ordinal)
            ? [new NameSpan(userId[NamePrefix.Length..], p[0].FirstMetAt, p[0].LastMetAt)]
            : List("SELECT name, first_seen_at, last_seen_at FROM person_names WHERE user_id = $id ORDER BY first_seen_at",
                r => new NameSpan(r.GetString(0), Time(r, 1), Time(r, 2)), ("$id", userId));
        // 訪問は新しい方から 100 件（続きは Visits(userId) で読む。全部の数は Person.VisitCount）
        // フレンドの記録（VRCX の取り込み）と、その人とのフレンド申請の通知
        var friendship = List("""
            SELECT id, at, kind, user_id, name, data FROM events
            WHERE user_id = $id AND (kind = 'friend' OR (kind = 'notification' AND json_extract(data, '$.type') = 'friendRequest'))
            ORDER BY at
            """, ReadEvent, ("$id", userId));
        // フレンドの記録（VRCX）で名前を変えたと分かっていれば、それも名前の移り変わりに入れ、新しい方を見出しの名前にする
        var renamed = friendship.Where(e => e.Kind == "friend" && e.Data.GetValueOrDefault("type") == "DisplayName" && e.Name != null).ToList();
        var person = p[0];
        if (renamed.Count > 0)
        {
            var spans = names.ToList();
            foreach (var e in renamed)
                if (spans.All(n => n.Name != e.Name)) spans.Add(new NameSpan(e.Name!, e.At, e.At));
            names = spans.OrderBy(n => n.FirstSeenAt).ToList();
            var latest = renamed[^1];
            if (person.LastMetAt == null || latest.At > person.LastMetAt) person = person with { Name = latest.Name };
        }
        return new PersonDetail(person, names, Visits(userId: userId, limit: 100).Items, Notes("person", userId), friendship);
    }

    public IReadOnlyList<AccountView> Accounts()
    {
        var names = List("SELECT id, name, first_seen_at, last_seen_at FROM account_names ORDER BY first_seen_at",
            r => (Id: r.GetString(0), Span: new NameSpan(r.GetString(1), Time(r, 2), Time(r, 3)))).ToLookup(x => x.Id, x => x.Span);
        // 起動の記録（ログ）のあるアカウントと、訪問だけがあるアカウント（VAT の取り込みなど。名前は取り込みのときに見分けたもの）
        return List("""
            SELECT a.id, a.name, a.first_seen_at, a.last_seen_at, (SELECT COUNT(*) FROM visits v WHERE v.account_id = a.id) FROM accounts a
            UNION ALL
            SELECT v.account_id, (SELECT value FROM settings WHERE key = 'account_name:' || v.account_id), MIN(v.joined_at), MAX(COALESCE(v.left_at, v.joined_at)), COUNT(*)
            FROM visits v WHERE v.account_id IS NOT NULL AND v.account_id NOT IN (SELECT id FROM accounts) GROUP BY v.account_id
            ORDER BY 4 DESC
            """,
            r => new AccountView(r.GetString(0), Str(r, 1), Time(r, 2), Time(r, 3), r.GetInt32(4), names[r.GetString(0)].ToList()));
    }

    public IReadOnlyList<DeviceView> Devices() =>
        List("SELECT d.id, d.name, d.is_self, d.created_at, (SELECT COUNT(*) FROM visits v WHERE v.device_id = d.id) FROM devices d ORDER BY d.is_self DESC, d.name",
            r => new DeviceView(r.GetString(0), r.GetString(1), r.GetInt64(2) != 0, Database.ParseTime(r.GetString(3)), r.GetInt32(4)));

    // ---- メモ

    public IReadOnlyList<Note> Notes(string? targetType = null, string? targetId = null, int limit = 500)
    {
        var where = new List<string>();
        var args = new List<(string, object?)>();
        if (targetType != null) { where.Add("target_type = $t"); args.Add(("$t", targetType)); }
        if (targetId != null) { where.Add("target_id = $i"); args.Add(("$i", targetId)); }
        args.Add(("$limit", Math.Clamp(limit, 1, 5000)));
        var w = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        return List($"SELECT id, target_type, target_id, body, device_id, created_at, updated_at FROM notes {w} ORDER BY created_at LIMIT $limit", ReadNote, [.. args]);
    }

    public Note? Note(string id) =>
        List("SELECT id, target_type, target_id, body, device_id, created_at, updated_at FROM notes WHERE id = $id", ReadNote, ("$id", id)).FirstOrDefault();

    static Note ReadNote(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), Str(r, 4),
        Database.ParseTime(r.GetString(5)), Database.ParseTime(r.GetString(6)));

    public static readonly string[] TargetTypes = ["visit", "world", "person", "day"];

    public Note AddNote(string targetType, string targetId, string body, string? deviceId)
    {
        var now = DateTime.UtcNow;
        var id = "n_" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(10));
        using var cmd = db.Command("INSERT INTO notes (id, target_type, target_id, body, device_id, created_at, updated_at) VALUES ($id, $t, $i, $b, $d, $now, $now)", null,
            ("$id", id), ("$t", targetType), ("$i", targetId), ("$b", body), ("$d", deviceId), ("$now", Database.Time(now)));
        cmd.ExecuteNonQuery();
        return Note(id)!;
    }

    public Note? UpdateNote(string id, string body)
    {
        // 取り込んだメモ（VRCX）を直したら、本人のメモにする（次の取り込みで上書きしない）
        using var cmd = db.Command("UPDATE notes SET body = $b, updated_at = $now, source = NULL WHERE id = $id", null, ("$id", id), ("$b", body), ("$now", Database.Time(DateTime.UtcNow)));
        return cmd.ExecuteNonQuery() == 0 ? null : Note(id);
    }

    /// <summary>メモを消す。この端末で書いたメモ（取り込みでないもの）は、消した印を残す（子なら親へ送って、親でも消す）。</summary>
    public bool DeleteNote(string id)
    {
        using var tx = db.Connection.BeginTransaction();
        using (var mark = db.Command("""
            INSERT INTO note_deletions (id, device_id, deleted_at) SELECT id, device_id, $now FROM notes WHERE id = $id AND source IS NULL
            ON CONFLICT (id) DO UPDATE SET deleted_at = excluded.deleted_at
            """, tx, ("$id", id), ("$now", Database.Time(DateTime.UtcNow))))
            mark.ExecuteNonQuery();
        using var cmd = db.Command("DELETE FROM notes WHERE id = $id", tx, ("$id", id));
        var n = cmd.ExecuteNonQuery();
        tx.Commit();
        return n > 0;
    }

    /// <summary>お気に入り・保存の一覧の名前と数。</summary>
    public IReadOnlyList<(string Name, int Count)> FavoriteLists() =>
        List("SELECT list_name, COUNT(DISTINCT world_id) FROM world_favorites GROUP BY list_name ORDER BY list_name", r => (r.GetString(0), r.GetInt32(1)));

    /// <summary>
    /// ワールドを保存の一覧に入れる（スマホの共有・ショートカット・拡張機能から）。text から wrld_… を探す。入れたワールドの ID、見つからなければ null。
    /// </summary>
    /// <summary>Yamaori で保存した一覧の鍵（画面では「保存」と出す）。</summary>
    public const string SavedList = "saved";

    public string? SaveWorld(string text, string? name = null, string list = SavedList)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text ?? "", @"wrld_[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        if (!m.Success) return null;
        var id = m.Value.ToLowerInvariant();
        using (var cmd = db.Command("INSERT INTO worlds (id, name) VALUES ($id, $n) ON CONFLICT (id) DO UPDATE SET name = COALESCE(worlds.name, $n)", null,
            ("$id", id), ("$n", string.IsNullOrWhiteSpace(name) ? null : name.Trim())))
            cmd.ExecuteNonQuery();
        using (var cmd = db.Command("INSERT OR IGNORE INTO world_favorites (world_id, list_name, source, added_at) VALUES ($id, $l, 'yamaori', $now)", null,
            ("$id", id), ("$l", list), ("$now", Database.Time(DateTime.UtcNow))))
            cmd.ExecuteNonQuery();
        return id;
    }

    /// <summary>メモの付け先があるか（日は日付の形か）。</summary>
    public bool TargetExists(string targetType, string targetId) => targetType switch
    {
        "visit" => db.Scalar("SELECT 1 FROM visits WHERE id = $i", ("$i", targetId)) != null,
        "world" => db.Scalar("SELECT 1 FROM worlds WHERE id = $i", ("$i", targetId)) != null || targetId.StartsWith("wrld_", StringComparison.Ordinal),
        "person" => targetId.StartsWith(NamePrefix, StringComparison.Ordinal)
            ? db.Scalar("SELECT 1 FROM encounters WHERE user_id IS NULL AND name = $i LIMIT 1", ("$i", targetId[NamePrefix.Length..])) != null
            : db.Scalar("SELECT 1 FROM encounters WHERE user_id = $i LIMIT 1", ("$i", targetId)) != null || targetId.StartsWith("usr_", StringComparison.Ordinal),
        "day" => DateOnly.TryParseExact(targetId, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        _ => false,
    };

    // ---- 下まわり

    List<T> List<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] args)
    {
        using var cmd = db.Command(sql, null, args);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    static IReadOnlyList<string> JsonList(string? json) => json == null ? [] : JsonSerializer.Deserialize<List<string>>(json) ?? [];
    static DateTime? Time(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Database.ParseTime(r.GetString(i));
}
