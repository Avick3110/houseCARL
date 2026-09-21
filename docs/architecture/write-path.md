---
updated: 2026-09-22
covers: [src/housecarl-mcp/RecordWrites.cs, src/housecarl-mcp/WriteSentences.cs]
---
# The write path, service side

## What it is
The service half of every write verb: from a call's parsed ops to a plugin on disk, and the one catalogue the
resulting prose is rendered from. The lanes are apply, create, remove, forward, closure copy, compact, merge and
create_plugin, each with a default new-plugin route and an in-place opt-in.
Pre-flight belongs to [`corpus-rulebook.md`](corpus-rulebook.md); where a write lands belongs to
[`output-and-artifacts.md`](output-and-artifacts.md). Neither is repeated here.

## Contracts
- A write's default destination is a NEW plugin; editing an existing plugin is the `in_place=true` opt-in.
- `in_place=true` requires `target=`, is mutually exclusive with `into=` / `patch=`, and `target=` without
  `in_place` is refused by name rather than ignored — the same contract on apply, create, remove and forward.
- An in-place `target=` is resolved to an on-disk path by plugin filename through the load order, and a name that
  is not an active plugin is refused; a coincidentally-named folder is never a target.
- Consent is a persistent first-touch handshake keyed off the resolved path, shared by the edit, create, remove and
  forward lanes, so acknowledging a plugin once covers all four. `acknowledge=true` waives the consent axis only,
  never a post-write verify.
- The acknowledgement is recorded only after the write has landed. A refused call records nothing, so the prompt
  says it is shown until an in-place write LANDS rather than calling itself one-time, and its file claim is
  direction-neutral.
- A dry run neither needs nor records consent, and stamps nothing: no marker, no `.seq` note.
- An in-place write stamps `[houseCARL] editedInPlace=<ISO>` into the target mod's `meta.ini` and never
  `generated=true`, so the user's mod keeps failing the ownership gate and a later `into=` cannot overwrite it. Only
  for a mod folder under ModsDir.
- Each lane's own pre-flights answer BEFORE the consent gate and before anything is staged: a localized target and
  an unwritable parent folder both refuse in that lane's words, with the file untouched.
- Side-effect notes after a landed write — consent, the marker, a now-stale `.seq` — are best-effort; none can fail
  the write, and the engine's own note is joined first so it survives the merge.
- One write at a time: `_writeGate` is held from resolve through commit, while argument parsing and op mapping run
  outside it, so a malformed call never queues behind a real write.
- Every write response decided after a capture carries that build's epoch — success, refusal, dry run and consent
  prompt alike. An outcome that consulted no build carries none.
- A write response states only what it re-read from the written FILE: the file's value, `not-checked` where the file
  could not answer, and a did-not-land verdict only off a walk that succeeded. Never the applied in-memory value.
  (The W0 rule, 2026-09-15; PRs #743, #744, #746.) An opaque `bytes` leaf re-reads as a byte count with its
  structure NOT checked.
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
- The sentence catalogue: every user-facing write sentence has ONE source in `WriteSentences`, because each outcome
  renders twice (text and json). `Twins` holds what BOTH transports must state; a sentence that is prose on one
  transport by design stays on the outer class.
- Every catalogue const DECIDES: `[MustState]` phrases that must still appear in it, or `[NoClaims]` with a stated
  reason. An undecorated const fails by name rather than going unchecked, and a phrase is the claim, not the topic.

## Pinned by
- `inplace-guard` arms E / L / U — the `in_place`⇔`target=` contract and the `into=` / `patch=` exclusion, on the
  edit, create and remove lanes.
- `inplace-guard` arms F / V — a target that is not an active plugin is refused.
- `inplace-guard` arms G / K / W — the handshake refuses, then writes under `acknowledge=true`, does not re-prompt,
  persists, and one acknowledgement covers the edit, create and remove lanes.
- `inplace-guard` arms CO-A–CO-D — a refused in-place write spends no consent; CO-E / CO-F — one that lands records
  it; CO-G — the prompt states when it stops and keeps its file claim direction-neutral.
- `inplace-guard` arm I — the marker is stamped, `generated=true` is not, and a later `into=` on that folder is
  still refused.
- `inplace-guard` arms LOC-A–LOC-J — the localized pre-flight answers before consent in each lane's own words, file
  untouched and no consent spent, on the real call and the dry run alike.
- `apply-guard` arm 5 — every write render carries the epoch, on both transports.
- `WriteEditLineSourceTests.ThePerEditLinePrintsTheFileValueNotTheAppliedOne`,
  `…AnOpTheFileCouldNotAnswerForIsNotCheckedRatherThanTheAppliedValue`,
  `…ARecordMissingFromTheWrittenFileIsSaidOutright` and `…AFailedWalkIsNotCheckedRatherThanAVerdict` — the W0 rule's
  four readings.
- `OpaqueBytesVerifyTests.TheVerifySentenceNamesTheOpaqueFieldItReReadAsBytesOnly` — the opaque-leaf caveat.
- `write-surface-guard` — every `WriteSentences` const decides and still states its declared phrases, every `Twins`
  member is rendered by both lanes, and every outer `[MustState]` sentence reaches a render.

## Where
- `src/housecarl-mcp/RecordWrites.cs` — the lanes: `ApplyEdits`, `CreateRecordsBatch` / `CommitCreate`,
  `RemoveRecords`, `ForwardRecords`, `CopyClosure`, `CompactPlugin`, `MergePlugins`, `CreatePlugin`, each with its
  `…InPlace` branch, plus the shared in-place seams (`ResolveActivePluginPath`, `InPlaceHandshakeText`,
  `PersistInPlaceConsent`, `InPlaceParentUnwritable`, `MergeEditedInPlaceMarker`, `SeqStaleInPlaceNote`) and the wire
  mappers (`MapEdit`, `MapCreateEdit`, `MapStruct`, `MapComposes`).
- `src/housecarl-mcp/WriteSentences.cs` — the catalogue, `WriteSentences.Twins`, and the `[MustState]` /
  `[NoClaims]` attributes.
- Tools: `housecarl_apply`, `housecarl_create`, `housecarl_remove`, `housecarl_forward`, `housecarl_copy`,
  `housecarl_compact_plugin`, `housecarl_merge_plugins`, `housecarl_create_plugin`.
