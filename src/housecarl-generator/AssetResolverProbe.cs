using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// AssetResolver guard (facegen-diagnostics step 1). Proves the VFS-aware asset resolver that the dark-face skill
/// rides: which mod/BSA provides a Data-relative asset, and which copy WINS, under MO2 precedence.
///
/// Self-contained LOOSE arms (always run — synthetic mod/overwrite/data folders, no external tool):
///   1  loose precedence — a path in Data + a low mod + a high mod + overwrite resolves to OVERWRITE, all 4 providers listed, ambiguous.
///   2  loose by mod priority — with overwrite removed, the HIGHER-priority enabled mod wins.
///   3  absent — a path no source provides → Exists=false, Winner=null, not ambiguous.
///   4  loose is LIVE — a loose file dropped in AFTER Build resolves with NO RefreshIfStale (the resolver caches no loose state).
///
/// BSA arms (the native-Mutagen-read spike — run only when a BSArch is available to MAKE a fixture; SKIP cleanly otherwise,
/// like bsa-probe; pass BSArch as arg 1 or set HOUSECARL_BSARCH):
///   5  native BSA read — a path packed into a real SSE .bsa resolves via Mutagen's native reader (BsaFailures empty), kind=Bsa.
///   6  loose beats BSA — a loose copy of the same path wins over the BSA copy; both providers listed; ambiguous.
///   7  BSA by plugin rank — a path in two BSAs resolves to the HIGHER-rank one.
///   8  mtime refresh — repacking a BSA (new content) is picked up by RefreshIfStale (cached tables invalidate on mtime).
///
/// Run: dotnet run --project src/housecarl-generator asset-resolver-guard ["&lt;BSArch.exe&gt;"]
/// </summary>
internal static class AssetResolverProbe
{
    static readonly string DefaultBsarch = Environment.GetEnvironmentVariable("HOUSECARL_BSARCH") ?? "";

    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" asset-resolver guard — VFS asset resolution (loose precedence + native BSA)");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        const string rel = @"meshes\actors\character\facegendata\facegeom\Skyrim.esm\000918E2.nif";
        var root = Path.Combine(Path.GetTempPath(), "hc-asset-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            var overwrite = Path.Combine(root, "overwrite");
            var mods = Path.Combine(root, "mods");
            var data = Path.Combine(root, "Data");
            var high = Path.Combine(mods, "HighMod");      // higher MO2 priority (listed first)
            var low = Path.Combine(mods, "LowMod");        // lower MO2 priority
            foreach (var d in new[] { overwrite, high, low, data }) Directory.CreateDirectory(d);

            void WriteLoose(string baseDir) { var p = Path.Combine(baseDir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, "x"); }

            // ---- 1: full loose stack → overwrite wins, all 4 providers, ambiguous ----
            Console.WriteLine("--- 1-4: loose resolution (self-contained) ---");
            WriteLoose(data); WriteLoose(low); WriteLoose(high); WriteLoose(overwrite);
            var enabled = new[] { "HighMod", "LowMod" };   // highest priority FIRST (Mo2Composition order)
            using (var r = AssetResolver.Build(overwrite, mods, data, enabled, Array.Empty<ActiveArchive>()))
            {
                var hit = r.Resolve(rel);
                Check(hit.Exists && hit.Winner is { Source: "overwrite", Kind: AssetKind.Loose },
                      $"overwrite wins the full loose stack — winner={hit.Winner?.Source ?? "(none)"}");
                Check(hit.Providers.Count == 4, $"all 4 loose providers listed — {hit.Providers.Count}");
                Check(hit.Ambiguous, "contention flagged ambiguous (the dark-face desync signal)");
            }

            // ---- 2: remove overwrite copy → highest enabled mod wins ----
            File.Delete(Path.Combine(overwrite, rel));
            using (var r = AssetResolver.Build(overwrite, mods, data, enabled, Array.Empty<ActiveArchive>()))
            {
                var hit = r.Resolve(rel);
                Check(hit.Winner is { Source: "HighMod" }, $"the higher-priority mod wins among loose — winner={hit.Winner?.Source ?? "(none)"}");
            }

            // ---- 3: a path nobody provides ----
            using (var r = AssetResolver.Build(overwrite, mods, data, enabled, Array.Empty<ActiveArchive>()))
            {
                var miss = r.Resolve(@"meshes\does\not\exist.nif");
                Check(!miss.Exists && miss.Winner is null && !miss.Ambiguous, "an absent asset → Exists=false, no winner, not ambiguous");

                // ---- 4: loose is live — drop a new copy in AFTER Build, no refresh, it resolves ----
                var freshRel = @"meshes\foo\fresh.nif";
                Check(!r.Resolve(freshRel).Exists, "fresh path absent before it's written");
                var fp = Path.Combine(high, freshRel); Directory.CreateDirectory(Path.GetDirectoryName(fp)!); File.WriteAllText(fp, "x");
                Check(r.Resolve(freshRel).Winner is { Source: "HighMod" }, "a loose file added AFTER Build resolves with NO refresh (loose holds no state)");
            }

            // ---- dedup: the SAME .bsa bound by two plugins is read ONCE (no double-count → no false Ambiguous) ----
            // Self-contained: two ActiveArchive entries share one (unreadable) path. BuildTables reads it a single
            // time, so exactly ONE failure is recorded — proof the path-dedup collapsed the duplicate binding. (The
            // stronger "a readable shared archive lists ONE provider, not ambiguous" arm rides the committed fixture below.)
            Console.WriteLine();
            Console.WriteLine("--- dedup: a path-duplicate BSA binding is collapsed ---");
            {
                var dupPath = Path.Combine(root, "Shared.bsa");   // never created → unreadable; both bindings point here
                var dupArchives = new[]
                {
                    new ActiveArchive(dupPath, "PluginX.esp", PluginRank: 1),
                    new ActiveArchive(dupPath, "PluginY.esp", PluginRank: 2),
                };
                using var r = AssetResolver.Build(overwrite, mods, data, enabled, dupArchives);
                Check(r.BsaFailures.Count == 1, $"a path-duplicate archive is read once — {r.BsaFailures.Count} failure(s) (expected 1)");
            }

            // ---- 5-8: native BSA read (needs BSArch only to MAKE the fixture) ----
            Console.WriteLine();
            Console.WriteLine("--- 5-8: native Mutagen BSA read (fixture via BSArch) ---");
            var bsarch = args.Length > 0 ? args[0] : DefaultBsarch;
            if (!File.Exists(bsarch))
            {
                Console.WriteLine($"  SKIP  no BSArch at '{bsarch}' to build a .bsa fixture (pass its path as arg 1, or set HOUSECARL_BSARCH). Loose arms above are self-contained.");
            }
            else
            {
                const string bsaRel = @"meshes\actors\character\facegendata\facegeom\Dawnguard.esm\0001A51A.nif";
                const string rankRel = @"meshes\rank\only-in-bsas.nif";

                // archive A: holds the facegen path + the rank path
                var srcA = Path.Combine(root, "srcA");
                foreach (var rr in new[] { bsaRel, rankRel }) { var p = Path.Combine(srcA, rr); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, "A"); }
                var bsaA = Path.Combine(root, "ArchiveA.bsa");
                var packA = BsaArchive.Pack(bsarch, srcA, bsaA, BsaArchive.TryFormatFlag("sse")!, compress: false);

                // archive B: holds ONLY the rank path (to prove BSA-by-rank); higher rank than A
                var srcB = Path.Combine(root, "srcB");
                { var p = Path.Combine(srcB, rankRel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, "B"); }
                var bsaB = Path.Combine(root, "ArchiveB.bsa");
                var packB = BsaArchive.Pack(bsarch, srcB, bsaB, BsaArchive.TryFormatFlag("sse")!, compress: false);

                if (!packA.Success || !packB.Success)
                {
                    Console.WriteLine($"  SKIP  could not pack a fixture .bsa (A:{packA.Success} B:{packB.Success}) — {packA.RunError ?? packB.RunError ?? "see output"}");
                }
                else
                {
                    var archives = new[]
                    {
                        new ActiveArchive(bsaA, "PluginA.esp", PluginRank: 1),
                        new ActiveArchive(bsaB, "PluginB.esp", PluginRank: 2),   // higher rank wins among BSAs
                    };
                    using var r = AssetResolver.Build(overwrite, mods, data, enabled, archives);

                    // 5: native read worked — no failures, the facegen path resolves from the BSA
                    Check(r.BsaFailures.Count == 0, $"native Mutagen read: no archive-read failures — {(r.BsaFailures.Count == 0 ? "clean" : string.Join(" | ", r.BsaFailures))}");
                    var bhit = r.Resolve(bsaRel);
                    Check(bhit.Exists && bhit.Winner is { Kind: AssetKind.Bsa } && bhit.Winner.Source.Equals("ArchiveA.bsa", StringComparison.OrdinalIgnoreCase),
                          $"a BSA-packed asset resolves via the native reader — winner={bhit.Winner?.Source ?? "(none)"}/{bhit.Winner?.Kind}");

                    // 6: loose beats BSA — drop a loose copy of the BSA path into a mod
                    var lp = Path.Combine(high, bsaRel); Directory.CreateDirectory(Path.GetDirectoryName(lp)!); File.WriteAllText(lp, "loose");
                    var beat = r.Resolve(bsaRel);
                    Check(beat.Winner is { Source: "HighMod", Kind: AssetKind.Loose }, $"a loose copy beats the BSA copy — winner={beat.Winner?.Source ?? "(none)"}/{beat.Winner?.Kind}");
                    Check(beat.Providers.Any(p => p.Kind == AssetKind.Bsa) && beat.Ambiguous, "…and the BSA copy is still listed as a provider, flagged ambiguous");

                    // 7: BSA-by-rank — the rank path is in both BSAs; the higher rank (B) wins
                    var rank = r.Resolve(rankRel);
                    Check(rank.Winner is { Kind: AssetKind.Bsa } && rank.Winner.Source.Equals("ArchiveB.bsa", StringComparison.OrdinalIgnoreCase),
                          $"among BSAs the higher plugin-rank wins — winner={rank.Winner?.Source ?? "(none)"}");

                    // 8: mtime refresh — repack ArchiveA with an added path; RefreshIfStale picks it up
                    const string addedRel = @"meshes\added\after.nif";
                    var ap = Path.Combine(srcA, addedRel); Directory.CreateDirectory(Path.GetDirectoryName(ap)!); File.WriteAllText(ap, "A2");
                    var repack = BsaArchive.Pack(bsarch, srcA, bsaA, BsaArchive.TryFormatFlag("sse")!, compress: false);
                    if (!repack.Success) Console.WriteLine($"  SKIP  could not repack ArchiveA for the mtime arm — {repack.RunError ?? "see output"}");
                    else
                    {
                        bool refreshed = r.RefreshIfStale();
                        Check(refreshed, "RefreshIfStale() reports the repacked archive as stale");
                        Check(r.Resolve(addedRel).Winner is { Kind: AssetKind.Bsa }, "the newly-packed path resolves after the refresh (cached table invalidated on mtime)");
                    }
                }
            }
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* temp scratch */ } }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "================ ALL PASS ================" : $"================ {fail} CHECK(S) FAILED ================");
        return fail == 0 ? 0 : 1;
    }
}
