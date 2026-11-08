using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Yamaori.Core;

/// <summary>写真から読んだこと。TakenAt は UTC。</summary>
public sealed record PhotoMeta(
    DateTime TakenAt, string TakenSource, int? Width, int? Height,
    string? WorldId, string? WorldName, string? AuthorId, string? AuthorName);

/// <summary>
/// VRChat の写真のメタデータを読む。画像そのものは読まない（頭の 1 MB だけ見る）。
/// <list type="bullet">
/// <item>XMP（VRChat 2025.3.1 以降）: xmp:CreateDate（時差つき）、vrc:WorldID・vrc:WorldDisplayName・vrc:AuthorID、xmp:Author</item>
/// <item>なければファイル名（VRChat_2026-11-03_02-14-20.083_7680x4320.png、ローカル時刻）</item>
/// <item>なければファイルの更新時刻</item>
/// </list>
/// 大きさは PNG の IHDR、なければファイル名の「_幅x高さ」。
/// </summary>
public static partial class PhotoMetaReader
{
    [GeneratedRegex(@"VRChat_(\d{4}-\d\d-\d\d_\d\d-\d\d-\d\d(?:\.\d{1,3})?)(?:_(\d+)x(\d+))?")]
    private static partial Regex FileNamePattern();

    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp"];

    public static bool IsPhoto(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static PhotoMeta Read(Stream stream, string fileName, DateTime fileTimeUtc, TimeZoneInfo timeZone)
    {
        var head = new byte[1 << 20];
        var n = 0;
        while (n < head.Length)
        {
            var r = stream.Read(head, n, head.Length - n);
            if (r == 0) break;
            n += r;
        }
        var text = Encoding.UTF8.GetString(head, 0, n);
        var xmpStart = text.IndexOf("<x:xmpmeta", StringComparison.Ordinal);
        var xmpEnd = xmpStart >= 0 ? text.IndexOf("</x:xmpmeta>", xmpStart, StringComparison.Ordinal) : -1;
        var xmp = xmpStart >= 0 && xmpEnd > xmpStart ? text[xmpStart..xmpEnd] : null;

        string? Tag(string name) =>
            xmp != null && Regex.Match(xmp, $@"<{Regex.Escape(name)}>([^<]*)</{Regex.Escape(name)}>") is { Success: true } m && m.Groups[1].Value.Length > 0
                ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value.Trim()) : null;

        var (w, h) = PngSize(head, n);
        var fm = FileNamePattern().Match(fileName);
        if (w == null && fm.Success && fm.Groups[2].Success)
        {
            w = int.Parse(fm.Groups[2].Value, CultureInfo.InvariantCulture);
            h = int.Parse(fm.Groups[3].Value, CultureInfo.InvariantCulture);
        }

        DateTime taken;
        string source;
        if (Tag("xmp:CreateDate") is { } cd && DateTimeOffset.TryParse(cd, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
        {
            taken = dto.UtcDateTime;
            source = "xmp";
        }
        else if (fm.Success && DateTime.TryParseExact(fm.Groups[1].Value.Split('.')[0], "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            var ms = fm.Groups[1].Value.Contains('.', StringComparison.Ordinal) ? int.Parse(fm.Groups[1].Value.Split('.')[1].PadRight(3, '0'), CultureInfo.InvariantCulture) : 0;
            taken = Log.LogProjector.ToUtc(local.AddMilliseconds(ms), timeZone);
            source = "file_name";
        }
        else
        {
            taken = fileTimeUtc;
            source = "file_time";
        }

        return new PhotoMeta(taken, source, w, h, Tag("vrc:WorldID"), Tag("vrc:WorldDisplayName"), Tag("vrc:AuthorID"), Tag("xmp:Author"));
    }

    static (int?, int?) PngSize(byte[] b, int n)
    {
        // 89 50 4E 47 0D 0A 1A 0A のあと、IHDR の幅と高さ（ビッグエンディアン）
        if (n < 24 || b[0] != 0x89 || b[1] != 0x50 || b[2] != 0x4E || b[3] != 0x47 || b[12] != (byte)'I' || b[13] != (byte)'H') return (null, null);
        int Be(int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
        return (Be(16), Be(20));
    }
}
