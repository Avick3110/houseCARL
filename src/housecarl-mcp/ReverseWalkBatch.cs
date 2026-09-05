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
    /// <summary>Why the body check dropped index candidates, one count per cause: the walk judges the winner, and
    /// "the winner does not carry the link" is only one of the four ways a candidate fails it. An unreadable
    /// winner is a coverage gap, not a verdict, so it is never rendered as one.</summary>
    public sealed record DropCensus(int NoLink, int Unreadable, int NoLiveBody, int NoWinner)
    {
        public static readonly DropCensus Empty = new(0, 0, 0, 0);
        public int Total => NoLink + Unreadable + NoLiveBody + NoWinner;
    }

    /// <summary>What one reverse walk produced: the per-hop reached sets (an empty hop is kept and reported), the
    /// selection the reading forms consume, the index candidates the body check dropped and why, the index's own
    /// accounting line, and the build the whole answer was read from.</summary>
    public sealed record Result(IReadOnlyList<ReverseSelection.Hop> Hops, IReadOnlyList<string> Selection,
                                int Seeds, bool Capped, DropCensus Dropped, string? IndexNote, OrderStamp? Stamp,
                                string? Refusal)
    {
        /// <summary>The build's fingerprint alone, for the places that compare epochs rather than render them.</summary>
        public string? Epoch => Stamp?.Epoch;
    }

    /// <summary>Run the walk from these seeds. Every seed is parsed against the captured build, so a bad FormID is
    /// a refusal naming it rather than a seed that silently reaches nothing.</summary>
    public static Result Run(LoadOrderService svc, IReadOnlyList<string> seeds, int depth, int maxNodes,
                             ArtifactDemand? demand)
    {
        var pin = svc.CapturePin();
        var view = pin.View;
        var stamp = view.Stamp;
        if (demand is not null && demand.Epoch != stamp.Epoch)
            return new Result(Array.Empty<ReverseSelection.Hop>(), Array.Empty<string>(), 0, false, DropCensus.Empty, null, stamp,
                              LoadOrderService.ArtifactEpochMismatch(demand, stamp.Epoch));

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
                return new Result(Array.Empty<ReverseSelection.Hop>(), Array.Empty<string>(), 0, false, DropCensus.Empty, null, stamp,
                                  $"bad FormID '{raw}': {ex.Message} — every seed of a reverse walk must parse before the walk starts.");
            }
            if (seedSeen.Add(fk)) seedKeys.Add(fk);
        }

        var built = view.EnsureReverseIndex();
        int unreadable = 0, noLiveBody = 0, noWinner = 0;
        // Every candidate is judged once, however many frontiers name it: the winner's links are remembered (null
        // when the winner cannot be judged at all), so the same body is never read twice and each cause counts
        // records rather than checks. Whether the winner carries a link is frontier-relative, so it is asked again
        // per hop off the remembered set — a record dropped at hop 1 can still be legitimately reached at hop 2,
        // and it then leaves the drop count.
        var linksOf = new Dictionary<FormKey, IReadOnlySet<FormKey>?>();
        var noLink = new HashSet<FormKey>();
        using var session = pin.Resolver.OpenSession();
        // The index answers in candidates — it says SOME plugin's copy carries the link. references= then re-tests
        // each candidate against the body it judges, and so does this: a record whose winner dropped the link is
        // neither listed nor expanded, so the two spellings of the reverse question cannot disagree and a false
        // hop-1 node cannot seed a false subtree.
        bool Verify(FormKey candidate, IReadOnlySet<FormKey> frontier)
        {
            if (!linksOf.TryGetValue(candidate, out var links))
            {
                links = null;
                var w = view.ResolveWinner(candidate);
                if (w is null) noWinner++;
                else
                {
                    IMajorRecordGetter? body = null;
                    bool threw = false;
                    // Any throw out of the lazy overlay seek — an unreadable plugin, a malformed subrecord — is a
                    // coverage gap on that one record, counted and skipped, never the end of the whole walk. The
                    // same rule references= keeps.
                    try { body = view.GetRecord(session, w.Value.WinnerPlugin, candidate); }
                    catch (Exception) { threw = true; }
                    if (threw || body is null) unreadable++;
                    else if (DeletedRecordRule.HasNoLiveBody(body) || body is not IFormLinkContainerGetter flc) noLiveBody++;
                    else
                    {
                        var set = new HashSet<FormKey>();
                        try { foreach (var l in flc.EnumerateFormLinks()) set.Add(l.FormKey); links = set; }
                        catch (Exception) { unreadable++; }
                    }
                }
                linksOf[candidate] = links;
            }
            if (links is null) return false;
            foreach (var l in links)
                if (frontier.Contains(l)) { noLink.Remove(candidate); return true; }
            noLink.Add(candidate);
            return false;
        }

        var hops = ReverseSelection.Transitive(view.ReverseIndex!, seedKeys, depth, maxNodes, Verify, out var capped);

        // Seeds first, then each hop in order: the selection reads in walk order, and the render says the seeds
        // are in it.
        var selection = new List<string>(seedKeys.Count);
        foreach (var k in seedKeys) selection.Add(k.ToString());
        foreach (var hop in hops)
            foreach (var k in hop.Reached) selection.Add(k.ToString());

        return new Result(hops, selection, seedKeys.Count, capped,
                          new DropCensus(noLink.Count, unreadable, noLiveBody, noWinner), built.Note, stamp, null);
    }
}
