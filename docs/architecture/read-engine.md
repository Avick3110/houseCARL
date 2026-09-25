---
updated: 2026-09-23
covers: [src/housecarl-core/ReadEngine.cs, src/housecarl-core/BodyGather.cs, src/housecarl-core/WinnerBodies.cs, src/housecarl-core/RecordLinks.cs, src/housecarl-core/RecordArms.cs, src/housecarl-core/RecordNaming.cs, src/housecarl-core/PathFold.cs, src/housecarl-core/PluginFile.cs, src/housecarl-mcp/RecordReads.cs, src/housecarl-mcp/RecordPoles.cs, src/housecarl-mcp/RecordWalk.cs, src/housecarl-mcp/RecordQuery.cs, src/housecarl-mcp/TreeFold.cs, src/housecarl-mcp/FieldFold.cs, src/housecarl-mcp/ReverseWalkBatch.cs, src/housecarl-mcp/ScanDetailReader.cs, src/housecarl-mcp/ReadSentences.cs, src/housecarl-mcp/ScopeSplit.cs, src/housecarl-mcp/BodyPrefetch.cs, src/housecarl-mcp/PoleGather.cs, src/housecarl-core/FieldsDiff.cs]
---
# The read engine

## What it is
The path from a `records` call to a rendered body. `ReadEngine` (core) reflects ONE plugin's
record out to round-trippable tokens; `RecordReads` (mcp) resolves which body that is against a
captured load-order build and folds the result into the shape the render consumes. How the records were
SELECTED before a body is read — the `where=` predicate and the containment index — is
`docs/architecture/select-and-walk.md`, and the closure walk and the reverse-reference index are
`docs/architecture/walk-and-reverse.md`; `FieldsDiff` is in this note; the owned-child
union stated beside a child-bearing field is `docs/architecture/records-owned-child-declarers.md`;
what `max_chars` counts and what a cut response may claim is `docs/architecture/render-budget.md`.
The tool front above the engine, `RecordsTools` and `ReadTools`, is `docs/architecture/records-tool-front.md`.

## Contracts
- A value leaf's token is the faithful inverse of `WriteEngine.Coerce`: reading a value and writing that exact token back is a byte-level no-op.
- Navigation is the write engine's own walk (`ResolveProperty` / `StepIntoElement`), but the READ never materialises an absent optional substruct: reading must not mutate.
- `Display`, `Link`, `NoteRef`, `Bytes` and `BytesFormVersion` are display-only — never part of the round-trip token, so write, read-proof and the conflict diff never see them.
- Presence is carried structurally (`Present`, `Readable`, `Count`), never decided by matching a note's prose: an unreadable leaf is not evidence of absence.
- A fault is isolated to the leaf, the list element or the child it happened on; the line names itself and the walk carries on with its siblings.
- The deep walk generates at most `MaxExpandNodes` lines and emits one truncation note at the bound.
- Expansion stops at the modeled corpus: a value outside Mutagen/Noggog renders its summary line and is not descended.
- `DepthExpandHint` names `depth=2` and is only honest on a surface that HAS a `depth=` parameter; a surface that refuses depth passes its own redirect as `containerHint`, or null to suppress it.
- `CollectLinksAt` / `LinksIn` answer an EMPTY list for a present-but-empty field — a genuine "no links here" — and null only for "not a link-bearing path", whose note reuses the leaf-read vocabulary so a caller classifies it like a leaf miss.
- Winner resolution is NOT the core read's — it reads the body it is handed. `RecordReads` resolves the winner, and every outcome of one response carries the `ViewPin` it was answered from, so tree, touching list and epoch stamp name one build.
- A read that cannot answer returns a recoverable NAMED error — not in the order, plugin does not touch it, fetch inconsistency — never a silent empty result; `ResolveRead` reads the winner's body unless a plugin is named, in which case it reads that plugin's override.
- A batch returns one outcome per input, in input order, and a bad or absent formid is a per-item error that does not fail the batch.
- An unresolved FormID names which of the three causes it is — the defining plugin was excluded, it is not in the order, or it IS in the order and defines no such record — and the ESL-compaction clause is stated only where the index says that plugin is light-flagged.
- Bodies are gathered one walk per plugin (`BodyGather`, `WinnerBodies`, `BodyPrefetch`), and the gather is never a second error path — but what a faulted plugin then answers is the lane's own choice, and it is one of three: under `BodyGather.Absent.Seek` the pair falls back to the per-record fetch and raises the same fault, in the same words, from the same place in the caller's own loop; under `Absent.Null` (which `BodyPrefetch` constructs with) it answers null and the caller's own read raises whatever it raises; `WinnerBodies` reads by the returned map alone, so a faulted winner plugin's candidates are simply ABSENT and the plugin is named once in `unreadable`, never re-fetched per record.
- `OutOfMemoryException` and `OperationCanceledException` are rethrown out of a guarded walk rather than recorded as a plugin fault: neither is that plugin's, and calling a readable master a coverage gap because the machine ran out of memory misnames the failure.
- The tree fold runs plugin-major over a chunk of rows in descending load order: each provider plugin is walked once, and the caller is handed its fields and releases them before the next plugin is walked.
- Because those passes are plugin-major, the row a tree fold's fault NAMES can differ from the row the streamed walk named — with two bad rows in one chunk the message names whichever plugin group reached one first. The call throws either way, so no row answers wrong.
- The scan detail lane reads every row through one overlay session and one `BodyPrefetch` chunk, and checks the caller's cancellation token per ROW, so a client that aborts stops inside one row rather than at the end of the chunk.
- `RecordArms.OfTypes` is the one typed enumeration: an abstract group is re-checked per record, so a filter naming one arm is not handed the whole group.
- `RecordLinks.Walk` is the one link read a scan makes, so a record whose links only read leniently is reachable in every lane or in none.
- The reverse walk judges each candidate against its WINNER's links, so `references=` and the walk cannot disagree about the same record.
- `project.fields` quantifiers are tokenized by `PathFoldGrammar`, the same tokenizer `where=` parses with; each surface refuses the other's folds by name.
- `resolve_names` is type-agnostic: a token that parses as a FormKey IS a form reference, so the annotation inherits its coverage from the read surface with no per-type wiring, and an unresolvable target is a named unresolved row rather than a dropped one.
- The conflict diff reads at `ConflictDiffDepth`, deep enough to reach every modeled scalar leaf rather than compare depth-1 count summaries, and is bounded by the corpus boundary and `MaxExpandNodes`, whose truncation sentinel it surfaces as `Complete=false`.
- A named plugin that does not touch a record refuses by naming the plugins that DO, on every lane — active, off-order and pole alike — never a bare "does not define".
- A per-record fault is isolated and accounted, never silent: an unscannable record, a leniently read one and an unreadable plugin are three separate counts in the scan's own note.
- The conflict-tree diff compares DEEP reads, not depth-1 rendered lines. Positional lists compare as
  order-insensitive multisets of whole elements, with a pure reorder reported as its own delta; dict
  brackets are semantic keys and compare by exact path. A CAP suppresses one-sided deltas and the
  agreed count record-wide; an UNREADABLE leaf suppresses only that path and the list element
  comparison it sits in. An empty delta list with `Complete` false must never render as identical.
- The agreed count counts only exact-path VALUE leaves read on BOTH sides — never a container
  summary, never a side's absent or null-link sentinel, because an absent field is not an agreement.
  Per-field presence is reliable only for nullable fields, whose absence the read engine spells, so a
  non-nullable scalar equalling the winner counts as agreement while the render never claims the
  contributor CARRIES it as a distinct subrecord.

## Pinned by
- `WriteProof` step 6, the read-proof oracle (`src/housecarl-generator`, run by `ci-all`) — the round-trip no-op, over every coercible value leaf the write surface drives.
- `RecordsBulkSelectTests.AMalformedFormidIsAPerItemErrorRowWhileTheOtherRowsStillResolve` and `TheIdentityJsonCarriesOneResolvedRowPerInput` — a bad formid is a per-item error, and the batch renders one row per input.
- `RuntimeFormIdTests.AMissingRecordInAnEslFlaggedPluginIsToldAboutCompaction` and `RecordsRemedyRepairTests.AndDoesNotBlameEslCompactionOnAPluginThatIsNotEslFlagged` — the ESL clause is stated on a light-flagged plugin and NOT on a plain full master.
- `RecordsRenderCostTests.ATreeGathersItsProviderBodiesPerPluginNotPerRow` — the tree fold's one walk per provider plugin per chunk.
- `RecordsRenderCostTests.ABatchBodyReadStopsWhenTheClientCancels` and `ACancelStopsAnEverythingRenderToo` — a cancelled batch and a cancelled render stop inside one record.
- `RecordsWalkCostTests.AWalkHoldsNoReachedBodiesPastTheGatherThatReadThem` and `AWalkSplitAcrossPassesReachesTheSameSetAndHoldsOnePass` — a walk holds one gather pass and nothing at return.
- `RecordsRemedyRepairTests.AScanComputesOneListHopRemedyForTheWholeScan` — the list-hop verdict is memoised per (element type, segment).
- `RecordsFieldFoldTests` — `[*]` and `[*count]` columns, and the read's truncation note surviving the fold.
- `BodyGatherEquivalenceTests.AGatheredBodyIsTheBodyTheSingleFetchReturns` and `AFaultedPluginIsNamedAndFallsBackToTheSingleFetch` — a gathered body equals the one-at-a-time body, and a faulted plugin's fallback raises the same exception type and message the direct fetch does.
- `PresentNullLinkDiffTests.ACarriedHeadMarkerDeltasAgainstASideCarryingNothing` — the diff's
  present-null-link split, and `TwoAbsentSidesStillCollapse` the two-absent case.

## Where
`src/housecarl-core/`: `ReadEngine.cs` (leaf read, emit, deep walk), `BodyGather.cs` /
`WinnerBodies.cs` (bulk bodies), `RecordLinks.cs`, `RecordArms.cs`, `RecordNaming.cs`,
`PathFold.cs`, `PluginFile.cs`, `FieldsDiff.cs` (the deep comparison behind `project={"form":"tree"}`).
`src/housecarl-mcp/`: `RecordReads.cs` (resolve, batch, the one-pole batch, info order, the dialogue fold),
`RecordPoles.cs` (comparison poles, the delta/tree batches), `RecordWalk.cs` (walk), `RecordQuery.cs`
(cross-plugin query, off-order scan, effect chain), `ScopeSplit.cs` (the scan's `plugins=` split),
`BodyPrefetch.cs` and `PoleGather.cs` (bulk bodies for a chunk of rows and of pole reads),
`TreeFold.cs`, `FieldFold.cs`, `ReverseWalkBatch.cs`, `ScanDetailReader.cs`, `ReadSentences.cs`
(the read surface's prose; the check families' own sentences now live in `CheckSentences.cs`, under
`docs/architecture/check-families.md` and
`docs/architecture/check-scripts-and-dialogue-families.md`). The type lookup is in `TypeLookup.cs`,
under `docs/architecture/corpus-rulebook.md`.
Tool: `housecarl_records`.
