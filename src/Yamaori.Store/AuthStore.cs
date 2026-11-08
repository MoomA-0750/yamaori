using System.Security.Cryptography;
using System.Text;

namespace Yamaori.Store;

public sealed record ApiToken(string Id, string Name, DateTime CreatedAt, DateTime? LastUsedAt, string Scope = "full", string? DeviceId = null);

/// <summary>トークンの範囲。full は設定・トークン・止める以外の全部、extension は拡張機能の口だけ、sync は子の端末の口だけ。</summary>
public static class TokenScopes
{
    public const string Full = "full";
    public const string Extension = "extension";
    public const string Sync = "sync";
    public static readonly string[] All = [Full, Extension, Sync];
}

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
    public (ApiToken Token, string Secret) CreateToken(string name, string scope = TokenScopes.Full)
    {
        if (!TokenScopes.All.Contains(scope)) throw new ArgumentException($"範囲は {string.Join("・", TokenScopes.All)}", nameof(scope));
        var secret = "ymt_" + NewSecret();
        var id = "t_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        var now = DateTime.UtcNow;
        using var cmd = db.Command("INSERT INTO api_tokens (id, name, hash, created_at, scope) VALUES ($id, $n, $h, $now, $s)", null,
            ("$id", id), ("$n", name), ("$h", Hash(secret)), ("$now", Database.Time(now)), ("$s", scope));
        cmd.ExecuteNonQuery();
        return (new ApiToken(id, name, now, null, scope), secret);
    }

    public bool ValidToken(string? secret) => FindToken(secret) != null;

    /// <summary>トークンを探す（見つかれば使った時刻を記録する）。</summary>
    public ApiToken? FindToken(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return null;
        var h = Hash(secret);
        using (var up = db.Command("UPDATE api_tokens SET last_used_at = $now WHERE hash = $h", null, ("$h", h), ("$now", Database.Time(DateTime.UtcNow))))
            if (up.ExecuteNonQuery() == 0) return null;
        using var cmd = db.Command("SELECT id, name, created_at, last_used_at, scope, device_id FROM api_tokens WHERE hash = $h", null, ("$h", h));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadToken(r) : null;
    }

    /// <summary>sync のトークンを端末に結び付ける（まだなら結び付けて true、結び付いた端末と同じなら true）。</summary>
    public bool BindTokenDevice(string tokenId, string deviceId)
    {
        using (var cmd = db.Command("UPDATE api_tokens SET device_id = $d WHERE id = $id AND device_id IS NULL", null, ("$id", tokenId), ("$d", deviceId)))
            cmd.ExecuteNonQuery();
        return db.Scalar("SELECT device_id FROM api_tokens WHERE id = $id", ("$id", tokenId)) as string == deviceId;
    }

    static ApiToken ReadToken(Microsoft.Data.Sqlite.SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), Database.ParseTime(r.GetString(2)), r.IsDBNull(3) ? null : Database.ParseTime(r.GetString(3)),
            r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5));

    public IReadOnlyList<ApiToken> Tokens()
    {
        using var cmd = db.Command("SELECT id, name, created_at, last_used_at, scope, device_id FROM api_tokens ORDER BY created_at");
        using var r = cmd.ExecuteReader();
        var list = new List<ApiToken>();
        while (r.Read()) list.Add(ReadToken(r));
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
