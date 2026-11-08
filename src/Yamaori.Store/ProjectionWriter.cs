using System.Text.Json;
using Microsoft.Data.Sqlite;
using Yamaori.Core;

namespace Yamaori.Store;

/// <summary>
/// 出どころ 1 つ分（ログ 1 本など）の記録を、消して入れ直す。ID は出どころから決まるので、入れ直してもメモの付け先は変わらない。
/// </summary>
public static class ProjectionWriter
{
    public static void Replace(Database db, string source, Projection p, SqliteTransaction tx)
    {
        void Exec(string sql, params (string, object?)[] args)
        {
            using var cmd = db.Command(sql, tx, args);
            cmd.ExecuteNonQuery();
        }

        Exec("DELETE FROM visits WHERE source = $s", ("$s", source));
        Exec("DELETE FROM encounters WHERE source = $s", ("$s", source));
        Exec("DELETE FROM events WHERE source = $s", ("$s", source));

        var s = p.Session;
        Exec("""
            INSERT INTO sessions (device_id, log_file, started_at, ended_at, end_reason, self_id, self_name, vr, hmd) VALUES ($dev, $f, $st, $en, $r, $sid, $sn, $vr, $hmd)
            ON CONFLICT (device_id, log_file) DO UPDATE SET started_at = $st, ended_at = $en, end_reason = $r, self_id = $sid, self_name = $sn, vr = $vr, hmd = $hmd
            """,
            ("$dev", s.DeviceId), ("$f", s.LogFile), ("$st", Database.Time(s.StartedAt)), ("$en", Database.Time(s.EndedAt)), ("$r", s.EndReason),
            ("$sid", s.SelfId), ("$sn", s.SelfName), ("$vr", s.Vr is { } vr ? (vr ? 1 : 0) : null), ("$hmd", s.Hmd));

        foreach (var v in p.Visits)
        {
            Exec("""
                INSERT INTO visits (id, source, device_id, account_id, world_id, world_name, instance, instance_number, access, owner_id, group_id, region, joined_at, left_at, left_reason)
                VALUES ($id, $s, $dev, $acc, $w, $wn, $i, $n, $a, $o, $g, $r, $j, $l, $lr)
                """,
                ("$id", v.Id), ("$s", source), ("$dev", p.DeviceId), ("$acc", v.AccountId), ("$w", v.WorldId), ("$wn", v.WorldName), ("$i", v.Instance), ("$n", v.InstanceNumber), ("$a", v.Access),
                ("$o", v.OwnerId), ("$g", v.GroupId), ("$r", v.Region), ("$j", Database.Time(v.JoinedAt)), ("$l", Database.Time(v.LeftAt)), ("$lr", v.LeftReason));
            // ワールドの名前は、まだ持っていなければ入れる（拡張機能などで詳しい情報が入ったら、そちらを残す）
            Exec("""
                INSERT INTO worlds (id, name) VALUES ($w, $wn)
                ON CONFLICT (id) DO UPDATE SET name = COALESCE(worlds.name, $wn)
                """, ("$w", v.WorldId), ("$wn", v.WorldName));
        }
        foreach (var e in p.Encounters)
        {
            Exec("""
                INSERT INTO encounters (id, source, device_id, visit_id, user_id, name, joined_at, left_at) VALUES ($id, $s, $dev, $v, $u, $n, $j, $l)
                """,
                ("$id", e.Id), ("$s", source), ("$dev", p.DeviceId), ("$v", e.VisitId), ("$u", e.UserId), ("$n", e.Name), ("$j", Database.Time(e.JoinedAt)), ("$l", Database.Time(e.LeftAt)));
        }
        foreach (var e in p.Events)
        {
            // 通知は別のログにも同じものが出るので、先に入ったものを残す
            Exec("""
                INSERT OR IGNORE INTO events (id, source, device_id, account_id, visit_id, at, kind, user_id, name, data) VALUES ($id, $s, $dev, $acc, $v, $at, $k, $u, $n, $d)
                """,
                ("$id", e.Id), ("$s", source), ("$dev", p.DeviceId), ("$acc", e.AccountId), ("$v", e.VisitId), ("$at", Database.Time(e.At)), ("$k", e.Kind), ("$u", e.UserId), ("$n", e.Name),
                ("$d", JsonSerializer.Serialize(e.Data)));
        }
    }
}
