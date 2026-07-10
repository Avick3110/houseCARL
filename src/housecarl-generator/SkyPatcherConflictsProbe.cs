using HousecarlCore;

namespace HousecarlGenerator;

// ======================================================================
//  SkyPatcherConflictsProbe — CI guard for the Wave-2 INI-vs-INI conflict
//  detector (SkyPatcherConflicts; plan §3.2.4, report-only per §8).
//
//  RED-proofs the classification lines a wrong model silently corrupts:
//  a SET-vs-SET same-target collision IS a conflict (winner = the later
//  file in apply order); accumulating ops are NOT; same-value sets are
//  NOT; a not-applied file's lines do NOT participate; a broad line
//  collides with an explicit target; extra filters flag CONDITIONAL.
//
//  Also the intra-file dead-write (ITM-class) half: a write is dead ONLY
//  when a later line of the same file unconditionally re-covers EVERY
//  target (same value included — deadness doesn't depend on value).
//  RED-proofs the two review-confirmed kill-rule bugs: a multi-target
//  line partially overwritten is NOT dead (it still carries the other
//  target's write), and a CONDITIONAL overwriter kills nothing (it may
//  not fire) — while a conditional EARLIER write killed unconditionally
//  IS dead. Explicit-then-broad kills; broad-then-explicit kills
//  nothing; accumulating / gated / cross-file writes never ITM.
// ======================================================================
public static class SkyPatcherConflictsProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("[skypatcher-conflicts-guard] SkyPatcher INI-vs-INI conflict detector (Wave 2)");
        int failures = 0;

        var catalog = SkyPatcherCatalog.Load();
        var fieldMap = SkyPatcherFieldMap.Load();
        var weapCat = catalog.ForSubfolder("weapon")!;

        SkyPatcherDiscovery.IniFile Ini(string name, params string[] lines) => new(
            RelPath: $"SKSE\\Plugins\\SkyPatcher\\weapon\\{name}", Subfolder: "weapon", SortKey: name,
            WinningProvider: "TestMod", ShadowedProviders: Array.Empty<string>(), GatePlugin: null,
            NotApplied: null, Lines: lines.Select(SkyPatcherParse.ParseLine).ToList());

        const string target = "Skyrim.esm|12EB7";          // 012EB7:Skyrim.esm
        const string other = "Skyrim.esm|13790";

        var folder = new SkyPatcherDiscovery.FolderScan("weapon", weapCat, PatchingEnabled: true, Files: new[]
        {
            Ini("a.ini",
                $"filterByWeapons={target}:attackDamage=40:weight=5",             // damage + weight sets
                $"filterByWeapons={target}:keywordsToAdd=Some.esp|100"),          // accumulating — never a conflict
            Ini("m.ini",
                $"filterByWeapons={other}:attackDamage=99",                        // DIFFERENT target — no collision
                $"filterByWeapons={target}:weight=5",                              // SAME value — no collision
                $"filterByWeapons={target}:filterByKeywords=Some.esp|200:speed=2"),// conditional set
            Ini("z.ini",
                $"filterByWeapons={target}:attackDamage=60",                       // the damage collision (z wins)
                "reach=1.5",                                                        // BROAD set
                $"filterByWeapons={target}:keywordsToAdd=Some.esp|101",
                $"filterByWeapons={target}:speed=9"),                              // collides with m's conditional set
            Ini("gated.ini", $"filterByWeapons={target}:attackDamage=1") with { NotApplied = "filename-gated off" },
            Ini("y.ini", $"filterByWeapons={target}:reach=2.5"),                   // explicit vs z's BROAD reach
        });

        var report = SkyPatcherConflicts.Detect(folder, catalog, fieldMap);
        var conflicts = report.Conflicts;
        string Dump() => string.Join(" ; ", conflicts.Select(c => $"{c.Field}@{c.Target}:{string.Join("|", c.Entries.Select(e => $"{e.File}={e.Value}"))}"));

        var dmg = conflicts.FirstOrDefault(c => c.Field == "BasicStats.Damage");
        failures += Check("SET-vs-SET same-target collision detected (attackDamage a vs z)",
            dmg is not null && dmg.Entries.Count == 2, Dump());
        failures += Check("winner = the LATER file in apply order (z.ini, 60)",
            dmg?.Winner.File == "SKSE\\Plugins\\SkyPatcher\\weapon\\z.ini" && dmg?.Winner.Value == "60", Dump());
        failures += Check("the not-applied (gated) file's set did NOT participate",
            dmg is not null && dmg.Entries.All(e => !e.File.Contains("gated")), Dump());
        failures += Check("accumulating op (keywordsToAdd) is NOT a conflict",
            conflicts.All(c => c.Field != "Keywords"), Dump());
        failures += Check("same-value sets are NOT a conflict (weight 5 vs 5)",
            conflicts.All(c => c.Field != "BasicStats.Weight"), Dump());
        var dup = report.Duplicates.FirstOrDefault(x => x.Field == "BasicStats.Weight");
        failures += Check("same-value sets across files ARE a cross-INI DUPLICATE (weight 5 vs 5, a.ini + m.ini)",
            dup is not null && dup.Entries.Count == 2
            && dup.Entries.Select(e => Path.GetFileName(e.File)).SequenceEqual(new[] { "a.ini", "m.ini" }),
            string.Join(" ; ", report.Duplicates.Select(x => $"{x.Field}@{x.Target}")));
        failures += Check("a value-MIXED group stays a conflict only, never double-reported as a duplicate",
            report.Duplicates.All(x => x.Field != "BasicStats.Damage" && x.Field != "Data.Reach" && x.Field != "Data.Speed"),
            string.Join(" ; ", report.Duplicates.Select(x => $"{x.Field}@{x.Target}")));
        var reach = conflicts.FirstOrDefault(c => c.Field == "Data.Reach");
        failures += Check("a BROAD set collides with an explicit-target set (reach 1.5 vs 2.5)",
            reach is not null && reach.Entries.Count == 2 && reach.Winner.Value == "2.5", Dump());
        var speed = conflicts.FirstOrDefault(c => c.Field == "Data.Speed");
        failures += Check("an entry whose line carries EXTRA filters is flagged CONDITIONAL",
            speed is not null && speed.Conditional
            && speed.Entries.Any(e => e.Conditional) && speed.Entries.Any(e => !e.Conditional), Dump());
        failures += Check("different-target sets do NOT collide (no conflict lists 99)",
            conflicts.All(c => c.Entries.All(e => e.Value != "99")), Dump());

        // ---- the intra-file dead-write (ITM-class) half ----
        failures += Check("cross-file-only writes are NOT ITMs (the conflict fixture yields zero)",
            report.Itms.Count == 0, string.Join(" ; ", report.Itms.Select(m => $"{m.Field}:{m.File}")));

        var itmFolder = new SkyPatcherDiscovery.FolderScan("weapon", weapCat, PatchingEnabled: true, Files: new[]
        {
            Ini("g.ini",
                "attackDamage=1",                                                   // BROAD, overwritten by...
                "attackDamage=2"),                                                  // ...this later BROAD — the earlier is dead
            Ini("gated2.ini",
                $"filterByWeapons={target}:weight=1",
                $"filterByWeapons={target}:weight=2") with { NotApplied = "filename-gated off" },
            Ini("i.ini",
                $"filterByWeapons={target}:attackDamage=40",                        // dead — even though...
                $"filterByWeapons={target}:attackDamage=40",                        // ...the value is IDENTICAL (the purest ITM)
                $"filterByWeapons={target}:weight=5",                               // dead — a later BROAD covers it
                "weight=9",
                "reach=1.0",                                                        // BROAD then explicit: broad stays live elsewhere — NOT dead
                $"filterByWeapons={target}:reach=2.0",
                $"filterByWeapons={target}:keywordsToAdd=Some.esp|100",             // accumulating twice — never an ITM
                $"filterByWeapons={target}:keywordsToAdd=Some.esp|101"),
            Ini("multi.ini",
                $"filterByWeapons={target},{other}:attackDamage=40",                // NOT dead — the later line covers only ONE of its targets
                $"filterByWeapons={target}:attackDamage=60",
                $"filterByWeapons={target},{other}:weight=1",                       // dead ONCE — the broad re-covers BOTH targets
                "weight=2"),
            Ini("cond.ini",
                $"filterByWeapons={target}:speed=7",                                // NOT dead — the overwriter is CONDITIONAL (may not fire)
                $"filterByWeapons={target}:filterByKeywords=Some.esp|200:speed=9",
                $"filterByWeapons={target}:filterByKeywords=Some.esp|200:reach=1.0",// dead — conditional itself, but killed UNCONDITIONALLY
                $"filterByWeapons={target}:reach=2.0"),
        });
        var itms = SkyPatcherConflicts.Detect(itmFolder, catalog, fieldMap).Itms;
        string DumpItms() => string.Join(" ; ", itms.Select(m => $"{Path.GetFileName(m.File)}:{m.Field}:{string.Join("|", m.Entries.Select(e => $":{e.Line}={e.Value}→kill:{string.Join("+", e.KillerLines)}"))}"));

        var dmgItm = itms.FirstOrDefault(m => m.Field == "BasicStats.Damage" && m.File.Contains("i.ini"));
        failures += Check("same field/target written twice in ONE file IS a dead write — same value included, killer named",
            dmgItm is not null && dmgItm.Entries is [{ Line: 1, Value: "40", KillerLines: [2] }], DumpItms());
        var wItm = itms.FirstOrDefault(m => m.Field == "BasicStats.Weight" && m.File.Contains("i.ini"));
        failures += Check("explicit-target write killed by a later same-file BROAD write is dead",
            wItm is not null && wItm.Entries is [{ Line: 3, KillerLines: [4] }], DumpItms());
        failures += Check("BROAD-then-explicit kills nothing (broad stays live for other records) — no i.ini reach ITM",
            itms.All(m => m.Field != "Data.Reach" || !m.File.Contains("i.ini")), DumpItms());
        failures += Check("accumulating op (keywordsToAdd) twice is NOT an ITM",
            itms.All(m => m.Field != "Keywords"), DumpItms());
        var bItm = itms.FirstOrDefault(m => m.File.Contains("g.ini"));
        failures += Check("BROAD-vs-BROAD in one file IS a dead write (earlier broad dead)",
            bItm is not null && bItm.Field == "BasicStats.Damage" && bItm.Entries is [{ Line: 1, KillerLines: [2] }], DumpItms());
        failures += Check("a not-applied (gated) file's duplicates do NOT ITM",
            itms.All(m => !m.File.Contains("gated2")), DumpItms());
        // The two review-confirmed kill-rule bugs stay RED-proofed:
        failures += Check("a MULTI-TARGET write partially overwritten is NOT dead (still live for the other target)",
            itms.All(m => m.Field != "BasicStats.Damage" || !m.File.Contains("multi.ini")), DumpItms());
        var mwItm = itms.FirstOrDefault(m => m.Field == "BasicStats.Weight" && m.File.Contains("multi.ini"));
        failures += Check("a MULTI-TARGET write fully re-covered is dead — reported ONCE (per write, not per token)",
            mwItm is not null && mwItm.Entries is [{ Line: 3, KillerLines: [4] }], DumpItms());
        failures += Check("a CONDITIONAL overwriter kills nothing (it may not fire) — no cond.ini speed ITM",
            itms.All(m => m.Field != "Data.Speed"), DumpItms());
        var crItm = itms.FirstOrDefault(m => m.Field == "Data.Reach" && m.File.Contains("cond.ini"));
        failures += Check("a conditional EARLIER write killed unconditionally IS dead (flagged informational)",
            crItm is not null && crItm.Entries is [{ Line: 3, Conditional: true, KillerLines: [4] }], DumpItms());

        // ---- the set/accumulate PARTITION is exhaustive: a new SkyPatcherOpSemantic member cannot
        //      silently default to "accumulating" and make the detector under-report (review fold). ----
        var unclassified = Enum.GetValues<SkyPatcherOpSemantic>()
            .Where(s => !SkyPatcherConflicts.SetClassSemantics.Contains(s) && !SkyPatcherConflicts.AccumulatingSemantics.Contains(s))
            .ToList();
        failures += Check("every op semantic is classified set-class OR accumulating (none silently default)",
            unclassified.Count == 0, string.Join(", ", unclassified));
        failures += Check("the set/accumulate sets are disjoint",
            !SkyPatcherConflicts.SetClassSemantics.Intersect(SkyPatcherConflicts.AccumulatingSemantics).Any());

        Console.WriteLine(failures == 0
            ? "[skypatcher-conflicts-guard] PASS — set-collision classification holds."
            : $"[skypatcher-conflicts-guard] FAIL — {failures} case(s).");
        return failures == 0 ? 0 : 1;
    }

    static int Check(string label, bool ok, string? detail = null)
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}{(ok || detail is null ? "" : $"  [{detail}]")}");
        return ok ? 0 : 1;
    }
}
