using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD (standing CI instrument, self-contained) for bulk-primitives WAVE 1 — the three type-agnostic
/// additions to <c>housecarl_cross_plugin_query</c> (PLAN.md P1/P2/P4):
///   • P1 <c>defined_in=</c> — narrow a plugins= scope from 'records this plugin TOUCHES' (definitions AND overrides)
///     to 'records DEFINED in this plugin' (origin FormKey), the catalogue-scope semantics; refused loud without plugins=.
///   • P2 list-valued <c>references=</c> — OR over many targets in ONE scan (was one scan per target), each match
///     recording WHICH target(s) it hit (matches=…) so a multi-target reverse lookup can be un-merged.
///   • P4 <c>group_by=</c> winner|type|defined_in — a count table over ALL matches (not limit-capped) instead of lines.
///
/// Synthesizes a 2-plugin order ON DISK (a master + a replacer that OVERRIDES one weapon and DEFINES new ones, with
/// keyword links so references= has something to reverse) and drives the REAL service-layer scan
/// (<see cref="LoadOrderService.CrossQuery"/> via the ForGuard seam) + the tool-layer guard
/// (<see cref="ReadTools.CrossPluginQuery"/>). Asserts each primitive's contract AND both loud refusals
/// (defined_in without plugins=; group_by with fields=/conflict_tree). group_by counts are cross-checked against a
/// hand tally of the same scope's per-match summaries. Self-contained: a corpus is generated in-process if none is
/// configured (in ci-all the runner pre-sets it), so type= resolution works standalone too.
///
/// Run: <c>dotnet run --project src/housecarl-generator bulk-query-primitives-guard</c>
/// </summary>
public static class BulkQueryPrimitivesProbe
{
    static int _pass, _fail;

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
            ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(repl, w1)).BasicStats = new WeaponBasicStats { Damage = 15 }; // W1 override wins
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
            var byType = svc.CrossQuery(null, null, null, false, new[] { masterName, replName }, null, 500, groupBy: "type");
            var gt = GroupMap(byType);
            var plain = svc.CrossQuery(null, null, null, false, new[] { masterName, replName }, null, 5000);   // same scope, no group_by
            var handTally = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var s in plain.Prefilled ?? Array.Empty<RecordSummary>()) handTally[s.Type] = handTally.GetValueOrDefault(s.Type) + 1;
            Check($"group_by=type over plugins=[master,Repl]: Weapon=3, Armor=2, Keyword=2, total 7 — got total {byType.Total}",
                  byType.Total == 7 && gt.GetValueOrDefault("Weapon") == 3 && gt.GetValueOrDefault("Armor") == 2 && gt.GetValueOrDefault("Keyword") == 2);
            Check("  ... group_by=type counts MATCH a hand tally of the same scope's per-match summaries",
                  plain.Total == byType.Total && gt.Count == handTally.Count && gt.All(kv => handTally.GetValueOrDefault(kv.Key) == kv.Value));

            // conflicts_only + group_by=winner: the pure-index branch (NO body) still aggregates by winner.
            var conflictWinner = svc.CrossQuery(null, null, null, true, null, null, 500, groupBy: "winner");
            var cw = GroupMap(conflictWinner);
            Check($"conflicts_only + group_by=winner (no body scope): the 1 contested record (W1) → Repl=1 — got total {conflictWinner.Total}",
                  conflictWinner.Total == 1 && cw.GetValueOrDefault(replName) == 1);

            // group_by=type WITHOUT a body-bearing scope is refused (type isn't known without a per-record fetch).
            var typeNoBody = svc.CrossQuery(null, null, null, true, null, null, 500, groupBy: "type");
            Check("group_by=type without type=/plugins= is REFUSED loud (no body to name the type)",
                  typeNoBody.Error is not null && typeNoBody.Error.Contains("group_by=type", StringComparison.OrdinalIgnoreCase));

            // group_by with an unknown key is refused before any scan.
            var badKey = svc.CrossQuery("Weapon", null, null, false, null, null, 500, groupBy: "bogus");
            Check("group_by=<unknown key> is REFUSED loud", badKey.Error is not null && badKey.Error.Contains("group_by", StringComparison.OrdinalIgnoreCase));

            // ================= tool-layer refusal — group_by cannot combine with fields=/conflict_tree= =================
            Console.WriteLine();
            Console.WriteLine("── tool-layer guard: group_by= vs fields=/conflict_tree= ──");
            var sFields = ReadTools.CrossPluginQuery(svc, type: "Weapon", references: null, editorid_contains: null,
                conflicts_only: false, plugins: null, defined_in: false, where: null, group_by: "winner",
                fields: new[] { "BasicStats.Damage" }, conflict_tree: false, limit: 500, max_chars: 0);
            Check("group_by + fields= is REFUSED loud at the tool layer",
                  sFields.StartsWith("error:", StringComparison.OrdinalIgnoreCase) && sFields.Contains("group_by", StringComparison.OrdinalIgnoreCase));
            var sTree = ReadTools.CrossPluginQuery(svc, type: "Weapon", references: null, editorid_contains: null,
                conflicts_only: false, plugins: null, defined_in: false, where: null, group_by: "winner",
                fields: null, conflict_tree: true, limit: 500, max_chars: 0);
            Check("group_by + conflict_tree=true is REFUSED loud at the tool layer",
                  sTree.StartsWith("error:", StringComparison.OrdinalIgnoreCase) && sTree.Contains("group_by", StringComparison.OrdinalIgnoreCase));

            // A positive tool-layer render sanity check: group_by=winner renders a count table (not per-match lines).
            var sGroup = ReadTools.CrossPluginQuery(svc, type: "Weapon", references: null, editorid_contains: null,
                conflicts_only: false, plugins: null, defined_in: false, where: null, group_by: "winner",
                fields: null, conflict_tree: false, limit: 500, max_chars: 0);
            Check("group_by=winner renders a 'grouped by winner' count table",
                  sGroup.Contains("grouped by winner") && sGroup.Contains($"{replName} = 2"));

            // Group-table max_chars truncation: a tiny cap clips the ROW list but the header total stays EXACT (Q3).
            var sGroupClip = ReadTools.CrossPluginQuery(svc, type: null, references: null, editorid_contains: null,
                conflicts_only: false, plugins: new[] { masterName, replName }, defined_in: false, where: null,
                group_by: "type", fields: null, conflict_tree: false, limit: 500, max_chars: 60);
            Check("group_by table truncates the ROW list under max_chars but keeps the exact total (Q3)",
                  sGroupClip.Contains("7 matches across 3 groups") && sGroupClip.Contains("before hitting max_chars=") && sGroupClip.Contains("the total above is exact"));

            // Detail-mode (fields=) multi-target references= still renders the per-match matches= un-merge line.
            var sDetail = ReadTools.CrossPluginQuery(svc, type: "Weapon", references: new[] { $"{kaFk}", $"{kbFk}" },
                editorid_contains: null, conflicts_only: false, plugins: null, defined_in: false, where: null,
                group_by: null, fields: new[] { "BasicStats.Damage" }, conflict_tree: false, limit: 500, max_chars: 0);
            Check("detail-mode (fields=) multi-target references= renders the matches= line (W3 → both targets)",
                  sDetail.Contains("matches=") && sDetail.Contains($"matches={kaFk}, {kbFk}"));

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
