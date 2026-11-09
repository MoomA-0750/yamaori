using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace Yamaori.Web;

/// <summary>
/// 同じ Tailscale のアカウントの端末から、Yamaori を見つける（tailscale status --json の、自分と同じ人の、つながっている端末だけ）。
/// それぞれ https://名前/、https://名前:8443/、http://IP:47800/ の順に、認証なしの挨拶（/api/group/hello）を試す。
/// この端末に tailscale のコマンドがなければ（コンテナなど）、何も見つからない。
/// </summary>
public static class GroupDiscovery
{
    public sealed record Found(string Url, string Host, string DeviceId, string DeviceName, string? GroupId, string? GroupName, bool IsHub, string Version);
    public sealed record Result(bool TailscaleAvailable, string? Problem, IReadOnlyList<Found> Found);

    static string? TailscaleCli()
    {
        if (OperatingSystem.IsWindows())
        {
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
            return File.Exists(p) ? p : null;
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (dir.Length > 0 && File.Exists(Path.Combine(dir, "tailscale"))) return Path.Combine(dir, "tailscale");
        return null;
    }

    sealed record Peer(string Host, string Ip);

    static async Task<(List<Peer>? Peers, string? Problem)> PeersAsync(CancellationToken cancel)
    {
        var cli = TailscaleCli();
        if (cli == null) return (null, "この端末に tailscale のコマンドがない");
        using var p = Process.Start(new ProcessStartInfo(cli, ["status", "--json"]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
        if (p == null) return (null, "tailscale を動かせない");
        var json = await p.StandardOutput.ReadToEndAsync(cancel).ConfigureAwait(false);
        await p.WaitForExitAsync(cancel).ConfigureAwait(false);
        if (p.ExitCode != 0) return (null, "tailscale がつながっていない");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var me = root.GetProperty("Self").GetProperty("UserID").GetInt64();
        var peers = new List<Peer>();
        if (root.TryGetProperty("Peer", out var all) && all.ValueKind == JsonValueKind.Object)
            foreach (var x in all.EnumerateObject())
            {
                var v = x.Value;
                if (!v.TryGetProperty("Online", out var on) || !on.GetBoolean()) continue;
                if (!v.TryGetProperty("UserID", out var uid) || uid.GetInt64() != me) continue;
                var host = v.GetProperty("DNSName").GetString()?.TrimEnd('.') ?? "";
                var ip = v.TryGetProperty("TailscaleIPs", out var ips) && ips.GetArrayLength() > 0 ? ips[0].GetString() ?? "" : "";
                if (host.Length > 0) peers.Add(new Peer(host, ip));
            }
        return (peers, null);
    }

    public static async Task<Result> FindAsync(string selfDeviceId, CancellationToken cancel)
    {
        var (peers, problem) = await PeersAsync(cancel).ConfigureAwait(false);
        if (peers == null) return new Result(false, problem, []);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var found = await Task.WhenAll(peers.Select(async peer =>
        {
            foreach (var url in new[] { $"https://{peer.Host}", $"https://{peer.Host}:8443", peer.Ip.Length > 0 ? $"http://{peer.Ip}:47800" : null })
            {
                if (url == null) continue;
                try
                {
                    var hello = await http.GetFromJsonAsync<GroupApi.Hello>(url + "/api/group/hello", cancel).ConfigureAwait(false);
                    if (hello is { App: "yamaori" } && hello.DeviceId != selfDeviceId)
                        return new Found(url, peer.Host, hello.DeviceId, hello.DeviceName, hello.GroupId, hello.GroupName, hello.IsHub, hello.Version);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException) { }
            }
            return null;
        })).ConfigureAwait(false);
        return new Result(true, null, found.OfType<Found>().OrderByDescending(f => f.IsHub).ThenBy(f => f.DeviceName).ToList());
    }
}
