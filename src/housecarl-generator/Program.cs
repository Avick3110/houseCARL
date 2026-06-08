using HousecarlGenerator;

// houseCARL build-time schema generator (first-wave step 2).
//
// Reflects over the entire Mutagen.Bethesda.Skyrim type universe and emits a flat
// type catalog (corpus.json + corpus.summary.md) covering literally every modeled
// type at full depth. Pure reflection over types — no plugin file required.
//
// Usage:  dotnet run --project src/housecarl-generator [outputDir]   (default: ./generated)

// Maintenance diagnostic: re-verify the mutable-collection whitelist on a Mutagen bump.
if (args.Length > 0 && args[0] == "vocab") return Probe.RunVocab();

// Index-build resilience (Nexus bug): feasibility probe — is group enumeration resumable past a parse throw?
if (args.Length > 0 && args[0] == "pkcu-probe") return PkcuProbe.Run(args[1..]);

// Index-build resilience (Nexus bug): end-to-end proof the malformed plugin is isolated, not fatal.
if (args.Length > 0 && args[0] == "pkcu-fix-proof") return PkcuProbe.RunFixProof(args[1..]);

// Index-build resilience (Nexus bug): real-scale proof — full MO2 order + 1 malformed plugin, only it excluded.
if (args.Length > 0 && args[0] == "pkcu-scale-proof") return PkcuProbe.RunScaleProof(args[1..]);

// Index-build resilience (Nexus bug): SELF-CONTAINED CI regression guard — synthesizes the malformed PKCU, asserts isolation.
if (args.Length > 0 && args[0] == "pkcu-regression") return PkcuProbe.RunRegression(args[1..]);

// Depth-walker reflection leak (HCBR-2026-06-08-01): SELF-CONTAINED CI regression guard — synthesizes a CTDA
// condition whose arm carries a System.Type Parameter1Type, asserts a deep read renders it as one opaque token.
if (args.Length > 0 && args[0] == "depth-leak-guard") return DepthLeakProbe.RunGuard(args[1..]);

// Field-value query predicate (cross_plugin_query where=): SELF-CONTAINED CI regression guard — synthesizes records
// with known field values, asserts the evaluator's matched set == a brute-force reference + the Q3 teeth.
if (args.Length > 0 && args[0] == "value-predicate-guard") return ValuePredicateProbe.RunGuard(args[1..]);

// Winner-vs-source display (wishlist #8): SELF-CONTAINED CI regression guard — synthesizes a master + two overrides
// (B wins) and asserts RecordsIn pairs each yielded body with its OWN source plugin, the winner stream with the winner.
if (args.Length > 0 && args[0] == "source-display-guard") return SourceDisplayProbe.RunGuard(args[1..]);

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

// Wave 4 scout: recon Mutagen's IFormLinkOrIndex condition-target API — the form-vs-index discriminator (condition oracle), the wave-4 unknown.
if (args.Length > 0 && args[0] == "condition-probe") return ConditionProbe.RunConditionProbe(args[1..]);

// Wave 5 scout: ModHeader mutable-root reachability (the header is a singleton property, not a group/record).
if (args.Length > 0 && args[0] == "header-probe") return Wave5Probe.RunHeaderProbe(args[1..]);

// Wave 5 scout: the PEX read->write round-trip GATE (project_pex_prefer_source_policy) — the wave-5 unknown.
if (args.Length > 0 && args[0] == "pex-probe") return Wave5Probe.RunPexProbe(args[1..]);

// Coercion completeness guard: corpus-derived audit of every writable value-leaf's coercibility.
if (args.Length > 0 && args[0] == "coerce-audit") return WriteEngine.RunCoerceAudit(args[1..]);

// Coercion construction self-test: confirms each value-type rule builds a valid, assignable instance.
if (args.Length > 0 && args[0] == "coerce-selftest") return WriteEngine.RunCoerceSelftest(args[1..]);

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

// Launch-arc item 3 proof: derive the three load-order roots + active profile from ONE MO2 instance path (ModOrganizer.ini).
if (args.Length > 0 && args[0] == "mo2instance-probe") return Mo2InstanceProbe.RunProbe(args[1..]);

// Cleanup-gotcha / Option-B viability: prove a plain overlay LOCKS a plugin, Dispose() RELEASES it promptly, and open->read->dispose latency is invisible (de-risks the LOCKED Option-B fix).
if (args.Length > 0 && args[0] == "handle-probe") return HandleProbe.RunProbe(args[1..]);

// Cleanup-gotcha / Option-B AT-REST proof: drive the REAL product code (resolver Build -> read via session -> create via write path) on temp copies and assert files are renamable at rest (zero handles held).
if (args.Length > 0 && args[0] == "atrest-probe") return AtRestProbe.RunProbe(args[1..]);

// Active-patch write self-lock (Heisen bug 2026-06-08): EXPLORATORY — map the Windows file-sharing semantics of writing
// into a patch whose own overlay is held by AllMasters() (direct vs temp+Replace vs release-then-write). Decides the fix.
if (args.Length > 0 && args[0] == "writelock-probe") return WriteLockProbe.RunProbe(args[1..]);

// Active-patch write self-lock (Heisen bug 2026-06-08): SELF-CONTAINED CI regression guard — drives a real product write
// (RemoveRecords + the Apply winner-fetch path) into an ACTIVE patch, asserts success (a control proves the lock reproduces).
if (args.Length > 0 && args[0] == "writelock-guard") return WriteLockProbe.RunGuard(args[1..]);

// Active-patch write self-lock follow-up (PR #24 review): EXPLORATORY — prove Apply's Phase-1 winner-fetch opens a SECOND
// overlay on the target (when re-editing an own override) that survives AllMastersExcept and still self-locks the serialize.
if (args.Length > 0 && args[0] == "writelock-apply-probe") return WriteLockProbe.RunApplyResidualProbe(args[1..]);

// Active-patch write self-lock follow-up (PR #24 review #2): REAL-DATA proof that a NESTED record (PlacedObject, via the
// link-cache context path) survives the re-edit-own-override case under the new "release overlay before serialize" invariant.
if (args.Length > 0 && args[0] == "writelock-nested-proof") return WriteLockProbe.RunNestedProof(args[1..]);

// External-tool bridge (step 1) proof: the pure core pieces housecarl_set_tool_path + the riders ride — shared-config
// clobber-safety, path validation, the missing-dependency forcing prompt, and canonical-home auto-detect.
if (args.Length > 0 && args[0] == "tool-bridge") return ToolBridgeProbe.Run(args[1..]);

// External-tool bridge (step 2) proof: the compile rider's stderr parser + a real .psc → .pex against the CK compiler.
if (args.Length > 0 && args[0] == "compile-probe") return CompileProbe.Run(args[1..]);

// External-tool bridge (step 3) proof: the BSA riders' list→unpack→pack→re-list round-trip against real BSArch.
if (args.Length > 0 && args[0] == "bsa-probe") return BsaProbe.Run(args[1..]);

var outputDir = Path.GetFullPath(args.Length > 0 ? args[0] : "generated");
// The slim reference tree ships INSIDE the skill (tracked); corpus.json + summary stay in generated/.
// Default assumes the generator is run from the repo root (as `dotnet run --project src/housecarl-generator`).
var refDir = Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine(".claude", "skills", "mutagen-reference", "references"));
return CorpusGenerator.GenerateAll(outputDir, refDir);
