using System.Threading.Channels;
using Yamaori.Core.Log;

namespace Yamaori.Web;

/// <summary>今の出来事（SSE で流すもの）。At は UTC。</summary>
public sealed record LiveEvent(string Kind, DateTime At, IReadOnlyDictionary<string, string?> Data);

/// <summary>
/// ログの見張りが新しく読んだ出来事を、SSE でつないでいる相手へ配る。
/// 遅い相手のために溜めるのは 256 件までで、それを超えたら古いものから捨てる。
/// </summary>
public sealed class LiveHub
{
    readonly Lock _lock = new();
    readonly List<Channel<LiveEvent>> _subscribers = [];

    public ChannelReader<LiveEvent> Subscribe(CancellationToken cancel)
    {
        var ch = Channel.CreateBounded<LiveEvent>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_lock) _subscribers.Add(ch);
        cancel.Register(() =>
        {
            lock (_lock) _subscribers.Remove(ch);
            ch.Writer.TryComplete();
        });
        return ch.Reader;
    }

    public void Publish(LiveEvent e)
    {
        lock (_lock)
            foreach (var ch in _subscribers) ch.Writer.TryWrite(e);
    }

    /// <summary>ログの出来事を、流す形にする（流さないものは null）。</summary>
    public static LiveEvent? From(string logFile, LogEvent e)
    {
        var at = LogProjector.ToUtc(e.At, TimeZoneInfo.Local);
        Dictionary<string, string?> d = new() { ["log_file"] = logFile };
        string kind;
        switch (e)
        {
            case RoomJoining j: kind = "joined"; d["world_id"] = j.WorldId; d["instance"] = j.Instance; break;
            case RoomEntered r: kind = "world_name"; d["world_name"] = r.WorldName; break;
            case RoomLeft: kind = "left"; break;
            case PlayerJoined p: kind = "player_joined"; d["name"] = p.Name; d["user_id"] = p.UserId; break;
            case PlayerLeft p: kind = "player_left"; d["name"] = p.Name; d["user_id"] = p.UserId; break;
            case VideoPlayed v: kind = "video"; d["url"] = v.Url; break;
            case ScreenshotTaken s: kind = "screenshot"; d["path"] = s.Path; break;
            case NotificationLogged n: kind = "notification"; d["direction"] = n.Direction; d["type"] = n.Type; d["name"] = n.SenderName; d["user_id"] = n.SenderId; break;
            case SelfIdentified s: kind = "login"; d["name"] = s.Name; d["user_id"] = s.UserId; break;
            case ApplicationQuit: kind = "quit"; break;
            default: return null;
        }
        return new LiveEvent(kind, at, d);
    }
}
