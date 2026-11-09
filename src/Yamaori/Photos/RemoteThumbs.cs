using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Renci.SshNet;
using Yamaori.Store;

namespace Yamaori.Photos;

/// <summary>
/// 写真のある機械（NAS）に SSH で入り、yamaori-thumbs.py を送って動かす。縮小は写真のフォルダの .yamaori/thumbs にでき、
/// この端末の Yamaori は写真を読まずにそれを写す（<see cref="Web.Thumbnails"/>）。
/// 鍵は Yamaori が作り、公開鍵を NAS のユーザーに登録してもらう。NAS の鍵は最初につないだときに覚え、違えばつながない。
/// 設定は yamaori.json から毎回読む（画面で変えたらすぐ効く）。
/// </summary>
public sealed class RemoteThumbs(IOptions<YamaoriOptions> options)
{
    public const string HostKeyPrefix = "ssh_hostkey:";
    const string Script = "yamaori-thumbs.py";

    public sealed record RemoteThumbStatus(string Local, string Remote, bool Running, int Total, int Made, int Skipped, int Failed, DateTime? Updated, IReadOnlyList<string> Errors, string? Problem);

    /// <summary>今の設定（yamaori.json から読み直す）。</summary>
    public RemoteThumbOptions Current()
    {
        var path = ConfigFile.PathIn(options.Value.DataDir);
        var o = new YamaoriOptions();
        if (File.Exists(path)) new ConfigurationBuilder().AddJsonFile(path, optional: true, reloadOnChange: false).Build().Bind(o);
        return o.RemoteThumbs;
    }

    public bool Configured => Current() is { Host.Length: > 0, User.Length: > 0, PrivateKey.Length: > 0, Dirs.Count: > 0 };

    /// <summary>新しい鍵を作る（RSA 3072）。秘密鍵は暗号化して、公開鍵はそのまま yamaori.json に置く。公開鍵を返す。</summary>
    public string NewKey()
    {
        using var rsa = RSA.Create(3072);
        var pem = rsa.ExportRSAPrivateKeyPem();
        var p = rsa.ExportParameters(false);
        var pub = "ssh-rsa " + Convert.ToBase64String(SshString(Encoding.ASCII.GetBytes("ssh-rsa"), Mpint(p.Exponent!), Mpint(p.Modulus!))) + " yamaori@" + Environment.MachineName;
        ConfigFile.Update(options.Value.DataDir, json =>
        {
            var r = json["RemoteThumbs"] as System.Text.Json.Nodes.JsonObject ?? [];
            r["PrivateKey"] = Secrets.Protect(pem);
            r["PublicKey"] = pub;
            json["RemoteThumbs"] = r;
        });
        return pub;
    }

    static byte[] Mpint(byte[] v)
    {
        var i = 0;
        while (i < v.Length - 1 && v[i] == 0) i++;
        var trimmed = v[i..];
        return (trimmed[0] & 0x80) != 0 ? [0, .. trimmed] : trimmed;
    }

    static byte[] SshString(params byte[][] parts)
    {
        using var ms = new MemoryStream();
        foreach (var part in parts)
        {
            var len = BitConverter.GetBytes(part.Length);
            if (BitConverter.IsLittleEndian) Array.Reverse(len);
            ms.Write(len);
            ms.Write(part);
        }
        return ms.ToArray();
    }

    (SshClient Ssh, SftpClient Sftp) Connect(RemoteThumbOptions o, AuthStore store)
    {
        var pem = Secrets.Unprotect(o.PrivateKey) ?? throw new InvalidOperationException("鍵を読めない（鍵を作り直して、公開鍵を NAS に登録し直す）");
        var key = new PrivateKeyFile(new MemoryStream(Encoding.ASCII.GetBytes(pem)));
        var info = new Renci.SshNet.ConnectionInfo(o.Host, o.Port > 0 ? o.Port : 22, o.User, new PrivateKeyAuthenticationMethod(o.User, key)) { Timeout = TimeSpan.FromSeconds(15) };
        var pinKey = $"{HostKeyPrefix}{o.Host}:{(o.Port > 0 ? o.Port : 22)}";
        var known = store.Get(pinKey);
        string? seen = null, rejected = null;
        void Check(object? _, Renci.SshNet.Common.HostKeyEventArgs e)
        {
            var fp = "SHA256:" + e.FingerPrintSHA256;
            if (known == null) { seen = fp; e.CanTrust = true; }
            else if (fp == known) e.CanTrust = true;
            else { rejected = fp; e.CanTrust = false; }
        }
        var ssh = new SshClient(info);
        var sftp = new SftpClient(info);
        ssh.HostKeyReceived += Check;
        sftp.HostKeyReceived += Check;
        try
        {
            ssh.Connect();
            // 初めての相手でも、SFTP の接続では SSH の接続で見た鍵と同じかを確かめる
            if (seen != null) known = seen;
            sftp.Connect();
        }
        catch (Renci.SshNet.Common.SshException) when (rejected != null)
        {
            ssh.Dispose(); sftp.Dispose();
            throw new InvalidOperationException($"NAS の鍵が、覚えている鍵と違う（今の鍵 {rejected}）。NAS を作り直したのなら、設定で「NAS の鍵を覚え直す」を押す");
        }
        catch { ssh.Dispose(); sftp.Dispose(); throw; }
        if (seen != null && known == seen) store.Set(pinKey, seen);
        return (ssh, sftp);
    }

    public void ForgetHostKey(AuthStore store)
    {
        var o = Current();
        store.Set($"{HostKeyPrefix}{o.Host}:{(o.Port > 0 ? o.Port : 22)}", null);
    }

    static string Quote(string s) => "'" + s.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    static byte[] ScriptBytes()
    {
        using var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("Yamaori.Photos.Remote.yamaori-thumbs.py")
            ?? throw new InvalidOperationException("縮小を作るスクリプトが見つからない");
        using var ms = new MemoryStream();
        st.CopyTo(ms);
        return ms.ToArray();
    }

    Database OpenDb() => Database.Open(options.Value.DatabasePath);

    /// <summary>つないで、Python と Pillow があり、写真のフォルダに書けるかを確かめる。結果の言葉。</summary>
    public Task<string> TestAsync(CancellationToken cancel) => Task.Run(() =>
    {
        var o = Current();
        using var db = OpenDb();
        var (ssh, sftp) = Connect(o, new AuthStore(db));
        using (ssh) using (sftp)
        {
            var py = ssh.RunCommand("python3 -c 'import PIL; print(PIL.__version__)'");
            if (py.ExitStatus != 0) return $"つながったが、Python 3 と Pillow がない: {py.Error.Trim()}";
            var lines = new List<string> { $"つながった（Pillow {py.Result.Trim()}）" };
            foreach (var d in o.Dirs)
            {
                var c = ssh.RunCommand($"test -d {Quote(d.Remote)} && mkdir -p {Quote(d.Remote + "/.yamaori")} && test -w {Quote(d.Remote + "/.yamaori")} && echo ok");
                lines.Add(c.Result.Trim() == "ok" ? $"{d.Remote}: 書ける" : $"{d.Remote}: 見つからないか書けない");
            }
            return string.Join("。", lines);
        }
    }, cancel);

    /// <summary>NAS でスクリプトを動かし始める（もう動いていれば、スクリプトがそのまま終わる）。結果の言葉。</summary>
    public Task<string> StartAsync(CancellationToken cancel) => Task.Run(() =>
    {
        var o = Current();
        using var db = OpenDb();
        var (ssh, sftp) = Connect(o, new AuthStore(db));
        using (ssh) using (sftp)
        {
            var script = ScriptBytes();
            var started = new List<string>();
            foreach (var d in o.Dirs)
            {
                var dir = d.Remote.TrimEnd('/') + "/.yamaori";
                ssh.RunCommand($"mkdir -p {Quote(dir)}");
                using (var ms = new MemoryStream(script)) sftp.UploadFile(ms, dir + "/" + Script, canOverride: true);
                var cmd = $"cd {Quote(dir)} && nohup python3 {Script} --root {Quote(d.Remote)} --jobs {Math.Clamp(o.Jobs, 1, 8)} > thumbs.log 2>&1 < /dev/null &";
                var r = ssh.RunCommand(cmd);
                started.Add(r.ExitStatus == 0 ? d.Remote : $"{d.Remote}（動かせない: {r.Error.Trim()}）");
            }
            return "動かし始めた: " + string.Join("、", started);
        }
    }, cancel);

    /// <summary>NAS の上の進み具合（写真のフォルダごと）。</summary>
    public Task<IReadOnlyList<RemoteThumbStatus>> StatusAsync(CancellationToken cancel) => Task.Run<IReadOnlyList<RemoteThumbStatus>>(() =>
    {
        var o = Current();
        using var db = OpenDb();
        var (ssh, sftp) = Connect(o, new AuthStore(db));
        using (ssh) using (sftp)
        {
            var list = new List<RemoteThumbStatus>();
            foreach (var d in o.Dirs)
            {
                var path = d.Remote.TrimEnd('/') + "/.yamaori/thumbs-status.json";
                try
                {
                    if (!sftp.Exists(path)) { list.Add(new RemoteThumbStatus(d.Local, d.Remote, false, 0, 0, 0, 0, null, [], "まだ動かしていない")); continue; }
                    using var ms = new MemoryStream();
                    sftp.DownloadFile(path, ms);
                    using var doc = JsonDocument.Parse(ms.ToArray());
                    var j = doc.RootElement;
                    int I(string n) => j.TryGetProperty(n, out var v) && v.TryGetInt32(out var x) ? x : 0;
                    var updated = j.TryGetProperty("updated", out var u) && u.TryGetDouble(out var sec) ? DateTimeOffset.FromUnixTimeMilliseconds((long)(sec * 1000)).UtcDateTime : (DateTime?)null;
                    var errors = j.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
                    list.Add(new RemoteThumbStatus(d.Local, d.Remote, j.TryGetProperty("running", out var run) && run.ValueKind == JsonValueKind.True,
                        I("total"), I("made"), I("skipped"), I("failed"), updated, errors, null));
                }
                catch (Exception ex) when (ex is IOException or JsonException or Renci.SshNet.Common.SshException)
                {
                    list.Add(new RemoteThumbStatus(d.Local, d.Remote, false, 0, 0, 0, 0, null, [], ex.Message));
                }
            }
            return list;
        }
    }, cancel);
}
