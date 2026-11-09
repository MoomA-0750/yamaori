using System.Globalization;
using Microsoft.Data.Sqlite;
using Yamaori.Core;
using Yamaori.Core.Log;

namespace Yamaori.Store;

/// <summary>
/// VRChatActivityTools（VAT）の DB（VRChatActivityLog.db、表 ActivityLogs）を読むだけで取り込む。
/// <list type="bullet">
/// <item>VAT には出た時刻がないので推定する（left_reason = estimated）。次に入った時刻が、その訪問で最後に記録された出来事（入った・会った・動画）から 3 時間以内ならそこで、そうでなければ最後の出来事の時刻で閉じる。通知は起動のときにまとめて届くので、訪問を延ばさない</item>
/// <item>会った人は名前しかないので、DB が知っている名前と ID の対応（ログ・VAT の通知）から ID を引く。その時期に使われていた名前を優先する。引けなければ名前だけで入れる</item>
/// <item>同じ端末のログの記録がある期間の行は飛ばす（ログを正とする）</item>
/// <item>ID は VAT の行の番号から決めるので、何度入れ直しても二重にならない。通知の ID はログと同じ決め方なので、同じ通知はログとも重ならない</item>
/// </list>
/// </summary>
public sealed class VatImport(Database db, string deviceId, TimeZoneInfo? timeZone = null)
{
    public const string Source = "vat";
    static readonly TimeSpan MaxGap = TimeSpan.FromHours(3);
    readonly TimeZoneInfo _tz = timeZone ?? TimeZoneInfo.Local;

    public sealed record Result(int Visits, int Encounters, int Resolved, int Events, int Skipped, IReadOnlyList<string> SelfNames);

    sealed record Row(long Id, int Type, DateTime At, string? NotificationId, string? UserId, string? UserName, string? WorldId, string? WorldName, string? Message, string? Url);

    /// <param name="accountId">どのアカウントの記録か（VAT は持たない）。null なら、DB のアカウントが 1 つだけのときそれを使う。</param>
    /// <param name="replace">前に取り込んだものと別の DB でも、入れ替えて取り込む。</param>
    public Result Import(string vatPath, string? accountId = null, bool replace = false)
    {
        var rows = Read(vatPath);
        // DB の見分け: いちばん古い行（VAT は書き足していくので、続きの DB でも同じ）
        var origin = rows.Count == 0 ? "empty" : rows.MinBy(r => r.Id) is { } first ? $"{first.Id}@{Database.Time(first.At)}" : "empty";
        ImportGuard.CheckOrigin(db, Source, origin, replace);
        accountId ??= ImportGuard.SingleAccount(db);
        var covered = LogCoveredRanges();
        var vrcxVisits = ImportGuard.VisitsOf(db, VrcxImport.Source);
        var inDuplicate = false; // VRCX から入れた訪問と同じ訪問の中（その間の会った人・動画も入れない）
        var selfNames = SelfNames(rows);
        var names = NameIndex(rows, selfNames);

        var visits = new List<Visit>();
        var encounters = new List<Encounter>();
        var events = new List<ActivityEvent>();
        var skipped = 0;
        var resolved = 0;

        Visit? visit = null;
        DateTime lastActivity = default;
        var met = new Dictionary<string, Encounter>();

        void Close(DateTime? nextJoin)
        {
            if (visit == null) return;
            var left = ImportGuard.ClipToCovered(visit.JoinedAt, nextJoin is { } n && n - lastActivity <= MaxGap ? n : lastActivity, covered);
            visits.Add(visit with { LeftAt = left, LeftReason = "estimated" });
            foreach (var e in met.Values) encounters.Add(e with { LeftAt = left });
            met.Clear();
            visit = null;
        }

        foreach (var r in rows)
        {
            // 前の出来事から 3 時間以上あいたら、前の訪問はそこで終わっている（VRChat を閉じていた）
            if (visit != null && r.At - lastActivity > MaxGap) Close(null);
            if (covered.Any(c => r.At >= c.From && r.At <= c.To))
            {
                skipped++;
                if (r.Type == 0) Close(r.At);
                continue;
            }
            if (inDuplicate && r.Type != 0) { skipped++; continue; }
            switch (r.Type)
            {
                case 0 when r.WorldId != null:
                {
                    Close(r.At);
                    var colon = r.WorldId.IndexOf(':', StringComparison.Ordinal);
                    var world = colon > 0 ? r.WorldId[..colon] : r.WorldId;
                    inDuplicate = ImportGuard.SameVisit(vrcxVisits, world, r.At);
                    if (inDuplicate) { skipped++; break; }
                    var instance = colon > 0 ? r.WorldId[(colon + 1)..] : "";
                    var info = InstanceInfo.Parse(instance);
                    visit = new Visit(StableId.Of("visit", Source, r.Id), Source, world, r.WorldName, instance, info.Number, info.Access,
                        info.OwnerId, info.GroupId, info.Region, r.At, null, null, accountId);
                    lastActivity = r.At;
                    break;
                }
                // VAT は自分も「会った人」に入れるので、本人の名前は外す
                case 1 when visit != null && !string.IsNullOrEmpty(r.UserName) && !selfNames.Contains(r.UserName):
                {
                    var id = Resolve(names, r.UserName, r.At);
                    if (id != null) resolved++;
                    var key = id ?? "name:" + r.UserName;
                    if (!met.ContainsKey(key))
                        met[key] = new Encounter(StableId.Of("encounter", Source, r.Id), visit.Id, id, r.UserName, r.At, null);
                    lastActivity = r.At;
                    break;
                }
                case 13 when !string.IsNullOrEmpty(r.Url):
                    events.Add(new ActivityEvent(StableId.Of("activity", Source, r.Id), Source, visit?.Id, r.At, "video", null, null,
                        new Dictionary<string, string?> { ["url"] = r.Url }, accountId));
                    if (visit != null) lastActivity = r.At;
                    break;
                default:
                    if (Notification(r.Type) is { } n && r.NotificationId != null)
                    {
                        var data = new Dictionary<string, string?>
                        {
                            ["direction"] = n.Direction,
                            ["type"] = n.Type,
                            ["notification_id"] = r.NotificationId,
                            ["message"] = r.Message,
                        };
                        if (r.WorldId != null) data["location"] = r.WorldId;
                        if (r.WorldName != null) data["world_name"] = r.WorldName;
                        // 通知の ID はログから読んだものと同じ決め方（同じ通知を二重にしない）
                        events.Add(new ActivityEvent(StableId.Of("activity", "notification", n.Direction, r.NotificationId), Source, visit?.Id, r.At,
                            "notification", r.UserId, r.UserName, data, accountId));
                        // 通知は起動したときにまとめて届くことがあるので、訪問を延ばさない
                    }
                    break;
            }
        }
        Close(null);

        using var tx = db.Connection.BeginTransaction();
        ProjectionWriter.Replace(db, Source, new Projection(deviceId, null, visits, encounters, events), tx);
        ImportGuard.RememberOrigin(db, Source, origin, tx);
        // 起動の記録のないアカウントにも名前を付ける（本人の名前のうち、最後に使っていたもの）
        if (accountId != null && LastSelfName(rows, selfNames) is { } selfName)
        {
            using var cmd = db.Command("INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = $v", tx,
                ("$k", "account_name:" + accountId), ("$v", selfName));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return new Result(visits.Count, encounters.Count, resolved, events.Count, skipped, selfNames.Order(StringComparer.Ordinal).ToList());
    }

    static string? LastSelfName(List<Row> rows, HashSet<string> selfNames) =>
        rows.LastOrDefault(r => r.Type == 1 && r.UserName != null && selfNames.Contains(r.UserName))?.UserName;

    /// <summary>
    /// 本人の名前を見分ける。VAT は自分も「会った人」に入れるので、その名前が使われていた期間の訪問の半分以上に出てくる名前（20 回以上）を本人とみなす。
    /// 2021〜2026 の本物の記録では、本人の名前は 70〜100%、いちばんよく会う人でも 17% だった。
    /// </summary>
    static HashSet<string> SelfNames(List<Row> rows)
    {
        var visitIndex = -1;
        var seen = new Dictionary<string, (int Count, int First, int Last)>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (r.Type == 0) visitIndex++;
            else if (r.Type == 1 && visitIndex >= 0 && !string.IsNullOrEmpty(r.UserName))
            {
                if (!seen.TryGetValue(r.UserName, out var s)) seen[r.UserName] = (1, visitIndex, visitIndex);
                else if (s.Last != visitIndex) seen[r.UserName] = (s.Count + 1, s.First, visitIndex);
            }
        }
        return seen.Where(x => x.Value.Count >= 20 && x.Value.Count * 2 >= x.Value.Last - x.Value.First + 1).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
    }

    List<Row> Read(string path)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT ID, ActivityType, Timestamp, NotificationID, UserID, UserName, WorldID, WorldName, Message, Url FROM ActivityLogs WHERE Timestamp IS NOT NULL ORDER BY Timestamp, ID";
        using var r = cmd.ExecuteReader();
        var list = new List<Row>();
        while (r.Read())
        {
            if (!DateTime.TryParse(r.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) continue;
            string? S(int i) => r.IsDBNull(i) || r.GetString(i).Length == 0 ? null : r.GetString(i);
            list.Add(new Row(r.GetInt64(0), r.GetInt32(1), LogProjector.ToUtc(local, _tz), S(3), S(4), S(5), S(6), S(7), S(8), S(9)));
        }
        return list;
    }

    /// <summary>この端末のログの記録がある期間（VRChat を起こしてから閉じるまで）。</summary>
    List<(DateTime From, DateTime To)> LogCoveredRanges()
    {
        using var cmd = db.Command("SELECT started_at, ended_at FROM sessions WHERE device_id = $d AND started_at IS NOT NULL", null, ("$d", deviceId));
        using var r = cmd.ExecuteReader();
        var list = new List<(DateTime, DateTime)>();
        while (r.Read()) list.Add((Database.ParseTime(r.GetString(0)), r.IsDBNull(1) ? DateTime.UtcNow : Database.ParseTime(r.GetString(1))));
        return list;
    }

    /// <summary>名前 → (ID, その名前を見た期間)。DB のログの記録と、VAT の通知（名前と ID の両方がある）から作る。</summary>
    Dictionary<string, List<(string Id, DateTime From, DateTime To)>> NameIndex(List<Row> rows, HashSet<string> selfNames)
    {
        var index = new Dictionary<string, List<(string, DateTime, DateTime)>>(StringComparer.Ordinal);
        void Add(string name, string id, DateTime from, DateTime to)
        {
            if (!index.TryGetValue(name, out var list)) index[name] = list = [];
            var i = list.FindIndex(x => x.Item1 == id);
            if (i < 0) list.Add((id, from, to));
            else list[i] = (id, from < list[i].Item2 ? from : list[i].Item2, to > list[i].Item3 ? to : list[i].Item3);
        }
        using (var cmd = db.Command("SELECT user_id, name, first_seen_at, last_seen_at FROM person_names WHERE user_id IS NOT NULL AND user_id NOT LIKE 'name:%'"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) Add(r.GetString(1), r.GetString(0), Database.ParseTime(r.GetString(2)), Database.ParseTime(r.GetString(3)));
        foreach (var row in rows)
            if (row.UserId != null && row.UserName != null && row.UserId.StartsWith("usr_", StringComparison.Ordinal) && !selfNames.Contains(row.UserName))
                Add(row.UserName, row.UserId, row.At, row.At);
        return index;
    }

    /// <summary>名前から ID を引く。同じ名前の人が何人もいたら、その時期にその名前だった人を選ぶ。</summary>
    static string? Resolve(Dictionary<string, List<(string Id, DateTime From, DateTime To)>> index, string name, DateTime at)
    {
        if (!index.TryGetValue(name, out var list) || list.Count == 0) return null;
        if (list.Count == 1) return list[0].Id;
        return list.OrderBy(x => at < x.From ? x.From - at : at > x.To ? at - x.To : TimeSpan.Zero).First().Id;
    }

    /// <summary>VAT の ActivityType を、通知の向きと種類にする（VRChatActivityToolsShared/Database/ActivityType.cs の定義による）。</summary>
    static (string Direction, string Type)? Notification(int type) => type switch
    {
        2 => ("sent", "invite"),
        3 => ("received", "invite"),
        4 => ("sent", "requestInvite"),
        5 => ("received", "requestInvite"),
        6 => ("sent", "friendRequest"),
        7 => ("received", "friendRequest"),
        8 => ("accepted", "friendRequest"),
        9 => ("sent", "inviteResponse"),
        10 => ("received", "inviteResponse"),
        11 => ("sent", "requestInviteResponse"),
        12 => ("received", "requestInviteResponse"),
        14 => ("accepted", "invite"),
        15 => ("accepted", "requestInvite"),
        _ => null,
    };
}
