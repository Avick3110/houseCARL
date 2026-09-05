using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

/// <summary>
/// The transitive reverse walk — "what points at the seeds, and what points at that" — served off the
/// reverse-reference index. The forward direction has the body in hand and resolves one link per hop; the reverse
/// direction has to have been walked already, which is what the index is, so this lane is a lookup per hop rather
/// than a scan of scans. The follow rule is every link, at every hop, which is what depth= means everywhere.
/// </summary>
public static class ReverseWalkBatch
{
    /// <summary>What one reverse walk produced: the per-hop reached sets (an empty hop is kept and reported), the
    /// selection the reading forms consume, the index's own accounting line, and the build the whole answer was
    /// read from.</summary>
    public sealed record Result(IReadOnlyList<ReverseSelection.Hop> Hops, IReadOnlyList<string> Selection,
                                bool Capped, string? IndexNote, string? Epoch, string? Refusal);

    /// <summary>Run the walk from these seeds. Every seed is parsed against the captured build, so a bad FormID is
    /// a refusal naming it rather than a seed that silently reaches nothing.</summary>
    public static Result Run(LoadOrderService svc, IReadOnlyList<string> seeds, int depth, int maxNodes,
                             ArtifactDemand? demand)
    {
        var view = svc.CaptureView();
        var epoch = view.Epoch;
        if (demand is not null && demand.Epoch != epoch)
            return new Result(Array.Empty<ReverseSelection.Hop>(), Array.Empty<string>(), false, null, epoch,
                              LoadOrderService.ArtifactEpochMismatch(demand, epoch));

        var seedKeys = new List<FormKey>(seeds.Count);
        foreach (var raw in seeds)
        {
            try { seedKeys.Add(view.ParseFormId(raw)); }
            catch (Exception ex)
            {
                return new Result(Array.Empty<ReverseSelection.Hop>(), Array.Empty<string>(), false, null, epoch,
                                  $"bad FormID '{raw}': {ex.Message} — every seed of a reverse walk must parse before the walk starts.");
            }
        }

        var built = view.EnsureReverseIndex();
        var hops = ReverseSelection.Transitive(view.ReverseIndex!, seedKeys, depth, maxNodes, out var capped);

        // Seeds first, then each hop in order: the selection reads in walk order, and the render says the seeds
        // are in it.
        var selection = new List<string>(seedKeys.Count);
        foreach (var k in seedKeys) selection.Add(k.ToString());
        foreach (var hop in hops)
            foreach (var k in hop.Reached) selection.Add(k.ToString());

        return new Result(hops, selection, capped, built.Note, epoch, null);
    }
}
