using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcp;

public sealed partial class LoadOrderService
{
    // ---- the traversal construct (walk=) ---------------------------------------------------------------

    /// <summary>The most record bodies one forward walk held at once — pinned, with the line above, by
    /// <c>RecordsWalkCostTests.AWalkHoldsNoReachedBodiesPastTheGatherThatReadThem</c>.</summary>
    internal static int WalkBodyHighWater;

    /// <summary>How many record bodies the last forward walk was STILL holding when it returned — zero on every
    /// walk, the refusal and the no-frontier returns included.</summary>
    internal static int WalkBodiesHeldAtReturn;

    /// <summary>How many record bodies one forward-walk gather pass reads before it releases them;
    /// <see cref="BodyPrefetch.ChunkRows"/>, and a test lowers it to split a hop.</summary>
    internal static int WalkPassRows = BodyPrefetch.ChunkRows;

    /// <summary>Everything a walk takes from one reached node: its identity and its links, as values — what a key
    /// is remembered by once its body is gone, so a node two seeds both reach is still ONE read per call.</summary>
    sealed class WalkNodeFact
    {
        public bool Resolved;
        public string? Type;
        public string? EditorId;
        public List<FormKey>? Links;
        /// <summary>Set when Mutagen could not parse the node's content, so its links never read — the same fact the
        /// scan lanes account as an unscannable record.</summary>
        public string? Unscannable;
    }

    static readonly List<FormKey> EmptyKeys = new();

    /// <summary>Whether a fault reading one record is a PARSE of that record's content — Mutagen's own exceptions
    /// and the argument/format failures its lazy span reads raise. Anything else is the CALL's and goes on up.</summary>
    static bool IsWalkRecordFault(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is OperationCanceledException or OutOfMemoryException) return false;
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e.GetType().Namespace is { } ns && ns.StartsWith("Mutagen.Bethesda", StringComparison.Ordinal)) return true;
            if (e is ArgumentException or FormatException or IndexOutOfRangeException or OverflowException or InvalidCastException) return true;
        }
        return false;
    }

    /// <summary>How a walk reports a record whose content would not parse — the scan lanes' own account.</summary>
    static string WalkUnscannableNote(string fault)
        => $"could not be scanned (Mutagen could not parse its content) and was not entered — {fault}";

    static string WalkFaultOf(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    /// <summary>What the NPC template report needs from one node — values, never a getter, so reading a chain pins
    /// no record group's bytes.</summary>
    readonly record struct WalkTemplateFact(string TypeName, string? EditorId, FormKey Template, bool HasTemplate,
                                            NpcConfiguration.TemplateFlag Flags, bool IsNpc, bool IsLeveled,
                                            string? Unscannable = null);

    /// <summary>One record the walk reached: its identity, its provenance (<see cref="PulledBy"/>) and whether the
    /// walk entered it or recorded it as a boundary, with a boundary's reason in <see cref="Note"/>.</summary>
    public sealed record WalkNodeRow(string Key, string? Type, string? EditorId, int Depth,
                                     string PulledBy, string Status, string? Note);

    /// <summary>The NPC_ TemplateFlags typed interpreter — deliberately the only such interpreter until a gap
    /// report demands a second.</summary>
    public sealed record NpcTemplateCategory(string Category, bool InheritedAtSeed,
                                             string? ProviderKey, string? ProviderEditorId, string? Note);

    /// <summary>One seed's walk: the reached nodes in BFS order with provenance; the cycles found in the graph it
    /// walked, ONE PER CLOSING LINK, so the count is a lower bound on the distinct loops and none means none
    /// (<see cref="GraphCycles.Find"/>); the truncation note when a cap cut the walk; and, for an NPC_ seed under
    /// follow="Template", the per-category inheritance report.</summary>
    public sealed record WalkSeedResult(string Seed, string? Type, string? EditorId,
                                        IReadOnlyList<WalkNodeRow> Nodes, IReadOnlyList<string> Cycles,
                                        string? TruncationNote, IReadOnlyList<NpcTemplateCategory>? TemplateReport,
                                        string? Error, bool CyclesCapped = false);

    /// <summary>How many loops one seed's cycle search collects.</summary>
    public const int WalkCycleCap = 200;

    /// <summary>One seed's walk in progress: the rows it has proved and the frontier it has still to enter.</summary>
    sealed class WalkSeedState
    {
        public FormKey Key;
        public WalkTemplateFact? SeedTemplateFact;
        public string? EditorId;
        public string Type = "";
        public string Label = "";
        public List<WalkNodeRow> Nodes = new();
        /// <summary>The walked graph as edges, parent to target, per node this seed entered — one entry per link
        /// CROSSED, which is why it goes at <see cref="Settle"/> rather than at return.</summary>
        public Dictionary<FormKey, List<FormKey>> Edges = new();
        /// <summary>This seed's cycles, found once at <see cref="Settle"/>.</summary>
        public IReadOnlyList<string>? Cycles;
        /// <summary>Whether <see cref="WalkCycleCap"/> stopped the search.</summary>
        public bool CyclesCapped;
        /// <summary>Whether this walk will be READ for its cycles; a reading form records no edges and settles
        /// none.</summary>
        public bool WantCycles;
        public string? Truncation;
        /// <summary>Set when the seed itself gave the walk nothing to start from — its content would not parse.</summary>
        public string? Error;
        public HashSet<FormKey> Visited = new();
        public Queue<(FormKey Key, int Depth, string PulledBy)> Frontier = new();

        /// <summary>Record one walked edge; every link off an entered node is recorded, cycle or not.</summary>
        public void Edge(FormKey from, FormKey to)
        {
            if (!WantCycles || to.IsNull) return;
            if (!Edges.TryGetValue(from, out var outgoing)) Edges[from] = outgoing = new List<FormKey>();
            outgoing.Add(to);
        }

        /// <summary>This seed is finished: find its cycles and let its edge set go, where the visited set and the
        /// frontier are dropped, rather than staying resident until the last seed in the batch finishes.</summary>
        public void Settle()
        {
            Cycles ??= WantCycles ? CyclesFound() : Array.Empty<string>();
            Edges = new();
        }

        /// <summary>This seed's cycles, each stated as its loop of records — the last hop closes it.</summary>
        IReadOnlyList<string> CyclesFound()
        {
            var found = GraphCycles.Find(Edges, WalkCycleCap, out var capped);
            CyclesCapped = capped;
            if (found.Count == 0) return Array.Empty<string>();
            var editorIds = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [FormIdToken.Of(Key)] = EditorId,
            };
            foreach (var n in Nodes) editorIds[n.Key] = n.EditorId;
            string Label(FormKey k)
            {
                var token = FormIdToken.Of(k);
                var editorId = editorIds.TryGetValue(token, out var e) ? e : null;
                return $"{token} ({editorId ?? "<no editorid>"})";
            }
            return found.Select(path => string.Join(" -> ", path.Select(Label)) + " -> " + Label(path[0])).ToList();
        }
    }

    /// <summary>The forward walk over the winner link graph, per seed off ONE captured build.</summary>
    public IReadOnlyList<WalkSeedResult> WalkForwardBatch(
        IReadOnlyList<string> seeds, IReadOnlyList<string>? seedPaths, string? follow,
        int depth, int maxNodes, IReadOnlyList<(string Match, bool Refuse)> exclusions,
        ArtifactDemand? demand, out string? refusal, out OrderStamp? epoch, CancellationToken ct = default,
        bool wantCycles = false)
    {
        refusal = null;
        var resolver = Host.Resolver;
        var view = resolver.Capture();
        epoch = view.Stamp;
        if (demand is not null && demand.Epoch != view.Epoch)
        {
            refusal = ArtifactEpochMismatch(demand, view.Epoch);
            return Array.Empty<WalkSeedResult>();
        }
        using var session = resolver.OpenSession();

        string[]? followSegs = null;
        bool closure = string.IsNullOrWhiteSpace(follow) || follow!.Trim() == "*";
        if (!closure)
        {
            followSegs = follow!.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (followSegs.Length == 0) { refusal = $"walk.follow '{follow}' is not a usable field path."; return Array.Empty<WalkSeedResult>(); }
        }
        bool templateFollow = followSegs is { Length: 1 } && followSegs[0].Equals("Template", StringComparison.OrdinalIgnoreCase);

        var bodyCache = new Dictionary<FormKey, IMajorRecordGetter?>();
        WalkBodyHighWater = 0;
        WalkBodiesHeldAtReturn = 0;
        IMajorRecordGetter? Fetch(FormKey k)
        {
            if (bodyCache.TryGetValue(k, out var c)) return c;
            IMajorRecordGetter? g = view.ResolveWinner(k) is { } w ? view.GetRecord(session, w.WinnerPlugin, k) : null;
            bodyCache[k] = g;
            return g;
        }
        // The same read WITHOUT the cache — for the template chain, whose nodes are read once and dropped.
        IMajorRecordGetter? FetchTransient(FormKey k)
            => bodyCache.TryGetValue(k, out var c) ? c
             : view.ResolveWinner(k) is { } w ? view.GetRecord(session, w.WinnerPlugin, k) : null;

        // One node's template facts, memoised BY VALUE: nothing is pinned between seeds, and the walk fills this
        // as it reads, so the report only reads a chain node the walk did not reach itself.
        var templateFacts = new Dictionary<FormKey, WalkTemplateFact?>();
        WalkTemplateFact? TemplateFactOf(FormKey k)
        {
            if (templateFacts.TryGetValue(k, out var have)) return have;
            var body = FetchTransient(k);
            WalkTemplateFact? fact = body is null ? null : FactOf(body);
            templateFacts[k] = fact;
            return fact;
        }
        // The template link and flags are lazily parsed subrecords too, so this read carries the same per-record
        // guard the link read does: a body that will not parse comes back NAMED.
        static WalkTemplateFact FactOf(IMajorRecordGetter b)
        {
            var typeName = RecordNaming.StripOverlay(b.GetType().Name);
            var npc = b as INpcGetter;
            try
            {
                var t = npc?.Template;
                return new WalkTemplateFact(typeName, b.EditorID,
                                            t is null || t.IsNull ? default : t.FormKey, t is not null && !t.IsNull,
                                            npc?.Configuration.TemplateFlags ?? default, npc is not null,
                                            b is ILeveledNpcGetter);
            }
            catch (Exception ex) when (IsWalkRecordFault(ex))
            {
                return new WalkTemplateFact(typeName, null, default, false, default, npc is not null, false,
                                            WalkFaultOf(ex));
            }
        }
        // The hop's bodies, one enumeration per source plugin. A key the gather does not return stays UNCACHED, so
        // Fetch still raises whatever the per-record read raises: the gather is an optimisation, not an error path.
        void Prefetch(IReadOnlyList<FormKey> keys)
        {
            var wanted = new List<FormKey>();
            var seen = new HashSet<FormKey>();
            foreach (var k in keys)
                if (!k.IsNull && !bodyCache.ContainsKey(k) && seen.Add(k)) wanted.Add(k);
            for (int i = 0; i < wanted.Count; i += BodyPrefetch.ChunkRows)
            {
                int end = Math.Min(i + BodyPrefetch.ChunkRows, wanted.Count);
                var chunk = BodyPrefetch.Gather(view, session, wanted, i, end, _ => null, null, ct);
                // The walk asks for every key it gathered, so the chunk's deferred per-plugin walk is forced here.
                for (int k = i; k < end; k++)
                    if (chunk.Body(wanted[k]) is { } body) bodyCache[wanted[k]] = body;
            }
        }
        static string TypeOf(IMajorRecordGetter b) => RecordNaming.StripOverlay(b.GetType().Name);
        List<FormKey> LinksOf(IMajorRecordGetter body, string[]? segs, out string? note)
        {
            note = null;
            if (segs is null)
            {
                var seen = new HashSet<FormKey>();
                var list = new List<FormKey>();
                if (body is Mutagen.Bethesda.Plugins.Records.IFormLinkContainerGetter flc)
                    foreach (var link in flc.EnumerateFormLinks())
                        if (!link.FormKey.IsNull && seen.Add(link.FormKey)) list.Add(link.FormKey);
                return list;
            }
            // The '*parent' containment step: hop to the record that CONTAINS this one, then read the rest of the
            // path there. A path that is nothing but hops IS the edge, and the same grammar where= enforces.
            var (hops, gerr) = ContainmentIndex.SplitHops(segs, string.Join(".", segs), allowBare: true);
            if (gerr is not null) { note = $"({gerr})"; return new List<FormKey>(); }
            for (int i = 0; i < hops; i++)
            {
                var pk = view.ParentOf(body.FormKey);
                if (pk is null)
                {
                    note = $"(no record contains this {TypeOf(body)} — containment runs from these properties only: {ContainmentIndex.ChildBearingSurface()})";
                    return new List<FormKey>();
                }
                if (i == hops - 1 && hops == segs.Length) return new List<FormKey> { pk.Value };
                IMajorRecordGetter? up;
                // A fault reading the CONTAINING record is that record's, and the note names it — never this node's.
                try { up = Fetch(pk.Value); }
                catch (Exception ex) when (IsWalkRecordFault(ex))
                { note = $"(the containing record {FormIdToken.Of(pk.Value)} {WalkUnscannableNote(WalkFaultOf(ex))})"; return new List<FormKey>(); }
                if (up is null) { note = $"(the containing record {FormIdToken.Of(pk.Value)} would not fetch)"; return new List<FormKey>(); }
                body = up;
            }

            var (links, n) = ReadEngine.CollectLinksAt(body, hops == 0 ? segs : segs[hops..]);
            note = n;
            return links ?? new List<FormKey>();
        }

        // What each reached key yielded, by value, so a key two seeds both reach is READ once per call.
        var nodeFacts = seeds.Count > 1 ? new Dictionary<FormKey, WalkNodeFact>() : null;

        // The node's identity and, unless it is at the depth cap or an excluded class, its links.
        WalkNodeFact FactFor(FormKey k, bool atCap)
        {
            var fact = nodeFacts is not null && nodeFacts.TryGetValue(k, out var f) ? f : null;
            if (fact is not null && (!fact.Resolved || atCap || fact.Links is not null || fact.Unscannable is not null || Excluded(fact.Type))) return fact;

            var body = Fetch(k);
            fact = body is null
                 ? new WalkNodeFact { Resolved = false }
                 : new WalkNodeFact { Resolved = true, Type = TypeOf(body), EditorId = body.EditorID };
            // On a template walk the reached nodes ARE the chain nodes, so the report takes its facts off the body
            // in hand here rather than re-reading every chain node from disk.
            if (templateFollow && body is not null)
            {
                var tf = FactOf(body);
                templateFacts.TryAdd(k, tf);
                if (tf.Unscannable is { } tfault) fact.Unscannable = tfault;
            }
            // PER-RECORD FAULT ISOLATION, the twin of the scan lanes': reading a node's links parses its content
            // lazily, so one record Mutagen cannot parse is a boundary and the walk goes on.
            if (body is not null && !atCap && !Excluded(fact.Type) && fact.Unscannable is null)
                try { fact.Links = LinksOf(body, followSegs, out _); }
                catch (Exception ex) when (IsWalkRecordFault(ex)) { fact.Unscannable = WalkFaultOf(ex); }
            if (nodeFacts is not null) nodeFacts[k] = fact;
            return fact;
        }
        bool Excluded(string? type)
            => type is not null && exclusions.Any(x => x.Match.Equals(type, StringComparison.OrdinalIgnoreCase));

        // Is this queued item's row already answerable from the memo? Then its body is not worth a gather slot.
        bool Memoised(FormKey k, int hop)
            => nodeFacts is not null && nodeFacts.TryGetValue(k, out var f)
               && (!f.Resolved || hop >= depth || f.Links is not null || f.Unscannable is not null || Excluded(f.Type));

        // The seeds: parsed, then gathered together, then started on their first hop — in SLICES of a pass, for
        // the reason the hops are, since a walk can be seeded from a spilled artifact holding thousands of IDs.
        var rows = new WalkSeedResult?[seeds.Count];
        var states = new WalkSeedState?[seeds.Count];
        var seedKeys = new FormKey[seeds.Count];
        for (int s0 = 0; s0 < seeds.Count; s0 += WalkPassRows)
        {
        int s1 = Math.Min(s0 + WalkPassRows, seeds.Count);
        var seedGather = new List<FormKey>(s1 - s0);
        for (int i = s0; i < s1; i++)
        {
            try { seedKeys[i] = view.ParseFormId(seeds[i]); seedGather.Add(seedKeys[i]); }
            catch (Exception ex) { rows[i] = new WalkSeedResult(seeds[i]?.Trim() ?? "", null, null, Array.Empty<WalkNodeRow>(), Array.Empty<string>(), null, null, $"bad FormID '{seeds[i]}': {ex.Message}"); }
        }
        Prefetch(seedGather);

        for (int i = s0; i < s1; i++)
        {
            if (rows[i] is not null) continue;
            ct.ThrowIfCancellationRequested();
            var seedFk = seedKeys[i];
            var seedBody = Fetch(seedFk);
            if (seedBody is null)
            {
                // Fetch returns null for two conditions needing different sentences: no winner at all, or a named
                // winner whose body did not come back on fetch.
                var seedWin = view.ResolveWinner(seedFk);
                rows[i] = new WalkSeedResult(FormIdToken.Of(seedFk), null, null, Array.Empty<WalkNodeRow>(), Array.Empty<string>(), null, null,
                    seedWin is null
                        ? UnresolvedFormId(view, seedFk) + " Nothing to walk from."
                        : $"the winner body of {FormIdToken.Of(seedFk)} could not be read from '{seedWin.Value.WinnerPlugin}' — nothing to walk from.");
                continue;
            }
            var seedType = TypeOf(seedBody);
            // The seed's own facts parse its content too, so a seed that will not parse carries its fault.
            var seedFact = templateFollow && seedBody is INpcGetter ? FactOf(seedBody) : (WalkTemplateFact?)null;
            string? seedFault = seedFact?.Unscannable;
            var st = new WalkSeedState
            {
                Key = seedFk,
                // The template report takes the seed's facts, not its body: a getter kept per seed would pin one
                // record group per seed.
                SeedTemplateFact = seedFault is null ? seedFact : null,
                EditorId = seedBody.EditorID,
                Type = seedType,
                Label = $"{seedType} {FormIdToken.Of(seedFk)} ({seedBody.EditorID ?? "<no editorid>"})",
                Visited = new HashSet<FormKey> { seedFk },
                WantCycles = wantCycles,
            };
            // The seed's facts go in the shared memo too, for the seed that sits on another seed's chain.
            if (st.SeedTemplateFact is { } sf) templateFacts.TryAdd(seedFk, sf);

            // Reading the seed's own links parses its content, so a seed Mutagen cannot parse says so rather than
            // raising out of the call; every path still answers for ITSELF.
            List<FormKey> SeedLinks(string[]? segs, out string? note)
            {
                note = null;
                try { return LinksOf(seedBody, segs, out note); }
                catch (Exception ex) when (IsWalkRecordFault(ex))
                {
                    var fault = WalkFaultOf(ex);
                    seedFault ??= fault;
                    note = WalkUnscannableNote(fault);
                    return new List<FormKey>();
                }
            }

            // First hop: seed_paths (each path's links) or every link on the seed.
            if (seedPaths is { Count: > 0 })
            {
                foreach (var p in seedPaths)
                {
                    var segs = p.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (segs.Length == 0) continue;
                    var links = SeedLinks(segs, out var note);
                    if (links.Count == 0 && note is not null)
                        st.Nodes.Add(new WalkNodeRow($"(seed path '{p}')", null, null, 0, st.Label, "no links", note));   // a wrong path fails loudly in the rows
                    foreach (var l in links) { st.Edge(seedFk, l); st.Frontier.Enqueue((l, 1, $"{st.Label}.{p}")); }
                }
            }
            else
            {
                foreach (var l in SeedLinks(null, out _)) { st.Edge(seedFk, l); st.Frontier.Enqueue((l, 1, st.Label)); }
            }
            // The seed itself would not parse.
            if (seedFault is { } fault)
            {
                if (st.Frontier.Count == 0 && st.Nodes.Count == 0)
                    st.Error = $"{FormIdToken.Of(seedFk)} {WalkUnscannableNote(fault)}. Nothing to walk from.";
                if (nodeFacts is not null)
                    nodeFacts[seedFk] = new WalkNodeFact
                    {
                        Resolved = true, Type = seedType, EditorId = st.EditorId, Unscannable = fault,
                    };
            }
            states[i] = st;
        }
        // The slice's seed bodies have given up their identity and their first-hop links; they go now.
        if (bodyCache.Count > WalkBodyHighWater) WalkBodyHighWater = bodyCache.Count;
        bodyCache.Clear();
        }

        // The hops: every seed advances one hop together, so the hop's bodies are ONE gather, and a node at the depth
        // cap is recorded and not entered.
        var atLevel = new int[states.Length];
        for (int d = 1; d <= depth; d++)
        {
            ct.ThrowIfCancellationRequested();
            // How many queued items belong to THIS hop, snapshotted before anything is enqueued for the next one.
            bool pending = false;
            for (int i = 0; i < states.Length; i++)
            {
                atLevel[i] = states[i] is { } s0 ? s0.Frontier.Count : 0;
                if (atLevel[i] > 0) pending = true;
            }
            if (!pending) break;

            while (true)
            {
            ct.ThrowIfCancellationRequested();
            // The gather is bounded by what each seed can still RECORD, not by the size of its frontier.
            var frontier = new List<FormKey>();
            var gatherSeen = new HashSet<FormKey>();
            var take = new int[states.Length];
            bool more = false;
            for (int i = 0; i < states.Length; i++)
            {
                var s = states[i];
                if (s is null || atLevel[i] == 0) continue;
                more = true;
                int room = maxNodes - s.Nodes.Count;
                if (room <= 0) { take[i] = atLevel[i]; continue; }        // at its cap: its turn below records the cut
                int took = 0, seenItems = 0;
                foreach (var q in s.Frontier)
                {
                    if (seenItems >= atLevel[i] || frontier.Count >= WalkPassRows) break;
                    seenItems++;
                    // A key this seed already visited is dropped at dequeue, so gathering it would spend a slot
                    // on a body no row ever shows.
                    if (q.Key.IsNull || s.Visited.Contains(q.Key) || bodyCache.ContainsKey(q.Key)
                        || Memoised(q.Key, q.Depth) || !gatherSeen.Add(q.Key)) continue;
                    frontier.Add(q.Key);
                    if (++took >= room) break;
                }
                take[i] = seenItems;
            }
            if (!more) break;
            if (frontier.Count > 0) Prefetch(frontier);

            for (int si = 0; si < states.Length; si++)
            {
                var st = states[si];
                if (st is null || take[si] == 0) continue;
                ct.ThrowIfCancellationRequested();
                int thisPass = take[si];
                atLevel[si] -= thisPass;
                for (int q = 0; q < thisPass; q++)
                {
                    var (key, hop, pulledBy) = st.Frontier.Dequeue();
                    if (key.IsNull) continue;
                    // A revisit is deduped and nothing more; the edge that reached it is already recorded.
                    if (!st.Visited.Add(key)) continue;
                    if (st.Nodes.Count >= maxNodes)
                    {
                        st.Truncation = $"walk truncated: the {maxNodes}-node cap was reached — what is listed IS reached and proved; raise walk.max_nodes to walk further.";
                        st.Frontier.Clear();
                        atLevel[si] = 0;
                        break;
                    }
                    bool atCap = hop >= depth;
                    var fact = FactFor(key, atCap);
                    if (!fact.Resolved)
                    {
                        st.Nodes.Add(new WalkNodeRow(FormIdToken.Of(key), null, null, hop, pulledBy, "kept",
                                                     "unresolved — no active plugin defines this target (a missing endpoint)"));
                        continue;
                    }
                    var type = fact.Type!;
                    var excl = exclusions.FirstOrDefault(x => x.Match.Equals(type, StringComparison.OrdinalIgnoreCase));
                    if (excl.Match is not null)
                    {
                        // A refuse ends the whole call.
                        if (excl.Refuse)
                        {
                            refusal = $"the walk reached a {type} ({FormIdToken.Of(key)}, via {pulledBy}) — a node class this call excludes with severity 'refuse'. Nothing is returned for this call.";
                            // A refusal returns nothing, so the pass in hand is dead: release it here.
                            if (bodyCache.Count > WalkBodyHighWater) WalkBodyHighWater = bodyCache.Count;
                            bodyCache.Clear();
                            WalkBodiesHeldAtReturn = 0;
                            return Array.Empty<WalkSeedResult>();
                        }
                        st.Nodes.Add(new WalkNodeRow(FormIdToken.Of(key), type, fact.EditorId, hop, pulledBy, "kept", $"excluded ({type}, severity stop) — recorded as a boundary, not entered"));
                        continue;
                    }
                    // A node Mutagen could not parse: named, kept as a boundary, and the walk continues.
                    if (fact.Unscannable is { } unscannable)
                    {
                        st.Nodes.Add(new WalkNodeRow(FormIdToken.Of(key), type, fact.EditorId, hop, pulledBy, "kept",
                                                     WalkUnscannableNote(unscannable)));
                        // A node at the depth cap is a cut chain whatever else is true of it.
                        if (atCap)
                            st.Truncation ??= $"walk reached its depth cap ({depth}) on at least one chain — nodes at the cap are recorded, not entered; raise walk.depth to walk deeper.";
                        continue;
                    }
                    st.Nodes.Add(new WalkNodeRow(FormIdToken.Of(key), type, fact.EditorId, hop, pulledBy,
                                                 atCap ? "kept" : "expanded",
                                                 atCap ? $"at the walk.depth cap ({depth}) — not entered" : null));
                    if (atCap)
                    {
                        st.Truncation ??= $"walk reached its depth cap ({depth}) on at least one chain — nodes at the cap are recorded, not entered; raise walk.depth to walk deeper.";
                        continue;
                    }
                    var label = $"{type} {FormIdToken.Of(key)} ({fact.EditorId ?? "<no editorid>"})";
                    foreach (var l in fact.Links ?? EmptyKeys)
                        if (!l.IsNull) { st.Edge(key, l); st.Frontier.Enqueue((l, hop + 1, label)); }
                }
                // An empty frontier means this seed is finished, so the bookkeeping goes back now.
                if (st.Frontier.Count == 0) { st.Visited = new(); st.Frontier = new(); st.Settle(); }
            }
            // The pass is over: the bodies it gathered have given up their identity and their links, so they go
            // now rather than at the end of the call.
            if (bodyCache.Count > WalkBodyHighWater) WalkBodyHighWater = bodyCache.Count;
            bodyCache.Clear();
            }
        }

        var results = new List<WalkSeedResult>(seeds.Count);
        for (int i = 0; i < seeds.Count; i++)
        {
            if (states[i] is not { } st) { results.Add(rows[i]!); continue; }
            IReadOnlyList<NpcTemplateCategory>? templateReport = null;
            if (st.SeedTemplateFact is { } seedFact) templateReport = NpcTemplateReport(TemplateFactOf, seedFact, st.Key);
            // A seed that never ran a pass — no links off it at all — has not settled yet; one that did settled there.
            st.Settle();
            results.Add(new WalkSeedResult(FormIdToken.Of(st.Key), st.Type, st.EditorId, st.Nodes, st.Cycles!, st.Truncation, templateReport, st.Error, st.CyclesCapped));
        }
        WalkBodiesHeldAtReturn = bodyCache.Count;
        return results;
    }

    /// <summary>The NPC_ TemplateFlags interpreter: a SET flag means the category is inherited and the seed's own
    /// local data is masked, and the provider is the first record down the chain whose flag is CLEAR. A chain ending
    /// in a leveled actor resolves at runtime; a broken link is reported, never guessed.</summary>
    static IReadOnlyList<NpcTemplateCategory> NpcTemplateReport(Func<FormKey, WalkTemplateFact?> factOf,
                                                                WalkTemplateFact seed, FormKey seedFk)
    {
        var report = new List<NpcTemplateCategory>();
        foreach (NpcConfiguration.TemplateFlag flag in Enum.GetValues(typeof(NpcConfiguration.TemplateFlag)))
        {
            var name = flag.ToString();
            if (!seed.Flags.HasFlag(flag))
            {
                report.Add(new NpcTemplateCategory(name, false, FormIdToken.Of(seedFk), seed.EditorId,
                                                   "local data ACTIVE (flag clear)"));
                continue;
            }
            // Walk down: the provider is the first node NOT forwarding this category.
            var cur = seed;
            string? note = null; string? provKey = null; string? provEid = null;
            var hops = new HashSet<FormKey> { seedFk };
            while (true)
            {
                if (!cur.HasTemplate) { note = "flag SET but the template link is empty — the category inherits from nothing (worth a look)"; break; }
                var nextKey = cur.Template;
                if (!hops.Add(nextKey)) { note = $"template chain CYCLES at {nextKey} — no provider is reachable"; break; }
                if (factOf(nextKey) is not { } next) { note = $"template target {nextKey} is unresolved — the chain is broken here"; break; }
                // A node that resolves but will not parse is a different answer from a broken chain.
                if (next.Unscannable is { } bad) { note = $"template target {nextKey} {WalkUnscannableNote(bad)}"; break; }
                if (next.IsLeveled)
                { provKey = FormIdToken.Of(nextKey); provEid = next.EditorId; note = "a LEVELED actor — the concrete provider is rolled at runtime"; break; }
                if (!next.IsNpc)
                { note = $"template target {nextKey} is a {next.TypeName}, not an NPC or leveled actor"; break; }
                if (!next.Flags.HasFlag(flag))
                { provKey = FormIdToken.Of(nextKey); provEid = next.EditorId; break; }
                cur = next;
            }
            report.Add(new NpcTemplateCategory(name, true, provKey, provEid, note));
        }
        return report;
    }
}
