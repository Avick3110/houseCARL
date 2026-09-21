---
updated: 2026-09-22
covers: [src/housecarl-core/ReadEngine.cs, src/housecarl-core/BodyGather.cs, src/housecarl-core/WinnerBodies.cs, src/housecarl-core/RecordLinks.cs, src/housecarl-core/RecordArms.cs, src/housecarl-core/RecordNaming.cs, src/housecarl-core/PathFold.cs, src/housecarl-core/PluginFile.cs, src/housecarl-mcp/RecordReads.cs, src/housecarl-mcp/TreeFold.cs, src/housecarl-mcp/FieldFold.cs, src/housecarl-mcp/ReverseWalkBatch.cs, src/housecarl-mcp/ScanDetailReader.cs, src/housecarl-mcp/ReadSentences.cs, src/housecarl-mcp/RecordsTools.cs, src/housecarl-mcp/ReadTools.cs]
---
# The read engine

## What it is
The path from a `records` call to a rendered body. `ReadEngine` (core) reflects ONE plugin's
record out to round-trippable tokens; `RecordReads` (mcp) resolves which body that is against a
captured load-order build and folds the result into the shape the render consumes. How the records were
SELECTED before a body is read — the `where=` predicate, the containment index, the closure walk, the
reverse-reference index and `FieldsDiff` — is `docs/architecture/select-and-walk.md`; the owned-child
union stated beside a child-bearing field is `docs/architecture/records-owned-child-declarers.md`;
what `max_chars` counts and what a cut response may claim is `docs/architecture/render-budget.md`.

Above both sit the two tool-front files. `RecordsTools` is `housecarl_records` itself: it parses the
four axes, validates them against each other, decides which lane answers, and assembles the response
envelope every render carries. `ReadTools` is the text render that lane and the check families share —
`Wire`. Their own contracts are the argument grammar, the refusal shape and the lane routing below;
the json twin of the same responses is `docs/architecture/json-wire.md`.

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
- `project`'s sub-parameters are form-scoped: one passed outside the form that reads it is refused by name rather than accepted and dropped, and the rule is on the parameter, not its value, so `depth=1` refuses where `depth=2` does.
- A refusal is stated once, in the text spelling, and `Wire.Refuse` gives it the shape the caller asked for — `error: ` on text, the same bare sentence in an `error` property on json — so a refusal raised where no transport is in scope still reaches a json caller as a refusal document.
- One FormID door per call: `formids=`, `references=` and the walk seeds parse against one captured build, and the scan runs on that same build, so a call's own tokens cannot name records in two orders.
- A response carries exactly ONE `source` statement, first writer wins, so a lane that names its specific source and then falls through to a general pipeline cannot state two.
- The remedy and truncation vocabulary is a function of (tool, FORM), not of the tool: a notice never offers a lever the form it was written for refuses.
- Wherever two captures meet in one call — a walk or scan deriving a selection a reading form then reads, a source-arm probe ahead of a scan, an off-order fold ahead of a merge — the epochs are compared and a divergence refuses loud rather than answering from two builds.
- `limit=`/`offset=` window the RENDER, except on the comparison forms, where the keys are windowed before any body is read because a delta or tree row reads every provider of its record; a census and a `to_file=` artifact are never windowed, since both state the whole selection by definition.
- The accounting beside a read states the BODIES READ, not the list's length and not the window's: a malformed token and an id the named pole holds no version of never reach a read.
- The reverse direction has two walks and `walk.follow` tells them apart, never the form; `RecordsWalk.Ceiling` bounds only the PER-SEED reading of `walk.max_nodes` (the forward and carrier walks), not the transitive walk's one shared budget.
- The owned-child clause is registered where an annotated field line LANDED in the response, not where the annotation was decided, and one set per TIER, so no response states a clause over a field the cut took nor one clause covering both tiers; the room it may still take is reserved out of `max_chars` rather than appended past it.
- `project.form='info_order'` takes ONE off-order file on `source=`, folded where MO2 would load it, and every statement of that answer names it as a projection of the order with that file enabled rather than the live order; an active plugin is refused, because it is already in the merge.

## Pinned by
- `WriteProof` step 6, the read-proof oracle (`src/housecarl-generator`, run by `ci-all`) — the round-trip no-op, over every coercible value leaf the write surface drives.
- `RecordsBulkSelectTests.AMalformedFormidIsAPerItemErrorRowWhileTheOtherRowsStillResolve` and `TheIdentityJsonCarriesOneResolvedRowPerInput` — a bad formid is a per-item error, and the batch renders one row per input.
- `RuntimeFormIdTests.AMissingRecordInAnEslFlaggedPluginIsToldAboutCompaction` and `RecordsRemedyRepairTests.AndDoesNotBlameEslCompactionOnAPluginThatIsNotEslFlagged` — the ESL clause is stated on a light-flagged plugin and NOT on a plain full master.
- `RecordsRenderCostTests.ATreeGathersItsProviderBodiesPerPluginNotPerRow` — the tree fold's one walk per provider plugin per chunk.
- `RecordsRenderCostTests.LimitBoundsWhatATreeOverAScanReads_NotOnlyWhatItRenders` — the window bounds what the fold READS, not only what it renders.
- `RecordsRenderCostTests.ABatchBodyReadStopsWhenTheClientCancels` and `ACancelStopsAnEverythingRenderToo` — a cancelled batch and a cancelled render stop inside one record.
- `RecordsWalkCostTests.AWalkHoldsNoReachedBodiesPastTheGatherThatReadThem` and `AWalkSplitAcrossPassesReachesTheSameSetAndHoldsOnePass` — a walk holds one gather pass and nothing at return.
- `RecordsRemedyRepairTests.AScanComputesOneListHopRemedyForTheWholeScan` — the list-hop verdict is memoised per (element type, segment).
- `RecordsFieldFoldTests` — `[*]` and `[*count]` columns, and the read's truncation note surviving the fold.
- `BodyGatherEquivalenceTests.AGatheredBodyIsTheBodyTheSingleFetchReturns` and `AFaultedPluginIsNamedAndFallsBackToTheSingleFetch` — a gathered body equals the one-at-a-time body, and a faulted plugin's fallback raises the same exception type and message the direct fetch does.
- `RecordsRowsFormTests.GroupByStaysOnTheAggregateForm`, `ADepthOfOneWouldRenderNoRowsAndIsRefused` and `RecordsFieldFoldTests.TheTokenBelongsToTheFieldsForm` — the form-scoping bullet: a sub-parameter outside its form is refused by name.
- `RecordsRefusalGrammarTests.ARefusalRaisedInANoTransportHelperStillReachesTheCallerAsJson` and `TheJsonSentenceCarriesNoTextLaneErrorPrefix_ThePropertyNameAlreadySaysWhatItIs` — one refusal sentence, two transport shapes.
- `RecordsComparisonFormTests.Fold3F1_AScanLaneDeltasJsonEnvelopeCarriesExactlyOneSourceProperty` and `Fold3F1_AScanSeededWalksReEnteredSummaryStatesOneSourceArm` — one source statement per response, on both transports.
- `RecordsRemedyRepairTests.TheEverythingFormsTruncationNoticeNamesNoFieldSelector_ThatFormRefusesOne` and `TheFieldsFormStillNamesItsSelector_TheVocabularyIsPerFormNotPerTool` — the lever vocabulary is per form, not per tool.
- `RecordsRenderCostTests.AFormidsPoleReadCountsOnlyTheBodiesThePoleHeld` and `AnIdentityReadCountsOnlyTheIdsThatResolved` — the accounting counts bodies read, not the list.
- `RecordsRenderCostTests.AWindowedTreeSaysOnlyTheseRowsWereRead` and `PagingATreeReadsTheRowsTheWindowNoteNames` — the comparison window is taken on the keys, so only the windowed rows are read.
- `RecordsComparisonFormTests.ReReview_LimitWindowsTheSeedsOnly_BothCarriersOfTheOneSeedRender` — `limit=` windows the reverse carrier walk's seeds and not its carrier rows.
- `RecordsOwnedChildTests.ACapThatTruncatesTheAnnotatedFieldAwayStatesNoClauseOverIt`, `EachTiersClauseNamesOnlyItsOwnFields` and `AnAnnotatedResponseAnswersInsideItsMaxChars_TheClauseIsReservedNotAppended` — the owned-child clause bullet: earned at emission, one set per tier, reserved not appended.
- `RecordsInfoOrderFoldTests.AnActivePluginIsRefusedWithWhatToDoInstead`, `TwoOffOrderFilesAreRefused`, `AFoldedResponseSaysTheFileIsNotActiveAndWasPlacedLast` and `AShadowedCopyIsLabelledApartAndTheBannerSaysTheFilenameIsActive` — the `info_order` fold's one-file rule and its projection statement.

## Where
`src/housecarl-core/`: `ReadEngine.cs` (leaf read, emit, deep walk), `BodyGather.cs` /
`WinnerBodies.cs` (bulk bodies), `RecordLinks.cs`, `RecordArms.cs`, `RecordNaming.cs`,
`PathFold.cs`, `PluginFile.cs`.
`src/housecarl-mcp/`: `RecordReads.cs` (resolve, batch, poles, delta/tree, walk, scan),
`TreeFold.cs`, `FieldFold.cs`, `ReverseWalkBatch.cs`, `ScanDetailReader.cs`, `ReadSentences.cs`
(the read surface's prose; the check families' own sentences in that file belong to
`docs/architecture/check-family-tests.md`), `RecordsTools.cs` (the tool front: the four axes, the
lane decision, the response envelope, and the delta/tree/chain/info_order/summary/aggregate text
renders), `ReadTools.cs` (`Wire` — the shared text render: the epoch stamp, the resolve, batch and
scan renders, the owned-child clause bookkeeping, and the check families' sweep pieces).
Tool: `housecarl_records`.
