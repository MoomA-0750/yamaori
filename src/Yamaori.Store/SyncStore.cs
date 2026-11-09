using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Yamaori.Core;

namespace Yamaori.Store;

/// <summary>子から親へ送る、出どころ 1 つ分の記録（ログ 1 本など）。</summary>
public sealed record SyncProjection(string DeviceId, string DeviceName, string Source, Projection Projection);

/// <summary>子から親へ送るメモ。</summary>
public sealed record SyncNote(string Id, string TargetType, string TargetId, string Body, string? DeviceId, DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>
/// 親と子の端末のあいだで記録をまとめる。
/// <list type="bullet">
/// <item>子: この端末のログから組み立てた記録を、出どころ（ログ 1 本）ごとにまとめ、前に送った中身と変わったものだけを送る</item>
/// <item>親: 受け取った出どころの分を入れ替える。ID は端末ごとに決まっているので、二重にならない</item>
/// <item>メモ: 子で書いた・直したメモを送る。親でも直していたら、後から直した方を残す</item>
/// </list>
/// 取り込み（VAT・VRCX）と写真は送らない（取り込みは親で、写真は NAS を親の写真のフォルダにも足して見る）。
/// </summary>
public sealed class SyncStore(Database db)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---- 子

    /// <summary>この端末のログの出どころ（log:&lt;端末&gt;/…）。</summary>
    public IReadOnlyList<string> LocalSources(string deviceId)
    {
        using var cmd = db.Command("SELECT 'log:' || device_id || '/' || name FROM log_files WHERE device_id = $d ORDER BY name", null, ("$d", deviceId));
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>出どころ 1 つ分の記録を DB から組み立て直す（送るため）。</summary>
    public Projection Read(string deviceId, string source)
    {
        var logFile = source[(source.IndexOf('/', StringComparison.Ordinal) + 1)..];
        LogSession? session = null;
        foreach (var s in Rows("SELECT log_file, started_at, ended_at, end_reason, self_id, self_name, vr, hmd FROM sessions WHERE device_id = $d AND log_file = $f",
            r => new LogSession(deviceId, r.GetString(0), T(r, 1), T(r, 2), S(r, 3), S(r, 4), S(r, 5), r.IsDBNull(6) ? null : r.GetInt64(6) != 0, S(r, 7)),
            ("$d", deviceId), ("$f", logFile)))
            session = s;
        var visits = Rows("""
            SELECT id, source, world_id, world_name, instance, instance_number, access, owner_id, group_id, region, joined_at, left_at, left_reason, account_id
            FROM visits WHERE source = $s ORDER BY joined_at
            """, r => new Visit(r.GetString(0), r.GetString(1), r.GetString(2), S(r, 3), r.GetString(4), r.GetString(5), r.GetString(6), S(r, 7), S(r, 8),
                r.GetString(9), Database.ParseTime(r.GetString(10)), T(r, 11), S(r, 12), S(r, 13)), ("$s", source));
        var encounters = Rows("SELECT id, visit_id, user_id, name, joined_at, left_at FROM encounters WHERE source = $s ORDER BY joined_at",
            r => new Encounter(r.GetString(0), r.GetString(1), S(r, 2), r.GetString(3), Database.ParseTime(r.GetString(4)), T(r, 5)), ("$s", source));
        var events = Rows("SELECT id, source, visit_id, at, kind, user_id, name, data, account_id FROM events WHERE source = $s ORDER BY at, id",
            r => new ActivityEvent(r.GetString(0), r.GetString(1), S(r, 2), Database.ParseTime(r.GetString(3)), r.GetString(4), S(r, 5), S(r, 6),
                JsonSerializer.Deserialize<Dictionary<string, string?>>(r.GetString(7)) ?? [], S(r, 8)), ("$s", source));
        return new Projection(deviceId, session, visits, encounters, events);
    }

    public static string Hash(object payload) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, Json))));

    public string? PushedHash(string parent, string source) =>
        db.Scalar("SELECT hash FROM sync_state WHERE parent = $p AND source = $s", ("$p", parent), ("$s", source)) as string;

    public void MarkPushed(string parent, string source, string hash)
    {
        using var cmd = db.Command("""
            INSERT INTO sync_state (parent, source, hash, pushed_at) VALUES ($p, $s, $h, $now)
            ON CONFLICT (parent, source) DO UPDATE SET hash = $h, pushed_at = $now
            """, null, ("$p", parent), ("$s", source), ("$h", hash), ("$now", Database.Time(DateTime.UtcNow)));
        cmd.ExecuteNonQuery();
    }

    /// <summary>この端末で書いた・直したメモのうち、前に送ってから変わったもの。</summary>
    public IReadOnlyList<SyncNote> NotesToPush(string parent, string deviceId)
    {
        // 送った印は「最後の時刻|その時刻に送ったメモの ID」。時刻は秒までなので、同じ秒にあとから書いたメモも拾う
        var mark = (PushedHash(parent, "notes") ?? "").Split('|', 2);
        var since = mark[0];
        var sent = mark.Length > 1 ? mark[1].Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet() : [];
        return Rows("""
            SELECT id, target_type, target_id, body, device_id, created_at, updated_at FROM notes
            WHERE device_id = $d AND source IS NULL AND updated_at >= $since ORDER BY updated_at
            """, r => new SyncNote(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), S(r, 4), Database.ParseTime(r.GetString(5)), Database.ParseTime(r.GetString(6))),
            ("$d", deviceId), ("$since", since))
            .Where(n => Database.Time(n.UpdatedAt) != since || !sent.Contains(n.Id))
            .ToList();
    }

    /// <summary>送ったメモの印（最後の時刻と、その時刻のメモの ID）。前の印と同じ時刻なら、前に送った ID も残す。</summary>
    public string NotesMark(string parent, IReadOnlyList<SyncNote> pushed)
    {
        var last = Database.Time(pushed.Max(n => n.UpdatedAt));
        var ids = pushed.Where(n => Database.Time(n.UpdatedAt) == last).Select(n => n.Id);
        var prev = (PushedHash(parent, "notes") ?? "").Split('|', 2);
        if (prev[0] == last && prev.Length > 1) ids = ids.Concat(prev[1].Split(',', StringSplitOptions.RemoveEmptyEntries));
        return last + "|" + string.Join(",", ids.Distinct());
    }

    // ---- 親

    // 子から受け取る量の上限（ログ 1 本分としては十分に大きく、DB を膨らませるほどではない）
    public const int MaxVisits = 20_000, MaxEncounters = 500_000, MaxEvents = 200_000, MaxText = 2_000, MaxData = 20_000;

    /// <summary>受け取ってよい形か。だめなら理由（端末にトークンを結び付ける前に確かめる）。</summary>
    public string? Check(SyncProjection p)
    {
        if (p.DeviceId is not { Length: > 0 and <= 64 } || p.DeviceName is not { Length: <= 200 } || p.Source is not { Length: <= 600 })
            return "端末の ID・名前・出どころが長すぎる";
        if (!p.Source.StartsWith($"log:{p.DeviceId}/", StringComparison.Ordinal))
            return "出どころは、その端末のログ（log:<端末>/…）だけ受け取る";
        // 親そのものの端末の記録は、子から入れ替えさせない
        if (db.Scalar("SELECT is_self FROM devices WHERE id = $id", ("$id", p.DeviceId)) is long self && self != 0)
            return "親そのものの端末の記録は受け取らない";
        var x = p.Projection;
        if (x.Visits.Count > MaxVisits || x.Encounters.Count > MaxEncounters || x.Events.Count > MaxEvents) return "記録の数が多すぎる";
        static bool Long(string? s) => s is { Length: > MaxText };
        if (x.Visits.Any(v => Long(v.Id) || Long(v.WorldId) || Long(v.WorldName) || Long(v.Instance) || Long(v.OwnerId) || Long(v.GroupId) || Long(v.AccountId))
            || x.Encounters.Any(e => Long(e.Id) || Long(e.VisitId) || Long(e.UserId) || Long(e.Name))
            || x.Events.Any(e => Long(e.Id) || Long(e.Kind) || Long(e.UserId) || Long(e.Name) || e.Data.Sum(d => d.Key.Length + (d.Value?.Length ?? 0)) > MaxData))
            return "記録の文字が長すぎる";
        return null;
    }

    public void Apply(SyncProjection p)
    {
        if (Check(p) is { } bad) throw new ArgumentException(bad);
        using var tx = db.Connection.BeginTransaction();
        using (var cmd = db.Command("""
            INSERT INTO devices (id, name, is_self, created_at) VALUES ($id, $n, 0, $now)
            ON CONFLICT (id) DO UPDATE SET name = $n WHERE devices.is_self = 0
            """, tx, ("$id", p.DeviceId), ("$n", p.DeviceName), ("$now", Database.Time(DateTime.UtcNow))))
            cmd.ExecuteNonQuery();
        ProjectionWriter.Replace(db, p.Source, p.Projection with { DeviceId = p.DeviceId }, tx);
        tx.Commit();
    }

    /// <summary>
    /// メモを入れる。親にもっと新しく直したものがあれば、そちらを残す。入れた数。
    /// onlyDevice を渡すと（端末に結び付いたトークンから来たとき）、その端末のメモだけを入れ、ほかの端末のメモは書き換えない。
    /// </summary>
    public int ApplyNotes(IReadOnlyList<SyncNote> notes, string? onlyDevice = null)
    {
        var n = 0;
        using var tx = db.Connection.BeginTransaction();
        foreach (var x in notes)
        {
            if (onlyDevice != null && x.DeviceId != onlyDevice) continue;
            using var cmd = db.Command("""
                INSERT INTO notes (id, target_type, target_id, body, device_id, created_at, updated_at) VALUES ($id, $t, $i, $b, $d, $c, $u)
                ON CONFLICT (id) DO UPDATE SET body = $b, updated_at = $u WHERE notes.updated_at < $u AND notes.device_id IS $d
                """, tx, ("$id", x.Id), ("$t", x.TargetType), ("$i", x.TargetId), ("$b", x.Body), ("$d", x.DeviceId),
                ("$c", Database.Time(x.CreatedAt)), ("$u", Database.Time(x.UpdatedAt)));
            n += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return n;
    }

    List<T> Rows<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] args)
    {
        using var cmd = db.Command(sql, null, args);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    static DateTime? T(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Database.ParseTime(r.GetString(i));
}
