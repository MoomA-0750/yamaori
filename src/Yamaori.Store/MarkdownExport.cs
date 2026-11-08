using System.Globalization;
using System.Text;

namespace Yamaori.Store;

/// <summary>
/// 記録とメモを Markdown に書き出す。日ごとに 1 つの文書（見出し、訪問の一覧、その日の日記、訪問のメモ）。
/// ほかのアプリ（Obsidian など）へ持っていくための書き出しで、Yamaori の正は DB のまま。
/// </summary>
public sealed class MarkdownExport(Queries queries, TimeZoneInfo? timeZone = null)
{
    readonly TimeZoneInfo _tz = timeZone ?? TimeZoneInfo.Local;

    /// <summary>その日の文書。</summary>
    public string Day(DateOnly date)
    {
        var key = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var visits = queries.Day(date);
        var b = new StringBuilder();
        b.Append("---\ndate: ").Append(key).Append("\nsource: yamaori\n---\n\n");
        b.Append("# ").Append(key).Append("\n\n");
        foreach (var n in queries.Notes("day", key)) b.Append(n.Body.Trim()).Append("\n\n");
        if (visits.Count > 0)
        {
            b.Append("## 行ったところ\n\n");
            foreach (var v in visits)
            {
                var name = v.WorldName ?? v.WorldId;
                b.Append("- ").Append(Local(v.JoinedAt)).Append('–').Append(v.LeftAt is { } l ? Local(l) : "").Append(' ').Append(Escape(name));
                var extras = new List<string>();
                if (v.PeopleCount > 0) extras.Add($"{v.PeopleCount}人");
                if (v.VideoCount > 0) extras.Add($"動画 {v.VideoCount}");
                if (v.ScreenshotCount > 0) extras.Add($"写真 {v.ScreenshotCount}");
                if (extras.Count > 0) b.Append("（").Append(string.Join("・", extras)).Append('）');
                b.Append('\n');
                foreach (var n in queries.Notes("visit", v.Id))
                    foreach (var line in n.Body.Trim().Split('\n')) b.Append("    ").Append(line).Append('\n');
            }
            b.Append('\n');
        }
        return b.ToString();
    }

    /// <summary>期間の全部の日を、日ごとの文書の (ファイル名, 中身) で返す（古い順）。</summary>
    public IEnumerable<(string FileName, string Content)> Days(DateOnly? from = null, DateOnly? to = null)
    {
        foreach (var d in queries.Days(from, to).OrderBy(d => d.Date, StringComparer.Ordinal))
        {
            var date = DateOnly.ParseExact(d.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            yield return ($"{d.Date}.md", Day(date));
        }
    }

    string Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, _tz).ToString("HH:mm", CultureInfo.InvariantCulture);

    static string Escape(string s) => s.Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal);
}
