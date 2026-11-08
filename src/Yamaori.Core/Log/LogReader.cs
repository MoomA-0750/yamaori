using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Yamaori.Core.Log;

/// <summary>
/// ログの 1 件。VRChat のログは「時刻 レベル - 本文」で始まり、本文が次の行へ続くことがある（通知の本文など）。
/// Offset はその件の頭のバイト位置で、同じ件を 2 度入れないための目印にする。
/// </summary>
public sealed record LogEntry(long Offset, DateTime LocalTime, string Level, string Body);

/// <summary>
/// output_log_*.txt を、前に読んだ位置から読み進めて件に分ける。
/// 最後の件は、次の件の頭が来るか、ログが閉じた（final）と分かるまで返さない（続きの行が来るかもしれないため）。
/// </summary>
public static partial class LogReader
{
    [GeneratedRegex(@"^(\d{4}\.\d\d\.\d\d \d\d:\d\d:\d\d) (\w+)\s+-\s{1,2}(.*)$")]
    private static partial Regex Header();
    [GeneratedRegex(@"^\d{4}\.\d\d\.\d\d \d\d:\d\d:\d\d ")]
    private static partial Regex Stamp();

    /// <summary>読んだ結果。NextOffset から次を読む（返さなかった最後の件の頭）。</summary>
    public sealed record Result(IReadOnlyList<LogEntry> Entries, long NextOffset);

    public static Result Read(Stream stream, long offset, bool final)
    {
        stream.Position = offset;
        var entries = new List<LogEntry>();
        long? start = null;
        DateTime time = default;
        string level = "";
        var body = new StringBuilder();

        void Flush()
        {
            if (start is { } s) entries.Add(new LogEntry(s, time, level, body.ToString().TrimEnd()));
            start = null;
            body.Clear();
        }

        var buffer = new MemoryStream();
        long lineStart = offset;
        long pos = offset;
        int b;
        while (true)
        {
            b = stream.ReadByte();
            if (b == -1) break;
            pos++;
            if (b != '\n')
            {
                buffer.WriteByte((byte)b);
                continue;
            }
            Line(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length).TrimEnd('\r'), lineStart);
            buffer.SetLength(0);
            lineStart = pos;
        }
        // 改行で終わっていない最後の行は、書きかけかもしれない。閉じたログなら読む
        var partial = buffer.Length > 0;
        if (partial && final)
        {
            Line(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length).TrimEnd('\r'), lineStart);
            partial = false;
        }

        long next;
        if (final)
        {
            Flush();
            next = pos;
        }
        else if (partial && Stamp().IsMatch(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length)))
        {
            // 書きかけの行が次の件の頭なら、その前の件は終わっている
            Flush();
            next = lineStart;
        }
        else
        {
            // 最後の件はまだ続くかもしれないので返さず、そこから読み直す
            next = start ?? (partial ? lineStart : pos);
        }
        return new Result(entries, next);

        void Line(string line, long at)
        {
            var m = Header().Match(line);
            if (m.Success && DateTime.TryParseExact(m.Groups[1].Value, "yyyy.MM.dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            {
                Flush();
                start = at;
                time = t;
                level = m.Groups[2].Value;
                body.Append(m.Groups[3].Value);
                return;
            }
            if (start == null) return; // 頭の見つからない続きの行（読み始めの位置の前の件の続き）は捨てる
            if (line.Length == 0) return;
            body.Append('\n').Append(line);
        }
    }

    /// <summary>ファイル名（output_log_2026-11-08_02-36-41.txt）から、VRChat を起こした時刻（ローカル）。</summary>
    public static DateTime? StartedAt(string fileName)
    {
        var m = Regex.Match(fileName, @"output_log_(\d{4}-\d\d-\d\d_\d\d-\d\d-\d\d)");
        return m.Success && DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
    }
}
