using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

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

        /// <summary>The NPCs left out because only a field the walk.inherit category masks linked the walk; null when
        /// walk.inherit is unset.</summary>
        public int? Masked { get; init; }

        /// <summary>The leveled NPC lists crossed on a template chain, not reached; null when walk.inherit is unset.</summary>
        public int? Crossed { get; init; }

        /// <summary>The build's fingerprint alone, for the places that compare epochs rather than render them.</summary>
        public string? Epoch => Stamp?.Epoch;
    }

    /// <summary>Run the walk from these seeds; a bad FormID is a refusal naming it.</summary>
    public static Result Run(LoadOrderService svc, IReadOnlyList<string> seeds, int depth, int maxNodes,
                             ArtifactDemand? demand, CancellationToken ct = default,
                             IReadOnlyList<(string Match, bool Refuse)>? exclusions = null,
                             IReadOnlySet<string>? through = null, NpcInherit? inherit = null)
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
        // Set only when the call passes walk.through, exclusions or walk.inherit; unset is the plain walk.
        bool shaped = exclusions is { Count: > 0 } || through is not null || inherit is not null;
        // The type a read reports for each judged candidate, so exclusions and walk.through cost no second read.
        var typeOf = new Dictionary<FormKey, string>();
        var noLink = new HashSet<FormKey>();
        // walk.inherit state: template crossings, list entries, the category's own and masked fields per NPC, and
        // the nodes expanded only through templates.
        var crossesTo = new Dictionary<FormKey, FormKey>();
        var entriesOf = new Dictionary<FormKey, HashSet<FormKey>>();
        var ownKeys = new Dictionary<FormKey, IReadOnlySet<FormKey>>();
        var maskedKeys = new Dictionary<FormKey, IReadOnlySet<FormKey>>();
        var masked = new HashSet<FormKey>();
        // The nodes the category carried: the seeds, an NPC linked through its own fields for the category, and
        // anything reached by crossing. Only these are crossed from.
        var carried = new HashSet<FormKey>(seedKeys);
        var templateOnly = new HashSet<FormKey>();
        var crossed = new HashSet<FormKey>();
        // The referencers of this hop's fully expanded nodes; a candidate outside it can only be a template inheritor.
        HashSet<FormKey>? fullRefs = null;
        void OnHop(IReadOnlySet<FormKey> frontier)
        {
            fullRefs = null;
            if (!frontier.Any(templateOnly.Contains)) return;
            fullRefs = new HashSet<FormKey>(view.ReverseIndex!.ReferencersOf(frontier.Where(k => !templateOnly.Contains(k)).ToList()));
        }
        // A candidate only template-only nodes name can matter only as an NPC or a leveled NPC list.
        bool TemplateOnlyCandidate(FormKey k) => fullRefs is not null && !fullRefs.Contains(k);
        // Such candidates the typed read found to be neither; a contained record (a placed reference, an INFO) never is.
        var notTemplateType = new HashSet<FormKey>();
        bool Relevant(FormKey k)
            => !TemplateOnlyCandidate(k) || (!notTemplateType.Contains(k) && view.ParentOf(k) is null);
        IReadOnlyList<Type> templateTypes = new[] { typeof(INpcGetter), typeof(ILeveledNpcGetter) };
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
            var typed = new List<FormKey>();
            foreach (var k in block)
                if (!linksOf.ContainsKey(k) && Relevant(k)) (TemplateOnlyCandidate(k) ? typed : need).Add(k);
            gathered = WinnerBodies.For(view, session, need, null, out var faults, ct);
            gatheredKeys = new HashSet<FormKey>(need);
            foreach (var plugin in faults.Keys)
                if (unreadableSeen.Add(plugin)) unreadableWinners.Add(plugin);
            // Template-only candidates are judged off their winner plugin's NPC and leveled NPC groups alone.
            foreach (var group in typed.GroupBy(k => view.ResolveWinner(k)?.WinnerPlugin))
            {
                if (group.Key is not { } plugin) continue;
                var want = new HashSet<FormKey>(group);
                try
                {
                    foreach (var (fk, _, body, _) in view.RecordsIn(new[] { plugin }, templateTypes))
                        if (want.Remove(fk)) { linksOf[fk] = Judge(fk, body); if (want.Count == 0) break; }
                }
                catch (Exception) when (!ct.IsCancellationRequested) { continue; }
                notTemplateType.UnionWith(want);
            }
        }
        // One judged body's links, or null (counted) when it has none to give.
        IReadOnlySet<FormKey>? Judge(FormKey candidate, IMajorRecordGetter body)
        {
            if (DeletedRecordRule.HasNoLiveBody(body) || body is not IFormLinkContainerGetter) { noLiveBody++; return null; }
            if (shaped) typeOf[candidate] = RecordNaming.StripOverlay(body.GetType().Name);
            // The SAME link walk references= makes, so the two spellings cannot disagree about a
            // record whose links only read leniently.
            var set = new HashSet<FormKey>();
            try
            {
                if (RecordLinks.Collect(body, set) is { } note && lenientSeen.Add(candidate))
                    lenientRecords.Add(note);
                if (inherit is not null && body is INpcGetter npc)
                {
                    var split = inherit.Of(npc);
                    set.ExceptWith(split.Removed);
                    if (split.Crossed is { } to) crossesTo[candidate] = to;
                    if (split.Own.Count > 0) ownKeys[candidate] = split.Own;
                    if (split.Masked.Count > 0) maskedKeys[candidate] = split.Masked;
                }
                else if (inherit is not null && body is ILeveledNpcGetter list)
                    entriesOf[candidate] = NpcInherit.Entries(list);
                return set;
            }
            catch (Exception) { unreadable++; return null; }
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
                    else links = Judge(candidate, body);
                }
                linksOf[candidate] = links;
            }
            if (links is null) return false;
            if (inherit is null)
            {
                foreach (var l in links)
                    if (frontier.Contains(l)) { noLink.Remove(candidate); return true; }
                noLink.Add(candidate);
                return false;
            }
            // A Template link is crossed only from a carried node; a template-only node is reached only through a
            // Template link or a leveled NPC list's entries.
            bool reached = false, carries = false, linked = false;
            foreach (var l in links)
            {
                if (!frontier.Contains(l)) continue;
                linked = true;
                bool viaTemplate = crossesTo.TryGetValue(candidate, out var to) && to == l;
                bool viaEntry = entriesOf.TryGetValue(candidate, out var es) && es.Contains(l);
                if (viaTemplate && !carried.Contains(l)) continue;
                if (templateOnly.Contains(l) && !viaTemplate && !viaEntry) continue;
                reached = true;
                if ((viaTemplate || viaEntry) && carried.Contains(l)
                    || ownKeys.TryGetValue(candidate, out var own) && own.Contains(l)) carries = true;
            }
            if (reached)
            {
                noLink.Remove(candidate); masked.Remove(candidate);
                if (carries) carried.Add(candidate);
                return true;
            }
            // Linked only through masked fields: left out and counted.
            if (maskedKeys.TryGetValue(candidate, out var mk) && mk.Any(k => frontier.Contains(k) && !templateOnly.Contains(k)))
            {
                masked.Add(candidate); noLink.Remove(candidate);
                return false;
            }
            // A link that is there but not crossed, or a candidate only a template-only node names, is no verdict.
            if (!linked && (fullRefs is null || fullRefs.Contains(candidate))) noLink.Add(candidate);
            return false;
        }

        // A verified candidate's type: a stop is a boundary, a refuse ends the call, a type outside walk.through is left out.
        var leftOut = new Dictionary<FormKey, string>();
        var boundaries = new HashSet<FormKey>();
        bool Admit(FormKey candidate, IReadOnlySet<FormKey> frontier)
        {
            if (leftOut.ContainsKey(candidate)) return false;
            if (inherit is not null && !Relevant(candidate)) return false;
            if (!Verify(candidate, frontier)) return false;
            if (!shaped || !typeOf.TryGetValue(candidate, out var type)) return true;
            if (exclusions is not null && WalkExclusionMatch.Match(exclusions, type) is { } x)
            {
                if (x.Refuse) throw new WalkRefused(WalkExclusionMatch.RefuseSentence(type, candidate));
                boundaries.Add(candidate);
                // A carried NPC boundary still passes the category on to the NPCs templated on it.
                if (inherit is not null && type == "Npc" && carried.Contains(candidate)) templateOnly.Add(candidate);
                return true;
            }
            if (through is not null && !through.Contains(type))
            {
                // A leveled NPC list on a template chain is crossed, not reached.
                if (inherit is not null && type == "LeveledNpc" && carried.Contains(candidate))
                {
                    crossed.Add(candidate);
                    templateOnly.Add(candidate);
                    return true;
                }
                leftOut[candidate] = type;
                return false;
            }
            return true;
        }

        IReadOnlyList<ReverseSelection.Hop> hops;
        bool capped;
        try
        {
            hops = ReverseSelection.Transitive(view.ReverseIndex!, seedKeys, depth, maxNodes,
                                               shaped ? Admit : Verify, out capped, Gather,
                                               shaped ? k => !boundaries.Contains(k) || templateOnly.Contains(k) : null,
                                               inherit is null ? null : OnHop);
        }
        catch (WalkRefused r)
        {
            return new Result(Array.Empty<ReverseSelection.Hop>(), Array.Empty<string>(), 0, false, DropCensus.Empty, built.Note, stamp, r.Message);
        }

        // Seeds first, then each hop in order: the selection reads in walk order.
        var selection = new List<string>(seedKeys.Count);
        foreach (var k in seedKeys) selection.Add(FormIdToken.Of(k));
        // A crossed leveled list is a step of a template chain, not a reached record.
        if (crossed.Count > 0)
            hops = hops.Select(h => h with { Reached = h.Reached.Where(k => !crossed.Contains(k)).ToList() }).ToList();
        foreach (var hop in hops)
            foreach (var k in hop.Reached) selection.Add(FormIdToken.Of(k));

        return new Result(hops, selection, seedKeys.Count, capped,
                          new DropCensus(noLink.Count, unreadable, noLiveBody, noWinner), built.Note, stamp, null,
                          unreadableWinners, lenientRecords)
        {
            LeftOut = through is null ? null : leftOut.Values.ToList(),
            Boundaries = boundaries.Count,
            Masked = inherit is null ? null : masked.Count,
            Crossed = inherit is null ? null : crossed.Count,
        };
    }

    /// <summary>A refuse exclusion reached: the walk ends and the call returns nothing.</summary>
    sealed class WalkRefused(string message) : Exception(message);
}
