using System.Text.Json;
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
///   • #223 <c>offset=</c> pagination (windows tile the enumeration exactly; refusals for negative / group_by) and
///     <c>format=dense</c> (the columnar render: columns once + positional rows, cross-checked against the plain
///     summaries and the known winner field values, and materially smaller than format=json for the same query).
///   • #231 <c>depth=</c> — expand list/dict contents of fields= paths per match (read_record's semantics, applied
///     to the scan; text+json, resolve_names composes); refused loud without fields= and under format=dense
///     (positional cells), whose container cells hint the text/json hop instead of a blind knob.
///   • #233 <c>where_source=winner</c> — the body filters (where=/references=/editorid_contains=) decide the MATCH on
///     the live load-order WINNER, not the scoped body (the 259-vs-82 post-patch-audit split); de-dups per FK, composes
///     with defined_in=, decoupled from winner_fields= (DISPLAY); refused loud on an unknown value / no body filter,
///     redundant-but-noted under a type=-only scope.
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

            // Text render names the window and the NEXT offset while paging.
            var sPage = ReadTools.CrossPluginQuery(svc, type: "Weapon", references: null, editorid_contains: null,
                conflicts_only: false, plugins: null, defined_in: false, where: null, group_by: null,
                fields: null, conflict_tree: false, limit: 1, offset: 1, max_chars: 0);
            Check("text render: 'showing matches 2–2' + 'continue with offset=2'",
                  sPage.Contains("showing matches 2–2") && sPage.Contains("continue with offset=2"));

            // format=dense summary rows: columnar shape, values cross-checked against the known records.
            var sDense = ReadTools.CrossPluginQuery(svc, type: "Weapon", references: null, editorid_contains: null,
                conflicts_only: false, plugins: null, defined_in: false, where: null, group_by: null,
                fields: null, conflict_tree: false, limit: 500, max_chars: 0, format: "dense");
            using (var doc = JsonDocument.Parse(sDense))
            {
                var root = doc.RootElement;
                var cols = root.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToArray();
                Check("dense summary: columns = [formid,type,editorid,winner,override_depth]",
                      cols.SequenceEqual(new[] { "formid", "type", "editorid", "winner", "override_depth" }));
                var rows = root.GetProperty("rows").EnumerateArray().ToList();
                Check("dense summary: 3 positional rows of 5 cells, rendered=3, not truncated",
                      rows.Count == 3 && rows.All(r => r.ValueKind == JsonValueKind.Array && r.GetArrayLength() == 5)
                      && root.GetProperty("rendered").GetInt32() == 3 && !root.GetProperty("truncated").GetBoolean());
                var w1row = rows.FirstOrDefault(r => r[0].GetString() == w1Fk.ToString());
                Check("dense summary: W1's row shows type=Weapon, winner=Repl (the override wins)",
                      w1row.ValueKind == JsonValueKind.Array && w1row[1].GetString() == "Weapon" && w1row[3].GetString() == replName);
            }

            // format=dense detail rows (fields=): [formid, editorid, value…]; winner values under type= scope; and
            // the whole point — materially smaller than format=json for the SAME query.
            var sDenseF = ReadTools.CrossPluginQuery(svc, type: "Weapon", references: null, editorid_contains: null,
                conflicts_only: false, plugins: null, defined_in: false, where: null, group_by: null,
                fields: new[] { "BasicStats.Damage" }, conflict_tree: false, limit: 500, max_chars: 0, format: "dense");
            var sJsonF = ReadTools.CrossPluginQuery(svc, type: "Weapon", references: null, editorid_contains: null,
                conflicts_only: false, plugins: null, defined_in: false, where: null, group_by: null,
                fields: new[] { "BasicStats.Damage" }, conflict_tree: false, limit: 500, max_chars: 0, format: "json");
            using (var doc = JsonDocument.Parse(sDenseF))
            {
                var root = doc.RootElement;
                var cols = root.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToArray();
                Check("dense detail: columns = [formid,editorid,BasicStats.Damage]",
                      cols.SequenceEqual(new[] { "formid", "editorid", "BasicStats.Damage" }));
                var vals = root.GetProperty("rows").EnumerateArray().ToDictionary(r => r[0].GetString()!, r => r[2].GetString());
                Check("dense detail: damage cells carry the WINNER values (W1=15 override, W2=20, W3=30)",
                      vals.GetValueOrDefault(w1Fk.ToString()) == "15" && vals.GetValueOrDefault(w2Fk.ToString()) == "20"
                      && vals.GetValueOrDefault(w3Fk.ToString()) == "30");
            }
            Check($"dense detail is materially smaller than json for the same query ({sDenseF.Length} vs {sJsonF.Length} chars)",
                  sDenseF.Length < sJsonF.Length);

            // Tiling on the OTHER two collect paths (PR #239 review: type= never fires the de-dup, and conflicts_only
            // collects in its own branch — a branch-confined offset regression must not pass the guard).
            var fullP = svc.CrossQuery(null, null, null, false, new[] { masterName, replName }, null, 5000);   // 7 records, de-dup ACTIVE
            var pagedP = new List<FormKey>();
            for (int off = 0; off < fullP.Total; off += 3)
            {
                var win = svc.CrossQuery(null, null, null, false, new[] { masterName, replName }, null, 3, offset: off);
                Check($"plugins-scope window offset={off} limit=3: sources stay parallel to keys",
                      win.Sources is not null && win.Sources.Count == win.Keys.Count);
                pagedP.AddRange(win.Keys);
            }
            Check($"plugins=[master,Repl] windows tile the de-dup'd enumeration EXACTLY ({fullP.Total} records)",
                  pagedP.SequenceEqual(fullP.Keys));
            var fullC = svc.CrossQuery(null, null, null, true, null, null, 500);                                // conflicts_only branch
            var winC0 = svc.CrossQuery(null, null, null, true, null, null, 500, offset: 0);
            var winC1 = svc.CrossQuery(null, null, null, true, null, null, 500, offset: 1);
            Check("conflicts_only offset: window 0 = the 1 contested record; offset=1 = honest empty window, not capped",
                  fullC.Total == 1 && winC0.Keys.SequenceEqual(fullC.Keys) && winC1.Keys.Count == 0 && winC1.Total == 1 && !winC1.Capped);

            // total==0 with offset>0: the header must blame the FILTER, not the paging (PR #239 review).
            var sNoMatch = ReadTools.CrossPluginQuery(svc, type: "Weapon", references: null, editorid_contains: "zzz-no-such-record",
                conflicts_only: false, plugins: null, defined_in: false, where: null, group_by: null,
                fields: null, conflict_tree: false, limit: 500, offset: 5, max_chars: 0);
            Check("text render: total==0 + offset>0 says 'NO records match at any offset' (not 'lower offset=')",
                  sNoMatch.Contains("NO records match at any offset") && !sNoMatch.Contains("lower offset="));

            // Dense under a plugins= scope carries the per-row SOURCE provenance (PR #239 review, MEDIUM): the row's
            // values are the scoped plugin's OWN body — W1 reads master's damage 10 (not the winner's 15), and the
            // source cell names master so that value can never read as live truth unattributed.
            var sDenseScoped = ReadTools.CrossPluginQuery(svc, type: null, references: null, editorid_contains: null,
                conflicts_only: false, plugins: new[] { masterName, replName }, defined_in: false, where: null, group_by: null,
                fields: new[] { "BasicStats.Damage" }, conflict_tree: false, limit: 500, max_chars: 0, format: "dense");
            using (var doc = JsonDocument.Parse(sDenseScoped))
            {
                var root = doc.RootElement;
                var cols = root.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToArray();
                Check("dense scoped detail: columns gain 'source' = [formid,editorid,BasicStats.Damage,source]",
                      cols.SequenceEqual(new[] { "formid", "editorid", "BasicStats.Damage", "source" }));
                var w1row = root.GetProperty("rows").EnumerateArray().FirstOrDefault(r => r[0].GetString() == w1Fk.ToString());
                Check("dense scoped detail: W1's row = master's OWN damage 10 WITH source=master (provenance in-row)",
                      w1row.ValueKind == JsonValueKind.Array && w1row[2].GetString() == "10" && w1row[3].GetString() == masterName);
                Check("dense scoped detail: the P5 scoped-vs-winner note still rides in-band",
                      root.TryGetProperty("notes", out var notes) && notes.EnumerateArray().Any(n => (n.GetString() ?? "").Contains("winner_fields=true")));
            }

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

            // DISPLAY decoupling (the reason where_source is its own param, not an overload of winner_fields): match on the
            // winner, but SHOW the scoped origin. W1 matches on winner=15 yet the rendered cell is master's OWN 10.
            var wsDecouple = ReadTools.CrossPluginQuery(svc, type: "Weapon", references: null, editorid_contains: null,
                conflicts_only: false, plugins: new[] { masterName }, defined_in: false, where: new[] { "BasicStats.Damage = 15" },
                group_by: null, fields: new[] { "BasicStats.Damage" }, conflict_tree: false, winner_fields: false,
                where_source: "winner", limit: 500, max_chars: 0, format: "dense");
            using (var doc = JsonDocument.Parse(wsDecouple))
            {
                var root = doc.RootElement;
                var rows = root.GetProperty("rows");
                var w1row = rows.EnumerateArray().FirstOrDefault(r => r[0].GetString() == w1Fk.ToString());
                Check("#233 decouple: matched W1 on winner=15 but DISPLAYS the scoped master body (damage 10, source=master)",
                      rows.GetArrayLength() == 1 && w1row.ValueKind == JsonValueKind.Array
                      && w1row[2].GetString() == "10" && w1row[3].GetString() == masterName);
                Check("#233 decouple: the note says the MATCH was on the WINNER while values are the SCOPED body (no D2 drift)",
                      root.TryGetProperty("notes", out var notes) && notes.EnumerateArray().Any(n => { var s = n.GetString() ?? ""; return s.Contains("where_source=winner") && s.Contains("SCOPED"); }));
            }
            // ...and winner_fields=true shows the winner on both axes.
            var wsBothWinner = ReadTools.CrossPluginQuery(svc, type: "Weapon", references: null, editorid_contains: null,
                conflicts_only: false, plugins: new[] { masterName }, defined_in: false, where: new[] { "BasicStats.Damage = 15" },
                group_by: null, fields: new[] { "BasicStats.Damage" }, conflict_tree: false, winner_fields: true,
                where_source: "winner", limit: 500, max_chars: 0, format: "dense");
            using (var doc = JsonDocument.Parse(wsBothWinner))
            {
                var root = doc.RootElement;
                var w1row = root.GetProperty("rows").EnumerateArray().FirstOrDefault(r => r[0].GetString() == w1Fk.ToString());
                Check("#233 winner_fields=true: match AND display are both the winner — W1 shows damage 15",
                      w1row.ValueKind == JsonValueKind.Array && w1row[2].GetString() == "15");
                Check("#233 winner_fields=true: the note says match AND values are the winner",
                      root.TryGetProperty("notes", out var notes) && notes.EnumerateArray().Any(n => { var s = n.GetString() ?? ""; return s.Contains("where_source=winner") && s.Contains("winner_fields=true"); }));
            }

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

            // dense + offset compose: the in-band offset field + the windowed rows.
            var sDenseOff = ReadTools.CrossPluginQuery(svc, type: "Weapon", references: null, editorid_contains: null,
                conflicts_only: false, plugins: null, defined_in: false, where: null, group_by: null,
                fields: null, conflict_tree: false, limit: 1, offset: 1, max_chars: 0, format: "dense");
            using (var doc = JsonDocument.Parse(sDenseOff))
                Check("dense + offset=1 limit=1: offset in-band, 1 row, capped=true",
                      doc.RootElement.GetProperty("offset").GetInt32() == 1
                      && doc.RootElement.GetProperty("rows").GetArrayLength() == 1
                      && doc.RootElement.GetProperty("capped").GetBoolean());

            // dense + group_by renders the SAME count table as json (documented on format= — not a refusal).
            var sDenseGroup = ReadTools.CrossPluginQuery(svc, type: "Weapon", references: null, editorid_contains: null,
                conflicts_only: false, plugins: null, defined_in: false, where: null, group_by: "winner",
                fields: null, conflict_tree: false, limit: 500, max_chars: 0, format: "dense");
            Check("dense + group_by renders the json count table (groups in-band, no refusal)",
                  !sDenseGroup.StartsWith("error", StringComparison.OrdinalIgnoreCase) && sDenseGroup.Contains("\"groups\""));

            // dense + conflict_tree refused (text-only view); an unknown format names all three (Q3).
            var sDenseTree = ReadTools.CrossPluginQuery(svc, type: "Weapon", references: null, editorid_contains: null,
                conflicts_only: false, plugins: null, defined_in: false, where: null, group_by: null,
                fields: null, conflict_tree: true, limit: 500, max_chars: 0, format: "dense");
            Check("dense + conflict_tree=true is REFUSED loud",
                  sDenseTree.StartsWith("error:", StringComparison.OrdinalIgnoreCase) && sDenseTree.Contains("dense", StringComparison.OrdinalIgnoreCase));
            var sBadFmt = ReadTools.CrossPluginQuery(svc, type: "Weapon", references: null, editorid_contains: null,
                conflicts_only: false, plugins: null, defined_in: false, where: null, group_by: null,
                fields: null, conflict_tree: false, limit: 500, max_chars: 0, format: "csv");
            Check("format=csv is REFUSED naming text/json/dense",
                  sBadFmt.StartsWith("error:", StringComparison.OrdinalIgnoreCase) && sBadFmt.Contains("dense", StringComparison.OrdinalIgnoreCase));

            // ================= #231 — depth= expands fields= containers per match =================
            Console.WriteLine();
            Console.WriteLine("── #231: depth= expands list/dict contents in the scan; refusals keep summary/dense honest ──");

            // Default depth=1: a container renders the count + the GENERIC 'pass depth=2' hint — the old
            // "cross_plugin_query has no depth=; expand via batch_record_detail" redirect must be GONE.
            var sD1 = ReadTools.CrossPluginQuery(svc, type: "Weapon", references: null, editorid_contains: null,
                conflicts_only: false, plugins: null, defined_in: false, where: null, group_by: null,
                fields: new[] { "Keywords" }, conflict_tree: false, limit: 500, max_chars: 0);
            Check("depth default (1): Keywords renders as a count with the generic 'pass depth=2 to expand' hint",
                  sD1.Contains("pass depth=2 to expand") && !sD1.Contains("has no depth="));

            // depth=2 (text): elements expand across ALL matches — W1 (1 keyword) and W3 (2 keywords) each show
            // exactly their OWN indices, with none of the out-of-bounds noise the hand-written bracket paths caused.
            var sD2 = ReadTools.CrossPluginQuery(svc, type: "Weapon", references: null, editorid_contains: null,
                conflicts_only: false, plugins: null, defined_in: false, where: null, group_by: null,
                fields: new[] { "Keywords" }, depth: 2, conflict_tree: false, limit: 500, max_chars: 0);
            Check("depth=2 text: expanded elements appear (Keywords[0] everywhere, Keywords[1] on W3) with NO out-of-bounds noise",
                  sD2.Contains("Keywords[0]") && sD2.Contains("Keywords[1]") && !sD2.Contains("out of bounds", StringComparison.OrdinalIgnoreCase));
            Check("depth=2 text: the expanded leaf carries its round-trip FormKey token", sD2.Contains(kaFk.ToString()));

            // resolve_names composes with the expansion — the #231 use case ('Effects with identities in one scan').
            var sD2n = ReadTools.CrossPluginQuery(svc, type: "Weapon", references: null, editorid_contains: null,
                conflicts_only: false, plugins: null, defined_in: false, where: null, group_by: null,
                fields: new[] { "Keywords" }, depth: 2, conflict_tree: false, resolve_names: true, limit: 500, max_chars: 0);
            Check("depth=2 + resolve_names: expanded elements annotate → their target's editorid", sD2n.Contains("→ hcbpKwA"));

            // json parity: depth=2 emits the SAME expanded paths in the json document's field objects.
            var sD2j = ReadTools.CrossPluginQuery(svc, type: "Weapon", references: null, editorid_contains: null,
                conflicts_only: false, plugins: null, defined_in: false, where: null, group_by: null,
                fields: new[] { "Keywords" }, depth: 2, conflict_tree: false, limit: 500, max_chars: 0, format: "json");
            using (var doc = JsonDocument.Parse(sD2j))
            {
                bool found = doc.RootElement.GetProperty("matches").EnumerateArray().Any(m =>
                    m.TryGetProperty("fields", out var fs) && fs.EnumerateArray().Any(f => f.GetProperty("path").GetString() == "Keywords[0]"));
                Check("depth=2 json: matches carry the expanded field paths (Keywords[0])", found);
            }

            // refusals (Q3): depth>1 without fields= (summary lines have nothing to expand); depth>1 under
            // format=dense (positional cells align 1:1 with the requested paths).
            var sDNoF = ReadTools.CrossPluginQuery(svc, type: "Weapon", references: null, editorid_contains: null,
                conflicts_only: false, plugins: null, defined_in: false, where: null, group_by: null,
                fields: null, depth: 2, conflict_tree: false, limit: 500, max_chars: 0);
            Check("depth=2 WITHOUT fields= is REFUSED loud (never silently ignored)",
                  sDNoF.StartsWith("error:", StringComparison.OrdinalIgnoreCase) && sDNoF.Contains("fields=", StringComparison.OrdinalIgnoreCase));

            // conflict_tree=true without fields= is a WHOLE-RECORD dump (PR #244 review, MEDIUM): its containers
            // hint 'pass depth=2', so that exact call must WORK — read_record's no-fields depth semantics.
            var sDTree = ReadTools.CrossPluginQuery(svc, type: "Weapon", references: null, editorid_contains: null,
                conflicts_only: false, plugins: null, defined_in: false, where: null, group_by: null,
                fields: null, depth: 2, conflict_tree: true, limit: 500, max_chars: 0);
            Check("depth=2 + conflict_tree (no fields=) WORKS — the whole-record dump expands (its own hint led here)",
                  !sDTree.StartsWith("error:", StringComparison.OrdinalIgnoreCase) && sDTree.Contains("Keywords[0]"));

            // depth under group_by gets its OWN refusal (PR #244 review, LOW): the old fields-refusal advised
            // 'pass fields=' — a dead end group_by itself refuses. The message must name group_by, not that trap.
            var sDGroup = ReadTools.CrossPluginQuery(svc, type: "Weapon", references: null, editorid_contains: null,
                conflicts_only: false, plugins: null, defined_in: false, where: null, group_by: "winner",
                fields: null, depth: 2, conflict_tree: false, limit: 500, max_chars: 0);
            Check("depth=2 + group_by is REFUSED naming group_by's count table (never the 'pass fields=' dead end)",
                  sDGroup.StartsWith("error:", StringComparison.OrdinalIgnoreCase) && sDGroup.Contains("group_by", StringComparison.OrdinalIgnoreCase)
                  && sDGroup.Contains("count table", StringComparison.OrdinalIgnoreCase));
            var sDDense = ReadTools.CrossPluginQuery(svc, type: "Weapon", references: null, editorid_contains: null,
                conflicts_only: false, plugins: null, defined_in: false, where: null, group_by: null,
                fields: new[] { "Keywords" }, depth: 2, conflict_tree: false, limit: 500, max_chars: 0, format: "dense");
            Check("depth=2 + format=dense is REFUSED loud, naming the text/json hop",
                  sDDense.StartsWith("error:", StringComparison.OrdinalIgnoreCase) && sDDense.Contains("dense", StringComparison.OrdinalIgnoreCase)
                  && sDDense.Contains("json", StringComparison.OrdinalIgnoreCase));

            // dense at depth=1 still renders — its container cells hint the FORMAT hop with the knob, so the
            // "pass depth=2" advice never sends a dense caller into a blind refusal.
            var sDenseKw = ReadTools.CrossPluginQuery(svc, type: "Weapon", references: null, editorid_contains: null,
                conflicts_only: false, plugins: null, defined_in: false, where: null, group_by: null,
                fields: new[] { "Keywords" }, conflict_tree: false, limit: 500, max_chars: 0, format: "dense");
            Check("dense (depth=1) container cells carry the format-hop hint ('pass depth=2 with format=text/json')",
                  sDenseKw.Contains("pass depth=2 with format=text/json"));

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
