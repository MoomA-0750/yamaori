using System.Text.RegularExpressions;

namespace Yamaori.Core;

/// <summary>
/// インスタンスの文字列（「87271~private(usr_…)~canRequestInvite~region(jp)」）を分けたもの。
/// Access は public・friends_plus・friends・invite_plus・invite・group_public・group_plus・group。
/// </summary>
public sealed partial record InstanceInfo(string Number, string Access, string? OwnerId, string? GroupId, string Region)
{
    [GeneratedRegex(@"~(\w+)(?:\(([^)]*)\))?")]
    private static partial Regex Tag();

    public static InstanceInfo Parse(string instance)
    {
        var number = instance.Split('~')[0];
        var tags = new Dictionary<string, string>();
        foreach (Match m in Tag().Matches(instance)) tags[m.Groups[1].Value] = m.Groups[2].Value;
        string? owner = null;
        string? group = null;
        string access;
        if (tags.TryGetValue("group", out var g))
        {
            group = g;
            access = tags.GetValueOrDefault("groupAccessType") switch
            {
                "public" => "group_public",
                "plus" => "group_plus",
                _ => "group",
            };
        }
        else if (tags.TryGetValue("hidden", out var h)) { owner = h; access = "friends_plus"; }
        else if (tags.TryGetValue("friends", out var f)) { owner = f; access = "friends"; }
        else if (tags.TryGetValue("private", out var p)) { owner = p; access = tags.ContainsKey("canRequestInvite") ? "invite_plus" : "invite"; }
        else access = "public";
        var region = tags.TryGetValue("region", out var r) && r.Length > 0 ? r : "us";
        return new InstanceInfo(number, access, owner, group, region);
    }
}
