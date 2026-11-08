using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Server;
using Yamaori.Store;

namespace Yamaori.Web;

/// <summary>
/// AI のエージェント向けの口（MCP、/mcp）。記録を引き、メモを書ける。
/// 返すのは JSON の文字列（時刻は UTC）。入口の見張りは REST API と同じ（外からはトークンが要る）。
/// </summary>
[McpServerToolType]
public sealed class McpTools(Queries queries, YamaoriState state)
{
    // 日本語などを \u… に逃がさずそのまま出す（読み手は AI なので、短く読みやすい方がよい）
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static string Out(object? o) => JsonSerializer.Serialize(o, Json);

    [McpServerTool(Name = "list_days", ReadOnly = true), Description("VRChat で遊んだ日の一覧（新しい順）。日ごとの訪問の数・遊んだ分・ワールドの数・会った人の数・日記の数。")]
    public string ListDays([Description("この日から（yyyy-MM-dd、任意）")] string? from = null, [Description("この日まで（yyyy-MM-dd、任意）")] string? to = null) =>
        Out(queries.Days(ParseDate(from), ParseDate(to)));

    [McpServerTool(Name = "get_day", ReadOnly = true), Description("その日の訪問（古い順）と、その日の日記。")]
    public string GetDay([Description("日付（yyyy-MM-dd）")] string date)
    {
        if (ParseDate(date) is not { } d) return Out(new { error = "日付は yyyy-MM-dd" });
        return Out(new { date, visits = queries.Day(d), notes = queries.Notes("day", date) });
    }

    [McpServerTool(Name = "search_visits", ReadOnly = true), Description("訪問を探す（新しい順）。ワールド・一緒にいた人・期間で絞れる。")]
    public string SearchVisits(
        [Description("ワールドの ID（wrld_…、任意）")] string? worldId = null,
        [Description("一緒にいた人のユーザー ID（usr_…、任意）")] string? userId = null,
        [Description("この時刻から（ISO 8601、任意）")] string? from = null,
        [Description("この時刻まで（ISO 8601、任意）")] string? to = null,
        [Description("最大の件数（既定 20）")] int limit = 20) =>
        Out(queries.Visits(ParseTime(from), ParseTime(to), worldId, userId, limit: limit));

    [McpServerTool(Name = "get_visit", ReadOnly = true), Description("訪問の詳細。いた人と出入りの時刻、流れた動画、撮った写真、通知、メモ。")]
    public string GetVisit([Description("訪問の ID（v_…）")] string id) => Out(queries.Visit(id) ?? (object)new { error = "見つからない" });

    [McpServerTool(Name = "search_worlds", ReadOnly = true), Description("行ったワールドを探す。名前で検索でき、行った回数・いた分・最後に行った時刻が付く。")]
    public string SearchWorlds(
        [Description("名前の一部（任意）")] string? query = null,
        [Description("並べ方: last（最後に行った順）・visits・minutes・name")] string sort = "last",
        [Description("最大の件数（既定 20）")] int limit = 20) =>
        Out(queries.Worlds(query, sort, limit));

    [McpServerTool(Name = "get_world", ReadOnly = true), Description("ワールドの詳細。行ったときの一覧とメモ。")]
    public string GetWorld([Description("ワールドの ID（wrld_…）")] string id) => Out(queries.World(id) ?? (object)new { error = "見つからない" });

    [McpServerTool(Name = "search_people", ReadOnly = true), Description("会った人を探す。名前の検索は前の名前も見る。")]
    public string SearchPeople(
        [Description("名前の一部（任意）")] string? query = null,
        [Description("並べ方: last（最後に会った順）・visits・name")] string sort = "last",
        [Description("最大の件数（既定 20）")] int limit = 20) =>
        Out(queries.People(query, sort, limit));

    [McpServerTool(Name = "get_person", ReadOnly = true), Description("人の詳細。名前の移り変わり、一緒にいた訪問、メモ。")]
    public string GetPerson([Description("ユーザー ID（usr_…）")] string id) => Out(queries.Person(id) ?? (object)new { error = "見つからない" });

    [McpServerTool(Name = "add_note"), Description("メモ・日記を書き足す。訪問・ワールド・人・日のどれかに付ける。本文は Markdown。")]
    public string AddNote(
        [Description("付け先の種類: visit・world・person・day")] string targetType,
        [Description("付け先の ID（訪問の v_…、wrld_…、usr_…、日なら yyyy-MM-dd）")] string targetId,
        [Description("本文（Markdown）")] string body)
    {
        if (!Queries.TargetTypes.Contains(targetType)) return Out(new { error = "targetType は visit・world・person・day" });
        if (!queries.TargetExists(targetType, targetId)) return Out(new { error = "付け先が見つからない" });
        return Out(queries.AddNote(targetType, targetId, body, state.DeviceId));
    }

    [McpServerTool(Name = "update_note"), Description("メモの本文を書き換える。")]
    public string UpdateNote([Description("メモの ID（n_…）")] string id, [Description("新しい本文（Markdown）")] string body) =>
        Out(queries.UpdateNote(id, body) ?? (object)new { error = "見つからない" });

    static DateOnly? ParseDate(string? s) =>
        s != null && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    static DateTime? ParseTime(string? s) =>
        s != null && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;
}
