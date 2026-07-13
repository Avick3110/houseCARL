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
        ("script-property-check-guard", ScriptPropertyCheckProbe.RunGuard),
        ("source-display-guard", SourceDisplayProbe.RunGuard),
        ("writelock-guard", WriteLockProbe.RunGuard),
        ("inplace-guard", InPlaceProbe.RunGuard),
        ("subclass-remove-guard", SubclassRemoveGuardProbe.RunGuard),
        ("perk-refs-guard", PerkRefsProbe.RunGuard),
        ("conflict-diff-guard", ConflictDiffProbe.RunGuard),
        ("formid-floor-guard", FormIdFloorProbe.RunGuard),
        ("esl-formid-guard", EslFormIdProbe.RunGuard),
        ("upsert-guard", UpsertGuardProbe.RunGuard),
        ("nested-create-guard", NestedCreateGuardProbe.RunGuard),
        ("substruct-nullable-clear-guard", SubstructNullableClearProbe.RunGuard),
        ("coord-cell-guard", CoordCellGuardProbe.RunGuard),
        ("dialogue-validate-guard", DialogueValidateGuardProbe.RunGuard),
        ("dialogue-subtype-marker-guard", DialogueSubtypeMarkerGuardProbe.RunGuard),
        ("dialogue-ckparity-guard", DialogueCkParityGuardProbe.RunGuard),
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
        ("formlink-remove-guard", FormLinkRemoveProbe.RunGuard),
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
        // SKSE-plugin-layer visibility (gap 2026-06-08, tier C): pins the SKSEPlugin_Version decode contract (the
        // reverse-engineered offset map — supportEmail is 252, not 256 — + the flag/version/compat interpretation) and
        // the honest-degrade paths (real-PE Read → NotSkse; non-PE / missing → Unreadable, never a throw).
        ("skse-reader-guard", SkseReaderProbe.RunGuard),
        ("mo2instance-probe", Mo2InstanceProbe.RunProbe),
        // meta.ini Nexus-update-cache parse (Tier 0 PR review fold): the QSettings quirks + exact-key vs [installedFiles]
        // 1\modid, the fiddliest OFFLINE logic behind housecarl_update_status — locked with synthetic fixtures. Now also
        // pins the [installedFiles] N\fileid capture (single / multi / size=0 no-fileid) the file-level check joins on.
        ("mo2-modmeta-guard", Mo2ModMetaProbe.RunGuard),
        // FILE-LEVEL Nexus update check (fixes the multi-file-page false positive): ComputeStatus verdicts (live→current,
        // archived→outdated+same-name pointer, missing→file-gone, no-fileid→loud fallback), the id#fileid parse, and the
        // same-modId-across-folders fileid MERGE (never dedup-drop). Pure, network-free; synthetic AMON-shaped fixtures.
        ("nexus-file-check-guard", NexusFileCheckProbe.RunGuard),
        ("atomic-commit-guard", AtomicCommitProbe.RunGuard),
        ("place-asset-guard", PlaceAssetProbe.RunGuard),
        // NIF layer Wave 1: NifService.Inspect decodes an authored SE mesh's N2-whitelist values (version, census, node
        // tree, shape flags/scale, dismember partitions, alpha) and refuses a bad file loud. Self-contained (the fixture
        // is authored in-memory via NiflySharp); the spike §5 facegen smoke arm is existence-gated (SKIPs off-corpus).
        ("nif-service-guard", NifServiceGuardProbe.RunGuard),
        // NIF Wave 2 — housecarl_nif_set: each whitelist write op applies + verifies (two offset-immune gates), the gates
        // RED-prove they catch a collateral/no-op write, and every can't-do is a named refusal. Self-contained; the
        // set_path success arm is corpus-gated (nifly's read-only TextureSetRef blocks synthesizing a texture set).
        ("nif-set-guard", NifSetGuardProbe.RunGuard),
        ("strings-decision-guard", StringsDecisionProbe.RunGuard),
        ("assetlink-write-guard", AssetLinkWriteProbe.RunGuard),
        // The two coercion COMPLETENESS proofs, now CI guards (were manual-only — the coerce-audit blind spot that
        // shipped the asset-link gap showed a manual proof silently goes stale). Both are self-contained: selftest is
        // pure; audit reads the shared canonicalCorpus via CorpusRulebook.CorpusPath (set per-probe by RunAll, empty
        // args here → it uses that path). Each returns 0/1 like any guard.
        ("coerce-selftest", WriteEngine.RunCoerceSelftest),
        ("coerce-audit", WriteEngine.RunCoerceAudit),
        // Compact/merge foundation (RemapEngine, Wave 1): a self-contained multi-plugin compact end-to-end (renumber
        // into the ESL window + identify external referencers + in-place repoint) plus the two loud-refusal boundaries
        // (ESL capacity overflow, nested-only record). Pins the foundation the compact/merge tools (later waves) ride on.
        ("remap-wave1-guard", RemapWave1Probe.RunGuard),
        // COMPACT/MERGE Wave 2 GATE: the NESTED compact end-to-end (RemapEngine.RenumberModInto) — a synthetic mod with
        // a flat record, a FormList internal ref, an interior cell + placed, a worldspace + exterior cell + placed, and a
        // dialog topic + INFO is compacted into the ESL window (nesting preserved, internal refs repointed), then its
        // external referencer is found + repointed in place. Pins the housecarl_compact_plugin tool's cell-bearing path.
        ("remap-wave2-compact-guard", RemapWave2NestedMechProbe.RunCompactGuard),
        // COMPACT Wave 2 SERVICE-POLICY gate (PR #122 review #3): drives LoadOrderService.CompactPlugin over a synthetic
        // MO2 instance — the clean new-file lane, esl=false, override-of-master-cell-with-new-child, external refusal, the
        // repoint→in_place gate, not-active, and the in_place+repoint consent handshake. Covers the policy the engine guard bypasses.
        ("compact-service-guard", CompactServiceGuardProbe.RunGuard),
        // COMPACT/MERGE Wave A1 — the asset-rename SPINE's first category: compacting an NPC mod carries its FormID-keyed
        // FaceGen (head mesh + face tint) to the new FormID, end-to-end through the real service, so it no longer silently
        // dark-faces (the gap MERGE_REFERENCE_RESEARCH §3/§8 exposed in the shipped tool). New-file + in-place + no-facegen.
        ("facegen-carry-guard", FacegenCarryProbe.RunGuard),
        // COMPACT/MERGE Wave A2 — the asset-rename spine's SECOND category: compacting a VOICED mod carries its FormID-keyed
        // voice (.fuz spoken audio + .lip lip-sync) to the new INFO FormID, end-to-end through the real service, so it no
        // longer silently goes mute. New-file + in-place + multi-line + no-voice; rides the same two-phase carry as facegen.
        ("voice-carry-guard", VoiceCarryProbe.RunGuard),
        // COMPACT gap #2 — the identify-pass now detects external OVERRIDERS (a plugin that overrides a renumbered record,
        // not just one that FormLinks to it), surfaced as a WARN (warn-and-proceed, xEdit parity) distinct from the
        // referencer refuse/repoint. Proves overrider→success+named and referencer→refused stay separate.
        ("overrider-detect-guard", OverriderDetectProbe.RunGuard),
        // COMPACT/MERGE Wave A3 — the asset-rename spine's THIRD category: compacting a start-game-enabled-quest mod
        // REGENERATES its .seq from the renumbered plugin (a renumber shifts the on-disk FormIDs a stale .seq lists, so its
        // quests would silently never start). NOT a map-rename — rebuilt from P′. New-file + in-place-stale-replace + multi-quest + no-SGE.
        ("seq-regen-guard", SeqRegenProbe.RunGuard),
        // COMPACT/MERGE Wave A4 — the merge tool (housecarl_merge_plugins) end-to-end over a synthetic MO2 instance:
        // multi-donor collision-only renumber (first donor keeps ids), cross-donor LOAD-ORDER-WINNER conflicts each
        // reported, the un-relisted-child GRAFT (a patch's DIAL override + the base mod's second INFO — the arm that
        // fails if merging a mod with its patch drops the base mod's lines), warn-not-refuse externals (referencer +
        // overrider named, in the outcome AND the rendered output), facegen/voice carried to the MERGED plugin-name
        // folders + .seq regen, donors byte-untouched, and the five loud refusals.
        ("merge-service-guard", MergeServiceGuardProbe.RunGuard),
        // COMPACT in-place verify read-back (HCBR-2026-06-28-01) — a multi-op in-place edit's forced touched-record verify
        // renders COMPACT by default (one re-read-clean line per record, all N, names what landed) instead of a deep
        // whole-record dump that overflowed the host token cap and spilled to a file (reading as "only some ops applied").
        // full_readback=true still gives the deep dump, now bounded under the host limit with an explicit truncation note.
        ("compact-readback-guard", CompactReadbackProbe.RunGuard),
        // STANDALONE-COPY CHAIN Stage 1 — housecarl_read_plugin_file: a RAW, out-of-load-order read of ONE plugin file
        // straight off disk (INCLUDING one DISABLED in MO2), the enabler for forking a donor you're removing from the
        // order. Pins: locate+read a disabled plugin by filename, enumerate a type, whole-file summary, direct-path
        // read, the OUT-OF-LOAD-ORDER stamp, the missing-master advisory, and the Q3 refusals (missing/ambiguous
        // filename, bad/absent FormID, formid+type together). Opens its OWN overlay — never touches the resolver index.
        ("read-plugin-file-guard", ReadPluginFileProbe.RunGuard),
        // STANDALONE-COPY CHAIN Stage 3 — housecarl_copy_npc_appearance: the composed verb. Duplicate+RemapLinks the
        // donor's appearance closure under new keys (HDPT EditorIDs preserved — facegeom block-name identity; HDPT.Parts
        // — the lip-sync layer — carried by the whole-record copy), facegen pair renamed to the new FormKey path, donor-
        // only assets carried (record harvest + geom byte-scrape), clone-mode strips NAMED, donor NEVER a master.
        ("copy-npc-appearance-guard", NpcCopyProbe.RunGuard),
        // SKYPATCHER DISTRIBUTOR Wave 0a — the catalog-free structural tokenizer (SkyPatcherParse): pins the
        // grammar mechanics a SkyPatcher INI reader stands on — ':'-segment / '='-key-value / ','-list /
        // '~'-compound splitting, the ~…~ rename name-literal, the Plugin.esp|FormID address (leading-zero
        // trim), the 0a boundary (a bare EditorID stays un-addressed until the Wave-0b catalog), and the Q3
        // loud-note paths for malformed segments. Pure in-process string→model — no game data, no Mutagen.
        ("skypatcher-parse-guard", SkyPatcherParseProbe.RunGuard),
        // SKYPATCHER DISTRIBUTOR Wave 0b — the CLOSED grammar catalog (SkyPatcherCatalog): every documented
        // filter + operation across all 27 record types (+ the OMOD gap), transcribed from the skypatcher-
        // authoring reference. Pins coverage (all index.jsonl types present, sig/subfolder/primaryFilter), the
        // shape⇒tractability invariants, classification (filter+connective / operation / unknown = warn), and
        // the flagship HARD ops the tiered-honesty reader keys on. Catalog is an embedded resource; no game data.
        ("skypatcher-catalog-guard", SkyPatcherCatalogProbe.RunGuard),
        // SKYPATCHER DISTRIBUTOR Wave 1 — INI discovery (SkyPatcherDiscovery): the loose-only ordered UNION
        // the overlay replays. RED-proofs the two silent-corruption spots (plan §7): union-not-filename-winner
        // (differently-named INIs from different mods BOTH apply) and same-path collision (winner's content
        // once, loser NAMED shadowed); plus path-sort apply order, the Plugin.esp.ini filename gate, the
        // SkyPatcher.ini [Patcher] toggle, stray/undocumented folders. Synthetic mod dirs through the REAL
        // AssetResolver; the BSA-only arm is existence-gated on a committed fixture.
        ("skypatcher-discovery-guard", SkyPatcherDiscoveryProbe.RunGuard),
        // SKYPATCHER DISTRIBUTOR Wave 1 — the overlay replay engine (SkyPatcherOverlay): ordered, STATEFUL,
        // running-value replay onto a record copy. RED-proofs apply order (40 ×2.5 +11 = 111 — every wrong
        // model lands elsewhere), later-set-wins, collection accumulate, the Wave-1 filter tier (primary/
        // keywords/editorid/name/hasPlugins evaluated; anything else = LOUD unresolved skip), tiered honesty
        // (HARD ⇒ directive, unknown key ⇒ warn), null-clear, rename, enum + valueMap, vector components.
        ("skypatcher-overlay-guard", SkyPatcherOverlayProbe.RunGuard),
        // SKYPATCHER DISTRIBUTOR Wave 1 — the op→field map triangle (SkyPatcherFieldMap): every catalog
        // CLEAN/COLLECTION op across all 27 types is mapped or EXPLICITLY unmapped-with-reason; HARD ops
        // carry no mapping; every mapped path walks the REAL Mutagen mutable type via the write engine's
        // own property resolution; every valueMap/flag token parses into the real leaf enum; element types
        // instantiate and their sub-paths walk. Self-test arm RED-proves the checker catches a broken map.
        ("skypatcher-fieldmap-guard", SkyPatcherFieldMapProbe.RunGuard),
        // SKYPATCHER DISTRIBUTOR Wave 2 — the INI-vs-INI conflict detector (SkyPatcherConflicts): same-field
        // SET collisions across the ordered game-visible union (winner = the later file), accumulating ops
        // never conflict, same-value sets don't, not-applied files don't participate, broad-vs-explicit
        // collides, extra filters flag CONDITIONAL. Report-only by design (plan §8) — merge decisions stay
        // agent-side.
        ("skypatcher-conflicts-guard", SkyPatcherConflictsProbe.RunGuard),
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
