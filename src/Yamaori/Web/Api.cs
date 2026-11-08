using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http.HttpResults;
using Yamaori.Store;

namespace Yamaori.Web;

/// <summary>REST API（/api/…）。定義は /openapi/v1.json。</summary>
public static class Api
{
    public sealed record NoteInput(string TargetType, string TargetId, string Body);
    public sealed record NoteUpdate(string Body);
    public sealed record Status(string Version, string DeviceId, string DeviceName, string Listen, string TimeZone, DateTime Now);

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/status", (Database db, YamaoriState state) =>
            new Status(typeof(Api).Assembly.GetName().Version?.ToString() ?? "", state.DeviceId, state.DeviceName, state.Listen, TimeZoneInfo.Local.Id, DateTime.UtcNow))
            .WithSummary("Yamaori の状態");

        api.MapGet("/days", (Queries q, DateOnly? from, DateOnly? to) => q.Days(from, to))
            .WithSummary("日ごとのまとめ（新しい順）");

        api.MapGet("/days/{date}", Results<Ok<DayView>, BadRequest<string>> (Queries q, string date) =>
        {
            if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return TypedResults.BadRequest("日付は yyyy-MM-dd");
            return TypedResults.Ok(new DayView(date, q.Day(d), q.Notes("day", date)));
        }).WithSummary("その日の訪問とメモ");

        api.MapGet("/visits", (Queries q, DateTime? from, DateTime? to, string? world, string? person, string? account, string? device, int? limit, int? offset) =>
            q.Visits(Utc(from), Utc(to), world, person, account, device, limit ?? 50, offset ?? 0))
            .WithSummary("訪問の一覧（新しい順）");

        api.MapGet("/visits/{id}", Results<Ok<VisitDetail>, NotFound> (Queries q, string id) =>
            q.Visit(id) is { } v ? TypedResults.Ok(v) : TypedResults.NotFound())
            .WithSummary("訪問の詳細（会った人・出来事・メモ）");

        api.MapGet("/events", (Queries q, string? kind, DateTime? from, DateTime? to, int? limit, int? offset) =>
            q.Events(kind, Utc(from), Utc(to), limit ?? 100, offset ?? 0))
            .WithSummary("出来事の一覧（動画・通知・スクショ・アバター）");

        api.MapGet("/worlds", (Queries queries, string? q, string? sort, int? limit, int? offset, string? list) => queries.Worlds(q, sort ?? "last", limit ?? 50, offset ?? 0, list))
            .WithSummary("ワールドの一覧（q で名前を検索、sort は last・visits・minutes・name・saved、list でお気に入り・保存の一覧に絞る。* はどれか）");

        api.MapGet("/worlds/lists", (Queries queries) => queries.FavoriteLists().Select(x => new FavoriteList(x.Name, x.Count)))
            .WithSummary("お気に入り・保存の一覧の名前と数");

        api.MapPost("/worlds/save", Results<Ok<SavedWorld>, BadRequest<string>> (Queries queries, SaveWorldInput input) =>
            queries.SaveWorld(string.Join(" ", new[] { input.Url, input.Text, input.WorldId }.Where(x => x != null)), input.Name, input.List ?? "保存") is { } id
                ? TypedResults.Ok(new SavedWorld(id, input.List ?? "保存"))
                : TypedResults.BadRequest("ワールドの ID（wrld_…）が見つからない"))
            .WithSummary("ワールドを保存の一覧に入れる（スマホの共有・ショートカットから。url・text・worldId のどれかに wrld_… があればよい）");

        api.MapGet("/worlds/{id}", Results<Ok<WorldDetail>, NotFound> (Queries q, string id) =>
            q.World(id) is { } w ? TypedResults.Ok(w) : TypedResults.NotFound())
            .WithSummary("ワールドの詳細（訪問・メモ）");

        api.MapGet("/people", (Queries queries, string? q, string? sort, int? limit, int? offset, string? kind) => queries.People(q, sort ?? "last", limit ?? 50, offset ?? 0, kind ?? "all"))
            .WithSummary("会った人の一覧（q で名前を検索。前の名前も見る。kind は all・id・name。名前だけの人の ID は name:名前）");

        api.MapGet("/people/{id}", Results<Ok<PersonDetail>, NotFound> (Queries q, string id) =>
            q.Person(id) is { } p ? TypedResults.Ok(p) : TypedResults.NotFound())
            .WithSummary("人の詳細（名前の移り変わり・一緒にいた訪問・メモ）");

        api.MapPost("/people/{id}/forget", Results<Ok<ForgetResult>, BadRequest<string>> (Database db, string id, ForgetInput? input) =>
        {
            try { return TypedResults.Ok(new ForgetResult(new ForgetStore(db).Forget(id, input?.Label))); }
            catch (ArgumentException ex) { return TypedResults.BadRequest(ex.Message); }
        }).WithSummary("人を忘れる（会った記録・出来事・メモを消し、あとから読み直しても入れない。生のログには残る）");

        api.MapGet("/people/forgotten", (Database db) => new ForgetStore(db).List()).WithSummary("忘れた人");

        api.MapDelete("/people/forgotten/{key}", Results<NoContent, NotFound> (Database db, string key) =>
            new ForgetStore(db).Unforget(key) ? TypedResults.NoContent() : TypedResults.NotFound())
            .WithSummary("忘れるのをやめる（消した記録は、次に読み直す・取り込み直すと戻る。メモは戻らない）");

        api.MapGet("/accounts", (Queries q) => q.Accounts()).WithSummary("自分が遊んだアカウント");
        api.MapGet("/devices", (Queries q) => q.Devices()).WithSummary("記録を集めた端末");

        // ---- メモ

        api.MapGet("/notes", (Queries q, string? targetType, string? targetId) => q.Notes(targetType, targetId))
            .WithSummary("メモの一覧");

        api.MapGet("/notes/{id}", Results<Ok<Note>, NotFound> (Queries q, string id) =>
            q.Note(id) is { } n ? TypedResults.Ok(n) : TypedResults.NotFound());

        api.MapPost("/notes", Results<Created<Note>, BadRequest<string>> (Queries q, YamaoriState state, NoteInput input) =>
        {
            if (!Queries.TargetTypes.Contains(input.TargetType)) return TypedResults.BadRequest("targetType は visit・world・person・day");
            if ((input.Body ?? "").Length > MaxNote) return TypedResults.BadRequest($"メモは {MaxNote} 文字まで");
            if (!q.TargetExists(input.TargetType, input.TargetId)) return TypedResults.BadRequest("付け先が見つからない");
            var note = q.AddNote(input.TargetType, input.TargetId, input.Body ?? "", state.DeviceId);
            return TypedResults.Created($"/api/notes/{note.Id}", note);
        }).WithSummary("メモを書く（訪問・ワールド・人・日に付ける。本文は Markdown）");

        api.MapPut("/notes/{id}", Results<Ok<Note>, NotFound, BadRequest<string>> (Queries q, string id, NoteUpdate input) =>
            (input.Body ?? "").Length > MaxNote ? TypedResults.BadRequest($"メモは {MaxNote} 文字まで")
            : q.UpdateNote(id, input.Body ?? "") is { } n ? TypedResults.Ok(n) : TypedResults.NotFound())
            .WithSummary("メモを書き換える");

        api.MapDelete("/notes/{id}", Results<NoContent, NotFound> (Queries q, string id) =>
            q.DeleteNote(id) ? TypedResults.NoContent() : TypedResults.NotFound())
            .WithSummary("メモを消す");

        // ---- 子の端末から（親で受ける）

        api.MapPost("/sync/projection", Results<NoContent, BadRequest<string>, ProblemHttpResult> (HttpContext ctx, Database db, SyncProjection input) =>
        {
            // 子の端末のトークンは、最初に送ってきた端末に結び付け、ほかの端末の記録は入れ替えさせない
            if (Auth.TokenOf(ctx) is { Scope: TokenScopes.Sync } t && !new AuthStore(db).BindTokenDevice(t.Id, input.DeviceId))
                return TypedResults.Problem("このトークンは、ほかの端末に結び付いている", statusCode: StatusCodes.Status403Forbidden);
            try { new SyncStore(db).Apply(input); }
            catch (ArgumentException ex) { return TypedResults.BadRequest(ex.Message); }
            return TypedResults.NoContent();
        }).WithSummary("子の端末から、出どころ 1 つ分の記録を受け取って入れ替える");

        api.MapPost("/sync/notes", (HttpContext ctx, Database db, IReadOnlyList<SyncNote> notes) =>
            {
                notes = notes.Where(x => x.Body.Length <= MaxNote).Take(10_000).ToList();
                var only = Auth.TokenOf(ctx) is { Scope: TokenScopes.Sync } t ? t.DeviceId ?? "" : null;
                return new { applied = new SyncStore(db).ApplyNotes(notes, only) };
            })
            .WithSummary("子の端末から、メモを受け取る（後から直した方を残す）");

        // ---- 拡張機能から

        api.MapPut("/worlds/{id}/details", Results<NoContent, BadRequest<string>> (Database db, string id, WorldDetailsInput input) =>
        {
            if (!id.StartsWith("wrld_", StringComparison.Ordinal)) return TypedResults.BadRequest("ワールドの ID は wrld_…");
            new ExtensionStore(db).SaveWorld(id, input);
            return TypedResults.NoContent();
        }).WithSummary("ワールドの詳しい情報を入れる（拡張機能が vrchat.com のページから取ったもの）");

        api.MapGet("/worlds/missing-details", (Database db, int? limit) => new ExtensionStore(db).MissingWorlds(limit ?? 20))
            .WithSummary("詳しい情報のないワールド（行った回数の多い順。拡張機能がまとめて取るときに使う）");

        api.MapPost("/friends/snapshot", Results<Ok<FriendSnapshotResult>, BadRequest<string>> (Database db, FriendSnapshotInput input) =>
        {
            var account = input.AccountId;
            if (account == null)
            {
                var accounts = new Queries(db).Accounts();
                if (accounts.Count != 1) return TypedResults.BadRequest("アカウントが 1 つに決まらないので accountId を渡す");
                account = accounts[0].Id;
            }
            if (input.Friends is { Count: > 10_000 }) return TypedResults.BadRequest("フレンドは 10000 人まで");
            var friends = (input.Friends ?? []).Where(x => x.Id is { Length: > 0 and <= 64 })
                .Select(x => x with { DisplayName = x.DisplayName is { Length: > 200 } dn ? dn[..200] : x.DisplayName }).ToList();
            return TypedResults.Ok(new ExtensionStore(db).SaveFriends(account, friends));
        }).WithSummary("フレンドの一覧を入れ、前に入れた一覧と比べて増えた・減った・名前を変えた人を記録する（拡張機能から）");

        // ---- 写真

        api.MapGet("/photos", (PhotoCatalog photos, DateTime? from, DateTime? to, string? world, string? visit, int? limit, int? offset) =>
            photos.List(Utc(from), Utc(to), world, visit, limit ?? 100, offset ?? 0))
            .WithSummary("写真の一覧（新しい順。visit を渡すと、その訪問の間に撮った写真を古い順に）");

        api.MapGet("/photos/{id}", Results<Ok<PhotoView>, NotFound> (PhotoCatalog photos, string id) =>
            photos.Get(id) is { } p ? TypedResults.Ok(p) : TypedResults.NotFound())
            .WithSummary("写真のメタデータ（撮った時刻・ワールド・撮った人・大きさ）");

        api.MapGet("/photos/{id}/thumb", async Task<Results<PhysicalFileHttpResult, NotFound>> (PhotoCatalog photos, Thumbnails thumbs, string id, int? size, CancellationToken cancel) =>
        {
            if (photos.FindFile(id) is not { } file) return TypedResults.NotFound();
            var path = await thumbs.GetAsync(id, file, size ?? 320, cancel).ConfigureAwait(false);
            return path == null ? TypedResults.NotFound() : TypedResults.PhysicalFile(path, "image/jpeg", enableRangeProcessing: false);
        }).WithSummary("写真の縮小（size は 320・800・1600 のうち、それ以上でいちばん小さいもの）");

        api.MapGet("/photos/{id}/file", Results<PhysicalFileHttpResult, NotFound> (PhotoCatalog photos, string id) =>
        {
            if (photos.FindFile(id) is not { } file) return TypedResults.NotFound();
            var type = Path.GetExtension(file).ToLowerInvariant() switch { ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg" };
            return TypedResults.PhysicalFile(file, type, Path.GetFileName(file), enableRangeProcessing: true);
        }).WithSummary("写真のもとのファイル");

        // ---- 書き出し

        api.MapGet("/export/markdown", (Queries q, DateOnly? from, DateOnly? to) =>
        {
            var text = string.Join("\n", new MarkdownExport(q).Days(from, to).Select(x => x.Content));
            return TypedResults.Text(text, "text/markdown; charset=utf-8");
        }).WithSummary("日記を Markdown で書き出す（期間の日を古い順に 1 つの文書に。日ごとのファイルは yamaori export で）");

        // ---- 今の出来事

        api.MapGet("/stream", (LiveHub hub, IHostApplicationLifetime life, CancellationToken cancel) =>
        {
            // 相手が切ったときだけでなく、Yamaori を止めるときにも閉じる（閉じないと止まるのを待たせる）
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, life.ApplicationStopping);
            return TypedResults.ServerSentEvents(Stream(hub, linked));
        })
            .WithSummary("今の出来事を SSE で流す（入った・出た・人が来た・動画・スクショ・通知）");
    }

    public sealed record SaveWorldInput(string? Url, string? Text, string? WorldId, string? Name, string? List);
    public sealed record SavedWorld(string WorldId, string List);
    public sealed record FavoriteList(string Name, int Count);
    public sealed record ForgetInput(string? Label);
    public sealed record ForgetResult(int Removed);

    /// <summary>メモの長さの上限（文字）。</summary>
    public const int MaxNote = 100_000;

    public sealed record FriendSnapshotInput(string? AccountId, IReadOnlyList<FriendInput>? Friends);
    public sealed record DayView(string Date, IReadOnlyList<VisitSummary> Visits, IReadOnlyList<Note> Notes);

    static async IAsyncEnumerable<SseItem<LiveEvent>> Stream(LiveHub hub, CancellationTokenSource linked, [EnumeratorCancellation] CancellationToken enumeration = default)
    {
        using var _ = linked;
        using var both = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, enumeration);
        var cancel = both.Token;
        var reader = hub.Subscribe(cancel);
        yield return new SseItem<LiveEvent>(new LiveEvent("hello", DateTime.UtcNow, new Dictionary<string, string?>()), "hello");
        while (!cancel.IsCancellationRequested)
        {
            LiveEvent e;
            try
            {
                if (!await reader.WaitToReadAsync(cancel).ConfigureAwait(false)) yield break;
                if (!reader.TryRead(out e!)) continue;
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            yield return new SseItem<LiveEvent>(e, e.Kind);
        }
    }

    static DateTime? Utc(DateTime? t) => t?.Kind switch
    {
        null => null,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(t.Value, DateTimeKind.Utc),
        _ => t.Value.ToUniversalTime(),
    };
}

/// <summary>Web の側から見える、この Yamaori の状態。</summary>
public sealed class YamaoriState
{
    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public required string Listen { get; init; }
}
