---
updated: 2026-09-23
covers: [src/housecarl-mcp/RecordWrites.cs, src/housecarl-mcp/WriteSentences.cs, src/housecarl-core/WriteEngine.cs,
  src/housecarl-core/WriteVerbs.cs, src/housecarl-core/RemapEngine.cs, src/housecarl-core/ClosureCopy.cs,
  src/housecarl-core/MergeInjection.cs, src/housecarl-core/MergeLoadPosition.cs,
  src/housecarl-core/WritePatchBuilder.cs,
  src/housecarl-mcp/ApplyTools.cs, src/housecarl-mcp/CreateTools.cs, src/housecarl-mcp/ForwardTools.cs,
  src/housecarl-mcp/RemoveTools.cs, src/housecarl-mcp/SeqTools.cs, src/housecarl-mcp/WriteTools.cs,
  src/housecarl-core/LocalizedStrings.cs]
---
# The write path, service side

## What it is
The service half of every write verb: from a call's parsed ops to a plugin on disk, and the one catalogue the
resulting prose is rendered from. Every lane writes a new plugin by default: apply, create, remove and forward also
take the `in_place=true` opt-in described below; closure copy, merge and create_plugin have no in-place route at all;
and compact can overwrite its source, but under its own per-call confirm rather than the persistent handshake.
Pre-flight belongs to [`corpus-rulebook.md`](corpus-rulebook.md); where a write lands belongs to
[`output-and-artifacts.md`](output-and-artifacts.md). Neither is repeated here.

The core half of the same path is `WritePatchBuilder`: the one `(edits) → (patch)` surface every write tool goes
through, and the home of the `PatchEdit` / `CreateSpec` / `ForwardSpec` shapes the service maps a call onto and of the
`PatchOutcome` / `CreateOutcome` / `RemovalOutcome` / `ForwardOutcome` shapes it renders back.

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
- A call's destinations are mutually exclusive — `patch=` / `into=` / `in_place=`, and `.seq`'s `out_path=`, which
  supersedes the `patch=`/`into=` pair, says so, and is checked before it — and a named lane is honoured or refused BY
  NAME rather than accepted-and-ignored. Emptiness is judged one way for a lane string, so the exclusivity checks and
  the write cannot disagree about whether a lane was named.
- `format=` is resolved BEFORE the unconfigured-MO2 prompt, which is prose a json caller could not parse, and every
  refusal below it answers in the requested format with a null epoch.
- The lane a response reports is the one the CALL named, never one derived from the outcome's flags, which sit at
  their defaults on a refusal.
- The copy zip is a ZIP and never a product — N assignments × M bundle paths is N*M ops over N sources — and each
  generated op carries the caller's own spelling, `assignments[i]` × `bundle[j]`, so a refusal names something the
  caller wrote.
- A record missing from the written file is said ABOVE the rows and outside their budget, so a row cut cannot remove
  it. A nested child whose PARENT is missing belongs in both lists, each selected on its own flag.
- Whether re-issuing a write to widen a truncated display is safe is a property of the LANE, not of the verb: safe on
  `into=` and on a dry run, a SECOND patch mod on the default lane, a read-back on `in_place=`, and a READ rather
  than a re-issue after a create, which would allocate the records again. A removal is the one exception with no lane
  to consult — it is all-or-nothing over the `formids=` passed, so those rows ARE that set and a re-issue is refused.
  The budget and cut-notice mechanics are [`render-budget.md`](render-budget.md)'s.
- The touched-record verify is forced on the in-place lane and renders compact by default, in full only on
  `full_readback=true`. A dry run's records come from the in-memory would-be content and the render says so first, so
  a dry run can never read like a write.
- An external OVERRIDER is warned about by name rather than routed through the referencer path, an override being an
  identity and not a link; a merge's swap is PLUGIN-level, not mod-level, because the merged records still reference
  the donors' files by path; and the light, master, localized and header-text notes are keyed on what the DONORS
  carried, never on the donor count.
- A `.seq` write gives "already current" and "replaced" their own headlines and states the absent epoch, the bytes
  being load-order-independent and a skipped write reported as a write reading like a silent failure.
- Which write verbs work on a collection leaf is derived ONCE from its SHAPE — list or dict, crossed with how an
  element gets in (a coerced value, built from parts, or an owned child record) — and each site filters that table by
  purpose rather than naming verbs, so a dict caller cannot be offered a list verb and a verb added to the surface
  reaches every message at once.
- That table is indexed by SHAPE while the gate is indexed by VERB, and the two are held together by measurement
  rather than by copying: every collection field in the corpus is bucketed by shape, and each bucket replays a
  well-formed request through the real `CorpusRulebook.Validate` for every verb the table names (each must be
  ACCEPTED) and every verb it omits (each REFUSED).
- The two routes to a shape — the corpus `FieldSchema` route and the live-property route the engine's own throws use
  — must give the same answer for every collection field in the corpus, because the engine keeps its
  schema-blindness by never taking the corpus.
- An owned-child-record collection's menu is `Remove` alone: it has no placing verb, so the how-to-place site prints
  the record-axis route rather than naming one. The rule itself — an owned child is written on the record axis by its
  own FormID, never built into a parent by a write verb — and that route's `parent=` / `collection=` remedy are
  [`corpus-rulebook.md`](corpus-rulebook.md)'s. `CopyFrom` is absent from every dict shape too, a dict transplant not
  being built.
- A list's keyed menu leads with `SetAtIndex`, not `InsertAtIndex`: a caller who bracketed an index that already
  holds an element and read the menu top-down would otherwise append, which the gate ACCEPTS, and on a CTDA OR-run
  that changes what the record gates on.
- The create and nested-compose verb sets are DERIVED by subtraction from the one vocabulary, never hand-typed, and
  each subtraction throws at startup when its subtrahend is absent from that vocabulary, because a subtraction
  matching nothing would silently publish the whole set.
- The caller-facing recitals are separate compile-time consts because an attribute argument must be constant, and a
  description must CONCATENATE one rather than type the names out. The full recital's LAST token is load-bearing:
  `BulkOp.verb` glues a gloss straight onto its tail, so appending or reordering a verb moves that gloss onto a
  different verb.
- Renumbering a record is `record.Duplicate(newKey)` into a FRESH mod followed by `RemapLinks(dict)`. `RemapLinks`
  repoints outgoing references only, and the record's own FormKey setter — reachable but non-public — leaves the
  FormKey-keyed group cache stale, so it is never used.
- The identify pass is one whole-order walk per operation, never a held index, and its coverage is accounted: a
  record whose link walk throws is counted and sampled, and a plugin that could not be read THROUGH is named with
  its cause and is not counted as scanned.
- The two unscannable causes stay apart because the remedies differ — a file that would not OPEN is almost always
  another program holding it, while one that opened and then faulted part-way is not. A header read that faults
  falls THROUGH to the records, a different read that may well succeed, and the plugin is named once, for the fault
  that came first.
- An external REFERENCER is found by outgoing link and can be repointed; an external OVERRIDER is found by record
  IDENTITY, cannot be auto-repointed, and is warned about instead. The identity test runs BEFORE the link test, and
  the deleted-record skip scopes to the link walk alone, so a deleted override is still a dependent.
- A third dependent kind is read from HEADERS: a plugin declaring a transform-set plugin as a master while
  referencing and overriding none of its records. Listed only where the record walk did not already find it, dropped
  for a plugin whose record walk faulted, and opt-in because it costs one extra open per plugin and only a caller
  that RENAMES the set has anything to report.
- Allocation into an id window refuses LOUD when the source overflows it and never truncates; for an ESL compaction
  that is the light-master ceiling, the usable window being 0x800–0xFFF INCLUSIVE, 2048 ids.
- A merge KEEPS an object id wherever it is in-window and unclaimed, donors claiming in load order, and allocates a
  fresh id only for a collision or an id below the write floor.
- A record living only in a NESTED group has no flat top-level group, so the flat renumber refuses it by name rather
  than dropping it; the structural renumber walks the source mod's structure instead, which is what keeps parentage,
  and re-files a renumbered interior cell by its NEW id.
- A cross-donor conflict on one FormKey resolves to the LOAD-ORDER WINNER and is reported per losing donor; donors
  walk in reverse load order so the winner places first, and a losing donor's nested children the winner does not
  re-list are GRAFTED into the winner's container. A structural mismatch on the winner's side, or an unrecognized
  nested block shape, THROWS into the all-or-nothing refusal — any engine fault abandons the renumber or merge with
  nothing shippable — rather than dropping a child.
- A repoint result's entry count is the size of the dict APPLIED, not the number of links rewritten, which Mutagen
  does not report.
- The in-place repoint opens the single target mutable, resolves the target's OWN declared masters to overlays and
  re-serializes over itself; a declared master that is inactive, unopenable or missing from disk is a loud refusal
  with the file untouched, because this runs only once the compacted plugin is already on disk.
- Every remap method opens at most one plugin mutable at a time and disposes its master overlays after the write, so
  the load order is never held parsed.
- An INJECTED record — carried by one donor under another plugin's FormID — originates to no donor, so it is given
  to the FIRST donor carrying it and renumbered with that donor's own records; left out of the dict it is copied at
  an identity naming a plugin the merge removes, and the write fails with a raw missing-mod fault after the whole
  merge is built. Which plugin DEFINES it is not decidable from the order, so nothing claims it.
- A donor link whose target no donor holds is refused before anything is built, and only the links that SURVIVE the
  merge are asked about: the merge keeps the load-order winner's body, so a stale reference a later donor already
  fixed must not refuse the merge.
- The merged plugin must load after its masters and AT the last donor's position, because the merge baked the donors'
  conflict outcomes in as they stood there. Positions come in 0-based from the resolver and are reported 1-based, and
  a master that is not flagged ESM can sit after the last donor — an order the advice cannot satisfy, so it is
  FLAGGED rather than an impossible slot printed.
- The placement names no other plugins: the records whose winner it decides are the overrides the donors carry at
  their MASTERS' FormIDs, which no pass in a merge enumerates, so the constraint is stated and the roster is not
  guessed at.
- A closure copy internalizes the walk's reached set with `Duplicate(newKey)` + `RemapLinks`, never field by field,
  so no field can be forgotten; allocation comes off the patch's own counter, so an EXTENDED patch keeps counting.
- Those duplicates are built in a SCRATCH mod sharing the patch's ModKey and transplanted in. That is a correctness
  step, not an optimization: the remap runs over the whole target mod and would otherwise repoint a record the caller
  had already put in an extended patch.
- After internalize and remap, any link still pointing into the bound source universe was not part of what was
  copied, and is removed by link identity with every removal named; a REQUIRED link that cannot be cleared is a loud
  refusal, because keeping it would master the plugin the artifact claims to be free of.
- Whether a link may be nulled is judged on the record model's `IFormLinkNullable<T>`, never on whether a
  `SetToNull` method exists: Mutagen's required links expose one too, so deciding by method presence writes a null
  into a required field.
- The strip is two passes and the first does not mutate — a read-only scan refuses an unclearable shape before
  anything is removed — so a refusal never leaves a half-stripped record. Nulling a link-bearing substruct takes the
  WHOLE property rather than just the offending link, and the entry is marked so the render says so.
- The attach lane writes links already mapped rather than fixing them up with a mod-wide pass, so a patch record the
  caller never named is unreachable from it. A target inside the bound universe is refused, and a target in a NESTED
  group is a typed refusal naming the shape rather than a throw rendered as an internal fault.
- An unset or empty source seed CLEARS the target's value rather than leaving it, and the clear is reported as a
  clear rather than as a no-op or a zero count. A seed's shape is classified once, by the walk's own classifier, so
  the attach and clone lanes cannot disagree about a field.
- The off-order link check is per lane: the attach lane asks UP FRONT, nothing stripping there, while the clone lane
  asks the ARTIFACT after the strip, and the refusal splits by cause so the remedy names something the caller did —
  their own `stop`, a record a previous call left in the patch, or an unseeded field carried across.
- The post-attach leak check is scoped to BOUND keys only: a broader "any link that does not resolve" test would also
  catch the target's pre-existing dangling reference, which is not this operation's defect.
- Asset paths are harvested from the IN-PATCH duplicates and before the serialize, the donor bodies being
  overlay-backed and released there, and an unreadable asset link is a report rather than a reason to fail a written
  copy.
- A walk that cycles back to the `from` record has already internalized it, so the clone reuses that copy rather
  than minting a second duplicate sharing its EditorID.
- Every closure-copy refusal returns with nothing usable written, while a post-commit read-back failure is a WARNING
  on a success: the patch is on disk by then, and mislabelling it invites a duplicate re-run.
- One complete `.esp` per call. A fresh patch by default; `extend:true` opens the existing patch and adds to it, so the
  disk file IS the accumulating state and a multi-session build survives a server restart with no server-held state.
- Originals untouched is structural on the patch lane: it only ever writes the output path the caller sandboxed, and
  every original is opened read-only as a lazy overlay.
- All-or-nothing on every lane: resolve and pre-flight collect EVERY problem, report them in the caller's edit order,
  and refuse the whole call with no file written rather than emit a partial patch.
- Phase 1 resolves every edit of one call against ONE captured view. A per-edit capture would let a freshness rebuild
  landing mid-loop resolve two edits of one call against two builds' winners — a silently MIXED patch.
- A target absent from the load order that the EXTENDED patch itself defines resolves to the patch's own settable copy.
  That consults only the named output artifact of the current authoring session, never an arbitrary un-enabled plugin;
  an override the patch merely CARRIES still resolves through the load order, so its definer must be enabled.
- A dry run stops AT the point of no return: the same resolve, pre-flight and in-memory apply the real write uses have
  already run, and the one Phase-4 hazard the halt skips — a reference to a plugin outside the serialize's resolution
  context — is re-checked by the same membership test, so a dry run never says "would apply" about a write that would
  fail. It is the real path halted, never a parallel validate-lite.
- A nested create hosts its child in the parent's DEFINING plugin's version, not the load-order winner's: the child
  lives in the parent's child group and survives the parent record losing, so the winner's fields would cost a master
  the child never needed and freeze another mod's content. The winner stays a fallback for an injected or excluded
  definer, and which copy was read is reported per record.
- Only the LAST op touching a leaf is answerable by the written file: an earlier one's reading was taken mid-sequence,
  so it is marked superseded and shown with the leaf's final state, rather than compared and wrongly reported as not
  landed.
- The dry run's unopenable-reference threshold is a MEASURED header rule, not something `set.Count > 1` states on its
  own: a header carrying ONE master writes even when that master is the unopenable plugin, because Mutagen derives the
  entry from the record's own FormKey, and a header that must be SORTED — two or more — refuses. Dropping the count
  test so that any unopenable reference refuses turns a legal write into a refusal.
- BOUND on the dry-run guarantee above: `DryRunMastersPreview` asks `resolver.IsUnopenable`, which reads the resolver's
  CURRENT snapshot, while the write lanes resolve against a pinned `IndexView` — so a rebuild between the prediction
  and the write lets the two disagree. Inherited from the method taking a resolver rather than a view; the failure mode
  is a stale prediction, never a bad write.
- That agreement about membership is also why the engine's off-order re-check is KEPT although it cannot currently
  change the arm: it is a structural invariant at one dictionary lookup, so the right behaviour is already there if
  membership ever can move under a live resolver. Not dead code to delete.
- What the per-op file compare CATCHES is content that is GONE — a container whose count moved, a leaf that held
  something and now holds nothing. It is not a judgement that the write landed: a real difference cannot be told
  reliably from a representational one ([`json-wire.md`](json-wire.md) carries that reading), and an element that
  landed but serialized with fewer fields than the caller supplied is bounded from the other end instead, by
  `WriteEngine.EmptyComposeRefusal` refusing the case where nothing was supplied at all.
- The localized classifier supplies WORDS, never the in-place outcome, which is the same for every
  shape. Its shapes are `NotLocalized`, `Unreadable` (the header was never read), `LooseComplete`,
  `LoosePartial` (a missing kind would be materialised holding empty values), `LooseWithGameDataDuplicate`,
  `BsaEmbedded` (including an archive that would not parse), `GameDataOnly` (a write beside the plugin
  would shadow, not replace), `StringsFolderUnreadable`, `ModFolderUnreadable` and `Nowhere`. The last
  three claim what houseCARL could FIND, never that the plugin has no strings.
- Every folder look has the same three answers the plugin header read has — absent, listed,
  unlistable — because "enumerated it and found nothing" and "could not enumerate it" are different
  facts, and collapsing them makes an absence claim nothing checked.

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
- `AtomicCommitGuardTests` — the staged commit lands a fresh file, replaces an existing one
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
- `remedy-verbs-guard` arms population / routes / agreement / sites — the shape-indexed verb table: every shape's
  corpus population stated out loud, the schema and runtime routes agreeing on every collection field, the measured
  accept-and-refuse sweep through the real gate in both directions, and each consuming message carrying this
  cardinality's verbs and not the other's.
- `remedy-verbs-guard`'s SITE-BRACKET-ORDER arm — the bracketed-leaf remedy names `SetAtIndex` before
  `InsertAtIndex`, asserted as a POSITION comparison so a reorder of the table fails it where membership would not;
  `ElementRefusalRemedyTests.AListElementRemedyNamesOnlyVerbsThatTakeTheKeyItPrinted` asserts the same ordering on
  the rendered remedy, beside the keyless `Add` being withheld.
- `apply-guard`'s ZIP arm — the copy zip: a bundle copied BETWEEN records, the zip composing with `ops=` in one call,
  and a bad FormID in an assignment refused NAMING THE ASSIGNMENT rather than a phantom `ops[2]` the caller never
  wrote, which is the caller's-own-spelling half.
- `write-surface-guard` — the `lane` value names the lane the CALL asked for on an `into=` refusal and on an in-place
  consent prompt as much as on a success, and is spelled the same on apply / create / remove / forward; the
  lane-aware truncation remedy on `into=` (the plain re-issue), on the default lane (a SECOND patch mod) and on
  `in_place=` (a READ, never re-serializing the caller's original); `write_seq`'s `patch=`/`into=` pair refused BY
  NAME on both transports, and its absent epoch stated with its reason on both.
- `WriteReadbackFromFileTests.ThePerEditLineMatchesAFreshReadOfTheFile` and `TheJsonOpCarriesTheFilesOwnReading` —
  the verify ran and rendered its compact per-op clause with no `full_readback=true` passed; `apply-guard` arms 6 and
  8 drive the same clause off the written file on the in-place lane, which is the forced half.
- `dry-run-guard`'s RENDER HONESTY arm — a dry outcome leads with DRY RUN and nothing-written and never reads like a
  write, with the `full_readback` dump labelled as the IN-MEMORY preview.
- `seq-write-guard` arms TOOL-LANE and UNCHANGED / -DIFFERS / RENDER-UNCHANGED / JSON-UNCHANGED — `out_path=` wins
  over `patch=`/`into=` with the ignored lane STATED, and a byte-identical destination is left alone and renders as
  its own state on both transports while a stale one is rewritten.
- `description-vocab-guard` arms INV4-HOMES / INV4-CREATEHOMES / INV4-COMPOSEHOMES — each verb list and its recital
  agree with each other and with a vocabulary written independently in the probe, which is what lets the derived
  subtractions fail; INV4-MARK — the recital marks exactly one verb as the default; INV4-TAILGLOSS — the verb the
  recital ends with is the one the glued gloss describes.
- `remap-wave1-guard` arms HAPPY / CAPACITY / NESTED — the whole compact proven on disk (records renumbered into the
  ESL window, an internal reference repointed, the identify pass finding the external referencer and not the donor,
  the in-place repoint rewriting it), the window-overflow refusal, and the flat renumber's nested-only refusal.
- `remap-wave2-compact-guard` arms NESTED / EXTERNAL — the structural renumber keeping every nesting shape
  (cell→placed, worldspace→exterior cell→placed, topic→INFO) with internal references repointed, and the
  identify-plus-repoint half over it.
- `merge-service-guard` arms MERGE / WINNER / GRAFT — the first donor's ids kept, a later donor's collision
  renumbered, the cross-donor reference repointed, conflicts resolved to the load-order winner and reported with
  winner and loser named, and the losing donor's un-relisted INFO grafted into the winning topic; arm HEADER — the
  light, master and header-text notes keyed on what the donors carried; arm DECLARER — a declarer-only dependent
  reaching the rendered report.
- `overrider-detect-guard` arms OVERRIDER / REFERENCER — an overrider is a warn that lets the compaction succeed
  while a referencer is refused and named, the contrast that holds the two apart.
- `MasterDeclarerScanTests.ADeclarerOnlyDependentIsFoundAndNamed`, `AReferencerIsNotAlsoListedAsADeclarer`,
  `APluginThePassCouldNotReadIsNotCalledADeclarer`, `APluginDeclaringAMasterOutsideTheTransformSetIsNotADeclarer` and
  `ACorruptMasterTableReadsTheSameForBothCallers` — the declarer category's exclusions and the header-fault reading.
- `IdentifyScanCoverageTests.AReferencerLockedAfterTheIndexWasBuiltIsReportedUnscannable` and
  `TheReportSaysAnUnreadablePluginCouldNotBeOpened` — a plugin that could not be read through is named with its
  cause; `InPlaceCompactRefusesWhenAReferencerCouldNotBeRead` — what that gap costs the caller.
- `MergeInjectedRecordTests.AnInjectedRecordCarriedByADonorIsRenumberedIntoTheOutput` and
  `APluginOutsideTheMergeCarryingTheSameInjectedRecordIsWarnedAboutByName` — the injected-record rule and its posture
  toward a plugin outside the merge; `ADonorReferenceNoDonorHoldsIsRefusedBeforeAnythingIsBuilt`,
  `AStaleReferenceALaterDonorAlreadyFixedDoesNotRefuseTheMerge` and
  `ADeletedRecordsStaleReferenceDoesNotRefuseTheMerge` — the unremappable-link refusal and the two links it does not
  ask about.
- `MergeSitingTests.PositionsComeInZeroBasedAndComeBackOneBased` and `AMasterBelowTheLastDonorIsFlagged` — the
  placement derivation itself; `MergePlacementTests.ThePlacementParagraphGivesTheDonorRangeAndTheSlot`,
  `…NamesTheLastMasterAndItsPosition`, `…ClaimsNoWinnerOverARenumberedRecord` (the paragraph names the donors'
  overrides at their masters' FormIDs and no roster) and `ASingleDonorGetsItsOwnPositionAndClaimsNothingAboutAnInterval`
  — the rendered paragraph.
- `closure-copy-guard` arms INTERNALIZE / REMAP SCOPING / NULLABILITY / REQUIRED LINK / LEAK SCOPING / PROVENANCE —
  fresh keys off the patch's own counter with an extended patch still counting, the scratch-mod step (a record the
  caller already put in the patch survives untouched, and the arm fails if the step is removed), nullability judged
  on the record model's interface, the required-link refusal checkable in both directions, a surviving bound link
  being a leak while a pre-existing dangling one is not, and the walk's arm attribution surviving into the report.
- The seam `freshness-capture-guard` arm 4 parks the write on is `WritePatchBuilder.InsidePhase1ResolveForGuard`,
  which is why that flip is staged rather than timed and no runner can be too fast to land it inside the resolve loop.
  The field has no product caller.
- `excluded-master-guard` — the unopenable-reference threshold, pinned BOTH ways on BOTH lanes: a one-master header
  WRITES and a two-master header REFUSES naming the unopenable plugin and its remedy, on the patch lane and on the
  in-place lane, with the dry run predicting the real call's refusal verbatim and still predicting success for a write
  that does not reference it.
- `LocalizedStringsSourceTests` / `LocalizedModFolderUnreadableTests` / `StatusLocalizedLookupTests`,
  and `StringsResolveProbe` / `StringsDecisionGuardTests` / `LocalizedShapeSweep` — the shapes and the
  three-answer folder reads.

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
  `CompositionRequiredException`, `LocalizedTargetUnsupportedException`), and the `patch` / `show` /
  `condition-patch` dev harnesses.
- `src/housecarl-generator/CoerceAuditProbe.cs` and `CoerceSelftestProbe.cs` — the `coerce-audit` and
  `coerce-selftest` probes over the coercion family.
- Tools: `housecarl_apply`, `housecarl_create`, `housecarl_remove`, `housecarl_forward`, `housecarl_copy`,
  `housecarl_compact_plugin`, `housecarl_merge_plugins`, `housecarl_create_plugin`.
- `src/housecarl-core/WriteVerbs.cs` — the write-verb vocabulary, `CollectionShape`, `WriteVerbs.On` and the purpose
  filters (`HowToPlace`, `HowToPlaceOne`, `HowToPlaceOneAt`, `HowToAddress`), plus the two routes to a shape
  (`OfField`, `OfRuntimeType` / `OfElement`).
- `src/housecarl-core/RemapEngine.cs` — the shared foundation under compact and merge:
  `IdentifyExternalReferencers`, `BuildSequentialRemap` / `BuildMergeRemap`, `RenumberRecordsInto` /
  `RenumberModInto`, `MergeModsInto` with its graft helpers, `LocalizedAmong` and `RepointInPlace`.
- `src/housecarl-core/ClosureCopy.cs` — `Internalize`, `StripBoundLinks`, `AttachSeedFields`, `FindBoundLeak` and
  `BuildAndWrite`, behind `housecarl_copy`; the walk that feeds it is
  [`walk-and-reverse.md`](walk-and-reverse.md)'s.
- `src/housecarl-core/MergeInjection.cs` (`Renumberable`, `UnremappableLink`) and
  `src/housecarl-core/MergeLoadPosition.cs` (`Derive`, `MergeSiting`) — the two merge pre-flights.
- The tool fronts: `src/housecarl-mcp/ApplyTools.cs`, `CreateTools.cs`, `ForwardTools.cs`, `RemoveTools.cs`,
  `SeqTools.cs` and `WriteTools.cs` — argument reading, the lane and transport gates, and the render helpers every
  write tool calls.
- `src/housecarl-core/WritePatchBuilder.cs` — the core half: `Apply` / `ApplyInPlace`, `CreateRecords` /
  `CreateRecordsInPlace`, `RemoveRecords` / `RemoveRecordsInPlace`, `ForwardRecords` / `ForwardRecordsInPlace`,
  `CreatePlugin`, `CompactBuild` and `MergeBuild`. The four record lanes — apply, create, remove, forward — are the
  ones that split a `…Core` body, so the one captured build's fingerprint stamps every one of THEIR outcomes from a
  single place; `CreatePlugin`, `CompactBuild` and `MergeBuild` have no such split and their results carry no stamp at
  all. Plus the shared seams: `LinkTypeLookup`, `ResolveForwardSources`,
  `SyncEditedTopicMarkers`, `DryRunMastersPreview`, `MasterGrowNote`, `SerializeFailure`, `ReadBackInFull`,
  `VerifyLandedAgainstFile` and `VerifyCreatedAgainstFile`.
- `src/housecarl-core/LocalizedStrings.cs` — the strings-shape classifier, whose localized language set is Mutagen's
  `Language` enum; the write lanes' pre-flights call
  `LocalizedStrings.RefusalFor`.
