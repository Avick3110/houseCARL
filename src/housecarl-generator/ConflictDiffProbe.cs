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
/// Extended for PR-G (HCBR-2026-06-15-01 item 4.3) — distinguish present-==-winner from absent:
///   I. a NULLABLE FORMLINK the contributor doesn't carry but the winner does → a FIRST-CLASS "ABSENT here"
///      state, not the pre-fix phantom "=(absent) (winner …)" value delta (RED before the fix);
///   J. the contributor RESTATES a field == winner (an ITM override) → no delta, but a positive AgreedCount
///      makes it distinguishable from a contributor that simply doesn't carry the field (RED before the fix);
///   K. the SYMMETRIC absent case — the contributor carries the link, the WINNER cleared it → the distinct
///      "<path>=<val> (winner has <path> ABSENT)" render (review finding #3).
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

        // ---- MASTER: four target factions (relation targets) + the subject factions, one per arm (A–D the
        //      list/scalar arms; I/J the nullable-formlink absent / ITM-restate arms added for PR-G). ----
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

        // A FormList for the nullable-formlink arms (I/J) to point at — a real link target so the
        // present-side renders a round-trippable FormKey, the absent-side a note sentinel.
        var fl = master.FormLists.AddNew(); fl.EditorID = "hcDiffCrimeList";

        var fI = master.Factions.AddNew(); fI.EditorID = "hcDiffAbsent";     // arm I: nullable formlink absent on the contributor, set on the winner
        // (SharedCrimeFactionList deliberately LEFT NULL on the master)
        var fJ = master.Factions.AddNew(); fJ.EditorID = "hcDiffITM";        // arm J: contributor restates the field == winner (an ITM override)
        fJ.SharedCrimeFactionList.SetTo(fl.FormKey);
        var fK = master.Factions.AddNew(); fK.EditorID = "hcDiffWinnerCleared"; // arm K: contributor CARRIES the link, the winner CLEARS it (symmetric absent)
        fK.SharedCrimeFactionList.SetTo(fl.FormKey);
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
        var oI = (IFaction)WriteEngine.GenericGetOrAddAsOverride(over, fI);
        oI.SharedCrimeFactionList.SetTo(fl.FormKey);                          // winner CARRIES the link the contributor (master) left null
        var oJ = (IFaction)WriteEngine.GenericGetOrAddAsOverride(over, fJ);
        oJ.SharedCrimeFactionList.SetTo(fl.FormKey);                          // winner == contributor (ITM restate)
        var oK = (IFaction)WriteEngine.GenericGetOrAddAsOverride(over, fK);
        oK.SharedCrimeFactionList.SetToNull();                                // winner CLEARS the link the contributor carries (symmetric absent)
        over.BeginWrite.ToPath(overPath).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

        Console.WriteLine($"-- synthesized {masterName} < {overName} (winner); subject factions, one comparison arm each --");
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

        // ---- I (PR-G, item 4.3): a NULLABLE FORMLINK the contributor (master) doesn't carry but the winner
        //      does. The empirically-confirmed render is "SharedCrimeFactionList: ABSENT here (winner has …)" —
        //      a FIRST-CLASS absent state, NOT the pre-fix phantom "=(absent) (winner …)" value delta. The null
        //      formlink reads through the read engine's "(absent)" sentinel; the symmetric "(null link)"
        //      sentinel (a present-but-FormKey.Null link) is handled identically by construction
        //      (IsAbsentSentinel covers both). ----
        var dI = DiffOf(svc, fI.FormKey);
        Check("I: an absent nullable formlink renders as a FIRST-CLASS ABSENT state, not a phantom value delta",
              dI.Complete
              && dI.Deltas.Any(d => d.StartsWith("SharedCrimeFactionList: ABSENT here (winner has ", StringComparison.Ordinal))
              && !dI.Deltas.Any(d => d.Contains("=(absent)", StringComparison.Ordinal)));
        Check("I: the ABSENT delta names the value the winner carries",
              dI.Deltas.Any(d => d.Contains(fl.FormKey.ToString(), StringComparison.Ordinal)));
        // The agreement signal is alive even alongside a delta: the shared scalar leaves (EditorID/Flags/…)
        // are restated identical, and the absent formlink is NOT miscounted as an agreement.
        Check("I: AgreedCount counts the restated VALUE leaves but NOT the absent field",
              dI.AgreedCount > 0 && !dI.AgreedSample.Contains("SharedCrimeFactionList"));

        // ---- J (PR-G, item 4.3): the contributor RESTATES the formlink == winner (an ITM override). No delta,
        //      but AgreedCount must be STRICTLY HIGHER than arm I's (it agrees on the formlink that arm I left
        //      absent) — the signal that distinguishes a deliberate same-as-winner override from a no-op. ----
        var dJ = DiffOf(svc, fJ.FormKey);
        Check("J: a present-==-winner (ITM) override carries NO delta",
              dJ.Complete && dJ.Deltas.Count == 0);
        Check("J: the ITM override is detectable — AgreedCount > arm-I (it restates the formlink arm I omits)",
              dJ.AgreedCount > dI.AgreedCount && dJ.AgreedSample.Count > 0);

        // (Review finding #1's node-neutral render WORDING — IdenticalWholeRecord/IdenticalAcrossFields in
        // ReadTools — is human-reviewed, not pinned here: a cross-assembly call into the mcp render helper
        // could not be compiled in this worktree (a build-server metadata-cache pathology, see the PR summary),
        // and the reviewer explicitly authorised skipping the render-string pin when it needs an awkward
        // harness. The data-layer signal the wording rests on — AgreedCount distinguishing an ITM restate from
        // a no-op — IS pinned by arm J above.)

        // ---- K (review finding #3): the SYMMETRIC absent branch — the contributor CARRIES the link, the WINNER
        //      cleared it. Distinct render string "<path>=<val> (winner has <path> ABSENT)", previously
        //      unexercised. The winner's null formlink reads through the "(absent)" sentinel. ----
        var dK = DiffOf(svc, fK.FormKey);
        Check("K: a field the winner CLEARED renders as the contributor's value + 'winner has … ABSENT'",
              dK.Complete
              && dK.Deltas.Any(d => d.StartsWith("SharedCrimeFactionList=", StringComparison.Ordinal)
                                 && d.Contains("(winner has SharedCrimeFactionList ABSENT)", StringComparison.Ordinal)
                                 && d.Contains(fl.FormKey.ToString(), StringComparison.Ordinal)));
        Check("K: the cleared-on-winner field is NOT rendered as a phantom value delta",
              !dK.Deltas.Any(d => d.Contains("(winner (absent))", StringComparison.Ordinal)
                               || d.Contains("(winner (null link))", StringComparison.Ordinal)));

        // ---- E: truncation honesty (in-memory; the expansion cap fires well below 900 relations). ----
        var big = new SkyrimMod(new ModKey("hcDiffBig", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var bigF = big.Factions.AddNew();
        for (int i = 0; i < 900; i++) bigF.Relations.Add(Rel(t[i % 4]));
        var bigRead = ReadEngine.ReadFields(bigF, new[] { "Relations" }, LoadOrderService.ConflictDiffDepth);
        var dE = FieldsDiff.Compare(bigRead, bigRead);
        Check("E: a capped read yields Complete=false (no false 'identical' claim)",
              dE is { Complete: false, Deltas.Count: 0 });

        // ---- E2: a TRUNCATED comparison must not FABRICATE deltas from where the cap fell (PR #28 review):
        //      one side cut mid-list ⇒ no list or one-sided deltas; a value mismatch observed on BOTH sides
        //      (read before either cap) still reports. ----
        var truncA = Fields(("Flags", "A", null), ("Relations", null, "[2 item(s)]"),
                            ("Relations[0]", null, "[Relation]"), ("Relations[0].Target", "AAAAAA:m.esm", null),
                            ("…", null, "(expansion truncated at 2000 lines — narrow with a field path or a lower depth)"));
        var fullB = Fields(("Flags", "B", null), ("Relations", null, "[2 item(s)]"),
                           ("Relations[0]", null, "[Relation]"), ("Relations[0].Target", "AAAAAA:m.esm", null),
                           ("Relations[1]", null, "[Relation]"), ("Relations[1].Target", "BBBBBB:m.esm", null));
        var dE2 = FieldsDiff.Compare(truncA, fullB);
        Check("E2: truncation fabricates NO list/one-sided deltas; both-sides mismatch kept",
              dE2 is { Complete: false, Deltas.Count: 1 } && dE2.Deltas[0].StartsWith("Flags=A", StringComparison.Ordinal));

        // ---- G: a numeric-KEYED dict (Package.Data) — the bracket is a semantic KEY: the same values bound
        //      to swapped keys is a REAL delta, and must not vanish into order-insensitive list handling
        //      (PR #28 review: pre-fix this compared "identical"). Classified by the engine's in-band
        //      "pair(s)" marker. ----
        var dictA = Fields(("Data", null, "[Dictionary`2: 2 pair(s)]"),
                           ("Data[0]", null, "[PackageDataBool]"), ("Data[0].Name", "TopicData", null),
                           ("Data[3]", null, "[PackageDataBool]"), ("Data[3].Name", "Repeatable", null));
        var dictB = Fields(("Data", null, "[Dictionary`2: 2 pair(s)]"),
                           ("Data[0]", null, "[PackageDataBool]"), ("Data[0].Name", "Repeatable", null),
                           ("Data[3]", null, "[PackageDataBool]"), ("Data[3].Name", "TopicData", null));
        Check("G: numeric-keyed dict key rebinding IS a delta (exact-path, not positional)",
              FieldsDiff.Compare(dictA, dictB) is { Complete: true, Deltas.Count: 2 });

        // ---- G2: the dict marker is REAL — the engine renders a numeric-keyed dict (Package.Data) as
        //      "pair(s)", the in-band signal arm G's classification rests on. ----
        var packMod = new SkyrimMod(new ModKey("hcDiffPack", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var pack = packMod.Packages.AddNew();
        pack.Data.Add(3, new PackageDataBool { Name = "hcDiffPackDatum" });
        var packRead = ReadEngine.ReadFields(pack, new[] { "Data" }, 4);
        Check("G2: a real numeric-keyed dict renders the 'pair(s)' marker",
              packRead.Fields.Any(fv => fv.Path == "Data" && (fv.Note ?? "").Contains(" pair(s)]", StringComparison.Ordinal)));

        // ---- E3: a root COUNT delta still surfaces under truncation — the root summary lines are real,
        //      cap-independent reads on both sides (review #2's recovery note). ----
        var truncC = Fields(("Relations", null, "[ExtendedList`1: 600 item(s)]"),
                            ("Relations[0]", null, "[Relation]"), ("Relations[0].Target", "AAAAAA:m.esm", null),
                            ("…", null, "(expansion truncated at 2000 lines — narrow with a field path or a lower depth)"));
        var truncD = Fields(("Relations", null, "[ExtendedList`1: 601 item(s)]"),
                            ("Relations[0]", null, "[Relation]"), ("Relations[0].Target", "AAAAAA:m.esm", null),
                            ("…", null, "(expansion truncated at 2000 lines — narrow with a field path or a lower depth)"));
        var dE3 = FieldsDiff.Compare(truncC, truncD);
        Check("E3: truncated comparison still reports a root COUNT delta",
              dE3 is { Complete: false, Deltas.Count: 1 } && dE3.Deltas[0].StartsWith("Relations=", StringComparison.Ordinal));

        // ---- H: a fields=-BRACKETED read (fields=["Data[3].Name"]) emits no root summary, so the dict
        //      marker is structurally absent — such roots must fall to EXACT-PATH comparison, not positional
        //      handling (review #2: a dict key swap compared "identical" through this hole). ----
        var brackA = Fields(("Data[0].Name", "TopicData", null), ("Data[3].Name", "Repeatable", null));
        var brackB = Fields(("Data[0].Name", "Repeatable", null), ("Data[3].Name", "TopicData", null));
        Check("H: bracketed fields= read without a root summary compares exact-path",
              FieldsDiff.Compare(brackA, brackB) is { Complete: true, Deltas.Count: 2 });

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
        var order = Mo2LoadOrder.Build(p.ProfileDir, p.ModsDir, p.DataDir, p.OverwriteDir);
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
