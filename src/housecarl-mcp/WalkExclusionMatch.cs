using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

/// <summary>The walk exclusions both walk directions judge a reached node's type against.</summary>
internal static class WalkExclusionMatch
{
    /// <summary>The first exclusion matching this type, or null when none does.</summary>
    public static (string Match, bool Refuse)? Match(IReadOnlyList<(string Match, bool Refuse)> exclusions, string type)
    {
        foreach (var x in exclusions)
            if (x.Match.Equals(type, StringComparison.OrdinalIgnoreCase)) return x;
        return null;
    }

    /// <summary>The refusal sentence for a reached node a refuse exclusion names.</summary>
    public static string RefuseSentence(string type, FormKey key, string? via = null)
        => $"the walk reached a {type} ({FormIdToken.Of(key)}{(via is null ? "" : $", via {via}")}) — a node class this call excludes with severity 'refuse'. Nothing is returned for this call.";
}
