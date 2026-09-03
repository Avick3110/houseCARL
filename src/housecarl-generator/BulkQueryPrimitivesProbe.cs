using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD (standing CI instrument, self-contained) for bulk-primitives WAVE 1 — the type-agnostic scan
/// primitives at the SERVICE layer (PLAN.md P1/P2/P4):
///   • P1 <c>definedIn</c> — narrow a plugins= scope from 'records this plugin TOUCHES' (definitions AND overrides)
///     to 'records DEFINED in this plugin' (origin FormKey), the catalogue-scope semantics; refused loud without plugins=.
///   • P2 list-valued <c>references</c> — OR over many targets in ONE scan (was one scan per target), each match
///     recording WHICH target(s) it hit (MatchedTargets) so a multi-target reverse lookup can be un-merged.
///   • P4 <c>groupBy</c> winner|type|defined_in — a count table over ALL matches (not limit-capped) instead of lines,
///     with case-folded keys (#248) so two spellings of one plugin never split into two rows.
///   • #223 <c>offset</c> pagination — windows tile each collect path's enumeration exactly (no gap, no overlap,
///     same order), an offset past the end is an honest empty window, and negative / under-group_by refuse loud.
///   • #233 <c>whereSource=winner</c> — the body filters (where/references/editoridContains) decide the MATCH on
///     the live load-order WINNER, not the scoped body (the 259-vs-82 post-patch-audit split); de-dups per FK, composes
///     with definedIn, and retargets EVERY body filter (editoridContains proves the widening beyond where=);
///     refused loud on an unknown value / no body filter, redundant-but-noted under a type=-only scope.
///
/// Synthesizes a 2-plugin order ON DISK (a master + a replacer that OVERRIDES one weapon and DEFINES new ones, with
/// keyword links so references= has something to reverse) and drives the REAL service-layer scan
/// (<see cref="LoadOrderService.CrossQuery"/> via the ForGuard seam). Asserts each primitive's contract and the
/// engine's loud refusals (defined_in without plugins=; group_by=type without a body-bearing scope; an unknown
/// group_by key; a negative offset; offset under group_by; an unknown where_source; where_source=winner with no
/// body filter). group_by counts are cross-checked against a hand tally of the same scope's per-match summaries.
/// Self-contained: a corpus is generated in-process if none is configured (in ci-all the runner pre-sets it), so
/// type= resolution works standalone too.
///
/// This file's TOOL-LAYER blocks were removed when the eight 1.x read tools were deleted; the surviving claims are
/// tests against <c>housecarl_records</c> in <c>src/housecarl-mcp-tests/RecordsScanProjectionTests.cs</c>. Each
/// removed block leaves a comment in its place saying what stood there.
///
/// Run: <c>dotnet run --project src/housecarl-generator bulk-query-primitives-guard</c>
/// </summary>
public static class BulkQueryPrimitivesProbe
{
    static int _pass, _fail;

    [CiProbe("bulk-query-primitives-guard")]
    public static int RunGuard(string[] args)
    {
        _pass = _fail = 0;
        Console.WriteLine("################  REGRESSION GUARD — bulk-primitives Wave 1 (cross_plugin_query defined_in / list references= / group_by)  ################");
        Console.WriteLine();

        var dir = Path.Combine(Path.GetTempPath(), "hc_bulk_query_primitives_guard_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        // type= resolution reads the schema corpus. In ci-all the runner pre-sets CorpusRulebook.CorpusPath; standalone
        // it may be unset — generate one in-process so this guard is genuinely self-contained (self-skips nothing).
        try { _ = CorpusRulebook.LoadCorpus(); }
        catch
        {
            var gen = Path.Combine(dir, "generated");
            CorpusGenerator.GenerateAll(gen, Path.Combine(dir, "refs"));
            CorpusRulebook.CorpusPath = Path.Combine(gen, "corpus.json");
            Console.WriteLine($"-- generated a corpus for type= resolution: {CorpusRulebook.CorpusPath} --");
        }

        const string masterName = "hcbpMaster.esp", replName = "hcbpRepl.esp";
        var masterPath = Path.Combine(dir, masterName);
        var replPath = Path.Combine(dir, replName);

        try
        {
            // ---- 1. MASTER: two keywords, two weapons (each carrying one keyword), one armor. Masterless (CI has no
            //         game files). W1 will be OVERRIDDEN by the replacer; W2/A1 stay master-only. ----
            var master = new SkyrimMod(ModKey.FromNameAndExtension(masterName), SkyrimRelease.SkyrimSE);
            var ka = master.Keywords.AddNew(); ka.EditorID = "hcbpKwA"; var kaFk = ka.FormKey;
            var kb = master.Keywords.AddNew(); kb.EditorID = "hcbpKwB"; var kbFk = kb.FormKey;
            var w1 = master.Weapons.AddNew(); w1.EditorID = "hcbpSword1"; w1.BasicStats = new WeaponBasicStats { Damage = 10 };
            w1.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>> { new FormLink<IKeywordGetter>(kaFk) };
            var w1Fk = w1.FormKey;
            var w2 = master.Weapons.AddNew(); w2.EditorID = "hcbpSword2"; w2.BasicStats = new WeaponBasicStats { Damage = 20 };
            w2.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>> { new FormLink<IKeywordGetter>(kbFk) };
            var w2Fk = w2.FormKey;
            var a1 = master.Armors.AddNew(); a1.EditorID = "hcbpArmor1"; var a1Fk = a1.FormKey;
            master.BeginWrite.ToPath(masterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            // ---- 2. REPLACER (masters [master]): OVERRIDE W1 (so it's a record Repl TOUCHES but does NOT define),
            //         plus DEFINE a new weapon W3 (keywords KA+KB — references BOTH targets) and a new armor A2. ----
            var repl = new SkyrimMod(ModKey.FromNameAndExtension(replName), SkyrimRelease.SkyrimSE);
            var w1ov = (IWeapon)WriteEngine.GenericGetOrAddAsOverride(repl, w1);
            w1ov.BasicStats = new WeaponBasicStats { Damage = 15 };   // W1 override wins on damage (10 -> 15)
            w1ov.EditorID = "hcbpSword1Winner";                       // ...and on EditorID (scoped 'hcbpSword1' vs winner 'hcbpSword1Winner') — lets editorid_contains prove the widened retarget under where_source=winner (keywords stay [KA] so the whole-order references= tests are untouched)
            var w3 = repl.Weapons.AddNew(); w3.EditorID = "hcbpSword3"; w3.BasicStats = new WeaponBasicStats { Damage = 30 };
            w3.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>>
                { new FormLink<IKeywordGetter>(kaFk), new FormLink<IKeywordGetter>(kbFk) };
            var w3Fk = w3.FormKey;
            var a2 = repl.Armors.AddNew(); a2.EditorID = "hcbpArmor2"; var a2Fk = a2.FormKey;
            repl.BeginWrite.ToPath(replPath).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

            Console.WriteLine($"-- synthesized {masterName} (KA,KB; W1[KA],W2[KB],A1) < {replName} (override W1; W3[KA,KB],A2) --");
            Console.WriteLine();

            using var resolver = LoadOrderResolver.Build(new[] { masterPath, replPath });
            var svc = LoadOrderService.ForGuard(resolver, new UserConfigStore(Path.Combine(dir, "houseCARL.user.json")));

            // ================= P1 — defined_in= (definitions vs touches) =================
            Console.WriteLine("── P1: defined_in= narrows plugins= from TOUCHES to DEFINITIONS ──");
            var touches = svc.CrossQuery("Weapon", null, null, false, new[] { replName }, null, 500);
            Check($"plugins=[Repl] type=Weapon TOUCHES 2 (W1 override + W3 def) — got {touches.Total}", touches.Total == 2 && touches.Keys.Count == 2);
            Check("  ... and the override W1 is among the touched", touches.Keys.Contains(w1Fk));

            var defd = svc.CrossQuery("Weapon", null, null, false, new[] { replName }, null, 500, definedIn: true);
            Check($"plugins=[Repl] type=Weapon defined_in=true DEFINES 1 (only W3) — got {defd.Total}", defd.Total == 1 && defd.Keys.Count == 1);
            Check("  ... the one defined record is W3 (origin=Repl)", defd.Keys.Count == 1 && defd.Keys[0] == w3Fk);
            Check("  ... the override W1 (origin=master) is EXCLUDED", !defd.Keys.Contains(w1Fk));
            Check($"  ... the header names the scope explicitly (ScopeLabel='{replName}')", defd.ScopeLabel == replName);

            var defRefusal = svc.CrossQuery("Weapon", null, null, false, null, null, 500, definedIn: true);
            Check("defined_in=true WITHOUT plugins= is REFUSED loud (not silently ignored)",
                  defRefusal.Error is not null && defRefusal.Error.Contains("plugins=", StringComparison.OrdinalIgnoreCase));

            // ================= P2 — list-valued references= (OR + matches= un-merge) =================
            Console.WriteLine();
            Console.WriteLine("── P2: references= is a LIST — OR over targets, each match records WHICH it hit ──");
            var multi = svc.CrossQuery("Weapon", new[] { kaFk, kbFk }, null, false, null, null, 500);
            Check($"references=[KA,KB] over Weapons matches 3 (W1→KA, W2→KB, W3→KA,KB) — got {multi.Total}", multi.Total == 3);
            Check("  ... MatchedTargets is populated for a multi-target lookup", multi.MatchedTargets is not null && multi.MatchedTargets.Count == multi.Keys.Count);
            if (multi.MatchedTargets is not null)
            {
                var matchOf = new Dictionary<FormKey, string?>();
                for (int i = 0; i < multi.Keys.Count; i++) matchOf[multi.Keys[i]] = multi.MatchedTargets[i];
                Check($"  ... W1 matches=KA only", matchOf.TryGetValue(w1Fk, out var m1) && m1 == kaFk.ToString());
                Check($"  ... W2 matches=KB only", matchOf.TryGetValue(w2Fk, out var m2) && m2 == kbFk.ToString());
                Check($"  ... W3 matches=KA, KB (both, in input order)", matchOf.TryGetValue(w3Fk, out var m3) && m3 == $"{kaFk}, {kbFk}");
            }
            var oneKa = svc.CrossQuery("Weapon", new[] { kaFk }, null, false, null, null, 500);
            Check($"references=[KA] alone matches 2 (W1,W3) — got {oneKa.Total}", oneKa.Total == 2 && oneKa.Keys.Contains(w1Fk) && oneKa.Keys.Contains(w3Fk));
            Check("  ... single-target references= adds NO matches= noise (MatchedTargets null)", oneKa.MatchedTargets is null);
            var oneKb = svc.CrossQuery("Weapon", new[] { kbFk }, null, false, null, null, 500);
            Check($"references=[KB] alone matches 2 (W2,W3) — the OR union of [KA]+[KB] is the 3 above", oneKb.Total == 2 && oneKb.Keys.Contains(w2Fk) && oneKb.Keys.Contains(w3Fk));

            // ================= P4 — group_by= aggregation =================
            Console.WriteLine();
            Console.WriteLine("── P4: group_by= winner|type|defined_in → a count table over ALL matches ──");
            var byWinner = svc.CrossQuery("Weapon", null, null, false, null, null, 500, groupBy: "winner");
            var gw = GroupMap(byWinner);
            Check($"group_by=winner over Weapons: total 3, Repl=2 (W1,W3) & master=1 (W2) — got total {byWinner.Total}",
                  byWinner.Total == 3 && byWinner.GroupBy == "winner" && gw.GetValueOrDefault(replName) == 2 && gw.GetValueOrDefault(masterName) == 1);
            Check("  ... groups are sorted by count desc (Repl(2) before master(1))",
                  byWinner.Groups is { Count: 2 } && byWinner.Groups[0].Key == replName && byWinner.Groups[1].Key == masterName);

            var byDef = svc.CrossQuery("Weapon", null, null, false, null, null, 500, groupBy: "defined_in");
            var gd = GroupMap(byDef);
            Check($"group_by=defined_in over Weapons: master=2 (W1,W2 defined there) & Repl=1 (W3) — got total {byDef.Total}",
                  byDef.Total == 3 && gd.GetValueOrDefault(masterName) == 2 && gd.GetValueOrDefault(replName) == 1);

            // group_by=type over a broad (all-touched) scope, cross-checked against a hand tally of the same scope.
            var byType = svc.CrossQuery((string?)null, null, null, false, new[] { masterName, replName }, null, 500, groupBy: "type");
            var gt = GroupMap(byType);
            var plain = svc.CrossQuery((string?)null, null, null, false, new[] { masterName, replName }, null, 5000);   // same scope, no group_by
            var handTally = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var s in plain.Prefilled ?? Array.Empty<RecordSummary>()) handTally[s.Type] = handTally.GetValueOrDefault(s.Type) + 1;
            Check($"group_by=type over plugins=[master,Repl]: Weapon=3, Armor=2, Keyword=2, total 7 — got total {byType.Total}",
                  byType.Total == 7 && gt.GetValueOrDefault("Weapon") == 3 && gt.GetValueOrDefault("Armor") == 2 && gt.GetValueOrDefault("Keyword") == 2);
            Check("  ... group_by=type counts MATCH a hand tally of the same scope's per-match summaries",
                  plain.Total == byType.Total && gt.Count == handTally.Count && gt.All(kv => handTally.GetValueOrDefault(kv.Key) == kv.Value));

            // conflicts_only + group_by=winner: the pure-index branch (NO body) still aggregates by winner.
            var conflictWinner = svc.CrossQuery((string?)null, null, null, true, null, null, 500, groupBy: "winner");
            var cw = GroupMap(conflictWinner);
            Check($"conflicts_only + group_by=winner (no body scope): the 1 contested record (W1) → Repl=1 — got total {conflictWinner.Total}",
                  conflictWinner.Total == 1 && cw.GetValueOrDefault(replName) == 1);

            // group_by=type WITHOUT a body-bearing scope is refused (type isn't known without a per-record fetch).
            var typeNoBody = svc.CrossQuery((string?)null, null, null, true, null, null, 500, groupBy: "type");
            Check("group_by=type without type=/plugins= is REFUSED loud (no body to name the type)",
                  typeNoBody.Error is not null && typeNoBody.Error.Contains("group_by=type", StringComparison.OrdinalIgnoreCase));

            // group_by with an unknown key is refused before any scan.
            var badKey = svc.CrossQuery("Weapon", null, null, false, null, null, 500, groupBy: "bogus");
            Check("group_by=<unknown key> is REFUSED loud", badKey.Error is not null && badKey.Error.Contains("group_by", StringComparison.OrdinalIgnoreCase));

            // ================= #248 — group_by keys are CASE-FOLDED (case-variant plugin spellings merge) =================
            // A load-order-wide group_by=defined_in split case-variant spellings of the SAME master into two rows
            // (ccBGSSSE025-AdvDSGS.esm=40 AND ccbgssse025-advdsgs.esm=35) because the group dictionary keyed Ordinal —
            // any consumer summing per-plugin counts silently double-groups. Plugin filenames are case-insensitive
            // identifiers everywhere else in houseCARL (and in the game), so the counts must merge (#248).
            // Reproduced FAITHFULLY: two overrider plugins whose OWN masters lists spell the shared master with
            // different casing. The scan reads each plugin's own body (LoadOrderService.RecordsIn), so the origin
            // ModKey it reports for a record carries THAT plugin's master spelling — the exact real-world source of
            // the split. A(mixed) overrides one master weapon, B(lower) overrides a different one → 2 distinct FKs,
            // same master, variant casing. Ordinal → two groups of 1; OrdinalIgnoreCase → one group of 2.
            Console.WriteLine();
            Console.WriteLine("── #248: group_by keys are case-folded (case-variant plugin names merge into one group) ──");
            const string cfMasterName = "hcbpcfMaster.esp", cfAName = "hcbpcfA.esp", cfBName = "hcbpcfB.esp";
            var cfMasterPath = Path.Combine(dir, cfMasterName);
            var cfAPath = Path.Combine(dir, cfAName);
            var cfBPath = Path.Combine(dir, cfBName);

            var cfMaster = new SkyrimMod(ModKey.FromNameAndExtension(cfMasterName), SkyrimRelease.SkyrimSE);
            var cw1 = cfMaster.Weapons.AddNew(); cw1.EditorID = "hcbpcfSword1"; cw1.BasicStats = new WeaponBasicStats { Damage = 10 };
            var cw2 = cfMaster.Weapons.AddNew(); cw2.EditorID = "hcbpcfSword2"; cw2.BasicStats = new WeaponBasicStats { Damage = 20 };
            cfMaster.BeginWrite.ToPath(cfMasterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            // A overrides cw1, listing the master with its ON-DISK (mixed) casing.
            var cfA = new SkyrimMod(ModKey.FromNameAndExtension(cfAName), SkyrimRelease.SkyrimSE);
            _ = WriteEngine.GenericGetOrAddAsOverride(cfA, cw1);
            cfA.BeginWrite.ToPath(cfAPath).WithLoadOrder(new ISkyrimModGetter[] { cfMaster }).Write();

            // B overrides cw2 through a LOWERCASE-cased handle of the same master (ModKey equality is case-insensitive,
            // so it still resolves to the on-disk file) — so B's masters entry, and the origin ModKey the scan reports
            // for B's records, is lowercase.
            var cfMasterLc = new SkyrimMod(ModKey.FromNameAndExtension(cfMasterName.ToLowerInvariant()), SkyrimRelease.SkyrimSE);
            var cw2Lc = new Weapon(new FormKey(cfMasterLc.ModKey, cw2.FormKey.ID), SkyrimRelease.SkyrimSE) { EditorID = "hcbpcfSword2" };
            var cfB = new SkyrimMod(ModKey.FromNameAndExtension(cfBName), SkyrimRelease.SkyrimSE);
            _ = WriteEngine.GenericGetOrAddAsOverride(cfB, cw2Lc);
            cfB.BeginWrite.ToPath(cfBPath).WithLoadOrder(new ISkyrimModGetter[] { cfMasterLc }).Write();

            using (var cfResolver = LoadOrderResolver.Build(new[] { cfMasterPath, cfAPath, cfBPath }))
            {
                var cfSvc = LoadOrderService.ForGuard(cfResolver, new UserConfigStore(Path.Combine(dir, "houseCARL.cf.user.json")));
                var byDef248 = cfSvc.CrossQuery((string?)null, null, null, false, new[] { cfAName, cfBName }, null, 500, groupBy: "defined_in");
                // Setup sanity: the two overrides ARE seen (2 touched records) — the variance test has something to fold.
                Check($"#248 setup: plugins=[A,B] group_by=defined_in sees 2 touched records — got total {byDef248.Total}",
                      byDef248.Total == 2);
                // The fix: ONE merged group of count 2, not two case-variant rows of 1 each (the Ordinal-comparer bug).
                Check($"#248: case-variant master spellings MERGE into one group of count 2 — got {byDef248.Groups?.Count ?? 0} group(s)",
                      byDef248.Groups is { Count: 1 } && byDef248.Groups[0].Count == 2);
                // And the merged display key is a REAL spelling of the master (first-seen), never blank or mangled.
                Check("#248: the merged group key is a real (case-folded) master spelling",
                      byDef248.Groups is { Count: 1 } && string.Equals(byDef248.Groups[0].Key, cfMasterName, StringComparison.OrdinalIgnoreCase));
            }

            // ================= the cross_plugin_query TOOL-LAYER block =================
            // It stood here: the group_by-vs-fields=/conflict_tree refusals, the rendered count table, its
            // max_chars row clipping (the row LIST clips, the header total stays exact), and the per-match
            // matches= un-merge line. The tool is gone. The two refusals have no spelling on housecarl_records —
            // group_by lives inside project.form='aggregate' and fields inside the 'fields' form, so one call
            // cannot carry both, and the form-scoping rule that replaced them is asserted in
            // src/housecarl-mcp-tests/RecordsScanLaneTests.cs. The count table, the clipping and the un-merge
            // line are tests in src/housecarl-mcp-tests/RecordsScanProjectionTests.cs.

            // ================= #223 — offset= pagination + format=dense =================
            Console.WriteLine();
            Console.WriteLine("── #223: offset= pages exact windows; format=dense renders columnar rows ──");

            // Service-level paging: limit=1 windows at offset 0/1/2 tile the FULL Weapon enumeration exactly.
            var full = svc.CrossQuery("Weapon", null, null, false, null, null, 500);
            var paged = new List<FormKey>();
            for (int off = 0; off < 3; off++)
            {
                var win = svc.CrossQuery("Weapon", null, null, false, null, null, 1, offset: off);
                Check($"window offset={off} limit=1: 1 row, total still {full.Total}, offset in the outcome",
                      win.Keys.Count == 1 && win.Total == full.Total && win.Offset == off);
                Check($"  ... capped={(off < 2).ToString().ToLowerInvariant()} (matches beyond the WINDOW {(off < 2 ? "exist" : "don't")} — skipped-before-offset never reads as capped)",
                      win.Capped == (off < 2));
                paged.AddRange(win.Keys);
            }
            Check("the 3 windows tile the full enumeration EXACTLY (no gap, no overlap, same order)", paged.SequenceEqual(full.Keys));

            // offset past the end: an honest empty window — exact total, not capped (nothing beyond the window).
            var past = svc.CrossQuery("Weapon", null, null, false, null, null, 500, offset: 10);
            Check("offset past the last match: 0 rows, exact total, not capped", past.Keys.Count == 0 && past.Total == full.Total && !past.Capped);

            // refusals (Q3): negative offset; offset under group_by (a count table has no window to page).
            var neg = svc.CrossQuery("Weapon", null, null, false, null, null, 500, offset: -1);
            Check("offset=-1 is REFUSED loud", neg.Error is not null && neg.Error.Contains("offset", StringComparison.OrdinalIgnoreCase));
            var offGroup = svc.CrossQuery("Weapon", null, null, false, null, null, 500, groupBy: "winner", offset: 5);
            Check("offset + group_by is REFUSED loud (never silently ignored)",
                  offGroup.Error is not null && offGroup.Error.Contains("group_by", StringComparison.OrdinalIgnoreCase));

            // The paging TEXT render arm stood here (the window it rendered, and the next offset to continue
            // from), and after it the whole format=dense block: the summary and detail column sets, the
            // positional row shape, the winner values in the cells, and dense being materially smaller than
            // json for the same query. Both renders belong to housecarl_records now — the claims are tests in
            // src/housecarl-mcp-tests/RecordsScanProjectionTests.cs.

            // Tiling on the OTHER two collect paths (PR #239 review: type= never fires the de-dup, and conflicts_only
            // collects in its own branch — a branch-confined offset regression must not pass the guard).
            var fullP = svc.CrossQuery((string?)null, null, null, false, new[] { masterName, replName }, null, 5000);   // 7 records, de-dup ACTIVE
            var pagedP = new List<FormKey>();
            for (int off = 0; off < fullP.Total; off += 3)
            {
                var win = svc.CrossQuery((string?)null, null, null, false, new[] { masterName, replName }, null, 3, offset: off);
                Check($"plugins-scope window offset={off} limit=3: sources stay parallel to keys",
                      win.Sources is not null && win.Sources.Count == win.Keys.Count);
                pagedP.AddRange(win.Keys);
            }
            Check($"plugins=[master,Repl] windows tile the de-dup'd enumeration EXACTLY ({fullP.Total} records)",
                  pagedP.SequenceEqual(fullP.Keys));
            var fullC = svc.CrossQuery((string?)null, null, null, true, null, null, 500);                                // conflicts_only branch
            var winC0 = svc.CrossQuery((string?)null, null, null, true, null, null, 500, offset: 0);
            var winC1 = svc.CrossQuery((string?)null, null, null, true, null, null, 500, offset: 1);
            Check("conflicts_only offset: window 0 = the 1 contested record; offset=1 = honest empty window, not capped",
                  fullC.Total == 1 && winC0.Keys.SequenceEqual(fullC.Keys) && winC1.Keys.Count == 0 && winC1.Total == 1 && !winC1.Capped);

            // The zero-match-with-an-offset header arm stood here (blame the filter, not the paging), and after
            // it the scoped-dense arms: the added source column, the scoped body's own value beside the plugin
            // that produced it, and the P5 scoped-vs-winner note riding in-band. Tests in
            // src/housecarl-mcp-tests/RecordsScanProjectionTests.cs — where the note names
            // fields_source="winner", which is how housecarl_records spells that lever.

            // ================= #233 — where_source=winner (predicate decides on the LIVE winner, not the scoped body) =================
            // The bug in miniature: W1 is DEFINED in master (Damage 10) and OVERRIDDEN by repl (Damage 15). A plugins=
            // scope streams master's OWN body (10); the reporter's 259-vs-82 split is exactly this — where= on the scoped
            // body counts records that ONCE matched, where_source=winner counts those whose LIVE winner still does.
            Console.WriteLine("── #233: where_source=winner retargets the where= predicate onto the live load-order winner ──");
            var wsScoped10 = svc.CrossQuery("Weapon", null, null, false, new[] { masterName }, new[] { "BasicStats.Damage = 10" }, 500);
            Check($"where=[Damage=10] default (scoped) → W1 matches on master's OWN body 10 (Total {wsScoped10.Total})",
                  wsScoped10.Total == 1 && wsScoped10.Keys.Count == 1 && wsScoped10.Keys[0] == w1Fk && !wsScoped10.WhereWinner);
            var wsWinner10 = svc.CrossQuery("Weapon", null, null, false, new[] { masterName }, new[] { "BasicStats.Damage = 10" }, 500, whereSource: "winner");
            Check($"THE FIX: where=[Damage=10] where_source=winner → 0 (W1's live winner is repl's 15, not 10) — the scoped-vs-winner split (Total {wsWinner10.Total})",
                  wsWinner10.Total == 0 && wsWinner10.WhereWinner && wsWinner10.Error is null);
            var wsWinner15 = svc.CrossQuery("Weapon", null, null, false, new[] { masterName }, new[] { "BasicStats.Damage = 15" }, 500, whereSource: "winner");
            Check($"where=[Damage=15] where_source=winner → W1 matches on the WINNER's body 15 (scoped master is 10) (Total {wsWinner15.Total})",
                  wsWinner15.Total == 1 && wsWinner15.Keys.Count == 1 && wsWinner15.Keys[0] == w1Fk && wsWinner15.WhereWinner);

            // defined_in= composes — the issue's exact call shape (defining-plugin scope + a condition on the final winner).
            var wsDefWinner = svc.CrossQuery("Weapon", null, null, false, new[] { masterName }, new[] { "BasicStats.Damage = 15" }, 500, definedIn: true, whereSource: "winner");
            Check($"defined_in=true + where_source=winner → W1 (DEFINED in master, winner 15); W3 (defined in repl) excluded (Total {wsDefWinner.Total})",
                  wsDefWinner.Total == 1 && wsDefWinner.Keys.Count == 1 && wsDefWinner.Keys[0] == w1Fk);

            // Multi-scoped de-dup under winner-source: W1 is touched by BOTH scoped plugins, but the winner verdict is
            // FK-intrinsic — it de-dups up front and resolves the winner ONCE, so W1 counts exactly one match (not two).
            var wsBoth = svc.CrossQuery("Weapon", null, null, false, new[] { masterName, replName }, new[] { "BasicStats.Damage = 15" }, 500, whereSource: "winner");
            Check($"plugins=[master,repl] where_source=winner → W1 counted ONCE despite living in both scoped plugins (Total {wsBoth.Total}, Keys {wsBoth.Keys.Count})",
                  wsBoth.Total == 1 && wsBoth.Keys.Count == 1 && wsBoth.Keys[0] == w1Fk);

            // type=-only scope: the scan already streams the winner, so where_source=winner is REDUNDANT — accepted with
            // a note (never a silent no-op, never a hostile refusal), and identical to the plain call.
            var wsTypeOnly = svc.CrossQuery("Weapon", null, null, false, null, new[] { "BasicStats.Damage = 15" }, 500, whereSource: "winner");
            var wsTypePlain = svc.CrossQuery("Weapon", null, null, false, null, new[] { "BasicStats.Damage = 15" }, 500);
            Check($"type=-only where_source=winner → matches W1 (winner 15), carries the REDUNDANT note, same result as plain (Total {wsTypeOnly.Total} vs {wsTypePlain.Total})",
                  wsTypeOnly.Total == 1 && wsTypeOnly.Keys[0] == w1Fk && wsTypeOnly.WhereWinner
                  && wsTypeOnly.WhereSourceNote is not null && wsTypeOnly.WhereSourceNote.Contains("redundant")
                  && wsTypeOnly.Total == wsTypePlain.Total);

            // Loud refusals (Q3, up front): unknown value names scoped/winner; winner without a body filter to retarget.
            var wsBad = svc.CrossQuery("Weapon", null, null, false, new[] { masterName }, new[] { "BasicStats.Damage = 15" }, 500, whereSource: "bogus");
            Check("where_source='bogus' REFUSED naming 'scoped' and 'winner'",
                  wsBad.Error is not null && wsBad.Error.Contains("scoped") && wsBad.Error.Contains("winner"));
            var wsNoFilter = svc.CrossQuery("Weapon", null, null, false, new[] { masterName }, null, 500, whereSource: "winner");
            Check("where_source=winner WITHOUT a body filter REFUSED (nothing to retarget)",
                  wsNoFilter.Error is not null && wsNoFilter.Error.Contains("body filter"));

            // The DISPLAY-decoupling arms stood here — matching on the winner while showing the scoped body,
            // then moving display to the winner too, each with its own in-band note. Why where_source= is its
            // own parameter rather than an overload of the display pole is unchanged; the arms are tests in
            // src/housecarl-mcp-tests/RecordsScanProjectionTests.cs.

            // where_source=winner retargets ALL body filters, not just where= (the widened scope): editorid_contains=
            // rides the SAME filterBody as where=/references=. W1's WINNER editorid ('hcbpSword1Winner') differs from
            // its scoped master body ('hcbpSword1'), so 'Winner' matches ONLY under where_source=winner — proving the
            // retarget end-to-end for a non-where filter (references= shares the identical filterBody line in core).
            var ecScoped = svc.CrossQuery("Weapon", null, "Winner", false, new[] { masterName }, null, 500);
            Check($"editorid_contains='Winner' default (scoped) → 0 (master's W1 editorid is 'hcbpSword1', no 'Winner') (Total {ecScoped.Total})",
                  ecScoped.Total == 0 && !ecScoped.WhereWinner);
            var ecWinner = svc.CrossQuery("Weapon", null, "Winner", false, new[] { masterName }, null, 500, whereSource: "winner");
            Check($"editorid_contains='Winner' where_source=winner → W1 (its WINNER editorid 'hcbpSword1Winner' contains 'Winner') — the body-filter widening beyond where= (Total {ecWinner.Total})",
                  ecWinner.Total == 1 && ecWinner.Keys.Count == 1 && ecWinner.Keys[0] == w1Fk && ecWinner.WhereWinner);
            Console.WriteLine();

            // dense composed with offset, with group_by, with conflict_tree, and the unknown-format refusal
            // stood here. Three survive as tests in src/housecarl-mcp-tests/RecordsScanProjectionTests.cs: the
            // offset carried in-band, the columnar transport refusing the tree form, and the format refusal
            // naming every transport the parser accepts — derived from the parser's own enum rather than a
            // hand-typed trio. dense + group_by does NOT: housecarl_records refuses the columnar transport on
            // the aggregate form by name, which is the opposite of what that arm claimed.

            // ================= #231 — depth= expands fields= containers per match =================
            // This whole block drove housecarl_cross_plugin_query and goes with it. What expansion does — the
            // collapsed container's hint, per-match elements with no out-of-bounds noise, the round-trip token
            // on the leaf, composition with resolve_names, the json paths, the whole-record dump at depth, and
            // dense's format-hop hint — is tested against housecarl_records in
            // src/housecarl-mcp-tests/RecordsScanProjectionTests.cs. The two refusals about depth's COMPANY
            // (without fields=, and under group_by) have no spelling there: depth lives inside the
            // 'fields'/'everything' forms, so those pairings cannot be written, and the form-scoping rule that
            // replaced them is asserted in src/housecarl-mcp-tests/RecordsScanLaneTests.cs.
            //
            // depth>1 under format='dense' was a LOUD refusal here, and deleting this file's tool-layer block
            // would have taken the guard with it: housecarl_records was accepting the pair and rendering the
            // depth-1 table silently. It refuses by name now, and the arms that hold it live beside the other
            // dense refusals in src/housecarl-mcp-tests — the repair landed with the cut, not this conversion.

            Console.WriteLine();
            Console.WriteLine($"=== bulk-query-primitives-guard: {_pass} passed, {_fail} failed -> {(_fail == 0 ? "PASS" : "FAIL")} ===");
            return _fail == 0 ? 0 : 1;
        }
        catch (Exception ex) { Console.WriteLine($"   FAIL (unexpected): {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); return 1; }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    static Dictionary<string, int> GroupMap(CrossQueryOutcome q)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var g in q.Groups ?? Array.Empty<GroupCount>()) d[g.Key] = g.Count;
        return d;
    }

    static void Check(string label, bool ok)
    {
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {label}");
        if (ok) _pass++; else _fail++;
    }
}
