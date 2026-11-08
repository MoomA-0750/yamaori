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

    /// <summary>ログを見る間隔（秒）。</summary>
    public double PollSeconds { get; set; } = 2;

    /// <summary>生のログを写して残すフォルダ。空なら DataDir の logs。</summary>
    public string? ArchiveDir { get; set; }

    public string ArchivePath => string.IsNullOrEmpty(ArchiveDir) ? Path.Combine(DataDir, "logs") : ArchiveDir;

    public string DatabasePath => string.IsNullOrEmpty(Database) ? Path.Combine(DataDir, "yamaori.sqlite3") : Database;
}
