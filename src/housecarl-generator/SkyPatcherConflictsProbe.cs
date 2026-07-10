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

        var conflicts = SkyPatcherConflicts.Detect(folder, catalog, fieldMap);
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
        var reach = conflicts.FirstOrDefault(c => c.Field == "Data.Reach");
        failures += Check("a BROAD set collides with an explicit-target set (reach 1.5 vs 2.5)",
            reach is not null && reach.Entries.Count == 2 && reach.Winner.Value == "2.5", Dump());
        var speed = conflicts.FirstOrDefault(c => c.Field == "Data.Speed");
        failures += Check("an entry whose line carries EXTRA filters is flagged CONDITIONAL",
            speed is not null && speed.Conditional
            && speed.Entries.Any(e => e.Conditional) && speed.Entries.Any(e => !e.Conditional), Dump());
        failures += Check("different-target sets do NOT collide (no conflict lists 99)",
            conflicts.All(c => c.Entries.All(e => e.Value != "99")), Dump());

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
