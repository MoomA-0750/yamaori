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
        var since = PushedHash(parent, "notes") ?? "";
        return Rows("""
            SELECT id, target_type, target_id, body, device_id, created_at, updated_at FROM notes
            WHERE device_id = $d AND source IS NULL AND updated_at > $since ORDER BY updated_at
            """, r => new SyncNote(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), S(r, 4), Database.ParseTime(r.GetString(5)), Database.ParseTime(r.GetString(6))),
            ("$d", deviceId), ("$since", since));
    }

    // ---- 親

    public void Apply(SyncProjection p)
    {
        if (!p.Source.StartsWith($"log:{p.DeviceId}/", StringComparison.Ordinal))
            throw new ArgumentException("出どころは、その端末のログ（log:<端末>/…）だけ受け取る");
        using var tx = db.Connection.BeginTransaction();
        using (var cmd = db.Command("""
            INSERT INTO devices (id, name, is_self, created_at) VALUES ($id, $n, 0, $now)
            ON CONFLICT (id) DO UPDATE SET name = $n WHERE devices.is_self = 0
            """, tx, ("$id", p.DeviceId), ("$n", p.DeviceName), ("$now", Database.Time(DateTime.UtcNow))))
            cmd.ExecuteNonQuery();
        ProjectionWriter.Replace(db, p.Source, p.Projection with { DeviceId = p.DeviceId }, tx);
        tx.Commit();
    }

    /// <summary>メモを入れる。親にもっと新しく直したものがあれば、そちらを残す。入れた数。</summary>
    public int ApplyNotes(IReadOnlyList<SyncNote> notes)
    {
        var n = 0;
        using var tx = db.Connection.BeginTransaction();
        foreach (var x in notes)
        {
            using var cmd = db.Command("""
                INSERT INTO notes (id, target_type, target_id, body, device_id, created_at, updated_at) VALUES ($id, $t, $i, $b, $d, $c, $u)
                ON CONFLICT (id) DO UPDATE SET body = $b, updated_at = $u WHERE notes.updated_at < $u
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
