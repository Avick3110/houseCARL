using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// asset-status guard (facegen-diagnostics Phase 2 — housecarl_asset_status). Proves the layer that turns the MO2
/// profile into a live asset answer: ArchiveDiscovery (which BSAs load, bound to their owning plugin + rank) and the
/// LoadOrderService wiring that wraps AssetResolver into the tool response, kept fresh + decoupled from the heavy
/// record index.
///
/// DISCOVERY arms (ArchiveDiscovery directly, self-contained synthetic folders + committed .bsa fixtures, NO BSArch):
///   A  co-name + Textures + base + RANK — X.bsa AND "X - Textures.bsa" bind to X.esp at one rank; Skyrim.ini base
///      archives load BELOW every plugin archive; a later plugin outranks an earlier one. (rank-direction teeth)
///   B  archive VFS winner — the SAME .bsa filename in two mods resolves to the HIGHER-priority mod's physical copy.
///   C  missing Skyrim.ini — no base list found → a LOUD warning (Q3) and no base archive, never a silent gap.
///
/// SERVICE arms (the real LoadOrderService over a synthetic MO2 instance, FreshnessCaptureProbe style):
///   D  tool response — a BSA-packed facegen path resolves to the right winner; a loose copy BEATS the BSA; the base
///      archive is discovered (a 3rd provider) and the higher-rank plugin wins among BSAs; an absent path is ABSENT;
///      a drive-rooted path is a per-path error (Q3); a clean read reports no BsaFailures / not ReadIncomplete.
///   E  decoupled + cheap — asset status RESOLVES even when no .esp is on disk to build the record index (Stats THROWS
///      while AssetStatus answers): the asset path never pays the heavy build (the ReResolve null-safe guard).
///   F  membership freshness — a newly-ENABLED mod's asset is seen on the next call (the service rebuilds the asset
///      resolver on a profile change; RED if the membership invalidation regresses).
///
/// Self-contained: synthetic folders/instances in temp + the committed fixtures/asset-resolver/*.bsa, so the whole
/// thing runs on CI with NO BSArch. See memory project_facegen_diagnostics_resolver.
/// Run: dotnet run --project src/housecarl-generator asset-status-guard
/// </summary>
internal static class AssetStatusProbe
{
    // Paths that live INSIDE the committed fixtures (same content the asset-resolver guard rides).
    const string FacegenRel = @"meshes\actors\character\facegendata\facegeom\Dawnguard.esm\0001A51A.nif";   // in FixtureA only
    const string RankRel = @"meshes\rank\only-in-bsas.nif";                                                 // in FixtureA AND FixtureB

    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" asset-status guard — BSA discovery + plugin->BSA binding + freshness + tool response");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var fixDir = Path.GetFullPath(@"src/housecarl-generator/fixtures/asset-resolver");   // CI + docs run from the repo root
        var fixA = Path.Combine(fixDir, "FixtureA.bsa");
        var fixB = Path.Combine(fixDir, "FixtureB.bsa");
        if (!File.Exists(fixA) || !File.Exists(fixB))
        {
            Console.WriteLine($"  FAIL  committed BSA fixtures present at {fixDir} (run from the repo root)");
            Console.WriteLine("================ 1 CHECK(S) FAILED ================");
            return 1;
        }

        var root = Path.Combine(Path.GetTempPath(), "hc-asset-status-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            // ================= DISCOVERY arms (ArchiveDiscovery directly) =================

            // ---- A: co-name + " - Textures" + base archive + RANK order ----
            Console.WriteLine("--- A: discovery — co-name + Textures + Skyrim.ini base + rank order ---");
            {
                var a = Path.Combine(root, "discA");
                var mods = Path.Combine(a, "mods");
                var data = Path.Combine(a, "game", "Data");
                var prof = Path.Combine(a, "profiles", "Default");
                var modA = Path.Combine(mods, "ModA");
                var modB = Path.Combine(mods, "ModB");
                foreach (var d in new[] { data, prof, modA, modB }) Directory.CreateDirectory(d);
                File.Copy(fixA, Path.Combine(modA, "PluginA.bsa"));
                File.Copy(fixA, Path.Combine(modA, "PluginA - Textures.bsa"));   // the SE Textures convention
                File.Copy(fixB, Path.Combine(modB, "PluginB.bsa"));
                File.Copy(fixB, Path.Combine(data, "Skyrim - Textures.bsa"));    // a base archive
                WriteProfile(prof, new[] { "PluginA.esp", "PluginB.esp" },       // winner LAST → PluginB highest
                             new[] { "*PluginA.esp", "*PluginB.esp" }, new[] { "+ModA", "+ModB" });
                WriteSkyrimIni(prof, "Skyrim - Textures.bsa");

                var res = ArchiveDiscovery.Discover(prof, mods, data, "", Path.Combine(a, "game"));
                var arc = res.Archives;
                var baseA = arc.Where(x => x.OwningPlugin == "Skyrim.ini [Archive]").ToList();
                var aArc = arc.Where(x => x.OwningPlugin == "PluginA.esp").ToList();
                var bArc = arc.Where(x => x.OwningPlugin == "PluginB.esp").ToList();

                Check(arc.Count == 4, $"4 active archives discovered (1 base + 2 for PluginA + 1 for PluginB) — got {arc.Count}");
                Check(baseA.Count == 1 && baseA[0].Path.Replace('/', '\\').StartsWith(data, StringComparison.OrdinalIgnoreCase),
                      "the Skyrim.ini base archive is discovered, from the Data folder");
                Check(aArc.Count == 2 && aArc.Any(x => x.Path.EndsWith("PluginA.bsa", StringComparison.OrdinalIgnoreCase))
                      && aArc.Any(x => x.Path.EndsWith("PluginA - Textures.bsa", StringComparison.OrdinalIgnoreCase)),
                      $"PluginA binds BOTH 'PluginA.bsa' and 'PluginA - Textures.bsa' (the Textures convention) — got {aArc.Count}");
                Check(aArc.Count == 2 && aArc[0].PluginRank == aArc[1].PluginRank, "a plugin's two archives share its rank");
                Check(bArc.Count == 1, $"PluginB binds 'PluginB.bsa' — got {bArc.Count}");
                // rank direction: base BELOW every plugin archive; a later plugin (B) ABOVE an earlier (A).
                Check(baseA[0].PluginRank < aArc.Min(x => x.PluginRank), "base archives rank BELOW every plugin archive");
                Check(aArc[0].PluginRank < bArc[0].PluginRank, "a later-loaded plugin's archive OUTRANKS an earlier one (rank = wins among BSAs)");
            }

            // ---- B: archive VFS winner — the higher-priority mod's copy of a .bsa filename wins ----
            Console.WriteLine();
            Console.WriteLine("--- B: discovery — a duplicate .bsa filename resolves to the higher-priority mod ---");
            {
                var b = Path.Combine(root, "discB");
                var mods = Path.Combine(b, "mods");
                var data = Path.Combine(b, "game", "Data");
                var prof = Path.Combine(b, "profiles", "Default");
                var hi = Path.Combine(mods, "HiMod");
                var lo = Path.Combine(mods, "LoMod");
                foreach (var d in new[] { data, prof, hi, lo }) Directory.CreateDirectory(d);
                File.Copy(fixA, Path.Combine(hi, "PluginA.bsa"));   // higher priority (listed first)
                File.Copy(fixB, Path.Combine(lo, "PluginA.bsa"));   // SAME filename, lower priority
                WriteProfile(prof, new[] { "PluginA.esp" }, new[] { "*PluginA.esp" }, new[] { "+HiMod", "+LoMod" });
                WriteSkyrimIni(prof, "");   // no base archives

                var res = ArchiveDiscovery.Discover(prof, mods, data, "", Path.Combine(b, "game"));
                var pa = res.Archives.Single(x => x.OwningPlugin == "PluginA.esp");
                Check(pa.Path.Replace('/', '\\').StartsWith(hi, StringComparison.OrdinalIgnoreCase),
                      "the higher-priority mod's copy of 'PluginA.bsa' is the discovered winning path (VFS, not look-beside-the-esp)");
            }

            // ---- C: a Skyrim.ini that can't be found → LOUD warning, base archives not scanned (Q3) ----
            Console.WriteLine();
            Console.WriteLine("--- C: discovery — missing Skyrim.ini is surfaced LOUD, never a silent gap ---");
            {
                var c = Path.Combine(root, "discC");
                var mods = Path.Combine(c, "mods");
                var data = Path.Combine(c, "game", "Data");
                var prof = Path.Combine(c, "profiles", "Default");
                var modA = Path.Combine(mods, "ModA");
                foreach (var d in new[] { data, prof, modA }) Directory.CreateDirectory(d);
                File.Copy(fixA, Path.Combine(modA, "PluginA.bsa"));
                WriteProfile(prof, new[] { "PluginA.esp" }, new[] { "*PluginA.esp" }, new[] { "+ModA" });
                // NO Skyrim.ini written anywhere.

                var res = ArchiveDiscovery.Discover(prof, mods, data, "", Path.Combine(c, "game"));
                Check(res.Warnings.Any(w => w.Contains("Skyrim.ini", StringComparison.OrdinalIgnoreCase) && w.Contains("base", StringComparison.OrdinalIgnoreCase)),
                      "a missing Skyrim.ini base list is a LOUD warning (Q3 — 'absent' isn't over-trusted)");
                Check(res.Archives.All(x => x.OwningPlugin != "Skyrim.ini [Archive]") && res.Archives.Any(x => x.OwningPlugin == "PluginA.esp"),
                      "no base archive is invented, but plugin co-name discovery still works");
            }

            // ================= SERVICE arms (the real LoadOrderService over a synthetic instance) =================

            // ---- D + E: tool response + decoupling (an asset-only instance: BSAs + loose, NO .esp on disk) ----
            Console.WriteLine();
            Console.WriteLine("--- D: service AssetStatus — winner / providers / ambiguity / absent / bad path (Q3) ---");
            {
                var inst = Path.Combine(root, "svc-d");
                var mods = Path.Combine(inst, "mods");
                var data = Path.Combine(inst, "game", "Data");
                var prof = Path.Combine(inst, "profiles", "Default");
                var modA = Path.Combine(mods, "ModA");
                var modB = Path.Combine(mods, "ModB");
                var looseMod = Path.Combine(mods, "LooseWins");
                foreach (var d in new[] { data, prof, modA, modB, looseMod }) Directory.CreateDirectory(d);
                File.Copy(fixA, Path.Combine(modA, "PluginA.bsa"));            // FacegenRel + RankRel
                File.Copy(fixB, Path.Combine(modB, "PluginB.bsa"));            // RankRel
                File.Copy(fixB, Path.Combine(data, "Skyrim - Textures.bsa")); // base: RankRel
                WriteLoose(looseMod, FacegenRel);                             // a loose copy of the facegen path
                WriteProfile(prof, new[] { "PluginA.esp", "PluginB.esp" }, new[] { "*PluginA.esp", "*PluginB.esp" },
                             new[] { "+LooseWins", "+ModA", "+ModB" });        // LooseWins highest priority
                WriteSkyrimIni(prof, "Skyrim - Textures.bsa");
                WriteIni(inst, "Default", Path.Combine(inst, "game"));

                var store = new UserConfigStore(Path.Combine(root, "user-d.json"));
                using var svc = LoadOrderService.WithInstance(inst, 0, store);

                var data2 = svc.AssetStatus(new[] { FacegenRel, RankRel, @"meshes\nope\missing.nif", @"C:\Windows\evil.nif" });
                var rFace = data2.Results[0];
                var rRank = data2.Results[1];
                var rMiss = data2.Results[2];
                var rBad = data2.Results[3];

                Check(rFace.Hit is { Exists: true } && rFace.Hit.Winner is { Source: "LooseWins", Kind: AssetKind.Loose }
                      && rFace.Hit.Ambiguous && rFace.Hit.Providers.Any(p => p.Kind == AssetKind.Bsa),
                      $"a loose copy BEATS the BSA copy, contention flagged — winner={rFace.Hit?.Winner?.Source}/{rFace.Hit?.Winner?.Kind}");
                Check(rRank.Hit is { Exists: true } && rRank.Hit.Winner is { Source: "PluginB.bsa", Kind: AssetKind.Bsa }
                      && rRank.Hit.Providers.Count == 3,
                      $"among BSAs the higher-rank plugin wins + the base archive is a discovered provider — winner={rRank.Hit?.Winner?.Source}, providers={rRank.Hit?.Providers.Count}");
                Check(rMiss.Hit is { Exists: false }, "an unprovided path is ABSENT");
                Check(rBad.Hit is null && rBad.Error is not null && rBad.Error.Contains("drive-rooted"),
                      $"a drive-rooted path is a per-path error, not a batch failure — {rBad.Error}");
                Check(data2.BsaFailures.Count == 0 && !data2.ReadIncomplete, "a clean read reports no archive failures, not incomplete");

                // ---- E: decoupled + cheap — the record index can't build (no .esp) but asset status answered ----
                Console.WriteLine();
                Console.WriteLine("--- E: asset status is decoupled from the record index (no .esp on disk) ---");
                Check(Throws<Exception>(() => svc.Stats()),
                      "the record index can't build with no .esp on disk — yet AssetStatus above resolved: the asset path is decoupled and never pays the ~10s record build");
            }

            // ---- F: membership freshness — a newly-enabled mod's asset is seen on the next call ----
            Console.WriteLine();
            Console.WriteLine("--- F: a newly-ENABLED mod's asset is seen after the profile change (membership rebuild) ---");
            {
                var inst = Path.Combine(root, "svc-f");
                var mods = Path.Combine(inst, "mods");
                var data = Path.Combine(inst, "game", "Data");
                var prof = Path.Combine(inst, "profiles", "Default");
                var modA = Path.Combine(mods, "ModA");
                foreach (var d in new[] { data, prof, modA }) Directory.CreateDirectory(d);
                // A dummy .esp so the load order resolves to a real path (paths.Count>0 → ReResolve's real branch fires);
                // its bytes are never parsed (AssetStatus skips the heavy record build via the null-safe guard).
                File.WriteAllText(Path.Combine(modA, "PluginA.esp"), "dummy");
                File.Copy(fixA, Path.Combine(modA, "PluginA.bsa"));
                WriteProfile(prof, new[] { "PluginA.esp" }, new[] { "*PluginA.esp" }, new[] { "+ModA" });
                WriteSkyrimIni(prof, "");
                WriteIni(inst, "Default", Path.Combine(inst, "game"));

                var store = new UserConfigStore(Path.Combine(root, "user-f.json"));
                using var svc = LoadOrderService.WithInstance(inst, 0, store);

                const string newRel = @"meshes\new\added.nif";
                Check(svc.AssetStatus(new[] { newRel }).Results[0].Hit is { Exists: false }, "the new asset is absent before its mod is enabled");

                // Enable a NEW loose mod that provides it, and record the profile change (modlist.txt mtime moves).
                var newMod = Path.Combine(mods, "NewMod");
                Directory.CreateDirectory(newMod);
                WriteLoose(newMod, newRel);
                WriteProfile(prof, new[] { "PluginA.esp" }, new[] { "*PluginA.esp" }, new[] { "+NewMod", "+ModA" });
                File.SetLastWriteTimeUtc(Path.Combine(prof, "modlist.txt"), DateTime.UtcNow.AddHours(1));   // guarantee the change is detected by value

                var after = svc.AssetStatus(new[] { newRel }).Results[0];
                Check(after.Hit is { Exists: true } && after.Hit.Winner is { Source: "NewMod", Kind: AssetKind.Loose },
                      $"after enabling the mod, its asset resolves (the asset resolver rebuilt on the membership change) — winner={after.Hit?.Winner?.Source}");
            }
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* temp scratch */ } }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "================ ALL PASS ================" : $"================ {fail} CHECK(S) FAILED ================");
        return fail == 0 ? 0 : 1;
    }

    // ---- synthetic MO2 layout helpers (the FreshnessCaptureProbe pattern) ----

    static void WriteProfile(string profDir, string[] loadorder, string[] plugins, string[] modlist)
    {
        Directory.CreateDirectory(profDir);
        File.WriteAllText(Path.Combine(profDir, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", loadorder) + "\r\n");
        File.WriteAllText(Path.Combine(profDir, "plugins.txt"), string.Join("\r\n", plugins) + "\r\n");
        File.WriteAllText(Path.Combine(profDir, "modlist.txt"), "# header\r\n" + string.Join("\r\n", modlist) + "\r\n");
    }

    /// <summary>Write a profile Skyrim.ini with an [Archive] sResourceArchiveList (empty string = the section with no
    /// archives, the "INI present but lists nothing" case).</summary>
    static void WriteSkyrimIni(string profDir, string resourceArchiveList)
    {
        Directory.CreateDirectory(profDir);
        File.WriteAllText(Path.Combine(profDir, "Skyrim.ini"),
            "[Archive]\r\nsResourceArchiveList=" + resourceArchiveList + "\r\n");
    }

    static void WriteIni(string inst, string profile, string gameDir) =>
        File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(" + profile + ")\r\ngamePath=@ByteArray("
            + gameDir.Replace(@"\", @"\\") + ")\r\n");

    static void WriteLoose(string baseDir, string rel)
    {
        var p = Path.Combine(baseDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, "x");
    }

    static bool Throws<T>(Action a) where T : Exception
    {
        try { a(); return false; } catch (T) { return true; } catch { return false; }
    }
}
