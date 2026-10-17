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
        var index = view.ReverseIndex!;
        // One generation for the whole walk, so candidates and verdicts cannot come from two builds.
        var gen = index.Pin();
        int unreadable = 0, noLiveBody = 0, noWinner = 0;
        // Every candidate is judged once, however many frontiers name it.
        var linksOf = new Dictionary<FormKey, IReadOnlySet<FormKey>?>();
        // Set only when the call passes walk.through or exclusions; unset is the plain walk.
        bool shaped = exclusions is { Count: > 0 } || through is not null;
        // The type a read reports for each judged candidate, so exclusions and walk.through cost no second read.
        var typeOf = new Dictionary<FormKey, string>();
        var noLink = new HashSet<FormKey>();
        using var session = pin.Resolver.OpenSession();
        // Each candidate's winner plugin, resolved once.
        var winners = new Dictionary<FormKey, string?>();
        string? WinnerOf(FormKey k)
        {
            if (!winners.TryGetValue(k, out var w)) winners[k] = w = view.ResolveWinner(k)?.WinnerPlugin;
            return w;
        }
        // This hop's candidates whose winning copy links the frontier, by the index.
        var fromWinner = new HashSet<FormKey>();
        // Candidates the index judged live: their links are not held, fromWinner says whether they reach a frontier.
        var indexJudged = new HashSet<FormKey>();
        // What the door's bodies decided, for the candidates the index cannot judge.
        var pre = new Dictionary<FormKey, PreJudged>();
        // The winner plugins the gather could not read, named once each, so a caller can act on the coverage gap.
        var unreadableWinners = new List<string>();
        // Candidates the body check could only read leniently: verified, but with a named gap.
        var lenientRecords = new List<string>();
        var lenientSeen = new HashSet<FormKey>();
        var unreadableSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The index judges the winner's copy without a body read, unless the winner holds the key twice.
        ReverseReferenceIndex.CopyFact IndexFact(FormKey k, string winner, out string? note)
        {
            note = null;
            return view.WinnerHoldsOnce(k) ? index.WinnerFact(k, winner, out note, gen) : ReverseReferenceIndex.CopyFact.Unknown;
        }
        // Each block is judged off the index where it can be; the rest are read through the door, each body reduced to its verdict while its group is open.
        void Gather(IReadOnlyList<FormKey> block)
        {
            // A candidate judged at an earlier hop is remembered, so its body is not read again.
            var need = new List<(string, FormKey)>(block.Count);
            foreach (var k in block)
            {
                if (linksOf.ContainsKey(k) || indexJudged.Contains(k) || pre.ContainsKey(k) || WinnerOf(k) is not { } w) continue;
                var fact = IndexFact(k, w, out var note);
                if (fact == ReverseReferenceIndex.CopyFact.Unknown) need.Add((w, k));
                else pre[k] = FromIndex(k, fact, note);
            }
            if (need.Count == 0) return;
            var faults = new Dictionary<string, PluginUnreadableException>(StringComparer.OrdinalIgnoreCase);
            OrderedGather.ForEachGroup(view, session, need, (_, bodies, asked) =>
            {
                foreach (var fk in asked) pre[fk] = Judge(bodies.GetValueOrDefault(fk));
            }, faults, ct);
            foreach (var plugin in faults.Keys)
                if (unreadableSeen.Add(plugin)) unreadableWinners.Add(plugin);
        }
        PreJudged Judge(IMajorRecordGetter? body)
        {
            if (body is null) return new(PreKind.Unreadable);
            if (ReverseReferenceIndex.HoldsNoEdges(body)) return new(PreKind.NoLiveBody);
            string? type = shaped ? RecordNaming.StripOverlay(body.GetType().Name) : null;
            // The SAME link walk references= makes, so the two spellings cannot disagree about a record whose links
            // only read leniently.
            var set = new HashSet<FormKey>();
            try { return new(PreKind.Links, set, type, RecordLinks.Collect(body, set)); }
            catch (Exception) { return new(PreKind.LinksThrew, null, type); }
        }
        PreJudged FromIndex(FormKey k, ReverseReferenceIndex.CopyFact fact, string? note)
        {
            if (fact == ReverseReferenceIndex.CopyFact.NoLiveBody) return new(PreKind.NoLiveBody);
            string? type = shaped ? RecordNaming.StripOverlay(view.WinnerClassOf(k)!.Name) : null;
            return fact == ReverseReferenceIndex.CopyFact.Unscannable ? new(PreKind.LinksThrew, null, type)
                                                                     : new(PreKind.Indexed, null, type, note);
        }
        // The index answers in CANDIDATES: one the index judged live is checked against the winner partition's own
        // edges (fromWinner), the rest against the body's links, so a false hop-1 node cannot seed a false subtree.
        bool Verify(FormKey candidate, IReadOnlySet<FormKey> frontier)
        {
            IReadOnlySet<FormKey>? links = null;
            if (!indexJudged.Contains(candidate) && !linksOf.TryGetValue(candidate, out links))
            {
                if (WinnerOf(candidate) is not { } w) noWinner++;
                else
                {
                    // Gather has judged the block already; the index is asked here only for a candidate it did not cover.
                    if (!pre.Remove(candidate, out var pj))
                    {
                        var fact = IndexFact(candidate, w, out var indexNote);
                        if (fact != ReverseReferenceIndex.CopyFact.Unknown) pj = FromIndex(candidate, fact, indexNote);
                        else
                        {
                            // Any throw out of the lazy overlay seek is a coverage gap on that one record, counted and
                            // skipped, never the end of the walk.
                            IMajorRecordGetter? body = null;
                            try { body = view.GetRecord(session, w, candidate); }
                            catch (Exception) { }
                            pj = Judge(body);
                        }
                    }
                    if (pj.Kind == PreKind.Unreadable) unreadable++;
                    else if (pj.Kind == PreKind.NoLiveBody) noLiveBody++;
                    else
                    {
                        if (shaped) typeOf[candidate] = pj.Type!;
                        if (pj.Kind == PreKind.LinksThrew) unreadable++;
                        else
                        {
                            if (pj.Note is { } note && lenientSeen.Add(candidate)) lenientRecords.Add(note);
                            if (pj.Kind == PreKind.Indexed) indexJudged.Add(candidate);
                            else links = pj.Links;
                        }
                    }
                }
                if (!indexJudged.Contains(candidate)) linksOf[candidate] = links;
            }
            bool judgedByIndex = indexJudged.Contains(candidate);
            if (!judgedByIndex && links is null) return false;
            if (judgedByIndex ? fromWinner.Contains(candidate) : links!.Any(frontier.Contains))
            {
                noLink.Remove(candidate);
                return true;
            }
            noLink.Add(candidate);
            return false;
        }

        // A verified candidate's type: a stop is a boundary, a refuse ends the call, a type outside walk.through is left out.
        var leftOut = new Dictionary<FormKey, string>();
        var boundaries = new HashSet<FormKey>();
        bool Admit(FormKey candidate, IReadOnlySet<FormKey> frontier)
        {
            if (leftOut.ContainsKey(candidate)) return false;
            if (!Verify(candidate, frontier)) return false;
            if (!shaped || !typeOf.TryGetValue(candidate, out var type)) return true;
            if (exclusions is not null && WalkExclusionMatch.Match(exclusions, type) is { } x)
            {
                if (x.Refuse) throw new WalkRefused(WalkExclusionMatch.RefuseSentence(type, candidate));
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
            hops = ReverseSelection.Transitive(index, seedKeys, depth, maxNodes,
                                               shaped ? Admit : Verify, out capped, Gather,
                                               shaped ? k => !boundaries.Contains(k) : null,
                                               frontier => index.ReferencersOf(frontier, WinnerOf, fromWinner, gen));
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

    /// <summary>Indexed: live by the index, so whether it links the frontier is <c>fromWinner</c>'s.</summary>
    enum PreKind { Unreadable, NoLiveBody, Links, LinksThrew, Indexed }

    /// <summary>One candidate's verdict for the check: its links, its type for the shaped walk, and its lenient note.</summary>
    readonly record struct PreJudged(PreKind Kind, IReadOnlySet<FormKey>? Links = null, string? Type = null, string? Note = null);

    /// <summary>A refuse exclusion reached: the walk ends and the call returns nothing.</summary>
    sealed class WalkRefused(string message) : Exception(message);
}
