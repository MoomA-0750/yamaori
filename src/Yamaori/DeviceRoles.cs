using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Yamaori.Store;
using Yamaori.Web;

namespace Yamaori;

/// <summary>
/// この端末の役（logs・photos・vrcx）。グループに入っていなければ全部を持つ。まとめ役なら自分の行、メンバーならまとめ役から読んだもの。
/// 見張りが何度も訊くので、30 秒ごとに読み直す。
/// </summary>
public sealed class DeviceRoles(IOptions<YamaoriOptions> options, YamaoriState state)
{
    readonly Lock _gate = new();
    IReadOnlyList<string> _roles = [];
    DateTime _readAt = DateTime.MinValue;
    bool _inGroup;

    public bool Has(string role) { Refresh(); lock (_gate) return _roles.Contains(role); }
    public bool InGroup { get { Refresh(); lock (_gate) return _inGroup; } }
    public bool IsMember { get { var o = Config(); return o.Role == "child" && o.GroupId != null; } }

    public void Invalidate() { lock (_gate) _readAt = DateTime.MinValue; }

    public YamaoriOptions Config()
    {
        var o = new YamaoriOptions();
        var path = ConfigFile.PathIn(options.Value.DataDir);
        if (File.Exists(path)) new ConfigurationBuilder().AddJsonFile(path, optional: true, reloadOnChange: false).Build().Bind(o);
        o.DataDir = options.Value.DataDir;
        o.Database = options.Value.Database;
        return o;
    }

    void Refresh()
    {
        lock (_gate) if (DateTime.UtcNow - _readAt < TimeSpan.FromSeconds(30)) return;
        try
        {
            var o = Config();
            using var db = Database.Open(options.Value.DatabasePath);
            var roles = GroupApi.MyRoles(db, o, state.DeviceId);
            var inGroup = new GroupStore(db).IsHub || (o.Role == "child" && o.GroupId != null);
            lock (_gate) { _roles = roles; _inGroup = inGroup; _readAt = DateTime.UtcNow; }
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            // 読めなければ前の役のまま（はじめは全部）
            lock (_gate) { if (_readAt == DateTime.MinValue) _roles = [GroupRoles.Logs, GroupRoles.Photos, GroupRoles.Vrcx]; _readAt = DateTime.UtcNow; }
        }
    }
}
