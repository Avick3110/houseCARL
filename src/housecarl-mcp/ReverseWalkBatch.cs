using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

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
    /// selection the reading forms consume, the count of index candidates the body check dropped, the index's own
    /// accounting line, and the build the whole answer was read from.</summary>
    public sealed record Result(IReadOnlyList<ReverseSelection.Hop> Hops, IReadOnlyList<string> Selection,
                                int Seeds, bool Capped, int Dropped, string? IndexNote, string? Epoch,
                                string? Refusal);

    /// <summary>Run the walk from these seeds. Every seed is parsed against the captured build, so a bad FormID is
    /// a refusal naming it rather than a seed that silently reaches nothing.</summary>
    public static Result Run(LoadOrderService svc, IReadOnlyList<string> seeds, int depth, int maxNodes,
                             ArtifactDemand? demand)
    {
        var pin = svc.CapturePin();
        var view = pin.View;
        var epoch = view.Epoch;
        if (demand is not null && demand.Epoch != epoch)
            return new Result(Array.Empty<ReverseSelection.Hop>(), Array.Empty<string>(), 0, false, 0, null, epoch,
                              LoadOrderService.ArtifactEpochMismatch(demand, epoch));

        // Seeds are deduplicated: two spellings of one key (a runtime FormID and its ID:Plugin form) parse to the
        // same FormKey, and the selection the reading forms consume must list it once.
        var seedKeys = new List<FormKey>(seeds.Count);
        var seedSeen = new HashSet<FormKey>();
        foreach (var raw in seeds)
        {
            FormKey fk;
            try { fk = view.ParseFormId(raw); }
            catch (Exception ex)
            {
                return new Result(Array.Empty<ReverseSelection.Hop>(), Array.Empty<string>(), 0, false, 0, null, epoch,
                                  $"bad FormID '{raw}': {ex.Message} — every seed of a reverse walk must parse before the walk starts.");
            }
            if (seedSeen.Add(fk)) seedKeys.Add(fk);
        }

        var built = view.EnsureReverseIndex();
        int dropped = 0;
        using var session = pin.Resolver.OpenSession();
        // The index answers in candidates — it says SOME plugin's copy carries the link. references= then re-tests
        // each candidate against the body it judges, and so does this: a record whose winner dropped the link is
        // neither listed nor expanded, so the two spellings of the reverse question cannot disagree and a false
        // hop-1 node cannot seed a false subtree.
        bool Verify(FormKey candidate, IReadOnlySet<FormKey> frontier)
        {
            var w = view.ResolveWinner(candidate);
            if (w is null) { dropped++; return false; }
            IMajorRecordGetter? body;
            // Any throw out of the lazy overlay seek — an unreadable plugin, a malformed subrecord — is a
            // coverage gap on that one record, counted and skipped, never the end of the whole walk. The same
            // rule references= keeps.
            try { body = view.GetRecord(session, w.Value.WinnerPlugin, candidate); }
            catch (Exception) { dropped++; return false; }
            if (body is null || DeletedRecordRule.HasNoLiveBody(body) || body is not IFormLinkContainerGetter flc)
            {
                dropped++;
                return false;
            }
            foreach (var l in flc.EnumerateFormLinks())
                if (frontier.Contains(l.FormKey)) return true;
            dropped++;
            return false;
        }

        var hops = ReverseSelection.Transitive(view.ReverseIndex!, seedKeys, depth, maxNodes, Verify, out var capped);

        // Seeds first, then each hop in order: the selection reads in walk order, and the render says the seeds
        // are in it.
        var selection = new List<string>(seedKeys.Count);
        foreach (var k in seedKeys) selection.Add(k.ToString());
        foreach (var hop in hops)
            foreach (var k in hop.Reached) selection.Add(k.ToString());

        return new Result(hops, selection, seedKeys.Count, capped, dropped, built.Note, epoch, null);
    }
}
