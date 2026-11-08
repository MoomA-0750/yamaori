using System.Text.RegularExpressions;

namespace Yamaori.Core;

/// <summary>
/// ワールドの説明文とタグに出てくる言葉から、決まりで分類を付ける（AI を使わない）。
/// 例: 「QV Pen」があれば QvPen、「YamaPlayer」があれば YamaPlayer と動画プレイヤー。
/// 決まりは足していける。付けた分類は worlds.categories に入る。
/// </summary>
public static class WorldClassifier
{
    // (分類, 合う言葉の正規表現)。大文字と小文字、空白やハイフンのゆれを許す
    static readonly (string Category, Regex Pattern)[] Rules =
    [
        ("QvPen", R(@"qv\s*-?\s*pen")),
        ("YamaPlayer", R(@"yama\s*-?\s*player")),
        ("iwaSync", R(@"iwa\s*-?\s*sync")),
        ("ProTV", R(@"pro\s*-?\s*tv")),
        ("VizVid", R(@"viz\s*-?\s*vid|vvmw")),
        ("USharpVideo", R(@"u\s*#\s*video|usharp\s*video")),
        ("KineLVideoPlayer", R(@"kinel")),
        ("ミラー", R(@"mirror|ミラー|鏡")),
        ("ペン", R(@"\bpen\b|ペン")),
        ("カメラ", R(@"camera|カメラ|撮影")),
        ("睡眠", R(@"\bsleep|睡眠|寝る|寝れ|寝落ち|おやすみ|就寝|添い寝")),
        ("チェアー", R(@"\bchairs?\b|椅子|いす|ソファ|sofa")),
        ("ゲーム", R(@"\bgame\b|ゲーム")),
        ("音楽", R(@"\bmusic\b|\bdj\b|音楽|ライブ|live\s*house")),
    ];

    static readonly string[] VideoPlayers = ["YamaPlayer", "iwaSync", "ProTV", "VizVid", "USharpVideo", "KineLVideoPlayer"];

    static Regex R(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static IReadOnlyList<string> Classify(string? description, IEnumerable<string>? tags = null)
    {
        var text = (description ?? "") + "\n" + string.Join(" ", (tags ?? []).Select(t => t.Replace("author_tag_", "", StringComparison.Ordinal).Replace('_', ' ')));
        var found = Rules.Where(r => r.Pattern.IsMatch(text)).Select(r => r.Category).ToList();
        if (found.Any(VideoPlayers.Contains)) found.Insert(0, "動画プレイヤー");
        // ペンの決まりは QvPen と重なるので、QvPen があれば外す
        if (found.Contains("QvPen")) found.Remove("ペン");
        return found.Distinct().ToList();
    }
}
