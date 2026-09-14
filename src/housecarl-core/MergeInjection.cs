using Mutagen.Bethesda.Plugins;

namespace HousecarlCore;

/// <summary>The donor-space records a merge must renumber but no donor lists as its own: INJECTED records (xEdit's
/// term — a record whose FormID sits in plugin A's space while plugin B defines it).
///
/// <para>The merge renumbers by donor ORIGINATING keys, which are the records whose FormID master index names the
/// donor itself. An injected record is carried by the plugin that injects it, under a FormID belonging to the plugin
/// it is injected INTO, so it is originating to neither and never enters the remap dict. When both plugins are donors
/// the record is copied into the merged plugin at its old identity, which still names a donor: the write then fails
/// with a raw Mutagen missing-mod fault after the whole merge has been built (#715). Here the injected key is given
/// to the DEFINING donor, so the remap treats it like any other record that donor brings.</para>
///
/// <para>When the definer is NOT in the merge the record cannot be renumbered correctly — the definer keeps injecting
/// it into a plugin that is about to be merged away, and claiming the record for the output would split it from the
/// plugin that defines it — so the merge refuses and names both.</para></summary>
public static class MergeInjection
{
    /// <summary>The per-donor key lists to remap (originating plus injected), or the refusal that stops the merge.</summary>
    public sealed record Plan(IReadOnlyList<(string Donor, IReadOnlyList<FormKey> Keys)> DonorKeys, string? Refusal)
    {
        public static Plan Fail(string refusal) => new(Array.Empty<(string, IReadOnlyList<FormKey>)>(), refusal);
    }

    /// <summary>Classify every donor-space record the donors carry. <paramref name="donorsByLoadOrder"/> carries, per
    /// donor, the records it defines under its OWN name and the records it carries whose FormID names ANOTHER donor.
    /// <paramref name="definerOf"/> answers which plugin defines a FormKey — the first plugin touching it in the
    /// active order — and null when the order has none.</summary>
    public static Plan Classify(
        IReadOnlyList<(string Donor, IReadOnlyList<FormKey> Originating, IReadOnlyList<FormKey> Carried)> donorsByLoadOrder,
        Func<FormKey, string?> definerOf)
    {
        var originating = new HashSet<FormKey>();
        foreach (var d in donorsByLoadOrder) foreach (var k in d.Originating) originating.Add(k);
        var donorNames = new HashSet<string>(donorsByLoadOrder.Select(d => d.Donor), StringComparer.OrdinalIgnoreCase);

        // Injected keys are gathered per DEFINING donor, which is the donor whose records carry them — so the remap
        // sees them alongside that donor's own records and the per-donor accounting stays true.
        var extra = new Dictionary<string, List<FormKey>>(StringComparer.OrdinalIgnoreCase);
        var claimed = new HashSet<FormKey>();
        foreach (var (donor, _, carried) in donorsByLoadOrder)
            foreach (var k in carried)
            {
                if (originating.Contains(k) || !claimed.Add(k)) continue;   // a plain override of a donor record, or already claimed
                var definer = definerOf(k);
                if (definer is null || !donorNames.Contains(definer))
                    return Plan.Fail(
                        $"cannot merge: {FormIdToken.Of(k)} sits in donor '{k.ModKey.FileName}'s FormID space but is defined by " +
                        (definer is null ? "no plugin in your active load order" : $"'{definer}', which is not in the merge") +
                        $", so the merge cannot renumber it without splitting it from the plugin that defines it — " +
                        (definer is null ? $"fix it in xEdit, or drop '{k.ModKey.FileName}'" : $"add '{definer}' to plugins=, or drop '{k.ModKey.FileName}'") +
                        ". Nothing was written.");
                if (!extra.TryGetValue(definer, out var list)) extra[definer] = list = new List<FormKey>();
                list.Add(k);
            }

        var keys = donorsByLoadOrder
            .Select(d => (d.Donor, (IReadOnlyList<FormKey>)(extra.TryGetValue(d.Donor, out var e)
                ? d.Originating.Concat(e).ToList()
                : d.Originating)))
            .ToList();
        return new Plan(keys, null);
    }

    /// <summary>The donor reference that the remap cannot carry: a link into a donor's FormID space whose target no
    /// donor defines. Left alone it survives the renumber pointing at a plugin the merge removes, and the write throws
    /// a raw missing-mod fault (#715) — so it is asked here, before anything is built. Null when every donor link is
    /// covered.</summary>
    public static string? UnremappableLink(
        IReadOnlyDictionary<FormKey, FormKey> dict,
        IReadOnlyList<(string Donor, IReadOnlyList<(FormKey Source, FormKey Target)> Links)> donorLinks)
    {
        foreach (var (donor, links) in donorLinks)
            foreach (var (source, target) in links)
            {
                if (dict.ContainsKey(target)) continue;
                return $"cannot merge: {FormIdToken.Of(source)} in donor '{donor}' references {FormIdToken.Of(target)}, a FormID in " +
                       $"donor '{target.ModKey.FileName}'s space that no donor defines, so after the merge it would point at a plugin " +
                       "that is no longer in your load order — fix that reference in xEdit, or drop that donor. Nothing was written.";
            }
        return null;
    }
}
