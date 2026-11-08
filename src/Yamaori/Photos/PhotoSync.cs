using Yamaori.Store;
using Yamaori.Web;

namespace Yamaori.Photos;

/// <summary>
/// 写真を送り先へ送り、手元の残し方に従って消す（1 回分）。
/// 消すのは、送り先に同じ大きさで届いたと確かめて記録した写真だけで、消す前に縮小を作っておく（消したあとも WebUI で見られる）。
/// フォルダの送り先は、送ったファイルを目録の置き場所にも入れる（手元を消しても、もとの大きさで見られる）。
/// </summary>
public sealed class PhotoSync(PhotoCatalog catalog, IPhotoTarget target, IReadOnlyList<string> localRoots, Thumbnails? thumbs, TimeZoneInfo? timeZone = null)
{
    readonly TimeZoneInfo _tz = timeZone ?? TimeZoneInfo.Local;

    public sealed record Result(int Uploaded, int AlreadyThere, int Failed, int Removed);

    public string RemotePath(PhotoCatalog.LocalPhoto p) =>
        $"{TimeZoneInfo.ConvertTimeFromUtc(p.TakenAt, _tz):yyyy-MM}/{p.FileName}";

    /// <summary>名前がぶつかったときの送り先（ファイル名に写真の ID の頭を足す）。</summary>
    static string AltPath(string remote, string photoId)
    {
        var dot = remote.LastIndexOf('.');
        var tag = "-" + photoId[^8..];
        return dot > remote.LastIndexOf('/') ? remote[..dot] + tag + remote[dot..] : remote + tag;
    }

    static bool SameContent(string a, string b)
    {
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        return System.Security.Cryptography.SHA256.HashData(fa).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(fb));
    }

    static bool SameFile(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public async Task<Result> RunAsync(string retention, int retentionDays, CancellationToken cancel, Action<string>? log = null)
    {
        int uploaded = 0, already = 0, failed = 0, removed = 0;
        foreach (var p in catalog.PendingUploads(localRoots, target.Key))
        {
            cancel.ThrowIfCancellationRequested();
            var remote = RemotePath(p);
            try
            {
                var there = await target.SizeAsync(remote, cancel).ConfigureAwait(false);
                if (there != null && there != p.Size)
                {
                    // 同じ名前で大きさの違う写真が送り先にある（ほかのフォルダの同じ名前の写真など）。上書きせず、別の名前で送る
                    remote = AltPath(remote, p.PhotoId);
                    there = await target.SizeAsync(remote, cancel).ConfigureAwait(false);
                }
                if (there == p.Size) already++;
                else
                {
                    await target.UploadAsync(p.Path, remote, cancel).ConfigureAwait(false);
                    if (await target.SizeAsync(remote, cancel).ConfigureAwait(false) != p.Size) throw new IOException("送り先の大きさが合わない");
                    uploaded++;
                }
                catalog.RecordUpload(p.PhotoId, target.Key, remote, p.Size);
                if (target.LocalPath(remote) is { } local)
                    catalog.Add(local, local[..^remote.Length].TrimEnd('/', '\\'));
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException or Renci.SshNet.Common.SshException
                                       && ex is not TargetRefusedException)
            {
                failed++;
                log?.Invoke($"{p.FileName} を送れなかった: {ex.Message}");
            }
        }

        DateTime? before = retention switch
        {
            "after_upload" => DateTime.MaxValue,
            "days" => DateTime.UtcNow.AddDays(-Math.Max(1, retentionDays)),
            _ => null,
        };
        if (before != null)
        {
            foreach (var p in catalog.RemovableLocal(localRoots, target.Key, before == DateTime.MaxValue ? null : before))
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    // 消す前に確かめ直す: 手元のファイルが目録のときと同じ大きさか（置き換わっていないか）、
                    // 送り先が手元と同じファイルではないか、送り先にまだ同じ大きさであるか（なければ送り直しに戻す）
                    var local = new FileInfo(p.Path);
                    if (!local.Exists || local.Length != p.Size) continue;
                    if (p.Mtime != null && Database.Time(local.LastWriteTimeUtc) != p.Mtime) continue; // 目録のあとで書き換わった
                    // 名前と大きさが同じで中身の違う写真がほかにあれば、どちらが送り先にあるか分からないので消さない
                    if (catalog.OtherLocations(p.PhotoId, p.Path).Any(o => File.Exists(o) && !SameContent(o, p.Path)))
                    {
                        log?.Invoke($"{p.FileName} と同じ名前・大きさで中身の違う写真があるので、手元から消さない");
                        continue;
                    }
                    if (p.RemotePath == null) continue;
                    if (target.LocalPath(p.RemotePath) is { } same && SameFile(same, p.Path)) continue;
                    if (await target.SizeAsync(p.RemotePath, cancel).ConfigureAwait(false) != p.Size)
                    {
                        catalog.ForgetUpload(p.PhotoId, target.Key);
                        log?.Invoke($"{p.FileName} が送り先で見つからないか大きさが違うので、消さずに送り直す");
                        continue;
                    }
                    if (thumbs != null)
                        foreach (var size in new[] { 320, 1600 }) await thumbs.GetAsync(p.PhotoId, p.Path, size, cancel).ConfigureAwait(false);
                    File.Delete(p.Path);
                    catalog.RemoveLocation(p.Path);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or Renci.SshNet.Common.SshException)
                {
                    log?.Invoke($"{p.FileName} を手元から消せなかった: {ex.Message}");
                }
            }
        }
        return new Result(uploaded, already, failed, removed);
    }

    /// <summary>SFTP の相手の鍵を覚えておく設定の名前の頭（写真の設定を保存し直すと消す）。</summary>
    public const string HostKeyPrefix = "sftp_hostkey:";

    public static IPhotoTarget? Create(PhotoUploadOptions o, AuthStore? store = null) => o.Kind switch
    {
        "folder" when o.Target.Length > 0 => new FolderTarget(o.Target),
        "webdav" when o.Target.Length > 0 => new WebDavTarget(o.Target, o.Username, Secrets.Unprotect(o.Password)),
        "sftp" when o.Target.Length > 0 => CreateSftp(o, store),
        _ => null,
    };

    static SftpTarget CreateSftp(PhotoUploadOptions o, AuthStore? store)
    {
        var t = new SftpTarget(o.Target, o.Username, Secrets.Unprotect(o.Password));
        var known = store?.Get(HostKeyPrefix + t.Key);
        if (known == null) return t;
        t.Dispose();
        return new SftpTarget(o.Target, o.Username, Secrets.Unprotect(o.Password), known);
    }

    /// <summary>初めてつないだ SFTP の相手の鍵を覚える。</summary>
    public static void RememberHostKey(IPhotoTarget target, AuthStore store)
    {
        if (target is SftpTarget { SeenHostKey: { } fp } s && store.Get(HostKeyPrefix + s.Key) == null)
            store.Set(HostKeyPrefix + s.Key, fp);
    }
}
