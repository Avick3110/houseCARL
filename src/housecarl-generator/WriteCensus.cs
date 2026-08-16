using System.Reflection;
using System.Text.Json;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlGenerator;

/// <summary>
/// The write-surface CENSUS (step 7) — the cornerstone-completeness scoreboard.
///
/// Replaces the originally-planned "breadth sweep" (plan §5.2), which a pre-build audit
/// (<c>dev/review/STEP7_SWEEP_AUDIT_2026-05-30.md</c>) proved would false-green: iterating the 133
/// record roots and picking one field per type silently never touches the ~5k writable leaves that
/// live behind a collection element, on a nested-group record, or on a polymorphic arm — yet would
/// still report green. That is the exact Q3 failure (a silently degraded mode) the project exists to
/// prevent.
///
/// Instead — the same discipline <c>coerce-audit</c> already proves for the VALUE surface, applied to
/// the REACHABILITY surface: walk EVERY writable non-identity leaf by construction, classify each by
/// the EARLIEST engine capability that makes it writable, and loud-list every deferred leaf with a
/// per-bucket WIRE-WHEN trigger — surfaced every run, never silently skipped.
///
/// Classification is by construction, on two axes the live engine code imposes today:
///   - record resolution: <see cref="WriteEngine.EnumerateFlatGroups"/> — only flat SkyrimGroup&lt;T&gt;
///     records resolve; nested-group records (Cell/Worldspace/Placed*/INFO/Navmesh/Landscape) do not.
///   - path navigation: the engine descends substructs only; mid-path collection traversal is rejected
///     (<c>CorpusRulebook</c>), so a leaf reachable only THROUGH a list/dict element is deferred.
///
/// The result is a count, re-run after each capability wave to watch the deferred buckets shrink toward
/// zero — that running count IS the proof that coverage is becoming complete by construction.
///
/// Run: <c>dotnet run --project src/housecarl-generator write-census</c>
/// </summary>
public static class WriteCensus
{
    public static int Run(string[] args)
    {
        var corpusPath = args.Length > 0 ? args[0] : CorpusRulebook.CorpusPath;
        Corpus corpus;
        try { corpus = CorpusRulebook.LoadCorpus(corpusPath); }
        catch (Exception ex) { Console.Error.WriteLine($"error: {ex.Message}"); return 1; }

        // ---- AXIS 1: which records resolve to a flat SkyrimGroup<T>? (shared with the engine) ----
        var asm = typeof(SkyrimMod).Assembly;
        var flatGetters = WriteEngine.EnumerateFlatGroups(typeof(SkyrimMod)).Select(g => g.getterIface).ToList();
        bool GroupReachable(string recordName)
        {
            var rGetter = asm.GetType("Mutagen.Bethesda.Skyrim.I" + recordName + "Getter");
            return rGetter is not null && flatGetters.Any(g => g.IsAssignableFrom(rGetter));
        }
        // EXCLUDE the SkyrimMod container itself: it is the mod object (kind "struct" — the read-only multi-mod
        // overlay arm is filtered as non-authorable, so it is not a union), not a game record type. Its ~120
        // "writable fields" are the 113 record-group containers + structural plumbing — not user-facing content
        // edits. Counting them would inflate the denominator with non-content and misattribute structural plumbing
        // to a deferred wave. The per-type leaf walk below excludes it by NAME (kind-independent), and its field
        // count is reported LOUDLY (modContainerFields), never silently dropped.
        const string ModContainer = "SkyrimMod";
        var records = corpus.Types.Values
            .Where(t => t.Kind == "record" && t.Name != ModContainer)
            .Select(t => t.Name).ToList();
        var flatRecords = records.Where(GroupReachable).ToHashSet(StringComparer.Ordinal);
        var nestedRecords = records.Where(r => !flatRecords.Contains(r)).ToHashSet(StringComparer.Ordinal);
        var modContainerFields = corpus.Types.TryGetValue(ModContainer, out var mc)
            ? mc.Fields.Count(f => f.Writable && !f.IsIdentity) : 0;

        // ---- AXIS 2: navigation adjacency from the corpus (substruct vs collection edges) ----
        var subAdj = new Dictionary<string, List<string>>(StringComparer.Ordinal);   // descend-a-substruct (today)
        var navAdj = new Dictionary<string, List<string>>(StringComparer.Ordinal);   // substruct + step-through-a-collection-element
        foreach (var name in corpus.Types.Keys) { subAdj[name] = new(); navAdj[name] = new(); }
        bool IsRecord(string name) => corpus.Types.TryGetValue(name, out var rt) && rt.Kind == "record";
        foreach (var t in corpus.Types.Values)
            foreach (var f in t.Fields)
            {
                switch (f.Cardinality)
                {
                    // …and the same rule on the SUBSTRUCT axis (#335). A substruct TypeRef could not be a record until
                    // the classifier stopped calling Cell.Landscape / Worldspace.TopCell FormLinks; without this guard
                    // the walk crosses Worldspace -> TopCell -> Cell and counts the whole nested-group world as
                    // reachable today (+87 leaves, and Cell's 44 fields leaving the deferred bucket while axis 1 still
                    // prints them as deferred — a scoreboard contradicting itself, which is the Q3 failure this census
                    // exists to prevent). Descending INTO a present child record is not the same capability as
                    // resolving the ~60k CELLs that need the nested-group wave.
                    case "substruct" when f.TypeRef is { } tr && !IsRecord(tr):
                        subAdj[t.Name].Add(tr); navAdj[t.Name].Add(tr); break;
                    // A collection element that is itself a RECORD is NOT unlocked by collection navigation —
                    // a record must be resolved (flat- or nested-group), never reached by walking into a parent's
                    // bytes. So only STRUCT collection-elements are collnav edges; record-elements are left to the
                    // record-resolution axis (the nested-group wave). This is the correctness fix that stops
                    // Cell/DialogResponses/Navmesh (records pointed at by a collection) being mislabeled collnav.
                    case "list" or "dict" when f.ElementTypeRef is { } er && !IsRecord(er):
                        navAdj[t.Name].Add(er); break;
                }
            }

        HashSet<string> Bfs(Dictionary<string, List<string>> adj, IEnumerable<string> roots)
        {
            var seen = new HashSet<string>(roots, StringComparer.Ordinal);
            var q = new Queue<string>(seen);
            while (q.Count > 0)
                if (adj.TryGetValue(q.Dequeue(), out var nbrs))
                    foreach (var m in nbrs) if (seen.Add(m)) q.Enqueue(m);
            return seen;
        }

        // EARLIEST-unlock reachability sets (each strictly adds to the previous):
        var today    = Bfs(subAdj, flatRecords);                       // resolve flat record + descend substructs
        var afterColl = Bfs(navAdj, flatRecords);                       // + step through a collection element
        var afterNested = Bfs(navAdj, records);                         // + resolve nested-group records too

        // ---- classify each TYPE into exactly one bucket (priority = earliest unlock) ----
        // ARM is its own axis (polymorphic Set), reported separately from the navigation waves.
        const string TODAY = "today", COLL = "wave_collnav", NESTED = "wave_nested",
                     ARM = "arm_surface", ORPHAN = "orphan";
        string Bucket(TypeSchema t)
        {
            if (t.Kind is "arm" or "polymorphic-base") return ARM;
            if (today.Contains(t.Name)) return TODAY;
            if (afterColl.Contains(t.Name)) return COLL;            // reachable from a flat record via a collection
            if (nestedRecords.Contains(t.Name) || afterNested.Contains(t.Name)) return NESTED;
            return ORPHAN;
        }

        // ---- walk every writable non-identity leaf, attribute to its owning type's bucket ----
        var leafCount = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var typeCount = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var cardInBucket = new Dictionary<string, SortedDictionary<string, int>>();
        var sampleOwners = new Dictionary<string, List<(string name, int wf)>>();
        foreach (var b in new[] { TODAY, COLL, NESTED, ARM, ORPHAN })
        { leafCount[b] = 0; typeCount[b] = 0; cardInBucket[b] = new(StringComparer.Ordinal); sampleOwners[b] = new(); }

        // Unifies the two completeness instruments: a leaf that is REACHABLE today but whose value type the
        // engine cannot yet coerce (the coerce-audit deferred surface) must not be counted as writable-today.
        var reachableButCoercionDeferred = new List<string>();
        // …and the leaves whose field holds an owned child record — reachable, but nothing writes AT them (#335).
        var ownedChildLeaves = new List<string>();
        int totalWritable = 0;
        // Of the TODAY list/dict fields, those with a STRUCT element are only structurally writable today
        // (Remove/Clear); ADDING a composed element is wave-1 (element composition), exactly as coerce-audit +
        // write-proof treat them. Counted here so the "writable today" headline is faithful about that nuance.
        int todayStructElemColl = 0;

        foreach (var t in corpus.Types.Values)
        {
            if (t.Name == ModContainer) continue; // structural container, excluded + reported separately
            var wfs = t.Fields.Where(f => f.Writable && !f.IsIdentity).ToList();
            if (wfs.Count == 0) continue;
            var b = Bucket(t);
            typeCount[b]++;
            sampleOwners[b].Add((t.Name, wfs.Count));
            foreach (var f in wfs)
            {
                totalWritable++;
                // Coercion cross-check: a leaf we'd otherwise call writable-today whose value type the engine
                // cannot yet coerce is NOT writable today — pull it out and tally it separately (unifies this
                // census with coerce-audit so the two completeness instruments can't disagree).
                if (b == TODAY && IsScalarish(f.Cardinality)
                    && ScalarAq(f) is { } aq && WriteEngine.ResolveType(aq) is { } rt && !WriteEngine.CanCoerce(rt))
                {
                    reachableButCoercionDeferred.Add($"{t.Name}.{f.Name} ({Pretty(rt)})");
                    continue;
                }
                // The same subtraction for an OWNED CHILD RECORD leaf (#335). Every write AT the field is refused —
                // it is not built from parts, not set from a value, and not cleared (that would delete the record) —
                // so counting it writable-today would inflate the headline by a field nothing can write. It was
                // already excluded while it was classified a formlink, by the coercion clause just above (a Cell
                // does not coerce); the reclassification moved it out of "scalarish" and the exclusion has to move
                // with it, or the correction silently buys a leaf. What IS writable is the child record itself, on
                // its own axis, and that record's own leaves are counted under its own type.
                // NOT gated on the TODAY bucket, unlike the coercion clause above. Bucket is a property of the leaf's
                // OWNER (Worldspace is flat → TODAY; Cell is nested → wave_nested), and the two owned-child leaves are
                // the same field kind in different owners. Gating on TODAY subtracted Worldspace.TopCell and left
                // Cell.Landscape sitting in the nested-wave bucket — promised to a wave that will never make it
                // writable, since no wave changes the answer for a field that holds a record. Same kind, same line.
                if (SchemaClassifier.IsOwnedChildRecord(f, corpus))
                {
                    ownedChildLeaves.Add($"{t.Name}.{f.Name} ({f.TypeRef})");
                    continue;
                }
                leafCount[b]++;
                var cb = cardInBucket[b];
                cb[f.Cardinality] = cb.GetValueOrDefault(f.Cardinality) + 1;
                if (b == TODAY && f.Cardinality is "list" or "dict" && !SchemaClassifier.CoercibleElement(f)) todayStructElemColl++;
            }
        }

        // ======================================================================
        //  REPORT — plain-English lay-of-the-land first (for Aaron), then the precise buckets.
        // ======================================================================
        Console.WriteLine("================================================================");
        Console.WriteLine(" houseCARL write-surface census");
        Console.WriteLine("================================================================");
        Console.WriteLine($"Mutagen models {totalWritable} writable content fields across {records.Count} game record types.");
        Console.WriteLine($"(Excluded: the SkyrimMod container's {modContainerFields} structural fields — record-group plumbing, not content.)");
        Console.WriteLine("Here is exactly what houseCARL can write today, and what is deferred (and when each is unlocked):");
        Console.WriteLine();
        var coercionDeferred = reachableButCoercionDeferred.Count;
        void Line(string b, string plain) =>
            Console.WriteLine($"  {leafCount[b],6} fields  ({typeCount[b],4} types)   {plain}");
        Line(TODAY,  "WRITABLE TODAY — engine reaches these directly");
        Line(COLL,   "DEFERRED  -> needs collection navigation   (fields inside lists/dicts, e.g. a spell's effects)");
        Line(NESTED, "DEFERRED  -> needs nested-group access      (world objects: cells, placed refs, navmesh, INFO)");
        Line(ARM,    "DEFERRED  -> polymorphic arm breadth        (condition data + arm sub-fields)");
        Line(ORPHAN, "DEFERRED  -> header / script metadata       (mod header, compiled-script internals)");
        if (coercionDeferred > 0)
            Console.WriteLine($"  {coercionDeferred,6} fields  (   - types)   DEFERRED  -> reachable but value-type coercion-deferred (see coerce-audit)");
        if (ownedChildLeaves.Count > 0)
            Console.WriteLine($"  {ownedChildLeaves.Count,6} fields  (   - types)   NOT A LEAF -> holds an owned child record; the record is written on its own axis ({string.Join(", ", ownedChildLeaves)})");
        Console.WriteLine("  ------");
        var grandTotal = leafCount.Values.Sum() + coercionDeferred + ownedChildLeaves.Count;
        Console.WriteLine($"  {grandTotal,6} fields total   (must equal {totalWritable}: {(grandTotal == totalWritable ? "OK" : "!! MISMATCH")})");
        var pctToday = totalWritable == 0 ? 0 : 100.0 * leafCount[TODAY] / totalWritable;
        Console.WriteLine();
        Console.WriteLine($"  => {leafCount[TODAY]} of {totalWritable} ({pctToday:F0}%) writable today by the proven mechanism; the rest is mapped to a named wave below.");
        Console.WriteLine();

        // ---- the navigation reachability detail (records axis) ----
        Console.WriteLine("---- record resolution (axis 1) ----");
        Console.WriteLine($"  flat-group records (resolvable today): {flatRecords.Count}/{records.Count}");
        Console.WriteLine($"  nested-group records (deferred):       {nestedRecords.Count}/{records.Count}  [WIRE-WHEN: nested-group record-resolution wave]");
        foreach (var n in nestedRecords.OrderBy(x => x, StringComparer.Ordinal))
        {
            var wf = corpus.Types[n].Fields.Count(f => f.Writable && !f.IsIdentity);
            Console.WriteLine($"       {n,-22} sig={corpus.Types[n].Signature ?? "?",-5} writable-fields={wf}");
        }
        Console.WriteLine();

        // ---- per deferred bucket: WIRE-WHEN + the heaviest owners (so the value is visible) ----
        void DumpBucket(string b, string wireWhen)
        {
            Console.WriteLine($"---- DEFERRED bucket '{b}': {leafCount[b]} fields / {typeCount[b]} types ----");
            Console.WriteLine($"     WIRE-WHEN: {wireWhen}");
            var card = cardInBucket[b];
            if (card.Count > 0)
                Console.WriteLine("     by cardinality: " + string.Join(", ", card.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")));
            var top = sampleOwners[b].OrderByDescending(o => o.wf).Take(12).ToList();
            if (top.Count > 0)
                Console.WriteLine("     heaviest owners: " + string.Join(", ", top.Select(o => $"{o.name}({o.wf})")));
            Console.WriteLine();
        }
        DumpBucket(COLL,   "the path step can pass THROUGH a list/dict element by key/index to reach a sub-leaf (lift CorpusRulebook's mid-path collection rejection); add oracle cells e.g. Spell -> Effects[0] -> Data.Magnitude.");
        DumpBucket(NESTED, "the lifecycle can GetOrAddAsOverride a record stored in a nested group (Cell/Worldspace/Placed*/INFO/Navmesh/Landscape); add an oracle cell per nested shape.");
        DumpBucket(ARM,    "a breadth proof sweeps arm-set + arm sub-field coercion across all polymorphic unions (not just the proven MagicEffect->Light). NOTE: condition arms are double-blocked — also behind the Conditions collection AND the typed-value coercion deferral (coerce-audit's 202).");
        DumpBucket(ORPHAN, "ModHeader half PROVEN — write-proof Phase 11 (wave 5; see that phase for detail). PEX half (~75 fields) DEFERRED: the " +
            "Mutagen PEX byte-identity gate did NOT clear (pex-probe: 101/400 byte-identical, the rest a benign string-table re-order — not a surgical " +
            "write) — a Mutagen-delta residual; prefer .psc source (project_pex_prefer_source_policy). Bucket does NOT zero (Option A); reconciles at the final sweep.");

        // ---- coercion cross-check result (unify the two instruments) ----
        Console.WriteLine("---- coercion cross-check (vs coerce-audit) ----");
        if (coercionDeferred == 0)
            Console.WriteLine("     none — every reachable-today leaf's value type is coercible (the two instruments agree).");
        else
        {
            Console.WriteLine($"     {coercionDeferred} leaf/leaves reachable today but value-type coercion-deferred (correctly NOT counted as writable-today):");
            foreach (var s in reachableButCoercionDeferred.Take(20)) Console.WriteLine($"       {s}");
        }
        Console.WriteLine();

        // ---- TODAY coverage matrix: what the byte-true proof must cover ----
        Console.WriteLine("---- TODAY surface by cardinality (what the reachable-proof harness must cover byte-true) ----");
        foreach (var kv in cardInBucket[TODAY].OrderByDescending(kv => kv.Value))
            Console.WriteLine($"     {kv.Key,-12} {kv.Value}");
        if (todayStructElemColl > 0)
            Console.WriteLine($"     (of the list/dict above, {todayStructElemColl} have a STRUCT element: editable today via Remove/Clear, " +
                              "and ADDING a composed element is wave-1 half B — NOW IMPLEMENTED, byte-proven by write-proof Phase 6.)");
        Console.WriteLine();

        // Authoritative machine-readable emit — written directly to disk (NOT via the console/tee path,
        // which this environment intermittently corrupts), so the partition can be verified byte-exactly.
        var summary = new
        {
            totalWritable,
            gameRecordTypes = records.Count,
            excludedModContainerFields = modContainerFields,
            flatGroupRecords = flatRecords.Count,
            nestedGroupRecords = nestedRecords.Count,
            buckets = new
            {
                today = leafCount[TODAY],
                wave_collnav = leafCount[COLL],
                wave_nested = leafCount[NESTED],
                arm_surface = leafCount[ARM],
                orphan = leafCount[ORPHAN],
                coercion_deferred = coercionDeferred,
                today_struct_element_collections_wave1 = todayStructElemColl,
            },
            bucketTypes = new
            {
                today = typeCount[TODAY],
                wave_collnav = typeCount[COLL],
                wave_nested = typeCount[NESTED],
                arm_surface = typeCount[ARM],
                orphan = typeCount[ORPHAN],
            },
            sum = leafCount.Values.Sum() + coercionDeferred,
            nestedGroupRecordNames = nestedRecords.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            reachableButCoercionDeferred,
        };
        var outDir = Path.GetFullPath("write-output");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "write-census.json"),
            JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));

        // The census MEASURES; it does not itself prove the reachable surface byte-true (that is the proof
        // harness, the next increment). It also never silently passes: it asserts its own partition is total.
        var partitionOk = grandTotal == totalWritable;
        Console.WriteLine(partitionOk
            ? $"=== CENSUS COMPLETE: {totalWritable} writable leaves fully partitioned; {leafCount[TODAY]} writable today, {totalWritable - leafCount[TODAY]} deferred across named waves. ==="
            : "=== CENSUS PARTITION ERROR: buckets do not sum to the writable total — investigate before trusting any number above. ===");
        return partitionOk ? 0 : 1;
    }

    static bool IsScalarish(string cardinality) =>
        cardinality is "scalar" or "enum" or "value" or "formlink";

    // CoercibleElement is now SchemaClassifier.CoercibleElement(f) — see SchemaClassifier.cs.

    static string? ScalarAq(FieldSchema f) =>
        f.MutableTypeAssemblyQualified ?? f.GetterTypeAssemblyQualified;

    static string Pretty(Type t)
    {
        if (!t.IsGenericType) return t.Name;
        var n = t.Name; var i = n.IndexOf('`'); if (i > 0) n = n[..i];
        return $"{n}<{string.Join(", ", t.GetGenericArguments().Select(Pretty))}>";
    }
}
