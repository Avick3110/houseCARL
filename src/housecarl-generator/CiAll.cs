using System.Diagnostics;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// The CI "run-all" probe runner (CI optimization Phase 2B; plan dev/plans/CI_OPTIMIZATION_RESEARCH_2026-06-24.md).
/// Runs the CI regression guards IN ONE PROCESS (all but one — the timing-fragile freshness-capture-guard stays
/// a cold step; see its registry note below), so the big Mutagen assembly loads + JITs once (vs once per
/// `dotnet run`/exe step) and the schema corpus is reflected once (CorpusGenerator memoizes — Phase 2A) instead of
/// ~21x. Replaces the per-probe steps in ci.yml with a single invocation.
///
/// Failure model — STRICTLY BETTER than the per-step job: every probe runs even if an earlier one fails, so one
/// run surfaces EVERY failing probe (the per-step job stopped at the first red step). The job still goes red if
/// any probe fails. Each failure is emitted as a GitHub `::error::` annotation naming the probe.
///
/// SAFETY — the per-probe co-hosting harness (research §5; the only cross-probe shared state in the suite):
///   * CorpusRulebook.CorpusPath (the one mutable static) is reset to the runner's canonical corpus BEFORE each
///     probe, so the 7 "check-first" probes (vmad-poly, poly-field-descend, sameshape, nullarm, formlink-null,
///     gendered-nav, floi-fields) reuse it and never validate against a prior probe's deleted temp corpus.
///   * setup-update-lock-guard nulls the CODEX_HOME env var and never restores it — snapshot + restore around
///     every probe.
///   * Each probe runs inside its own try/catch: a probe that THROWS (rather than returning non-zero) fails only
///     itself. (Many probes wrap their body only in try/finally cleanup, not try/catch-return.)
/// Everything else is already per-probe-scoped: Guid-unique temp dirs (deleted in each probe's finally) and
/// explicit-path UserConfigStores. The class-parents/decompile caches are per-LoadOrderService-INSTANCE (each
/// probe builds its own), not process statics, so co-hosting is safe (research §5 #6).
/// </summary>
public static class CiAll
{
    // The ordered CI probe set — the single source of truth for what CI runs (was the per-probe ci.yml steps).
    // Adding a CI probe = add it here. Kept in ci.yml step order so the one-step log reads the same as before.
    static readonly (string Name, Func<string[], int> Run)[] Probes =
    {
        ("tool-bridge", ToolBridgeProbe.Run),
        ("compile-probe", CompileProbe.Run),
        ("bsa-probe", BsaProbe.Run),
        ("pkcu-regression", PkcuProbe.RunRegression),
        ("depth-leak-guard", DepthLeakProbe.RunGuard),
        ("vmad-property-read-guard", VmadPropertyReadProbe.RunGuard),
        ("floi-read-guard", FloiReadProbe.RunGuard),
        ("floi-fields-guard", FloiFieldsProbe.RunGuard),
        ("forward-from-plugin-guard", ForwardFromPluginProbe.RunGuard),
        ("extend-resolve-guard", ExtendResolveProbe.RunGuard),
        ("create-plugin-guard", CreatePluginGuardProbe.RunGuard),
        ("value-predicate-guard", ValuePredicateProbe.RunGuard),
        ("effect-chain-guard", EffectChainProbe.RunGuard),
        ("check-errors-guard", CheckErrorsProbe.RunGuard),
        ("source-display-guard", SourceDisplayProbe.RunGuard),
        ("writelock-guard", WriteLockProbe.RunGuard),
        ("inplace-guard", InPlaceProbe.RunGuard),
        ("perk-refs-guard", PerkRefsProbe.RunGuard),
        ("conflict-diff-guard", ConflictDiffProbe.RunGuard),
        ("formid-floor-guard", FormIdFloorProbe.RunGuard),
        ("esl-formid-guard", EslFormIdProbe.RunGuard),
        ("upsert-guard", UpsertGuardProbe.RunGuard),
        ("nested-create-guard", NestedCreateGuardProbe.RunGuard),
        ("coord-cell-guard", CoordCellGuardProbe.RunGuard),
        ("dialogue-validate-guard", DialogueValidateGuardProbe.RunGuard),
        ("seq-write-guard", SeqWriteGuardProbe.RunGuard),
        ("seq-staleness-guard", SeqStalenessProbe.RunGuard),
        ("bulk-create-guard", BulkCreateGuardProbe.RunGuard),
        ("create-abstract-group-guard", CreateGlobalProbe.RunGuard),
        ("binding-shim-guard", BindingShimProbe.RunGuard),
        ("snapshot-view-guard", SnapshotViewProbe.RunGuard),
        ("verify-loop-guard", VerifyLoopProbe.RunGuard),
        ("vmad-poly-guard", VmadPolyProbe.RunGuard),
        ("poly-field-descend-guard", PolyFieldDescendProbe.RunGuard),
        ("sameshape-agree-guard", SameShapeAgreeProbe.RunGuard),
        ("corpus-hygiene-guard", CorpusHygieneProbe.RunGuard),
        ("plugin-validate-guard", PluginValidateProbe.RunGuard),
        ("nullarm-guard", NullArmGuardProbe.RunGuard),
        ("formlink-null-guard", FormLinkNullProbe.RunGuard),
        ("gendered-nav-guard", GenderedNavProbe.RunGuard),
        ("loadorder-status-guard", LoadOrderStatusProbe.RunGuard),
        ("compile-ergonomics-guard", CompileErgonomicsProbe.RunGuard),
        ("setup-update-lock-guard", SetupUpdateLockProbe.RunGuard),
        ("import-order-guard", ImportOrderProbe.RunGuard),
        ("render-clamp-guard", RenderClampProbe.RunGuard),
        ("decompile-guard", DecompileGuardProbe.RunGuard),
        ("bsa-contract-guard", BsaContractProbe.RunGuard),
        ("hierarchy-cache-guard", HierarchyCacheProbe.RunGuard),
        ("write-mutex-guard", WriteMutexProbe.RunGuard),
        // NOTE: freshness-capture-guard is deliberately NOT in the runner — its deferral arm needs a write slow
        // enough to straddle a fixed ~100ms sleep, which only holds in a COLD process. In this warm runner (hot
        // JIT + memoized corpus) the write finishes too fast to land that race, so the arm fails. It runs as its
        // OWN cold ci.yml step instead, where its timing assumption holds. The other 55 probes co-host cleanly.
        ("overwrite-resolve-guard", OverwriteResolveProbe.RunGuard),
        ("asset-resolver-guard", AssetResolverProbe.RunGuard),
        ("asset-status-guard", AssetStatusProbe.RunGuard),
        ("mo2instance-probe", Mo2InstanceProbe.RunProbe),
        ("atomic-commit-guard", AtomicCommitProbe.RunGuard),
        ("place-asset-guard", PlaceAssetProbe.RunGuard),
        ("strings-decision-guard", StringsDecisionProbe.RunGuard),
    };

    /// <summary>Dispatch a single CI guard by name through the registry — the ONE place a CI probe is listed, so
    /// Program.cs routes local single-probe runs here instead of keeping a parallel if-chain that could silently
    /// drift out of sync with the CI set (a guard runnable locally but missing from CI — the Q3 coverage-gap
    /// class). Returns false if the name isn't a registry probe; the caller then tries its own dispatches (the
    /// cold freshness-capture-guard carve-out + the manual/exploratory probes).</summary>
    public static bool TryDispatch(string name, string[] args, out int rc)
    {
        foreach (var (n, run) in Probes)
            if (n == name) { rc = run(args); return true; }
        rc = 0;
        return false;
    }

    public static int RunAll(string[] args)
    {
        var swAll = Stopwatch.StartNew();
        Console.WriteLine("================================================================");
        Console.WriteLine($" ci-all — running {Probes.Length} CI probes in ONE process");
        Console.WriteLine("================================================================");

        // Pre-generate the schema corpus ONCE up front. This (a) warms CorpusGenerator's memoize cache so the
        // ~21 corpus probes reflect zero extra times (Phase 2A), and (b) gives a canonical CorpusPath the
        // check-first probes reuse. Non-fatal if it fails — probes then self-generate (slower, still correct).
        string? canonicalCorpus = null;
        var corpusDir = Path.Combine(Path.GetTempPath(), "hc-ci-all-corpus-" + Guid.NewGuid().ToString("N"));
        try
        {
            var gen = Path.Combine(corpusDir, "generated");
            CorpusGenerator.GenerateAll(gen, Path.Combine(corpusDir, "refs"));
            var path = Path.Combine(gen, "corpus.json");
            if (File.Exists(path)) canonicalCorpus = path;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  (shared-corpus pre-gen failed: {ex.Message} — probes will self-generate)");
        }

        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");   // snapshot once (setup-update-lock nulls it)
        var results = new List<(string Name, bool Ok, string? Error, double Secs)>();

        foreach (var (name, run) in Probes)
        {
            // Reset the shared mutable state before each probe (the §5 co-hosting harness).
            if (canonicalCorpus != null) CorpusRulebook.CorpusPath = canonicalCorpus;
            Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);

            Console.WriteLine();
            Console.WriteLine($"──── [{results.Count + 1}/{Probes.Length}] {name} ────");
            var sw = Stopwatch.StartNew();
            int code;
            string? error = null;
            try
            {
                code = run(Array.Empty<string>());
            }
            catch (Exception ex)
            {
                code = 1;
                error = $"{ex.GetType().Name}: {ex.Message}";
                Console.WriteLine($"  THREW: {error}");
            }
            sw.Stop();
            bool ok = code == 0;
            results.Add((name, ok, error, sw.Elapsed.TotalSeconds));
            if (!ok)
                Console.WriteLine($"::error::CI probe '{name}' FAILED (exit {code}){(error != null ? " — " + error : "")}");
        }

        Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);        // final restore
        try { Directory.Delete(corpusDir, recursive: true); } catch { /* best-effort temp cleanup */ }

        // ---- summary ----
        swAll.Stop();
        var failed = results.Where(r => !r.Ok).ToList();
        Console.WriteLine();
        Console.WriteLine("================================================================");
        Console.WriteLine($" ci-all summary — {results.Count - failed.Count}/{results.Count} passed in {swAll.Elapsed.TotalMinutes:N2} min");
        Console.WriteLine("================================================================");
        Console.WriteLine(" slowest probes:");
        foreach (var r in results.OrderByDescending(r => r.Secs).Take(8))
            Console.WriteLine($"   {r.Secs,6:N1}s  {r.Name}");
        if (failed.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($" FAILED ({failed.Count}):");
            foreach (var r in failed)
                Console.WriteLine($"   - {r.Name}{(r.Error != null ? " — " + r.Error : "")}");
        }
        Console.WriteLine(failed.Count == 0
            ? "\n================ ALL PASS ================"
            : $"\n================ {failed.Count} PROBE(S) FAILED ================");
        return failed.Count == 0 ? 0 : 1;
    }
}
