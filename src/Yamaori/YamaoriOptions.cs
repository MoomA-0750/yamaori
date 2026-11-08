namespace Yamaori;

/// <summary>設定。既定は本人のフォルダの下。引数（--log-dir など）か、データのフォルダの yamaori.json で変える。</summary>
public sealed class YamaoriOptions
{
    /// <summary>VRChat のログのフォルダ。</summary>
    public string LogDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "VRChat", "VRChat");

    /// <summary>Yamaori のデータ（DB・設定・残したログ）のフォルダ。</summary>
    public string DataDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yamaori");

    /// <summary>DB のファイル。空なら DataDir の yamaori.sqlite3。</summary>
    public string? Database { get; set; }

    /// <summary>この端末の名前（どの端末の記録かを見分けるのに使う）。既定は PC の名前。</summary>
    public string DeviceName { get; set; } = Environment.MachineName;

    /// <summary>
    /// 写真を見張るフォルダ（NAS の共有も可）。空なら VRChat の写真のフォルダ（VRChat の config.json の picture_output_folder、なければ ピクチャ\VRChat）。
    /// </summary>
    public List<string> PhotoDirs { get; set; } = [];

    /// <summary>写真のフォルダを読み直す間隔（分）。新しく撮った写真は、この PC のフォルダならすぐ入る。</summary>
    public double PhotoScanMinutes { get; set; } = 10;

    /// <summary>見張る写真のフォルダ（決めていなければ VRChat の写真のフォルダ）。</summary>
    public IReadOnlyList<string> EffectivePhotoDirs => PhotoDirs.Count > 0 ? PhotoDirs : [VrchatPictureDir()];

    string VrchatPictureDir()
    {
        try
        {
            var config = Path.Combine(LogDir, "config.json");
            if (File.Exists(config) && System.Text.Json.JsonDocument.Parse(File.ReadAllText(config)).RootElement.TryGetProperty("picture_output_folder", out var p)
                && p.GetString() is { Length: > 0 } dir)
                return Path.Combine(dir, "VRChat");
        }
        catch (System.Text.Json.JsonException) { }
        catch (IOException) { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "VRChat");
    }

    /// <summary>WebUI と API の番号。</summary>
    public int Port { get; set; } = 47800;

    /// <summary>
    /// 見せ方。local（この PC だけ。既定）・lan（同じネットワークの端末から。認証が要る）・tailscale（tailscale serve 越し。Tailscale の本人の情報で通す）。
    /// </summary>
    public string Listen { get; set; } = "local";

    /// <summary>ログを見る間隔（秒）。</summary>
    public double PollSeconds { get; set; } = 2;

    /// <summary>生のログを写して残すフォルダ。空なら DataDir の logs。</summary>
    public string? ArchiveDir { get; set; }

    public string ArchivePath => string.IsNullOrEmpty(ArchiveDir) ? Path.Combine(DataDir, "logs") : ArchiveDir;

    public string DatabasePath => string.IsNullOrEmpty(Database) ? Path.Combine(DataDir, "yamaori.sqlite3") : Database;
}
