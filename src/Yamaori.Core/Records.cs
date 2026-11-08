using System.Security.Cryptography;
using System.Text;

namespace Yamaori.Core;

/// <summary>
/// 記録の ID。出どころ（どのログの何バイト目か、どの取り込み元の何番か）から決まるので、作り直しても同じ ID になる。
/// メモはこの ID に付くので、読み直してもメモは外れない。サーバーへ送るときもこの ID で重ねる。
/// </summary>
public static class StableId
{
    public static string Of(string kind, params object?[] parts)
    {
        var key = kind + "\n" + string.Join("\n", parts.Select(p => p?.ToString() ?? ""));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return kind[0] + "_" + Convert.ToHexStringLower(hash, 0, 10);
    }
}

/// <summary>VRChat を 1 回起こしてから閉じるまで（ログ 1 本）。時刻は UTC。</summary>
public sealed record LogSession(
    string DeviceId,
    string LogFile,
    DateTime? StartedAt,
    DateTime? EndedAt,
    string? EndReason,
    string? SelfId,
    string? SelfName,
    bool? Vr,
    string? Hmd);

/// <summary>
/// インスタンスにいた 1 回。LeftAt が null ならまだいる。
/// LeftReason: left（OnLeftRoom）・moved（移動を始めた時刻で代えた）・quit（終了）・log_end（ログの最後の行で代えた）・estimated（取り込み元に出た時刻がなく推定した）。
/// </summary>
public sealed record Visit(
    string Id,
    string Source,
    string WorldId,
    string? WorldName,
    string Instance,
    string InstanceNumber,
    string Access,
    string? OwnerId,
    string? GroupId,
    string Region,
    DateTime JoinedAt,
    DateTime? LeftAt,
    string? LeftReason);

/// <summary>訪問の中で、ある人と同じインスタンスにいた間。</summary>
public sealed record Encounter(
    string Id,
    string VisitId,
    string? UserId,
    string Name,
    DateTime JoinedAt,
    DateTime? LeftAt);

/// <summary>
/// 訪問の中（または外）で起きたこと。Kind: video・avatar・notification・screenshot。
/// Data は種類ごとの中身（URL、アバター名、通知の種別など）。
/// </summary>
public sealed record ActivityEvent(
    string Id,
    string Source,
    string? VisitId,
    DateTime At,
    string Kind,
    string? UserId,
    string? Name,
    IReadOnlyDictionary<string, string?> Data);

/// <summary>ログ 1 本から組み立てた記録の全部。</summary>
public sealed record Projection(
    string DeviceId,
    LogSession Session,
    IReadOnlyList<Visit> Visits,
    IReadOnlyList<Encounter> Encounters,
    IReadOnlyList<ActivityEvent> Events);
