using SkiaSharp;

namespace Yamaori.Web;

/// <summary>
/// 写真の縮小（JPEG）を作って、データのフォルダの thumbs に置いておく。大きさは決まった幾つかだけ（キャッシュを散らさない）。
/// 8K の PNG は読むのに重いので、同時に作るのは 2 枚まで。
/// </summary>
public sealed class Thumbnails(string dir)
{
    public static readonly int[] Sizes = [320, 800, 1600];
    readonly SemaphoreSlim _gate = new(2);

    public static int Fit(int requested) => Sizes.FirstOrDefault(s => s >= requested, Sizes[^1]);

    /// <summary>縮小の置き場所（なければ作る）。読めなければ null。</summary>
    public async Task<string?> GetAsync(string photoId, string source, int size, CancellationToken cancel)
    {
        size = Fit(size);
        var path = Path.Combine(dir, photoId[..4], $"{photoId}_{size}.jpg");
        if (File.Exists(path)) return path;
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            if (File.Exists(path)) return path;
            return await Task.Run(() => Make(source, path, size), cancel).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

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
