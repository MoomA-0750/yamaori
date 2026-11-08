using System.Text;
using Yamaori.Core;
using Yamaori.Core.Log;

namespace Yamaori.Tests;

public class LogTests
{
    static readonly TimeZoneInfo Tokyo = TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST");
    const string FileName = "output_log_2026-11-08_02-36-41.txt";

    static byte[] Fixture() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", FileName));

    static Projection ProjectAll(bool closed)
    {
        using var s = new MemoryStream(Fixture());
        var r = LogReader.Read(s, 0, final: closed);
        var events = r.Entries.Select(e => (e.Offset, Event: LineParser.Parse(e))).Where(x => x.Event != null).Select(x => (x.Offset, x.Event!));
        return LogProjector.Project("d_test", FileName, events, Tokyo, r.Entries[^1].LocalTime, closed);
    }

    [Fact]
    public void 続きの行は前の件の本文に入る()
    {
        using var s = new MemoryStream(Fixture());
        var r = LogReader.Read(s, 0, final: true);
        var n = r.Entries.Select(LineParser.Parse).OfType<NotificationLogged>().First();
        Assert.Equal("お知らせの一行目\n二行目", n.Message);
        Assert.Equal("group", n.Type);
        Assert.Null(n.SenderId);
        Assert.Equal(new DateTime(2026, 11, 7, 14, 11, 3, DateTimeKind.Utc), n.CreatedUtc);
    }

    [Fact]
    public void 閉じていないログの最後の件は返さず_続きから読める()
    {
        var bytes = Fixture();
        using var s = new MemoryStream(bytes);
        var first = LogReader.Read(s, 0, final: false);
        Assert.DoesNotContain(first.Entries, e => e.Body.Contains("Destroying Carol"));
        var rest = LogReader.Read(s, first.NextOffset, final: true);
        Assert.Single(rest.Entries);
        Assert.Contains("Destroying Carol", rest.Entries[0].Body);

        var all = LogReader.Read(new MemoryStream(bytes), 0, final: true);
        Assert.Equal(all.Entries.Count, first.Entries.Count + rest.Entries.Count);
    }

    [Fact]
    public void 書きかけの行は読まない()
    {
        var text = "2026.11.08 02:36:53 Debug      -  [Behaviour] OnLeftRoom\n\n2026.11.08 02:36:54 Debug      -  [Behaviour] Joining wr";
        using var s = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var r = LogReader.Read(s, 0, final: false);
        Assert.Single(r.Entries);
        Assert.Equal(text.IndexOf("\n\n2026", StringComparison.Ordinal) + 2, r.NextOffset);
    }

    [Fact]
    public void 訪問と出た時刻()
    {
        var p = ProjectAll(closed: true);
        Assert.Equal(2, p.Visits.Count);
        var a = p.Visits[0];
        Assert.Equal("Cushion Room", a.WorldName);
        Assert.Equal("invite", a.Access);
        Assert.Equal("jp", a.Region);
        Assert.Equal(new DateTime(2026, 11, 7, 17, 36, 53, DateTimeKind.Utc), a.JoinedAt);
        Assert.Equal(new DateTime(2026, 11, 7, 17, 40, 48, DateTimeKind.Utc), a.LeftAt);
        Assert.Equal("left", a.LeftReason);

        var b = p.Visits[1];
        Assert.Equal("Evermore - 彼方", b.WorldName);
        Assert.Equal("group_public", b.Access);
        Assert.Equal("grp_00000000-0000-0000-0000-000000000001", b.GroupId);
        Assert.Equal("log_end", b.LeftReason);
        Assert.Equal(new DateTime(2026, 11, 7, 17, 45, 15, DateTimeKind.Utc), b.LeftAt);

        Assert.Equal("log_end", p.Session!.EndReason);
        Assert.Equal("usr_00000000-0000-0000-0000-000000000001", p.Session!.SelfId);
        Assert.True(p.Session!.Vr);
        Assert.Equal("Meta Quest 3", p.Session!.Hmd);
    }

    [Fact]
    public void 閉じていなければ最後の訪問は続いている()
    {
        var p = ProjectAll(closed: false);
        Assert.Null(p.Visits[^1].LeftAt);
        Assert.Null(p.Session!.EndedAt);
        Assert.Null(p.Encounters.Single(e => e.Name == "Carol").LeftAt);
    }

    [Fact]
    public void 出会いは自分を除き_出入りごとに分かれる()
    {
        var p = ProjectAll(closed: true);
        var alice = p.Encounters.Where(e => e.Name == "Alice").OrderBy(e => e.JoinedAt).ToList();
        Assert.Equal(2, alice.Count);
        Assert.Equal(new DateTime(2026, 11, 7, 17, 38, 0, DateTimeKind.Utc), alice[0].LeftAt);
        Assert.Equal(new DateTime(2026, 11, 7, 17, 40, 48, DateTimeKind.Utc), alice[1].LeftAt); // 自分が出た時刻で閉じる
        Assert.DoesNotContain(p.Encounters, e => e.Name == "自分。");
        Assert.Equal(p.Visits[1].Id, p.Encounters.Single(e => e.Name == "Carol").VisitId);
    }

    [Fact]
    public void 出来事_通知は重ねず_アバターは自分だけ()
    {
        var p = ProjectAll(closed: true);
        Assert.Equal(2, p.Events.Count(e => e.Kind == "notification"));
        var friend = p.Events.Single(e => e.Kind == "notification" && e.Data["type"] == "friendRequest");
        Assert.Equal("Bob 7ad1", friend.Name);
        Assert.Equal(p.Visits[0].Id, friend.VisitId);
        Assert.Equal("Salt", p.Events.Single(e => e.Kind == "avatar").Data["avatar"]);
        Assert.Equal("https://youtu.be/example", p.Events.Single(e => e.Kind == "video").Data["url"]);
        var shot = p.Events.Single(e => e.Kind == "screenshot");
        Assert.EndsWith("1920x1080.png", shot.Data["path"]);
        Assert.Equal(p.Visits[1].Id, shot.VisitId);
    }

    [Fact]
    public void 何度組み立てても同じID()
    {
        var a = ProjectAll(closed: true);
        var b = ProjectAll(closed: true);
        Assert.Equal(a.Visits.Select(v => v.Id), b.Visits.Select(v => v.Id));
        Assert.Equal(a.Encounters.Select(v => v.Id), b.Encounters.Select(v => v.Id));
    }

    static IEnumerable<(long, LogEvent)> Events(params LogEvent[] events) => events.Select((e, i) => ((long)i, e));
    static DateTime T(int h, int m) => new(2026, 11, 8, h, m, 0);

    [Fact]
    public void 起こしたままアカウントを切り替えると_そこから先はそのアカウントの訪問()
    {
        var p = LogProjector.Project("d_test", FileName, Events(
            new SelfIdentified("Main", "usr_main") { At = T(1, 0) },
            new RoomJoining("wrld_a", "1~region(jp)") { At = T(1, 1) },
            new RoomLeft { At = T(1, 30) },
            new SelfIdentified("Alt", "usr_alt") { At = T(1, 40) },
            new RoomJoining("wrld_b", "2~region(jp)") { At = T(1, 41) },
            new PlayerJoined("Main", "usr_main") { At = T(1, 42) }), Tokyo, T(2, 0), true);
        Assert.Equal(["usr_main", "usr_alt"], p.Visits.Select(v => v.AccountId));
        // 切り替えたあとは、前のアカウントもほかの人として会った人に入る
        Assert.Equal("usr_main", Assert.Single(p.Encounters).UserId);
    }

    [Theory]
    [InlineData("12976~private(usr_x)~canRequestInvite~region(jp)", "invite_plus", "jp")]
    [InlineData("1~hidden(usr_x)~region(eu)", "friends_plus", "eu")]
    [InlineData("1~friends(usr_x)", "friends", "us")]
    [InlineData("64725~region(jp)", "public", "jp")]
    [InlineData("2~group(grp_x)~groupAccessType(plus)~region(jp)", "group_plus", "jp")]
    public void インスタンスの種類(string instance, string access, string region)
    {
        var i = InstanceInfo.Parse(instance);
        Assert.Equal(access, i.Access);
        Assert.Equal(region, i.Region);
    }
}
