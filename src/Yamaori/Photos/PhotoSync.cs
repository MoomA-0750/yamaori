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

    public async Task<Result> RunAsync(string retention, int retentionDays, CancellationToken cancel, Action<string>? log = null)
    {
        int uploaded = 0, already = 0, failed = 0, removed = 0;
        foreach (var p in catalog.PendingUploads(localRoots, target.Key))
        {
            cancel.ThrowIfCancellationRequested();
            var remote = RemotePath(p);
            try
            {
                if (await target.SizeAsync(remote, cancel).ConfigureAwait(false) == p.Size) already++;
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
                    if (thumbs != null)
                        foreach (var size in new[] { 320, 1600 }) await thumbs.GetAsync(p.PhotoId, p.Path, size, cancel).ConfigureAwait(false);
                    File.Delete(p.Path);
                    catalog.RemoveLocation(p.Path);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
