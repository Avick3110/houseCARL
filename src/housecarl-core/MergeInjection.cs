using Mutagen.Bethesda.Plugins;

namespace HousecarlCore;

/// <summary>The donor-space records a merge must renumber but no donor lists as its own: INJECTED records (xEdit's
/// term — a record whose FormID sits in plugin A's space while another plugin carries it).
///
/// <para>The merge renumbers by donor ORIGINATING keys, which are the records whose FormID master index names the
/// donor itself. An injected record is carried by the plugin that injects it, under a FormID belonging to the plugin
/// it is injected INTO, so it is originating to neither and never enters the remap dict. The record is then copied
/// into the merged plugin at its old identity, which still names a donor: the write fails with a raw Mutagen
/// missing-mod fault after the whole merge has been built (#715). Here the injected key is given to the first donor
/// carrying it, so the remap treats it like any other record that donor brings.</para>
///
/// <para>Which plugin DEFINES an injected record is not decidable from the order — an override of one declares the
/// same master the injector does, and may sit anywhere after it — so this makes no claim about that. A plugin outside
/// the merge that carries the record too is a dependent like any other: the identify pass sees it once the key is in
/// the dict, and the report warns about it and names it, which is the merge's posture for every external overrider.</para></summary>
public static class MergeInjection
{
    /// <summary>The per-donor key lists to remap: each donor's originating records, plus the injected records it is
    /// the first donor to carry. <paramref name="donorsByLoadOrder"/> carries, per donor, the records it defines under
    /// its OWN name and the records it carries whose FormID names ANOTHER donor.</summary>
    public static IReadOnlyList<(string Donor, IReadOnlyList<FormKey> Keys)> Renumberable(
        IReadOnlyList<(string Donor, IReadOnlyList<FormKey> Originating, IReadOnlyList<FormKey> Carried)> donorsByLoadOrder)
    {
        var originating = new HashSet<FormKey>();
        foreach (var d in donorsByLoadOrder) foreach (var k in d.Originating) originating.Add(k);

        // Gathered under the FIRST donor carrying the key, so the remap sees it beside that donor's own records and
        // the per-donor accounting counts it once.
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

    /// <summary>The donor reference that the remap cannot carry: a link into a donor's FormID space whose target no
    /// donor holds at all. Left alone it survives the renumber pointing at a plugin the merge removes, and the write
    /// throws a raw missing-mod fault (#715) — so it is asked here, before anything is built. Null when every donor
    /// link that reaches the output is covered.
    ///
    /// <para>Only the links that SURVIVE are asked about. A merge keeps the load-order winner's body for a record two
    /// donors both carry and discards the loser's, so a stale reference a later donor already fixed is not in the
    /// output and must not refuse the merge. <paramref name="donorLinks"/> arrives in load order and the last donor
    /// carrying a record wins, exactly as the merge walk resolves it.</para></summary>
    public static string? UnremappableLink(
        IReadOnlyDictionary<FormKey, FormKey> dict,
        IReadOnlyList<(string Donor, IReadOnlyList<FormKey> Records, IReadOnlyList<(FormKey Source, FormKey Target)> Links)> donors)
    {
        // Which donor's body of each record the merge keeps: the LAST one in load order holding it, the same
        // winner-first resolution RemapEngine.MergeModsInto applies.
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
