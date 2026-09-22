using Mutagen.Bethesda.Plugins;

namespace HousecarlCore;

/// <summary>The merge pre-flights for donor-space records no donor originates — injected records and unremappable donor links; contracts in docs/architecture/write-path.md.</summary>
public static class MergeInjection
{
    /// <summary>The per-donor key lists to remap: each donor's originating records, plus the injected records it is the first donor to carry.</summary>
    public static IReadOnlyList<(string Donor, IReadOnlyList<FormKey> Keys)> Renumberable(
        IReadOnlyList<(string Donor, IReadOnlyList<FormKey> Originating, IReadOnlyList<FormKey> Carried)> donorsByLoadOrder)
    {
        var originating = new HashSet<FormKey>();
        foreach (var d in donorsByLoadOrder) foreach (var k in d.Originating) originating.Add(k);

        // Gathered under the FIRST donor carrying the key, so the per-donor accounting counts it once.
        var extra = new Dictionary<string, List<FormKey>>(StringComparer.OrdinalIgnoreCase);
        var claimed = new HashSet<FormKey>();
        foreach (var (donor, _, carried) in donorsByLoadOrder)
            foreach (var k in carried)
            {
                if (originating.Contains(k) || !claimed.Add(k)) continue;   // a plain override of a donor record: already in the dict
                if (!extra.TryGetValue(donor, out var list)) extra[donor] = list = new List<FormKey>();
                list.Add(k);
            }

        return donorsByLoadOrder
            .Select(d => (d.Donor, (IReadOnlyList<FormKey>)(extra.TryGetValue(d.Donor, out var e)
                ? d.Originating.Concat(e).ToList()
                : d.Originating)))
            .ToList();
    }

    /// <summary>The donor reference the remap cannot carry — a link into a donor's FormID space whose target no donor
    /// holds — asked of the links that SURVIVE the merge, before anything is built. Null when every one is covered.</summary>
    public static string? UnremappableLink(
        IReadOnlyDictionary<FormKey, FormKey> dict,
        IReadOnlyList<(string Donor, IReadOnlyList<FormKey> Records, IReadOnlyList<(FormKey Source, FormKey Target)> Links)> donors)
    {
        // Which donor's body of each record the merge keeps: the LAST one in load order holding it.
        var winner = new Dictionary<FormKey, string>();
        foreach (var (donor, records, _) in donors)
            foreach (var k in records) winner[k] = donor;

        foreach (var (donor, _, links) in donors)
            foreach (var (source, target) in links)
            {
                if (winner.TryGetValue(source, out var keeps) && !string.Equals(keeps, donor, StringComparison.OrdinalIgnoreCase))
                    continue;                                              // a losing donor's body: the merge discards it, links and all
                if (dict.ContainsKey(target)) continue;
                return $"cannot merge: {FormIdToken.Of(source)} in donor '{donor}' references {FormIdToken.Of(target)}, a FormID in " +
                       $"donor '{target.ModKey.FileName}'s space that no donor holds, so after the merge it would point at a plugin " +
                       "that is no longer in your load order — add the plugin that holds that record to plugins= (a plugin outside " +
                       "the merge may be injecting it), fix the reference in xEdit, or drop that donor. Nothing was written.";
            }
        return null;
    }
}
