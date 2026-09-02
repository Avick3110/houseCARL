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
        // Conditions[].Data arm expansion (#258): the depth-floor "open one bounded level" exception — VMAD-property-
        // only until now (vmad-property-read-guard's sibling) — also opens a polymorphic ConditionData arm's params,
        // so a `Conditions` list dump at depth=3 reaches Data.ActorValue/Faction/… instead of stopping at the bare
        // arm type. Bounded (one level), depth-not-lowered (depth=2 still suppresses), non-arm substructs still stop.
        ("condition-arm-expand-guard", ConditionArmExpandProbe.RunGuard),
        // depth-2 element identity (#198): a struct with no Name/EditorID/Title but EXACTLY ONE FormLink surfaces
        // that link as its identity ([PerkPlacement] Perk=…) instead of a bare opaque [PerkPlacement]; name-identity
        // still wins over the lone link (fallback fires only when no name-like identity exists). Self-contained.
        ("element-identity-guard", ElementIdentityProbe.RunGuard),
        // depth-2 element identity for an OWNED RECORD (#252): a list element that is itself an IMajorRecordGetter
        // (DIAL Responses → DialogResponses/INFO) surfaces its OWN FormKey (+ EditorID when present) instead of a
        // bare [DialogResponses] — the #198 family carried to records (FormKey is the identity, not a lone link).
        ("owned-record-identity-guard", OwnedRecordIdentityProbe.RunGuard),
        // unknown-bits flag decode (#255): a [Flags] enum leaf carrying unnamed bits (ToString falls back to a bare
        // decimal, losing the known bits) now hangs a DISPLAY-ONLY "<names> (+unknown bits 0x…)" decode — the token
        // (bare decimal, Enum.Parse-round-trippable) is untouched; biped-slot flags keep their own slot decode.
        ("flag-bits-display-guard", FlagBitsDisplayProbe.RunGuard),
        // owned-child content annotation (#342 stage 1): a parent's child records are declared PER PLUGIN, so a
        // winner that touched the parent for an unrelated reason reports an empty cell the game fills. A read of an
        // owning field (the write surface's pinned child-bearing set, not a cell hand-list) now says so, in two
        // tiers — the default read states from the INDEX that other plugins touch the record and were not opened;
        // conflict_tree, which has already fetched every body, names which ones declare. Two sentence arms, because
        // a COLLECTION child is assembled additively and a SINGULAR one is overridden. Display-only, and free on
        // every read whose type owns no children.
        ("owned-child-content-guard", OwnedChildContentProbe.RunGuard),
        ("floi-read-guard", FloiReadProbe.RunGuard),
        ("floi-fields-guard", FloiFieldsProbe.RunGuard),
        ("forward-from-plugin-guard", ForwardFromPluginProbe.RunGuard),
        ("extend-resolve-guard", ExtendResolveProbe.RunGuard),
        // Patch-stem load-order collision (PR #192 review): the default stem "Patch" → "Patch.esp" basename is common,
        // and mod-folder uniqueness alone can't see a same-named plugin in another mod, so UniqueStem now also uniquifies
        // against the active load order. Drives the real service write on a synth order that already holds an active
        // "Patch.esp"; a default-stem write must dodge to "Patch_001", a non-colliding stem stays bare, base byte-intact.
        ("patch-stem-collision-guard", PatchStemCollisionProbe.RunGuard),
        ("create-plugin-guard", CreatePluginGuardProbe.RunGuard),
        ("value-predicate-guard", ValuePredicateProbe.RunGuard),
        ("effect-chain-guard", EffectChainProbe.RunGuard),
        ("check-errors-guard", CheckErrorsProbe.RunGuard),
        ("script-property-check-guard", ScriptPropertyCheckProbe.RunGuard),
        // The merged surface the two above are absorbed into (SPEC §6.1). Its own guard rather than an extension of
        // either ancestor's: what it holds is the response the MERGE produces — sectioning, one accounting per
        // family, the roster declared once, and the divided budget — none of which either ancestor can render.
        ("check-guard", CheckMergeProbe.RunGuard),
        ("source-display-guard", SourceDisplayProbe.RunGuard),
        // BULK-PRIMITIVES Wave 1 — the three type-agnostic cross_plugin_query additions (PLAN P1/P2/P4): defined_in=
        // (definitions vs touches), list-valued references= (OR + matches= un-merge), group_by= (winner|type|
        // defined_in count table). Drives the real service scan + the tool-layer group_by/fields guard on a synthetic
        // master+replacer order; group_by counts are cross-checked against a hand tally; both loud refusals asserted.
        ("bulk-query-primitives-guard", BulkQueryPrimitivesProbe.RunGuard),
        // WAVE 2 output contract — housecarl_resolve (P3), winner_fields= (P5), format=json (P6), resolve_names= (P7).
        // Drives the real service + tool layer on a synthetic order with NAMED weapons; asserts identity resolution,
        // per-item error isolation, always-valid JSON with token parity, the winner-vs-scoped body choice, and the
        // display-only link annotation.
        ("bulk-primitives-wave2-guard", BulkPrimitivesWave2Probe.RunGuard),
        // WAVE 3 write batch + diff — composes= (P8a batch struct-list Add/ReplaceAll), CopyFrom (P8b field transplant),
        // housecarl_diff_record (P8c pairwise diff). Drives the real tool path on a synthetic order (+ an off-order pole
        // for CopyFrom/diff); asserts append-vs-clear semantics, all-or-nothing per-element reasons, copy-then-readback
        // equality across field kinds + named non-transplantable refusals, and the two-pole diff incl. an off-order side.
        ("bulk-primitives-wave3-guard", BulkPrimitivesWave3Probe.RunGuard),
        // W3 — housecarl_apply, the 2.0 S1 write surface: the ops grammar + @file, the LANE grammar (three
        // exclusive destinations, in_place as the overwritten file's NAME, the consent handshake), the §4.5
        // bundle x assignments cross-record copy zip, CopyFrom on the in-place lane, and json/epoch TRANSPORT.
        ("apply-guard", ApplyGuardProbe.RunGuard),
        // W3 PR 2 — the REST of the 2.0 S1 write surface (create / remove / forward / the migrated write_seq):
        // the records grammar incl. the nested one-shot and the strict reader's corrections, the LANE grammar on
        // every tool (removal creates no artifact, so it refuses a call naming no lane), removal's recovered
        // plural capability, forward's renamed source= pole, and json/epoch TRANSPORT — including write_seq's
        // ABSENT epoch, stated as a fact with its reason.
        ("write-surface-guard", WriteSurfaceGuardProbe.RunGuard),
        // #314 — an UNOPENABLE active plugin must not break every write. Its own probe because its fixture cannot be
        // shared: the broken plugin poisons every write in whatever order it sits in, which IS the bug.
        ("excluded-master-guard", ExcludedMasterWriteProbe.RunGuard),
        ("writelock-guard", WriteLockProbe.RunGuard),
        // #225 dry_run= on the write tools — the real pipeline HALTED before serialize, nothing written: refusal
        // parity with the real call, prediction parity (path/After/masters), the pre-empted missing-master failure,
        // the read-only in-place consent axis, and the DRY RUN render honesty.
        ("dry-run-guard", DryRunProbe.RunGuard),
        ("inplace-guard", InPlaceProbe.RunGuard),
        ("subclass-remove-guard", SubclassRemoveGuardProbe.RunGuard),
        ("perk-refs-guard", PerkRefsProbe.RunGuard),
        ("deleted-record-scan-guard", PerkRefsProbe.RunDeletedGuard),
        // #279 — the SAME deleted-record rule in the two SIBLING link walkers (check_errors' dangling sweep and the
        // compact/merge dependency scan), now all three routed through DeletedRecordRule. Two arms per walker: the
        // SEMANTIC one (an intact deleted body's link is not a finding) and the CRASH-CLASS one (a throwing deleted
        // body is not an untyped unscannable skip), plus a SCOPE arm pinning the guard behind remap's identity-only
        // overrider test. Controls prove each fixture still exhibits the pre-fix hazard.
        ("deleted-link-walk-guard", DeletedLinkWalkProbe.RunGuard),
        ("conflict-diff-guard", ConflictDiffProbe.RunGuard),
        ("formid-floor-guard", FormIdFloorProbe.RunGuard),
        ("esl-formid-guard", EslFormIdProbe.RunGuard),
        ("upsert-guard", UpsertGuardProbe.RunGuard),
        ("nested-create-guard", NestedCreateGuardProbe.RunGuard),
        // #302 — InsertAtIndex, the third member of the Add / SetAtIndex family. Its own probe rather than more
        // arms on nested-create-guard because what it pins is one claim (the element lands AT the index and the
        // rows after it shift rather than move), asserted by reference identity in memory and again off disk.
        ("insert-at-index-guard", InsertAtIndexGuardProbe.RunGuard),
        // The remedy sentences the verb set is recited into. Six emitted messages used to hand-copy verb names;
        // they now derive them from WriteVerbs, and this guard measures that derivation against the real gate —
        // every named verb ACCEPTED and every omitted verb REFUSED, over every collection field in the corpus,
        // bucketed by shape with the population of each reported so an empty bucket cannot pass quietly.
        ("remedy-verbs-guard", RemedyVerbsGuardProbe.RunGuard),
        ("substruct-nullable-clear-guard", SubstructNullableClearProbe.RunGuard),
        ("coord-cell-guard", CoordCellGuardProbe.RunGuard),
        ("dialogue-validate-guard", DialogueValidateGuardProbe.RunGuard),
        ("dialogue-subtype-marker-guard", DialogueSubtypeMarkerGuardProbe.RunGuard),
        ("dialogue-ckparity-guard", DialogueCkParityGuardProbe.RunGuard),
        ("dialogue-info-order-guard", DialogueInfoOrderProbe.RunGuard),
        ("seq-write-guard", SeqWriteGuardProbe.RunGuard),
        ("seq-staleness-guard", SeqStalenessProbe.RunGuard),
        ("bulk-create-guard", BulkCreateGuardProbe.RunGuard),
        ("create-abstract-group-guard", CreateGlobalProbe.RunGuard),
        ("binding-shim-guard", BindingShimProbe.RunGuard),
        // The $ref-flattening mechanism (#451): synthetic documents for the shapes the real published surface
        // cannot produce, the real strict reader for the terminator's accept claim (ARM 6), and every registered
        // tool's real pre-flatten schema for the emission grammar (ARM 7). The invariant on the served surface is
        // binding-shim-guard's SCHEMA arms.
        ("schema-flatten-guard", SchemaFlattenProbe.RunGuard),
        ("alias-layer-guard", AliasLayerProbe.RunGuard),
        ("snapshot-view-guard", SnapshotViewProbe.RunGuard),
        // Epoch fingerprint (tool-surface 2.0 W1, SPEC §2.1.1): the captured-index build identity — deterministic
        // over (names, mtimes, order), sensitive to content/order/set changes (backdated included), stamped by every
        // index-backed lane at its Capture() boundary, carried by the text AND json renders (D2), re-stamped on a
        // mid-session rebuild so cross-page drift is visible. Refusals answered off a build carry it; refusals that
        // never consulted one stay null.
        ("epoch-guard", EpochGuardProbe.RunGuard),
        // Artifact disposition (tool-surface 2.0 W1 PR 2, SPEC §2.1.1): bulk results decouple from renders via ONE
        // JSONL file (manifest line 1 with the epoch fingerprint); to_file forces it, a max_chars truncation ALWAYS
        // auto-spills the complete result to the server results dir with the spilled marker naming its file in both
        // formats, and @file re-entry yields the identity column epoch-checked against the consuming call's own
        // capture — a stale artifact refuses loud naming both epochs, with no override switch by design.
        ("artifact-guard", ArtifactGuardProbe.RunGuard),
        ("verify-loop-guard", VerifyLoopProbe.RunGuard),
        ("vmad-poly-guard", VmadPolyProbe.RunGuard),
        ("poly-field-descend-guard", PolyFieldDescendProbe.RunGuard),
        ("sameshape-agree-guard", SameShapeAgreeProbe.RunGuard),
        ("corpus-hygiene-guard", CorpusHygieneProbe.RunGuard),
        // #397: the arm classifier must tell a real coverage gap ("no getter interface, yet writable") from a
        // correct exclusion ("read-only projection"). The split is byte-invisible in the shards by design, so
        // this drives ClassifyArm directly and pins all three verdicts to real types in the live assembly.
        ("arm-classification-guard", ArmClassificationProbe.RunGuard),
        // #351: the committed mutagen-reference shards are a GENERATED artifact that also ships. Every other
        // corpus guard asserts against a fresh temp emit, so a classifier or emitter change landing without a
        // regeneration left the shipped reference stale with CI green. Compare the two and name what differs.
        ("emit-match-guard", EmitMatchProbe.RunGuard),
        ("plugin-validate-guard", PluginValidateProbe.RunGuard),
        // Codex umbrella coverage: the single hand-maintained Codex router (plugin/codex/housecarl/SKILL.md) must
        // reference every current MCP tool (reflected off [McpServerTool]) and every .claude/skills/* folder, or
        // allow-list the omission — turns the silent 45→9 drift into a RED arm naming exactly what's unrouted.
        ("codex-umbrella-coverage-guard", CodexUmbrellaCoverageProbe.RunGuard),
        // Spec-object wire names (#341): every [JsonPropertyName] on the surface, held against the caller-facing
        // shape declaration that names the same member — so a misspelled attribute goes RED instead of dropping
        // that parameter for every real MCP caller with the suite green (every probe builds specs with the C#
        // initializer, which never consults the attribute). Reflection-discovered, so a new spec object enrols
        // itself. Self-contained (no corpus, no MO2 instance).
        ("wire-names-guard", WireNamesProbe.RunGuard),
        // Caller-facing prose vocabulary (#386): nothing read the WORDS of the [Description] attributes a model
        // reads or of the consent prompts a modder reads, so the in-place consent fix left stale claims that took
        // four sweeps to clear — three run surface by surface, finding 11 on the acknowledge= parameters, 6 in the
        // WriteTools lane parentheticals and 2 in the handshake builders, and a fourth run repo-wide by VOCABULARY
        // instead, finding fourteen more in homes the first three had no reason to look at. (The counts are given
        // per pass, as the probe's own header gives them: #386's headline totals its first three as thirteen,
        // which its table contradicts, so nothing here restates a total.) (wire-names-guard does reach the
        // description surface, but for the brace shape declaration it parses out of one — never for what the
        // sentence around it claims.)
        // The net is every string literal in the three SHIPPED trees, read TWICE —
        // by Roslyn and by an independently written lexer — with INV6-AGREE holding the two against each other, so
        // a reader that stops early turns red instead of quietly shrinking the net. Self-contained (no corpus, no
        // MO2 instance); it reads source files, so it must run from the repo root.
        ("description-vocab-guard", DescriptionVocabularyGuardProbe.RunGuard),
        // The read surface's refusal grammar is COMPLETE, and stays complete (#403). The grammar shipped as 81
        // explicit Wire.Refuse sites whose population was found by a regex over two hand-named files; the pre-PR
        // review found it short by fourteen, and proved by mutation that reverting all fourteen ReadTools sites
        // to prose left this whole suite green. So: the population DERIVES itself (every Guard.Tool body that
        // consults the format machinery — a new json-capable tool enrols itself, and housecarl_check stops being
        // outside the net), the enumeration PARSES rather than pattern-matching (the construct that defeated the
        // sweep was a ternary spanning three lines, which is exactly what a line-oriented pattern cannot see),
        // and the bare refusals that remain each cite the settled decision that rules them correct. Carries its
        // own known-RED fixture — the pre-fix ternary — because a checker whose known-red case comes back green
        // is a broken checker. Self-contained (no corpus, no MO2 instance); reads source, so it must run from the
        // repo root.
        ("refusal-completeness-guard", RefusalCompletenessGuardProbe.RunGuard),
        ("nullarm-guard", NullArmGuardProbe.RunGuard),
        ("formlink-null-guard", FormLinkNullProbe.RunGuard),
        ("formlink-remove-guard", FormLinkRemoveProbe.RunGuard),
        // Flags-enum bit verbs (HCBR-2026-07-15): Add/Remove on a [Flags] enum are bit-SET / bit-CLEAR, preserving the
        // OTHER bits — closing the silent-clobber a whole-value Set caused. Pre-flight admits the verb + validates the
        // flag (gate), apply ORs/AND-NOTs the bit (WriteEngine), the two keyed off the SAME FlagsAttribute test; the
        // anti-clobber Add + scoped-not-universal controls are the teeth. Self-contained (Quest.Flags, no Skyrim.esm).
        ("flags-bit-verb-guard", FlagsBitVerbProbe.RunGuard),
        ("gendered-nav-guard", GenderedNavProbe.RunGuard),
        ("loadorder-status-guard", LoadOrderStatusProbe.RunGuard),
        // SKSE config audit (tier B, #199): the reference EXTRACTOR pinned against every §3 evidence shape (both token
        // orders, ESL FExxxYYY vs low-24 masking via the shared FormIdRange home, tilde form, path-segment gate, comment/
        // overflow/no-ref accounting) PLUS the service VERDICTS (OK/PLUGIN-MISSING/DANGLING/UNPARSEABLE + ESL FE-prefix
        // resolve) driven through LoadOrderService.Adjudicate over a synthetic full+light order. Self-contained.
        ("skse-config-audit-guard", SkseConfigAuditProbe.RunGuard),
        // Native-function pairing audit guard: the pure pex native-class extractor (raw-bit1 off-by-one pin), the
        // provenance anchor (official archives / chain presence), the §4c ladder, the runtime compare, and the wire
        // renderer arms (dead-vs-verify adjudication, unpaired framing, baseline accounting, filter + did-you-mean).
        ("native-pairing-guard", NativePairingProbe.RunGuard),
        ("compile-ergonomics-guard", CompileErgonomicsProbe.RunGuard),
        ("setup-update-lock-guard", SetupUpdateLockProbe.RunGuard),
        ("import-order-guard", ImportOrderProbe.RunGuard),
        ("render-clamp-guard", RenderClampProbe.RunGuard),
        ("decompile-guard", DecompileGuardProbe.RunGuard),
        ("bsa-contract-guard", BsaContractProbe.RunGuard),
        // BSA extract read path (#217): housecarl_bsa_extract / _list read through Mutagen's in-process BSA reader
        // (Archive.CreateReader) instead of shelling BSArch — BSArch's unpacker is stricter than its lister + the game,
        // so a non-BSArch-written archive could list yet unpack to nothing. Self-contained — hand-authors valid uncompressed
        // v105/v104 archives Mutagen reads: byte-correct round-trip, content-aware idempotence, path-traversal refusal,
        // and loud failure on a non-archive. (Real-BSArch + compressed-archive byte parity lives in the opt-in bsa-probe.)
        ("bsa-extract-guard", BsaExtractProbe.RunGuard),
        ("hierarchy-cache-guard", HierarchyCacheProbe.RunGuard),
        ("write-mutex-guard", WriteMutexProbe.RunGuard),
        // NOTE: freshness-capture-guard is deliberately NOT in the runner — its deferral arm needs a write slow
        // enough to straddle a fixed ~100ms sleep, which only holds in a COLD process. In this warm runner (hot
        // JIT + memoized corpus) the write finishes too fast to land that race, so the arm fails. It runs as its
        // OWN cold ci.yml step instead, where its timing assumption holds. The other 55 probes co-host cleanly.
        ("overwrite-resolve-guard", OverwriteResolveProbe.RunGuard),
        ("asset-resolver-guard", AssetResolverProbe.RunGuard),
        ("asset-status-guard", AssetStatusProbe.RunGuard),
        // #273 — the missing-root suggestion on an ABSENT asset path (a record's Model.File is stored relative to
        // meshes\, so passing it verbatim is the normal way one arrives at a mesh). Drives the real nif_inspect /
        // nif_set / asset_status over a synthetic instance. Paired arms: the suggestion FIRES on a re-resolvable
        // path, and is ABSENT for a look-alike that doesn't resolve — the teeth against a string heuristic.
        ("asset-prefix-hint-guard", AssetPrefixHintProbe.RunGuard),
        // SKSE-plugin-layer visibility (gap 2026-06-08, tier C): pins the SKSEPlugin_Version decode contract (the
        // reverse-engineered offset map — supportEmail is 252, not 256 — + the flag/version/compat interpretation) and
        // the honest-degrade paths (real-PE Read → NotSkse; non-PE / missing → Unreadable, never a throw).
        ("skse-reader-guard", SkseReaderProbe.RunGuard),
        // SKSE tier D (static peek): the string extraction (ASCII *and* UTF-16LE — an ASCII-only scan is a confident
        // half-blind answer), the classification filter that keeps compiler noise out of a DLL's "config surface", the
        // PE import walk + its empty-vs-unknown tri-state, the CURATED Debug-CRT list (a d-suffix is a convention, not a
        // loader rule), and the render arms — load-order cross-check, the machine-checked "will not load" wording, the
        // framing line, and the bare-peek loud error.
        ("skse-peek-guard", SksePeekProbe.RunGuard),
        ("mo2instance-probe", Mo2InstanceProbe.RunProbe),
        // meta.ini Nexus-update-cache parse (Tier 0 PR review fold): the QSettings quirks + exact-key vs [installedFiles]
        // 1\modid, the fiddliest OFFLINE logic behind housecarl_update_status — locked with synthetic fixtures. Now also
        // pins the [installedFiles] N\fileid capture (single / multi / size=0 no-fileid) the file-level check joins on.
        ("mo2-modmeta-guard", Mo2ModMetaProbe.RunGuard),
        // FILE-LEVEL Nexus update check (fixes the multi-file-page false positive): ComputeStatus verdicts (live→current,
        // archived→outdated+same-name pointer, missing→file-gone, no-fileid→loud fallback), the id#fileid parse, and the
        // same-modId-across-folders fileid MERGE (never dedup-drop). Pure, network-free; synthetic AMON-shaped fixtures.
        ("nexus-file-check-guard", NexusFileCheckProbe.RunGuard),
        // The raw GraphQL passthrough backstop: read-only mutation/subscription refusal (an op keyword at doc start or
        // after a prior '}', never a field that merely contains the word) + BOUNDED pretty-printed output. Pure, no network.
        ("nexus-graphql-guard", NexusGraphqlProbe.RunGuard),
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
        // nif_inspect batch wire (#229 — mesh_paths array, asset_status parity): input order, per-path error
        // isolation (one bad path never aborts the batch), batch-level alarms once + first, explicit omitted-mesh
        // cut notice. Self-contained (constructed results through the real NifWire renderer).
        ("nif-inspect-batch-guard", NifInspectBatchGuardProbe.RunGuard),
        // nif_inspect sections= parsing (#247): the JSON-array-as-string form (["shapes","paths"]) now parses (bracket
        // + quote are delimiters too) instead of tokenizing to garbage and QUIETLY rendering the default summary; an
        // all-unrecognized sections= is a LOUD error, not a silent fallback; the known-sections hint points texture-set
        // slot paths at where they live ('shapes'/'paths'). Drives NifTools.ParseSections/SectionsError/hint directly.
        ("nif-sections-guard", NifSectionsProbe.RunGuard),
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
        // LOCALIZED IN-PLACE WRITE (#368 + #373): that EVERY strings arrangement refuses the in-place write and each
        // refuses in its own words, naming where that plugin's text actually is; that the refusal is decided off the
        // mod in memory, so a destination locked by another process still refuses; that a destination houseCARL cannot
        // read refuses rather than being read as not-localized; and that a refusal leaves the plugin and its tables
        // byte-identical. It carries NO round-trip, remedy or crash-window pass: there is no accepted in-place write
        // to round-trip and no commit window to fault-inject, both having been cut (2026-08-26).
        ("localized-write-guard", LocalizedWriteGuardProbe.RunGuard),
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
        // bulk_apply composes= Add read-back count (#259): appending N elements in ONE composes= op reported the
        // verify line as "(+1), new [last]" (the Add renderer hardcoded a +1 delta); it now carries the op's
        // appended count and reports the whole run "(+N), new [a..b]". Drives the real in-place write; a 1-element
        // compose still reads (+1), new [0] = <element> (count wired from Structs.Count, not a constant).
        ("readback-count-guard", ReadbackCountProbe.RunGuard),
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
        // ORDERED SOURCE UNIVERSE (SPEC §3.1 amendment 2026-08-14) — a walk's source universe is a LIST of §4.2
        // poles tried in order, first hit wins. Pins: length-1 is today's grammar in BOTH spellings (["winner"] =
        // the active order's winner, ["X.esp"] = that file's own version, disabled included); order IS the
        // semantics (reversing the arms reverses the answer); the hit names WHICH arm produced it; a miss names
        // EVERY arm consulted; a faulting arm STOPS the chain instead of substituting a later arm's version; and
        // the §5.5 no-sigil exemption, pinned against a plugin actually named 'winner.esp'.
        ("source-chain-guard", SourceChainProbe.RunGuard),
        // CLOSURE WALK (SPEC §3) — the generic link walk the 2.0 copy verb is built on, tested apart from MO2 on in-memory
        // records. Pins: EnumerateFormLinks expansion (no per-type list); seed PATHS as caller data, with a typo
        // REFUSING rather than seeding nothing; expand-vs-keep with boundaries named; exclusions as data at two
        // severities; a cap breach refusing with the last pull AND its full chain and nothing usable (the ACT
        // posture); and cycles RECORDED but told apart from a diamond — the one behavior with no ancestor in
        // copy_npc_appearance, so its fixture carries both shapes at once.
        ("closure-walk-guard", ClosureWalkProbe.RunGuard),
        // CLOSURE COPY — the walk's ACT consumer: internalize under fresh keys, strip what still points at the
        // source universe, prove the artifact does not master it. Every arm sits where the ancestor had a real bug
        // or trap: remap confined by the scratch-mod step (a patch's deliberate reference survives untouched);
        // nullability judged on IFormLinkNullable, not SetToNull presence (that shipped Class=00000000); a required
        // bound link refusing loud; and the leak check scoped to bound keys, so a pre-existing dangling link is
        // NOT a false positive.
        ("closure-copy-guard", ClosureCopyProbe.RunGuard),
        // COPY SERVICE — the closure copy driven through the REAL service over a synthetic MO2 with a DISABLED
        // source: the attach lane onto an active target, the clone lane with its strip, arm attribution surviving
        // into the outcome, and E4 (an IN-PATCH target resolves off the OPENED patch mod, never the load order).
        ("copy-service-guard", CopyServiceProbe.RunGuard),
        // COPY PARSER — housecarl_copy's argument layer driven through the TOOL METHOD, so the WIRE spelling of
        // every documented parameter is under test and not just the typed values the service guard hands over.
        // Pins: 'Type:stop' vs 'Type:refuse' told apart BY RESULT (prune-and-keep vs fail the copy), the severity
        // token's case-insensitivity and its unknown/typeless refusals; exactly-one-destination, with a
        // whitespace-only target= counting as ABSENT; the from=/target= FormID refusals and the ORDER they fire in;
        // seed_paths required across absent/empty/blank-only, and trimmed; and the documented from_source= default.
        ("copy-parser-guard", CopyParserProbe.RunGuard),
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

    /// <summary>Every CI probe's name, for the unknown-mode refusal's list and did-you-mean (Program.cs). Read off the
    /// SAME registry the dispatch uses, so a probe can never be runnable and yet missing from the help. Materialised
    /// once — the refusal path reads it twice and nothing mutates the registry.</summary>
    public static IReadOnlyList<string> ProbeNames { get; } = Probes.Select(p => p.Name).ToArray();

    /// <summary>The TYPE hosting each registry guard's entry point, off the dispatch delegates themselves. Read by
    /// the residue countdown to derive which files still hold a CI guard — the registry is the only honest answer to
    /// "what does ci-all run", and a name convention is not: nine rows dispatch something other than RunGuard, and
    /// two of them (WriteEngine's coerce verbs) do not live in this project at all.</summary>
    public static IReadOnlyList<Type> ProbeTypes { get; } =
        Probes.Select(p => p.Run.Method.DeclaringType!).Distinct().ToArray();

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
