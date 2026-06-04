using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlGenerator;

/// <summary>
/// Wave 0 — the write-PROOF harness. Proves the census's "writable today" surface (≈2,900 leaves on
/// flat-group records) is not merely <i>reachable</i> but actually <b>writes the intended value and disturbs
/// nothing else</b>, across real vanilla records, by construction. The roadmap
/// (<c>dev/plans/WRITE_SURFACE_ROADMAP_2026-05-30.md</c> §3/§6) makes this the wave that BUILDS the
/// only-target-moved differ every later wave reuses.
///
/// Two phases, both fail-loud (Q3 — never a silent skip; a silent skip is the read-side false-green the
/// step-7 sweep audit caught):
///
///   PHASE 1 — DIFFER COMPLETENESS (the load-bearing primitive).
///     A corpus-driven reflective <see cref="Snapshot"/> walks EVERY modeled field of a record into a flat
///     path→value-token map. It descends substructs, struct list/dict elements, and polymorphic arms, but
///     treats a field that targets another RECORD as a leaf reference (records its FormKey, does not recurse
///     — that is a separate record, the nested-group wave's surface). Driven by the corpus field lists (the
///     same source of truth as the validator), so it reads exactly the modeled surface; it FAILS LOUD on any
///     field whose kind it cannot classify or any leaf type it cannot reduce to a value token (reference- or
///     runtime-type equality would manufacture false diffs). Phase 1 snapshots one real instance of every
///     flat-group record and asserts the walker read the whole tree with zero loud failures.
///
///   PHASE 2 — ONLY-TARGET-MOVED + VALUE-LANDED (the actual write proof).
///     For each real settable leaf on the today surface, snapshot a fresh override, apply the engine edit to
///     ANOTHER fresh override of the same source, snapshot, and assert: (a) the intended value LANDED at the
///     target, and (b) every changed path lies WITHIN the target's subtree — except the record-flags alias
///     family (IsCompressed/IsDeleted/MajorRecordFlagsRaw/… all project ONE storage int; intra-family moves
///     are expected and that family is DERIVED empirically, not hand-listed). Each request is also cross-checked
///     against the <see cref="CorpusRulebook"/> pre-flight: the proof's leaf-enumeration and the rulebook are
///     two independently-derived definitions of "today surface" — they must agree. Edits are synthesised to a
///     value GUARANTEED different from the current one where the type allows, so "landed" proves a real change
///     propagates (vacuous edits — value already equalled the sample — are tallied honestly, not hidden).
///
/// What this does NOT re-prove (covered elsewhere, cited so the proof's scope is honest):
///   - serialization byte-identity per kind — the oracle (<see cref="WriteOracle"/>, 17/17 byte-identical);
///   - value-type construction — coerce-selftest; coercion completeness — coerce-audit;
///   - the deferred surface (collection-nav / nested-group / arm-breadth / header) — the census loud-lists it.
///
/// Run: <c>dotnet run --project src/housecarl-generator write-proof</c>
/// </summary>
public static class WriteProof
{
    // The modlist's own canonical game root — its isolated "Stock Game" instance, paired with the
    // mods/ tree — NOT the global Steam install. The full vanilla master set lives here; loading all five
    // (vs Skyrim.esm alone) is the residual lever the wave-0 handoff named: more real Bethesda instances
    // populate the optional paths a single master leaves empty.
    const string DefaultDataDir = @"C:\MO2\Instance\Stock Game\Data";
    static readonly string[] VanillaMasters =
        { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" };
    // Beyond vanilla, the live mods/ tree. The heaviest mod plugins (Requiem, the modlist's own outputs, LOTD, the
    // quest mods, ...) populate the optional fields no vanilla master ever fills — driving the residual toward
    // zero AND proving the engine against real MODDED records. Loaded the same standalone way (no load-order
    // assembly: we snapshot records, never resolve cross-plugin links). Curated by record density (largest =
    // most records); the full-folder sweep (all ~3,400 plugins) is a separate, larger pass.
    const string DefaultModsDir = @"C:\MO2\Instance\mods";
    const int TopModPluginsBySize = 50;
    const int GlobalCapPerType = 1024; // backstop on total pooled instances per type across all sources (keeps Phase 2 bounded)
    static readonly ModKey PatchKey = new("HousecarlWriteProof", ModType.Plugin);
    // An empty mod whose link cache contains nothing — used by Phase 8 to drive the "record absent from cache"
    // fail-closed guard (resolving any real record against it must throw, never silently resolve).
    static readonly ModKey EmptyProbeKey = new("HousecarlEmptyProbe", ModType.Plugin);
    const int InstancesPerType = 64;   // pool size per record type PER MASTER — DLC instances must not be crowded out by Skyrim.esm's share

    public static int RunProof(string[] args)
    {
        // Sources: explicit paths from args, else the full vanilla master set + the top-N mod plugins by size.
        var requested = args.Length > 0 ? args : DefaultSources();
        var existing = requested.Where(File.Exists).ToList();
        var missing = requested.Where(s => !File.Exists(s)).Select(Path.GetFileName).ToList();

        var corpus = CorpusRulebook.LoadCorpus();
        var rulebook = CorpusRulebook.Load();

        // Load each as a standalone binary overlay. A plugin that won't parse is surfaced LOUD, never silently
        // dropped (Q3) — at modlist scale a real differ/format gap must show, not vanish into a skipped source.
        var sources = new List<(string label, ISkyrimModGetter overlay)>();
        var loaded = new List<string>();
        var loadFailures = new List<string>();
        foreach (var s in existing)
        {
            try { sources.Add((Path.GetFileName(s)!, (ISkyrimModGetter)SkyrimMod.CreateFromBinaryOverlay(s, SkyrimRelease.SkyrimSE))); loaded.Add(s); }
            catch (Exception ex) { loadFailures.Add($"{Path.GetFileName(s)}: {Flatten(ex)}"); }
        }
        if (sources.Count == 0) { Console.Error.WriteLine($"error: no source plugins loaded (looked in {DefaultDataDir} + {DefaultModsDir})"); return 1; }

        // SHA every loaded source up front; re-checked at the end to prove no plugin file was mutated.
        var shaBefore = loaded.ToDictionary(s => s, Sha, StringComparer.OrdinalIgnoreCase);
        var ctx = new WalkContext(corpus);

        // One pool of real instances per flat-group record type, pooled ACROSS all sources (up to InstancesPerType
        // per source, GlobalCapPerType total) so mod records populate the optional paths vanilla leaves empty.
        var pools = CollectInstancePools(sources, corpus, InstancesPerType, GlobalCapPerType, out var emptyRecordTypes, out var perMasterContrib);

        Console.WriteLine("================================================================");
        Console.WriteLine(" houseCARL write-proof (wave 0 — prove-today)");
        Console.WriteLine("================================================================");
        Console.WriteLine($"Sources pooled: {sources.Count} plugin(s) — {VanillaMasters.Length} vanilla master(s) + up to {TopModPluginsBySize} top mod plugins by size.");
        Console.WriteLine($"   vanilla: {DefaultDataDir}");
        Console.WriteLine($"   mods   : {DefaultModsDir}");
        var contribOrdered = perMasterContrib.OrderByDescending(m => m.instances).ToList();
        foreach (var (label, instances, types) in contribOrdered.Take(16))
            Console.WriteLine($"   {label,-46} {instances,7} instances across {types,3} types");
        if (contribOrdered.Count > 16)
            Console.WriteLine($"   ... +{contribOrdered.Count - 16} more sources contributing {contribOrdered.Skip(16).Sum(m => m.instances)} instances");
        if (loadFailures.Count > 0)
        {
            Console.WriteLine($"   !! {loadFailures.Count} source(s) FAILED to load (surfaced not skipped):");
            foreach (var lf in loadFailures.Take(20)) Console.WriteLine($"        {lf}");
            if (loadFailures.Count > 20) Console.WriteLine($"        ... +{loadFailures.Count - 20} more");
        }
        if (missing.Count > 0)
            Console.WriteLine($"   (requested but absent, surfaced not skipped: {string.Join(", ", missing)})");
        Console.WriteLine($"Flat-group record types with a real instance across these sources: {pools.Count}");
        if (emptyRecordTypes.Count > 0)
            Console.WriteLine($"Flat-group record types with NO instance in any loaded source (cannot prove from here, " +
                              $"surfaced not skipped): {emptyRecordTypes.Count} -> {string.Join(", ", emptyRecordTypes.OrderBy(x => x))}");
        Console.WriteLine();

        // The full known-master set so cross-master refs serialize (used by the byte-identity phases 3/4/6).
        var allMasters = sources.Select(s => s.overlay).ToArray();

        // Run the eight proof phases in order (1→8). Order is load-bearing: some phases read shared ctx state
        // (e.g. ctx.DeferredElementComposition is populated by TodaySettableLeaves and printed during Phase 2;
        // later phases call TodaySettableLeaves again and append more), so Phase 2's printed count must reflect
        // only its own pass. Each returns a PhaseResult whose Pass folds into the verdict — no hand-written
        // boolean conjunction to forget when a future phase is added (fail-CLOSED).
        var p1 = Phase1_DifferCompleteness(pools, ctx);
        var p2 = Phase2_OnlyTargetMoved(pools, ctx, rulebook);
        var p3 = Phase3_AbsentCollection(pools, ctx, allMasters);
        var p4 = Phase4_AbsentSubstruct(pools, ctx, allMasters);
        var p5 = Phase5_CollectionNav(pools, ctx, rulebook);
        var p6 = Phase6_StructElement(pools, ctx, allMasters);
        var p7 = Phase7_NestedGroup(sources, ctx, rulebook, allMasters);
        var p8 = Phase8_NestedFailClosed(sources, ctx);
        var p9 = Phase9_ArmFloiByteTrue(sources, rulebook, allMasters);
        var p10 = Phase10_ArmFloiFailClosed(sources, rulebook);
        var p11 = Phase11_ModHeader(sources, ctx, rulebook, allMasters);
        var results = new PhaseResult[] { p1, p2, p3, p4, p5, p6, p7, p8, p9, p10, p11 };

        var changedSrcs = loaded.Where(s => !string.Equals(shaBefore[s], Sha(s), StringComparison.OrdinalIgnoreCase))
                                .Select(Path.GetFileName).ToList();
        var srcOk = changedSrcs.Count == 0;
        Console.WriteLine($"Sources unchanged: {(srcOk ? $"YES (all {loaded.Count})" : $"NO — mutated: {string.Join(", ", changedSrcs)}")}");

        // ENGINE proof = the wave-0 deliverable: differ complete + every exercised today-settable leaf lands
        // surgically + source untouched. Rulebook drift is reported but does NOT gate the engine verdict
        // (a validator bug is a separate fix; a false-ACCEPT, however, means the engine threw — already a throw).
        var enginePass = results.All(r => r.Pass) && srcOk;
        Console.WriteLine();
        Console.WriteLine(enginePass
            ? $"=== WRITE-PROOF: ENGINE PROVEN — differ complete; {p2.ProvenTotal}/{p2.AttemptTotal} settable leaf-sites only-target-moved + value-landed " +
              $"(incl. add-to-absent-collection); {p3.ByteOk} byte-identical (+{p3.ByteEquiv} serialize-equivalent) collections native==rebuilt; " +
              $"{p4.SsOk}+{p4.SsEquiv}/{p4.SsChecked} absent-substructs materialized==native (incl. GenderedItem composition; {p4.SsCompSites} still-deferred); " +
              $"{p5.CnProven}/{p5.CnAttempt} collection-nav leaf-sites proven (wave 1; arm sub-leaves behind collnav -> wave 4); " +
              $"{p6.SeOk}+{p6.SeEquiv}/{p6.SeChecked} struct-element Add-from-parts byte-identical (wave-1 half B; arm-bearing elements -> wave 4); " +
              $"{p7.LeavesProven}/{p7.LeavesAttempted} nested-group leaf-sites proven across {p7.TypesProven}/{p7.TypesInstanceBearing} instance-bearing nested types (wave 3; {p7.AbsentNested} absent nested types loud-listed); " +
              $"{p8.CasesLoud}/{p8.CasesRun} nested fail-closed negative cases threw loud (wave-3 guard; missing/uncacheable nested record never silently resolves); " +
              $"{p9.SitesProven}/{p9.SitesSampled} condition arm/FLOI sites byte-true (wave 4; {p9.FormProven} form + {p9.IndexProven} index; {p9.DistinctT} distinct target-T; {p9.AbsentSites} absent sites loud-listed; {p9.ArmFieldOk}/{p9.ArmFieldChecked} arm sub-field re-sets byte-true); " +
              $"{p10.CasesLoud}/{p10.CasesRun} arm/FLOI fail-closed negative cases threw loud (wave-4 guard; a malformed condition target never writes wrong bytes); " +
              $"{p11.LeavesProven}/{p11.LeavesAttempted} ModHeader leaf-sites only-target-moved + value-landed + {p11.StructAddProven}/{p11.StructAddAttempted} struct-element Add (wave 5; serialize round-trip {(p11.RoundTripOk ? "OK" : "FAIL")}; PEX-write loud-deferred — Mutagen round-trip gate not cleared, project_pex_prefer_source_policy); " +
            $"{p2.ProvenLeafKeys}/{p2.AllLeafKeys} distinct today leaves proven; {p2.NoInstance} residual (no instance, surfaced); sources untouched. ==="
            : "=== WRITE-PROOF INCOMPLETE: see loud failures above ===");
        if (enginePass && (p2.FalseRejects > 0 || p2.FalseAccepts > 0))
            Console.WriteLine($"    NOTE: {p2.FalseRejects} rulebook false-reject(s) + {p2.FalseAccepts} false-accept(s) reported above — pre-flight validator drift to fix separately.");
        return enginePass ? 0 : 1;
    }

    // ======================================================================
    //  The eight proof phases — each a self-contained pass returning a PhaseResult
    //  whose Pass folds into RunProof's verdict (fail-CLOSED).
    // ======================================================================

    internal record PhaseResult(string Name, bool Pass);
    sealed record Phase2Result(bool Pass, int ProvenTotal, int AttemptTotal, int ProvenLeafKeys, int AllLeafKeys, int NoInstance, int FalseRejects, int FalseAccepts)
        : PhaseResult("Phase 2 — only-target-moved + value-landed", Pass);
    sealed record Phase3Result(bool Pass, int ByteOk, int ByteEquiv, int ByteChecked)
        : PhaseResult("Phase 3 — absent-collection materialization byte-identity", Pass);
    sealed record Phase4Result(bool Pass, int SsOk, int SsEquiv, int SsChecked, int SsCompSites)
        : PhaseResult("Phase 4 — absent-substruct materialization byte-identity", Pass);
    sealed record Phase5Result(bool Pass, int CnProven, int CnAttempt)
        : PhaseResult("Phase 5 — collection-nav", Pass);
    sealed record Phase6Result(bool Pass, int SeOk, int SeEquiv, int SeChecked)
        : PhaseResult("Phase 6 — struct-element composition byte-identity", Pass);
    sealed record Phase7Result(bool Pass, int TypesProven, int TypesInstanceBearing, int LeavesProven, int LeavesAttempted, int AbsentNested)
        : PhaseResult("Phase 7 — nested-group byte-true", Pass);
    sealed record Phase8Result(bool Pass, int CasesLoud, int CasesRun, int TypesExercised, int AbsentCovered)
        : PhaseResult("Phase 8 — nested-group fail-closed", Pass);
    sealed record Phase9Result(bool Pass, int SitesProven, int SitesSampled, int FormProven, int IndexProven, int DistinctT, int AbsentSites, int ArmFieldOk, int ArmFieldChecked)
        : PhaseResult("Phase 9 — arm/FLOI byte-true", Pass);
    sealed record Phase10Result(bool Pass, int CasesLoud, int CasesRun)
        : PhaseResult("Phase 10 — arm/FLOI fail-closed", Pass);
    sealed record Phase11Result(bool Pass, int LeavesProven, int LeavesAttempted, int StructAddProven, int StructAddAttempted, bool RoundTripOk)
        : PhaseResult("Phase 11 — ModHeader (orphan) byte-true", Pass);

    static void DumpList(string header, List<string> items)
    {
        if (items.Count == 0) { Console.WriteLine($"   {header}: none."); return; }
        Console.WriteLine($"   !! {header}: {items.Count}");
        foreach (var v in items.Take(40)) Console.WriteLine($"        {v}");
        if (items.Count > 40) Console.WriteLine($"        ... +{items.Count - 40} more");
    }

    static PhaseResult Phase1_DifferCompleteness(List<(string typeName, List<IMajorRecordGetter> pool)> pools, WalkContext ctx)
    {
        // ============ PHASE 1 — DIFFER COMPLETENESS ============
        Console.WriteLine("---- PHASE 1: differ completeness (snapshot every modeled field, fail loud on any unreadable kind) ----");
        long totalLeaves = 0; int snapFail = 0;
        foreach (var (typeName, pool) in pools)
        {
            try { totalLeaves += Snapshot(pool[0], typeName, ctx).Count; }
            catch (Exception ex) { snapFail++; Console.WriteLine($"   !! SNAPSHOT FAILED on {typeName}: {Flatten(ex)}"); }
        }
        Console.WriteLine($"   snapshotted {pools.Count - snapFail}/{pools.Count} record instances; {totalLeaves} total leaves read.");
        if (ctx.UnknownKinds.Count > 0)
        {
            Console.WriteLine($"   !! {ctx.UnknownKinds.Count} UNCLASSIFIABLE field kind(s) — the differ would silently skip these (false-green risk):");
            foreach (var u in ctx.UnknownKinds.Take(60)) Console.WriteLine($"        {u}");
            if (ctx.UnknownKinds.Count > 60) Console.WriteLine($"        ... +{ctx.UnknownKinds.Count - 60} more");
        }
        var phase1Ok = snapFail == 0 && ctx.UnknownKinds.Count == 0;
        Console.WriteLine($"   PHASE 1: {(phase1Ok ? "COMPLETE — differ reads the whole modeled surface, no silent skips" : "INCOMPLETE — see loud failures above")}");
        Console.WriteLine();
        return new PhaseResult("Phase 1 — differ completeness", phase1Ok);
    }

    static Phase2Result Phase2_OnlyTargetMoved(List<(string typeName, List<IMajorRecordGetter> pool)> pools, WalkContext ctx, CorpusRulebook rulebook)
    {
        // ============ PHASE 2 — ONLY-TARGET-MOVED + VALUE-LANDED ============
        Console.WriteLine("---- PHASE 2: only-target-moved + value-landed across the today settable-leaf surface ----");
        var byCard = new SortedDictionary<string, int[]>(StringComparer.Ordinal); // card -> [proven, attempted, vacuous]
        var provenLeafKeys = new HashSet<string>(StringComparer.Ordinal);          // distinct owner.field proven (census reconcile)
        var allLeafKeys = new HashSet<string>(StringComparer.Ordinal);             // distinct owner.field considered
        var violations = new List<string>();
        var throwsList = new List<string>();
        var falseRejects = new List<string>();   // engine PROVED it but the rulebook rejected it (validator too strict)
        var falseAccepts = new List<string>();   // engine could NOT do it but the rulebook accepted it (validator too lax)
        var noInstance = new List<string>();
        var sampleFamily = new List<string>();   // one derived alias family, for the report
        int attemptTotal = 0, provenTotal = 0;

        foreach (var (typeName, pool) in pools)
        {
            List<TodayLeaf> leaves;
            try { leaves = TodaySettableLeaves(typeName, ctx); }
            catch (Exception ex) { violations.Add($"{typeName}: leaf-enum threw: {Flatten(ex)}"); continue; }

            // Per-type record-flags alias family — DERIVED empirically (set MajorRecordFlagsRaw on THIS type,
            // observe what co-moves), never hand-listed. Captures the type-specific MajorFlags enum too. These
            // fields all project one storage int, so intra-family moves are expected, not a leak.
            var aliasFamily = DeriveRecordFlagAliasFamily(typeName, pool, ctx);
            if (sampleFamily.Count == 0 && aliasFamily.Count > 2) sampleFamily = aliasFamily.OrderBy(x => x).ToList();

            var beforeCache = new Dictionary<IMajorRecordGetter, Dictionary<string, object?>>();

            foreach (var leaf in leaves)
            {
                allLeafKeys.Add(leaf.LeafKey);
                if (!byCard.TryGetValue(leaf.Cardinality, out var tally)) byCard[leaf.Cardinality] = tally = new int[3];

                var inst = pool.FirstOrDefault(r => Navigable(r, leaf));
                if (inst is null) { noInstance.Add($"{leaf.Display} [{leaf.Cardinality}]"); continue; }

                tally[1]++; attemptTotal++;
                if (!beforeCache.TryGetValue(inst, out var before))
                    beforeCache[inst] = before = Snapshot(MakeOverride(inst), typeName, ctx);

                var ov = MakeOverride(inst);
                WriteRequest req; string landKey; object? expectToken; bool distinct;
                try { (req, landKey, expectToken, distinct) = BuildEdit(leaf, ov, ctx); }
                catch (Exception ex) { throwsList.Add($"{leaf.Display} [{leaf.Cardinality}]: edit-synth threw {Flatten(ex)}"); continue; }

                var ruleReject = rulebook.Validate(req);   // SOFT cross-check — never blocks the engine proof
                try
                {
                    WriteEngine.ApplyVerb(ov, req);
                    var after = Snapshot(ov, typeName, ctx);

                    var allowExtra = aliasFamily.Contains(leaf.LeafFieldName) ? aliasFamily : EmptySet;
                    var outside = ChangedOutsideTarget(before, after, leaf.PathPrefix, allowExtra);
                    var landed = Landed(after, landKey, expectToken, leaf);

                    if (outside.Count == 0 && landed)
                    {
                        tally[0]++; provenTotal++; provenLeafKeys.Add(leaf.LeafKey);
                        if (!distinct) tally[2]++;
                        if (ruleReject is not null) falseRejects.Add($"{leaf.Display} [{leaf.Cardinality}]: engine PROVED it; rulebook rejected: {ruleReject}");
                    }
                    else if (!landed)
                        violations.Add($"{leaf.Display} [{leaf.Cardinality}]: value did NOT land (expected {landKey}={expectToken}, got {Tok(after.GetValueOrDefault(landKey))})");
                    else
                        violations.Add($"{leaf.Display} [{leaf.Cardinality}]: {outside.Count} field(s) moved OUTSIDE target (e.g. {outside[0]})");
                }
                catch (Exception ex)
                {
                    throwsList.Add($"{leaf.Display} [{leaf.Cardinality}]: threw {Flatten(ex)}");
                    if (ruleReject is null) falseAccepts.Add($"{leaf.Display} [{leaf.Cardinality}]: engine threw but rulebook ACCEPTED — {Flatten(ex)}");
                }
            }
        }
        Console.WriteLine($"   record-flags alias family (derived per type; sample): {(sampleFamily.Count > 0 ? string.Join(", ", sampleFamily) : "(none)")}");
        Console.WriteLine();

        // ============ REPORT ============
        Console.WriteLine($"   settable leaf-sites attempted: {attemptTotal}  (proven {provenTotal})");
        Console.WriteLine("   by cardinality (proven/attempted, vacuous=sample equalled current value):");
        foreach (var (card, t) in byCard)
            Console.WriteLine($"        {card,-12} {t[0]}/{t[1]}" + (t[2] > 0 ? $"  ({t[2]} vacuous)" : ""));
        Console.WriteLine();
        Console.WriteLine($"   distinct (type.field) census-today leaves: {allLeafKeys.Count} considered, {provenLeafKeys.Count} proven by ≥1 site.");

        if (ctx.DeferredElementComposition.Count > 0)
            Console.WriteLine($"   DEFERRED-FROM-PHASE-2 (inline attempt only — composition CAPABILITY PROVEN in Phase 6, Add-from-parts 116/117) — struct-element list/dict fields needing element COMPOSITION " +
                              $"(wave 1, matches coerce-audit): {ctx.DeferredElementComposition.Count} field-sites.");
        Console.WriteLine();
        DumpList("ONLY-TARGET-MOVED / VALUE-LANDED violations (engine defects)", violations);
        DumpList("THROWS (engine could not perform a today-settable write)", throwsList);
        // Cross-check drift (the proof's leaf-enumeration vs the rulebook pre-flight). A false-reject is a
        // rulebook bug that does NOT block the engine; a false-accept is the rulebook being too lax.
        DumpList("RULEBOOK FALSE-REJECTS (engine proved it, pre-flight wrongly rejected — validator bug)", falseRejects);
        DumpList("RULEBOOK FALSE-ACCEPTS (engine could not, pre-flight wrongly accepted — validator too lax)", falseAccepts);
        Console.WriteLine();
        // No instance to exercise these from = a master-coverage residual, not an engine defect. Surfaced, not skipped.
        var noInstByType = noInstance
            .Select(s => s.Split('.')[0]).GroupBy(x => x).OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key}({g.Count()})").ToList();
        Console.WriteLine($"   COVERAGE RESIDUAL — today leaf-sites with no populated instance in ANY loaded source: {noInstance.Count}" +
                          (noInstByType.Count > 0 ? $" across types: {string.Join(", ", noInstByType)}" : ""));
        Console.WriteLine("        (lever: load more plugins — raise TopModPluginsBySize / full mods sweep — or synthesize instances; surfaced, never counted as proven.)");
        var unprovenLeafKeys = allLeafKeys.Except(provenLeafKeys).OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (unprovenLeafKeys.Count > 0)
            Console.WriteLine($"        distinct unproven (type.field) leaf-keys ({unprovenLeafKeys.Count}): {string.Join(", ", unprovenLeafKeys)}");
        Console.WriteLine();
        return new Phase2Result(violations.Count == 0 && throwsList.Count == 0,
            provenTotal, attemptTotal, provenLeafKeys.Count, allLeafKeys.Count, noInstance.Count, falseRejects.Count, falseAccepts.Count);
    }

    // ======================================================================
    //  STEP 6 — READ-PROOF: the read↔write round-trip oracle.
    //
    //  For every coercible VALUE leaf the write surface drives, read its current value as a token
    //  (ReadEngine.EmitToken — the inverse of WriteEngine.Coerce), write that EXACT token straight
    //  back through the engine, and assert the only-target-moved differ sees NOTHING move: the
    //  round-trip is a perfect no-op. A no-op proves, BY CONSTRUCTION across the whole value-leaf
    //  surface, that the read token is the faithful inverse of the coercer — if any EmitToken family
    //  drifts from Coerce, writing the read token back changes the record and the proof fails LOUD,
    //  naming the type.field + the token. The read analog of write-proof's ENGINE PROVEN.
    //
    //  Scope: single-token VALUE leaves (scalar / value / enum / formlink + the whole-coercible
    //  TranslatedString substruct) — the exact set Phase 2 drives as a scalar Set. Collection / dict /
    //  polymorphic leaves are set via Add / Struct, not a token; their ELEMENT values reuse the SAME
    //  EmitToken proven here, so proving the emitter on the value-leaf surface proves it for elements
    //  too. Per-record fault isolation (Q3): a Mutagen-unparseable record is named + counted, never a
    //  crash (the product-read-path requirement carried from the write-surface completion sweep).
    //    dotnet run --project src/housecarl-generator read-proof [<plugin> ...]
    // ======================================================================
    public static int RunReadProof(string[] args)
    {
        var requested = args.Length > 0 ? args : DefaultSources();
        var existing = requested.Where(File.Exists).ToList();
        var corpus = CorpusRulebook.LoadCorpus();

        var sources = new List<(string label, ISkyrimModGetter overlay)>();
        var loadFailures = new List<string>();
        foreach (var s in existing)
        {
            try { sources.Add((Path.GetFileName(s)!, (ISkyrimModGetter)SkyrimMod.CreateFromBinaryOverlay(s, SkyrimRelease.SkyrimSE))); }
            catch (Exception ex) { loadFailures.Add($"{Path.GetFileName(s)}: {Flatten(ex)}"); }
        }
        if (sources.Count == 0) { Console.Error.WriteLine($"error: no source plugins loaded (looked in {DefaultDataDir} + {DefaultModsDir})"); return 1; }

        var ctx = new WalkContext(corpus);
        var pools = CollectInstancePools(sources, corpus, InstancesPerType, GlobalCapPerType, out _, out _);

        Console.WriteLine("================================================================");
        Console.WriteLine(" houseCARL read-proof (step 6 — read↔write round-trip oracle)");
        Console.WriteLine("================================================================");
        Console.WriteLine($"Sources pooled: {sources.Count} plugin(s); value-leaf round-trip = read token (inverse of Coerce) -> write back -> assert no-op.");
        if (loadFailures.Count > 0) Console.WriteLine($"   !! {loadFailures.Count} source(s) FAILED to load (surfaced not skipped): {string.Join(" | ", loadFailures.Take(5))}");
        Console.WriteLine();

        int attempted = 0, proven = 0;
        var provenLeafKeys = new HashSet<string>(StringComparer.Ordinal);
        var allLeafKeys = new HashSet<string>(StringComparer.Ordinal);
        var byCard = new SortedDictionary<string, int[]>(StringComparer.Ordinal);     // card -> [proven, attempted]
        var violations = new List<string>();        // read token did NOT round-trip (EmitToken/Coerce drift — a read defect)
        var readDefects = new List<string>();       // a getter-readable value the reader genuinely could not emit (a real defect)
        var overrideNotCarried = new List<string>();// getter-readable but Mutagen's override doesn't carry it (write-time-managed; same residual class write-proof names for Worldspace) — NOT a defect
        var noReadInstance = new List<string>();    // no populated instance to read (coverage residual, not a defect)
        var unreadable = new List<string>();        // Mutagen-unparseable record, fault-isolated + named (Q3)

        foreach (var (typeName, pool) in pools)
        {
            List<TodayLeaf> leaves;
            try { leaves = TodaySettableLeaves(typeName, ctx); }
            catch (Exception ex) { violations.Add($"{typeName}: leaf-enum threw {Flatten(ex)}"); continue; }
            var aliasFamily = DeriveRecordFlagAliasFamily(typeName, pool, ctx);

            foreach (var leaf in leaves)
            {
                if (leaf.Cardinality is not ("scalar" or "value" or "enum" or "formlink" or "substruct")) continue;
                allLeafKeys.Add(leaf.LeafKey);
                if (!byCard.TryGetValue(leaf.Cardinality, out var tally)) byCard[leaf.Cardinality] = tally = new int[2];

                // Pick a pooled instance where this leaf is actually POPULATED (a value to read). Reading
                // forces Mutagen's lazy parse; a record Mutagen can't parse is isolated + named, never fatal.
                IMajorRecordGetter? inst = null;
                string? untokenizable = null;   // a coercible value leaf that read as a container summary on some record
                foreach (var r in pool)
                {
                    ReadEngine.LeafRead probe;
                    try { probe = ReadEngine.ReadLeaf(r, leaf.FieldPath); }
                    catch (Exception ex) { unreadable.Add($"{typeName} {r.FormKey}: {leaf.Display}: {Flatten(ex)}"); continue; }
                    if (probe.HasValue) { inst = r; break; }
                    // A coercible VALUE leaf must read as a token or a genuine absence. A container summary ("[…]")
                    // means the reader did not recognise the value type — a read gap that must NOT hide as no-instance (Q3).
                    if (probe.Note is { } nt && nt.StartsWith("[", StringComparison.Ordinal)) untokenizable = nt;
                }
                if (inst is null)
                {
                    if (untokenizable is not null) readDefects.Add($"{leaf.Display} [{leaf.Cardinality}]: reader returned a non-value {untokenizable} for a coercible value leaf — value-type not handled");
                    else noReadInstance.Add($"{leaf.Display} [{leaf.Cardinality}]");
                    continue;
                }

                tally[1]++; attempted++;
                try
                {
                    var ov = MakeOverride(inst);
                    var before = Snapshot(ov, typeName, ctx);
                    var read = ReadEngine.ReadLeaf(ov, leaf.FieldPath);
                    if (!read.HasValue)
                    {
                        // The getter probe DID read a value (that's why inst was chosen), but the override read is empty.
                        // A genuine unreadable/no-field is a defect; an absent value = Mutagen's override doesn't carry it
                        // (a write-time-managed field like Worldspace OFST), which the PRODUCT still reads off the source —
                        // the same residual class write-proof names for Worldspace. Surface loud; not a read defect.
                        bool realFailure = read.Note is { } n && (n.StartsWith("(unreadable", StringComparison.Ordinal) || n.StartsWith("(no field", StringComparison.Ordinal));
                        if (realFailure) readDefects.Add($"{leaf.Display}: override read failed ({read.Note})");
                        else overrideNotCarried.Add($"{leaf.Display}: readable off source, Mutagen's override does not carry it (write-time-managed) — round-trip via override N/A");
                        continue;
                    }

                    var req = new WriteRequest { RecordType = leaf.RecordType, Path = leaf.FieldPath, Verb = "Set", Value = read.Token };
                    WriteEngine.ApplyVerb(ov, req);
                    var after = Snapshot(ov, typeName, ctx);

                    var allowExtra = aliasFamily.Contains(leaf.LeafFieldName) ? aliasFamily : EmptySet;
                    var changed = ChangedOutsideTarget(before, after, "", allowExtra);   // empty ⇒ a perfect no-op
                    if (changed.Count == 0) { tally[0]++; proven++; provenLeafKeys.Add(leaf.LeafKey); }
                    else violations.Add($"{leaf.Display}: read token '{read.Token}' did NOT round-trip — moved: {string.Join("; ", changed.Take(3))}");
                }
                catch (Exception ex) { violations.Add($"{leaf.Display}: round-trip threw {Flatten(ex)}"); }
            }
        }

        // ============ REPORT ============
        Console.WriteLine($"   value-leaf round-trips attempted: {attempted}  (proven no-op: {proven})");
        Console.WriteLine("   by cardinality (proven/attempted):");
        foreach (var (card, t) in byCard) Console.WriteLine($"        {card,-12} {t[0]}/{t[1]}");
        Console.WriteLine();
        Console.WriteLine($"   distinct (type.field) value leaves: {allLeafKeys.Count} considered, {provenLeafKeys.Count} proven by ≥1 site.");
        var unproven = allLeafKeys.Except(provenLeafKeys).OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (unproven.Count > 0) Console.WriteLine($"        distinct leaves not proven by any site ({unproven.Count}): {string.Join(", ", unproven.Take(40))}{(unproven.Count > 40 ? " …" : "")}");
        Console.WriteLine();
        DumpList("ROUND-TRIP VIOLATIONS (read token is NOT the inverse of Coerce — read defect)", violations);
        DumpList("READ DEFECTS (a getter-readable value the reader genuinely could not emit)", readDefects);
        Console.WriteLine();
        Console.WriteLine($"   COVERAGE RESIDUAL — value leaves with no populated instance to read in ANY loaded source: {noReadInstance.Count} (surfaced, never counted as proven).");
        if (overrideNotCarried.Count > 0)
        {
            Console.WriteLine($"   OVERRIDE-NOT-CARRIED residual — getter-readable value leaves Mutagen's override does not carry (write-time-managed; the PRODUCT reads them off the source, the round-trip-via-override can't no-op them): {overrideNotCarried.Count}");
            foreach (var o in overrideNotCarried.Take(10)) Console.WriteLine($"        {o}");
        }
        if (unreadable.Count > 0)
        {
            Console.WriteLine($"   MUTAGEN-UNPARSEABLE records (read delta, fault-isolated + named, never a crash): {unreadable.Count}");
            foreach (var u in unreadable.Take(10)) Console.WriteLine($"        {u}");
        }
        Console.WriteLine();

        bool pass = violations.Count == 0 && readDefects.Count == 0;
        Console.WriteLine(pass
            ? $"=== READ-PROOF: ENGINE PROVEN — {proven}/{attempted} value-leaf round-trips are byte-stable no-ops; {provenLeafKeys.Count}/{allLeafKeys.Count} distinct leaves proven; the read token is the faithful inverse of Coerce by construction; residuals surfaced: {noReadInstance.Count} no-instance + {overrideNotCarried.Count} override-not-carried + {unreadable.Count} unparseable. ==="
            : $"=== READ-PROOF: INCOMPLETE — {violations.Count} round-trip violation(s) + {readDefects.Count} read defect(s) (see loud failures above). ===");
        return pass ? 0 : 1;
    }

    static Phase3Result Phase3_AbsentCollection(List<(string typeName, List<IMajorRecordGetter> pool)> pools, WalkContext ctx, ISkyrimModGetter[] allMasters)
    {
        // ============ PHASE 3 — ABSENT-COLLECTION MATERIALIZATION BYTE-IDENTITY ============
        // Gate for the absent-collection fix: prove that rebuilding a collection via the engine (clear to null
        // -> Add the original elements) serializes BYTE-IDENTICALLY to the natively-loaded record. This catches
        // the one failure mode only-target-moved cannot see — a wrong presence / *DataTypeState marker from
        // materialization (the differ deliberately ignores *DataTypeState as derived state, so it would not flag it).
        Console.WriteLine("---- PHASE 3: absent-collection materialization byte-identity (clear -> engine-rebuild == native) ----");
        var byteSeen = new HashSet<string>(StringComparer.Ordinal);
        int byteChecked = 0, byteOk = 0, byteEquiv = 0, byteNoInst = 0;   // byteNoInst: finding 4 (parity) — surface Phase 3 no-instance residual
        var byteFail = new List<string>();
        foreach (var (typeName, pool) in pools)
        {
            List<TodayLeaf> leaves;
            try { leaves = TodaySettableLeaves(typeName, ctx); } catch { continue; }
            foreach (var leaf in leaves)
            {
                if (leaf.Cardinality is not ("list" or "dict") || !SchemaClassifier.CoercibleElement(leaf.Field)) continue;
                if (!byteSeen.Add(leaf.LeafKey)) continue;                        // one byte-check per distinct leaf-key
                var inst = pool.FirstOrDefault(r => PopulatedCollection(r, leaf));
                if (inst is null) { byteNoInst++; continue; }                                       // need a populated example to clear-and-rebuild
                try
                {
                    var (nativeMod, _)   = OverrideIn(inst);                      // the record as natively loaded
                    var (rebuiltMod, ov) = OverrideIn(inst);
                    var elems = ExtractAddStrings(SafeGet(ov, leaf));
                    if (!TryClearMember(ov, leaf)) continue;                      // get-only = ALWAYS present, never absent: nothing to materialize
                    byteChecked++;
                    foreach (var e in elems)                                      // engine-rebuild from scratch (materialize + Add)
                        WriteEngine.ApplyVerb(ov, new WriteRequest { RecordType = leaf.RecordType, Path = leaf.FieldPath, Verb = "Add", Value = e });
                    var na = TrySerialize(nativeMod, allMasters);
                    var rb = TrySerialize(rebuiltMod, allMasters);
                    if (na.bytes is not null && rb.bytes is not null)
                    {
                        if (na.bytes.Length == rb.bytes.Length && na.bytes.SequenceEqual(rb.bytes)) byteOk++;
                        else byteFail.Add($"{leaf.Display} [{leaf.Cardinality}]: rebuilt {rb.bytes.Length}B != native {na.bytes.Length}B");
                    }
                    else if (na.err == rb.err) byteEquiv++;                       // both serialize identically (incl. both un-serializable standalone) -> non-divergent
                    else byteFail.Add($"{leaf.Display} [{leaf.Cardinality}]: serialization DIVERGED — native[{na.err ?? "ok"}] vs rebuilt[{rb.err ?? "ok"}]");
                }
                catch (Exception ex) { byteFail.Add($"{leaf.Display} [{leaf.Cardinality}]: {Flatten(ex)}"); }
            }
        }
        Console.WriteLine($"   byte-identity: {byteOk} byte-identical + {byteEquiv} serialize-equivalent (cross-master records the standalone harness can't write — native==rebuilt behaviour) " +
                          $"= {byteOk + byteEquiv}/{byteChecked} non-divergent (materialize-then-add indistinguishable from native).");
        Console.WriteLine($"   COLLECTION residual — collection leaf-sites with no populated example to clear-and-rebuild (no-instance, surfaced not skipped): {byteNoInst}.");
        DumpList("BYTE-IDENTITY FAILURES (materialized collection diverges from native)", byteFail);
        var byteIdPass = byteFail.Count == 0;
        Console.WriteLine();
        return new Phase3Result(byteIdPass, byteOk, byteEquiv, byteChecked);
    }

    static Phase4Result Phase4_AbsentSubstruct(List<(string typeName, List<IMajorRecordGetter> pool)> pools, WalkContext ctx, ISkyrimModGetter[] allMasters)
    {
        // ============ PHASE 4 — ABSENT-SUBSTRUCT MATERIALIZATION BYTE-IDENTITY ============
        // The other half of approach A: prove that materializing an ABSENT intermediate substruct (clear it to null
        // -> rebuild its fields via the engine, where the first write triggers MaterializeSubstruct) serializes
        // BYTE-IDENTICALLY to native. The COMPOSITION surface (GenderedItem<T>/Array2d<T> — no parameterless ctor) is
        // derived BY CONSTRUCTION: the engine throws CompositionRequiredException, caught + tallied as a wave-1
        // deferral here and printed every run, NEVER silently skipped (Q3). Non-replayable content (arms,
        // struct-element collections, owned records) is a named residual whose byte-identity rides later waves.
        Console.WriteLine("---- PHASE 4: absent-substruct materialization byte-identity (clear substruct -> engine-rebuild == native) ----");
        var ssSeen = new HashSet<string>(StringComparer.Ordinal);
        int ssChecked = 0, ssOk = 0, ssEquiv = 0, ssGetOnly = 0, ssNoInst = 0, ssMarkerResid = 0;
        var ssByteFail = new List<string>();
        var ssMarkerList = new List<string>();   // value-identical; byte diff is a non-settable DataTypeState/break marker only
        var ssComposition = new SortedDictionary<string, int>(StringComparer.Ordinal);   // composition substruct type -> site count (DERIVED — the deferred list)
        var ssNotRebuildable = new SortedDictionary<string, int>(StringComparer.Ordinal); // residual reason -> count
        foreach (var (typeName, pool) in pools)
        {
            List<SubstructNode> nodes;
            try { nodes = NavigateIntoSubstructs(typeName, ctx); } catch { continue; }
            foreach (var sn in nodes)
            {
                if (!ssSeen.Add(sn.LeafKey)) continue;                            // one check per distinct substruct site
                var inst = pool.FirstOrDefault(r => PresentPopulatedSubstruct(r, sn.FieldPath));
                if (inst is null) { ssNoInst++; continue; }                       // no present instance here — surfaced in the residual count
                try
                {
                    var (nativeMod, natRec) = OverrideIn(inst);
                    var (rebuiltMod, ov) = OverrideIn(inst);
                    var replay = ExtractSubstructReplay(ov, sn, ctx, out var rebuildable, out var reason);
                    if (!rebuildable) { var k = reason ?? "?"; ssNotRebuildable.TryGetValue(k, out var rc); ssNotRebuildable[k] = rc + 1; continue; }
                    if (!TryClearSubstructPath(ov, sn.FieldPath)) { ssGetOnly++; continue; } // get-only = always present, never absent
                    foreach (var req in replay) WriteEngine.ApplyVerb(ov, req);   // rebuild (first write into the cleared substruct materializes it)
                    ssChecked++;
                    var na = TrySerialize(nativeMod, allMasters);
                    var rb = TrySerialize(rebuiltMod, allMasters);
                    if (na.bytes is not null && rb.bytes is not null)
                    {
                        if (na.bytes.Length == rb.bytes.Length && na.bytes.SequenceEqual(rb.bytes)) ssOk++;
                        else
                        {
                            // Phase-1 completeness backs this: an empty value-diff means EVERY modeled value is
                            // identical, so the only possible byte difference is Mutagen's internal DataTypeState/break
                            // marker (derived parse-state, not a content field, not settable via the value write API).
                            // Named residual, not a fail; a real VALUE divergence still fails loud.
                            var diff = SnapshotValueDiff(natRec, ov, sn.RecordType, ctx);
                            if (diff.Length == 0)
                            {
                                ssMarkerResid++;
                                ssMarkerList.Add($"{sn.Display} [{sn.SubstructType}]: {Math.Abs((long)na.bytes.Length - rb.bytes.Length)}B DataTypeState/break marker (all modeled values identical)");
                            }
                            else ssByteFail.Add($"{sn.Display} [{sn.SubstructType}]: rebuilt {rb.bytes.Length}B != native {na.bytes.Length}B; VALUE DIFF: {diff}");
                        }
                    }
                    else if (na.err == rb.err) ssEquiv++;
                    else ssByteFail.Add($"{sn.Display} [{sn.SubstructType}]: serialization DIVERGED — native[{na.err ?? "ok"}] vs rebuilt[{rb.err ?? "ok"}]");
                }
                catch (CompositionRequiredException cre)
                {
                    ssComposition.TryGetValue(cre.SubstructType.Name, out var cc); ssComposition[cre.SubstructType.Name] = cc + 1;
                }
                catch (Exception ex) { ssByteFail.Add($"{sn.Display} [{sn.SubstructType}]: {Flatten(ex)}"); }
            }
        }
        Console.WriteLine($"   byte-identity: {ssOk} byte-identical + {ssEquiv} serialize-equivalent = {ssOk + ssEquiv}/{ssChecked} non-divergent " +
                          $"(materialize-then-rebuild indistinguishable from native); {ssGetOnly} get-only/always-present skipped; {ssNoInst} no-instance.");
        if (ssMarkerList.Count > 0)
        {
            Console.WriteLine($"   RESIDUAL — value-identical, byte-identity blocked by Mutagen's internal DataTypeState/break marker " +
                              $"(not a content field, not settable via the value write API; the engine wrote every modeled value correctly). {ssMarkerList.Count}:");
            foreach (var p in ssMarkerList.Take(20)) Console.WriteLine($"        {p}");
        }
        // The DEFERRED COMPOSITION LIST — derived by construction (the engine's no-parameterless-ctor throw), printed
        // EVERY run so it can't be forgotten. WIRE-WHEN: substruct/element COMPOSITION lands (wave 1) — build a
        // GenderedItem<T>/Array2d<T> from its parts, after which these materialize like any other absent substruct.
        var ssCompSites = ssComposition.Values.Sum();
        Console.WriteLine($"   DEFERRED — absent COMPOSITION substructs (no parameterless ctor; build-from-parts). WIRE-WHEN: wave-1 composition. " +
                          $"{ssComposition.Count} type(s), {ssCompSites} site(s):");
        foreach (var (k, c) in ssComposition.OrderByDescending(kv => kv.Value)) Console.WriteLine($"        {c,4}x  {k}");
        if (ssComposition.Count == 0) Console.WriteLine("        (none encountered in the loaded sources)");
        if (ssNotRebuildable.Count > 0)
        {
            Console.WriteLine($"   RESIDUAL — substructs whose content is not byte-replayable here (byte-identity rides the composition/arm waves), by reason:");
            foreach (var (k, c) in ssNotRebuildable.OrderByDescending(kv => kv.Value).Take(30)) Console.WriteLine($"        {c,4}x  {k}");
        }
        DumpList("ABSENT-SUBSTRUCT BYTE-IDENTITY FAILURES (materialized substruct diverges from native)", ssByteFail);
        var ssBytePass = ssByteFail.Count == 0;
        Console.WriteLine();
        return new Phase4Result(ssBytePass, ssOk, ssEquiv, ssChecked, ssCompSites);
    }

    static Phase5Result Phase5_CollectionNav(List<(string typeName, List<IMajorRecordGetter> pool)> pools, WalkContext ctx, CorpusRulebook rulebook)
    {
        // ============ PHASE 5 — COLLECTION-NAV (wave 1): value-landed + only-target-moved through a struct element ============
        // Step INTO a list/dict struct element and edit a sub-field (Effects[0].Data.Magnitude). Reuses the wave-0
        // differ: snapshot → engine edit through the element → snapshot → assert the value LANDED at the bracketed
        // target AND nothing outside that subtree moved. Polymorphic (arm) sub-leaves behind collnav are excluded
        // (wave 4); a collnav leaf with no populated element in any source is a surfaced residual, never a skip (Q3).
        Console.WriteLine("---- PHASE 5: collection-nav (wave 1) — edit a sub-field inside a list/dict element; value-landed + only-target-moved ----");
        var cnByCard = new SortedDictionary<string, int[]>(StringComparer.Ordinal);   // sub-leaf card -> [proven, attempted]
        var cnProvenKeys = new HashSet<string>(StringComparer.Ordinal);
        var cnAllKeys = new HashSet<string>(StringComparer.Ordinal);
        var cnViolations = new List<string>();
        var cnThrows = new List<string>();
        var cnNoInstance = new List<string>();
        var cnDeferred = new List<string>();   // navigable on the getter but not byte-provable HERE — surfaced + labeled, never a silent skip
        int cnAttempt = 0, cnProven = 0;
        foreach (var (typeName, pool) in pools)
        {
            List<TodayLeaf> leaves;
            try { leaves = CollnavSettableLeaves(typeName, ctx); }
            catch (Exception ex) { cnViolations.Add($"{typeName}: collnav leaf-enum threw: {Flatten(ex)}"); continue; }
            foreach (var leaf in leaves)
            {
                cnAllKeys.Add(leaf.LeafKey);
                if (!cnByCard.TryGetValue(leaf.Cardinality, out var tally)) cnByCard[leaf.Cardinality] = tally = new int[2];
                var inst = pool.FirstOrDefault(r => CollnavNavigable(r, leaf));
                if (inst is null) { cnNoInstance.Add($"{leaf.Display} [{leaf.Cardinality}]"); continue; }

                // The override is what the engine edits, and GetOrAddAsOverride does NOT deep-copy nested-group
                // children (Worldspace SubCells/LargeReferences = the cell hierarchy), so a getter-navigable path can
                // be absent on the override. Reached via the Phase 7 by-FormKey nested path — surfaced here, never a throw.
                var ov = MakeOverride(inst);
                if (!CollnavNavigable(ov, leaf))
                { cnDeferred.Add($"{leaf.Display} [{leaf.Cardinality}] (override doesn't carry the element — reached via the Phase 7 by-FormKey nested path)"); continue; }

                var before = Snapshot(MakeOverride(inst), typeName, ctx);
                WriteRequest req; string landKey; object? expectToken; bool distinct;
                try { (req, landKey, expectToken, distinct) = BuildEdit(leaf, ov, ctx); }
                catch (Exception ex) { cnThrows.Add($"{leaf.Display} [{leaf.Cardinality}]: edit-synth threw {Flatten(ex)}"); continue; }

                // The differ defines the provable value-leaf surface. If the target path is not a single tracked leaf
                // in the snapshot, it is a WHOLE-VALUE element (e.g. an AssetLink path the differ records as one
                // "asset:" leaf) or a COMPOSITION-type internal (GenderedItem<T> half — half B), which the differ
                // treats as one leaf, not a collnav sub-field. The edit may still land, just not byte-verifiably HERE.
                // Deferred + labeled, never a false violation.
                if (!before.ContainsKey(landKey))
                { cnDeferred.Add($"{leaf.Display} [{leaf.Cardinality}] (not a differ-tracked value-leaf — whole-value/composition element)"); continue; }

                tally[1]++; cnAttempt++;
                var ruleReject = rulebook.Validate(req);   // SOFT cross-check — the pre-flight must accept what the engine can do
                try
                {
                    WriteEngine.ApplyVerb(ov, req);
                    var after = Snapshot(ov, typeName, ctx);
                    var outside = ChangedOutsideTarget(before, after, leaf.PathPrefix, EmptySet);
                    var landed = Landed(after, landKey, expectToken, leaf);
                    if (outside.Count == 0 && landed)
                    {
                        tally[0]++; cnProven++; cnProvenKeys.Add(leaf.LeafKey);
                        if (ruleReject is not null) cnViolations.Add($"{leaf.Display} [{leaf.Cardinality}]: engine PROVED it; rulebook REJECTED (pre-flight too strict): {ruleReject}");
                    }
                    else if (!landed)
                        cnViolations.Add($"{leaf.Display} [{leaf.Cardinality}]: value did NOT land (expected {landKey}={expectToken}, got {Tok(after.GetValueOrDefault(landKey))})");
                    else
                        cnViolations.Add($"{leaf.Display} [{leaf.Cardinality}]: {outside.Count} field(s) moved OUTSIDE target (e.g. {outside[0]})");
                }
                catch (Exception ex)
                {
                    cnThrows.Add($"{leaf.Display} [{leaf.Cardinality}]: threw {Flatten(ex)}");
                }
            }
        }
        Console.WriteLine($"   collnav leaf-sites attempted: {cnAttempt}  (proven {cnProven}); distinct (type.field): {cnAllKeys.Count} considered, {cnProvenKeys.Count} proven by ≥1 site.");
        Console.WriteLine("   by sub-leaf cardinality (proven/attempted):");
        foreach (var (card, t) in cnByCard) Console.WriteLine($"        {card,-12} {t[0]}/{t[1]}");
        DumpList("COLLNAV violations (value didn't land / moved outside the target subtree)", cnViolations);
        DumpList("COLLNAV throws (engine could not perform a collnav edit)", cnThrows);
        DumpList("COLLNAV deferred (navigable, but byte-identity rides a later wave — nested-group / whole-value / composition)", cnDeferred);
        Console.WriteLine($"   COLLNAV residual — collnav leaf-sites with no populated element in ANY loaded source (surfaced, not skipped): {cnNoInstance.Count}");
        var collnavPass = cnViolations.Count == 0 && cnThrows.Count == 0;
        Console.WriteLine();
        return new Phase5Result(collnavPass, cnProven, cnAttempt);
    }

    static Phase6Result Phase6_StructElement(List<(string typeName, List<IMajorRecordGetter> pool)> pools, WalkContext ctx, ISkyrimModGetter[] allMasters)
    {
        // ============ PHASE 6 — STRUCT-ELEMENT COMPOSITION BYTE-IDENTITY (wave 1 half B) ============
        // Add a NEW modeled struct element to a collection, built FROM PARTS. Prove that rebuilding a struct-element
        // list via the engine (clear -> Add each element, each built from a spec EXTRACTED from the native element by
        // the same WalkReplay recursion) serializes BYTE-IDENTICALLY to native. The element-build reuses the verb
        // engine (BuildStruct), so a faithful rebuild IS the byte-identity test. An element CONTAINING a polymorphic
        // arm (conditions, script properties, …) is a NAMED residual — its byte-identity rides the wave-4 arm proof,
        // never silently skipped (Q3); the Add CAPABILITY still works, only the BYTE-proof of arm-bearing elements defers.
        Console.WriteLine("---- PHASE 6: struct-element composition byte-identity (clear -> engine-Add-from-parts == native) ----");
        var seSeen = new HashSet<string>(StringComparer.Ordinal);
        int seChecked = 0, seOk = 0, seEquiv = 0, seNoInst = 0;
        var seFail = new List<string>();
        var seResid = new SortedDictionary<string, int>(StringComparer.Ordinal);  // un-replayable / deferred reason -> count
        var seMarkerList = new List<string>();                                    // value-identical; byte diff is a non-settable DataTypeState/break marker only
        void Defer(string reason) { seResid.TryGetValue(reason, out var c); seResid[reason] = c + 1; }
        foreach (var (typeName, pool) in pools)
        {
            List<TodayLeaf> leaves;
            try { leaves = StructElementLeaves(typeName, ctx); } catch { continue; }
            foreach (var leaf in leaves)
            {
                if (!seSeen.Add(leaf.LeafKey)) continue;                          // one byte-check per distinct leaf-key

                // Upfront structural guards — each is a struct-element collection the wave-1 Add-from-parts proof
                // does NOT close here; surfaced as a NAMED deferral (Q3), never a silent skip or a false failure.
                if (leaf.Cardinality != "list")                                  // a non-arm struct-valued dict — Phase 6 proves lists
                { Defer($"struct-valued {leaf.Cardinality} (Phase 6 proves lists)"); continue; }
                var collAq = leaf.Field.MutableTypeAssemblyQualified ?? leaf.Field.GetterTypeAssemblyQualified;
                if (collAq is { } caq && WriteEngine.ResolveType(caq) is { IsArray: true })     // fixed array (CloudLayer[]) — SetAtIndex surface, not Add
                { Defer("fixed-array struct collection (SetAtIndex surface, not Add)"); continue; }
                if (ReachesOwnedRecord(leaf.Field.ElementTypeRef!, ctx, new HashSet<string>(StringComparer.Ordinal)))  // owns child records — reached via the Phase 7 nested record axis
                { Defer($"nested-group struct-element list (owns child records — reached via the Phase 7 by-FormKey nested path):{leaf.Field.ElementTypeRef}"); continue; }

                var inst = pool.FirstOrDefault(r => PopulatedCollection(r, leaf));
                if (inst is null) { seNoInst++; continue; }                       // no populated example to clear-and-rebuild
                try
                {
                    var (nativeMod, natRec) = OverrideIn(inst);
                    var (rebuiltMod, ov)    = OverrideIn(inst);
                    // extract each native element into a build-from-parts spec (the inverse of BuildStruct)
                    bool rb = true; string? rs = null;
                    var specs = new List<StructSpec>();
                    if (SafeGet(ov, leaf) is IEnumerable coll)
                        foreach (var e in coll)
                        {
                            if (e is null) continue;
                            specs.Add(ExtractStructSpec(e, leaf.Field.ElementTypeRef!, ctx, ref rb, ref rs, 0));
                            if (!rb) break;
                        }
                    if (!rb) { Defer(rs ?? "?"); continue; }                       // NAMED residual (arm/record-bearing element — wave 4)
                    if (!TryClearMember(ov, leaf)) { Defer("get-only collection (always-present; Add works, clear-rebuild N/A)"); continue; }
                    try
                    {
                        if (specs.Count == 0)                                     // PRESENT-but-EMPTY list → materialize present-empty (Add can't: no element)
                            WriteEngine.ApplyVerb(ov, new WriteRequest { RecordType = leaf.RecordType, Path = leaf.FieldPath, Verb = "ReplaceAll", Values = Array.Empty<string>() });
                        else
                            foreach (var spec in specs)                           // engine-rebuild: Add each element FROM PARTS
                                WriteEngine.ApplyVerb(ov, new WriteRequest { RecordType = leaf.RecordType, Path = leaf.FieldPath, Verb = "Add", Struct = spec });
                    }
                    catch (InvalidOperationException ioe) when (ioe.Message.Contains("not settable — cannot materialize", StringComparison.Ordinal))
                    {
                        // The engine's get-only guard fired: ResolveProperty resolves a get-only collection at runtime
                        // (a Mutagen getter/concrete-settability quirk; the corpus marks it writable via the mutable
                        // interface). An UNREPLACEABLE collection cannot be clear-rebuilt — the proof METHOD is N/A here,
                        // NOT an engine defect (the Add capability is proven by the settable leaves + the oracle). Named
                        // deferral, never a silent skip or a false failure (Q3).
                        Defer("runtime get-only collection (clear-rebuild byte-proof N/A; Add capability proven elsewhere)");
                        continue;
                    }
                    seChecked++;
                    var na = TrySerialize(nativeMod, allMasters);
                    var rbz = TrySerialize(rebuiltMod, allMasters);
                    if (na.bytes is not null && rbz.bytes is not null)
                    {
                        if (na.bytes.Length == rbz.bytes.Length && na.bytes.SequenceEqual(rbz.bytes)) seOk++;
                        else
                        {
                            // Value-identical but byte-different => a Mutagen internal DataTypeState/break marker only
                            // (e.g. the ScenePhase Unused2 marker), exactly like Phase 4's Scene.Unused2: Phase-1 differ
                            // completeness guarantees an empty value-diff means no modeled content field differs. Named residual.
                            var diff = SnapshotValueDiff(natRec, ov, leaf.RecordType, ctx);
                            if (diff.Length == 0)
                                seMarkerList.Add($"{leaf.Display}: {Math.Abs((long)na.bytes.Length - rbz.bytes.Length)}B DataTypeState/break marker (all modeled values identical)");
                            else seFail.Add($"{leaf.Display}: rebuilt {rbz.bytes.Length}B != native {na.bytes.Length}B; VALUE DIFF: {diff}");
                        }
                    }
                    else if (na.err == rbz.err) seEquiv++;
                    else seFail.Add($"{leaf.Display}: serialization DIVERGED — native[{na.err ?? "ok"}] vs rebuilt[{rbz.err ?? "ok"}]");
                }
                catch (Exception ex) { seFail.Add($"{leaf.Display}: {Flatten(ex)}"); }
            }
        }
        Console.WriteLine($"   byte-identity: {seOk} byte-identical + {seEquiv} serialize-equivalent = {seOk + seEquiv}/{seChecked} non-divergent (Add-from-parts indistinguishable from native); {seNoInst} no-instance.");
        if (seMarkerList.Count > 0)
        {
            Console.WriteLine($"   RESIDUAL — value-identical, byte-identity blocked by Mutagen's internal DataTypeState/break marker (the engine Added every modeled value correctly). {seMarkerList.Count}:");
            foreach (var p in seMarkerList.Take(20)) Console.WriteLine($"        {p}");
        }
        if (seResid.Count > 0)
        {
            Console.WriteLine("   DEFERRED — struct-element collections the Add-from-parts proof does not close here (the Add CAPABILITY works; byte-identity rides the named wave), by reason:");
            foreach (var (k, c) in seResid.OrderByDescending(kv => kv.Value)) Console.WriteLine($"        {c,4}x  {k}");
        }
        DumpList("STRUCT-ELEMENT COMPOSITION BYTE-IDENTITY FAILURES (Add-from-parts diverges from native)", seFail);
        var sePass = seFail.Count == 0;
        Console.WriteLine();
        return new Phase6Result(sePass, seOk, seEquiv, seChecked);
    }

    // ============ PHASE 7 — NESTED-GROUP BYTE-TRUE (wave 3) ============
    // Resolve a record in a NESTED group by FormKey (the wave-3 engine path: GenericGetOrAddAsOverride with the
    // source's link cache reconstructs its parent chain), edit a real settable leaf, and assert — through the SAME
    // wave-0 differ — that the value LANDED and nothing else in the TARGET RECORD moved. The snapshot is of the
    // target record itself, so the reconstructed parent carriers (a Worldspace for exterior Landscape, a DialogTopic
    // for INFO, the Cell for a placed ref) are SEPARATE records in the patch, not fields of this record's tree —
    // subtree-scoping is automatic. A byte-write per type then proves serialization: single-master + source SHA
    // untouched. The 6 placed subtypes with 0 vanilla instances (Arrow/Barrier/Beam/Cone/Flame/Missile) share the
    // exact APlaced + cell-child resolution path as the proven placed types; they are LOUD-LISTED as a named
    // residual (Q3), byte-true deferred to a source that contains them — never silently treated as covered.
    static Phase7Result Phase7_NestedGroup(
        List<(string label, ISkyrimModGetter overlay)> sources, WalkContext ctx, CorpusRulebook rulebook, ISkyrimModGetter[] allMasters)
    {
        Console.WriteLine("---- PHASE 7: nested-group byte-true (resolve by FormKey via the wave-3 path; value-landed + only-target-moved subtree-scoped) ----");

        // The nested set BY CONSTRUCTION + one sample instance per type. Shared with Phase 8 (fail-closed) so the
        // nested-vs-flat predicate lives in ONE place — no forked predicate (baseline review S2 lesson).
        var (nestedGetters, sample, instanceBearing, absent) = SampleNestedTypes(sources, ctx);

        Console.WriteLine($"   nested types (by construction): {nestedGetters.Count}; instance-bearing in sources: {instanceBearing.Count}; absent (loud-listed): {absent.Count}");

        var violations = new List<string>();
        var throwsList = new List<string>();
        var byteFail = new List<string>();
        var typesProven = new List<string>();
        var byCard = new SortedDictionary<string, int[]>(StringComparer.Ordinal);  // card -> [proven, attempted]
        int leavesProven = 0, leavesAttempted = 0;
        int p7NoInst = 0, p7WholeValue = 0;   // finding 4 (parity): surface Phase 7 no-instance + whole-value residuals (was a silent continue)

        foreach (var cat in instanceBearing)
        {
            var (rec0, src) = sample[cat];
            var cache = src.ToImmutableLinkCache();
            // One leaf-set per nested type (substruct-only today surface — same as Phase 2). The alias family is
            // derived through a cache-aware override so record-flag co-movement is expected, not a false leak.
            List<TodayLeaf> leaves;
            try { leaves = TodaySettableLeaves(cat, ctx); }
            catch (Exception ex) { violations.Add($"{cat}: leaf-enum threw: {Flatten(ex)}"); continue; }
            var aliasFamily = DeriveRecordFlagAliasFamily(cat, new List<IMajorRecordGetter> { rec0 }, ctx, cache);

            int provenHere = 0;
            Dictionary<string, object?>? before0 = null;
            foreach (var leaf in leaves)
            {
                if (!Navigable(rec0, leaf)) { p7NoInst++; continue; }     // not populated on this instance — no-instance residual, not a skip
                if (!byCard.TryGetValue(leaf.Cardinality, out var tally)) byCard[leaf.Cardinality] = tally = new int[2];

                IMajorRecord ov;
                try { ov = MakeOverride(rec0, cache); }
                catch (Exception ex) { throwsList.Add($"{leaf.Display} [{leaf.Cardinality}]: nested override threw {Flatten(ex)}"); continue; }
                var before = Snapshot(ov, cat, ctx);
                if (before0 is null) before0 = before;

                WriteRequest req; string landKey; object? expectToken; bool distinct;
                try { (req, landKey, expectToken, distinct) = BuildEdit(leaf, ov, ctx); }
                catch (Exception ex) { throwsList.Add($"{leaf.Display} [{leaf.Cardinality}]: edit-synth threw {Flatten(ex)}"); continue; }
                if (!before.ContainsKey(landKey)) { p7WholeValue++; continue; }   // whole-value/composition element — not a differ-tracked leaf here

                tally[1]++; leavesAttempted++;
                var ruleReject = rulebook.Validate(req);      // SOFT cross-check
                try
                {
                    WriteEngine.ApplyVerb(ov, req);
                    var after = Snapshot(ov, cat, ctx);
                    var allowExtra = aliasFamily.Contains(leaf.LeafFieldName) ? aliasFamily : EmptySet;
                    var outside = ChangedOutsideTarget(before, after, leaf.PathPrefix, allowExtra);
                    var landed = Landed(after, landKey, expectToken, leaf);
                    if (outside.Count == 0 && landed)
                    {
                        tally[0]++; leavesProven++; provenHere++;
                        if (ruleReject is not null) violations.Add($"{leaf.Display} [{leaf.Cardinality}]: engine PROVED it; rulebook REJECTED: {ruleReject}");
                    }
                    else if (!landed)
                        violations.Add($"{leaf.Display} [{leaf.Cardinality}]: value did NOT land (expected {landKey}={expectToken}, got {Tok(after.GetValueOrDefault(landKey))})");
                    else
                        violations.Add($"{leaf.Display} [{leaf.Cardinality}]: {outside.Count} field(s) moved OUTSIDE target (e.g. {outside[0]})");
                }
                catch (Exception ex) { throwsList.Add($"{leaf.Display} [{leaf.Cardinality}]: threw {Flatten(ex)}"); }
            }

            // Serialization proof for this type: one real edit -> WritePatch -> reopen -> single-master + present.
            // Source-untouched is asserted globally at the end (the overlay is read-only; we never write to src).
            string byteNote = "no settable leaf to byte-write";
            try
            {
                var firstLeaf = leaves.FirstOrDefault(l => Navigable(rec0, l));
                if (firstLeaf is not null)
                {
                    var patch = new SkyrimMod(PatchKey, SkyrimRelease.SkyrimSE);
                    var ov = WriteEngine.GenericGetOrAddAsOverride(patch, rec0, cache);
                    var (req, _, _, _) = BuildEdit(firstLeaf, ov, ctx);
                    WriteEngine.ApplyVerb(ov, req);
                    var ser = TrySerialize(patch, allMasters);
                    if (ser.bytes is null) byteNote = $"serialize: {ser.err}";
                    else
                    {
                        var back = SkyrimMod.CreateFromBinaryOverlay(WriteToTemp(patch, allMasters), SkyrimRelease.SkyrimSE);
                        var masters = back.ModHeader.MasterReferences.Count;
                        var present = back.EnumerateMajorRecords().Any(r => r.FormKey == rec0.FormKey);
                        if (!present) byteFail.Add($"{cat} {rec0.FormKey}: edited record absent from written patch.");
                        else if (masters != 1) byteFail.Add($"{cat} {rec0.FormKey}: expected single-master patch, got {masters} masters.");
                        byteNote = $"byte-write OK (masters={masters}, present={present})";
                    }
                }
            }
            catch (Exception ex) { byteFail.Add($"{cat} {rec0.FormKey}: byte-write threw {Flatten(ex)}"); byteNote = "threw"; }

            if (provenHere > 0) typesProven.Add(cat);
            Console.WriteLine($"   {cat,-16} {rec0.FormKey}  leaves {provenHere} proven; {byteNote}");
        }

        Console.WriteLine();
        Console.WriteLine("   by cardinality (proven/attempted):");
        foreach (var (card, t) in byCard) Console.WriteLine($"        {card,-12} {t[0]}/{t[1]}");
        // LOUD-LIST the absent placed types — named residual, byte-true deferred to a source that contains them (Q3).
        Console.WriteLine($"   ABSENT nested types (0 instances in any loaded source; resolution path is identical — surfaced, never silently covered): {absent.Count}");
        if (absent.Count > 0) Console.WriteLine($"        {string.Join(", ", absent)}");
        // finding 4 (parity): Phase 7 per-sample residuals surfaced, not silently continued (like Phase 2/5 no-instance lines).
        Console.WriteLine($"   NESTED residual — leaf-sites not populated on the sampled instance (no-instance, surfaced not skipped): {p7NoInst}; whole-value/composition leaves (not a differ-tracked value-leaf here): {p7WholeValue}.");
        DumpList("NESTED-GROUP violations (value didn't land / moved outside the target record)", violations);
        DumpList("NESTED-GROUP throws (engine could not perform a nested edit)", throwsList);
        DumpList("NESTED-GROUP byte-write failures (serialization / master / presence)", byteFail);
        // Every instance-bearing nested type must prove >=1 leaf — a type that resolved but proved nothing is a loud gap.
        var unproven = instanceBearing.Where(c => !typesProven.Contains(c)).ToList();
        DumpList("NESTED-GROUP instance-bearing types that proved NO leaf (gap)", unproven);
        var pass = violations.Count == 0 && throwsList.Count == 0 && byteFail.Count == 0 && unproven.Count == 0;
        Console.WriteLine($"   PHASE 7: {(pass ? "PASS" : "INCOMPLETE — see loud failures above")} — {leavesProven}/{leavesAttempted} nested leaf-sites proven across {typesProven.Count}/{instanceBearing.Count} instance-bearing nested types.");
        Console.WriteLine();
        return new Phase7Result(pass, typesProven.Count, instanceBearing.Count, leavesProven, leavesAttempted, absent.Count);
    }

    // The nested set BY CONSTRUCTION (concrete records whose getter iface is NOT yielded by EnumerateFlatGroups —
    // the same derivation the census + scout use; no hand-list) + one sample instance per type from the sources.
    // Shared by Phase 7 (byte-true) and Phase 8 (fail-closed) so the nested-vs-flat predicate lives in ONE place.
    static (Dictionary<string, Type> nestedGetters,
            Dictionary<string, (IMajorRecordGetter rec, ISkyrimModGetter src)> sample,
            List<string> instanceBearing,
            List<string> absent)
        SampleNestedTypes(List<(string label, ISkyrimModGetter overlay)> sources, WalkContext ctx)
    {
        var asm = typeof(IArmorGetter).Assembly;
        var flatGetterIfaces = WriteEngine.EnumerateFlatGroups(typeof(SkyrimMod)).Select(x => x.getterIface).ToHashSet();
        var nestedGetters = new Dictionary<string, Type>(StringComparer.Ordinal);  // catalog name -> I{Name}Getter
        foreach (var t in ctx.Corpus.Types.Values.Where(t => t.Kind == "record" && t.Name != "SkyrimMod"))
        {
            var g = asm.GetType("Mutagen.Bethesda.Skyrim.I" + t.Name + "Getter");
            if (g is not null && !flatGetterIfaces.Any(f => f.IsAssignableFrom(g))) nestedGetters[t.Name] = g;
        }

        // Sample one instance per nested type across the sources (nested records are NOT in `pools`; enumerate the
        // overlays directly, like the scout). Capture the owning overlay so its link cache reconstructs the chain.
        // Vanilla masters are first in `sources`, so the 8 instance-bearing types fill quickly; stop once all found.
        var sample = new Dictionary<string, (IMajorRecordGetter rec, ISkyrimModGetter src)>(StringComparer.Ordinal);
        foreach (var (label, overlay) in sources)
        {
            if (sample.Count == nestedGetters.Count) break;
            foreach (var rec in overlay.EnumerateMajorRecords())
            {
                var cat = NestedCatalogOf(rec, nestedGetters);
                if (cat is null || sample.ContainsKey(cat)) continue;
                sample[cat] = (rec, overlay);
                if (sample.Count == nestedGetters.Count) break;
            }
        }

        var instanceBearing = nestedGetters.Keys.Where(sample.ContainsKey).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var absent = nestedGetters.Keys.Where(n => !sample.ContainsKey(n)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        return (nestedGetters, sample, instanceBearing, absent);
    }

    // ============ PHASE 8 — NESTED-GROUP FAIL-CLOSED (wave 3 guard) ============
    // The wave-3 resolver has two guard clauses that must fail LOUD, never silently resolve to the wrong record
    // (Q3): (A) a nested record routed WITHOUT the source link cache (WriteEngine NestedGetOrAddAsOverride guard 1),
    // and (B) a nested record ABSENT from the provided cache (guard 2). Phase 7 proves the HAPPY path byte-true;
    // Phase 8 proves the SAD path throws. It drives each real instance-bearing nested record through both negatives
    // and asserts an exception is raised — a return WITHOUT throwing is the silent-failure we are guarding against.
    // The actual exception TYPE that fires is surfaced per case (empirical — we do not assert which guard catches
    // it, since guard 2's failure may surface as the authored InvalidOperationException OR a reflected
    // TargetInvocationException from Mutagen's own resolve). The absent placed types share this EXACT resolution
    // path (no per-type branch in GenericGetOrAddAsOverride), so their fail-closed behavior is covered transitively;
    // they are named, never silently assumed covered.
    static Phase8Result Phase8_NestedFailClosed(List<(string label, ISkyrimModGetter overlay)> sources, WalkContext ctx)
    {
        Console.WriteLine("---- PHASE 8: nested-group fail-closed (a missing/uncacheable nested record must throw loud, never silently resolve) ----");
        var (_, sample, instanceBearing, absent) = SampleNestedTypes(sources, ctx);
        var emptyCache = new SkyrimMod(EmptyProbeKey, SkyrimRelease.SkyrimSE).ToImmutableLinkCache();

        var failures = new List<string>();
        int casesRun = 0, casesLoud = 0;
        foreach (var cat in instanceBearing)
        {
            var (rec0, _) = sample[cat];

            // Case A — a nested record routed WITHOUT the source link cache must fail loud.
            casesRun++;
            string aNote;
            try
            {
                WriteEngine.GenericGetOrAddAsOverride(new SkyrimMod(PatchKey, SkyrimRelease.SkyrimSE), rec0);
                aNote = "NO THROW (silent!)";
                failures.Add($"{cat} {rec0.FormKey}: routed with NO source cache and did NOT throw — a nested record silently resolved without its parent chain.");
            }
            catch (Exception ex) { casesLoud++; aNote = ex.GetType().Name; }

            // Case B — a nested record ABSENT from the provided cache must fail loud.
            casesRun++;
            string bNote;
            try
            {
                WriteEngine.GenericGetOrAddAsOverride(new SkyrimMod(PatchKey, SkyrimRelease.SkyrimSE), rec0, emptyCache);
                bNote = "NO THROW (silent!)";
                failures.Add($"{cat} {rec0.FormKey}: resolved against an EMPTY cache and did NOT throw — a missing nested record silently resolved.");
            }
            catch (Exception ex) { casesLoud++; bNote = ex.GetType().Name; }

            Console.WriteLine($"   {cat,-16} {rec0.FormKey}  no-cache: {aNote}; empty-cache: {bNote}");
        }

        // The absent placed types share this EXACT path (no per-type branch) — fail-closed covered transitively (Q3).
        Console.WriteLine($"   absent nested types share this exact resolution path (no per-type branch) — fail-closed covered transitively: {absent.Count}{(absent.Count > 0 ? " (" + string.Join(", ", absent) + ")" : "")}");
        DumpList("NESTED FAIL-CLOSED gaps (a missing/uncacheable nested record did NOT fail loud)", failures);
        var pass = failures.Count == 0 && casesRun > 0;
        Console.WriteLine($"   PHASE 8: {(pass ? "PASS" : (casesRun == 0 ? "INCONCLUSIVE — no instance-bearing nested type to exercise" : "INCOMPLETE — see loud failures above"))} — {casesLoud}/{casesRun} negative cases threw loud across {instanceBearing.Count} instance-bearing nested types.");
        Console.WriteLine();
        return new Phase8Result(pass, casesLoud, casesRun, instanceBearing.Count, absent.Count);
    }

    // ============ PHASE 9 — ARM / FLOI BYTE-TRUE (wave 4) ============
    // The wave-4 standing proof: every populated condition-target (FormLinkOrIndex) SITE — a (arm-type, FLOI-prop)
    // pair, 156 by construction — reconstructs to NATIVE bytes when driven THROUGH the engine's SetFloi. Mirrors
    // Phase 7's "one sample per type, byte-true, loud-list the absent" shape. The value is expressed by its NATIVE
    // mode (read off the arm's UseAliases/UsePackageData flags — NOT via the engine, so the proof's expected value is
    // derived independently of the code under test): form via FormKey.ToString(), index via "alias N"/"packdata N".
    // So SetFloi sets the SAME flag and must reproduce the exact bytes. This drives the ENGINE (ApplyVerb -> SetFloi),
    // not hand-reflection, and cross-checks the wave-4 rulebook FLOI-accept (a native value the pre-flight rejects =
    // recognition drift, a loud gate). Also sweeps one flat (integer) arm sub-field re-set per arm type — the
    // arm-breadth taste; the full arm_surface reconciles at the final completion sweep, NOT here. Cross-master owners
    // are handled exactly like Phase 3/6 (both serialize-err identically => non-divergent, never a false fail).
    static Phase9Result Phase9_ArmFloiByteTrue(
        List<(string label, ISkyrimModGetter overlay)> sources, CorpusRulebook rulebook, ISkyrimModGetter[] allMasters)
    {
        Console.WriteLine("---- PHASE 9: arm/FLOI byte-true (reconstruct every populated condition-target to native via the engine SetFloi) ----");
        var (samples, allSites, allTargets) = SampleFloiSites(sources);
        var absentSites = allSites.Where(s => !samples.ContainsKey(s)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Console.WriteLine($"   FLOI sites by construction: {allSites.Count}; sampled (populated in sources): {samples.Count}; absent (loud-listed): {absentSites.Count}");

        var byteFail = new List<string>();
        var ruleRejects = new List<string>();    // a NATIVE FLOI value the wave-4 pre-flight wrongly rejected (recognition drift)
        var provenT = new HashSet<string>(StringComparer.Ordinal);
        int sitesProven = 0, formProven = 0, indexProven = 0;
        int armFieldOk = 0, armFieldChecked = 0;
        var armFieldSeen = new HashSet<string>(StringComparer.Ordinal);
        var armFieldFail = new List<string>();

        var caches = new Dictionary<ISkyrimModGetter, ILinkCache>();
        ILinkCache CacheFor(ISkyrimModGetter src)
        {
            if (!caches.TryGetValue(src, out var c)) caches[src] = c = src.ToImmutableLinkCache();
            return c;
        }

        foreach (var key in samples.Keys.OrderBy(x => x, StringComparer.Ordinal))
        {
            var s = samples[key];
            var modeTag = s.IndexMode ? "index" : "form";
            try
            {
                var cache = CacheFor(s.Src);

                // Native bytes: override the owner unchanged (flat via the group lifecycle; nested via the wave-3 path).
                var pn = new SkyrimMod(PatchKey, SkyrimRelease.SkyrimSE);
                WriteEngine.GenericGetOrAddAsOverride(pn, s.Owner, cache);
                var na = TrySerialize(pn, allMasters);

                // Rebuilt bytes: override, navigate to the mutable arm, drive ApplyVerb -> SetFloi with the NATIVE value.
                var pr = new SkyrimMod(PatchKey, SkyrimRelease.SkyrimSE);
                var ovR = WriteEngine.GenericGetOrAddAsOverride(pr, s.Owner, cache);
                var arm = WriteEngine.NavigateToConditionArm(ovR, s.CondField, s.CondIndex);
                var req = new WriteRequest { RecordType = s.ArmType, Path = new[] { s.FloiProp }, Verb = "Set", Value = s.NativeExpr };
                if (rulebook.Validate(req) is { } rej)
                    ruleRejects.Add($"{key} [{modeTag}]: native value '{s.NativeExpr}' REJECTED by pre-flight: {rej}");
                WriteEngine.ApplyVerb(arm, req);
                var rb = TrySerialize(pr, allMasters);

                bool ok = na.bytes is not null && rb.bytes is not null
                    ? na.bytes.Length == rb.bytes.Length && na.bytes.SequenceEqual(rb.bytes)
                    : na.bytes is null && rb.bytes is null && na.err == rb.err;   // cross-master owner: both hit the limit identically

                if (ok) { sitesProven++; provenT.Add(s.TargetT); if (s.IndexMode) indexProven++; else formProven++; }
                else byteFail.Add($"{key} [{modeTag}] {s.Owner.FormKey}: rebuilt != native " +
                    $"({(na.bytes is null ? "na:" + na.err : na.bytes.Length + "B")} vs {(rb.bytes is null ? "rb:" + rb.err : rb.bytes.Length + "B")})");

                // Arm-breadth taste: one flat INTEGER sub-field per arm type, re-set to its OWN value, byte-compared.
                // Integers round-trip through ToString()/Coerce exactly, so a divergence here is a real engine defect,
                // never a format artifact. Skipped (noted) for an arm with no integer field; a throw/divergence gates.
                if (armFieldSeen.Add(s.ArmType))
                {
                    var armField = FirstIntegerArmField(arm);
                    if (armField is not null)
                    {
                        armFieldChecked++;
                        try
                        {
                            var prF = new SkyrimMod(PatchKey, SkyrimRelease.SkyrimSE);
                            var ovF = WriteEngine.GenericGetOrAddAsOverride(prF, s.Owner, cache);
                            var armF = WriteEngine.NavigateToConditionArm(ovF, s.CondField, s.CondIndex);
                            var cur = armField.GetValue(armF);
                            WriteEngine.ApplyVerb(armF, new WriteRequest { RecordType = s.ArmType, Path = new[] { armField.Name }, Verb = "Set", Value = cur?.ToString() ?? "0" });
                            var fb = TrySerialize(prF, allMasters);
                            bool fok = na.bytes is not null && fb.bytes is not null
                                ? na.bytes.Length == fb.bytes.Length && na.bytes.SequenceEqual(fb.bytes)
                                : na.bytes is null && fb.bytes is null && na.err == fb.err;
                            if (fok) armFieldOk++;
                            else armFieldFail.Add($"{s.ArmType}.{armField.Name}: re-set to own value diverged from native.");
                        }
                        catch (Exception ex) { armFieldFail.Add($"{s.ArmType}.{armField.Name}: threw {Flatten(ex)}"); }
                    }
                }
            }
            catch (Exception ex) { byteFail.Add($"{key} [{modeTag}]: threw {Flatten(ex)}"); }
        }

        var unprovenT = allTargets.Where(t => !provenT.Contains(t)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Console.WriteLine($"   byte-true: {sitesProven}/{samples.Count} sites ({formProven} form + {indexProven} index); distinct target-T proven: {provenT.Count}/{allTargets.Count}.");
        Console.WriteLine($"   arm sub-field re-sets byte-true: {armFieldOk}/{armFieldChecked} (one flat integer field per arm type — arm-breadth taste; full arm_surface = final sweep).");
        Console.WriteLine($"   ABSENT FLOI sites (0 populated instance in any loaded source; the SetFloi path is identical — surfaced, never silently covered): {absentSites.Count}");
        if (absentSites.Count > 0) Console.WriteLine($"        {string.Join(", ", absentSites.Take(40))}{(absentSites.Count > 40 ? $"  ... +{absentSites.Count - 40} more" : "")}");
        if (unprovenT.Count > 0) Console.WriteLine($"   target-T with no populated site proven (rides a source that contains one; surfaced, not a defect): {string.Join(", ", unprovenT)}");
        DumpList("ARM/FLOI byte-true FAILURES (rebuilt diverged from native)", byteFail);
        DumpList("ARM/FLOI rulebook FALSE-REJECTS (a native value the wave-4 pre-flight wrongly rejected — recognition drift)", ruleRejects);
        DumpList("ARM sub-field byte FAILURES (a flat integer arm field re-set diverged)", armFieldFail);
        // Every sampled site must byte-prove; rule-rejects gate (the wave-4 FLOI-accept must hold). Absent sites do
        // NOT gate (no instance != a defect) — same discipline as Phase 7.
        var pass = byteFail.Count == 0 && ruleRejects.Count == 0 && armFieldFail.Count == 0 && sitesProven == samples.Count && samples.Count > 0;
        Console.WriteLine($"   PHASE 9: {(pass ? "PASS" : "INCOMPLETE — see loud failures above")} — {sitesProven}/{samples.Count} condition arm/FLOI sites byte-true; {absentSites.Count} absent sites surfaced.");
        Console.WriteLine();
        return new Phase9Result(pass, sitesProven, samples.Count, formProven, indexProven, provenT.Count, absentSites.Count, armFieldOk, armFieldChecked);
    }

    // ============ PHASE 10 — ARM / FLOI FAIL-CLOSED (wave 4 guard) ============
    // Mirrors Phase 8: the wave-4 SetFloi value-classifier must throw LOUD on a malformed condition target, never
    // write the wrong four bytes (Q3). Each malformed value is driven through the REAL engine path (ApplyVerb ->
    // SetFloi) on a real condition arm AND through the pre-flight (rulebook.Validate, which routes to the shared
    // TryClassifyFloiValue — the same classifier the engine uses); BOTH must fail closed (engine throws + rulebook
    // rejects) or the case is a gap. The non-flag-bearing-parent guard in
    // SetFloi is structurally unreachable (every FLOI's parent is an arm = IFormLinkOrIndexFlagGetter) — named here,
    // defended by the guard, never silently assumed covered.
    static Phase10Result Phase10_ArmFloiFailClosed(
        List<(string label, ISkyrimModGetter overlay)> sources, CorpusRulebook rulebook)
    {
        Console.WriteLine("---- PHASE 10: arm/FLOI fail-closed (a malformed condition target must throw loud, never write wrong bytes) ----");
        var (samples, _, _) = SampleFloiSites(sources);
        var sample = samples.Values.FirstOrDefault();
        if (sample is null)
        {
            Console.WriteLine("   PHASE 10: INCONCLUSIVE — no condition-arm instance in any loaded source to exercise.");
            Console.WriteLine();
            return new Phase10Result(false, 0, 0);
        }

        // Malformed targets: unclassifiable text, negative, float, empty, bad index prefixes, bad radix — every one
        // must throw in SetFloi AND reject in pre-flight (no silent accept on either side).
        var bad = new[] { "notaform", "-5", "1.5", "", "alias", "packdata x", "0xZZ" };
        var failures = new List<string>();
        int casesRun = 0, casesLoud = 0;
        var cache = sample.Src.ToImmutableLinkCache();
        foreach (var v in bad)
        {
            casesRun++;
            bool threw = false; string engineNote;
            try
            {
                var pr = new SkyrimMod(PatchKey, SkyrimRelease.SkyrimSE);
                var ov = WriteEngine.GenericGetOrAddAsOverride(pr, sample.Owner, cache);
                var arm = WriteEngine.NavigateToConditionArm(ov, sample.CondField, sample.CondIndex);
                WriteEngine.ApplyVerb(arm, new WriteRequest { RecordType = sample.ArmType, Path = new[] { sample.FloiProp }, Verb = "Set", Value = v });
                engineNote = "NO THROW (silent!)";
            }
            catch (Exception ex) { threw = true; engineNote = ex.GetType().Name; }

            var rej = rulebook.Validate(new WriteRequest { RecordType = sample.ArmType, Path = new[] { sample.FloiProp }, Verb = "Set", Value = v });
            var ruleNote = rej is null ? "ACCEPT (silent!)" : "REJECT";

            if (threw && rej is not null) casesLoud++;
            else failures.Add($"value '{v}': engine={engineNote}, pre-flight={ruleNote} — a malformed target must fail closed on BOTH.");
            Console.WriteLine($"   value {("'" + v + "'"),-12}  engine: {engineNote}; pre-flight: {ruleNote}");
        }

        Console.WriteLine("   non-flag-bearing-parent guard: structurally unreachable (every FLOI parent is an arm = IFormLinkOrIndexFlagGetter) — defended in SetFloi, surfaced not assumed.");
        DumpList("ARM/FLOI FAIL-CLOSED gaps (a malformed target did NOT fail closed on BOTH engine + pre-flight)", failures);
        var pass = failures.Count == 0 && casesRun > 0;
        Console.WriteLine($"   PHASE 10: {(pass ? "PASS" : "INCOMPLETE — see loud failures above")} — {casesLoud}/{casesRun} malformed targets failed closed (engine threw + pre-flight rejected) on arm {sample.ArmType}.");
        Console.WriteLine();
        return new Phase10Result(pass, casesLoud, casesRun);
    }

    // ============ PHASE 11 — MODHEADER (ORPHAN) BYTE-TRUE (wave 5) ============
    // The wave-5 header slice. ModHeader is a SINGLETON property (mod.ModHeader) — NOT a flat SkyrimGroup<T> nor a
    // FormKey'd record, so the record-resolution path can't reach it; the engine's existing ApplyVerb writes the
    // header object DIRECTLY (no new value-handling — scout-confirmed). For every writable header leaf, drive the
    // engine on a fresh mutable header (new SkyrimMod().ModHeader) and assert — through the SAME wave-0 differ — that
    // the value LANDED and nothing else moved (the Phase-2 shape); plus a struct-element Add (MasterReferences) and a
    // serialize ROUND-TRIP anchor (Author + Description survive a real .esp write+reopen). Header bookkeeping leaves
    // Mutagen manages at write-time (Stats/MasterReferences/FormID stats) are proven only-target-moved IN MEMORY here;
    // their on-disk values are Mutagen's to recompute (faithfully surfaced, not a houseCARL gap).
    //
    // The PEX half of the orphan bucket stays LOUD-DEFERRED: the Mutagen PEX read->write round-trip GATE did NOT clear
    // (pex-probe — a rewritten .pex diverges from the source bytes), so binary-PEX write is not faithful enough to wire.
    // That is the cornerstone's ONE legitimate residual (a Mutagen-vs-source delta), surfaced here every run + by the
    // census orphan bucket; houseCARL PREFERS .psc source + Papyrus compile (project_pex_prefer_source_policy). Wave 5
    // closes the ModHeader proof slice; the PEX residual reconciles at the final completion sweep's fail-loud delta report.
    static Phase11Result Phase11_ModHeader(
        List<(string label, ISkyrimModGetter overlay)> sources, WalkContext ctx, CorpusRulebook rulebook, ISkyrimModGetter[] allMasters)
    {
        Console.WriteLine("---- PHASE 11: ModHeader (orphan) byte-true — every writable header leaf only-target-moved + value-landed ----");
        const string HdrType = "SkyrimModHeader";
        if (!ctx.Corpus.Types.ContainsKey(HdrType))
        {
            Console.WriteLine($"   PHASE 11: INCONCLUSIVE — {HdrType} absent from corpus.");
            Console.WriteLine();
            return new Phase11Result(false, 0, 0, 0, 0, false);
        }

        // A fresh mutable header per edit — the patch mod's own ModHeader (a real SkyrimModHeader, Stats initialized).
        static object FreshHeader() => new SkyrimMod(PatchKey, SkyrimRelease.SkyrimSE).ModHeader;

        var leaves = TodaySettableLeaves(HdrType, ctx);
        var structLeaves = StructElementLeaves(HdrType, ctx);
        var violations = new List<string>();
        var throwsList = new List<string>();
        var falseRejects = new List<string>();
        var noInstance = new List<string>();
        var byCard = new SortedDictionary<string, int[]>(StringComparer.Ordinal);
        int attempted = 0, proven = 0;

        foreach (var leaf in leaves)
        {
            if (!byCard.TryGetValue(leaf.Cardinality, out var tally)) byCard[leaf.Cardinality] = tally = new int[2];
            var hdr = FreshHeader();
            if (!HeaderNavigable(hdr, leaf)) { noInstance.Add($"{leaf.Display} [{leaf.Cardinality}] (intermediate absent on a fresh header)"); continue; }
            var before = Snapshot(hdr, HdrType, ctx);
            WriteRequest req; string landKey; object? expectToken;
            try { (req, landKey, expectToken, _) = BuildEdit(leaf, hdr, ctx); }   // distinct unused here (no vacuous tally)
            catch (Exception ex) { throwsList.Add($"{leaf.Display} [{leaf.Cardinality}]: edit-synth threw {Flatten(ex)}"); continue; }
            if (!before.ContainsKey(landKey)) { noInstance.Add($"{leaf.Display} [{leaf.Cardinality}] (not a differ-tracked value-leaf)"); continue; }

            tally[1]++; attempted++;
            var ruleReject = rulebook.Validate(req);   // RecordType="SkyrimModHeader" — pre-flight must accept what the engine does
            try
            {
                WriteEngine.ApplyVerb(hdr, req);
                var after = Snapshot(hdr, HdrType, ctx);
                var outside = ChangedOutsideTarget(before, after, leaf.PathPrefix, EmptySet);
                var landed = Landed(after, landKey, expectToken, leaf);
                if (outside.Count == 0 && landed)
                {
                    tally[0]++; proven++;
                    if (ruleReject is not null) falseRejects.Add($"{leaf.Display} [{leaf.Cardinality}]: engine PROVED it; rulebook REJECTED: {ruleReject}");
                }
                else if (!landed)
                    violations.Add($"{leaf.Display} [{leaf.Cardinality}]: value did NOT land (expected {landKey}={expectToken}, got {Tok(after.GetValueOrDefault(landKey))})");
                else
                    violations.Add($"{leaf.Display} [{leaf.Cardinality}]: {outside.Count} field(s) moved OUTSIDE target (e.g. {outside[0]})");
            }
            catch (Exception ex) { throwsList.Add($"{leaf.Display} [{leaf.Cardinality}]: threw {Flatten(ex)}"); }
        }

        // Struct-element Add (MasterReferences): only-target-moved — the new element appears + the collection grows,
        // nothing outside the target subtree moves. Built from parts via an empty spec (paramless element ctor).
        int structAttempt = 0, structProven = 0;
        foreach (var sLeaf in structLeaves)
        {
            var hdr = FreshHeader();
            if (!HeaderNavigable(hdr, sLeaf)) continue;
            var before = Snapshot(hdr, HdrType, ctx);
            var req = new WriteRequest { RecordType = HdrType, Path = sLeaf.FieldPath, Verb = "Add", Struct = new StructSpec { Type = sLeaf.Field.ElementTypeRef! } };
            structAttempt++;
            var ruleReject = rulebook.Validate(req);
            try
            {
                WriteEngine.ApplyVerb(hdr, req);
                var after = Snapshot(hdr, HdrType, ctx);
                var outside = ChangedOutsideTarget(before, after, sLeaf.PathPrefix, EmptySet);
                var countKey = sLeaf.PathPrefix + ".__count";
                bool grew = !Equals(before.GetValueOrDefault(countKey), after.GetValueOrDefault(countKey));
                if (outside.Count == 0 && grew)
                {
                    structProven++;
                    if (ruleReject is not null) falseRejects.Add($"{sLeaf.Display}: engine PROVED Add; rulebook REJECTED: {ruleReject}");
                }
                else violations.Add($"{sLeaf.Display}: struct-Add {(grew ? "moved a field OUTSIDE the target (e.g. " + (outside.Count > 0 ? outside[0] : "?") + ")" : "did not grow the collection")}");
            }
            catch (Exception ex) { throwsList.Add($"{sLeaf.Display}: struct-Add threw {Flatten(ex)}"); }
        }

        // Serialize ROUND-TRIP anchor: Author + Description through the engine -> WritePatch -> reopen -> read back.
        // These are the modder-meaningful, serialization-stable header fields (TES4 CNAM/SNAM).
        bool roundTripOk = false; string rtNote;
        try
        {
            var patch = new SkyrimMod(PatchKey, SkyrimRelease.SkyrimSE);
            WriteEngine.ApplyVerb(patch.ModHeader, new WriteRequest { RecordType = HdrType, Path = new[] { "Author" }, Verb = "Set", Value = "houseCARL wave5" });
            WriteEngine.ApplyVerb(patch.ModHeader, new WriteRequest { RecordType = HdrType, Path = new[] { "Description" }, Verb = "Set", Value = "wave-5 header round-trip" });
            var path = WriteModToTempPath(patch, allMasters, "hc-hdr-");
            try
            {
                var back = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
                var a = back.ModHeader.Author; var d = back.ModHeader.Description;
                roundTripOk = a == "houseCARL wave5" && d == "wave-5 header round-trip";
                rtNote = $"Author='{a}' Description='{d}'";
            }
            finally { try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch { } }
        }
        catch (Exception ex) { rtNote = $"threw {Flatten(ex)}"; }

        Console.WriteLine($"   header leaf-sites: {proven}/{attempted} only-target-moved + value-landed; struct-element Add: {structProven}/{structAttempt}.");
        Console.WriteLine("   by cardinality (proven/attempted):");
        foreach (var (card, t) in byCard) Console.WriteLine($"        {card,-12} {t[0]}/{t[1]}");
        Console.WriteLine($"   serialize round-trip (Author + Description survive a real .esp write+reopen): {(roundTripOk ? "OK" : "FAIL")} — {rtNote}");
        if (noInstance.Count > 0)
            Console.WriteLine($"   header leaves not exercised on a fresh header (absent intermediate / whole-value; surfaced, not skipped): {noInstance.Count}");
        Console.WriteLine("   PEX (the orphan bucket's other half): LOUD-DEFERRED — Mutagen PEX read->write byte-identity gate NOT cleared.");
        Console.WriteLine("        (pex-probe sweep: 101/400 real .pex round-trip BYTE-IDENTICAL; 299 diverge — Mutagen RE-ORDERS the PEX string table");
        Console.WriteLine("         (e.g. \"conditional\",\"hidden\" -> \"hidden\",\"conditional\"): a semantically-valid but NOT byte-faithful re-emit, so a");
        Console.WriteLine("         houseCARL PEX write would not be surgical/only-target-moved.) A Mutagen-delta residual — the cornerstone's one");
        Console.WriteLine("         legitimate residual; PREFER .psc source + Papyrus compile (project_pex_prefer_source_policy). Gate criterion");
        Console.WriteLine("         RESOLVED (Aaron 2026-06-01): byte-identity STANDS, always prefer .psc source — PEX binary-write deferred (not a");
        Console.WriteLine("         future wave); enumerated as the Mutagen-delta residual at the final sweep. (Only reopener: Mutagen writer becomes order-preserving.)");
        DumpList("MODHEADER violations (value didn't land / moved outside the target)", violations);
        DumpList("MODHEADER throws (engine could not perform a header write)", throwsList);
        DumpList("MODHEADER rulebook FALSE-REJECTS (engine proved it, pre-flight wrongly rejected)", falseRejects);
        var pass = violations.Count == 0 && throwsList.Count == 0 && roundTripOk && attempted > 0;
        Console.WriteLine($"   PHASE 11: {(pass ? "PASS" : "INCOMPLETE — see loud failures above")} — {proven}/{attempted} header leaf-sites + {structProven}/{structAttempt} struct-Add; serialize round-trip {(roundTripOk ? "OK" : "FAIL")}.");
        Console.WriteLine();
        return new Phase11Result(pass, proven, attempted, structProven, structAttempt, roundTripOk);
    }

    /// <summary>Navigability of a header leaf on a (non-record) ModHeader object — every intermediate hop resolvable +
    /// non-null. The header is not an IMajorRecordGetter, so the record-side <see cref="Navigable"/> can't be used.</summary>
    static bool HeaderNavigable(object hdr, TodayLeaf leaf)
    {
        object? cur = hdr;
        for (int i = 0; i < leaf.FieldPath.Length - 1; i++)
        {
            if (cur is null) return false;
            var (name, _) = WriteEngine.ParseSegment(leaf.FieldPath[i]);
            try { cur = GetValueByName(cur, name); } catch { return false; }
            if (cur is null) return false;
        }
        if (cur is null) return false;
        var (leafName, _) = WriteEngine.ParseSegment(leaf.FieldPath[^1]);
        try { GetValueByName(cur, leafName); return true; } catch { return false; }
    }

    /// <summary>One populated condition-target SITE sample, with its native value pre-expressed by mode.</summary>
    sealed record FloiSiteSample(IMajorRecordGetter Owner, ISkyrimModGetter Src, string CondField, int CondIndex,
                                 string FloiProp, string ArmType, string TargetT, bool IndexMode, string NativeExpr);

    /// <summary>The FLOI site universe BY CONSTRUCTION (every (concrete *ConditionData arm, FormLinkOrIndex prop)
    /// pair — the same 156 the scout + coerce-audit derive) + one POPULATED sample per site from the sources, its
    /// native value expressed by mode (read off the arm flags — independent of the engine). Shared by Phase 9 +
    /// Phase 10 so the site predicate lives in ONE place (no forked predicate — baseline review S2). Recognition uses
    /// the engine's shared <see cref="WriteEngine.IsFormLinkOrIndex"/> (no drift). Stops scanning once every site has
    /// a sample; if some are absent it sweeps all sources (the Phase 7 SampleNestedTypes shape).</summary>
    static (Dictionary<string, FloiSiteSample> samples, List<string> allSites, List<string> allTargets) SampleFloiSites(
        List<(string label, ISkyrimModGetter overlay)> sources)
    {
        var asm = typeof(IArmorGetter).Assembly;
        var armBase = asm.GetType("Mutagen.Bethesda.Skyrim.IConditionDataGetter");
        var allSites = new List<string>();
        var allTargets = new HashSet<string>(StringComparer.Ordinal);
        if (armBase is not null)
            foreach (var arm in asm.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false }
                         && !t.Name.EndsWith("BinaryOverlay", StringComparison.Ordinal) && armBase.IsAssignableFrom(t)))
                foreach (var p in arm.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    if (WriteEngine.IsFormLinkOrIndex(p.PropertyType))
                    {
                        allSites.Add($"{arm.Name}.{p.Name}");
                        allTargets.Add(p.PropertyType.GetGenericArguments()[0].Name);
                    }

        var samples = new Dictionary<string, FloiSiteSample>(StringComparer.Ordinal);
        var unparseable = new List<string>();   // records whose conditions Mutagen could not parse (the Mutagen-vs-xEdit READ delta) — surfaced loud, never silently skipped
        foreach (var (label, overlay) in sources)
        {
            if (samples.Count == allSites.Count) break;
            foreach (var rec in overlay.EnumerateMajorRecords())
            {
                List<(IConditionGetter cond, string field, int index)> conds;
                try { conds = WriteEngine.ConditionsOf(rec).ToList(); }   // force the lazy Mutagen parse INSIDE the guard (read-only scope)
                catch (Exception ex)
                {
                    // Mutagen could not lazily parse this record's conditions (e.g. a PerkEntryPoint effect with an
                    // unexpected parameter type flag) — the Mutagen-vs-xEdit READ delta surfacing in real data. Name it
                    // loud + count it, NEVER a silent skip; scoped to the READ so it cannot mask a houseCARL engine defect.
                    unparseable.Add($"{RecordNaming.StripOverlay(rec.GetType().Name)} {rec.FormKey}: {ex.GetType().Name}: {ex.Message}");
                    continue;
                }
                foreach (var (cond, field, index) in conds)
                {
                    var data = cond.GetType().GetProperty("Data")?.GetValue(cond);
                    if (data is null) continue;
                    var armType = RecordNaming.StripOverlay(data.GetType().Name);   // overlay -> corpus catalog name (rulebook key), as RunConditionPatch does
                    var ua = (bool)(data.GetType().GetProperty("UseAliases")?.GetValue(data) ?? false);
                    var up = (bool)(data.GetType().GetProperty("UsePackageData")?.GetValue(data) ?? false);
                    foreach (var fp in data.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (!WriteEngine.IsFormLinkOrIndex(fp.PropertyType)) continue;
                        var siteKey = $"{armType}.{fp.Name}";
                        if (samples.ContainsKey(siteKey)) continue;
                        var floi = fp.GetValue(data);
                        if (floi is null) continue;
                        bool indexMode = ua || up;
                        string? expr;
                        if (indexMode)
                            expr = floi.GetType().GetProperty("Index")?.GetValue(floi) is uint ix
                                ? (ua ? "alias " : "packdata ") + ix
                                : null;
                        else
                            expr = WriteEngine.ReadFloiFormKey(floi) is { } fk && !fk.IsNull ? fk.ToString() : null;
                        if (expr is null) continue;   // not populated in this mode — keep looking for a populated instance
                        samples[siteKey] = new FloiSiteSample(rec, overlay, field, index, fp.Name, armType,
                            fp.PropertyType.GetGenericArguments()[0].Name, indexMode, expr);
                    }
                }
                if (samples.Count == allSites.Count) break;
            }
        }
        if (unparseable.Count > 0)
        {
            Console.WriteLine($"   MUTAGEN-UNPARSEABLE records (Mutagen's READ delta vs xEdit — could not parse the record's conditions; surfaced loud, NOT a houseCARL defect, NOT silently skipped): {unparseable.Count}");
            foreach (var u in unparseable) Console.WriteLine($"        {u}");
        }
        return (samples, allSites, allTargets.OrderBy(x => x, StringComparer.Ordinal).ToList());
    }

    /// <summary>The first writable integer (exact-round-trip) sub-field on a condition arm, excluding the FLOI
    /// target(s) — the arm-breadth taste's settable field. Integers stringify and re-coerce exactly, so a byte
    /// divergence on a re-set-to-own-value is a real engine defect, not a format artifact.</summary>
    static PropertyInfo? FirstIntegerArmField(object arm)
    {
        foreach (var p in arm.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanWrite || WriteEngine.IsFormLinkOrIndex(p.PropertyType)) continue;
            var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            if (t == typeof(int) || t == typeof(uint) || t == typeof(short) || t == typeof(ushort)
                || t == typeof(byte) || t == typeof(sbyte) || t == typeof(long) || t == typeof(ulong))
                return p;
        }
        return null;
    }

    /// <summary>The nested catalog name for a runtime record iff it is one of the nested-getter types, else null.
    /// Matches by the most-derived nested getter interface the record implements (so PlacedObject isn't mistaken
    /// for a base APlaced).</summary>
    static string? NestedCatalogOf(IMajorRecordGetter rec, Dictionary<string, Type> nestedGetters)
    {
        string? best = null; int bestDepth = -1;
        foreach (var (name, g) in nestedGetters)
            if (g.IsInstanceOfType(rec))
            {
                var depth = g.GetInterfaces().Length;
                if (depth > bestDepth) { bestDepth = depth; best = name; }   // ties impossible in the current placed hierarchy (distinct interface depths)
            }
        return best;
    }

    /// <summary>Write a patch mod to a temp file and return its path for reopen (masters/presence check) — the
    /// keep-the-file sibling of <see cref="SerializeMod"/>'s read-and-delete, both via <see cref="WriteModToTempPath"/>.</summary>
    static string WriteToTemp(SkyrimMod mod, ISkyrimModGetter[] knownMasters)
        => WriteModToTempPath(mod, knownMasters, "hc-nested-");

    static readonly HashSet<string> EmptySet = new(StringComparer.Ordinal);

    /// <summary>Default source set: the full vanilla master set, then the top-N mod plugins by size (deduped by
    /// filename — largest copy wins) from the live mods/ tree, curated by record density. Each is pooled
    /// standalone; an absent dir is skipped (the vanilla set alone still runs). Enumerates only one level into
    /// mods/ (the plugin sits at each mod's data root), so the giant asset trees are never walked.</summary>
    static string[] DefaultSources()
    {
        var list = new List<string>();
        foreach (var m in VanillaMasters)
        {
            var p = Path.Combine(DefaultDataDir, m);
            if (File.Exists(p)) list.Add(p);
        }
        if (Directory.Exists(DefaultModsDir))
        {
            var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".esp", ".esm", ".esl" };
            var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1, IgnoreInaccessible = true };
            list.AddRange(Directory.EnumerateFiles(DefaultModsDir, "*.es*", opts)
                .Where(p => exts.Contains(Path.GetExtension(p)))
                .Select(p => new FileInfo(p))
                .GroupBy(fi => fi.Name, StringComparer.OrdinalIgnoreCase)                         // dedup name -> largest copy
                .Select(g => g.OrderByDescending(fi => fi.Length).First())
                .Where(fi => !VanillaMasters.Contains(fi.Name, StringComparer.OrdinalIgnoreCase)) // don't double-load DLC copies sitting in mods/
                .OrderByDescending(fi => fi.Length)
                .Take(TopModPluginsBySize)
                .Select(fi => fi.FullName));
        }
        return list.ToArray();
    }

    // ======================================================================
    //  The corpus-driven reflective snapshot (the differ's reader).
    // ======================================================================
    sealed class WalkContext
    {
        public readonly Corpus Corpus;
        public readonly SortedSet<string> UnknownKinds = new(StringComparer.Ordinal);
        public readonly List<string> DeferredElementComposition = new(); // struct-element collections (wave-1 element build)
        public WalkContext(Corpus c) => Corpus = c;
        public bool IsRecord(string name) => Corpus.Types.TryGetValue(name, out var t) && t.Kind == "record";
    }

    /// <summary>Flatten a record's entire modeled field tree to path→value-token. Fails loud (records into
    /// <see cref="WalkContext.UnknownKinds"/>) on anything it cannot classify. Corpus-driven.</summary>
    static Dictionary<string, object?> Snapshot(object record, string typeName, WalkContext ctx)
    {
        var leaves = new Dictionary<string, object?>(StringComparer.Ordinal);
        WalkType(record, typeName, "", leaves, ctx, 0);
        return leaves;
    }

    static void WalkType(object? value, string typeName, string path, Dictionary<string, object?> leaves, WalkContext ctx, int depth)
    {
        if (value is null) { leaves[path == "" ? typeName : path] = null; return; }
        if (depth > 64) throw new InvalidOperationException($"walk depth>64 at '{path}' ({typeName}) — possible cycle");
        if (!ctx.Corpus.Types.TryGetValue(typeName, out var schema))
        {
            ctx.UnknownKinds.Add($"(no corpus entry) {typeName} at {(path == "" ? "<root>" : path)}");
            return;
        }
        foreach (var f in schema.Fields)
        {
            var p = path == "" ? f.Name : $"{path}.{f.Name}";
            object? v;
            try { v = GetValueByName(value, f.Name); }
            catch (Exception ex) { ctx.UnknownKinds.Add($"read-failed {typeName}.{f.Name}: {ex.Message}"); continue; }
            WalkField(v, f, p, leaves, ctx, depth);
        }
    }

    static void WalkField(object? v, FieldSchema f, string p, Dictionary<string, object?> leaves, WalkContext ctx, int depth)
    {
        switch (f.Cardinality)
        {
            case "scalar":
            case "enum":
            case "value":
            case "formlink":
                leaves[p] = LeafValue(v);
                break;
            case "substruct":
                if (v is null) { leaves[p] = null; }
                else if (IsLeafComparable(v)) leaves[p] = LeafValue(v);           // TranslatedString etc.
                else if (f.TypeRef is { } tr && ctx.IsRecord(tr)) leaves[p] = LeafValue(v); // owned child RECORD — a reference, do not recurse
                else WalkType(v, CatalogName(v, f.TypeRef, ctx), p, leaves, ctx, depth + 1);
                break;
            case "polymorphic":
                if (v is null) { leaves[p] = null; }
                else { leaves[$"{p}.__arm"] = v.GetType().Name; WalkType(v, CatalogName(v, f.TypeRef, ctx), p, leaves, ctx, depth + 1); }
                break;
            case "list":
                if (v is null) { leaves[p] = null; break; }
                {
                    int i = 0;
                    foreach (var e in (IEnumerable)v)
                    {
                        var ep = $"{p}[{i}]";
                        WalkElement(e, f, ep, leaves, ctx, depth);
                        i++;
                    }
                    leaves[$"{p}.__count"] = i;
                }
                break;
            case "dict":
                if (v is null) { leaves[p] = null; break; }
                {
                    int i = 0;
                    foreach (DictionaryEntry de in AsDictionary(v))
                    {
                        WalkElement(de.Value, f, $"{p}[{de.Key}]", leaves, ctx, depth);
                        i++;
                    }
                    leaves[$"{p}.__count"] = i;
                }
                break;
            default:
                ctx.UnknownKinds.Add($"unknown cardinality '{f.Cardinality}' on {p}");
                break;
        }
    }

    static void WalkElement(object? e, FieldSchema f, string ep, Dictionary<string, object?> leaves, WalkContext ctx, int depth)
    {
        if (e is null) { leaves[ep] = null; return; }
        if (IsLeafComparable(e)) { leaves[ep] = LeafValue(e); return; }
        if (f.ElementTypeRef is { } er)
        {
            if (ctx.IsRecord(er)) { leaves[ep] = LeafValue(e); return; }  // record element = reference, do not recurse
            WalkType(e, CatalogName(e, er, ctx), ep, leaves, ctx, depth + 1);
            return;
        }
        // No declared element ref and not leaf-comparable: classify loud rather than skip.
        ctx.UnknownKinds.Add($"un-refd element {e.GetType().Name} at {ep}");
    }

    /// <summary>A value is leaf-comparable iff equality is by value (primitives, string, enum, the known value
    /// structs, formlinks, byte blobs, TranslatedString, asset links). Anything else must be descended.</summary>
    static bool IsLeafComparable(object v)
    {
        var t = v.GetType();
        if (t.IsPrimitive || t.IsEnum || v is string) return true;
        if (v is IFormLinkGetter) return true;
        if (v is byte[] || v is Noggog.MemorySlice<byte> || v is Noggog.ReadOnlyMemorySlice<byte>) return true;
        if (v is DateTime or TimeOnly or char) return true;
        if (v is FormKey or ModKey or RecordType) return true;
        var fn = t.FullName ?? "";
        if (fn == "Mutagen.Bethesda.Strings.TranslatedString") return true;
        if (t.Namespace == "Noggog" && (t.Name.StartsWith("P2") || t.Name.StartsWith("P3") || t.Name == "Percent")) return true;
        if (fn.StartsWith("System.Drawing.Color")) return true;
        if (v is Mutagen.Bethesda.Plugins.Assets.IAssetLinkGetter) return true;
        return false;
    }

    /// <summary>Reduce a leaf to a TYPE-INDEPENDENT value token. Critically does NOT include the runtime type
    /// name — an overlay getter (AssetLinkGetter) and its materialised concrete (AssetLink) must compare EQUAL
    /// when their value is the same, or every GetOrAddAsOverride materialisation manufactures a false diff.</summary>
    static object? LeafValue(object? v)
    {
        if (v is null) return null;
        if (v is byte[] ba) return "hex:" + Convert.ToHexString(ba);
        if (v is Noggog.MemorySlice<byte> ms) return "hex:" + Convert.ToHexString(ms.ToArray());
        if (v is Noggog.ReadOnlyMemorySlice<byte> rms) return "hex:" + Convert.ToHexString(rms.ToArray());
        if (v is IFormLinkGetter fl) return "link:" + (fl.FormKeyNullable?.ToString() ?? "null");
        if (v is Mutagen.Bethesda.Plugins.Assets.IAssetLinkGetter al) return "asset:" + al.GivenPath;
        if (v is bool b) return b ? "1" : "0";
        if (v.GetType().IsEnum) return "enum:" + v;                 // enum name, type-independent
        return Convert.ToString(v, CultureInfo.InvariantCulture);
    }

    static string Tok(object? v) => v?.ToString() ?? "∅";

    static List<string> ChangedOutsideTarget(
        Dictionary<string, object?> before, Dictionary<string, object?> after, string targetPrefix, HashSet<string> allowExtraFieldNames)
    {
        var changed = new List<string>();
        var keys = new HashSet<string>(before.Keys, StringComparer.Ordinal);
        keys.UnionWith(after.Keys);
        foreach (var k in keys)
        {
            if (IsWithin(k, targetPrefix)) continue;
            if (allowExtraFieldNames.Count > 0 && allowExtraFieldNames.Contains(TopField(k))) continue; // record-flags alias family
            if (k.EndsWith("DataTypeState", StringComparison.Ordinal)) continue;                        // Mutagen partial-data presence marker (derived state, co-moves legitimately)
            before.TryGetValue(k, out var bv);
            after.TryGetValue(k, out var av);
            if (!Equals(bv, av)) changed.Add($"{k}: {Tok(bv)} -> {Tok(av)}");
        }
        return changed;
    }

    static bool IsWithin(string path, string prefix) =>
        path == prefix
        || path.StartsWith(prefix + ".", StringComparison.Ordinal)
        || path.StartsWith(prefix + "[", StringComparison.Ordinal);

    static string TopField(string path)
    {
        int dot = path.IndexOf('.'), brk = path.IndexOf('[');
        int cut = dot < 0 ? brk : (brk < 0 ? dot : Math.Min(dot, brk));
        return cut < 0 ? path : path[..cut];
    }

    static bool Landed(Dictionary<string, object?> after, string landKey, object? expectToken, TodayLeaf leaf)
    {
        if (!after.TryGetValue(landKey, out var got)) return false;
        if (Equals(got, expectToken)) return true;
        // Mutagen enforces a type-prefix on GameSetting EditorIDs (f/i/s/b). The value DID land, just normalized.
        if (leaf.LeafFieldName == "EditorID" && leaf.RecordType.StartsWith("GameSetting", StringComparison.Ordinal)
            && got is string g && expectToken is string e && g.EndsWith(e, StringComparison.Ordinal))
            return true;
        return false;
    }

    // ======================================================================
    //  Today settable-leaf enumeration (substruct-only nav; matches engine + census reach).
    //  A settable leaf is a coercible scalar/enum/value/formlink (possibly nested through substructs),
    //  a whole-coercible substruct (TranslatedString), or a list/dict/polymorphic field. A plain substruct
    //  is NAVIGATION, not a settable leaf. A value-leaf whose type the engine cannot coerce is excluded
    //  (the coerce-audit deferral — unifies this proof's denominator with the census's writable-today).
    // ======================================================================
    sealed class TodayLeaf
    {
        public required string Display { get; init; }          // Owner.path.to.leaf
        public required string PathPrefix { get; init; }       // snapshot path subtree the edit may touch
        public required string Cardinality { get; init; }
        public required string RecordType { get; init; }       // the record root catalog name (WriteRequest.RecordType)
        public required string[] FieldPath { get; init; }      // field hops from the record root — NO type name (WriteRequest.Path)
        public required string LeafKey { get; init; }          // distinct (ownerType.field) for census reconcile
        public required string LeafFieldName { get; init; }    // the leaf field's own name (alias-family check)
        public required FieldSchema Field { get; init; }
    }

    static List<TodayLeaf> TodaySettableLeaves(string typeName, WalkContext ctx)
    {
        var outp = new List<TodayLeaf>();
        var onBranch = new HashSet<string>(StringComparer.Ordinal);
        void Recurse(string ownerType, List<string> prefix, int depth)
        {
            if (depth > 16 || !onBranch.Add(ownerType)) return;
            if (ctx.Corpus.Types.TryGetValue(ownerType, out var schema))
                foreach (var f in schema.Fields)
                {
                    if (!f.Writable || f.IsIdentity) continue;
                    var path = new List<string>(prefix) { f.Name };
                    switch (f.Cardinality)
                    {
                        case "scalar": case "enum": case "value": case "formlink":
                            if (SchemaClassifier.CoercibleLeaf(f)) outp.Add(Leaf(ownerType, f, path));
                            break;
                        case "polymorphic":
                            outp.Add(Leaf(ownerType, f, path));        // arm-set (proven for MagicEffect->Light)
                            break;
                        case "list": case "dict":
                            // Coercible-element collections (scalar/enum/formlink elements) are settable today via the
                            // verbs. STRUCT-element collections need element COMPOSITION (build a struct from parts) —
                            // deferred to the element-build wave, exactly as coerce-audit defers struct list/dict elements.
                            if (SchemaClassifier.CoercibleElement(f)) outp.Add(Leaf(ownerType, f, path));
                            else ctx.DeferredElementComposition.Add($"{typeName}." + string.Join(".", path) + $" [{f.Cardinality} of {f.ElementTypeRef ?? f.ElementType ?? "?"}]");
                            break;
                        case "substruct":
                            if (SchemaClassifier.CoercibleLeaf(f)) outp.Add(Leaf(ownerType, f, path));     // TranslatedString-style whole-set
                            else if (f.TypeRef is { } tr && !ctx.IsRecord(tr)) Recurse(tr, path, depth + 1); // navigate in (not into a record)
                            break;
                    }
                }
            onBranch.Remove(ownerType);
        }
        Recurse(typeName, new(), 0);
        return outp;

        TodayLeaf Leaf(string ownerType, FieldSchema f, List<string> path) => new()
        {
            Display = $"{typeName}." + string.Join(".", path),
            PathPrefix = string.Join(".", path),
            Cardinality = f.Cardinality,
            RecordType = typeName,
            FieldPath = path.ToArray(),
            LeafKey = $"{ownerType}.{f.Name}",
            LeafFieldName = f.Name,
            Field = f,
        };
    }

    /// <summary>Shared <see cref="TodayLeaf"/> factory (so independent enumerators emit the identical shape).</summary>
    static TodayLeaf MakeLeaf(string recordType, string ownerType, FieldSchema f, List<string> path) => new()
    {
        Display = $"{recordType}." + string.Join(".", path),
        PathPrefix = string.Join(".", path),
        Cardinality = f.Cardinality,
        RecordType = recordType,
        FieldPath = path.ToArray(),
        LeafKey = $"{ownerType}.{f.Name}",
        LeafFieldName = f.Name,
        Field = f,
    };

    /// <summary>Every STRUCT-element collection leaf (Add takes a build-from-parts spec — wave-1 half B), reachable
    /// through substruct nav from the record root. The mirror of <see cref="TodaySettableLeaves"/>'s DEFERRED branch
    /// (line "STRUCT-element collections need element COMPOSITION") — now the proof's Phase-6 denominator.</summary>
    static List<TodayLeaf> StructElementLeaves(string typeName, WalkContext ctx)
    {
        var outp = new List<TodayLeaf>();
        var onBranch = new HashSet<string>(StringComparer.Ordinal);
        void Recurse(string ownerType, List<string> prefix, int depth)
        {
            if (depth > 16 || !onBranch.Add(ownerType)) return;
            if (ctx.Corpus.Types.TryGetValue(ownerType, out var schema))
                foreach (var f in schema.Fields)
                {
                    if (!f.Writable || f.IsIdentity) continue;
                    var path = new List<string>(prefix) { f.Name };
                    if (f.Cardinality is "list" or "dict" && SchemaClassifier.IsStructElement(f, ctx.Corpus))
                        outp.Add(MakeLeaf(typeName, ownerType, f, path));
                    else if (f.Cardinality == "substruct" && !SchemaClassifier.CoercibleLeaf(f) && f.TypeRef is { } tr && !ctx.IsRecord(tr))
                        Recurse(tr, path, depth + 1);                       // navigate into a substruct (not a record) to reach nested struct-element lists
                }
            onBranch.Remove(ownerType);
        }
        Recurse(typeName, new(), 0);
        return outp;
    }

    /// <summary>
    /// The COLLECTION-NAV surface (wave 1): every settable scalar/enum/value/formlink/substruct-whole leaf reachable
    /// by stepping THROUGH a struct collection element (a <c>[0]</c> hop) from the record root, at any depth. Mirrors
    /// <see cref="TodaySettableLeaves"/> but with a <c>throughColl</c> gate so it emits ONLY leaves behind ≥1 collection
    /// element (the substruct-only leaves are Phase 2's today surface, proven there). Excluded by design:
    ///   - POLYMORPHIC sub-leaves behind a collection (arm-set, e.g. <c>Condition.Data</c>) — the arm-breadth wave
    ///     (wave 4); the census's wave_collnav bucket holds these "doubly-blocked" leaves (collnav AND arm), per the
    ///     roadmap §3 note, so wave 1 does NOT zero that bucket — it closes the navigate-into slice only;
    ///   - record-element collections (nested-group wave) and arm-element collections (arm wave) — only Kind=="struct"
    ///     elements are collnav edges, matching the census navAdj definition;
    ///   - ADDING a composed element (composition, half B) — a different operation, tallied by Phase 2.
    /// Bracket hops are baked into the path (<c>Effects[0].Data.Magnitude</c>); the WriteRequest path + the snapshot
    /// key + the only-target-moved prefix all use the same notation, so the wave-0 differ scopes collnav for free.
    /// </summary>
    static List<TodayLeaf> CollnavSettableLeaves(string typeName, WalkContext ctx)
    {
        var outp = new List<TodayLeaf>();
        var onBranch = new HashSet<string>(StringComparer.Ordinal);
        void Recurse(string ownerType, List<string> prefix, int depth, bool throughColl)
        {
            if (depth > 16 || !onBranch.Add(ownerType)) return;
            if (ctx.Corpus.Types.TryGetValue(ownerType, out var schema))
                foreach (var f in schema.Fields)
                {
                    if (!f.Writable || f.IsIdentity) continue;
                    var path = new List<string>(prefix) { f.Name };
                    switch (f.Cardinality)
                    {
                        case "scalar": case "enum": case "value": case "formlink":
                            if (throughColl && SchemaClassifier.CoercibleLeaf(f)) outp.Add(Leaf(ownerType, f, path));
                            break;
                        case "substruct":
                            if (throughColl && SchemaClassifier.CoercibleLeaf(f)) outp.Add(Leaf(ownerType, f, path));         // TranslatedString-style whole-set
                            else if (f.TypeRef is { } tr && !ctx.IsRecord(tr)) Recurse(tr, path, depth + 1, throughColl); // descend (carry throughColl)
                            break;
                        case "list": case "dict":
                            if (f.ElementTypeRef is { } er
                                && ctx.Corpus.Types.TryGetValue(er, out var et) && et.Kind == "struct")
                            {
                                var elemPath = new List<string>(prefix) { f.Name + "[0]" };
                                Recurse(er, elemPath, depth + 1, throughColl: true);                         // step INTO a struct element
                            }
                            break;
                        // polymorphic: arm-set leaf → arm-breadth wave (wave 4); excluded from the collnav slice.
                    }
                }
            onBranch.Remove(ownerType);
        }
        Recurse(typeName, new(), 0, throughColl: false);
        return outp;

        TodayLeaf Leaf(string ownerType, FieldSchema f, List<string> path) => new()
        {
            Display = $"{typeName}." + string.Join(".", path),
            PathPrefix = string.Join(".", path),
            Cardinality = f.Cardinality,
            RecordType = typeName,
            FieldPath = path.ToArray(),
            LeafKey = $"{ownerType}.{f.Name}",
            LeafFieldName = f.Name,
            Field = f,
        };
    }

    // CoercibleLeaf / CoercibleElement are now provided by the shared
    // SchemaClassifier — see SchemaClassifier.cs.

    // ======================================================================
    //  Edit synthesis — produce a value GUARANTEED different from current where the type allows.
    //  Returns (request, snapshot-path-where-the-value-should-land, expected-token, distinct?).
    // ======================================================================
    static (WriteRequest req, string landKey, object? expectToken, bool distinct) BuildEdit(TodayLeaf leaf, object record, WalkContext ctx)
    {
        var f = leaf.Field;
        var rec = leaf.RecordType;

        switch (f.Cardinality)
        {
            case "scalar":
            case "value":
            case "substruct": // whole-coercible (TranslatedString)
            {
                var aq = f.MutableTypeAssemblyQualified ?? f.GetterTypeAssemblyQualified;
                var rt = aq is null ? null : WriteEngine.ResolveType(aq);
                var (val, distinct) = DistinctScalarSample(rt, CurrentToken(record, leaf));
                var req = new WriteRequest { RecordType = rec, Path = leaf.FieldPath, Verb = "Set", Value = val };
                return (req, leaf.PathPrefix, ExpectToken(val, rt), distinct);
            }
            case "enum":
            {
                var et = ResolveEnumType(f);
                var names = et is not null ? Enum.GetNames(et) : (ctx.Corpus.Types.GetValueOrDefault(f.Type)?.EnumValues ?? new List<string>()).ToArray();
                var cur = CurrentToken(record, leaf);                              // "enum:Name"
                var pick = names.FirstOrDefault(n => "enum:" + n != cur) ?? names.FirstOrDefault() ?? "0";
                var distinct = "enum:" + pick != cur;
                var req = new WriteRequest { RecordType = rec, Path = leaf.FieldPath, Verb = "Set", Value = pick };
                return (req, leaf.PathPrefix, "enum:" + pick, distinct);
            }
            case "formlink":
            {
                var cur = CurrentToken(record, leaf);                              // "link:FORMKEY"
                var a = "000800:Skyrim.esm"; var b = "000C00:Skyrim.esm";
                var pick = cur == "link:" + a ? b : a;
                var req = new WriteRequest { RecordType = rec, Path = leaf.FieldPath, Verb = "Set", Value = pick };
                return (req, leaf.PathPrefix, "link:" + pick, cur != "link:" + pick);
            }
            case "list":
            {
                var (elem, _) = DistinctElementSample(f);
                var before = SafeGet(record, leaf);
                var idx = before is IEnumerable en ? en.Cast<object?>().Count() : 0;
                var rt = ElementRuntime(f);
                var req = new WriteRequest { RecordType = rec, Path = leaf.FieldPath, Verb = "Add", Value = elem };
                return (req, $"{leaf.PathPrefix}[{idx}]", ExpectToken(elem, rt), true); // Add always changes the list
            }
            case "dict":
            {
                var keyEnum = ResolveDictKeyEnum(f);
                var keyNames = keyEnum is not null ? Enum.GetNames(keyEnum) : (ctx.Corpus.Types.GetValueOrDefault(f.KeyType ?? "")?.EnumValues ?? new List<string>()).ToArray();
                var key = keyNames.FirstOrDefault() ?? "0";
                var (val, _) = DistinctElementSample(f);
                var rt = ElementRuntime(f);
                var req = new WriteRequest { RecordType = rec, Path = leaf.FieldPath, Verb = "Set", Key = key, Value = val };
                return (req, $"{leaf.PathPrefix}[{ResolveDictKeyToken(keyEnum, key)}]", ExpectToken(val, rt), true);
            }
            case "polymorphic":
            {
                var arms = f.Arms ?? (f.TypeRef is { } tr ? ctx.Corpus.Types.GetValueOrDefault(tr)?.Arms : null) ?? new();
                var curArm = (SafeGet(record, leaf))?.GetType().Name;
                var arm = arms.FirstOrDefault(a => a != curArm) ?? arms.FirstOrDefault()
                          ?? throw new InvalidOperationException($"no arms for {leaf.Display}");
                var req = new WriteRequest { RecordType = rec, Path = leaf.FieldPath, Verb = "Set", Struct = new StructSpec { Type = arm } };
                return (req, $"{leaf.PathPrefix}.__arm", arm, arm != curArm);
            }
            default:
                throw new InvalidOperationException($"no edit synthesis for cardinality '{f.Cardinality}'");
        }
    }

    /// <summary>The token a coerced value will read back as in the after-snapshot (so 'landed' compares like-for-like).</summary>
    static object? ExpectToken(string text, Type? targetType)
    {
        if (targetType is not null && WriteEngine.TryCoerce(text, targetType, out var v)) return LeafValue(v);
        return text; // fallback: rarely hit (enum/formlink handled by their own cases)
    }

    static (string val, bool distinct) DistinctScalarSample(Type? rt, object? curToken)
    {
        var u = rt is null ? null : (Nullable.GetUnderlyingType(rt) ?? rt);
        string A, B;
        if (u == typeof(bool)) { var cur = Equals(curToken, "1"); return ((!cur).ToString(), true); }
        else if (u == typeof(string) || u?.FullName == "Mutagen.Bethesda.Strings.TranslatedString") { A = "HC_Proof"; B = "HC_Proof2"; }
        else if (u == typeof(float) || u == typeof(double)) { A = "1.5"; B = "2.5"; }
        else if (u == typeof(byte) || u == typeof(sbyte) || u == typeof(short) || u == typeof(ushort)
                 || u == typeof(int) || u == typeof(uint) || u == typeof(long) || u == typeof(ulong)) { A = "7"; B = "8"; }
        else if (u == typeof(System.Drawing.Color)) { A = "10,20,30"; B = "40,50,60"; }
        else if (u?.FullName == "Noggog.Percent") { A = "0.25"; B = "0.75"; }
        else if (u?.Namespace == "Noggog" && u.Name.StartsWith("P2")) { A = "3,4"; B = "5,6"; }
        else if (u?.Namespace == "Noggog" && u.Name.StartsWith("P3")) { A = "3,4,5"; B = "6,7,8"; }
        else if (u == typeof(DateTime)) { A = "2026-01-02T03:04:05"; B = "2027-02-03T04:05:06"; }
        else if (u == typeof(TimeOnly)) { A = "04:05:06"; B = "07:08:09"; }
        else if (u == typeof(char)) { A = "Y"; B = "Z"; }
        else if (u == typeof(string[])) { A = "a,b"; B = "c,d"; }
        else if (u == typeof(RecordType)) { A = "ABCD"; B = "WXYZ"; }
        else if (u == typeof(FormKey)) { A = "000800:Skyrim.esm"; B = "000C00:Skyrim.esm"; }
        else if (u == typeof(ModKey)) { A = "Skyrim.esm"; B = "Update.esm"; }
        else if (u is { IsGenericType: true } && u.GetGenericTypeDefinition().Name.StartsWith("AssetLink")) { A = @"hc\proof.dds"; B = @"hc\proof2.dds"; }
        else if (u is { IsGenericType: true } && (u.GetGenericTypeDefinition() == typeof(Noggog.MemorySlice<>) || u.GetGenericTypeDefinition() == typeof(Noggog.ReadOnlyMemorySlice<>))) { A = "DEADBEEF"; B = "CAFEF00D"; }
        else return ("0", false); // unknown scalar: neutral token, not claimed distinct (engine fails loud if uncoercible)

        // pick whichever sample differs from the current value
        var ta = ExpectToken(A, rt);
        return Equals(ta, curToken) ? (B, true) : (A, true);
    }

    static (string val, bool distinct) DistinctElementSample(FieldSchema f)
    {
        var u = ElementRuntime(f);
        u = u is null ? null : (Nullable.GetUnderlyingType(u) ?? u);
        if (u is null) return ("0", false);
        if (u == typeof(string)) return ("HC_Proof", true);
        if (u == typeof(bool)) return ("true", true);
        if (u.IsEnum) return (Enum.GetNames(u).FirstOrDefault() ?? "0", true);
        if (u == typeof(float) || u == typeof(double)) return ("1.5", true);
        if (u.IsGenericType && (u.GetGenericTypeDefinition() == typeof(Noggog.MemorySlice<>) || u.GetGenericTypeDefinition() == typeof(Noggog.ReadOnlyMemorySlice<>))) return ("DEADBEEF", true);
        if (typeof(IFormLinkGetter).IsAssignableFrom(u) || (u.IsGenericType && u.GetGenericTypeDefinition().Name.StartsWith("IFormLink"))) return ("000800:Skyrim.esm", true);
        return ("7", true);
    }

    static Type? ElementRuntime(FieldSchema f) =>
        f.ElementTypeAssemblyQualified is { } aq ? WriteEngine.ResolveType(aq) : null;

    static Type? ResolveEnumType(FieldSchema f)
    {
        var aq = f.MutableTypeAssemblyQualified ?? f.GetterTypeAssemblyQualified;
        var rt = aq is null ? null : WriteEngine.ResolveType(aq);
        if (rt is null) return null;
        rt = Nullable.GetUnderlyingType(rt) ?? rt;
        return rt.IsEnum ? rt : null;
    }

    static Type? ResolveDictKeyEnum(FieldSchema f)
    {
        // The dict's key type AQ is not separately emitted; derive from the runtime dict if possible later.
        // Most dict keys in the corpus are enums named by KeyType — resolve via the Skyrim assembly.
        if (f.KeyType is null) return null;
        var t = typeof(SkyrimMod).Assembly.GetType("Mutagen.Bethesda.Skyrim." + f.KeyType);
        return t is { IsEnum: true } ? t : null;
    }

    static object ResolveDictKeyToken(Type? keyEnum, string key) => key; // snapshot keys dict entries by Key.ToString()

    // LeafValue always yields a string token (or null) for leaf-comparable values, so token equality is a
    // VALUE comparison (object== would be reference equality — silently wrong).
    static string? CurrentToken(object record, TodayLeaf leaf) => LeafValue(SafeGet(record, leaf)) as string;

    static object? SafeGet(object record, TodayLeaf leaf)
    {
        object? cur = record;
        foreach (var seg in leaf.FieldPath)
        {
            if (cur is null) return null;
            var (name, key) = WriteEngine.ParseSegment(seg);    // bracket-aware: collnav sub-fields read through the [idx] hop
            try { cur = GetValueByName(cur, name); } catch { return null; }
            if (key is not null && cur is not null) cur = IndexElement(cur, key);
        }
        return cur;
    }

    /// <summary>Navigable on THIS instance? Every intermediate substruct must be non-null. A list/dict leaf is
    /// navigable even when the collection is null — the engine MATERIALIZES an absent collection on Add/Set (so
    /// "add to an absent collection" is in scope, byte-proven in Phase 3). Checked on the getter — its nullness
    /// matches the override (a deep copy).</summary>
    static bool Navigable(IMajorRecordGetter rec, TodayLeaf leaf)
    {
        object? cur = rec;
        for (int i = 0; i < leaf.FieldPath.Length - 1; i++) // intermediate hops only
        {
            if (cur is null) return false;
            try { cur = GetValueByName(cur, leaf.FieldPath[i]); } catch { return false; }
            if (cur is null) return false;
        }
        if (cur is null) return false;
        if (leaf.Cardinality is "list" or "dict")
        {
            // Absent (null) collections ARE reachable now — the engine materializes them on Add/Set, so
            // "add to an absent collection" is a proven today operation, not a skipped residual. Require only
            // that the property is readable (a genuinely unreadable property is a real reach failure).
            try { GetValueByName(cur, leaf.FieldPath[^1]); return true; } catch { return false; }
        }
        return true;
    }

    /// <summary>Bracket-aware navigability for a collnav leaf (Phase 5): every intermediate hop resolvable + non-null,
    /// and each collection-element hop (<c>[0]</c>) actually POPULATED on this instance. Checked on the getter, whose
    /// nullness/population matches the override (a deep copy). A no-instance result is a surfaced residual, never a skip.</summary>
    static bool CollnavNavigable(IMajorRecordGetter rec, TodayLeaf leaf)
    {
        object? cur = rec;
        for (int i = 0; i < leaf.FieldPath.Length - 1; i++)
        {
            if (cur is null) return false;
            var (name, key) = WriteEngine.ParseSegment(leaf.FieldPath[i]);
            try { cur = GetValueByName(cur, name); } catch { return false; }
            if (cur is null) return false;
            if (key is not null) { cur = IndexElement(cur, key); if (cur is null) return false; }
        }
        if (cur is null) return false;
        var (leafName, _) = WriteEngine.ParseSegment(leaf.FieldPath[^1]);
        try { GetValueByName(cur, leafName); return true; } catch { return false; }
    }

    /// <summary>Index a collection element for read-side navigation: integer key → list element at that index
    /// (enumerate, since ExtendedList isn't non-generic IList); else dict element by Key.ToString(). Null if absent.</summary>
    static object? IndexElement(object coll, string key)
    {
        if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) && coll is IEnumerable en)
        {
            int j = 0;
            foreach (var e in en) if (j++ == idx) return e;
            return null;
        }
        foreach (DictionaryEntry de in AsDictionary(coll))
            if (string.Equals(de.Key?.ToString(), key, StringComparison.OrdinalIgnoreCase)) return de.Value;
        return null;
    }

    // ======================================================================
    //  Record-flags alias family — DERIVED empirically (no hand-list).
    //  Set MajorRecordFlagsRaw to a distinct value on a sample record; the snapshot paths that co-move are the
    //  family (IsCompressed/IsDeleted/the flags enum/…). They project one storage int — intra-family moves are
    //  expected, never a leak. Inherited identically by all records, so deriving once suffices.
    // ======================================================================
    static HashSet<string> DeriveRecordFlagAliasFamily(string typeName, List<IMajorRecordGetter> pool, WalkContext ctx, ILinkCache? cache = null)
    {
        var family = new HashSet<string>(StringComparer.Ordinal) { "MajorRecordFlagsRaw" };
        if (pool.Count == 0
            || !ctx.Corpus.Types.TryGetValue(typeName, out var t)
            || !t.Fields.Any(f => f.Name == "MajorRecordFlagsRaw" && f.Writable))
            return family;
        try
        {
            var rec = pool[0];
            var before = Snapshot(MakeOverride(rec, cache), typeName, ctx);
            var ov = MakeOverride(rec, cache);
            var cur = GetValueByName(ov, "MajorRecordFlagsRaw");
            var flipped = cur is int ci ? (~ci) : 0; // flip every bit → every projecting accessor changes
            WriteEngine.ApplyVerb(ov, new WriteRequest { RecordType = typeName, Path = new[] { "MajorRecordFlagsRaw" }, Verb = "Set", Value = flipped.ToString(CultureInfo.InvariantCulture) });
            var after = Snapshot(ov, typeName, ctx);
            var keys = new HashSet<string>(before.Keys, StringComparer.Ordinal); keys.UnionWith(after.Keys);
            foreach (var k in keys)
                if (!Equals(before.GetValueOrDefault(k), after.GetValueOrDefault(k)))
                    family.Add(TopField(k));
        }
        catch { /* fall back to just the raw field; a real flag leak would then surface as a violation (fail-loud) */ }
        return family;
    }

    // ======================================================================
    //  Reflection helpers
    // ======================================================================
    static IMajorRecord MakeOverride(IMajorRecordGetter rec, ILinkCache? cache = null)
        => WriteEngine.GenericGetOrAddAsOverride(new SkyrimMod(PatchKey, SkyrimRelease.SkyrimSE), rec, cache);

    static object? GetValueByName(object obj, string name)
    {
        var p = ResolveReadProperty(obj.GetType(), name)
            ?? throw new InvalidOperationException($"no readable property '{name}' on {obj.GetType().Name}");
        return p.GetValue(obj);
    }

    static readonly Dictionary<(Type, string), PropertyInfo?> _propCache = new();
    static PropertyInfo? ResolveReadProperty(Type type, string name)
    {
        var key = (type, name);
        if (_propCache.TryGetValue(key, out var cached)) return cached;
        PropertyInfo? found = null;
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>();
        queue.Enqueue(type);
        foreach (var i in type.GetInterfaces()) queue.Enqueue(i);
        while (queue.Count > 0)
        {
            var t = queue.Dequeue();
            if (!seen.Add(t)) continue;
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p is not null && p.GetIndexParameters().Length == 0) { found = p; break; }
        }
        return _propCache[key] = found;
    }

    /// <summary>Resolve a runtime value's CORPUS CATALOG NAME (not its concrete reflection name). A binary
    /// overlay loads <c>AttackBinaryOverlay</c> for catalog <c>Attack</c>; the declared ref is preferred, then
    /// the concrete name with a stripped "BinaryOverlay" suffix, then the getter interface I{X}Getter → X.</summary>
    static string CatalogName(object v, string? declaredRef, WalkContext ctx)
    {
        var t = v.GetType();
        // polymorphic/arm: the concrete identity matters — prefer the stripped concrete name when it is a catalog entry.
        var concrete = RecordNaming.StripOverlay(t.Name);
        if (ctx.Corpus.Types.ContainsKey(concrete)) return concrete;
        if (declaredRef is not null && ctx.Corpus.Types.ContainsKey(declaredRef)) return declaredRef;
        foreach (var i in t.GetInterfaces())
            if (i.Name.StartsWith("I") && i.Name.EndsWith("Getter"))
            {
                var cand = RecordNaming.StripGetterInterface(i.Name);
                if (ctx.Corpus.Types.ContainsKey(cand)) return cand;
            }
        return declaredRef ?? concrete; // let WalkType record a loud "no corpus entry" if this is wrong
    }

    // StripOverlay is now RecordNaming.StripOverlay(name) — see RecordNaming.cs.

    static IDictionary AsDictionary(object v)
    {
        if (v is IDictionary d) return d;
        var ht = new Hashtable();
        foreach (var item in (IEnumerable)v)
        {
            var it = item.GetType();
            if (it.IsGenericType && it.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
            {
                var k = it.GetProperty("Key")!.GetValue(item)!;
                ht[k] = it.GetProperty("Value")!.GetValue(item);
            }
        }
        return ht;
    }

    static List<(string typeName, List<IMajorRecordGetter> pool)> CollectInstancePools(
        IReadOnlyList<(string label, ISkyrimModGetter overlay)> sources, Corpus corpus, int perTypePerMaster, int globalCapPerType,
        out List<string> emptyRecordTypes, out List<(string label, int instances, int types)> perMaster)
    {
        var recordNames = corpus.Types.Values.Where(t => t.Kind == "record").Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var byType = new Dictionary<string, List<IMajorRecordGetter>>(StringComparer.Ordinal);
        perMaster = new List<(string, int, int)>();

        foreach (var (label, overlay) in sources)
        {
            var thisMaster = new Dictionary<string, int>(StringComparer.Ordinal); // per-type cap applied PER MASTER
            int contributed = 0;
            foreach (var p in overlay.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length != 0) continue;
                object? gv;
                try { gv = p.GetValue(overlay); } catch { continue; }
                if (gv is not IEnumerable en || gv is string) continue;
                try
                {
                    foreach (var item in en)
                    {
                        if (item is not IMajorRecordGetter mr) break;
                        var cat = RecordNaming.StripOverlay(mr.GetType().Name);
                        if (!recordNames.Contains(cat)) cat = PrimaryRecordCatalog(mr, recordNames) ?? cat;
                        if (!recordNames.Contains(cat)) break;
                        thisMaster.TryGetValue(cat, out var c);
                        if (c >= perTypePerMaster) break;     // enough from THIS group in THIS master
                        if (!byType.TryGetValue(cat, out var list)) byType[cat] = list = new();
                        if (list.Count >= globalCapPerType) break; // type already full across ALL sources (backstop)
                        list.Add(mr);
                        thisMaster[cat] = c + 1;
                        contributed++;
                    }
                }
                catch { /* group enumeration failed — surfaced via the empty list below */ }
            }
            perMaster.Add((label, contributed, thisMaster.Count));
        }

        emptyRecordTypes = recordNames.Where(rn => !byType.ContainsKey(rn)).OrderBy(x => x).ToList();
        return byType.Select(kv => (kv.Key, kv.Value)).OrderBy(x => x.Key, StringComparer.Ordinal).ToList();
    }

    // ======================================================================
    //  Phase 3 helpers — absent-collection materialization byte-identity.
    // ======================================================================
    static (SkyrimMod mod, IMajorRecord rec) OverrideIn(IMajorRecordGetter src)
    {
        var mod = new SkyrimMod(PatchKey, SkyrimRelease.SkyrimSE);
        return (mod, WriteEngine.GenericGetOrAddAsOverride(mod, src));
    }

    /// <summary>Reachable, non-null, and ≥1 element — a real populated example to clear-and-rebuild.</summary>
    static bool PopulatedCollection(IMajorRecordGetter rec, TodayLeaf leaf)
    {
        object? cur = rec;
        foreach (var seg in leaf.FieldPath) { if (cur is null) return false; try { cur = GetValueByName(cur, seg); } catch { return false; } }
        return cur is IEnumerable en && en.Cast<object?>().Any();
    }

    static List<string> ExtractAddStrings(object? collection)
    {
        var outp = new List<string>();
        if (collection is IEnumerable en) foreach (var e in en) outp.Add(ExtractAddString(e));
        return outp;
    }

    static string ExtractAddString(object? elem)
    {
        if (elem is null) return "Null";
        if (elem is IFormLinkGetter fl) return fl.FormKey.ToString();
        if (elem is byte[] ba) return Convert.ToHexString(ba);
        if (elem is Noggog.MemorySlice<byte> ms) return Convert.ToHexString(ms.ToArray());
        if (elem is Noggog.ReadOnlyMemorySlice<byte> rms) return Convert.ToHexString(rms.ToArray());
        if (elem.GetType().IsEnum) return elem.ToString()!;
        if (elem is bool b) return b ? "True" : "False";
        return Convert.ToString(elem, CultureInfo.InvariantCulture) ?? "";
    }

    /// <summary>Try to set the leaf's collection member back to null (absent) on a mutable override, via the
    /// concrete property. Returns false if the member is get-only — a Mutagen ALWAYS-present collection that is
    /// never null, so the absent-materialization case does not apply and Phase 3 simply skips it.</summary>
    static bool TryClearMember(object record, TodayLeaf leaf)
    {
        object? parent = record;
        for (int i = 0; i < leaf.FieldPath.Length - 1; i++)
        {
            parent = GetValueByName(parent, leaf.FieldPath[i]);
            if (parent is null) return false;
        }
        return WriteEngine.TrySetMemberToNull(parent, leaf.FieldPath[^1]);
    }

    /// <summary>Serialize a one-record patch mod to bytes (via the engine's WritePatch, into a temp dir whose
    /// filename matches the patch ModKey) so two builds of the same record can be compared byte-for-byte.</summary>
    static (byte[]? bytes, string? err) TrySerialize(SkyrimMod mod, ISkyrimModGetter[] knownMasters)
    {
        try { return (SerializeMod(mod, knownMasters), null); }
        catch (Exception ex) { return (null, $"{ex.GetType().Name}: {ex.Message}"); }
    }

    static byte[] SerializeMod(SkyrimMod mod, ISkyrimModGetter[] knownMasters)
    {
        var dir = Path.GetDirectoryName(WriteModToTempPath(mod, knownMasters, "hc-byteid-"))!;
        try { return File.ReadAllBytes(Path.Combine(dir, mod.ModKey.FileName.String)); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>The single write-a-patch-to-a-temp-file incantation. Writes <paramref name="mod"/> with masters to a
    /// fresh temp dir (filename = ModKey) and returns the FILE path; the caller owns the dir lifetime (read-and-delete
    /// for a byte compare, or keep-and-reopen for a masters/presence check). One home for the BeginWrite chain.</summary>
    static string WriteModToTempPath(SkyrimMod mod, ISkyrimModGetter[] knownMasters, string dirPrefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), dirPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, mod.ModKey.FileName.String);
        mod.BeginWrite.ToPath(path).WithLoadOrderFromHeaderMasters().WithKnownMasters(knownMasters).Write();
        return path;
    }

    static string? PrimaryRecordCatalog(IMajorRecordGetter rec, HashSet<string> recordNames)
    {
        foreach (var i in rec.GetType().GetInterfaces())
            if (i.Name.StartsWith("I") && i.Name.EndsWith("Getter"))
            {
                var cand = RecordNaming.StripGetterInterface(i.Name);
                if (recordNames.Contains(cand)) return cand;
            }
        return null;
    }

    // ======================================================================
    //  Phase 4 helpers — absent-SUBSTRUCT materialization byte-identity.
    //  Mirrors Phase 3 (collections) for the intermediate-substruct case: find a record where an optional
    //  substruct is present, CLEAR it to null, then REBUILD it field-by-field through the engine (the first
    //  write into it triggers MaterializeSubstruct), and assert the serialized bytes equal the native record's.
    //  The composition surface (GenderedItem<T>/Array2d<T> — no parameterless ctor) is DERIVED by construction:
    //  the engine throws CompositionRequiredException, which the proof catches and tallies as a wave-1 deferral.
    // ======================================================================
    sealed class SubstructNode
    {
        public required string Display { get; init; }       // Owner.path.to.substruct
        public required string RecordType { get; init; }    // root catalog name (WriteRequest.RecordType)
        public required string[] FieldPath { get; init; }   // hops from the record root to the substruct
        public required string SubstructType { get; init; } // the substruct's catalog name (TypeRef)
        public required string LeafKey { get; init; }       // distinct (ownerType.field) for one-check-per-site dedup
    }

    /// <summary>Every NAVIGATE-INTO substruct node (non-record TypeRef, not whole-coercible) reachable from the
    /// record root, at all depths — the intermediate substructs the engine may have to materialize when absent.</summary>
    static List<SubstructNode> NavigateIntoSubstructs(string typeName, WalkContext ctx)
    {
        var outp = new List<SubstructNode>();
        var onBranch = new HashSet<string>(StringComparer.Ordinal);
        void Recurse(string ownerType, List<string> prefix, int depth)
        {
            if (depth > 16 || !onBranch.Add(ownerType)) return;
            if (ctx.Corpus.Types.TryGetValue(ownerType, out var schema))
                foreach (var f in schema.Fields)
                {
                    if (!f.Writable || f.IsIdentity || f.Cardinality != "substruct") continue;
                    if (SchemaClassifier.CoercibleLeaf(f)) continue;                              // whole-coercible (TranslatedString) — a settable leaf, not a node
                    if (f.TypeRef is not { } tr || ctx.IsRecord(tr)) continue;   // owned child record — not navigated into
                    var path = new List<string>(prefix) { f.Name };
                    outp.Add(new SubstructNode
                    {
                        Display = $"{typeName}." + string.Join(".", path),
                        RecordType = typeName, FieldPath = path.ToArray(), SubstructType = tr, LeafKey = $"{ownerType}.{f.Name}",
                    });
                    Recurse(tr, path, depth + 1);                                // collect deeper substructs too
                }
            onBranch.Remove(ownerType);
        }
        Recurse(typeName, new(), 0);
        return outp;
    }

    /// <summary>Reachable and non-null — a present substruct to clear-and-rebuild.</summary>
    static bool PresentPopulatedSubstruct(IMajorRecordGetter rec, string[] path)
    {
        object? cur = rec;
        foreach (var seg in path) { if (cur is null) return false; try { cur = GetValueByName(cur, seg); } catch { return false; } }
        return cur is not null;
    }

    /// <summary>Clear an intermediate substruct member to null (absent) on a mutable override. False if get-only —
    /// a Mutagen ALWAYS-present substruct that is never null, so the absent-materialization case does not apply.</summary>
    static bool TryClearSubstructPath(object record, string[] path)
    {
        object? parent = record;
        for (int i = 0; i < path.Length - 1; i++) { parent = GetValueByName(parent, path[i]); if (parent is null) return false; }
        return WriteEngine.TrySetMemberToNull(parent, path[^1]);
    }

    /// <summary>Walk a PRESENT substruct and emit the engine writes that rebuild it identically (Set every simple
    /// leaf to its native value, Add every coercible-collection element). Sets <paramref name="rebuildable"/> false
    /// (with a reason) if the substruct's content is not faithfully replayable here — a polymorphic arm, a
    /// struct-element collection, an owned record, or a value the engine cannot round-trip from a string. Those are
    /// NAMED residuals (their byte-identity rides the composition/arm waves), never a silent skip.</summary>
    static List<WriteRequest> ExtractSubstructReplay(object ov, SubstructNode sn, WalkContext ctx, out bool rebuildable, out string? reason)
    {
        var reqs = new List<WriteRequest>();
        object? node = ov;
        foreach (var seg in sn.FieldPath) { if (node is null) { rebuildable = false; reason = "null mid-path at extraction"; return reqs; } node = GetValueByName(node, seg); }
        if (node is null) { rebuildable = false; reason = "substruct null at extraction"; return reqs; }
        bool rb = true; string? rs = null;
        WalkReplay(node, sn.SubstructType, sn.FieldPath, reqs, sn.RecordType, ctx, ref rb, ref rs, 0);
        rebuildable = rb; reason = rs;
        return reqs;
    }

    static void WalkReplay(object node, string typeName, string[] path, List<WriteRequest> reqs, string recordType,
        WalkContext ctx, ref bool rb, ref string? rs, int depth)
    {
        if (depth > 16) { rb = false; rs = "depth>16"; return; }
        if (!ctx.Corpus.Types.TryGetValue(typeName, out var schema)) { rb = false; rs = $"no corpus entry {typeName}"; return; }
        foreach (var f in schema.Fields)
        {
            if (!f.Writable || f.IsIdentity) continue;
            object? v;
            try { v = GetValueByName(node, f.Name); } catch { rb = false; rs = $"read-failed {f.Name}"; return; }
            var fieldPath = path.Append(f.Name).ToArray();
            switch (f.Cardinality)
            {
                case "scalar": case "enum": case "value": case "formlink":
                    if (v is null) break;                                   // null nullable leaf — fresh default is null, leave it
                    if (v is IFormLinkGetter ufl && ufl.FormKeyNullable is null) break; // UNSET nullable link == fresh default, leave it
                    var s = LeafSetString(v);
                    if (s is null) { rb = false; rs = $"unstringifiable {f.Cardinality} leaf {f.Name} ({v.GetType().Name})"; return; }
                    reqs.Add(new WriteRequest { RecordType = recordType, Path = fieldPath, Verb = "Set", Value = s });
                    break;
                case "substruct":
                    if (SchemaClassifier.CoercibleLeaf(f))                                    // whole-coercible (TranslatedString-style)
                    {
                        if (v is null) break;
                        var ws = LeafSetString(v);
                        if (ws is null) { rb = false; rs = $"unstringifiable substruct leaf {f.Name}"; return; }
                        reqs.Add(new WriteRequest { RecordType = recordType, Path = fieldPath, Verb = "Set", Value = ws });
                        break;
                    }
                    if (v is null) break;                                   // absent nested substruct — leave default (absent)
                    if (f.TypeRef is { } tr && ctx.IsRecord(tr)) { rb = false; rs = $"owned-record substruct {f.Name}"; return; }
                    WalkReplay(v, CatalogName(v, f.TypeRef, ctx), fieldPath, reqs, recordType, ctx, ref rb, ref rs, depth + 1);
                    if (!rb) return;
                    break;
                case "list":
                {
                    if (v is null) break;                                   // absent collection — fresh default is null
                    var litems = ((IEnumerable)v).Cast<object?>().Where(e => e is not null).ToList();
                    // A PRESENT-but-EMPTY list must rebuild as present-empty (count 0), NOT absent — ReplaceAll with no
                    // contents materializes the empty list (Add-on-first-element can't: there is no first element).
                    if (litems.Count == 0)
                    {
                        reqs.Add(new WriteRequest { RecordType = recordType, Path = fieldPath, Verb = "ReplaceAll", Values = Array.Empty<string>() });
                        break;
                    }
                    if (SchemaClassifier.CoercibleElement(f))
                    {
                        foreach (var e in litems)
                            reqs.Add(new WriteRequest { RecordType = recordType, Path = fieldPath, Verb = "Add", Value = ExtractAddString(e) });
                        break;
                    }
                    // struct-element list (wave-1 half B composition) — Add each element built FROM PARTS, extracting
                    // its sub-fields into an element-rooted spec (recurses for nested struct-element lists). A record-
                    // or arm-element list still bails: those are the nested-group / arm waves — NAMED, never silent.
                    if (!SchemaClassifier.IsStructElement(f, ctx.Corpus)) { rb = false; rs = $"record/arm-element list {f.Name}"; return; }
                    foreach (var e in litems)
                    {
                        var spec = ExtractStructSpec(e!, f.ElementTypeRef!, ctx, ref rb, ref rs, depth + 1);
                        if (!rb) return;
                        reqs.Add(new WriteRequest { RecordType = recordType, Path = fieldPath, Verb = "Add", Struct = spec });
                    }
                    break;
                }
                case "dict":
                    if (v is null) break;
                    if (!SchemaClassifier.CoercibleElement(f)) { rb = false; rs = $"struct/uncoercible-element dict {f.Name}"; return; }
                    foreach (DictionaryEntry de in AsDictionary(v))
                        reqs.Add(new WriteRequest { RecordType = recordType, Path = fieldPath, Verb = "Set", Key = ExtractAddString(de.Key), Value = ExtractAddString(de.Value) });
                    break;
                case "polymorphic":
                    if (v is null) break;
                    rb = false; rs = $"polymorphic arm {f.Name}"; return;
                default:
                    rb = false; rs = $"cardinality '{f.Cardinality}' {f.Name}"; return;
            }
        }
    }

    // IsStructElementField is now SchemaClassifier.IsStructElement(f, ctx.Corpus)
    // — see SchemaClassifier.cs.

    /// <summary>True iff the struct <paramref name="typeName"/> OWNS child records (a substruct/list/dict whose
    /// (element) type is a record), transitively — the signature of nested-group machinery (Worldspace blocks own
    /// Cells, …). Formlinks are references, NOT ownership, so only ownership cardinalities are followed. A struct
    /// element that owns records is the nested-group wave (wave 3), not a wave-1 build-from-parts element.</summary>
    static bool ReachesOwnedRecord(string typeName, WalkContext ctx, HashSet<string> seen)
    {
        if (!seen.Add(typeName) || !ctx.Corpus.Types.TryGetValue(typeName, out var schema)) return false;
        foreach (var f in schema.Fields)
        {
            if (f.Cardinality is not ("substruct" or "list" or "dict")) continue;   // ownership kinds only
            if ((f.ElementTypeRef ?? f.TypeRef) is not { } tr) continue;
            if (ctx.IsRecord(tr) || ReachesOwnedRecord(tr, ctx, seen)) return true;
        }
        return false;
    }

    /// <summary>Extract a present struct ELEMENT into a build-from-parts <see cref="StructSpec"/> — the inverse of
    /// <c>BuildStruct</c>. Type = the element's declared catalog type; Sets = element-rooted writes that rebuild it,
    /// produced by the same <see cref="WalkReplay"/> recursion (so nested sub-structs AND nested struct-element lists
    /// are captured). On an un-replayable element (a polymorphic arm inside — wave 4; an owned record — wave 3; or an
    /// unstringifiable value) it sets rb/rs false, so the whole rebuild becomes a NAMED residual, never a silent skip.</summary>
    static StructSpec ExtractStructSpec(object element, string elementCatalog, WalkContext ctx, ref bool rb, ref string? rs, int depth)
    {
        var sets = new List<WriteRequest>();
        WalkReplay(element, CatalogName(element, elementCatalog, ctx), Array.Empty<string>(), sets, elementCatalog, ctx, ref rb, ref rs, depth);
        return new StructSpec { Type = elementCatalog, Sets = sets.Count > 0 ? sets : null };
    }

    /// <summary>Stringify a scalar/enum/value/formlink leaf into the Set-string the engine's Coerce round-trips to
    /// the SAME value (so a rebuilt field is byte-identical). Returns null for a value kind not safely round-trippable
    /// here — the caller then marks the whole substruct a named residual rather than risk a false byte-divergence.</summary>
    static string? LeafSetString(object? v)
    {
        switch (v)
        {
            case null: return null;
            case bool b: return b ? "True" : "False";
            case string s: return s;
            case byte[] ba: return Convert.ToHexString(ba);
            case Noggog.MemorySlice<byte> ms: return Convert.ToHexString(ms.ToArray());
            case Noggog.ReadOnlyMemorySlice<byte> rms: return Convert.ToHexString(rms.ToArray());
            case IFormLinkGetter fl: return fl.FormKeyNullable is { } fk ? fk.ToString() : null; // unset link -> leave fresh default
            case Mutagen.Bethesda.Plugins.Assets.IAssetLinkGetter al: return al.GivenPath;
            case System.Drawing.Color c: return $"{c.R},{c.G},{c.B},{c.A}";
            case DateTime dt: return dt.ToString("o", CultureInfo.InvariantCulture);
            case TimeOnly to: return to.ToString("O", CultureInfo.InvariantCulture);
            case char ch: return ch.ToString();
            case FormKey fkk: return fkk.ToString();
            case ModKey mk: return mk.ToString();
            case RecordType rt: return rt.ToString();
        }
        var t = v.GetType();
        if (t.IsEnum) return v.ToString();
        if (t.IsPrimitive) return Convert.ToString(v, CultureInfo.InvariantCulture);
        var fn = t.FullName ?? "";
        if (fn == "Mutagen.Bethesda.Strings.TranslatedString") return v.ToString();
        if (t.Namespace == "Noggog" && (t.Name.StartsWith("P2") || t.Name.StartsWith("P3"))) return PointToString(v, t);
        if (fn == "Noggog.Percent")
        {
            // Percent stores a [0..1] fraction; engine coerces a Percent FROM that fraction (1-arg ctor). Recover the
            // fraction via a double/float property OR the implicit Percent->double/float operator, so it round-trips.
            var pp = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => p.GetIndexParameters().Length == 0 && (p.PropertyType == typeof(double) || p.PropertyType == typeof(float)));
            var pv = pp?.GetValue(v);
            if (pv is null)
            {
                var op = t.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m =>
                    m.Name == "op_Implicit" && (m.ReturnType == typeof(double) || m.ReturnType == typeof(float))
                    && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == t);
                pv = op?.Invoke(null, new[] { v });
            }
            return pv is null ? null : Convert.ToString(pv, CultureInfo.InvariantCulture);
        }
        return null; // not safely stringifiable -> named residual
    }

    /// <summary>Snapshot-diff two override records, returning the first differing MODELED field values (or "" when
    /// every modeled value is identical). DataTypeState keys are excluded (Mutagen derived parse-state). Backed by
    /// Phase-1 differ completeness: "" guarantees the only possible byte difference is a *DataTypeState/break marker,
    /// never an unread content field — so classifying a no-value-diff byte difference as a marker residual is sound.</summary>
    static string SnapshotValueDiff(object a, object b, string typeName, WalkContext ctx)
    {
        var sa = Snapshot(a, typeName, ctx); var sb = Snapshot(b, typeName, ctx);
        var keys = new HashSet<string>(sa.Keys, StringComparer.Ordinal); keys.UnionWith(sb.Keys);
        var diffs = keys.Where(k => !k.EndsWith("DataTypeState", StringComparison.Ordinal) && !Equals(sa.GetValueOrDefault(k), sb.GetValueOrDefault(k)))
            .OrderBy(k => k, StringComparer.Ordinal).Take(10).Select(k => $"{k}: {Tok(sa.GetValueOrDefault(k))}->{Tok(sb.GetValueOrDefault(k))}");
        return string.Join(" | ", diffs);
    }

    static string? PointToString(object v, Type t)
    {
        var x = t.GetProperty("X")?.GetValue(v); var y = t.GetProperty("Y")?.GetValue(v);
        if (x is null || y is null) return null;
        var z = t.GetProperty("Z")?.GetValue(v);
        var xs = Convert.ToString(x, CultureInfo.InvariantCulture); var ys = Convert.ToString(y, CultureInfo.InvariantCulture);
        return z is null ? $"{xs},{ys}" : $"{xs},{ys},{Convert.ToString(z, CultureInfo.InvariantCulture)}";
    }

    static string Flatten(Exception ex)
    {
        var inner = ex.InnerException is { } ie ? $" / {ie.GetType().Name}: {ie.Message}" : "";
        return $"{ex.GetType().Name}: {ex.Message}{inner}";
    }

    static string Sha(string path)
    {
        using var sha = SHA256.Create();
        using var s = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(s));
    }
}
