using System.Security.Cryptography;
using System.Text;

namespace Yamaori.Photos;

/// <summary>
/// 設定のファイルに置くパスワード。Windows では、そのユーザーにしか戻せない形（DPAPI）で暗号化する。
/// ほかの OS では暗号化できないので、"plain:" を付けてそのまま置く（設定のファイルを本人だけが読める場所に置く）。
/// </summary>
public static class Secrets
{
    public static string Protect(string secret)
    {
        if (OperatingSystem.IsWindows())
            return "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser));
        return "plain:" + secret;
    }

    /// <summary>
    /// 戻せないとき（ほかのユーザー・ほかの PC で暗号化したもの、壊れたもの）は null。
    /// PC を買い替えてデータのフォルダを戻したときに、ここで落ちて Yamaori が起きなくなるのを防ぐ（設定で入れ直せばよい）。
    /// </summary>
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        if (stored.StartsWith("dpapi:", StringComparison.Ordinal) && OperatingSystem.IsWindows())
        {
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[6..]), null, DataProtectionScope.CurrentUser)); }
            catch (Exception ex) when (ex is CryptographicException or FormatException) { return null; }
        }
        if (stored.StartsWith("plain:", StringComparison.Ordinal)) return stored[6..];
        return null;
    }

    /// <summary>置いてあるのに戻せない（入れ直しが要る）。</summary>
    public static bool Unreadable(string? stored) => !string.IsNullOrEmpty(stored) && Unprotect(stored) == null;
}
