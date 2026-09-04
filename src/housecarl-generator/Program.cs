using HousecarlGenerator;

// houseCARL build-time schema generator.
//
// Reflects over the entire Mutagen.Bethesda.Skyrim type universe and emits a flat
// type catalog (corpus.json + corpus.summary.md) covering literally every modeled
// type at full depth. Pure reflection over types — no plugin file required.
//
// Usage:  dotnet run --project src/housecarl-generator [outputDir]   (default: ./generated)
//         An unrecognised FIRST argument is refused, not read as [outputDir] — so an output directory must be
//         rooted, carry a separator, or be "." / "..". Anything else is a mode name, and an unknown one exits 2.

// Run EVERY CI probe in ONE process: the big Mutagen assembly loads and JITs once, and the schema corpus is
// reflected once via CorpusGenerator's memoize, instead of once per probe. See CiAll.
if (args.Length > 0 && args[0] == "ci-all") return CiAll.RunAll(args[1..]);

// Single-probe runs of any CI guard — roster or standalone — dispatch through the reflected [CiProbe] set, so a
// guard cannot be runnable locally yet missing from the CI run. Only the manual/exploratory probes below keep
// their own explicit dispatch.
if (args.Length > 0 && CiAll.TryDispatch(args[0], args[1..], out var ciRc)) return ciRc;

// The old copy_npc_appearance verb against its 2.0 successor, over constructed MO2 instances. Deliberately NOT
// in ci-all: it is evidence re-run on demand after a change to the copy path, not a standing guard.
if (args.Length > 0 && args[0] == "copy-differential") return CopyDifferentialHarness.Run(args[1..]);

// #459 measurement: is the containing parent in hand during the flat index walk, and what does a containment-aware
// pass cost on a real order. Needs a live MO2 instance (or --plugin), so it is a manual harness, not a CI probe.
if (args.Length > 0 && args[0] == "parent-in-hand") return ParentInHandProbe.Run(args[1..]);

// Maintenance diagnostic: re-verify the mutable-collection whitelist on a Mutagen bump.
if (args.Length > 0 && args[0] == "vocab") return Probe.RunVocab();

// SkyPatcher harness: the whole-layer scan + INI-vs-INI conflict report off a LIVE MO2 instance, rendered
// exactly as housecarl_skypatcher_layer returns it.
if (args.Length > 0 && args[0] == "skypatcher-layer") return SkyPatcherHarness.RunLayer(args[1..]);

// Index-build resilience: is group enumeration resumable past a parse throw?
if (args.Length > 0 && args[0] == "pkcu-probe") return PkcuProbe.Run(args[1..]);

// Index-build resilience: end-to-end proof a malformed plugin is isolated, not fatal.
if (args.Length > 0 && args[0] == "pkcu-fix-proof") return PkcuProbe.RunFixProof(args[1..]);

// Index-build resilience at real scale: full MO2 order + 1 malformed plugin, only it excluded.
if (args.Length > 0 && args[0] == "pkcu-scale-proof") return PkcuProbe.RunScaleProof(args[1..]);

// Emit vanilla-class-parents.json from the CK vanilla sources' own ScriptName-extends headers. Committed asset:
// vanilla sources do not exist on CI, so regenerate by hand on a game update.
if (args.Length > 0 && args[0] == "class-parents") return ClassParentsEmitter.Run(args[1..]);

// Localized-strings read: a DLC master resolved to a strings-less mod folder reads Name EMPTY.
if (args.Length > 0 && args[0] == "strings-resolve-probe") return StringsResolveProbe.Run(args[1..]);

// EXPLORATORY: the MUTABLE strings-aware open + the mutate-then-WriteInPlace round-trip.
if (args.Length > 0 && args[0] == "repoint-strings-probe") return RepointStringsProbe.Run(args[1..]);
if (args.Length > 0 && args[0] == "localized-write-probe") return LocalizedWriteProbe.Run(args[1..]);
if (args.Length > 0 && args[0] == "localized-shape-sweep") return LocalizedShapeSweep.Run(args[1..]);

// EXPLORATORY: the disabled-mod asset source lane's two binding measurements.
if (args.Length > 0 && args[0] == "f1-measure") return F1MeasureProbe.Run(args[1..]);

// EXPLORATORY: does the successor reach a FaceGen beside the SECOND donor-side plugin?
if (args.Length > 0 && args[0] == "f1-i14") return F1MeasureProbe.RunI14(args[1..]);

// One-shot verify: confirm the xEdit 4-char signature reflection path.
if (args.Length > 0 && args[0] == "sig") return Probe.RunSig();

// Write engine: discovery of Mutagen's generic group-override surface.
if (args.Length > 0 && args[0] == "write-api") return WriteDiagnosticsProbe.RunDiscovery(args[1..]);

// Write engine: NPC-skills-by-name acceptance proof (nested dict-in-substruct Set).
if (args.Length > 0 && args[0] == "npc-skills") return WriteDiagnosticsProbe.RunNpcSkillsProof(args[1..]);

// Oracle: per-kind byte-identical cells (Path A engine vs Path B hand-written setter).
if (args.Length > 0 && args[0] == "oracle") return WriteOracle.Run(args[1..]);

// Confirm the polymorphic arm-swap instantiation mechanism.
if (args.Length > 0 && args[0] == "poly-probe") return WriteDiagnosticsProbe.RunPolyProbe(args[1..]);

// Absent-substruct characterization: which navigate-into substructs lack a parameterless ctor (the tricky shapes).
if (args.Length > 0 && args[0] == "substruct-probe") return WriteDiagnosticsProbe.RunSubstructProbe(args[1..]);

// Recon Mutagen's nested-group override API (Cell/Placed*/INFO/Navmesh/Landscape).
if (args.Length > 0 && args[0] == "nested-probe") return NestedProbe.RunNestedProbe(args[1..]);

// Can houseCARL ALLOCATE a brand-new record INTO a nested parent? Tests DuplicateIntoAsNewRecord (clone-a-sibling)
// and construct-and-Add-into-collection (new parent), the FormID floor, and the coordinate-keyed cell seam.
// Throwaway recon; each shape fails loud rather than degrading.
if (args.Length > 0 && args[0] == "nested-create-probe") return NestedCreateProbe.RunProbe(args[1..]);

// Nested-create build proof: drive the REAL WritePatchBuilder.CreateRecords nested path — one-shot topic+INFO,
// INFO into an existing topic, Placed into a cell (named collection), plus the rejects (no-parent, bad parent,
// ambiguous collection, forward sibling). Re-opens each patch from disk; Skyrim.esm byte-checked.
if (args.Length > 0 && args[0] == "nested-create-proof") return NestedCreateProof.RunProof(args[1..]);

// The COORDINATE-KEYED cell seam (exterior/interior Cell + Placed-into-new-cell): round-trips a constructed cell
// into find-or-built WorldspaceBlock/SubBlock (exterior, floor(grid/32|8)) and CellBlock/SubBlock (interior,
// FormID digits), checks the override is thin, the block math against vanilla, OFST regen, source byte-unchanged.
if (args.Length > 0 && args[0] == "coord-cell-probe") return CoordCellProbe.RunProbe(args[1..]);

// Recon Mutagen's IFormLinkOrIndex condition-target API — the form-vs-index discriminator.
if (args.Length > 0 && args[0] == "condition-probe") return ConditionProbe.RunConditionProbe(args[1..]);

// ModHeader mutable-root reachability — the header is a singleton property, not a group/record.
if (args.Length > 0 && args[0] == "header-probe") return Wave5Probe.RunHeaderProbe(args[1..]);

// The PEX read-to-write round-trip gate.
if (args.Length > 0 && args[0] == "pex-probe") return Wave5Probe.RunPexProbe(args[1..]);

// coerce-audit and coerce-selftest carry [CiProbe] and dispatch via CiAll.TryDispatch at the top of this file,
// so they get no separate dispatch here.

// Write-surface census: corpus-derived reachability map of every writable leaf.
if (args.Length > 0 && args[0] == "write-census") return WriteCensus.Run(args[1..]);

// The only-target-moved differ + byte-true drive across the reachable settable surface.
if (args.Length > 0 && args[0] == "write-proof") return WriteProof.RunProof(args[1..]);

// Real-patch dev harness: one concrete set_field to a real .esp, checked in xEdit by hand.
if (args.Length > 0 && args[0] == "patch") return WriteEngine.RunPatch(args[1..]);

// Read-to-plan: resolve a record + print its fields/keywords (the minimum read to author a correct write).
if (args.Length > 0 && args[0] == "show") return WriteEngine.RunShow(args[1..]);

// Read surface: resolve a record and emit its fields as round-trippable tokens (inverse of Coerce).
if (args.Length > 0 && args[0] == "read") return ReadEngine.RunRead(args[1..]);

// Read-proof: read each value leaf, write the token back, assert no-op.
if (args.Length > 0 && args[0] == "read-proof") return WriteProof.RunReadProof(args[1..]);

// Re-target one real condition target to an .esp, with the new target checked in xEdit by hand.
if (args.Length > 0 && args[0] == "condition-patch") return WriteEngine.RunConditionPatch(args[1..]);

// Measure on-demand against held-index load-order resolution cost.
if (args.Length > 0 && args[0] == "resolve-probe") return ResolveProbe.RunResolveProbe(args[1..]);

// Measure the cost of resolving a conflict tree's bodies on demand.
if (args.Length > 0 && args[0] == "body-fetch-probe") return BodyFetchProbe.RunBodyFetchProbe(args[1..]);

// Stand up and verify LoadOrderResolver: held RAM, tree correctness, body-fetch timing, freshness sweep.
if (args.Length > 0 && args[0] == "resolve") return ResolveHarness.RunResolve(args[1..]);

// Prove the MULTI-MASTER write path: a real merge patch (leveled list + cross-master entries) to an .esp.
if (args.Length > 0 && args[0] == "multimaster-patch") return MultiMasterProof.RunMultiMasterPatch(args[1..]);

// Prove the PUBLIC write cleave the set_field/bulk_apply/into= tools call (flat, multi, extend, cross-master, reject).
if (args.Length > 0 && args[0] == "apply-proof") return ApplyProof.RunApplyProof(args[1..]);

// Verify the TRUE active order read from MO2's static profile files (loadorder.txt + modlist.txt + plugins.txt).
if (args.Length > 0 && args[0] == "mo2-order") return Mo2OrderHarness.RunMo2Order(args[1..]);

// Remove + Create recon: AddNew/FormID allocation, and write-path master derivation (clean-masters).
if (args.Length > 0 && args[0] == "remove-create-probe") return RemoveCreateProbe.RunProbe(args[1..]);

// Remove-record recon: whole-record removal via mod.Remove(FormKey) — flat, nested, and not-found semantics.
if (args.Length > 0 && args[0] == "remove-record-probe") return RemoveRecordProbe.RunProbe(args[1..]);

// Drive WritePatchBuilder.RemoveRecords, the core housecarl_remove_record calls, against a real, large load order.
if (args.Length > 0 && args[0] == "remove-proof") return RemoveProof.RunRemoveProof(args[1..]);

// Create recon: generic AddNew dispatch, FormID allocation, fields-via-ApplyVerb, and the nested/abstract-T scope fork.
if (args.Length > 0 && args[0] == "create-probe") return CreateProbe.RunProbe(args[1..]);

// Drive WritePatchBuilder.CreateRecords, the core housecarl_create_record calls, against a real, large load order.
if (args.Length > 0 && args[0] == "create-proof") return CreateProof.RunCreateProof(args[1..]);

// How to FORCE Skyrim.esm onto every written plugin, given that Mutagen strips unreferenced masters.
if (args.Length > 0 && args[0] == "master-probe") return MasterProbe.RunProbe(args[1..]);

// Prove a plain overlay LOCKS a plugin, that Dispose() releases it promptly, and that open-read-dispose latency is invisible.
if (args.Length > 0 && args[0] == "handle-probe") return HandleProbe.RunProbe(args[1..]);

// Drive the REAL product code (resolver Build, read via session, create via write path) on temp copies and assert the files are renamable at rest, i.e. no handles held.
if (args.Length > 0 && args[0] == "atrest-probe") return AtRestProbe.RunProbe(args[1..]);

// Active-patch write self-lock, EXPLORATORY: map the Windows file-sharing semantics of writing into a patch whose
// own overlay is held by AllMasters() — direct, temp+Replace, and release-then-write.
if (args.Length > 0 && args[0] == "writelock-probe") return WriteLockProbe.RunProbe(args[1..]);

// Active-patch write self-lock, EXPLORATORY: prove Apply's winner-fetch opens a SECOND overlay on the target when
// re-editing an own override, one that survives AllMastersExcept and still self-locks the serialize.
if (args.Length > 0 && args[0] == "writelock-apply-probe") return WriteLockProbe.RunApplyResidualProbe(args[1..]);

// REAL-DATA proof that a NESTED record (PlacedObject, via the link-cache context path) survives the
// re-edit-own-override case under the "release overlay before serialize" invariant.
if (args.Length > 0 && args[0] == "writelock-nested-proof") return WriteLockProbe.RunNestedProof(args[1..]);

// REAL-DATA proof of the NESTED own-override re-edit IN PLACE (the LinkCacheFor-on-a-foreign-target overlay path),
// the one arm the self-contained guard cannot synthesize. Needs Skyrim.esm; self-skips on the runner.
if (args.Length > 0 && args[0] == "inplace-nested-proof") return InPlaceProbe.RunNestedProof(args[1..]);

// REAL-DATA proof of the NESTED own-override REMOVE IN PLACE (typed nested Remove plus a real-data WriteInPlace
// re-serialize on a foreign target) — the remove counterpart of inplace-nested-proof. Needs Skyrim.esm; self-skips.
if (args.Length > 0 && args[0] == "inplace-remove-nested-proof") return InPlaceProbe.RunRemoveNestedProof(args[1..]);

// Perk references= crash, DIAGNOSIS: run Mutagen's EnumerateFormLinks over every PERK in a real plugin and report
// which records throw and with what. Skips without Skyrim.esm.
if (args.Length > 0 && args[0] == "perk-refs-diagnose") return PerkRefsProbe.RunDiagnose(args[1..]);

// Perk references= crash, REAL-DATA proof: the failing call (type=Perk references=) over a live MO2 order through
// the service layer. Manual; needs --mo2 and --corpus, and skips without them.
if (args.Length > 0 && args[0] == "perk-refs-proof") return PerkRefsProbe.RunProof(args[1..]);

// Conflict-tree content diff, REAL-DATA proof: the "(identical to winner)" false ITM over a live MO2 order.
// Manual; needs --mo2, and skips without it.
if (args.Length > 0 && args[0] == "conflict-diff-proof") return ConflictDiffProbe.RunProof(args[1..]);

// FormID allocation floor, EXPLORATORY: pin the Mutagen NextFormID semantics — fresh-mod init, the Iterate
// serialize recompute that seeds 0, CreateFromBinary rehydration, AddNew-from-0.
if (args.Length > 0 && args[0] == "formid-floor-probe") return FormIdFloorProbe.RunProbe(args[1..]);

// ESL / FE-space FormID handling, EXPLORATORY: pin the referenced Mutagen version's small-master semantics —
// legal object-ID range, IsSmallMaster to FE-space encode, FE decode round-trip, flag-tracking, index-independence.
if (args.Length > 0 && args[0] == "esl-formid-probe") return EslFormIdProbe.RunProbe(args[1..]);

// ESL ground-truth scan, EXPLORATORY: raw-byte scan of REAL plugins to settle whether SSE stores light-master
// references in FE-space on disk (0xFE high byte) or by master-list index.
if (args.Length > 0 && args[0] == "esl-real-scan") return EslFormIdProbe.RunRealScan(args[1..]);

// MANUAL/REAL-DATA probe: no-op re-serialize a sample of REAL plugins (counter-preserving, the correct in-place
// shape) and measure the whole-plugin byte divergence surface — identical, header-only, body, records-changed,
// unloadable — so the round-trip accept/refuse threshold is set from measured reality. Needs --mo2 <instance> and
// SKIPs without one, because a synthetic fixture round-trips clean and would reveal nothing. Writes only to temp;
// read-only on the load order.
if (args.Length > 0 && args[0] == "roundtrip-probe") return RoundTripProbe.RunProbe(args[1..]);

// Compact/merge, EXPLORATORY: settle, self-contained, whether RemapLinks changes a record's OWN identity or only
// its references, whether MajorRecord.FormKey is settable, and which Mutagen affordance compact must use to
// renumber a record into the ESL range.
if (args.Length > 0 && args[0] == "remap-wave1-mech") return RemapWave1Probe.RunMechanism(args[1..]);

// The self-contained compact/merge gate is a [CiProbe] guard, `remap-wave1-guard`, dispatched by CiAll.TryDispatch
// above, so it is not listed here.

// Compact/merge real-data run, MANUAL: ESL-compact a real plugin to a NEW one for checking in xEdit, and time the
// identify-pass over the live order. Needs --mo2 <inst> --plugin <Name.esp>, and SKIPs without them.
if (args.Length > 0 && args[0] == "remap-wave1-real") return RemapWave1Probe.RunReal(args[1..]);

// Compact/merge, EXPLORATORY: settle, self-contained, whether the recursive Duplicate-and-replace renumber closes
// the nested-record gap (cell/worldspace/topic children renumber and round-trip on disk), and whether
// IMajorRecordGetterEnumerable is the recurse discriminator.
if (args.Length > 0 && args[0] == "remap-wave2-nested-mech") return RemapWave2NestedMechProbe.RunMechanism(args[1..]);

// MANUAL real-data harness: run housecarl_skse_inventory against a live MO2 instance and print the render and
// timing. The CI skse-reader-guard pins the decode; this covers the full inventory.
if (args.Length > 0 && args[0] == "skse-inventory-real") return SkseInventoryProbe.RunReal(args[1..]);

// The SKSE static peek needs no manual harness of its own — skse-inventory-real --peek --filter <dll> drives it.

// SKSE config audit, MANUAL real-data harness: the whole audit against a live MO2 instance, with timing.
if (args.Length > 0 && args[0] == "skse-config-audit-real") return SkseConfigAuditProbe.RunReal(args[1..]);

// Native-function pairing audit, MANUAL real-data harness: the whole audit against a live MO2 instance, with timing.
if (args.Length > 0 && args[0] == "native-pairing-real") return NativePairingProbe.RunReal(args[1..]);

// UNKNOWN MODE — refused, never silently taken as an output directory. Every dispatch above matched nothing, and
// the corpus fallthrough below reads args[0] as the OUTPUT DIRECTORY, so a mistyped probe name would generate the
// whole corpus into a folder of that name and exit 0. A mode and a directory are distinguishable — a directory is
// ROOTED or carries a SEPARATOR (or is "." / "..") — so anything else is refused by name, with the real modes
// listed and near misses suggested. See IsDirectoryArgument for why "…or it already exists" is not a third clause.
if (args.Length > 0 && !IsDirectoryArgument(args[0]))
{
    Console.Error.WriteLine($"unknown mode '{args[0]}' — nothing was generated and nothing was written.");
    // TrimStart, then skip an EMPTY suggestion entirely: DidYouMean returns "" when nothing is close, and a blank
    // line above the mode list reads like a truncated message.
    var guardVerbs = CiAll.ProbeNames.Concat(CiAll.StandaloneProbeNames)
                                     .OrderBy(n => n, StringComparer.Ordinal).ToArray();
    if (HousecarlCore.PluginNameSuggest.DidYouMean(args[0], guardVerbs).TrimStart(' ') is { Length: > 0 } near)
        Console.Error.WriteLine(near);
    Console.Error.WriteLine();
    Console.Error.WriteLine("CI guards (`ci-all` runs the roster; a [standalone] verb is a CI step of its own):");
    foreach (var name in guardVerbs)
        Console.Error.WriteLine("  " + name + (CiAll.StandaloneProbeNames.Contains(name) ? "  [standalone]" : ""));
    Console.Error.WriteLine();
    Console.Error.WriteLine("Other modes are the manual/exploratory harnesses declared in src/housecarl-generator/Program.cs");
    Console.Error.WriteLine("(they are not in the suggestion pool above — only the CI guards are).");
    // State the RULE, not just the intent: "pass a directory path" alone is advice a caller who typed one has
    // already followed, and the accepted spellings are not guessable.
    Console.Error.WriteLine("To GENERATE the corpus into a directory, pass a path that is ROOTED (C:\\…), carries a");
    Console.Error.WriteLine("SEPARATOR (./out), or is \".\" / \"..\" — a BARE name is always read as a mode, even if a");
    Console.Error.WriteLine("folder of that name exists (that clause is what let a mistyped mode write into ./plugin).");
    Console.Error.WriteLine("With no argument at all it generates into ./generated.");
    return 2;
}

// A mode name is a BARE token; an output directory is rooted or carries a separator.
//
// Do not add "…or it already exists as a directory" as a third clause: `plugin`, `src`, `scripts`, `standards`,
// `release` and `generated` are all real folders at the repo root, so a mistyped mode colliding with one would
// generate the whole corpus into it. An output directory can always be spelled with a separator; a mistyped mode
// cannot be spelled back.
static bool IsDirectoryArgument(string a)
{
    if (a.Length == 0) return false;
    return Path.IsPathRooted(a) || a.Contains('\\') || a.Contains('/') || a is "." or "..";
}

var outputDir = Path.GetFullPath(args.Length > 0 ? args[0] : "generated");
// The slim reference tree ships INSIDE the skill (tracked); corpus.json + summary stay in generated/.
// Default assumes the generator is run from the repo root (as `dotnet run --project src/housecarl-generator`).
var refDir = Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine(".claude", "skills", "mutagen-reference", "references"));
return CorpusGenerator.GenerateAll(outputDir, refDir);
