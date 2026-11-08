using System.IO.Compression;

namespace Yamaori.Store;

/// <summary>
/// VRChat のログを、書き足されるそばから別のフォルダへ写して残す（VRChat は起動のたびに 24 時間より古いログを消すため）。
/// 書き足された分だけを足していき、ログが閉じたら gzip に固めて元の写しを消す。
/// 残したログからは、読み方を直したあとに全部読み直せる（<see cref="LogIngestor.Reparse"/>）。
/// </summary>
public sealed class LogArchive(string dir)
{
    public string Dir { get; } = dir;

    /// <summary>1 本のログを写す。写し終えて固めたものは何もしない。</summary>
    public void Sync(string sourcePath, bool closed)
    {
        var name = Path.GetFileName(sourcePath);
        var plain = Path.Combine(Dir, name);
        var gz = plain + ".gz";
        if (File.Exists(gz)) return;
        Directory.CreateDirectory(Dir);

        using (var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var dst = new FileStream(plain, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
        {
            if (src.Length < dst.Length)
            {
                // 同じ名前で中身が替わった（短くなった）。前の写しは別の名前で残し、頭から写し直す
                dst.Dispose();
                File.Move(plain, Path.Combine(Dir, $"{Path.GetFileNameWithoutExtension(name)}.replaced-{DateTime.UtcNow:yyyyMMddHHmmss}.txt"));
                Sync(sourcePath, closed);
                return;
            }
            if (src.Length > dst.Length)
            {
                src.Position = dst.Length;
                dst.Position = dst.Length;
                src.CopyTo(dst);
            }
        }
        if (closed) Compress(plain, gz);
    }

    static void Compress(string plain, string gz)
    {
        var tmp = gz + ".tmp";
        using (var input = File.OpenRead(plain))
        using (var output = File.Create(tmp))
        using (var zip = new GZipStream(output, CompressionLevel.SmallestSize))
            input.CopyTo(zip);
        File.Move(tmp, gz, overwrite: true);
        File.Delete(plain);
    }

    /// <summary>残したログの名前（output_log_….txt）と、開き方。古い順。</summary>
    public IReadOnlyList<(string Name, Func<Stream> Open, bool Closed)> List()
    {
        if (!Directory.Exists(Dir)) return [];
        var list = new List<(string, Func<Stream>, bool)>();
        foreach (var f in Directory.GetFiles(Dir, "output_log_*.txt*"))
        {
            var file = Path.GetFileName(f);
            if (file.EndsWith(".gz", StringComparison.Ordinal))
                list.Add((file[..^3], () => new GZipStream(File.OpenRead(f), CompressionMode.Decompress), true));
            else if (file.EndsWith(".txt", StringComparison.Ordinal) && !file.Contains(".replaced-", StringComparison.Ordinal))
                list.Add((file, () => new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), false));
        }
        return list.OrderBy(x => x.Item1, StringComparer.Ordinal).ToList();
    }
}
