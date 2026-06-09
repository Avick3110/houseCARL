using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD (standing CI instrument) for HCBR-2026-06-09-01 — the conflict-tree winner-relative diff
/// reported "(identical to winner)" when lists differed in CONTENT but not COUNT. The old diff compared depth-1
/// rendered lines, where a list is just a "[List: N item(s)]" count token — an affirmative false ITM verdict
/// that masked a real override regression (the report's USSEP PlayerFaction case).
///
/// Self-contained: synthesizes ON DISK a master + an override of four factions exercising each comparison arm,
/// then drives the REAL product path — <see cref="LoadOrderService.ResolveTree"/> (now a DEEP read, via the
/// ForGuard seam) + <see cref="FieldsDiff.Compare"/> (the new content comparison) — and asserts:
///   A. equal-count, different-content Relations → a DELTA naming the element only in each side (RED before
///      the fix: depth-1 reads gave the comparator nothing but equal count tokens);
///   B. same contents merely REORDERED → NO delta (content-keyed comparison; an index-wise diff over-reports);
///   C. different counts → still a delta (the case the old diff did catch);
///   D. a scalar field delta → still reported (the old behavior, preserved at depth);
///   E. a read that hits the expansion cap → Complete=false (the render must NOT claim "identical"; Q3).
///
/// Run: <c>dotnet run --project src/housecarl-generator conflict-diff-guard</c>
/// </summary>
public static class ConflictDiffProbe
{
    static int _pass, _fail;

    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — conflict-tree content diff (HCBR-2026-06-09-01)  ################");
        Console.WriteLine();
        _pass = _fail = 0;

        var dir = Path.Combine(Path.GetTempPath(), "hc-conflictdiff-guard");
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        Directory.CreateDirectory(dir);

        const string masterName = "hcDiffMaster.esp", overName = "hcDiffOver.esp";
        var masterPath = Path.Combine(dir, masterName);
        var overPath = Path.Combine(dir, overName);

        // ---- MASTER: four target factions (relation targets) + four subject factions, one per arm. ----
        var master = new SkyrimMod(ModKey.FromNameAndExtension(masterName), SkyrimRelease.SkyrimSE);
        var t = new FormKey[4];
        for (int i = 0; i < 4; i++) { var tf = master.Factions.AddNew(); tf.EditorID = $"hcDiffTarget{i + 1}"; t[i] = tf.FormKey; }

        var fA = master.Factions.AddNew(); fA.EditorID = "hcDiffContent";    // arm A: equal count, contents differ
        fA.Relations.AddRange(new[] { Rel(t[0]), Rel(t[1]), Rel(t[2]) });
        var fB = master.Factions.AddNew(); fB.EditorID = "hcDiffReorder";    // arm B: same contents, reordered
        fB.Relations.AddRange(new[] { Rel(t[0]), Rel(t[1]), Rel(t[2]) });
        var fC = master.Factions.AddNew(); fC.EditorID = "hcDiffCount";      // arm C: counts differ
        fC.Relations.AddRange(new[] { Rel(t[0]), Rel(t[1]) });
        var fD = master.Factions.AddNew(); fD.EditorID = "hcDiffScalar";     // arm D: scalar delta
        fD.Flags = Faction.FactionFlag.HiddenFromPC;
        master.BeginWrite.ToPath(masterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        // ---- OVERRIDE (the winner): the four subjects re-shaped per arm. ----
        var over = new SkyrimMod(ModKey.FromNameAndExtension(overName), SkyrimRelease.SkyrimSE);
        var oA = (IFaction)WriteEngine.GenericGetOrAddAsOverride(over, fA);
        oA.Relations.Clear();
        oA.Relations.AddRange(new[] { Rel(t[3]), Rel(t[0]), Rel(t[1]) });    // 3 items: t3 dropped, t4 added, shuffled
        var oB = (IFaction)WriteEngine.GenericGetOrAddAsOverride(over, fB);
        oB.Relations.Clear();
        oB.Relations.AddRange(new[] { Rel(t[2]), Rel(t[0]), Rel(t[1]) });    // same 3, different order
        var oC = (IFaction)WriteEngine.GenericGetOrAddAsOverride(over, fC);
        oC.Relations.Add(Rel(t[2]));                                          // 2 -> 3 items
        var oD = (IFaction)WriteEngine.GenericGetOrAddAsOverride(over, fD);
        oD.Flags = Faction.FactionFlag.SpecialCombat;                         // scalar enum delta
        over.BeginWrite.ToPath(overPath).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

        Console.WriteLine($"-- synthesized {masterName} < {overName} (winner); four factions, one comparison arm each --");
        Console.WriteLine();

        // ---- Drive the PRODUCT path: service ResolveTree (deep) + FieldsDiff per node-vs-winner. ----
        using var resolver = LoadOrderResolver.Build(new[] { masterPath, overPath });
        var svc = LoadOrderService.ForGuard(resolver, new UserConfigStore(Path.Combine(dir, "houseCARL.user.json")));

        var dA = DiffOf(svc, fA.FormKey);
        Check("A: equal-count content delta IS reported (the masked case)",
              dA.Deltas.Any(d => d.StartsWith("Relations:", StringComparison.Ordinal) && d.Contains("contents differ")));
        Check("A: the delta names the master-only element (t3)",
              dA.Deltas.Any(d => d.Contains(t[2].ToString())));
        Check("A: the delta names the winner-only element (t4)",
              dA.Deltas.Any(d => d.Contains(t[3].ToString())));

        var dB = DiffOf(svc, fB.FormKey);
        Check("B: reordered-only list reports NO delta (content-keyed, order-insensitive)",
              dB.Deltas.Count == 0 && dB.Complete);

        var dC = DiffOf(svc, fC.FormKey);
        Check("C: count delta still reported",
              dC.Deltas.Any(d => d.StartsWith("Relations: 2 vs winner 3", StringComparison.Ordinal)));

        var dD = DiffOf(svc, fD.FormKey);
        Check("D: scalar delta still reported",
              dD.Deltas.Any(d => d.StartsWith("Flags=", StringComparison.Ordinal) && d.Contains("winner")));

        // ---- E: truncation honesty (in-memory; the expansion cap fires well below 900 relations). ----
        var big = new SkyrimMod(new ModKey("hcDiffBig", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var bigF = big.Factions.AddNew();
        for (int i = 0; i < 900; i++) bigF.Relations.Add(Rel(t[i % 4]));
        var bigRead = ReadEngine.ReadFields(bigF, new[] { "Relations" }, LoadOrderService.ConflictDiffDepth);
        var dE = FieldsDiff.Compare(bigRead, bigRead);
        Check("E: a capped read yields Complete=false (no false 'identical' claim)",
              dE is { Complete: false, Deltas.Count: 0 });

        // ---- F: FormKey tokens differing only by hex/master-name CASE are the SAME content (ModKeys are
        //      case-insensitive; each plugin stores a master's filename as written in ITS OWN master list —
        //      seen live as ccBGSSSE001-Fish.esm vs ccbgssse001-fish.esm on the report's PlayerFaction). ----
        var caseA = Fields(("Relations", null, "[3 item(s)]"), ("Relations[0]", null, "[Relation]"),
                           ("Relations[0].Target", "17DDC4:ccBGSSSE001-Fish.esm", null));
        var caseB = Fields(("Relations", null, "[3 item(s)]"), ("Relations[0]", null, "[Relation]"),
                           ("Relations[0].Target", "17ddc4:ccbgssse001-fish.esm", null));
        Check("F: FormKey case drift is NOT a content delta",
              FieldsDiff.Compare(caseA, caseB) is { Complete: true, Deltas.Count: 0 });

        Console.WriteLine();
        Console.WriteLine($"=== conflict-diff-guard: {_pass} pass / {_fail} fail — {(_fail == 0 ? "PASS" : "FAIL")} ===");
        return _fail == 0 ? 0 : 1;
    }

    static FieldsDiff.Result DiffOf(LoadOrderService svc, FormKey fk)
    {
        var tree = svc.ResolveTree(fk, null) ?? throw new InvalidOperationException($"no conflict tree for {fk}");
        if (tree.Nodes.Count != 2) throw new InvalidOperationException($"expected 2 touching plugins for {fk}, got {tree.Nodes.Count}");
        return FieldsDiff.Compare(tree.Nodes[0].Record, tree.Winner.Record);   // master node vs the override (winner)
    }

    static Relation Rel(FormKey target)
    {
        var rel = new Relation { Reaction = CombatReaction.Ally };
        rel.Target.SetTo(target);
        return rel;
    }

    static RecordFields Fields(params (string path, string? token, string? note)[] lines) =>
        new("Faction", "000000:hcDiffGuard.esp", "hcDiffCase",
            lines.Select(l => new FieldValue(l.path, l.token is not null, l.token, l.note)).ToList());

    static void Check(string what, bool ok)
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"   {what,-72}: {(ok ? "PASS" : "FAIL")}");
    }

    /// <summary>REAL-DATA proof (manual; needs an MO2 instance with the Ashe plugins): the report's exact repro —
    /// the whole-record conflict diff of <c>E495A3:Ashe - Fire and Blood.esp</c> (MM_RelentlessFury, SPEL), whose
    /// origin and winning patch both carry exactly 1 effect but with DIFFERENT BaseEffect. The old diff said
    /// "(identical to winner)"; the content diff must report the Effects delta. Also prints the PlayerFaction
    /// (000DB1:Skyrim.esm) tree diff — the masked-regression record — for eyes-on confirmation.
    /// Run: <c>dotnet run --project src/housecarl-generator conflict-diff-proof -- --mo2 &lt;instanceDir&gt;</c></summary>
    public static int RunProof(string[] args)
    {
        var f = WriteEngine.ParseFlags(args);
        var instanceDir = f.GetValueOrDefault("mo2");
        if (instanceDir is null || !Directory.Exists(instanceDir)) { Console.WriteLine("SKIP: needs --mo2 <instanceDir>"); return 0; }

        Console.WriteLine($"################  REAL-DATA PROOF — conflict-tree content diff on {Path.GetFileName(instanceDir)}  ################");
        Console.WriteLine();
        var p = Mo2Instance.Resolve(instanceDir);
        var order = Mo2LoadOrder.Build(p.ProfileDir, p.ModsDir, p.DataDir);
        using var resolver = LoadOrderResolver.Build(order.OrderedPaths.ToList());
        Console.WriteLine($"   resolver: {resolver.PluginCount} plugins, {resolver.RecordCount:N0} records");
        var svc = LoadOrderService.ForGuard(resolver, new UserConfigStore(Path.Combine(Path.GetTempPath(), "hc-conflictdiff-proof.user.json")));

        bool pass = true;
        foreach (var subject in new[] { "E495A3:Ashe - Fire and Blood.esp", "000DB1:Skyrim.esm" })
        {
            Console.WriteLine();
            Console.WriteLine($"-- {subject} --");
            FormKey fk;
            try { fk = FormKey.Factory(subject); } catch { Console.WriteLine("   (bad FormKey)"); continue; }
            var tree = svc.ResolveTree(fk, null);
            if (tree is null || tree.Nodes.Count < 2) { Console.WriteLine("   (not in this order, or only one plugin touches it — skipped)"); continue; }
            var winner = tree.Winner;
            Console.WriteLine($"   {tree.Nodes.Count} plugins touch it; winner = {winner.Plugin}");
            for (int n = 0; n < tree.Nodes.Count - 1; n++)
            {
                var diff = FieldsDiff.Compare(tree.Nodes[n].Record, winner.Record);
                var line = diff.Deltas.Count > 0 ? string.Join("; ", diff.Deltas)
                         : diff.Complete ? "(identical to winner — full modeled content compared, list order ignored)"
                                         : "(comparison truncated — not a verified ITM)";
                Console.WriteLine($"   {tree.Nodes[n].Plugin}: {line}");
            }
            if (subject.StartsWith("E495A3", StringComparison.Ordinal))
            {
                var fnb = tree.Nodes.Take(tree.Nodes.Count - 1).FirstOrDefault(x => x.Plugin.StartsWith("Ashe - Fire and Blood", StringComparison.OrdinalIgnoreCase));
                bool reported = fnb is not null
                    && FieldsDiff.Compare(fnb.Record, winner.Record).Deltas.Any(d => d.StartsWith("Effects:", StringComparison.Ordinal) && d.Contains("0E40BF"));
                Console.WriteLine($"   ASSERT Effects content delta reported for Ashe - Fire and Blood.esp: {(reported ? "PASS" : "FAIL")}");
                pass &= reported;
            }
        }
        Console.WriteLine();
        Console.WriteLine($"=== conflict-diff-proof: {(pass ? "PASS" : "FAIL")} ===");
        return pass ? 0 : 1;
    }
}
