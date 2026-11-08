namespace Yamaori.Core.Log;

/// <summary>
/// ログ 1 本の出来事を頭から順に流して、訪問・出会い・出来事を組み立てる。
/// 何度流しても同じ結果になる（ID は出どころから決まる）ので、読み方を直したら流し直せばよい。
/// </summary>
public static class LogProjector
{
    /// <param name="deviceId">ログを書いた端末（ほかの端末の同じ名前のログとぶつからないように、ID に入れる）。</param>
    /// <param name="logFile">ログのファイル名（パスでなく名前。写して残したログでも同じになる）。</param>
    /// <param name="events">出来事と、その件のバイト位置。位置の順に並べる。</param>
    /// <param name="timeZone">ログを書いた PC のタイムゾーン（ログの時刻はローカル時刻）。</param>
    /// <param name="lastEntryAt">ログの最後の件の時刻（ローカル）。終了の行がないとき、ここで閉じる。</param>
    /// <param name="closed">ログが閉じた（VRChat が終わった、または次のログに移った）か。閉じていなければ、最後の訪問はまだ続いている。</param>
    public static Projection Project(string deviceId, string logFile, IEnumerable<(long Offset, LogEvent Event)> events, TimeZoneInfo timeZone, DateTime? lastEntryAt, bool closed)
    {
        var source = Source(deviceId, logFile);
        var visits = new List<Visit>();
        var encounters = new List<Encounter>();
        var activity = new List<ActivityEvent>();
        var seenNotifications = new HashSet<string>();

        string? selfId = null, selfName = null, hmd = null;
        bool? vr = null;
        DateTime? endedAt = null;
        string? endReason = null;

        Visit? visit = null;
        var open = new Dictionary<string, Encounter>(); // 人の鍵（ID、なければ名前）→ まだいる出会い
        DateTime? moving = null; // 移動を始めた時刻（OnLeftRoom が来なかったときの代わり）
        string? pendingName = null; // 今の VRChat では、ワールドの名前（Entering Room）が入った行（Joining）より先に出る

        DateTime Utc(DateTime local) => ToUtc(local, timeZone);

        void CloseVisit(DateTime at, string reason)
        {
            if (visit == null) return;
            foreach (var e in open.Values) encounters.Add(e with { LeftAt = at });
            open.Clear();
            visits.Add(visit with { LeftAt = at, LeftReason = reason });
            visit = null;
            moving = null;
        }

        bool IsSelf(string name, string? id) => id != null ? id == selfId : name == selfName;

        foreach (var (offset, e) in events)
        {
            var at = Utc(e.At);
            switch (e)
            {
                case SelfIdentified s:
                    // 起こしたままほかのアカウントに切り替えると、またこの行が出る。そこから先はそのアカウントの記録
                    selfId = s.UserId;
                    selfName = s.Name;
                    break;
                case VrModeDetected v:
                    vr = v.Vr;
                    hmd = v.Hmd ?? hmd;
                    break;
                case DestinationFetching:
                    if (visit != null) moving ??= at;
                    pendingName = null;
                    break;
                case RoomLeft:
                    CloseVisit(at, "left");
                    break;
                case RoomJoining j:
                    if (visit != null) CloseVisit(moving ?? at, "moved");
                    var info = InstanceInfo.Parse(j.Instance);
                    visit = new Visit(StableId.Of("visit", deviceId, logFile, offset), source, j.WorldId, pendingName, j.Instance,
                        info.Number, info.Access, info.OwnerId, info.GroupId, info.Region, at, null, null, selfId);
                    pendingName = null;
                    break;
                case RoomEntered r:
                    // 訪問の外で出た名前は次に入る訪問の分。訪問の中で出たものは、まだ名前がなければその訪問に付ける
                    if (visit == null) pendingName = r.WorldName;
                    else if (visit.WorldName == null) visit = visit with { WorldName = r.WorldName };
                    break;
                case PlayerJoined p:
                    if (visit == null || IsSelf(p.Name, p.UserId)) break;
                    var key = p.UserId ?? "name:" + p.Name;
                    if (open.ContainsKey(key)) break;
                    open[key] = new Encounter(StableId.Of("encounter", deviceId, logFile, offset), visit.Id, p.UserId, p.Name, at, null);
                    break;
                case PlayerLeft p:
                    if (visit == null) break;
                    var leftKey = p.UserId ?? "name:" + p.Name;
                    if (open.Remove(leftKey, out var enc)) encounters.Add(enc with { LeftAt = at });
                    break;
                case VideoPlayed v:
                    activity.Add(Event(offset, at, "video", null, null, new() { ["url"] = v.Url }));
                    break;
                case AvatarChanged a:
                    // ほかの人の分は、その人のアバターが読み込まれるたびに出るので残さない
                    if (!IsSelf(a.Name, null)) break;
                    activity.Add(Event(offset, at, "avatar", selfId, a.Name, new() { ["avatar"] = a.AvatarName }));
                    break;
                case ScreenshotTaken s:
                    activity.Add(Event(offset, at, "screenshot", selfId, selfName, new() { ["path"] = s.Path }));
                    break;
                case NotificationLogged n:
                    if (!seenNotifications.Add(n.Direction + ":" + n.NotificationId)) break;
                    var data = new Dictionary<string, string?>
                    {
                        ["direction"] = n.Direction,
                        ["type"] = n.Type,
                        ["notification_id"] = n.NotificationId,
                        ["receiver_id"] = n.ReceiverId,
                        ["details"] = n.Details,
                        ["message"] = n.Message,
                    };
                    activity.Add(new ActivityEvent(StableId.Of("activity", "notification", n.Direction, n.NotificationId), source,
                        visit?.Id, n.CreatedUtc ?? at, "notification", n.SenderId, n.SenderName, data, n.ReceiverId ?? selfId));
                    break;
                case ApplicationQuit:
                    endedAt = at;
                    endReason = "quit";
                    CloseVisit(at, "quit");
                    break;
            }
        }

        if (closed)
        {
            var last = lastEntryAt is { } l ? Utc(l) : (DateTime?)null;
            if (endedAt == null && last != null)
            {
                endedAt = last;
                endReason = "log_end";
            }
            if (visit != null && last != null) CloseVisit(last.Value, "log_end");
        }
        // まだいる訪問は、出た時刻を空けて入れる
        if (visit != null)
        {
            foreach (var e in open.Values) encounters.Add(e);
            visits.Add(visit);
        }

        var started = LogReader.StartedAt(logFile) is { } st ? Utc(st) : (DateTime?)null;
        var session = new LogSession(deviceId, logFile, started, endedAt, endReason, selfId, selfName, vr, hmd);
        return new Projection(deviceId, session, visits, encounters, activity);

        ActivityEvent Event(long offset, DateTime at, string kind, string? userId, string? name, Dictionary<string, string?> data) =>
            new(StableId.Of("activity", deviceId, logFile, offset), source, visit?.Id, at, kind, userId, name, data, selfId);
    }

    /// <summary>記録の出どころ（log:&lt;端末&gt;/&lt;ファイル名&gt;）。</summary>
    public static string Source(string deviceId, string logFile) => $"log:{deviceId}/{logFile}";

    /// <summary>ローカル時刻を UTC に直す。夏時間で飛んだ時刻は 1 時間後として扱う。</summary>
    public static DateTime ToUtc(DateTime local, TimeZoneInfo timeZone)
    {
        var t = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(t)) t = t.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(t, timeZone);
    }
}
