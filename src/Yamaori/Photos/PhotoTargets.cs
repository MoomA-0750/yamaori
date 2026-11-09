using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Renci.SshNet;

namespace Yamaori.Photos;

/// <summary>写真の送り先。remotePath は「yyyy-MM/ファイル名」の形（区切りは /）。</summary>
public interface IPhotoTarget : IDisposable
{
    /// <summary>送った記録を分ける鍵（種類とアドレス）。</summary>
    string Key { get; }
    /// <summary>送り先のファイルを Yamaori の目録で読めるなら、その道（フォルダの送り先だけ）。</summary>
    string? LocalPath(string remotePath);
    /// <summary>送り先に、その大きさのファイルがあるか。</summary>
    Task<long?> SizeAsync(string remotePath, CancellationToken cancel);
    /// <summary>送り先のファイルの中身の SHA-256（なければ null）。手元を消す前に、中身まで同じかを確かめる。</summary>
    Task<byte[]?> HashAsync(string remotePath, CancellationToken cancel);
    Task UploadAsync(string localPath, string remotePath, CancellationToken cancel);
}

/// <summary>送り先そのものを信用できないので、この回は全部やめる（1 枚ごとに試し直さない）。</summary>
public sealed class TargetRefusedException(string message) : IOException(message);

/// <summary>フォルダ（SMB の共有 \\NAS\…、割り当てたドライブ、ほかのディスク）。</summary>
public sealed class FolderTarget(string root) : IPhotoTarget
{
    public string Key => "folder:" + Path.GetFullPath(root);
    public string? LocalPath(string remotePath) => Path.Combine(root, remotePath.Replace('/', Path.DirectorySeparatorChar));

    public Task<long?> SizeAsync(string remotePath, CancellationToken cancel)
    {
        var p = LocalPath(remotePath)!;
        return Task.FromResult(File.Exists(p) ? new FileInfo(p).Length : (long?)null);
    }

    public async Task<byte[]?> HashAsync(string remotePath, CancellationToken cancel)
    {
        var p = LocalPath(remotePath)!;
        if (!File.Exists(p)) return null;
        await using var f = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return await System.Security.Cryptography.SHA256.HashDataAsync(f, cancel).ConfigureAwait(false);
    }

    public async Task UploadAsync(string localPath, string remotePath, CancellationToken cancel)
    {
        var dest = LocalPath(remotePath)!;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var tmp = dest + ".yamaori-tmp";
        await using (var src = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            await src.CopyToAsync(dst, cancel).ConfigureAwait(false);
        File.Move(tmp, dest, overwrite: true);
        File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(localPath));
    }

    public void Dispose() { }
}

/// <summary>WebDAV（Synology・QNAP・Nextcloud など）。</summary>
public sealed class WebDavTarget : IPhotoTarget
{
    readonly HttpClient _http;
    readonly Uri _base;

    public WebDavTarget(string baseUrl, string? user, string? password, HttpMessageHandler? handler = null)
    {
        _base = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
        _http = handler == null ? new HttpClient() : new HttpClient(handler);
        if (user != null)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
    }

    public string Key => "webdav:" + _base;
    public string? LocalPath(string remotePath) => null;
    Uri Url(string remotePath) => new(_base, string.Join('/', remotePath.Split('/').Select(Uri.EscapeDataString)));

    public async Task<long?> SizeAsync(string remotePath, CancellationToken cancel)
    {
        using var res = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Head, Url(remotePath)), cancel).ConfigureAwait(false);
        return res.IsSuccessStatusCode ? res.Content.Headers.ContentLength : null;
    }

    public async Task<byte[]?> HashAsync(string remotePath, CancellationToken cancel)
    {
        using var res = await _http.GetAsync(Url(remotePath), HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        await using var body = await res.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        return await System.Security.Cryptography.SHA256.HashDataAsync(body, cancel).ConfigureAwait(false);
    }

    public async Task UploadAsync(string localPath, string remotePath, CancellationToken cancel)
    {
        // 年月のフォルダを作る（もうあれば 405 が返るので、それは気にしない）
        var dir = remotePath[..remotePath.LastIndexOf('/')];
        using (var mk = await _http.SendAsync(new HttpRequestMessage(new HttpMethod("MKCOL"), Url(dir + "/")), cancel).ConfigureAwait(false))
            if (!mk.IsSuccessStatusCode && mk.StatusCode is not (HttpStatusCode.MethodNotAllowed or HttpStatusCode.Conflict))
                mk.EnsureSuccessStatusCode();
        await using var src = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var put = new HttpRequestMessage(HttpMethod.Put, Url(remotePath)) { Content = new StreamContent(src) };
        using var res = await _http.SendAsync(put, cancel).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// SFTP（NAS の SSH）。target は sftp://host[:port]/path。
/// 相手の鍵は、最初につないだときのものを覚え（knownHostKey が空なら受け入れて SeenHostKey に入れる）、
/// 覚えた鍵と違えばつながない（家の中で相手のふりをされても、パスワードを渡さない）。
/// </summary>
public sealed class SftpTarget : IPhotoTarget
{
    readonly SftpClient _client;
    readonly string _root;
    readonly string _key;
    string? _rejected;

    public SftpTarget(string target, string? user, string? password, string? knownHostKey = null)
    {
        var u = new Uri(target);
        _root = u.AbsolutePath.TrimEnd('/');
        _key = $"sftp://{user}@{u.Host}:{(u.Port > 0 ? u.Port : 22)}{_root}";
        _client = new SftpClient(u.Host, u.Port > 0 ? u.Port : 22, user ?? "", password ?? "");
        _client.HostKeyReceived += (_, e) =>
        {
            var fp = "SHA256:" + e.FingerPrintSHA256;
            HostKey = fp;
            if (string.IsNullOrEmpty(knownHostKey)) { SeenHostKey = fp; e.CanTrust = true; }
            else if (fp == knownHostKey) e.CanTrust = true;
            else { _rejected = fp; e.CanTrust = false; }
        };
    }

    /// <summary>つないだ相手の鍵（試しにつないだときに見せる）。</summary>
    public string? HostKey { get; private set; }

    /// <summary>初めてつないだときに受け入れた相手の鍵（覚えておく）。</summary>
    public string? SeenHostKey { get; private set; }

    public string Key => _key;
    public string? LocalPath(string remotePath) => null;

    void Connect()
    {
        if (_client.IsConnected) return;
        try { _client.Connect(); }
        catch (Renci.SshNet.Common.SshException) when (_rejected != null)
        {
            throw new TargetRefusedException($"送り先の鍵が、覚えている鍵と違う（今の鍵 {_rejected}）。送り先の鍵を変えたのなら、写真の設定で「送り先の鍵を覚え直す」を選んで保存する");
        }
    }

    public Task<long?> SizeAsync(string remotePath, CancellationToken cancel)
    {
        Connect();
        var p = $"{_root}/{remotePath}";
        return Task.FromResult(_client.Exists(p) ? _client.GetAttributes(p).Size : (long?)null);
    }

    public async Task<byte[]?> HashAsync(string remotePath, CancellationToken cancel)
    {
        Connect();
        var p = $"{_root}/{remotePath}";
        if (!_client.Exists(p)) return null;
        await using var f = _client.OpenRead(p);
        return await System.Security.Cryptography.SHA256.HashDataAsync(f, cancel).ConfigureAwait(false);
    }

    public Task UploadAsync(string localPath, string remotePath, CancellationToken cancel)
    {
        Connect();
        // 送り先のフォルダ（途中も含めて）が無ければ作る
        var dir = $"{_root}/{remotePath[..remotePath.LastIndexOf('/')]}";
        var path = "";
        foreach (var part in dir.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path += "/" + part;
            if (!_client.Exists(path)) _client.CreateDirectory(path);
        }
        using var src = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        _client.UploadFile(src, $"{_root}/{remotePath}", canOverride: true);
        return Task.CompletedTask;
    }

    public void Dispose() => _client.Dispose();
}
