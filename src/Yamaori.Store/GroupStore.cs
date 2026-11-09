using System.Security.Cryptography;
using System.Text;

namespace Yamaori.Store;

public static class GroupRoles
{
    public const string Hub = "hub", Logs = "logs", Photos = "photos", Vrcx = "vrcx";
    public static readonly string[] All = [Hub, Logs, Photos, Vrcx];
    public static readonly string[] Default = [Logs, Photos];

    public static IReadOnlyList<string> Parse(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(All.Contains).Distinct().ToList();
}

public sealed record GroupMember(string DeviceId, string Name, string? Url, IReadOnlyList<string> Roles, DateTime JoinedAt, DateTime? LastSeenAt, bool IsSelf);
public sealed record GroupInfo(string Id, string Name, string HubDeviceId, IReadOnlyList<GroupMember> Members);
public sealed record GroupInvite(string Code, DateTime ExpiresAt);
public sealed record GroupJoined(string GroupId, string GroupName, string HubDeviceId, string HubName, string Token, IReadOnlyList<string> Roles);

/// <summary>
/// 自分の端末のグループ（まとめ役の端末が持つ）。まとめ役は記録を全部持ち、画面を配る。ほかの端末（メンバー）は役に従って集めて、まとめ役へ送る。
/// 端末は招待の合言葉で入る（合言葉は一度きり・30 分まで。DB にはハッシュだけを置く）。入ると、その端末に結び付いた「子の端末」の範囲のトークンを渡す。
/// </summary>
public sealed class GroupStore(Database db)
{
    public const int CodeLength = 10;
    static readonly TimeSpan InviteLife = TimeSpan.FromMinutes(30);
    const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ"; // 見間違えやすい 0 O 1 I L を外す

    AuthStore Settings => new(db);

    public bool IsHub => Settings.Get("group_id") != null;

    /// <summary>この端末をまとめ役にしてグループを作る（もうあれば名前だけ変える）。</summary>
    public GroupInfo Create(string name, string selfDeviceId, string selfName, IReadOnlyList<string>? selfRoles = null)
    {
        var s = Settings;
        if (s.Get("group_id") == null) s.Set("group_id", "g_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(10)));
        s.Set("group_name", Clean(name, 100));
        using var cmd = db.Command("""
            INSERT INTO group_members (device_id, name, roles, joined_at) VALUES ($d, $n, $r, $now)
            ON CONFLICT (device_id) DO UPDATE SET name = $n
            """, null, ("$d", selfDeviceId), ("$n", Clean(selfName, 100)),
            ("$r", string.Join(',', new[] { GroupRoles.Hub }.Concat(selfRoles ?? GroupRoles.Default))), ("$now", Database.Time(DateTime.UtcNow)));
        cmd.ExecuteNonQuery();
        return Info(selfDeviceId)!;
    }

    public GroupInfo? Info(string selfDeviceId)
    {
        var s = Settings;
        if (s.Get("group_id") is not { } id) return null;
        var members = new List<GroupMember>();
        using (var cmd = db.Command("SELECT device_id, name, url, roles, joined_at, last_seen_at FROM group_members ORDER BY joined_at"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                members.Add(new GroupMember(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), GroupRoles.Parse(r.GetString(3)),
                    Database.ParseTime(r.GetString(4)), r.IsDBNull(5) ? null : Database.ParseTime(r.GetString(5)), r.GetString(0) == selfDeviceId));
        var hub = members.FirstOrDefault(m => m.Roles.Contains(GroupRoles.Hub))?.DeviceId ?? selfDeviceId;
        return new GroupInfo(id, s.Get("group_name") ?? "", hub, members);
    }

    /// <summary>招待の合言葉を作る（一度きり・30 分まで）。</summary>
    public GroupInvite Invite()
    {
        if (!IsHub) throw new InvalidOperationException("まだグループを作っていない");
        var code = new string(RandomNumberGenerator.GetItems<char>(Alphabet, CodeLength));
        var expires = DateTime.UtcNow + InviteLife;
        using var cmd = db.Command("INSERT INTO group_invites (code_hash, created_at, expires_at) VALUES ($h, $now, $e)", null,
            ("$h", Hash(code)), ("$now", Database.Time(DateTime.UtcNow)), ("$e", Database.Time(expires)));
        cmd.ExecuteNonQuery();
        // 古い招待は片付ける
        using var old = db.Command("DELETE FROM group_invites WHERE expires_at < $t", null, ("$t", Database.Time(DateTime.UtcNow.AddDays(-1))));
        old.ExecuteNonQuery();
        return new GroupInvite(Format(code), expires);
    }

    /// <summary>合言葉は 5 文字ずつ区切って見せる（入れるときは区切りも大小も気にしない）。</summary>
    static string Format(string code) => code[..5] + "-" + code[5..];

    public static string Normalize(string code) => new string(code.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    static string Hash(string code) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(code))));

    /// <summary>
    /// 招待を受ける（まとめ役で）。合言葉が正しければ、その端末をメンバーにして、端末に結び付いたトークンを返す。正しくなければ null。
    /// もうメンバーの端末は入れない（前のトークンを奪って成り代わらせないため。入り直すときは、まとめ役で外してから）。
    /// 合言葉を使う・メンバーにする・トークンを作るを、1 回の書き込みで行う（同じ端末が同時に入っても、トークンが 2 つできない）。
    /// </summary>
    public GroupJoined? Accept(string code, string deviceId, string deviceName, string? url, string selfDeviceId)
    {
        if (Info(selfDeviceId) is not { } group) return null;
        if (deviceId == selfDeviceId || deviceId.Length is 0 or > 64 || !deviceId.StartsWith("d_", StringComparison.Ordinal)) return null;
        var now = Database.Time(DateTime.UtcNow);
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var tokenId = "t_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        using var tx = db.Connection.BeginTransaction();
        using (var use = db.Command("UPDATE group_invites SET used_at = $now, used_by = $d WHERE code_hash = $h AND used_at IS NULL AND expires_at > $now", tx,
            ("$h", Hash(code)), ("$now", now), ("$d", deviceId)))
            if (use.ExecuteNonQuery() == 0) { tx.Rollback(); return null; }
        using (var exists = db.Command("SELECT 1 FROM group_members WHERE device_id = $d", tx, ("$d", deviceId)))
            if (exists.ExecuteScalar() != null) { tx.Rollback(); return null; }
        AuthStore.InsertToken(db, tx, tokenId, $"グループ: {Clean(deviceName, 64)}", secret, TokenScopes.Sync, deviceId);
        using (var cmd = db.Command("""
            INSERT INTO group_members (device_id, name, url, roles, token_id, joined_at) VALUES ($d, $n, $u, $r, $t, $now)
            """, tx, ("$d", deviceId), ("$n", Clean(deviceName, 100)), ("$u", CleanUrl(url)), ("$r", string.Join(',', GroupRoles.Default)), ("$t", tokenId), ("$now", now)))
            cmd.ExecuteNonQuery();
        tx.Commit();
        var hub = group.Members.FirstOrDefault(m => m.DeviceId == group.HubDeviceId);
        return new GroupJoined(group.Id, group.Name, group.HubDeviceId, hub?.Name ?? "", "ymt_" + secret, RolesOf(deviceId));
    }

    public IReadOnlyList<string> RolesOf(string deviceId) =>
        GroupRoles.Parse(db.Scalar("SELECT roles FROM group_members WHERE device_id = $d", ("$d", deviceId)) as string);

    /// <summary>メンバーの役を変える（まとめ役の役は動かせない）。</summary>
    public bool SetRoles(string deviceId, IReadOnlyList<string> roles, string? name = null)
    {
        var current = RolesOf(deviceId);
        var next = roles.Where(r => GroupRoles.All.Contains(r) && r != GroupRoles.Hub).Distinct().ToList();
        if (current.Contains(GroupRoles.Hub)) next.Insert(0, GroupRoles.Hub);
        // 「VRCX と連携する」はグループの中で 1 台だけ（取り込みは 1 つの VRCX の DB の続きとして入れるので、2 台で取り合わない）
        if (next.Contains(GroupRoles.Vrcx) && !current.Contains(GroupRoles.Vrcx))
        {
            var others = new List<(string Id, string Roles)>();
            using (var o = db.Command("SELECT device_id, roles FROM group_members WHERE device_id <> $d", null, ("$d", deviceId)))
            using (var r = o.ExecuteReader())
                while (r.Read()) others.Add((r.GetString(0), r.GetString(1)));
            foreach (var (id, csv) in others.Where(x => GroupRoles.Parse(x.Roles).Contains(GroupRoles.Vrcx)))
            {
                using var u = db.Command("UPDATE group_members SET roles = $r WHERE device_id = $d", null, ("$d", id), ("$r", string.Join(',', GroupRoles.Parse(csv).Where(x => x != GroupRoles.Vrcx))));
                u.ExecuteNonQuery();
            }
        }
        using var cmd = db.Command("UPDATE group_members SET roles = $r, name = COALESCE($n, name) WHERE device_id = $d", null,
            ("$d", deviceId), ("$r", string.Join(',', next)), ("$n", name is { Length: > 0 } n ? Clean(n, 100) : null));
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>メンバーを外す（渡したトークンも消す。記録は残す）。まとめ役は外せない。</summary>
    public bool Remove(string deviceId)
    {
        if (RolesOf(deviceId).Contains(GroupRoles.Hub)) return false;
        string? token;
        using (var o = db.Command("SELECT token_id FROM group_members WHERE device_id = $d", null, ("$d", deviceId))) token = o.ExecuteScalar() as string;
        if (token != null) new AuthStore(db).DeleteToken(token);
        using var cmd = db.Command("DELETE FROM group_members WHERE device_id = $d", null, ("$d", deviceId));
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>メンバーから連絡があった（役を読みに来た・記録を送ってきた）。</summary>
    public void Seen(string deviceId, string? url = null)
    {
        using var cmd = db.Command("UPDATE group_members SET last_seen_at = $now, url = COALESCE($u, url) WHERE device_id = $d", null,
            ("$d", deviceId), ("$now", Database.Time(DateTime.UtcNow)), ("$u", CleanUrl(url)));
        cmd.ExecuteNonQuery();
    }

    static string Clean(string s, int max) { s = s.Trim(); return s.Length > max ? s[..max] : s; }

    static string? CleanUrl(string? url) =>
        url is { Length: > 0 and < 300 } && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) ? u.ToString().TrimEnd('/') : null;
}
