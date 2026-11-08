using System.Globalization;
using System.Text.RegularExpressions;

namespace Yamaori.Core.Log;

/// <summary>
/// ログの 1 件を出来事にする。読めない件は null。
/// 行の形は docs/log-check-2026-11-08.md（実際のログで確かめた分）と、VRCX・VRChatActivityTools のソースによる。
/// 古い版では [Behaviour] が [RoomManager] や難読化した文字のタグになるので、タグの中身は見ない。
/// </summary>
public static partial class LineParser
{
    [GeneratedRegex(@"^\[[^\]]*\] (.*)$", RegexOptions.Singleline)]
    private static partial Regex Tagged();
    [GeneratedRegex(@"^Joining (wrld_[0-9a-f-]+):(\S+)$")]
    private static partial Regex Joining();
    [GeneratedRegex(@"^(?:Entering Room|Joining or Creating Room): (.+)$")]
    private static partial Regex Entering();
    [GeneratedRegex(@"^Destination fetching: (\S+)")]
    private static partial Regex Destination();
    [GeneratedRegex(@"^OnPlayer(Joined|Left) (.+?)(?: \((usr_[0-9a-f-]+)\))?$")]
    private static partial Regex Player();
    [GeneratedRegex(@"^Switching (.+) to avatar (.+)$")]
    private static partial Regex Avatar();
    [GeneratedRegex(@"(?:post login checks for|User Authenticated:) (.+) \((usr_[0-9a-f-]+)\)")]
    private static partial Regex Self();
    [GeneratedRegex(@"^\[Video Playback\] (?:Attempting to resolve|Resolving) URL '(.+)'$")]
    private static partial Regex Video();
    [GeneratedRegex(@"^\[VRC Camera\] Took screenshot to: (.+)$")]
    private static partial Regex Screenshot();
    [GeneratedRegex(@"\[SteamVR\]</b> Initialized\. Connected to [^:]*: ([^:]+?) :")]
    private static partial Regex SteamVr();
    [GeneratedRegex(
        @"^(?<dir>Received Notification|Send notification|AcceptNotification for notification|AnswerNotification for notification): ?<Notification from username:(?<name>.*?), sender user id:(?<sender>\S*) to (?<recv>\S*) ?of type: ?(?<type>\w+), id: (?<id>[^,]+), created at: (?<created>[^,]+?)(?: UTC)?, details: ?(?<details>\{\{.*?\}\}), type: ?\w+, m seen: ?\w+, message: ""(?<msg>.*)"">$",
        RegexOptions.Singleline)]
    private static partial Regex Notification();

    public static LogEvent? Parse(LogEntry entry)
    {
        var e = Parse(entry.Body);
        return e == null ? null : e with { At = entry.LocalTime };
    }

    static LogEvent? Parse(string body)
    {
        if (body.Contains("Notification", StringComparison.Ordinal) && Notification().Match(body) is { Success: true } n) return ToNotification(n);
        if (Self().Match(body) is { Success: true } self) return new SelfIdentified(self.Groups[1].Value.Trim(), self.Groups[2].Value);
        if (body.StartsWith("VRCApplication: OnApplicationQuit", StringComparison.Ordinal)) return new ApplicationQuit();
        if (body == "VR Disabled") return new VrModeDetected(false, null);
        if (SteamVr().Match(body) is { Success: true } vr) return new VrModeDetected(true, vr.Groups[1].Value.Trim());
        if (Video().Match(body) is { Success: true } v) return new VideoPlayed(v.Groups[1].Value);
        if (Screenshot().Match(body) is { Success: true } s) return new ScreenshotTaken(s.Groups[1].Value.Trim());

        if (Tagged().Match(body) is not { Success: true } t) return null;
        var rest = t.Groups[1].Value;
        if (rest == "OnLeftRoom") return new RoomLeft();
        if (Joining().Match(rest) is { Success: true } j) return new RoomJoining(j.Groups[1].Value, j.Groups[2].Value);
        if (Entering().Match(rest) is { Success: true } en) return new RoomEntered(en.Groups[1].Value.Trim());
        if (Destination().Match(rest) is { Success: true } d) return new DestinationFetching(d.Groups[1].Value);
        if (Player().Match(rest) is { Success: true } p)
        {
            var name = p.Groups[2].Value.Trim();
            var id = p.Groups[3].Success ? p.Groups[3].Value : null;
            return p.Groups[1].Value == "Joined" ? new PlayerJoined(name, id) : new PlayerLeft(name, id);
        }
        if (Avatar().Match(rest) is { Success: true } a) return new AvatarChanged(a.Groups[1].Value.Trim(), a.Groups[2].Value.Trim());
        return null;
    }

    static NotificationLogged ToNotification(Match n)
    {
        var dir = n.Groups["dir"].Value switch
        {
            "Received Notification" => "received",
            "Send notification" => "sent",
            "AcceptNotification for notification" => "accepted",
            _ => "answered",
        };
        DateTime? created = DateTime.TryParseExact(n.Groups["created"].Value.Trim(), "MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var c) ? c : null;
        var details = n.Groups["details"].Value;
        return new NotificationLogged(
            dir,
            n.Groups["type"].Value,
            n.Groups["id"].Value.Trim(),
            Empty(n.Groups["name"].Value),
            Empty(n.Groups["sender"].Value),
            Empty(n.Groups["recv"].Value),
            created,
            details == "{{}}" ? null : details,
            Empty(n.Groups["msg"].Value));
    }

    static string? Empty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
