using System.Text.Json;
using Microsoft.Data.Sqlite;
using Yamaori.Core;
using Yamaori.Core.Log;

namespace Yamaori.Store;

/// <summary>
/// ログのファイルを、前に読んだ位置から読んで DB に入れ、そのログの記録を組み立て直す。
/// 止めて起こしても続きから読み、同じ件を 2 度入れない。
/// </summary>
public sealed class LogIngestor(Database db, string deviceId)
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>1 本のログを読んだ結果。NewEvents は今回新しく読んだ出来事（今の出来事として流すのに使う）。</summary>
    public sealed record Result(string LogFile, IReadOnlyList<LogEvent> NewEvents, bool Changed);

    /// <param name="closed">ログが閉じたか（VRChat が終わった、または次のログに移った）。</param>
    public Result Ingest(string path, bool closed, TimeZoneInfo? timeZone = null)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Ingest(Path.GetFileName(path), path, fs, closed, timeZone);
    }

    /// <summary>
    /// 残したログから、そのログの出来事を全部読み直す（読み方を直したあと）。タイムゾーンは前に読んだときのものを使う。
    /// </summary>
    public Result Reparse(string name, Stream stream, bool closed, TimeZoneInfo? timeZone = null)
    {
        var tz = LoadState(name)?.TimeZone is { } id ? FindTimeZone(id) : timeZone;
        var copy = new MemoryStream();
        stream.CopyTo(copy);
        copy.Position = 0;
        // 消すことと入れ直すことを 1 回の書き込みにする（途中で落ちても、前の記録が残る）
        return Ingest(name, "archive:" + name, copy, closed, tz, fromStart: true);
    }

    Result Ingest(string name, string path, Stream stream, bool closed, TimeZoneInfo? timeZone, bool fromStart = false)
    {
        var state = fromStart ? null : LoadState(name);
        var length = stream.Length;
        if (state != null && length == state.Length && closed == state.Closed) return new Result(name, [], false);
        var offset = state?.Offset ?? 0;
        if (length < offset)
        {
            // 同じ名前で中身が替わった（短くなった）。頭から読み直す
            fromStart = true;
            state = null;
            offset = 0;
        }
        var read = LogReader.Read(new BufferedStream(stream, 1 << 16), offset, closed);

        var tzId = state?.TimeZone ?? (timeZone ?? TimeZoneInfo.Local).Id;
        var tz = FindTimeZone(tzId);
        var lastLocal = read.Entries.Count > 0 ? read.Entries[^1].LocalTime : state?.LastEntryLocal;
        var newEvents = new List<(long Offset, LogEvent Event)>();
        foreach (var entry in read.Entries)
            if (LineParser.Parse(entry) is { } e) newEvents.Add((entry.Offset, e));

        using var tx = db.Connection.BeginTransaction();
        if (fromStart) Delete(name, tx);
        foreach (var (at, e) in newEvents)
        {
            using var cmd = db.Command(
                "INSERT OR IGNORE INTO log_events (device_id, log_file, offset, at_local, kind, data) VALUES ($dev, $f, $o, $at, $k, $d)", tx,
                ("$dev", deviceId), ("$f", name), ("$o", at), ("$at", Database.Local(e.At)), ("$k", Kind(e)), ("$d", JsonSerializer.Serialize(e, Json)));
            cmd.ExecuteNonQuery();
        }
        using (var cmd = db.Command(
            """
            INSERT INTO log_files (device_id, name, path, length, offset, time_zone, last_entry_local, closed) VALUES ($dev, $n, $p, $l, $o, $tz, $last, $c)
            ON CONFLICT (device_id, name) DO UPDATE SET path = $p, length = $l, offset = $o, last_entry_local = $last, closed = $c
            """, tx,
            ("$dev", deviceId), ("$n", name), ("$p", path), ("$l", length), ("$o", read.NextOffset), ("$tz", tzId),
            ("$last", lastLocal is { } ll ? Database.Local(ll) : null), ("$c", closed ? 1 : 0)))
            cmd.ExecuteNonQuery();

        var projection = LogProjector.Project(deviceId, name, LoadEvents(name, tx), tz, lastLocal, closed);
        ProjectionWriter.Replace(db, LogProjector.Source(deviceId, name), projection, tx);
        tx.Commit();
        return new Result(name, newEvents.Select(x => x.Event).ToList(), true);
    }

    /// <summary>DB に残した出来事から、ログ 1 本の記録を組み立て直す（読み方を直したあと。生のログがなくてもできる）。</summary>
    public void Rebuild(string name)
    {
        var state = LoadState(name) ?? throw new InvalidOperationException($"{name} は読んでいない");
        using var tx = db.Connection.BeginTransaction();
        var projection = LogProjector.Project(deviceId, name, LoadEvents(name, tx), FindTimeZone(state.TimeZone), state.LastEntryLocal, state.Closed);
        ProjectionWriter.Replace(db, LogProjector.Source(deviceId, name), projection, tx);
        tx.Commit();
    }

    public IReadOnlyList<string> LogFiles()
    {
        using var cmd = db.Command("SELECT name FROM log_files WHERE device_id = $dev ORDER BY name", null, ("$dev", deviceId));
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    sealed record State(long Length, long Offset, string TimeZone, DateTime? LastEntryLocal, bool Closed);

    State? LoadState(string name)
    {
        using var cmd = db.Command("SELECT length, offset, time_zone, last_entry_local, closed FROM log_files WHERE device_id = $dev AND name = $n", null, ("$dev", deviceId), ("$n", name));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new State(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.IsDBNull(3) ? null : Database.ParseLocal(r.GetString(3)), r.GetInt64(4) != 0);
    }

    List<(long, LogEvent)> LoadEvents(string name, SqliteTransaction tx)
    {
        using var cmd = db.Command("SELECT offset, data FROM log_events WHERE device_id = $dev AND log_file = $f ORDER BY offset", tx, ("$dev", deviceId), ("$f", name));
        using var r = cmd.ExecuteReader();
        var list = new List<(long, LogEvent)>();
        while (r.Read())
            if (JsonSerializer.Deserialize<LogEvent>(r.GetString(1), Json) is { } e) list.Add((r.GetInt64(0), e));
        return list;
    }

    void Delete(string name, SqliteTransaction tx)
    {
        foreach (var sql in new[] { "DELETE FROM log_events WHERE device_id = $dev AND log_file = $f", "DELETE FROM log_files WHERE device_id = $dev AND name = $f" })
        {
            using var cmd = db.Command(sql, tx, ("$dev", deviceId), ("$f", name));
            cmd.ExecuteNonQuery();
        }
    }

    static string Kind(LogEvent e)
    {
        var json = JsonSerializer.SerializeToElement(e, Json);
        return json.GetProperty("kind").GetString() ?? e.GetType().Name;
    }

    /// <summary>
    /// ログを読んだときのタイムゾーンを引く（Windows と IANA の名前は互いに引き直す）。
    /// 見つからなければ止める（今の PC のタイムゾーンで黙って読み直すと、記録の時刻が何時間もずれる）。
    /// </summary>
    static TimeZoneInfo FindTimeZone(string id)
    {
        if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz)) return tz;
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana) && TimeZoneInfo.TryFindSystemTimeZoneById(iana, out tz)) return tz;
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var win) && TimeZoneInfo.TryFindSystemTimeZoneById(win, out tz)) return tz;
        throw new InvalidOperationException($"ログを読んだときのタイムゾーン {id} が、この PC で見つからない。読み直すと時刻がずれるので止めた");
    }
}
