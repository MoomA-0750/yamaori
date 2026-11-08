using System.Text.Json.Serialization;

namespace Yamaori.Core.Log;

/// <summary>
/// ログの 1 件から読み取った出来事。At はログに書かれたローカル時刻のまま（UTC への直しは組み立てるときに行う）。
/// 型の名前（kind）は DB の log_events に残るので、変えるときは読み直しが要る。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SelfIdentified), "self")]
[JsonDerivedType(typeof(DestinationFetching), "destination")]
[JsonDerivedType(typeof(RoomJoining), "joining")]
[JsonDerivedType(typeof(RoomEntered), "entered")]
[JsonDerivedType(typeof(RoomLeft), "left")]
[JsonDerivedType(typeof(PlayerJoined), "player_joined")]
[JsonDerivedType(typeof(PlayerLeft), "player_left")]
[JsonDerivedType(typeof(VideoPlayed), "video")]
[JsonDerivedType(typeof(AvatarChanged), "avatar")]
[JsonDerivedType(typeof(NotificationLogged), "notification")]
[JsonDerivedType(typeof(ScreenshotTaken), "screenshot")]
[JsonDerivedType(typeof(ApplicationQuit), "quit")]
[JsonDerivedType(typeof(VrModeDetected), "vr_mode")]
public abstract record LogEvent
{
    public DateTime At { get; init; }
}

/// <summary>本人（ログインしたとき）。</summary>
public sealed record SelfIdentified(string Name, string UserId) : LogEvent;

/// <summary>移動を始めた（行き先のインスタンス）。</summary>
public sealed record DestinationFetching(string Location) : LogEvent;

/// <summary>インスタンスに入った（「wrld_…:番号~…」）。</summary>
public sealed record RoomJoining(string WorldId, string Instance) : LogEvent;

/// <summary>入ったワールドの名前（Entering Room・Joining or Creating Room）。今の VRChat では Joining より先に出る。</summary>
public sealed record RoomEntered(string WorldName) : LogEvent;

/// <summary>インスタンスを出た。</summary>
public sealed record RoomLeft : LogEvent;

/// <summary>人が来た（自分の分も出る）。古い版では UserId がない。</summary>
public sealed record PlayerJoined(string Name, string? UserId) : LogEvent;

public sealed record PlayerLeft(string Name, string? UserId) : LogEvent;

/// <summary>動画のプレイヤーが URL を読みに行った。</summary>
public sealed record VideoPlayed(string Url) : LogEvent;

public sealed record AvatarChanged(string Name, string AvatarName) : LogEvent;

/// <summary>
/// 通知。Direction は received（受けた）・sent（送った）・accepted（承諾した）・answered（答えた）。
/// 同じ通知が何度も出るので、NotificationId で重ねる。
/// </summary>
public sealed record NotificationLogged(
    string Direction,
    string Type,
    string NotificationId,
    string? SenderName,
    string? SenderId,
    string? ReceiverId,
    DateTime? CreatedUtc,
    string? Details,
    string? Message) : LogEvent;

public sealed record ScreenshotTaken(string Path) : LogEvent;

public sealed record ApplicationQuit : LogEvent;

/// <summary>VR で起こしたか（Hmd は機種の名前。機器の番号は持たない）。</summary>
public sealed record VrModeDetected(bool Vr, string? Hmd) : LogEvent;
