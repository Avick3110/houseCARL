using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlMcp;

/// <summary>The transitive reverse walk — what points at the seeds, and what points at that — served off the
/// reverse-reference index, so a hop is a lookup rather than a scan of scans; the follow rule is every link at
/// every hop. Contract in docs/architecture/read-engine.md.</summary>
public static class ReverseWalkBatch
{
    /// <summary>Why the body check dropped index candidates, one count per cause; an unreadable winner is a
    /// coverage gap, not a verdict.</summary>
    public sealed record DropCensus(int NoLink, int Unreadable, int NoLiveBody, int NoWinner)
    {
        public static readonly DropCensus Empty = new(0, 0, 0, 0);
        public int Total => NoLink + Unreadable + NoLiveBody + NoWinner;
    }

    /// <summary>What one reverse walk produced: the per-hop reached sets, the selection, the dropped candidates and
    /// why, the unreadable winner plugins, the leniently read records, the index note, and the build behind it.</summary>
    public sealed record Result(IReadOnlyList<ReverseSelection.Hop> Hops, IReadOnlyList<string> Selection,
                                int Seeds, bool Capped, DropCensus Dropped, string? IndexNote, OrderStamp? Stamp,
                                string? Refusal, IReadOnlyList<string>? UnreadableWinners = null,
                                IReadOnlyList<string>? LenientRecords = null)
    {
        /// <summary>The type of each record walk.through left out, once per record; null when through is unset.</summary>
        public IReadOnlyList<string>? LeftOut { get; init; }

        /// <summary>How many reached records a stop exclusion kept as boundaries.</summary>
        public int Boundaries { get; init; }

        /// <summary>The build's fingerprint alone, for the places that compare epochs rather than render them.</summary>
        public string? Epoch => Stamp?.Epoch;
    }

    /// <summary>Run the walk from these seeds; a bad FormID is a refusal naming it.</summary>
    public static Result Run(LoadOrderService svc, IReadOnlyList<string> seeds, int depth, int maxNodes,
                             ArtifactDemand? demand, CancellationToken ct = default,
                             IReadOnlyList<(string Match, bool Refuse)>? exclusions = null,
                             IReadOnlySet<string>? through = null)
    {
        var pin = svc.CapturePin();
        var view = pin.View;
        var stamp = view.Stamp;
        if (demand is not null && demand.Epoch != stamp.Epoch)
            return new Result(Array.Empty<ReverseSelection.Hop>(), Array.Empty<string>(), 0, false, DropCensus.Empty, null, stamp,
                              RecordReads.ArtifactEpochMismatch(demand, stamp.Epoch));

        // Seeds are deduplicated: two spellings of one key parse to the same FormKey.
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
        // Every candidate is judged once, however many frontiers name it.
        var linksOf = new Dictionary<FormKey, IReadOnlySet<FormKey>?>();
        // The type a read reports for each judged candidate, so exclusions and walk.through cost no second read.
        var typeOf = new Dictionary<FormKey, string>();
        var noLink = new HashSet<FormKey>();
        using var session = pin.Resolver.OpenSession();
        // The bodies the check reads are gathered a block of candidates at a time, one enumeration per winner
        // plugin in the block; only the block about to be judged is gathered, so a spent node budget stops it.
        Dictionary<FormKey, IMajorRecordGetter> gathered = new();
        var gatheredKeys = new HashSet<FormKey>();
        // The winner plugins the gather could not read, named once each, so a caller can act on the coverage gap.
        var unreadableWinners = new List<string>();
        // Candidates the body check could only read leniently: verified, but with a named gap.
        var lenientRecords = new List<string>();
        var lenientSeen = new HashSet<FormKey>();
        var unreadableSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Gather(IReadOnlyList<FormKey> block)
        {
            // A candidate judged at an earlier hop is remembered, so its body is not read again.
            var need = new List<FormKey>(block.Count);
            foreach (var k in block) if (!linksOf.ContainsKey(k)) need.Add(k);
            gathered = WinnerBodies.For(view, session, need, null, out var faults, ct);
            gatheredKeys = new HashSet<FormKey>(need);
            foreach (var plugin in faults.Keys)
                if (unreadableSeen.Add(plugin)) unreadableWinners.Add(plugin);
        }
        // The index answers in CANDIDATES; references= re-tests each against the body it judges, and so does this,
        // so a false hop-1 node cannot seed a false subtree.
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
                    // Any throw out of the lazy overlay seek is a coverage gap on that one record, counted and
                    // skipped, never the end of the walk.
                    if (gatheredKeys.Contains(candidate)) gathered.TryGetValue(candidate, out body);
                    else
                        try { body = view.GetRecord(session, w.Value.WinnerPlugin, candidate); }
                        catch (Exception) { threw = true; }
                    if (threw || body is null) unreadable++;
                    else if (DeletedRecordRule.HasNoLiveBody(body) || body is not IFormLinkContainerGetter) noLiveBody++;
                    else
                    {
                        typeOf[candidate] = RecordNaming.StripOverlay(body.GetType().Name);
                        // The SAME link walk references= makes, so the two spellings cannot disagree about a
                        // record whose links only read leniently.
                        var set = new HashSet<FormKey>();
                        try
                        {
                            if (RecordLinks.Collect(body, set) is { } note && lenientSeen.Add(candidate))
                                lenientRecords.Add(note);
                            links = set;
                        }
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

        // A verified candidate is then judged by type: a stop exclusion keeps it as a boundary, a refuse ends the
        // call, and with walk.through set any other type outside it is left out and counted.
        bool shaped = exclusions is { Count: > 0 } || through is not null;
        var leftOut = new Dictionary<FormKey, string>();
        var boundaries = new HashSet<FormKey>();
        bool Admit(FormKey candidate, IReadOnlySet<FormKey> frontier)
        {
            if (!Verify(candidate, frontier)) return false;
            if (!shaped || !typeOf.TryGetValue(candidate, out var type)) return true;
            foreach (var x in exclusions ?? Array.Empty<(string, bool)>())
            {
                if (!x.Match.Equals(type, StringComparison.OrdinalIgnoreCase)) continue;
                if (x.Refuse)
                    throw new WalkRefused($"the walk reached a {type} ({FormIdToken.Of(candidate)}) — a node class this call excludes with severity 'refuse'. Nothing is returned for this call.");
                boundaries.Add(candidate);
                return true;
            }
            if (through is not null && !through.Contains(type)) { leftOut[candidate] = type; return false; }
            return true;
        }

        IReadOnlyList<ReverseSelection.Hop> hops;
        bool capped;
        try
        {
            hops = ReverseSelection.Transitive(view.ReverseIndex!, seedKeys, depth, maxNodes,
                                               shaped ? Admit : Verify, out capped, Gather,
                                               shaped ? k => !boundaries.Contains(k) : null);
        }
        catch (WalkRefused r)
        {
            return new Result(Array.Empty<ReverseSelection.Hop>(), Array.Empty<string>(), 0, false, DropCensus.Empty, built.Note, stamp, r.Message);
        }

        // Seeds first, then each hop in order: the selection reads in walk order.
        var selection = new List<string>(seedKeys.Count);
        foreach (var k in seedKeys) selection.Add(FormIdToken.Of(k));
        foreach (var hop in hops)
            foreach (var k in hop.Reached) selection.Add(FormIdToken.Of(k));

        return new Result(hops, selection, seedKeys.Count, capped,
                          new DropCensus(noLink.Count, unreadable, noLiveBody, noWinner), built.Note, stamp, null,
                          unreadableWinners, lenientRecords)
        {
            LeftOut = through is null ? null : leftOut.Values.ToList(),
            Boundaries = boundaries.Count,
        };
    }

    /// <summary>A refuse exclusion reached: the walk ends and the call returns nothing.</summary>
    sealed class WalkRefused(string message) : Exception(message);
}
