namespace Yamaori.Store;

/// <summary>VAT・VRCX の取り込みで共通の確かめ。</summary>
public static class ImportGuard
{
    /// <summary>
    /// 取り込む DB が、前に取り込んだものと同じものか（の続き）を確かめる。
    /// 取り込みは出どころ（vat・vrcx）ごとに入れ替えるので、別の DB を取り込むと前の取り込みが消える。違えば、replace のときだけ進める。
    /// </summary>
    public static void CheckOrigin(Database db, string source, string origin, bool replace)
    {
        var store = new AuthStore(db);
        var key = "import_origin:" + source;
        var before = store.Get(key);
        // 訪問がなくても、出来事・メモ・お気に入りがあれば、前の取り込みがある
        var has = db.Scalar("""
            SELECT 1 WHERE EXISTS (SELECT 1 FROM visits WHERE source = $s) OR EXISTS (SELECT 1 FROM events WHERE source = $s)
                OR EXISTS (SELECT 1 FROM notes WHERE source = $s) OR EXISTS (SELECT 1 FROM world_favorites WHERE source = $s)
            """, ("$s", source)) != null;
        if (before != null && before != origin && has && !replace)
            throw new InvalidOperationException(
                $"前に取り込んだものと別の DB に見える（前: {before}、今: {origin}）。取り込むと前の {source} の記録と入れ替わる。入れ替えてよければ --replace を付ける");
    }

    public static void RememberOrigin(Database db, string source, string origin, Microsoft.Data.Sqlite.SqliteTransaction? tx = null)
    {
        using var cmd = db.Command("INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = $v", tx,
            ("$k", "import_origin:" + source), ("$v", origin));
        cmd.ExecuteNonQuery();
    }

    /// <summary>ほかの取り込み（VAT なら VRCX）の訪問（ワールドと入った時刻）。同じ訪問を両方から入れて二重にしないため。</summary>
    public static ILookup<string, DateTime> VisitsOf(Database db, string otherSource)
    {
        using var cmd = db.Command("SELECT world_id, joined_at FROM visits WHERE source = $s", null, ("$s", otherSource));
        using var r = cmd.ExecuteReader();
        var list = new List<(string, DateTime)>();
        while (r.Read()) list.Add((r.GetString(0), Database.ParseTime(r.GetString(1))));
        return list.ToLookup(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
    }

    /// <summary>同じワールドに、入った時刻が 5 分以内の訪問があれば、同じ訪問とみなす。</summary>
    public static bool SameVisit(ILookup<string, DateTime> others, string world, DateTime joined) =>
        others[world].Any(t => (t - joined).Duration() <= TimeSpan.FromMinutes(5));

    /// <summary>この DB で遊んだアカウントが 1 つだけならそれ（セッションの最後のアカウントだけでなく、訪問に付いたアカウントも数える）。</summary>
    public static string? SingleAccount(Database db)
    {
        using var cmd = db.Command("SELECT id FROM accounts UNION SELECT account_id FROM visits WHERE account_id IS NOT NULL AND source LIKE 'log:%'");
        using var r = cmd.ExecuteReader();
        var ids = new List<string>();
        while (r.Read()) ids.Add(r.GetString(0));
        return ids.Count == 1 ? ids[0] : null;
    }
}
