using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// Hierarchy-cache lifecycle guard (2026-06-12 adversarial hunt, F1 — proven): in instance mode the
/// service derives ModsDir LAZILY, and the decompiler's class-parents cache is built on first use
/// and held for process lifetime. A decompile-first session used to build the cache BEFORE ModsDir
/// was derived — the mods-tree harvest (SKSE/PO3/… source headers) was skipped and the baseline-only
/// map was cached for the whole process, with the degraded mode never named (the note only covers a
/// missing baseline): a silent Q3 degradation of the tool's quality claim. The PR #47 review fix
/// invalidated the cache on SetInstance and the ini re-derive, but missed this third
/// _modsDir-mutation site (EnsurePathsDerived).
///
/// Fixed two ways, both locked here: ClassParentsForDecompile derives the instance paths FIRST
/// (under the service gate, the established gate→parents lock order), and the first derivation
/// itself invalidates any cache built before it. Self-contained: synthesizes a minimal MO2 instance
/// (ModOrganizer.ini + profile files + a mod shipping one .psc header) in temp — no game data.
/// </summary>
internal static class HierarchyCacheProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" hierarchy-cache guard — decompile-first must see the mods tree");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var root = Path.Combine(Path.GetTempPath(), "hc-hierarchy-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            // ---- minimal synthetic MO2 instance (the established synth-instance pattern) ----
            string instance = Path.Combine(root, "instance");
            string profiles = Path.Combine(instance, "profiles", "Default");
            string mods = Path.Combine(instance, "mods");
            string data = Path.Combine(root, "game", "Data");
            Directory.CreateDirectory(profiles); Directory.CreateDirectory(mods); Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
                "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
                + Path.Combine(root, "game").Replace(@"\", @"\\") + ")\r\n");
            File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n");
            File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "");
            File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+SourceMod\r\n");
            var srcDir = Path.Combine(mods, "SourceMod", "scripts", "source");
            Directory.CreateDirectory(srcDir);
            File.WriteAllText(Path.Combine(srcDir, "HcGuardChild.psc"), "ScriptName HcGuardChild extends HcGuardParent\r\n");

            // ---- the decompile-first flow: hierarchy is the FIRST service touch of the process ----
            Console.WriteLine("--- 1: hierarchy requested before any path derivation ---");
            {
                var store = new UserConfigStore(Path.Combine(root, "userA.json"));
                using var svc = LoadOrderService.WithInstance(instance, 0, store);
                var (edges, _) = svc.ClassParentsForDecompile();
                Check(edges.TryGetValue("HcGuardChild", out var p1) && p1 == "HcGuardParent",
                      "FIRST call already sees the mods-tree edge (paths derive before the build)");
                var outDir = svc.ResolveDecompiledSourceFolder(null, null).OutputDir;
                Check(outDir.StartsWith(mods, StringComparison.OrdinalIgnoreCase), "output folder resolves under the instance's mods dir");
                var (edges2, _) = svc.ClassParentsForDecompile();
                Check(edges2.ContainsKey("HcGuardChild"), "the cache stays correct after derivation (no poisoned survivor)");
            }

            // ---- control: derive-first ordering keeps working ----
            Console.WriteLine();
            Console.WriteLine("--- 2: derive-first control ---");
            {
                var store = new UserConfigStore(Path.Combine(root, "userB.json"));
                using var svc = LoadOrderService.WithInstance(instance, 0, store);
                svc.ResolveDecompiledSourceFolder(null, null);
                var (edges, _) = svc.ClassParentsForDecompile();
                Check(edges.TryGetValue("HcGuardChild", out var p) && p == "HcGuardParent",
                      "derive-first still sees the mods-tree edge");
            }
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* temp scratch */ } }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "================ ALL PASS ================" : $"================ {fail} CHECK(S) FAILED ================");
        return fail == 0 ? 0 : 1;
    }
}
