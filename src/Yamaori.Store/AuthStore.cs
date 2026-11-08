using System.Security.Cryptography;
using System.Text;

namespace Yamaori.Store;

public sealed record ApiToken(string Id, string Name, DateTime CreatedAt, DateTime? LastUsedAt);

/// <summary>
/// パスワード・ログインの Cookie・API のトークン・設定を DB に持つ。
/// パスワードは PBKDF2（SHA-256、60 万回）で、Cookie とトークンは SHA-256 で持ち、中身そのものは持たない。
/// </summary>
public sealed class AuthStore(Database db)
{
    const int Iterations = 600_000;
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);

    // ---- 設定

    public string? Get(string key) => db.Scalar("SELECT value FROM settings WHERE key = $k", ("$k", key)) as string;

    public void Set(string key, string? value)
    {
        using var cmd = value == null
            ? db.Command("DELETE FROM settings WHERE key = $k", null, ("$k", key))
            : db.Command("INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = $v", null, ("$k", key), ("$v", value));
        cmd.ExecuteNonQuery();
    }

    public bool GetBool(string key) => Get(key) == "true";

    public IReadOnlyList<string> GetList(string key) =>
        (Get(key) ?? "").Split([',', '\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // ---- パスワード

    public bool PasswordSet => Get("password") != null;

    public void SetPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
        Set("password", $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
        // パスワードを変えたら、ログインし直してもらう
        using var cmd = db.Command("DELETE FROM web_sessions");
        cmd.ExecuteNonQuery();
    }

    public void ClearPassword()
    {
        Set("password", null);
        using var cmd = db.Command("DELETE FROM web_sessions");
        cmd.ExecuteNonQuery();
    }

    public bool VerifyPassword(string password)
    {
        var stored = Get("password");
        if (stored == null) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256") return false;
        var iterations = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    // ---- ログインの Cookie

    /// <summary>新しいログインを作り、Cookie に入れる中身を返す。</summary>
    public string CreateSession(string? userAgent)
    {
        var token = NewSecret();
        var now = DateTime.UtcNow;
        using var cmd = db.Command("INSERT INTO web_sessions (hash, created_at, expires_at, last_seen_at, user_agent) VALUES ($h, $now, $exp, $now, $ua)", null,
            ("$h", Hash(token)), ("$now", Database.Time(now)), ("$exp", Database.Time(now + SessionLifetime)), ("$ua", userAgent));
        cmd.ExecuteNonQuery();
        return token;
    }

    public bool ValidSession(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var exp = db.Scalar("SELECT expires_at FROM web_sessions WHERE hash = $h", ("$h", Hash(token))) as string;
        if (exp == null || Database.ParseTime(exp) < DateTime.UtcNow) return false;
        using var cmd = db.Command("UPDATE web_sessions SET last_seen_at = $now WHERE hash = $h", null, ("$h", Hash(token)), ("$now", Database.Time(DateTime.UtcNow)));
        cmd.ExecuteNonQuery();
        return true;
    }

    public void DeleteSession(string? token)
    {
        if (string.IsNullOrEmpty(token)) return;
        using var cmd = db.Command("DELETE FROM web_sessions WHERE hash = $h", null, ("$h", Hash(token)));
        cmd.ExecuteNonQuery();
    }

    // ---- API のトークン

    /// <summary>トークンを作る。中身はこのときしか返さない。</summary>
    public (ApiToken Token, string Secret) CreateToken(string name)
    {
        var secret = "ymt_" + NewSecret();
        var id = "t_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        var now = DateTime.UtcNow;
        using var cmd = db.Command("INSERT INTO api_tokens (id, name, hash, created_at) VALUES ($id, $n, $h, $now)", null,
            ("$id", id), ("$n", name), ("$h", Hash(secret)), ("$now", Database.Time(now)));
        cmd.ExecuteNonQuery();
        return (new ApiToken(id, name, now, null), secret);
    }

    public bool ValidToken(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return false;
        using var cmd = db.Command("UPDATE api_tokens SET last_used_at = $now WHERE hash = $h", null, ("$h", Hash(secret)), ("$now", Database.Time(DateTime.UtcNow)));
        return cmd.ExecuteNonQuery() > 0;
    }

    public IReadOnlyList<ApiToken> Tokens()
    {
        using var cmd = db.Command("SELECT id, name, created_at, last_used_at FROM api_tokens ORDER BY created_at");
        using var r = cmd.ExecuteReader();
        var list = new List<ApiToken>();
        while (r.Read()) list.Add(new ApiToken(r.GetString(0), r.GetString(1), Database.ParseTime(r.GetString(2)), r.IsDBNull(3) ? null : Database.ParseTime(r.GetString(3))));
        return list;
    }

    public bool DeleteToken(string id)
    {
        using var cmd = db.Command("DELETE FROM api_tokens WHERE id = $id", null, ("$id", id));
        return cmd.ExecuteNonQuery() > 0;
    }

    static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    static string Hash(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
