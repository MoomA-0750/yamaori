namespace Yamaori.Store;

public sealed record ForgottenPerson(string Key, string? Label, DateTime ForgottenAt);

/// <summary>
/// 人を忘れる。会った記録・その人の出来事（動画・通知・フレンドの増減）・フレンドの一覧の写し・その人へのメモを消し、
/// あとから読み直し・取り込み・同期で入ってきても入れない（表のトリガー）。生のログ（logs）と log_events には残る。
/// </summary>
public sealed class ForgetStore(Database db)
{
    /// <summary>key は usr_… か name:&lt;名前&gt;。消した行の数を返す。</summary>
    public int Forget(string key, string? label)
    {
        if (!(key.StartsWith("usr_", StringComparison.Ordinal) || key.StartsWith("name:", StringComparison.Ordinal)) || key.Length > 300)
            throw new ArgumentException("人の ID は usr_… か name:<名前>", nameof(key));
        var name = key.StartsWith("name:", StringComparison.Ordinal) ? key[5..] : null;
        using var tx = db.Connection.BeginTransaction();
        var n = 0;
        int Exec(string sql, params (string, object?)[] args)
        {
            using var cmd = db.Command(sql, tx, args);
            return cmd.ExecuteNonQuery();
        }
        Exec("INSERT INTO forgotten_people (key, label, forgotten_at) VALUES ($k, $l, $now) ON CONFLICT (key) DO NOTHING",
            ("$k", key), ("$l", label), ("$now", Database.Time(DateTime.UtcNow)));
        if (name == null)
        {
            n += Exec("DELETE FROM encounters WHERE user_id = $k", ("$k", key));
            n += Exec("DELETE FROM events WHERE user_id = $k", ("$k", key));
            n += Exec("DELETE FROM friend_snapshots WHERE user_id = $k", ("$k", key));
        }
        else
        {
            n += Exec("DELETE FROM encounters WHERE user_id IS NULL AND name = $n", ("$n", name));
            n += Exec("DELETE FROM events WHERE user_id IS NULL AND name = $n", ("$n", name));
        }
        n += Exec("DELETE FROM notes WHERE target_type = 'person' AND target_id = $k", ("$k", key));
        tx.Commit();
        return n;
    }

    public IReadOnlyList<ForgottenPerson> List()
    {
        using var cmd = db.Command("SELECT key, label, forgotten_at FROM forgotten_people ORDER BY forgotten_at DESC");
        using var r = cmd.ExecuteReader();
        var list = new List<ForgottenPerson>();
        while (r.Read()) list.Add(new ForgottenPerson(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), Database.ParseTime(r.GetString(2))));
        return list;
    }

    /// <summary>忘れるのをやめる（消した記録は、次に読み直す・取り込み直すと戻る。メモは戻らない）。</summary>
    public bool Unforget(string key)
    {
        using var cmd = db.Command("DELETE FROM forgotten_people WHERE key = $k", null, ("$k", key));
        return cmd.ExecuteNonQuery() > 0;
    }
}
