using SkiaSharp;

namespace Yamaori.Web;

/// <summary>
/// 写真の縮小（JPEG）を作って、データのフォルダの thumbs に置いておく。大きさは決まった幾つかだけ（キャッシュを散らさない）。
/// 8K の PNG は読むのに重いので、同時に作るのは 2 枚まで。
/// 写真のフォルダの .yamaori/thumbs に縮小があれば（NAS の上で作ったもの。yamaori-thumbs.py か、NAS で動かした Yamaori）、
/// 写真を読まずにそれを写して使う。writeSidecar なら、作った縮小をそこにも置く（NAS で動かした Yamaori が、ほかの端末のために作る）。
/// </summary>
public sealed class Thumbnails(string dir, IReadOnlyList<string>? photoRoots = null, bool writeSidecar = false)
{
    public static readonly int[] Sizes = [320, 800, 1600];
    /// <summary>写真のフォルダの中で、Yamaori が使うフォルダ（写真の目録には入れない）。</summary>
    public const string SidecarDir = ".yamaori";
    readonly SemaphoreSlim _gate = new(2);
    readonly IReadOnlyList<string> _roots = photoRoots ?? [];

    /// <summary>縮小を置くフォルダ。</summary>
    public string Dir => dir;

    public static int Fit(int requested) => Sizes.FirstOrDefault(s => s >= requested, Sizes[^1]);

    public string LocalPath(string photoId, int size) => Path.Combine(dir, photoId[..4], $"{photoId}_{Fit(size)}.jpg");

    public static string SidecarPath(string root, string photoId, int size) =>
        Path.Combine(root, SidecarDir, "thumbs", photoId[..4], $"{photoId}_{Fit(size)}.jpg");

    /// <summary>縮小がもう手元にあるか（作らずに分かる）。</summary>
    public bool Has(string photoId, int size) => File.Exists(LocalPath(photoId, size));

    /// <summary>縮小の置き場所（なければ、写真のフォルダの縮小を写すか、作る）。読めなければ null。</summary>
    public async Task<string?> GetAsync(string photoId, string source, int size, CancellationToken cancel)
    {
        size = Fit(size);
        var path = LocalPath(photoId, size);
        if (File.Exists(path)) return path;
        // NAS の上で作った縮小があれば写す（数十 KB なので、写真を読むより何十倍も速い）
        foreach (var root in _roots)
        {
            var side = SidecarPath(root, photoId, size);
            try
            {
                if (!File.Exists(side)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tmp = path + ".tmp";
                File.Copy(side, tmp, overwrite: true);
                File.Move(tmp, path, overwrite: true);
                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            if (File.Exists(path)) return path;
            var made = await Task.Run(() => Make(source, path, size), cancel).ConfigureAwait(false);
            if (made != null && writeSidecar && RootOf(source) is { } r)
            {
                try
                {
                    var side = SidecarPath(r, photoId, size);
                    Directory.CreateDirectory(Path.GetDirectoryName(side)!);
                    File.Copy(made, side + ".tmp", overwrite: true);
                    File.Move(side + ".tmp", side, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return made;
        }
        finally { _gate.Release(); }
    }

    string? RootOf(string source) =>
        _roots.FirstOrDefault(r => source.StartsWith(Path.TrimEndingDirectorySeparator(r) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));

    static string? Make(string source, string path, int size)
    {
        using var codec = SKCodec.Create(source);
        if (codec == null) return null;
        var info = codec.Info;
        var scale = Math.Min(1f, (float)size / Math.Max(info.Width, info.Height));
        // JPEG などは縮めながら読める（PNG は全部読む）
        var decodeSize = codec.GetScaledDimensions(scale);
        using var decoded = SKBitmap.Decode(codec, new SKImageInfo(decodeSize.Width, decodeSize.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (decoded == null) return null;
        var w = Math.Max(1, (int)Math.Round(info.Width * scale));
        var h = Math.Max(1, (int)Math.Round(info.Height * scale));
        using var resized = decoded.Width == w && decoded.Height == h ? decoded.Copy() : decoded.Resize(new SKImageInfo(w, h), new SKSamplingOptions(SKCubicResampler.Mitchell));
        if (resized == null) return null;
        using var image = SKImage.FromBitmap(resized);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 82);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp)) data.SaveTo(fs);
        File.Move(tmp, path, overwrite: true);
        return path;
    }
}
