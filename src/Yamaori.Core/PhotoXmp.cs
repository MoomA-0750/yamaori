using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;

namespace Yamaori.Core;

/// <summary>
/// 写真（PNG）に、VRChat と同じ形の XMP を書く。画像のデータには触らず、XMP の塊（iTXt「XML:com.adobe.xmp」）だけを入れ替える。
/// VRChat の項目（xmp:CreateDate・xmp:Author・vrc:WorldID・vrc:WorldDisplayName・vrc:AuthorID）に加えて、
/// Yamaori の写真の ID（yamaori:PhotoId。書いて大きさが変わっても同じ写真として扱う）と一緒にいた人（yamaori:Players）を書く。
/// </summary>
public static class PhotoXmp
{
    static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    const string Keyword = "XML:com.adobe.xmp";

    public sealed record Fields(DateTimeOffset CreateDate, string? Author, string? AuthorId, string? WorldId, string? WorldName, string PhotoId, IReadOnlyList<PhotoPerson> Players);

    public static string Build(Fields f)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder();
        sb.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">");
        sb.Append("<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmlns:vrc=\"http://ns.vrchat.com/vrc/1.0/\" xmlns:yamaori=\"https://github.com/MoomA-0750/yamaori/ns/1.0/\">");
        sb.Append("<xmp:CreatorTool>Yamaori</xmp:CreatorTool>");
        if (f.Author != null) sb.Append($"<xmp:Author>{E(f.Author)}</xmp:Author>");
        sb.Append($"<xmp:CreateDate>{f.CreateDate.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture)}</xmp:CreateDate>");
        if (f.WorldId != null) sb.Append($"<vrc:WorldID>{E(f.WorldId)}</vrc:WorldID>");
        if (f.WorldName != null) sb.Append($"<vrc:WorldDisplayName>{E(f.WorldName)}</vrc:WorldDisplayName>");
        if (f.AuthorId != null) sb.Append($"<vrc:AuthorID>{E(f.AuthorId)}</vrc:AuthorID>");
        sb.Append($"<yamaori:PhotoId>{E(f.PhotoId)}</yamaori:PhotoId>");
        if (f.Players.Count > 0)
        {
            sb.Append("<yamaori:Players><rdf:Bag>");
            foreach (var p in f.Players) sb.Append($"<rdf:li>{E((p.Id ?? "") + "|" + p.Name)}</rdf:li>");
            sb.Append("</rdf:Bag></yamaori:Players>");
        }
        sb.Append("</rdf:Description></rdf:RDF></x:xmpmeta>");
        return sb.ToString();
    }

    public static bool IsPng(ReadOnlySpan<byte> head) => head.Length >= 8 && head[..8].SequenceEqual(Signature);

    /// <summary>
    /// input の PNG に、fields を XMP で書いて output に出す。前からある XMP は残し、Yamaori が書く項目だけを入れる・直す
    /// （ほかのアプリが書いた項目は消さない）。値のない項目は、前からある値のままにする。PNG でなければ例外。
    /// </summary>
    public static void Write(Stream input, Stream output, Fields fields)
    {
        var sig = new byte[8];
        input.ReadExactly(sig);
        if (!IsPng(sig)) throw new InvalidDataException("PNG でない");
        // 先に全部の塊を読む（前の XMP は IHDR より後ろのどこにあってもよい）
        var chunks = new List<(string Type, byte[] Header, byte[] Data)>();
        var header = new byte[8];
        while (true)
        {
            if (input.Read(header, 0, 1) == 0) break;
            input.ReadExactly(header, 1, 7);
            var len = BinaryPrimitives.ReadInt32BigEndian(header);
            if (len < 0) throw new InvalidDataException("PNG の塊の長さが壊れている");
            var data = new byte[len + 4];
            input.ReadExactly(data);
            var type = Encoding.ASCII.GetString(header, 4, 4);
            chunks.Add((type, (byte[])header.Clone(), data));
            if (type == "IEND") break;
        }
        if (chunks.Count == 0 || chunks[0].Type != "IHDR") throw new InvalidDataException("PNG に IHDR がない");
        string? existing = null;
        foreach (var c in chunks.Where(c => IsXmpChunk(c.Type, c.Data)))
        {
            // 圧縮フラグ（keyword の \0 の次）
            if (c.Data[Keyword.Length + 1] != 0) throw new InvalidDataException("圧縮された XMP は書き換えない");
            var text = ITxtText(c.Data);
            existing ??= text;
        }
        var xmp = Merge(existing, fields);
        output.Write(sig);
        foreach (var c in chunks)
        {
            if (IsXmpChunk(c.Type, c.Data)) continue;
            output.Write(c.Header);
            output.Write(c.Data);
            // IHDR のすぐあとに入れる（読む側は頭の 1 MB しか見ないので、画像のデータより前に置く）
            if (c.Type == "IHDR") WriteChunk(output, "iTXt", ITxt(xmp));
        }
    }

    static bool IsXmpChunk(string type, byte[] data) =>
        type == "iTXt" && data.Length >= Keyword.Length + 3 && Encoding.Latin1.GetString(data, 0, Keyword.Length) == Keyword && data[Keyword.Length] == 0;

    /// <summary>iTXt の本文（keyword・フラグ・言語・訳した keyword のあと。末尾 4 バイトは CRC）。</summary>
    static string ITxtText(byte[] data)
    {
        var i = Keyword.Length + 3; // keyword \0 フラグ 方式
        while (i < data.Length - 4 && data[i] != 0) i++; // 言語
        i++;
        while (i < data.Length - 4 && data[i] != 0) i++; // 訳した keyword
        i++;
        return Encoding.UTF8.GetString(data, i, Math.Max(0, data.Length - 4 - i));
    }

    /// <summary>前の XMP に、Yamaori の項目を入れる・直す（前がなければ新しく作る）。</summary>
    public static string Merge(string? existing, Fields f)
    {
        if (string.IsNullOrWhiteSpace(existing) || !existing.Contains("</rdf:Description>", StringComparison.Ordinal)) return Build(f);
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var x = existing;
        var namespaces = new Dictionary<string, string>
        {
            ["xmp"] = "http://ns.adobe.com/xap/1.0/",
            ["vrc"] = "http://ns.vrchat.com/vrc/1.0/",
            ["yamaori"] = "https://github.com/MoomA-0750/yamaori/ns/1.0/",
            ["rdf"] = "http://www.w3.org/1999/02/22-rdf-syntax-ns#",
        };
        var firstDesc = System.Text.RegularExpressions.Regex.Match(x, @"<rdf:Description\b[^>]*?(/?)>");
        if (!firstDesc.Success) return Build(f);
        var add = string.Concat(namespaces.Where(n => !x.Contains($"xmlns:{n.Key}=", StringComparison.Ordinal)).Select(n => $" xmlns:{n.Key}=\"{n.Value}\""));
        if (add.Length > 0) x = x.Insert(firstDesc.Index + firstDesc.Length - 1 - firstDesc.Groups[1].Length, add);
        string Set(string text, string tag, string? value)
        {
            if (value == null) return text;
            var re = new System.Text.RegularExpressions.Regex($@"<{System.Text.RegularExpressions.Regex.Escape(tag)}>[^<]*</{System.Text.RegularExpressions.Regex.Escape(tag)}>");
            var el = $"<{tag}>{E(value)}</{tag}>";
            if (re.IsMatch(text)) return re.Replace(text, el, 1);
            var at = text.IndexOf("</rdf:Description>", StringComparison.Ordinal);
            return text.Insert(at, el);
        }
        x = Set(x, "xmp:Author", f.Author);
        x = Set(x, "xmp:CreateDate", f.CreateDate.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
        x = Set(x, "vrc:WorldID", f.WorldId);
        x = Set(x, "vrc:WorldDisplayName", f.WorldName);
        x = Set(x, "vrc:AuthorID", f.AuthorId);
        x = Set(x, "yamaori:PhotoId", f.PhotoId);
        x = System.Text.RegularExpressions.Regex.Replace(x, @"<yamaori:Players>.*?</yamaori:Players>", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        if (f.Players.Count > 0)
        {
            var bag = "<yamaori:Players><rdf:Bag>" + string.Concat(f.Players.Select(p => $"<rdf:li>{E((p.Id ?? "") + "|" + p.Name)}</rdf:li>")) + "</rdf:Bag></yamaori:Players>";
            x = x.Insert(x.IndexOf("</rdf:Description>", StringComparison.Ordinal), bag);
        }
        return x;
    }

    static byte[] ITxt(string xmp)
    {
        // keyword \0 圧縮しない(0) 圧縮の方式(0) 言語 \0 訳した keyword \0 本文(UTF-8)
        var head = Encoding.Latin1.GetBytes(Keyword);
        return [.. head, 0, 0, 0, 0, 0, .. Encoding.UTF8.GetBytes(xmp)];
    }

    static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        output.Write(len);
        var t = Encoding.ASCII.GetBytes(type);
        output.Write(t);
        output.Write(data);
        var crc = Crc32(t, data);
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc);
        output.Write(c);
    }

    static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    static uint Crc32(byte[] type, byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in type) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
