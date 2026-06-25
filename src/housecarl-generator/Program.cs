using HousecarlGenerator;

// houseCARL build-time schema generator (first-wave step 2).
//
// Reflects over the entire Mutagen.Bethesda.Skyrim type universe and emits a flat
// type catalog (corpus.json + corpus.summary.md) covering literally every modeled
// type at full depth. Pure reflection over types — no plugin file required.
//
// Usage:  dotnet run --project src/housecarl-generator [outputDir]   (default: ./generated)

// CI optimization Phase 2B: run EVERY CI probe in ONE process (the big Mutagen assembly loads + JITs once;
// the schema corpus reflects once via CorpusGenerator's memoize). Replaces the per-probe ci.yml steps with one
// invocation. See CiAll + dev/plans/CI_OPTIMIZATION_RESEARCH_2026-06-24.md.
if (args.Length > 0 && args[0] == "ci-all") return CiAll.RunAll(args[1..]);

// Single-probe runs of any CI guard dispatch through CiAll.Probes (the ONE CI source of truth), so a guard can't
// be runnable locally yet missing from the CI run (the Q3 coverage-gap class). freshness-capture-guard (the cold
// carve-out) and the manual/exploratory probes below keep their own explicit dispatch.
if (args.Length > 0 && CiAll.TryDispatch(args[0], args[1..], out var ciRc)) return ciRc;

// Maintenance diagnostic: re-verify the mutable-collection whitelist on a Mutagen bump.
if (args.Length > 0 && args[0] == "vocab") return Probe.RunVocab();

// Index-build resilience (Nexus bug): feasibility probe — is group enumeration resumable past a parse throw?
if (args.Length > 0 && args[0] == "pkcu-probe") return PkcuProbe.Run(args[1..]);

// Index-build resilience (Nexus bug): end-to-end proof the malformed plugin is isolated, not fatal.
if (args.Length > 0 && args[0] == "pkcu-fix-proof") return PkcuProbe.RunFixProof(args[1..]);

// Index-build resilience (Nexus bug): real-scale proof — full MO2 order + 1 malformed plugin, only it excluded.
if (args.Length > 0 && args[0] == "pkcu-scale-proof") return PkcuProbe.RunScaleProof(args[1..]);

// Freshness + write-capture guard (2026-06-12 hunt F5–F8 + PR #51 review note): restored-backup profile/ini
// changes (older mtimes) are seen; one status line / one multi-op write composes from ONE build; a concurrent
// read's freshness refresh defers while a write is in flight (never rebuilds under a serialize).
if (args.Length > 0 && args[0] == "freshness-capture-guard") return FreshnessCaptureProbe.RunGuard(args[1..]);

// Decompiler baseline hierarchy: emit vanilla-class-parents.json from the CK vanilla sources' own
// ScriptName-extends headers (committed asset — vanilla sources don't exist on CI; regenerate on game updates).
if (args.Length > 0 && args[0] == "class-parents") return ClassParentsEmitter.Run(args[1..]);

// Localized-strings read fix (Heisen 2026-06-24): DLC master resolved to a strings-less mod folder reads Name EMPTY.
if (args.Length > 0 && args[0] == "strings-resolve-probe") return StringsResolveProbe.Run(args[1..]);

// One-shot verify (decision #1): confirm the xEdit 4-char signature reflection path.
if (args.Length > 0 && args[0] == "sig") return Probe.RunSig();

// Step 4 write engine: build-start discovery of Mutagen's generic group-override surface.
if (args.Length > 0 && args[0] == "write-api") return WriteEngine.RunDiscovery(args[1..]);

// Step 4 write engine: NPC-skills-by-name acceptance proof (nested dict-in-substruct Set).
if (args.Length > 0 && args[0] == "npc-skills") return WriteEngine.RunNpcSkillsProof(args[1..]);

// Step 5 oracle: per-kind byte-identical cells (Path A engine vs Path B hand-written setter).
if (args.Length > 0 && args[0] == "oracle") return WriteOracle.Run(args[1..]);

// Step 5 build-start confirm: polymorphic arm-swap instantiation mechanism.
if (args.Length > 0 && args[0] == "poly-probe") return WriteEngine.RunPolyProbe(args[1..]);

// Absent-substruct characterization: which navigate-into substructs lack a parameterless ctor (the tricky shapes).
if (args.Length > 0 && args[0] == "substruct-probe") return WriteEngine.RunSubstructProbe(args[1..]);

// Wave 3 scout: recon Mutagen's nested-group override API (Cell/Placed*/INFO/Navmesh/Landscape) — the highest-risk unknown.
if (args.Length > 0 && args[0] == "nested-probe") return NestedProbe.RunNestedProbe(args[1..]);

// STEP 0 scout (nested/dialogue plan §1.4, BLOCKING): can houseCARL ALLOCATE a brand-new record INTO a nested parent
// by construction? Tests DuplicateIntoAsNewRecord (clone-a-sibling) + construct-and-Add-into-collection (new parent),
// the FormID floor, and characterizes the coordinate-keyed §4-(b) seam. Throwaway recon, fail-loud per shape (Q3).
if (args.Length > 0 && args[0] == "nested-create-probe") return NestedCreateProbe.RunProbe(args[1..]);

// Nested-create build proof (Layer A): drive the REAL WritePatchBuilder.CreateRecords nested path — one-shot
// topic+INFO, INFO into an existing topic, Placed into a cell (named collection), + the Q3 rejects (no-parent,
// bad parent, ambiguous collection, forward sibling). Re-opens each patch from disk; Skyrim.esm byte-checked.
if (args.Length > 0 && args[0] == "nested-create-proof") return NestedCreateProof.RunProof(args[1..]);

// STEP 0 scout for the COORDINATE-KEYED §4-(b) seam (exterior/interior Cell + Placed-into-new-cell): round-trips a
// constructed cell into find-or-built WorldspaceBlock/SubBlock (exterior, floor(grid/32|8)) and CellBlock/SubBlock
// (interior, FormID digits), checks override is thin, block math vs vanilla, OFST regen, source byte-unchanged.
if (args.Length > 0 && args[0] == "coord-cell-probe") return CoordCellProbe.RunProbe(args[1..]);

// Wave 4 scout: recon Mutagen's IFormLinkOrIndex condition-target API — the form-vs-index discriminator (condition oracle), the wave-4 unknown.
if (args.Length > 0 && args[0] == "condition-probe") return ConditionProbe.RunConditionProbe(args[1..]);

// Wave 5 scout: ModHeader mutable-root reachability (the header is a singleton property, not a group/record).
if (args.Length > 0 && args[0] == "header-probe") return Wave5Probe.RunHeaderProbe(args[1..]);

// Wave 5 scout: the PEX read->write round-trip GATE (project_pex_prefer_source_policy) — the wave-5 unknown.
if (args.Length > 0 && args[0] == "pex-probe") return Wave5Probe.RunPexProbe(args[1..]);

// NOTE: coerce-audit + coerce-selftest are now CI guards in CiAll.Probes (the ONE CI source of truth, dispatched
// for single runs via CiAll.TryDispatch at the top of this file) — no separate dispatch here, by design.

// Step 7 write-surface census: corpus-derived reachability map of every writable leaf (the completeness scoreboard).
if (args.Length > 0 && args[0] == "write-census") return WriteCensus.Run(args[1..]);

// Wave 0 prove-today: the only-target-moved differ + byte-true drive across the reachable settable surface.
if (args.Length > 0 && args[0] == "write-proof") return WriteProof.RunProof(args[1..]);

// Wave 2 real-patch dev harness: one concrete set_field -> a real reviewable .esp (Aaron xEdit-verifies).
if (args.Length > 0 && args[0] == "patch") return WriteEngine.RunPatch(args[1..]);

// Read-to-plan: resolve a record + print its fields/keywords (the minimum read to author a correct write).
if (args.Length > 0 && args[0] == "show") return WriteEngine.RunShow(args[1..]);

// Step 6 read surface: resolve a record + emit its fields as round-trippable tokens (inverse of Coerce).
if (args.Length > 0 && args[0] == "read") return ReadEngine.RunRead(args[1..]);

// Step 6 read-proof: read↔write round-trip oracle — read each value leaf, write the token back, assert no-op.
if (args.Length > 0 && args[0] == "read-proof") return WriteProof.RunReadProof(args[1..]);

// Wave 4 capability lock: re-target one real condition target -> a reviewable .esp (Aaron xEdit-verifies the new target).
if (args.Length > 0 && args[0] == "condition-patch") return WriteEngine.RunConditionPatch(args[1..]);

// MCP step §8.1: measure-first probe — on-demand vs held-index load-order resolution cost (the FIRST build action; gates fork §6-C).
if (args.Length > 0 && args[0] == "resolve-probe") return ResolveProbe.RunResolveProbe(args[1..]);

// MCP step §8.3 beat 1: measure-first body-fetch probe — cost of resolving a conflict tree's bodies on demand (the one piece §8.1 left open).
if (args.Length > 0 && args[0] == "body-fetch-probe") return BodyFetchProbe.RunBodyFetchProbe(args[1..]);

// MCP step §8.3: stand up + verify the net-new LoadOrderResolver (held RAM, tree correctness, body-fetch timing, freshness sweep).
if (args.Length > 0 && args[0] == "resolve") return ResolveHarness.RunResolve(args[1..]);

// MCP step (Beat C de-risk): prove the MULTI-MASTER write path — a real merge patch (leveled list + cross-master entries) -> reviewable .esp.
if (args.Length > 0 && args[0] == "multimaster-patch") return MultiMasterProof.RunMultiMasterPatch(args[1..]);

// MCP step (Beat C build): prove the PUBLIC write cleave the MCP set_field/bulk_apply/into= tools call (flat + multi + extend + cross-master + reject).
if (args.Length > 0 && args[0] == "apply-proof") return ApplyProof.RunApplyProof(args[1..]);

// MCP step §8.5: verify the TRUE active order read from MO2's static profile files (loadorder.txt + modlist.txt + plugins.txt).
if (args.Length > 0 && args[0] == "mo2-order") return Mo2OrderHarness.RunMo2Order(args[1..]);

// Capability arc scout (Remove + Create): AddNew/FormID allocation (Create) + write-path master derivation (clean-masters).
if (args.Length > 0 && args[0] == "remove-create-probe") return RemoveCreateProbe.RunProbe(args[1..]);

// Capability arc scout #2 (remove-record): whole-record removal via mod.Remove(FormKey) — flat + nested + not-found semantics.
if (args.Length > 0 && args[0] == "remove-record-probe") return RemoveRecordProbe.RunProbe(args[1..]);

// Capability arc remove-record proof: drive WritePatchBuilder.RemoveRecords (the core housecarl_remove_record calls) vs a real, large load order.
if (args.Length > 0 && args[0] == "remove-proof") return RemoveProof.RunRemoveProof(args[1..]);

// Capability arc scout #3 (Create — the LAST build): generic AddNew dispatch + FormID allocation + fields-via-ApplyVerb + the nested/abstract-T scope fork.
if (args.Length > 0 && args[0] == "create-probe") return CreateProbe.RunProbe(args[1..]);

// Capability arc create proof: drive WritePatchBuilder.CreateRecords (the core housecarl_create_record calls) vs a real, large load order.
if (args.Length > 0 && args[0] == "create-proof") return CreateProof.RunCreateProof(args[1..]);

// Master-baseline scout: how to FORCE Skyrim.esm onto every written plugin (Mutagen strips unreferenced masters) — Aaron-flagged bug.
if (args.Length > 0 && args[0] == "master-probe") return MasterProbe.RunProbe(args[1..]);

// Cleanup-gotcha / Option-B viability: prove a plain overlay LOCKS a plugin, Dispose() RELEASES it promptly, and open->read->dispose latency is invisible (de-risks the LOCKED Option-B fix).
if (args.Length > 0 && args[0] == "handle-probe") return HandleProbe.RunProbe(args[1..]);

// Cleanup-gotcha / Option-B AT-REST proof: drive the REAL product code (resolver Build -> read via session -> create via write path) on temp copies and assert files are renamable at rest (zero handles held).
if (args.Length > 0 && args[0] == "atrest-probe") return AtRestProbe.RunProbe(args[1..]);

// Active-patch write self-lock (Heisen bug 2026-06-08): EXPLORATORY — map the Windows file-sharing semantics of writing
// into a patch whose own overlay is held by AllMasters() (direct vs temp+Replace vs release-then-write). Decides the fix.
if (args.Length > 0 && args[0] == "writelock-probe") return WriteLockProbe.RunProbe(args[1..]);

// Active-patch write self-lock follow-up (PR #24 review): EXPLORATORY — prove Apply's Phase-1 winner-fetch opens a SECOND
// overlay on the target (when re-editing an own override) that survives AllMastersExcept and still self-locks the serialize.
if (args.Length > 0 && args[0] == "writelock-apply-probe") return WriteLockProbe.RunApplyResidualProbe(args[1..]);

// Active-patch write self-lock follow-up (PR #24 review #2): REAL-DATA proof that a NESTED record (PlacedObject, via the
// link-cache context path) survives the re-edit-own-override case under the new "release overlay before serialize" invariant.
if (args.Length > 0 && args[0] == "writelock-nested-proof") return WriteLockProbe.RunNestedProof(args[1..]);

// In-place write lane Wave 1: REAL-DATA proof of the NESTED own-override re-edit IN PLACE (the LinkCacheFor-on-a-foreign-
// target overlay path), the one arm the self-contained guard can't synthesize. Needs Skyrim.esm; self-skips on the runner.
if (args.Length > 0 && args[0] == "inplace-nested-proof") return InPlaceProbe.RunNestedProof(args[1..]);

// In-place write lane Wave 2: REAL-DATA proof of the NESTED own-override REMOVE IN PLACE (typed nested Remove + a real-data
// WriteInPlace re-serialize on a foreign target). The remove counterpart of inplace-nested-proof. Needs Skyrim.esm; self-skips.
if (args.Length > 0 && args[0] == "inplace-remove-nested-proof") return InPlaceProbe.RunRemoveNestedProof(args[1..]);

// Perk references= crash (HCBR-2026-06-09-03): DIAGNOSIS — run Mutagen's EnumerateFormLinks over every PERK in a
// real plugin, report which records throw and with what (the evidence the fix is designed from). Skips without Skyrim.esm.
if (args.Length > 0 && args[0] == "perk-refs-diagnose") return PerkRefsProbe.RunDiagnose(args[1..]);

// Perk references= crash (HCBR-2026-06-09-03): REAL-DATA proof — the report's exact failing call (type=Perk references=)
// over a live MO2 order through the service layer. Manual; needs --mo2 + --corpus (skips without).
if (args.Length > 0 && args[0] == "perk-refs-proof") return PerkRefsProbe.RunProof(args[1..]);

// Conflict-tree content diff (HCBR-2026-06-09-01): REAL-DATA proof — the report's exact repro (MM_RelentlessFury's
// "(identical to winner)" false ITM) over a live MO2 order. Manual; needs --mo2 (skips without).
if (args.Length > 0 && args[0] == "conflict-diff-proof") return ConflictDiffProbe.RunProof(args[1..]);

// FormID allocation floor (HCBR-2026-06-09-04): EXPLORATORY — pin the Mutagen NextFormID semantics (fresh-mod init,
// the Iterate serialize recompute that seeds 0, CreateFromBinary rehydration, AddNew-from-0) the fix is designed from.
if (args.Length > 0 && args[0] == "formid-floor-probe") return FormIdFloorProbe.RunProbe(args[1..]);

// ESL / FE-space FormID handling (HCBR-2026-06-15-01 item 5.1): EXPLORATORY — pin the Mutagen 0.53.1 small-master
// semantics (legal object-ID range, IsSmallMaster→FE-space encode through our incantation, FE decode round-trip,
// flag-tracking, index-independence) the guard + any §4 surface-call are built from.
if (args.Length > 0 && args[0] == "esl-formid-probe") return EslFormIdProbe.RunProbe(args[1..]);

// ESL ground-truth scan (HCBR-2026-06-15-01 item 5.1): EXPLORATORY — raw-byte scan of REAL plugins to settle
// whether SSE stores light-master references in FE-space on disk (0xFE high byte) or by master-list index.
if (args.Length > 0 && args[0] == "esl-real-scan") return EslFormIdProbe.RunRealScan(args[1..]);

// IN-PLACE WRITE LANE, Wave 0 (design-gating, IN_PLACE_WRITE_LANE_PLAN §5.3/§9 STEP-2): MANUAL/REAL-DATA probe —
// no-op re-serialize a sample of REAL plugins (counter-preserving, the §5.1-correct in-place shape) and measure the
// whole-plugin byte divergence surface (identical / header-only / body / records-changed / unloadable), so fork #1's
// round-trip accept/refuse threshold is calibrated on measured reality. Needs --mo2 <instance>; SKIPs without (a
// synthetic fixture round-trips clean and would reveal nothing). Writes only to temp; read-only on the load order.
if (args.Length > 0 && args[0] == "roundtrip-probe") return RoundTripProbe.RunProbe(args[1..]);

var outputDir = Path.GetFullPath(args.Length > 0 ? args[0] : "generated");
// The slim reference tree ships INSIDE the skill (tracked); corpus.json + summary stay in generated/.
// Default assumes the generator is run from the repo root (as `dotnet run --project src/housecarl-generator`).
var refDir = Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine(".claude", "skills", "mutagen-reference", "references"));
return CorpusGenerator.GenerateAll(outputDir, refDir);
