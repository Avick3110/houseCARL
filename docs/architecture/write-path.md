---
updated: 2026-09-22
covers: [src/housecarl-mcp/RecordWrites.cs, src/housecarl-mcp/WriteSentences.cs, src/housecarl-core/WriteEngine.cs]
---
# The write path, service side

## What it is
The service half of every write verb: from a call's parsed ops to a plugin on disk, and the one catalogue the
resulting prose is rendered from. Every lane writes a new plugin by default: apply, create, remove and forward also
take the `in_place=true` opt-in described below; closure copy, merge and create_plugin have no in-place route at all;
and compact can overwrite its source, but under its own per-call confirm rather than the persistent handshake.
Pre-flight belongs to [`corpus-rulebook.md`](corpus-rulebook.md); where a write lands belongs to
[`output-and-artifacts.md`](output-and-artifacts.md). Neither is repeated here.

## Contracts
- `in_place=true` requires `target=`, is mutually exclusive with `into=` / `patch=`, and `target=` without
  `in_place` is refused by name rather than ignored, on apply, create, remove and forward alike.
- An in-place `target=` is resolved to an on-disk path by plugin filename through the load order, and a name that
  is not an active plugin is refused; a coincidentally-named folder is never a target.
- Consent is a persistent first-touch handshake keyed off the resolved path, shared by the edit, create, remove and
  forward lanes, so acknowledging a plugin once covers all four. `acknowledge=true` waives the consent axis only,
  never a post-write verify.
- Compact is outside that handshake: an in-place compaction re-confirms per call with its own overwrite list, so a
  prior acknowledgement of that plugin never authorizes one.
- The acknowledgement is recorded only after the write has landed. A refused call records nothing, so the prompt
  says it is shown until an in-place write LANDS rather than calling itself one-time, and its file claim is
  direction-neutral.
- A dry run neither needs nor records consent, and stamps nothing: no marker, no `.seq` note.
- An in-place write stamps `[houseCARL] editedInPlace=<ISO>` into the target mod's `meta.ini` and never
  `generated=true`, so the user's mod keeps failing the ownership gate and a later `into=` cannot overwrite it. Only
  for a mod folder under ModsDir.
- A localized target is refused by each lane's own pre-flight BEFORE the consent gate, in that lane's words, with the
  file untouched. The unwritable-parent probe answers AFTER the gate and before anything is staged, so a first touch
  of a read-only folder meets the consent prompt first.
- Side-effect notes after a landed write — consent, the marker, a now-stale `.seq` — are best-effort; none can fail
  the write, and the engine's own note is joined first so it survives the merge.
- One write at a time: `_writeGate` is held from resolve through commit, while argument parsing and op mapping run
  outside it, so a malformed call never queues behind a real write.
- Every response from the four record lanes — apply, create, remove, forward — decided after a capture carries that
  build's epoch: success, refusal, dry run and consent prompt alike, and an outcome that consulted no build carries
  none. Only their four outcomes hold a `Stamp`; compact, merge, copy and create_plugin capture a view but render no
  epoch at all.
- A write response states only what it re-read from the written FILE: the file's value, `not-checked` where the file
  could not answer, and a did-not-land verdict only off a walk that succeeded. Never the applied in-memory value.
  An opaque `bytes` leaf re-reads as a byte count with its structure NOT checked.
- A read-back proves what is in the file, never what wins in the ORDER.
- A walk's source universe is the caller's pole list in order, resolved first-hit-wins, with no separate single-pole
  path: a length-1 list is the same loop running once.
- An off-order source — a `CopyFrom` source, a `forward` source, a walk pole — is located by the on-disk locate
  every other lane uses, and its overlay is handed back OPEN because bodies are deep-copied during the serialize; the
  caller disposes it only after the write returns.
- A caller's PATH that names the order's own copy of an active plugin is re-spelled to that plugin's name before
  anything keys on it, so one rewrite reaches the arm decision, the winner comparison and every rendered sentence.
- A refused write removes a mod folder it created this call, gated on that folder holding nothing but our own
  `meta.ini` and an empty staging directory.
- Every folder allocation is serialized on one gate, because the check-then-create of a unique stem is race-free
  only then.
- The fresh-patch remedy arguments pass through to the extend refusals, so the calling operation states how, or
  whether, its own fresh-write path works. Both default to claiming nothing, so a lane added later cannot inherit a
  sentence that is false for it.
- The sentence catalogue: every user-facing write sentence has ONE source in `WriteSentences`, because each outcome
  renders twice (text and json). `Twins` holds what BOTH transports must state; a sentence that is prose on one
  transport by design stays on the outer class.
- Every catalogue const DECIDES: `[MustState]` phrases that must still appear in it, or `[NoClaims]` with a stated
  reason. An undecorated const fails by name rather than going unchecked, and a phrase is the claim, not the topic.
- The engine is blind to which record it edits, so coverage is a property of Mutagen's model; a path it cannot
  navigate or a value it cannot coerce is refused by name, never skipped.
- `ApplyVerb` is public and runs no rulebook check, so pre-flight is the CALLER's: a direct or CLI caller that skips
  it mutates unvalidated, including record identity, which is settable on every concrete Mutagen record. What
  pre-flight decides, and which recognisers it shares with apply, is [`corpus-rulebook.md`](corpus-rulebook.md)'s.
- Flat-versus-nested is ONE decision — does a flat `SkyrimGroup<T>` match — off one `EnumerateFlatGroups`
  enumeration the override, create, removal-type and seek-type lanes all derive from; a nested record needs the
  source link cache to rebuild its parent chain and fails loud without it.
- A flat group typed by an abstract base is created by naming a concrete arm, discovered from the runtime hierarchy;
  the bare base is refused with the discovered arms listed, never a guessed default.
- Create is idempotent through upsert: a record the patch ITSELF defines is replaced at the same FormKey, while a
  carried override, duplicate residue and a cross-type editorid collision are each refused rather than absorbed.
- A replace is never silent: the upsert returns whether an existing record was replaced and the caller MUST surface
  that, because a replace discards the prior record's state, including any field edits made since the original create.
- An owned child is created into a parent's modeled slot, collection or singular, and an occupied singular slot is
  refused before any FormID is allocated; a cell is filed by coordinate instead, through derived block arithmetic.
- Every allocation floors the patch's `HEDR.NextObjectID` at 0x800 and past every record the patch defines, and
  every patch write persists that counter verbatim; an in-place write floors nothing and keeps the author's own.
- A patch write is handed the whole load order to resolve and sort masters while Mutagen derives the lean list from
  the records' own links, and force-includes Skyrim.esm + Update.esm filtered to the ones that order carries; an
  in-place write force-includes nothing and declares only the target's own masters.
- Every write stages into a `.housecarl-tmp` sibling of the target and commits through `AtomicFile.Commit`, so the
  target only ever holds the old or the new complete file; the caller must release every handle it holds on the
  target first, and a failed stage leaves nothing behind.
- A localized plugin carries only integer indices into sibling `.STRINGS` tables, and a write commits the PLUGIN
  alone, so a rewrite's renumbered indices would be read against the old tables and values would land on records
  they do not belong to — which is why no arrangement of those tables is rewritten in place.
- Both write choke points refuse a localized target off the mod in memory and nothing else, before the staging
  directory exists; the re-read of the destination supplies the sentence, never the decision.
- A serialize-boundary `NullReferenceException` — bare, or wrapped in the parallel writer's aggregate, and only when
  every leaf's root is one — is re-stamped as a named null-arm refusal; any other serialize error keeps its own type
  and message.
- A forward that replaces a FormKey the destination already carries lifts that record's owned child records off
  before the drop and re-attaches them after the copy, and refuses rather than choosing when the copy arrives
  carrying children of its own or when the counts disagree afterwards.
- The child-bearing property set is reflected RECURSIVELY and links are cut, so a container two levels down is
  reached; the depth bound is a tripwire rather than a correctness assumption, because a deeper nesting drops the
  property out of the set and the count check refuses.
- Apply dispatches at the leaf on its RUNTIME shape — whole-coercible, dict, list, scalar — and materializes an
  absent intermediate substruct or collection so a first write into it works; a `Remove` on an absent collection
  refuses instead, before anything is materialized.
- A compose builds from parts through one primitive: the constructor its own fields satisfy is the one invoked, and
  its nested `sets` replay through `ApplyVerb` itself, so a built struct cannot miss a field kind apply handles.
- A compose given nothing at all is refused, because the object built from nothing serializes to zero bytes and the
  write would otherwise report a change the file does not carry.
- Each list index verb keeps its OWN bound — overwrite and remove-by-index address an element that exists, insert
  addresses a gap and admits the append slot — checked against the live length as an expected rejection.
- Every `Add` reports whether the list already carried what it appended, and a composed batch counts an element the
  list held before the write apart from a repeat within the batch.
- A flags-enum `Add` / `Remove` is a bit operation on the leaf's current value, so one flag flips without the caller
  re-listing the others; a VALUELESS `Remove` is the whole-field clear instead.
- `Remove` on a required FormLink fails loud rather than writing an empty link; on a nullable one it clears to the
  empty link, identically to a null-synonym `Set`.
- A condition `FormLinkOrIndex` is set through the parent-aware branch, which infers form-versus-index from the
  value and sets the owning arm's discriminator to match.
- The gendered `[0]` / `[1]` alias maps to the pair's named arms through the same materialize-and-write-back the
  named hop uses, off one index→arm mapping the read render also reads, so a fresh arm is never an orphan.
- Coercion recognition and conversion are one family: passing no text makes each rule a pure recogniser, so
  `CanCoerce` and `Coerce` cannot disagree about what is coercible.
- Three apply-time refusal categories stay distinct: a live-state rejection the schema-only gate cannot see, the
  target record's own malformed data, and a genuine gate/apply inconsistency.
- A localized refusal renders one sentence per ARRANGEMENT and the decision may collapse while the words may not,
  so the unreadable arm claims no localization state at all.
- The copy-from and off-order-forward source lanes take their own capture and the engine captures again; a write
  pins one resolver whose name table is never rebuilt, so the two captures cannot disagree about membership.
- Off-order-ness is decided by `WritePatchBuilder.IsOffOrderCopySource`, the one predicate the engine consumes
  through.
- The core note the four in-place lanes join first is the master-grow re-sort warning, emitted by
  `WritePatchBuilder`, not by the engine or the service.
- The in-place write's localized backstop names no lane, which is why each service lane pre-flights localization
  itself.
- `RemapEngine.LocalizedAmong` fails closed on a referencer it could not open, which is what forces the two-class
  split in `SplitBlockedReferencers`.

## Pinned by
- `inplace-guard` arms E / L / U — the `in_place`⇔`target=` contract and the `into=` / `patch=` exclusion, on the
  edit, create and remove lanes; `forward-from-plugin-guard`'s INPLACE-CONTRACT arm for the forward lane's three
  halves.
- `inplace-guard` arms F / V — a target that is not an active plugin is refused.
- `inplace-guard` arms G / K / W — the handshake refuses, then writes under `acknowledge=true`, does not re-prompt,
  persists, and one acknowledgement covers the edit, create and remove lanes.
- `inplace-guard` arms CO-A–CO-D — a refused in-place write spends no consent; CO-E / CO-F — one that lands records
  it; CO-G — the prompt states when it stops and keeps its file claim direction-neutral.
- `inplace-guard` arm I — the marker is stamped, `generated=true` is not, and a later `into=` on that folder is
  still refused.
- `inplace-guard` arms LOC-A–LOC-J — the localized pre-flight answers before consent in each lane's own words, file
  untouched and no consent spent, on the real call and the dry run alike.
- `apply-guard` arm 5 — the APPLY lane's render carries the epoch on both transports, on success, on a json refusal
  and on the consent prompt; `DegradedOrderMarkerTests.TheWriteLaneCarriesTheClauseBesideItsStamp` renders a DRY RUN
  and asserts the degraded clause that rides beside the stamp. The other three record lanes are unpinned for it.
- `WriteEditLineSourceTests.ThePerEditLinePrintsTheFileValueNotTheAppliedOne`,
  `…AnOpTheFileCouldNotAnswerForIsNotCheckedRatherThanTheAppliedValue`,
  `…ARecordMissingFromTheWrittenFileIsSaidOutright` and `…AFailedWalkIsNotCheckedRatherThanAVerdict` — the W0 rule's
  four readings.
- `OpaqueBytesVerifyTests.TheVerifySentenceNamesTheOpaqueFieldItReReadAsBytesOnly` — the opaque-leaf caveat.
- `write-surface-guard` — every `WriteSentences` const decides and still states its declared phrases, every `Twins`
  member is rendered by both lanes, and every outer `[MustState]` sentence reaches a render.
- `formid-floor-guard` — the 0x800 floor before an allocation and the in-memory counter persisted verbatim by the
  serialize.
- `atomic-commit-guard` arms A / B / C / C2 — the staged commit lands a fresh file, replaces an existing one
  byte-exact, and throws with the prior target intact when the source is missing or the target is held.
- `localized-write-guard` — the in-place refusal over every arrangement, each named accurately, the plugin and its
  tables byte-untouched, and a destination that cannot be classified refusing rather than reading as not-localized.
  It also pins what `RemedyFor` does with a lane clause passed IN, but not that the engine's own refusal carries
  none, so the backstop naming no lane is unpinned.
- `nullarm-guard` part B — a composed record missing a required arm surfaces as the named null-arm refusal, bare or
  aggregate-wrapped, with nothing on disk.
- `gendered-nav-guard` — the `[0]` / `[1]` alias navigates and writes an absent pair or arm back through the
  named-hop setter, off the mapping the read render shares.
- `insert-at-index-guard` — insert's append-inclusive bound, and a tail that keeps the same objects in the same
  order through a real serialize and re-read.
- `flags-bit-verb-guard` — a flags `Add` / `Remove` flips one bit and preserves every unlisted one, gate and apply
  keyed off the same test.
- `formlink-remove-guard` — `Remove` clears a nullable FormLink instead of throwing, and fails loud on a required
  one when pre-flight is bypassed.
- `subclass-remove-guard` — `RemovalTypeFor` routes the typed remove through the flat group's `T`, so a record whose
  concrete class is a subclass of it is really removed rather than silently skipped.
- `upsert-guard` arms RERUN / OVERRIDE / CROSS-TYPE / DUP — the replace at a stable FormKey, every replace surfaced
  on the outcome rather than silently, and the three collisions refused loud with the file untouched.
- `create-abstract-group-guard` arms G1 / G2 — a concrete arm of either abstract group creates, keyed off the
  runtime hierarchy rather than a per-type case.
- `nested-create-guard` and `coord-cell-guard` arms EXTERIOR / INTERIOR / PLACED — the modeled-slot nested create,
  and the coordinate-keyed cell routes through a real serialize and re-open.
- `OwnedChildLifecycleTests.EveryChildBearingPropertyIsASlotCreateCanNameOrACoordinateRouteItNames` — every
  child-bearing property the reflected set answers is a slot create can name or a coordinate route it names.
- `apply-guard` — a compose given no fields is refused as having no serializable content.
- `coerce-audit` — every writable scalar, enum, value, formlink and coercible-element leaf in the corpus resolves to
  a coercible type; `coerce-selftest` — each value-type rule builds an instance assignable to its target.
- `compact-service-guard`'s REPOINT-MIXED arm — both refusals split on the shape `LocalizedAmong` returns, rendered
  on the real renderers. It does not pin the fail-closed half: the arm's own note says a held referencer never
  reaches the pre-flight, because the identify pass drops it first, so that half is unpinned.
- `freshness-capture-guard` arm 4 — one call's patch carries ONE build's bodies. The two captures agreeing about
  membership is not separately pinned.

## Where
- `src/housecarl-mcp/RecordWrites.cs` — the lanes: `ApplyEdits`, `CreateRecordsBatch` / `CommitCreate`,
  `RemoveRecords`, `ForwardRecords`, `CopyClosure`, `CompactPlugin`, `MergePlugins`, `CreatePlugin`; the first four
  carry the `…InPlace` branch (`ApplyEditsInPlace`, `CommitCreateInPlace`, `RemoveRecordsInPlace`,
  `ForwardRecordsInPlace`), while compact overwrites inside its own lane. Plus the shared in-place seams
  (`ResolveActivePluginPath`, `InPlaceHandshakeText`,
  `PersistInPlaceConsent`, `InPlaceParentUnwritable`, `MergeEditedInPlaceMarker`, `SeqStaleInPlaceNote`) and the wire
  mappers (`MapEdit`, `MapCreateEdit`, `MapStruct`, `MapComposes`).
- `src/housecarl-mcp/WriteSentences.cs` — the catalogue, `WriteSentences.Twins`, and the `[MustState]` /
  `[NoClaims]` attributes.
- `src/housecarl-core/WriteEngine.cs` — the reflection-driven engine underneath every lane: the patch-mod lifecycle
  (`GenericGetOrAddAsOverride`, `NestedGetOrAddAsOverride`, `EnumerateFlatGroups`, `RemovalTypeFor`, `SeekTypeFor`),
  create (`CanCreateType`, `GenericAddNew`, `GenericUpsertNew`, `NestedAddNew`, `AddExteriorCell`, `AddInteriorCell`,
  `EnsureFormIdFloor`), the child-group carry (`CaptureChildGroup`, `RestoreChildGroup`, `ChildBearingProperties`),
  the serialize (`WritePatch`, `WriteInPlace`, `CommitStagedPatch`, `RootNullArm`, `PluginIsLocalized`), path
  navigation and the verbs (`ApplyVerb`, `ApplyScalarVerb`, `ApplyListVerb`, `ApplyDictVerb`, `BuildStruct`,
  `StepIntoElement`, `SetFloi`, `CopyField`), the coercion family (`Coerce` / `CanCoerce` and the `Try*` rules), the
  refusal types (`ExpectedApplyRejectionException`, `MalformedTargetDataException`, `NullArmSerializeException`,
  `CompositionRequiredException`, `LocalizedTargetUnsupportedException`), the `patch` / `show` / `condition-patch`
  dev harnesses, and the `coerce-audit` / `coerce-selftest` probes.
- Tools: `housecarl_apply`, `housecarl_create`, `housecarl_remove`, `housecarl_forward`, `housecarl_copy`,
  `housecarl_compact_plugin`, `housecarl_merge_plugins`, `housecarl_create_plugin`.
