using HousecarlCore;

namespace HousecarlGenerator;

// ======================================================================
//  SkyPatcherDiscoveryProbe — CI guard for Wave-1 INI discovery
//  (SkyPatcherDiscovery; plan dev/plans/SKYPATCHER_DISTRIBUTOR_TOOL_
//  PLAN_2026-07-08.md §5.1–5.2). Self-contained synthetic mod folders
//  through the REAL AssetResolver (the same build the service uses).
//
//  RED-PROOFS (plan §7 — the two places a wrong model silently corrupts
//  the answer):
//  • UNION, not filename-winner — two mods shipping DIFFERENTLY-named
//    INIs both contribute (a winner-only model would drop one).
//  • Same-name collision — two mods shipping the IDENTICAL relative path
//    contribute the WINNER's content only, with the loser NAMED as
//    shadowed (a union-of-everything model would double-apply).
//  • LOOSE-ONLY — an INI present only inside a BSA is listed as
//    NotApplied and excluded from the ordered lines (existence-gated on
//    a committed fixture BSA; SKIPs cleanly when absent).
//  Plus: apply-order path sort (nested organisation folders), the
//  Plugin.esp.ini filename gate both ways, the SkyPatcher.ini [Patcher]
//  per-type toggle, stray root-level INIs, undocumented subfolders.
// ======================================================================
public static class SkyPatcherDiscoveryProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("[skypatcher-discovery-guard] SkyPatcher INI discovery (Wave 1)");
        int failures = 0;
        var catalog = SkyPatcherCatalog.Load();

        var root = Path.Combine(Path.GetTempPath(), "hc-skypatcher-disc-" + Guid.NewGuid().ToString("N"));
        try
        {
            var overwrite = Path.Combine(root, "overwrite");
            var mods = Path.Combine(root, "mods");
            var data = Path.Combine(root, "Data");
            var modA = Path.Combine(mods, "ModA");         // higher MO2 priority
            var modB = Path.Combine(mods, "ModB");
            foreach (var d in new[] { overwrite, modA, modB, data }) Directory.CreateDirectory(d);
            var enabled = new[] { "ModA", "ModB" };

            void Write(string baseDir, string rel, string text)
            {
                var p = Path.Combine(baseDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllText(p, text);
            }

            const string W = @"SKSE\Plugins\SkyPatcher\weapon";
            Write(modA, $@"{W}\a.ini", "attackDamage=1");
            Write(modB, $@"{W}\b.ini", "attackDamage=2");                       // union: differently-named INIs BOTH apply
            Write(modA, $@"{W}\same.ini", "attackDamage=3");                    // same-path collision:
            Write(modB, $@"{W}\same.ini", "attackDamage=999");                  //   ModA wins, ModB shadowed
            Write(modA, $@"{W}\zzz.ini", "attackDamage=4");
            Write(modA, $@"{W}\org\bbb.ini", "attackDamage=5");                 // nested organisation folder
            Write(modA, $@"{W}\Gated.esp.ini", "attackDamage=6");               // filename gate — plugin present
            Write(modA, $@"{W}\Absent.esp.ini", "attackDamage=7");              // filename gate — plugin missing
            Write(modA, @"SKSE\Plugins\SkyPatcher\stray.ini", "attackDamage=8");        // root-level stray
            Write(modA, @"SKSE\Plugins\SkyPatcher\bogusType\x.ini", "foo=1");           // undocumented subfolder
            Write(modA, @"SKSE\Plugins\SkyPatcher\npc\n.ini", "setEssential=true");     // toggled OFF below
            Write(modA, @"SKSE\Plugins\SkyPatcher.ini", "[Patcher]\niEnableNpcPatching=0\niEnableWeaponPatching=1\n[Log]\niEnablelog=0\n");

            bool PluginPresent(string p) => p.Equals("Gated.esp", StringComparison.OrdinalIgnoreCase);

            using var r = AssetResolver.Build(overwrite, mods, data, enabled, Array.Empty<ActiveArchive>());
            var scan = SkyPatcherDiscovery.Scan(r.Capture(), catalog, PluginPresent);

            var weapon = scan.Folders.FirstOrDefault(f => f.Subfolder.Equals("weapon", StringComparison.OrdinalIgnoreCase));
            failures += Check("weapon folder discovered + catalog attached + enabled",
                weapon is { Catalog: not null, PatchingEnabled: true });
            if (weapon is null) return Done(failures);

            // ---- RED: union, not filename-winner ----
            var applied = weapon.Files.Where(f => f.NotApplied is null).Select(f => f.SortKey).ToList();
            failures += Check("differently-named INIs from DIFFERENT mods BOTH apply (union, not winner)",
                applied.Contains("a.ini") && applied.Contains("b.ini"), string.Join(",", applied));

            // ---- RED: same-path collision → winner's content once, loser named ----
            var same = weapon.Files.Single(f => f.SortKey == "same.ini");
            failures += Check("same-path collision: winner is ModA, ModB named as shadowed",
                same.WinningProvider == "ModA" && same.ShadowedProviders.Contains("ModB"),
                $"{same.WinningProvider} / [{string.Join(",", same.ShadowedProviders)}]");
            failures += Check("…the winner's CONTENT is what got parsed (3, not 999)",
                same.Lines.Any(l => l.Segments.Any(s => s.RawValue == "3"))
                && !same.Lines.Any(l => l.Segments.Any(s => s.RawValue == "999")));
            failures += Check("…and the collision is a layer note",
                scan.Notes.Any(n => n.Contains("same.ini") && n.Contains("SHADOWED")));

            // ---- apply order: path sort within the folder, nested dirs deterministic ----
            var order = weapon.Files.Select(f => f.SortKey).ToList();
            failures += Check("files sort 0→z by folder-relative path (a, Absent…, b, Gated…, org\\bbb, same, zzz)",
                order.SequenceEqual(new[] { "a.ini", "Absent.esp.ini", "b.ini", "Gated.esp.ini", @"org\bbb.ini", "same.ini", "zzz.ini" }),
                string.Join(" | ", order));

            // ---- the Plugin.esp.ini filename gate, both ways ----
            failures += Check("Gated.esp.ini applies (its plugin is active)",
                weapon.Files.Single(f => f.SortKey == "Gated.esp.ini") is { NotApplied: null, GatePlugin: "Gated.esp" });
            var absent = weapon.Files.Single(f => f.SortKey == "Absent.esp.ini");
            failures += Check("Absent.esp.ini is NotApplied, reason names the gate",
                absent.NotApplied is { } na && na.Contains("Absent.esp"), absent.NotApplied ?? "<applied>");
            failures += Check("…but its content is still inspectable (parsed lines kept)",
                absent.Lines.Any(l => l.Segments.Any(s => s.Key == "attackDamage")));

            // ---- OrderedLines: only game-visible content, 1-based line numbers ----
            var lines = SkyPatcherDiscovery.OrderedLines(weapon);
            failures += Check("ordered lines exclude gated-off files and keep file order",
                lines.All(l => !l.File.EndsWith("Absent.esp.ini", StringComparison.OrdinalIgnoreCase))
                && lines.First().File.EndsWith("a.ini", StringComparison.OrdinalIgnoreCase)
                && lines.First().LineNumber == 1);

            // ---- SkyPatcher.ini toggle: npc folder present but disabled ----
            var npc = scan.Folders.FirstOrDefault(f => f.Subfolder.Equals("npc", StringComparison.OrdinalIgnoreCase));
            failures += Check("SkyPatcher.ini [Patcher] toggle disables the npc folder",
                npc is { PatchingEnabled: false } && scan.Notes.Any(n => n.Contains("disables 'npc'")));
            failures += Check("…and a disabled folder contributes NO ordered lines",
                npc is not null && SkyPatcherDiscovery.OrderedLines(npc).Count == 0);

            // ---- stray root INI + undocumented subfolder are loud, never silently interpreted ----
            failures += Check("a root-level stray INI is excluded with a note",
                scan.Notes.Any(n => n.Contains("stray.ini") && n.Contains("NOT applied"))
                && scan.Folders.All(f => f.Files.All(x => !x.RelPath.EndsWith("stray.ini", StringComparison.OrdinalIgnoreCase))));
            var bogus = scan.Folders.FirstOrDefault(f => f.Subfolder.Equals("bogusType", StringComparison.OrdinalIgnoreCase));
            failures += Check("an undocumented subfolder is listed with Catalog=null + a note",
                bogus is { Catalog: null } && scan.Notes.Any(n => n.Contains("bogusType")));

            // ---- LOOSE-ONLY (existence-gated committed fixture): an INI only inside a BSA is NotApplied ----
            var fixBsa = Path.GetFullPath(@"src/housecarl-generator/fixtures/skypatcher/IniFixture.bsa");
            if (!File.Exists(fixBsa))
                Console.WriteLine("  SKIP  loose-only BSA arm — fixtures/skypatcher/IniFixture.bsa not present (author with BSArch; see AssetResolverProbe fixtures)");
            else
            {
                var archives = new[] { new ActiveArchive(fixBsa, "PluginA.esp", PluginRank: 1) };
                using var rb = AssetResolver.Build(overwrite, mods, data, enabled, archives);
                var scanB = SkyPatcherDiscovery.Scan(rb.Capture(), catalog, PluginPresent);
                var weaponB = scanB.Folders.First(f => f.Subfolder.Equals("weapon", StringComparison.OrdinalIgnoreCase));
                var bsaOnly = weaponB.Files.FirstOrDefault(f => f.SortKey.Equals("bsaonly.ini", StringComparison.OrdinalIgnoreCase));
                failures += Check("an INI present ONLY inside a BSA is NotApplied (loose-only)",
                    bsaOnly is { NotApplied: { } why } && why.Contains("BSA"), bsaOnly?.NotApplied ?? "<missing or applied>");
                failures += Check("…and it contributes NO ordered lines",
                    SkyPatcherDiscovery.OrderedLines(weaponB).All(l => !l.File.EndsWith("bsaonly.ini", StringComparison.OrdinalIgnoreCase)));
            }

            return Done(failures);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    static int Check(string label, bool ok, string? detail = null)
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}{(ok || detail is null ? "" : $"  [{detail}]")}");
        return ok ? 0 : 1;
    }

    static int Done(int failures)
    {
        Console.WriteLine(failures == 0
            ? "[skypatcher-discovery-guard] PASS — the loose-only ordered union holds."
            : $"[skypatcher-discovery-guard] FAIL — {failures} case(s) regressed.");
        return failures == 0 ? 0 : 1;
    }
}
