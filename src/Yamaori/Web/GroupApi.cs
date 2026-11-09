using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Yamaori.Store;

namespace Yamaori.Web;

/// <summary>
/// 端末のグループ。まとめ役（hub）が記録を全部持って画面を配り、メンバーは役（logs・photos・vrcx）に従って集めてまとめ役へ送る。
/// <list type="bullet">
/// <item>/api/settings/group …… 本人だけ（作る・招待の合言葉・役・外す・入る・抜ける）</item>
/// <item>/api/group/hello・/api/group/accept …… 認証なし（見つけるための挨拶と、合言葉で入る。合言葉は一度きり・30 分・試せる回数を絞る）</item>
/// <item>/api/sync/group …… メンバーのトークンで、自分の役を読みに来る</item>
/// </list>
/// メンバーとしての送り方は、これまでの「子の端末」と同じ（Role=child・ParentUrl・ParentToken）。
/// </summary>
public static class GroupApi
{
    public sealed record MemberState(string GroupId, string GroupName, string HubUrl, IReadOnlyList<string> Roles, bool TokenReadable);
    public sealed record GroupView(string Mode, GroupInfo? Group, MemberState? Member, string DeviceId, string DeviceName);
    public sealed record CreateInput(string? Name);
    public sealed record RolesInput(IReadOnlyList<string> Roles, string? Name);
    public sealed record JoinInput(string HubUrl, string Code);
    public sealed record AcceptInput(string Code, string DeviceId, string DeviceName, string? Url);
    public sealed record Hello(string App, string Version, string DeviceId, string DeviceName, string? GroupId, string? GroupName, bool IsHub);
    public sealed record GroupSync(string GroupId, string GroupName, IReadOnlyList<string> Roles, IReadOnlyList<GroupMember> Members);
    public sealed record JoinResult(bool Ok, string Message, bool RestartRequired);

    // 合言葉を試せる回数（全体で 10 分に 30 回）
    static readonly Queue<DateTime> Attempts = new();
    static readonly Lock AttemptGate = new();

    static bool AllowAttempt()
    {
        lock (AttemptGate)
        {
            var now = DateTime.UtcNow;
            while (Attempts.Count > 0 && now - Attempts.Peek() > TimeSpan.FromMinutes(10)) Attempts.Dequeue();
            if (Attempts.Count >= 30) return false;
            Attempts.Enqueue(now);
            return true;
        }
    }

    public const string RolesKey = "group_roles";

    /// <summary>この端末の役（メンバーならまとめ役から読んだもの、まとめ役なら自分の行、グループがなければ全部）。</summary>
    public static IReadOnlyList<string> MyRoles(Database db, YamaoriOptions o, string selfDeviceId)
    {
        var groups = new GroupStore(db);
        if (groups.IsHub) return groups.RolesOf(selfDeviceId);
        if (o.Role == "child" && o.GroupId != null && new AuthStore(db).Get(RolesKey) is { } csv) return GroupRoles.Parse(csv);
        return [GroupRoles.Logs, GroupRoles.Photos, GroupRoles.Vrcx];
    }

    static GroupView View(Database db, YamaoriOptions o, YamaoriState state)
    {
        var groups = new GroupStore(db);
        if (groups.IsHub) return new GroupView("hub", groups.Info(state.DeviceId), null, state.DeviceId, state.DeviceName);
        if (o.Role == "child" && o.GroupId != null && o.ParentUrl != null)
            return new GroupView("member", null, new MemberState(o.GroupId, o.GroupName ?? "", o.ParentUrl, GroupRoles.Parse(new AuthStore(db).Get(RolesKey)),
                Photos.Secrets.Unprotect(o.ParentToken) != null), state.DeviceId, state.DeviceName);
        return new GroupView(o.Role == "child" ? "child" : "none", null, null, state.DeviceId, state.DeviceName);
    }

    static YamaoriOptions Current(IOptions<YamaoriOptions> options)
    {
        var o = new YamaoriOptions();
        var path = ConfigFile.PathIn(options.Value.DataDir);
        if (File.Exists(path)) new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddJsonFile(path, optional: true, reloadOnChange: false).Build().Bind(o);
        o.DataDir = options.Value.DataDir;
        return o;
    }

    public static void Map(WebApplication app)
    {
        // ---- 認証なし
        app.MapGet("/api/group/hello", (Database db, YamaoriState state) =>
        {
            var info = new GroupStore(db).Info(state.DeviceId);
            return new Hello("yamaori", typeof(GroupApi).Assembly.GetName().Version?.ToString() ?? "", state.DeviceId, state.DeviceName, info?.Id, info?.Name, info != null);
        }).WithSummary("Yamaori の挨拶（同じ tailnet の Yamaori を見つけるため。認証なし）");

        app.MapPost("/api/group/accept", Results<Ok<GroupJoined>, ProblemHttpResult> (Database db, YamaoriState state, AcceptInput input) =>
        {
            if (!AllowAttempt()) return TypedResults.Problem("合言葉を試す回数が多すぎる。少し待つ", statusCode: StatusCodes.Status429TooManyRequests);
            var joined = new GroupStore(db).Accept(input.Code ?? "", input.DeviceId ?? "", input.DeviceName ?? "", input.Url, state.DeviceId);
            return joined != null ? TypedResults.Ok(joined) : TypedResults.Problem("合言葉が違うか、使い終わったか、時間が切れた", statusCode: StatusCodes.Status403Forbidden);
        }).WithSummary("招待の合言葉で、端末をグループに入れる（まとめ役で。認証なし）");

        // ---- メンバーのトークンで
        app.MapGet("/api/sync/group", Results<Ok<GroupSync>, ProblemHttpResult> (HttpContext ctx, Database db, YamaoriState state) =>
        {
            var groups = new GroupStore(db);
            if (groups.Info(state.DeviceId) is not { } info) return TypedResults.Problem("この Yamaori はまとめ役でない", statusCode: StatusCodes.Status404NotFound);
            var device = Auth.TokenOf(ctx)?.DeviceId ?? ctx.Request.Query["device"].ToString();
            if (string.IsNullOrEmpty(device)) return TypedResults.Problem("端末が分からない", statusCode: StatusCodes.Status400BadRequest);
            groups.Seen(device);
            return TypedResults.Ok(new GroupSync(info.Id, info.Name, groups.RolesOf(device), info.Members));
        }).WithSummary("メンバーが、自分の役とグループを読む");

        // ---- 本人だけ
        var g = app.MapGroup("/api/settings/group");
        g.MapGet("/", (Database db, IOptions<YamaoriOptions> o, YamaoriState state) => View(db, Current(o), state)).WithSummary("グループ（まとめ役ならメンバーと役、メンバーならまとめ役）");

        g.MapPost("/create", Results<Ok<GroupView>, BadRequest<string>> (Database db, IOptions<YamaoriOptions> o, YamaoriState state, CreateInput input) =>
        {
            var cur = Current(o);
            if (cur.Role == "child") return TypedResults.BadRequest("ほかのグループに入っている（先に抜ける）");
            new GroupStore(db).Create(string.IsNullOrWhiteSpace(input.Name) ? "自分の端末" : input.Name, state.DeviceId, state.DeviceName);
            return TypedResults.Ok(View(db, cur, state));
        }).WithSummary("この端末をまとめ役にして、グループを作る");

        g.MapGet("/discover", (YamaoriState state, CancellationToken cancel) => GroupDiscovery.FindAsync(state.DeviceId, cancel))
            .WithSummary("同じ Tailscale のアカウントの端末から、Yamaori を見つける（まとめ役のアドレスを選ぶため）");

        g.MapPost("/invite", Results<Ok<GroupInvite>, BadRequest<string>> (Database db) =>
        {
            try { return TypedResults.Ok(new GroupStore(db).Invite()); }
            catch (InvalidOperationException ex) { return TypedResults.BadRequest(ex.Message); }
        }).WithSummary("招待の合言葉を作る（一度きり・30 分まで）");

        g.MapPut("/members/{deviceId}", Results<Ok<GroupView>, NotFound> (Database db, IOptions<YamaoriOptions> o, YamaoriState state, string deviceId, RolesInput input) =>
            new GroupStore(db).SetRoles(deviceId, input.Roles, input.Name) ? TypedResults.Ok(View(db, Current(o), state)) : TypedResults.NotFound())
            .WithSummary("メンバーの役を変える（logs・photos・vrcx。まとめ役の役は動かせない）");

        g.MapDelete("/members/{deviceId}", Results<Ok<GroupView>, BadRequest<string>> (Database db, IOptions<YamaoriOptions> o, YamaoriState state, string deviceId) =>
            new GroupStore(db).Remove(deviceId) ? TypedResults.Ok(View(db, Current(o), state)) : TypedResults.BadRequest("まとめ役は外せない（見つからないこともある）"))
            .WithSummary("メンバーを外す（渡したトークンを消す。送ってきた記録は残す）");

        g.MapPost("/join", async Task<Results<Ok<JoinResult>, BadRequest<string>>> (Database db, IOptions<YamaoriOptions> o, YamaoriState state, JoinInput input, CancellationToken cancel) =>
        {
            if (new GroupStore(db).IsHub) return TypedResults.BadRequest("この端末はまとめ役（ほかのグループには入れない）");
            if (!(Uri.TryCreate(input.HubUrl?.Trim(), UriKind.Absolute, out var hub) && (hub.Scheme == Uri.UriSchemeHttp || hub.Scheme == Uri.UriSchemeHttps)))
                return TypedResults.BadRequest("まとめ役のアドレスは http://… か https://…");
            using var http = new HttpClient { BaseAddress = new Uri(hub.ToString().TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(20) };
            GroupJoined? joined;
            try
            {
                using var res = await http.PostAsJsonAsync("api/group/accept", new AcceptInput(input.Code ?? "", state.DeviceId, state.DeviceName, null), cancel);
                if (!res.IsSuccessStatusCode) return TypedResults.Ok(new JoinResult(false, $"まとめ役が断った（{(int)res.StatusCode}）: 合言葉を確かめる", false));
                joined = await res.Content.ReadFromJsonAsync<GroupJoined>(cancel);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return TypedResults.Ok(new JoinResult(false, $"まとめ役につながらない: {ex.Message}", false)); }
            if (joined == null) return TypedResults.Ok(new JoinResult(false, "まとめ役の返事が読めない", false));
            ConfigFile.Update(o.Value.DataDir, json =>
            {
                json["Role"] = "child";
                json["ParentUrl"] = hub.ToString().TrimEnd('/');
                json["ParentToken"] = Photos.Secrets.Protect(joined.Token);
                json["GroupId"] = joined.GroupId;
                json["GroupName"] = joined.GroupName;
            });
            new AuthStore(db).Set(RolesKey, string.Join(',', joined.Roles));
            return TypedResults.Ok(new JoinResult(true, $"「{joined.GroupName}」に入った（まとめ役: {joined.HubName}）。起こし直すと送り始める", true));
        }).WithSummary("まとめ役のアドレスと招待の合言葉で、この端末をグループに入れる");

        g.MapPost("/leave", (Database db, IOptions<YamaoriOptions> o) =>
        {
            ConfigFile.Update(o.Value.DataDir, json =>
            {
                json["Role"] = "standalone";
                json.Remove("ParentToken");
                json.Remove("GroupId");
                json.Remove("GroupName");
            });
            new AuthStore(db).Set(RolesKey, null);
            return new JoinResult(true, "グループから抜けた（手元の記録は残す）。起こし直すと送るのをやめる", true);
        }).WithSummary("グループから抜ける（まとめ役で外すのとは別に、この端末の設定を消す）");
    }
}
