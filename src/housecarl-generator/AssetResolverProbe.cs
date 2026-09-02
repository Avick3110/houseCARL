using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// AssetResolver guard (facegen-diagnostics step 1). Proves the VFS-aware asset resolver that the dark-face skill
/// rides: which mod/BSA provides a Data-relative asset, and which copy WINS, under MO2 precedence.
///
/// Self-contained LOOSE arms (always run — synthetic mod/overwrite/data folders, no external tool):
///   • loose precedence + ORDER — Data + low mod + high mod + overwrite → OVERWRITE wins, all 4 providers in precedence order, ambiguous.
///   • loose by mod priority — with overwrite removed, the HIGHER-priority enabled mod wins.
///   • healthy boundary — a single-source asset is Exists, ONE provider, NOT ambiguous.
///   • absent — a path no source provides → Exists=false, Winner=null, not ambiguous.
///   • loose CACHE — a file added into a warmed subtree is seen only AFTER RefreshIfStale (the dir went absent→present).
///   • normalize — slash / leading-separator / case forms resolve to the same winner; a drive-rooted or ".." path is rejected loud.
///   • capture/refresh — ResolveMany matches per-path Resolve off ONE build; a no-op RefreshIfStale is false; a captured view answers from one build.
///   • dedup (unreadable) — two bindings sharing one unreadable path are read ONCE (a single BsaFailure).
///
/// COMMITTED-fixture BSA arms (also self-contained — tiny SSE .bsa files under fixtures/asset-resolver/, so the native
/// read + cornerstone run on CI with NO BSArch):
///   5  native BSA read — the facegen path in FixtureA.bsa resolves via Mutagen's native reader (BsaFailures empty), kind=Bsa.
///   6  loose beats BSA — a loose copy of the same path wins over the BSA copy; both providers listed; ambiguous.
///   7  BSA by plugin rank — a path in both fixtures resolves to the HIGHER-rank one (FixtureB).
///   • dedup (readable) — one fixture bound by two plugins lists ONE provider, NOT ambiguous (the false-Ambiguous regression).
///   • AT-REST CORNERSTONE — the .bsa is renamable + deletable WHILE the resolver is alive (zero archive handles at rest).
///   • negative — a truncated .bsa is ONE named BsaFailure, a good archive still resolves, and ReadIncomplete flags the build (Q3).
///
/// Only the mtime arm needs BSArch (it REPACKS a fixture; pass BSArch as arg 1 or set HOUSECARL_BSARCH; SKIPs cleanly otherwise):
///   8  mtime refresh — repacking a BSA (new content) is picked up by RefreshIfStale (cached tables invalidate on mtime).
///
/// Run: dotnet run --project src/housecarl-generator asset-resolver-guard ["&lt;BSArch.exe&gt;"]
/// </summary>
internal static class AssetResolverProbe
{
    static readonly string DefaultBsarch = Environment.GetEnvironmentVariable("HOUSECARL_BSARCH") ?? "";

    [CiProbe("asset-resolver-guard")]
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
                Check(hit.Providers.Count == 4
                      && hit.Providers[0].Source == "overwrite" && hit.Providers[1].Source == "HighMod"
                      && hit.Providers[2].Source == "LowMod" && hit.Providers[3].Source == "Data",
                      $"all 4 loose providers listed IN PRECEDENCE ORDER — [{string.Join(",", hit.Providers.Select(p => p.Source))}]");
                Check(hit.Ambiguous, "contention flagged ambiguous (more than one source provides it)");
            }

            // ---- 2: remove overwrite copy → highest enabled mod wins ----
            File.Delete(Path.Combine(overwrite, rel));
            using (var r = AssetResolver.Build(overwrite, mods, data, enabled, Array.Empty<ActiveArchive>()))
            {
                var hit = r.Resolve(rel);
                Check(hit.Winner is { Source: "HighMod" }, $"the higher-priority mod wins among loose — winner={hit.Winner?.Source ?? "(none)"}");
            }

            // ---- healthy boundary: a single-source asset is Exists, ONE provider, NOT ambiguous ----
            {
                var soloRel = @"meshes\solo\unique.nif";
                var sp = Path.Combine(data, soloRel); Directory.CreateDirectory(Path.GetDirectoryName(sp)!); File.WriteAllText(sp, "x");
                using var r = AssetResolver.Build(overwrite, mods, data, enabled, Array.Empty<ActiveArchive>());
                var hit = r.Resolve(soloRel);
                Check(hit.Exists && hit.Providers.Count == 1 && !hit.Ambiguous && hit.Winner is { Source: "Data", Kind: AssetKind.Loose },
                      "a single-source loose asset is healthy — Exists, one provider, not ambiguous");
            }

            // ---- 3: a path nobody provides ----
            using (var r = AssetResolver.Build(overwrite, mods, data, enabled, Array.Empty<ActiveArchive>()))
            {
                var miss = r.Resolve(@"meshes\does\not\exist.nif");
                Check(!miss.Exists && miss.Winner is null && !miss.Ambiguous, "an absent asset → Exists=false, no winner, not ambiguous");

                // ---- 4: loose cache — a file added into a WARMED subtree is picked up by RefreshIfStale (dir mtime) ----
                var freshRel = @"meshes\foo\fresh.nif";
                Check(!r.Resolve(freshRel).Exists, "fresh path absent before it's written (warms meshes\\foo empty)");
                var fp = Path.Combine(high, freshRel); Directory.CreateDirectory(Path.GetDirectoryName(fp)!); File.WriteAllText(fp, "x");
                Check(!r.Resolve(freshRel).Exists, "still absent from the warmed cache before a refresh (cache holds the empty warm)");
                Check(r.RefreshIfStale(), "RefreshIfStale sees the new loose dir (the subtree went absent→present)");
                Check(r.Resolve(freshRel).Winner is { Source: "HighMod" }, "after the refresh the new loose file resolves");
            }

            // ---- capture/refresh: ResolveMany pins one build; RefreshIfStale no-op is false + result-stable ----
            Console.WriteLine();
            Console.WriteLine("--- capture/refresh: ResolveMany parity + stable no-op refresh + captured view ---");
            using (var r = AssetResolver.Build(overwrite, mods, data, enabled, Array.Empty<ActiveArchive>()))
            {
                // overwrite's copy was deleted in arm 2; HighMod/LowMod/Data still hold `rel`, so the winner is HighMod.
                var paths = new[] { rel, @"meshes\does\not\exist.nif", rel.ToUpperInvariant() };
                var many = r.ResolveMany(paths);
                var one = paths.Select(p => r.Resolve(p)).ToList();
                Check(many.Count == 3 && many[0].Winner?.Source == one[0].Winner?.Source
                      && many[1].Exists == one[1].Exists && many[2].Winner?.Source == one[2].Winner?.Source,
                      "ResolveMany matches per-path Resolve (one pinned build)");

                bool first = r.RefreshIfStale();                  // nothing changed since Build
                bool second = r.RefreshIfStale();
                Check(!first && !second, $"RefreshIfStale is false when nothing changed — {first}/{second}");
                Check(r.Resolve(rel).Winner is { Source: "HighMod" }, "the result is unchanged across the no-op refresh (single-thread parity)");

                var view = r.Capture();
                Check(view.Resolve(rel).Winner is { Source: "HighMod" } && view.BsaFailures.Count == 0 && !view.ReadIncomplete,
                      "a captured view resolves + reports its (empty) failure list from one build");
            }

            // ---- normalize: slash form / leading separator / case all resolve to the SAME winner; bad paths rejected ----
            Console.WriteLine();
            Console.WriteLine("--- normalize: query-path forms equivalent + drive-rooted/.. rejected ---");
            using (var r = AssetResolver.Build(overwrite, mods, data, enabled, Array.Empty<ActiveArchive>()))
            {
                // overwrite's copy was deleted in arm 2; HighMod/LowMod/Data still hold `rel`, so the winner is HighMod.
                var fwd = r.Resolve(rel.Replace('\\', '/'));               // forward slashes
                var lead = r.Resolve("\\" + rel);                         // leading separator
                var upper = r.Resolve(rel.ToUpperInvariant());            // different case
                Check(fwd.Winner is { Source: "HighMod" } && lead.Winner is { Source: "HighMod" } && upper.Winner is { Source: "HighMod" },
                      $"slash/leading-sep/case forms resolve to the same winner — {fwd.Winner?.Source}/{lead.Winner?.Source}/{upper.Winner?.Source}");
                Check(Throws<ArgumentException>(() => { r.Resolve(@"C:\Windows\evil.nif"); }), "a drive-rooted query path is rejected loud");
                Check(Throws<ArgumentException>(() => { r.Resolve(@"..\..\escape.nif"); }), "a parent-escaping ('..') query path is rejected loud");
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

            // ---- BSA arms — COMMITTED fixtures run on CI (no BSArch); only the repack/mtime arm (8) needs BSArch ----
            // Fixtures are tiny SSE .bsa files we packed once (regenerate: pack a srcA{facegen path, rank path} and a
            // srcB{rank path} with `BSArch pack <src> <out> -sse -mt` into the dir below). Mutagen reads PATHS, so the
            // file bodies are 1 byte each.
            const string bsaRel = @"meshes\actors\character\facegendata\facegeom\Dawnguard.esm\0001A51A.nif";   // in FixtureA
            const string rankRel = @"meshes\rank\only-in-bsas.nif";                                            // in FixtureA AND FixtureB
            var fixDir = Path.GetFullPath(@"src/housecarl-generator/fixtures/asset-resolver");                 // relative to the repo root (CI + docs run here)
            var fixA = Path.Combine(fixDir, "FixtureA.bsa");
            var fixB = Path.Combine(fixDir, "FixtureB.bsa");

            Console.WriteLine();
            Console.WriteLine("--- 5-7: native Mutagen BSA read (committed fixtures, self-contained) ---");
            if (!File.Exists(fixA) || !File.Exists(fixB))
            {
                Check(false, $"committed BSA fixtures present at {fixDir} (run from the repo root)");
            }
            else
            {
                var archives = new[]
                {
                    new ActiveArchive(fixA, "PluginA.esp", PluginRank: 1),
                    new ActiveArchive(fixB, "PluginB.esp", PluginRank: 2),   // higher rank wins among BSAs
                };
                using (var r = AssetResolver.Build(overwrite, mods, data, enabled, archives))
                {
                    // 5: native read worked — no failures, the facegen path resolves from the BSA
                    Check(r.BsaFailures.Count == 0, $"native Mutagen read: no archive-read failures — {(r.BsaFailures.Count == 0 ? "clean" : string.Join(" | ", r.BsaFailures))}");
                    var bhit = r.Resolve(bsaRel);
                    Check(bhit.Exists && bhit.Winner is { Kind: AssetKind.Bsa } && bhit.Winner.Source.Equals("FixtureA.bsa", StringComparison.OrdinalIgnoreCase),
                          $"a BSA-packed asset resolves via the native reader — winner={bhit.Winner?.Source ?? "(none)"}/{bhit.Winner?.Kind}");

                    // 6: loose beats BSA — drop a loose copy, refresh; then DELETE it + refresh so later arms see the BSA winner (critic-4)
                    var lp = Path.Combine(high, bsaRel); Directory.CreateDirectory(Path.GetDirectoryName(lp)!); File.WriteAllText(lp, "loose");
                    r.RefreshIfStale();                            // the loose subtree under HighMod went absent→present
                    var beat = r.Resolve(bsaRel);
                    Check(beat.Winner is { Source: "HighMod", Kind: AssetKind.Loose }, $"a loose copy beats the BSA copy — winner={beat.Winner?.Source ?? "(none)"}/{beat.Winner?.Kind}");
                    Check(beat.Providers.Any(p => p.Kind == AssetKind.Bsa) && beat.Ambiguous, "…and the BSA copy is still listed as a provider, flagged ambiguous");
                    File.Delete(lp);

                    // 7: BSA-by-rank — the rank path is in both fixtures; the higher rank (B) wins
                    var rank = r.Resolve(rankRel);
                    Check(rank.Winner is { Kind: AssetKind.Bsa } && rank.Winner.Source.Equals("FixtureB.bsa", StringComparison.OrdinalIgnoreCase),
                          $"among BSAs the higher plugin-rank wins — winner={rank.Winner?.Source ?? "(none)"}");
                }

                // dedup (readable): the SAME fixture bound by two plugins lists ONE provider, NOT ambiguous (the stronger correctness-1 arm)
                using (var r = AssetResolver.Build(overwrite, mods, data, enabled, new[]
                {
                    new ActiveArchive(fixA, "PluginA.esp", PluginRank: 1),
                    new ActiveArchive(fixA, "PluginA2.esp", PluginRank: 2),   // same path, a second binding
                }))
                {
                    var hit = r.Resolve(rankRel);                  // rankRel is BSA-only here → exactly one provider, not ambiguous
                    Check(hit.Exists && hit.Providers.Count == 1 && !hit.Ambiguous && hit.Winner is { Kind: AssetKind.Bsa },
                          $"a path-duplicate READABLE archive lists ONE provider, not ambiguous — {hit.Providers.Count} provider(s), ambiguous={hit.Ambiguous}");
                }

                // at-rest CORNERSTONE: the .bsa is renamable + deletable WHILE the resolver is alive (zero archive handles at rest)
                Console.WriteLine();
                Console.WriteLine("--- at-rest cornerstone: zero archive handles held (the #3 contract) ---");
                var atrest = Path.Combine(root, "AtRest.bsa");
                File.Copy(fixA, atrest, overwrite: true);
                using (var r = AssetResolver.Build(overwrite, mods, data, enabled, new[] { new ActiveArchive(atrest, "P.esp", 1) }))
                {
                    Check(r.Resolve(bsaRel).Winner is { Kind: AssetKind.Bsa }, "at-rest fixture: the BSA table was read");
                    bool renamable;
                    try { var ren = atrest + ".ren"; File.Move(atrest, ren); File.Move(ren, atrest); renamable = true; } catch { renamable = false; }
                    Check(renamable, "the .bsa is RENAMABLE while the resolver is alive — zero archive handles at rest (the cornerstone)");
                    bool deletable;
                    try { File.Delete(atrest); deletable = !File.Exists(atrest); } catch { deletable = false; }
                    Check(deletable, "…and DELETABLE — MO2/xEdit can move/delete the archive freely");
                }

                // negative (Q3): an unreadable BSA is a NAMED failure; a good archive still resolves; ReadIncomplete flags it
                Console.WriteLine();
                Console.WriteLine("--- negative: an unreadable BSA is surfaced, never silently absent ---");
                var garbage = Path.Combine(root, "Garbage.bsa");
                var goodBytes = File.ReadAllBytes(fixA);
                File.WriteAllBytes(garbage, goodBytes[..(goodBytes.Length / 3)]);   // a truncated .bsa — the table read throws
                using (var r = AssetResolver.Build(overwrite, mods, data, enabled, new[]
                {
                    new ActiveArchive(fixA, "GoodPlugin.esp", 1),
                    new ActiveArchive(garbage, "BadPlugin.esp", 2),
                }))
                {
                    Check(r.BsaFailures.Count == 1 && r.BsaFailures[0].Contains("Garbage.bsa") && r.BsaFailures[0].Contains("BadPlugin.esp"),
                          $"an unreadable BSA is ONE named failure (file + owning plugin) — {(r.BsaFailures.Count == 1 ? r.BsaFailures[0] : r.BsaFailures.Count + " failure(s)")}");
                    Check(r.ReadIncomplete, "ReadIncomplete flags the build so an Exists=false isn't over-trusted (the Q3 caveat)");
                    Check(r.Resolve(bsaRel).Winner is { Kind: AssetKind.Bsa }, "a good archive still resolves alongside the unreadable one");
                }
            }

            // ---- 8: mtime refresh — repacking a BSA's bytes is picked up by RefreshIfStale (needs BSArch to repack) ----
            Console.WriteLine();
            Console.WriteLine("--- 8: BSA mtime refresh (repack — needs BSArch) ---");
            var bsarch = args.Length > 0 ? args[0] : DefaultBsarch;
            if (!File.Exists(bsarch))
            {
                Console.WriteLine($"  SKIP  no BSArch at '{bsarch}' to repack (pass its path as arg 1, or set HOUSECARL_BSARCH). The committed-fixture arms above are self-contained.");
            }
            else
            {
                const string addedRel = @"meshes\added\after.nif";
                var srcA = Path.Combine(root, "srcA");
                var seed = Path.Combine(srcA, @"meshes\seed\seed.nif"); Directory.CreateDirectory(Path.GetDirectoryName(seed)!); File.WriteAllText(seed, "seed");
                var bsaA = Path.Combine(root, "ArchiveA.bsa");
                var pack0 = BsaArchive.Pack(bsarch, srcA, bsaA, BsaArchive.TryFormatFlag("sse")!, compress: false);
                Check(pack0.Success, $"BSArch packs the seed archive — {(pack0.Success ? "ok" : pack0.RunError ?? "see output")}");
                if (pack0.Success)
                {
                    using var r = AssetResolver.Build(overwrite, mods, data, enabled, new[] { new ActiveArchive(bsaA, "P.esp", 1) });
                    Check(!r.Resolve(addedRel).Exists, "the to-be-added path is absent before the repack");
                    var ap = Path.Combine(srcA, addedRel); Directory.CreateDirectory(Path.GetDirectoryName(ap)!); File.WriteAllText(ap, "A2");
                    var repack = BsaArchive.Pack(bsarch, srcA, bsaA, BsaArchive.TryFormatFlag("sse")!, compress: false);
                    Check(repack.Success, $"BSArch repacks the archive — {(repack.Success ? "ok" : repack.RunError ?? "see output")}");   // BSArch PRESENT but pack failed = FAIL, never a silent skip
                    if (repack.Success)
                    {
                        Check(r.RefreshIfStale(), "RefreshIfStale() reports the repacked archive as stale");
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

    /// <summary>True iff <paramref name="a"/> throws an exception of type <typeparamref name="T"/> (any other throw, or
    /// none, is false) — the probe's loud-failure assertion helper.</summary>
    static bool Throws<T>(Action a) where T : Exception
    {
        try { a(); return false; } catch (T) { return true; } catch { return false; }
    }
}
