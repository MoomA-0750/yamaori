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

        api.MapGet("/worlds", (Queries queries, string? q, string? sort, int? limit, int? offset) => queries.Worlds(q, sort ?? "last", limit ?? 50, offset ?? 0))
            .WithSummary("ワールドの一覧（q で名前を検索、sort は last・visits・minutes・name）");

        api.MapGet("/worlds/{id}", Results<Ok<WorldDetail>, NotFound> (Queries q, string id) =>
            q.World(id) is { } w ? TypedResults.Ok(w) : TypedResults.NotFound())
            .WithSummary("ワールドの詳細（訪問・メモ）");

        api.MapGet("/people", (Queries queries, string? q, string? sort, int? limit, int? offset) => queries.People(q, sort ?? "last", limit ?? 50, offset ?? 0))
            .WithSummary("会った人の一覧（q で名前を検索。前の名前も見る）");

        api.MapGet("/people/{id}", Results<Ok<PersonDetail>, NotFound> (Queries q, string id) =>
            q.Person(id) is { } p ? TypedResults.Ok(p) : TypedResults.NotFound())
            .WithSummary("人の詳細（名前の移り変わり・一緒にいた訪問・メモ）");

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
            if (!q.TargetExists(input.TargetType, input.TargetId)) return TypedResults.BadRequest("付け先が見つからない");
            var note = q.AddNote(input.TargetType, input.TargetId, input.Body ?? "", state.DeviceId);
            return TypedResults.Created($"/api/notes/{note.Id}", note);
        }).WithSummary("メモを書く（訪問・ワールド・人・日に付ける。本文は Markdown）");

        api.MapPut("/notes/{id}", Results<Ok<Note>, NotFound> (Queries q, string id, NoteUpdate input) =>
            q.UpdateNote(id, input.Body ?? "") is { } n ? TypedResults.Ok(n) : TypedResults.NotFound())
            .WithSummary("メモを書き換える");

        api.MapDelete("/notes/{id}", Results<NoContent, NotFound> (Queries q, string id) =>
            q.DeleteNote(id) ? TypedResults.NoContent() : TypedResults.NotFound())
            .WithSummary("メモを消す");

        // ---- 今の出来事

        api.MapGet("/stream", (LiveHub hub, CancellationToken cancel) =>
            TypedResults.ServerSentEvents(Stream(hub, cancel)))
            .WithSummary("今の出来事を SSE で流す（入った・出た・人が来た・動画・スクショ・通知）");
    }

    public sealed record DayView(string Date, IReadOnlyList<VisitSummary> Visits, IReadOnlyList<Note> Notes);

    static async IAsyncEnumerable<SseItem<LiveEvent>> Stream(LiveHub hub, [EnumeratorCancellation] CancellationToken cancel)
    {
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
