---
updated: 2026-09-21
covers: [src/housecarl-core/ReadEngine.cs, src/housecarl-core/BodyGather.cs, src/housecarl-core/WinnerBodies.cs, src/housecarl-core/RecordLinks.cs, src/housecarl-core/RecordArms.cs, src/housecarl-mcp/RecordReads.cs, src/housecarl-mcp/TreeFold.cs, src/housecarl-mcp/FieldFold.cs, src/housecarl-mcp/ReverseWalkBatch.cs, src/housecarl-mcp/ScanDetailReader.cs]
---
# The read engine

## What it is
The path from a `records` call to a rendered body. `ReadEngine` (core) reflects ONE plugin's
record out to round-trippable tokens; `RecordReads` (mcp) resolves which body that is against a
captured load-order build and folds the result into the shape the render consumes.

## Contracts
- A value leaf's token is the faithful inverse of `WriteEngine.Coerce`: reading a value and writing that exact token back is a byte-level no-op.
- `Display`, `Link`, `NoteRef`, `Bytes` and `BytesFormVersion` are display-only — never part of the round-trip token, so write, read-proof and the conflict diff never see them.
- Presence is carried structurally (`Present`, `Readable`, `Count`), never decided by matching a note's prose: an unreadable leaf is not evidence of absence.
- A fault is isolated to the leaf, the list element or the child it happened on; the line names itself and the walk carries on with its siblings.
- The deep walk generates at most `MaxExpandNodes` lines and emits one truncation note at the bound.
- Expansion stops at the modeled corpus: a value outside Mutagen/Noggog renders its summary line and is not descended.
- Winner resolution is NOT the core read's — it reads the body it is handed. `RecordReads` resolves the winner, and every outcome of one response carries the `ViewPin` it was answered from, so tree, touching list and epoch stamp name one build.
- Bodies are gathered one walk per plugin (`BodyGather`, `WinnerBodies`, `BodyPrefetch`); the gather is never a second error path, so a plugin whose walk faulted falls back to the per-record fetch and raises the same exception in the same place.
- The tree fold runs plugin-major over a chunk of rows in descending load order: each provider plugin is walked once, and the caller is handed its fields and releases them before the next plugin is walked.
- `RecordArms.OfTypes` is the one typed enumeration: an abstract group is re-checked per record, so a filter naming one arm is not handed the whole group.
- `RecordLinks.Walk` is the one link read a scan makes, so a record whose links only read leniently is reachable in every lane or in none.
- The reverse walk judges each candidate against its WINNER's links, so `references=` and the walk cannot disagree about the same record.
- `project.fields` quantifiers are tokenized by `PathFoldGrammar`, the same tokenizer `where=` parses with; each surface refuses the other's folds by name.

## Pinned by
- `WriteProof` step 6, the read-proof oracle (`src/housecarl-generator`, run by `ci-all`) — the round-trip no-op, over every coercible value leaf the write surface drives.
- `RecordsRenderCostTests.ATreeGathersItsProviderBodiesPerPluginNotPerRow` — the tree fold's one walk per provider plugin per chunk.
- `RecordsRenderCostTests.LimitBoundsWhatATreeOverAScanReads_NotOnlyWhatItRenders` — the window bounds what the fold READS, not only what it renders.
- `RecordsWalkCostTests.AWalkHoldsNoReachedBodiesPastTheGatherThatReadThem` and `AWalkSplitAcrossPassesReachesTheSameSetAndHoldsOnePass` — a walk holds one gather pass and nothing at return.
- `RecordsRemedyRepairTests.AScanComputesOneListHopRemedyForTheWholeScan` — the list-hop verdict is memoised per (element type, segment).
- `RecordsFieldFoldTests` — `[*]` and `[*count]` columns, and the read's truncation note surviving the fold.

## Where
`src/housecarl-core/`: `ReadEngine.cs` (leaf read, emit, deep walk), `BodyGather.cs` /
`WinnerBodies.cs` (bulk bodies), `RecordLinks.cs`, `RecordArms.cs`, `RecordNaming.cs`,
`PathFold.cs`, `PluginFile.cs`.
`src/housecarl-mcp/`: `RecordReads.cs` (resolve, batch, poles, delta/tree, walk, scan),
`TreeFold.cs`, `FieldFold.cs`, `ReverseWalkBatch.cs`, `ScanDetailReader.cs`, `ReadSentences.cs`.
Tool: `housecarl_records`.

## Related
- `docs/architecture/records-owned-child-declarers.md` — the owned-child union and the per-provider tier a read states beside a child-bearing field.
- `docs/architecture/render-budget.md` — `max_chars`, the render bound, and what a response may claim about what it cut.
