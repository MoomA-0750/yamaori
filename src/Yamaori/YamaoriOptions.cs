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

    /// <summary>端末の役割。standalone（ひとりで。既定。親にもなれる）・child（子。記録を親へ送る）。</summary>
    public string Role { get; set; } = "standalone";

    /// <summary>子のとき、親の Yamaori のアドレス（http://desktop:47800 など）。</summary>
    public string? ParentUrl { get; set; }

    /// <summary>子のとき、親で作ったトークン（暗号化した形）。</summary>
    public string? ParentToken { get; set; }

    /// <summary>子のとき、親へ送る間隔（分）。</summary>
    public double SyncMinutes { get; set; } = 10;

    /// <summary>写真を NAS などへ送る設定。</summary>
    public PhotoUploadOptions PhotoUpload { get; set; } = new();

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

/// <summary>写真を送る設定。</summary>
public sealed class PhotoUploadOptions
{
    /// <summary>none（送らない。既定）・folder（SMB の共有・割り当てたドライブ）・webdav・sftp。</summary>
    public string Kind { get; set; } = "none";

    /// <summary>送り先（フォルダの道、https://… の WebDAV、sftp://host/path）。</summary>
    public string Target { get; set; } = "";

    public string? Username { get; set; }

    /// <summary>暗号化したパスワード（Secrets.Protect の形）。</summary>
    public string? Password { get; set; }

    /// <summary>送ったあと手元を: keep（全部残す。既定）・days（RetentionDays 日たったら消す）・after_upload（送ったら消す）。</summary>
    public string Retention { get; set; } = "keep";

    public int RetentionDays { get; set; } = 30;

    /// <summary>送る間隔（分）。</summary>
    public double IntervalMinutes { get; set; } = 10;
}
