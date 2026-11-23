using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Yamaori.Store;

/// <summary>
/// VRCX の DB から、Yamaori が取り込む表だけを新しいファイルへ写す（まとめ役へ送ったり、取り込んだりする前に）。
/// 許した表だけを並べる形にしてある（VRCX の cookies・保存したログイン情報・フレンドの位置のフィードなどは写さない。VRCX が後で表を足しても漏れない）。
/// 合わせて、写した中身の指紋を取る（VRCX はフィードなどで常に DB を書くので、ファイルの時刻ではなく、取り込む中身が変わったかで送るかを決める）。
/// VRCX の DB は読むだけで開き、変えない。
/// </summary>
public static partial class VrcxSnapshot
{
    /// <summary>許す表の一覧の版。一覧を変えたら上げる（指紋が変わり、一度送り直す）。</summary>
    const string Version = "1";

    /// <summary>名前そのままで許す表。</summary>
    public static readonly string[] Tables =
        ["gamelog_location", "gamelog_join_leave", "gamelog_video_play", "cache_world", "favorite_world", "world_memos", "memos"];

    /// <summary>アカウントごとの表（頭 &lt;userId&gt;_ に続く名前）で許すもの。</summary>
    public static readonly string[] AccountTables =
        ["friend_log_history", "friend_log_current", "notifications", "notifications_v2", "notes"];

    /// <summary>configs の行で写すもの（アカウントの ID を知るためと、DB の版）。値にログイン情報を持つ鍵は写さない。</summary>
    static bool AllowedConfig(string key) =>
        key is "config:lastuserloggedin" or "config:vrcx_databaseversion" || key.StartsWith("config:friendloginit_usr_", StringComparison.Ordinal);

    /// <summary>行が多くなる表。中身を全部見ずに、件数と最後の id で変化を見る（gamelog_location は、出たときに time を書き換えるので time の和も）。</summary>
    static readonly Dictionary<string, string> CheapFingerprint = new(StringComparer.Ordinal)
    {
        ["gamelog_location"] = "SELECT COUNT(*), MAX(id), SUM(time) FROM gamelog_location",
        ["gamelog_join_leave"] = "SELECT COUNT(*), MAX(id) FROM gamelog_join_leave",
        ["gamelog_video_play"] = "SELECT COUNT(*), MAX(id) FROM gamelog_video_play",
    };

    [GeneratedRegex("^_?[A-Za-z0-9]+$")]
    private static partial Regex PrefixPattern();

    /// <summary>写してよい表か。configs は別に扱う（行を選ぶ）。</summary>
    public static bool Allowed(string table)
    {
        if (Tables.Contains(table, StringComparer.Ordinal)) return true;
        // 長い方から合わせる（notifications_v2 を notifications と取り違えない）
        foreach (var suffix in AccountTables.OrderByDescending(s => s.Length))
            if (table.EndsWith("_" + suffix, StringComparison.Ordinal))
                return PrefixPattern().IsMatch(table[..^(suffix.Length + 1)]);
        return false;
    }

    /// <summary>VRCX の DB を読むだけで開く。</summary>
    static SqliteConnection OpenReadOnly(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        using var busy = c.CreateCommand();
        busy.CommandText = "PRAGMA busy_timeout = 5000";
        busy.ExecuteNonQuery();
        return c;
    }

    /// <summary>写すと同じ中身の指紋（写さずに、送るかどうかを決めるため）。</summary>
    public static string Fingerprint(string vrcxPath)
    {
        using var c = OpenReadOnly(vrcxPath);
        using var tx = c.BeginTransaction();
        return Fingerprint(c, tx);
    }

    static string Fingerprint(SqliteConnection c, SqliteTransaction tx)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string s) { h.AppendData(Encoding.UTF8.GetBytes(s)); h.AppendData([0]); }
        Add("v" + Version);
        foreach (var t in TablesIn(c, tx))
        {
            Add("table:" + t);
            if (CheapFingerprint.TryGetValue(t, out var sql)) EachRow(c, tx, sql, Add);
            else EachRow(c, tx, $"SELECT * FROM \"{t}\" ORDER BY 1", Add);
        }
        foreach (var (k, v) in Configs(c, tx)) { Add("config:" + k); Add(v ?? ""); }
        return Convert.ToHexStringLower(h.GetHashAndReset())[..32];
    }

    /// <summary>許した表と configs の許した行だけを outPath へ写し、その中身の指紋を返す。指紋と写しは同じ時点のもの。</summary>
    public static string Narrow(string vrcxPath, string outPath)
    {
        using var src = OpenReadOnly(vrcxPath);
        using var tx = src.BeginTransaction();
        var fp = Fingerprint(src, tx);
        if (File.Exists(outPath)) File.Delete(outPath);
        using (var dst = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = outPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
        {
            dst.Open();
            using var dtx = dst.BeginTransaction();
            foreach (var t in TablesIn(src, tx))
            {
                var create = Scalar(src, tx, "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = $n", ("$n", t));
                if (create == null) continue;
                Exec(dst, dtx, create);
                CopyRows(src, tx, dst, dtx, t, $"SELECT * FROM \"{t}\"");
            }
            var configs = Configs(src, tx);
            if (configs.Count > 0)
            {
                Exec(dst, dtx, "CREATE TABLE configs (key TEXT PRIMARY KEY, value TEXT)");
                foreach (var (k, v) in configs) Exec(dst, dtx, "INSERT INTO configs (key, value) VALUES ($k, $v)", ("$k", k), ("$v", v));
            }
            dtx.Commit();
        }
        SqliteConnection.ClearAllPools();
        return fp;
    }

    /// <summary>VRCX の DB にある、許した表（名前の順）。</summary>
    static List<string> TablesIn(SqliteConnection c, SqliteTransaction tx)
    {
        var list = new List<string>();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name";
        using var r = cmd.ExecuteReader();
        while (r.Read()) if (r.GetString(0) is var n && Allowed(n)) list.Add(n);
        return list;
    }

    static List<(string Key, string? Value)> Configs(SqliteConnection c, SqliteTransaction tx)
    {
        var list = new List<(string, string?)>();
        if (Scalar(c, tx, "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'configs'") == null) return list;
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT key, value FROM configs ORDER BY key";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (!r.IsDBNull(0) && AllowedConfig(r.GetString(0)))
                list.Add((r.GetString(0), r.IsDBNull(1) ? null : Convert.ToString(r.GetValue(1), System.Globalization.CultureInfo.InvariantCulture)));
        return list;
    }

    static void CopyRows(SqliteConnection src, SqliteTransaction tx, SqliteConnection dst, SqliteTransaction dtx, string table, string select)
    {
        using var read = src.CreateCommand();
        read.Transaction = tx;
        read.CommandText = select;
        using var r = read.ExecuteReader();
        var cols = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToArray();
        using var ins = dst.CreateCommand();
        ins.Transaction = dtx;
        ins.CommandText = $"INSERT INTO \"{table}\" ({string.Join(", ", cols.Select(x => $"\"{x}\""))}) VALUES ({string.Join(", ", cols.Select((_, i) => "$p" + i))})";
        var ps = cols.Select((_, i) => ins.Parameters.Add(new SqliteParameter("$p" + i, null))).ToArray();
        while (r.Read())
        {
            for (var i = 0; i < cols.Length; i++) ps[i].Value = r.IsDBNull(i) ? DBNull.Value : r.GetValue(i);
            ins.ExecuteNonQuery();
        }
    }

    static void EachRow(SqliteConnection c, SqliteTransaction tx, string sql, Action<string> add)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            for (var i = 0; i < r.FieldCount; i++)
                add(r.IsDBNull(i) ? "\u0001" : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? "");
    }

    static string? Scalar(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return cmd.ExecuteScalar() as string;
    }

    static void Exec(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
