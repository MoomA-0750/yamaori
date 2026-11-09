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

    /// <summary>input の PNG を、XMP を入れ替えて output に書く。PNG でなければ例外。</summary>
    public static void Write(Stream input, Stream output, string xmp)
    {
        var sig = new byte[8];
        input.ReadExactly(sig);
        if (!IsPng(sig)) throw new InvalidDataException("PNG でない");
        output.Write(sig);
        var header = new byte[8];
        var wrote = false;
        while (true)
        {
            if (input.Read(header, 0, 1) == 0) break;
            input.ReadExactly(header, 1, 7);
            var len = BinaryPrimitives.ReadInt32BigEndian(header);
            var type = Encoding.ASCII.GetString(header, 4, 4);
            if (len < 0) throw new InvalidDataException("PNG の塊の長さが壊れている");
            var data = new byte[len + 4];
            input.ReadExactly(data);
            // 前の XMP は捨てる
            if (type == "iTXt" && len >= Keyword.Length + 1 && Encoding.Latin1.GetString(data, 0, Keyword.Length) == Keyword && data[Keyword.Length] == 0) continue;
            output.Write(header);
            output.Write(data);
            // IHDR のすぐあとに入れる（読む側は頭の 1 MB しか見ないので、画像のデータより前に置く）
            if (type == "IHDR" && !wrote) { WriteChunk(output, "iTXt", ITxt(xmp)); wrote = true; }
            if (type == "IEND") break;
        }
        if (!wrote) throw new InvalidDataException("PNG に IHDR がない");
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
