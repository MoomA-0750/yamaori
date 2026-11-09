using System.Text.Json;
using System.Text.Json.Nodes;

namespace Yamaori;

/// <summary>
/// データのフォルダの yamaori.json。書くときは一度に 1 つずつ、別のファイルに書いてから置き換える
/// （書いている途中で電源が落ちても、壊れた yamaori.json が残って起きられなくなることがない）。
/// </summary>
public static class ConfigFile
{
    static readonly Lock Gate = new();

    /// <summary>起きる前に退けた yamaori.json（記録のファイルに残すため）。</summary>
    public static string? LastRecovered { get; private set; }

    public static string PathIn(string dataDir) => Path.Combine(dataDir, "yamaori.json");

    /// <summary>今の中身を読んで change で変え、書き戻す。書き戻した中身を返す。</summary>
    public static JsonObject Update(string dataDir, Action<JsonObject> change)
    {
        lock (Gate)
        {
            var path = PathIn(dataDir);
            var json = Read(path) ?? [];
            change(json);
            Directory.CreateDirectory(dataDir);
            var tmp = path + ".tmp";
            using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(f, json, new JsonSerializerOptions { WriteIndented = true });
                f.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
            return json;
        }
    }

    /// <summary>
    /// 起きる前に呼ぶ。読めない yamaori.json は yamaori.json.broken-&lt;時刻&gt; へ退けて、既定で起きる（窓がないので、黙って起きなくなるのを防ぐ）。
    /// 退けたら、その名前を返す。
    /// </summary>
    public static string? Recover(string dataDir)
    {
        lock (Gate)
        {
            var path = PathIn(dataDir);
            if (!File.Exists(path)) return null;
            try
            {
                if (Read(path) != null) return null;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
            var broken = $"{path}.broken-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(path, broken, overwrite: true);
            LastRecovered = broken;
            return broken;
        }
    }

    /// <summary>ないときは null。形が壊れていれば例外。</summary>
    static JsonObject? Read(string path)
    {
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return [];
        return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })?.AsObject()
            ?? throw new JsonException("yamaori.json がオブジェクトでない");
    }
}
