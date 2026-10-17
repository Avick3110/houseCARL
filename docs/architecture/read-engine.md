---
updated: 2026-10-17
covers: [src/housecarl-core/ReadEngine.cs, src/housecarl-core/ConditionLine.cs, src/housecarl-core/BodyGather.cs, src/housecarl-core/WinnerBodies.cs, src/housecarl-core/RecordLinks.cs, src/housecarl-core/RecordArms.cs, src/housecarl-core/OrderedGather.cs, src/housecarl-core/RecordNaming.cs, src/housecarl-core/PathFold.cs, src/housecarl-core/PluginFile.cs, src/housecarl-mcp/RecordReads.cs, src/housecarl-mcp/RecordPoles.cs, src/housecarl-mcp/RecordWalk.cs, src/housecarl-mcp/RecordQuery.cs, src/housecarl-mcp/TreeFold.cs, src/housecarl-mcp/FieldFold.cs, src/housecarl-mcp/ReverseWalkBatch.cs, src/housecarl-mcp/ScanDetailReader.cs, src/housecarl-mcp/ReadSentences.cs, src/housecarl-mcp/ScopeSplit.cs, src/housecarl-mcp/BodyPrefetch.cs, src/housecarl-mcp/PoleGather.cs, src/housecarl-mcp/PluginPaths.cs, src/housecarl-core/FieldsDiff.cs]
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
- `Display`, `Link`, `NoteRef`, `Bytes`, `BytesFormVersion` and `Slots` are display-only — never part of the round-trip token, so write and read-proof never see them; the conflict diff prints a flags decode in brackets beside its token, which cannot change what differs because the decode is a function of the bits.
- A `[Flags]` value decodes one way in every format (`ReadEngine.FlagDecode`): when any set bit is unnamed, the decode lists the named bits and one `slotNN` (biped) or `bitN` token per unnamed bit, and a `BipedObjectFlag` field also states its slot numbers (slot = 30 + bit) unless every set bit is already a `slotNN` token. Its parts are joined by ` | `, never `; ` or `, `, which the diff lines use between deltas. Bits are read at the enum's own width, so an `int` flag with bit 31 set is one `bit31`. json always carries a biped value's slot numbers as a numeric `slots` array beside the raw value, `[]` when none is set.
- Presence is carried structurally (`Present`, `Readable`, `Count`), never decided by matching a note's prose: an unreadable leaf is not evidence of absence.
- A fault is isolated to the leaf, the list element or the child it happened on; the line names itself and the walk carries on with its siblings.
- The deep walk generates at most `MaxExpandNodes` lines and emits one truncation note at the bound.
- A `project.fields` column `List[*].Sub` is always its own read, whatever else the call reads off `List`: the fold passes `Sub` as parsed segments beside the read path (`ReadFields`' per-path `depths`), and the walk emits the list's own line, then only `Sub` off each element at depth 1, the one line its cell renders, so the budget counts those lines. The fold runs every sub-path read before any whole read, so a whole `List[*]` that overruns the budget cannot starve the column; the whole read keeps its own cut note. A column reading `List` whole reads it again beside that, and when the plan has such a pair the fold keeps a line both reads emit once. An element without `Sub` gives no line, as before; a fault on the way to it is its own line. When no element resolves `Sub` because no such field exists or a bracket key is the wrong shape (`NavigateValue`'s structured no-such-field flag, not the note's prose), the walk's own note is said once, on the first element, so a mistyped path never reads as an absence. A path string handed to `ReadFields` never carries `[*]`: only the fold reads a sub-path off each element.
- Expansion stops at the modeled corpus: a value outside Mutagen/Noggog renders its summary line and is not descended.
- A condition element's summary line is one compact line, `[HasPerk(058200:Skyrim.esm) == 1 on Subject OR]`, from `ConditionLine` through `ConditionHead` on both display paths (the deep walk and the depth-1 read): the function, the parameters the concrete Data arm declares minus Mutagen's `*Unused*`-named ones, the operator, the comparand (a float or a GLOB link), the run-on target with its Reference (run-on Reference) or index (QuestAlias, PackageData), and the flag names with OR last. It nests no brackets, so its first `]` closes it, and it keeps the leading `[` that the depth hint, the rows-form trim and the `where=` container and presence checks key on, and its `NoteRef` is the first FormID it spells. A part that cannot be read makes the element the ordinary fault line, `Readable=false`. It is display only: a presence or `where=` read of the element stays `[ConditionFloat]` and never builds the line; one depth level below the element prints every sub-field as before.
- `DepthExpandHint` names `depth=2` and is only honest on a surface that HAS a `depth=` parameter; a surface that refuses depth passes its own redirect as `containerHint`, or null to suppress it.
- `CollectLinksAt` / `LinksIn` answer an EMPTY list for a present-but-empty field — a genuine "no links here" — and null only for "not a link-bearing path", whose note reuses the leaf-read vocabulary so a caller classifies it like a leaf miss.
- Winner resolution is NOT the core read's — it reads the body it is handed. `RecordReads` resolves the winner, and every outcome of one response carries the `ViewPin` it was answered from, so tree, touching list and epoch stamp name one build.
- A read that cannot answer returns a recoverable NAMED error — not in the order, plugin does not touch it, fetch inconsistency — never a silent empty result; `ResolveRead` reads the winner's body unless a plugin is named, in which case it reads that plugin's override.
- A batch returns one outcome per input, in input order, and a bad or absent formid is a per-item error that does not fail the batch.
- An unresolved FormID names which of the three causes it is — the defining plugin was excluded, it is not in the order, or it IS in the order and defines no such record — and the ESL-compaction clause is stated only where the index says that plugin is light-flagged.
- Bodies are gathered in bulk (`BodyGather`, `WinnerBodies`, `BodyPrefetch`): an eager `BodyGather.Gather` reads its (plugin, key) pairs through the door, each (plugin, group) once in load order, and keeps the bodies for its caller, while the on-demand gather that `BodyPrefetch` builds walks one plugin's declared keys when a row first wants it; each walk is typed by the order index (load-order-resolver.md), and the gather is never a second error path — but what a faulted plugin then answers is the lane's own choice, and it is one of three: under `BodyGather.Absent.Seek` the pair falls back to the per-record fetch and raises the same fault, in the same words, from the same place in the caller's own loop; under `Absent.Null` (which `BodyPrefetch` constructs with) it answers null and the caller's own read raises whatever it raises; `WinnerBodies` reads by the returned map alone, so the candidates a faulted winner plugin could not yield (every one when it will not open, the broken group's when one group faults) are simply ABSENT and the plugin is named once in `unreadable`, never re-fetched per record.
- The read door (`OrderedGather.ForEachGroup`) takes every key a call will read, as winner keys or (plugin, key) pairs, sorts them by load position and then by Mutagen group (`OrderedGather.Sort`; seek types whose groups overlap in one plugin share a bucket, and a plugin outside the order gets buckets of its own), walks each group once and hands its bodies to the caller, which keeps only answers and verdicts before the group is dropped. Cancel and the caller's stop test are checked between groups. A faulted group keeps what its walk yielded and the plugin's first fault goes to the caller's `unreadable`. A plugin that will not open has its later groups handed over unread; a group that opened but could not be walked (`PluginUnscannableException`) leaves the plugin's other groups to be walked, so one broken group never hides a healthy one and the answer does not depend on group order.
- A `where=` with a `->` or own-side `*parent` term on the `types=`/`plugins=` scan primes through the door first (`FieldPredicateSet.PrimeOrdered`): one pass over the candidates collects every target, container and dead-end record (containment alone climbs the index from the candidate keys and reads no candidate body), then each group is judged once. Only verdicts are memoised, and none while an error is set. A key its group did not yield, a nested hop's target, and every key after a typed error are left to the scan, which reads them per record as before, so a typed error names the same record it names without the door. `where_source=winner` does not prime.
- The `formids=` universe with no `where=` judges each key through the door; a kept row's summary is built only while at most `offset+limit` kept rows precede it, and rows are counted and sampled in the universe's order, as the chunked lane counts them. Every key a faulted winner plugin holds counts as unscannable, whatever its groups yielded before the fault, so the answer does not depend on group or key order.
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
- A `plugins=` scan reads each record once, from the highest-loading scoped plugin that touches it, whatever order the names came in, and `where=` (scoped) judges that copy, so a lower copy that would pass makes no match.
- A per-record fault is isolated and accounted, never silent: an unscannable record, a leniently read one and an unreadable plugin are three separate counts in the scan's own note.
- The conflict-tree diff compares DEEP reads, not depth-1 rendered lines. Positional lists compare as
  order-insensitive multisets of whole elements, with a pure reorder reported as its own delta; dict
  brackets are semantic keys and compare by exact path. A CAP suppresses one-sided deltas and the
  agreed count record-wide; an UNREADABLE leaf suppresses only that path and the list element
  comparison it sits in. An empty delta list with `Complete` false must never render as identical.
- A path with no field on BOTH sides is a wrong path, not two equal values: it is a NO FIELD line,
  not compared, and leaves the comparison incomplete. It is counted apart from an unreadable leaf
  (`NoFieldCount`, json `no_field_count`), and its incomplete note says to fix the path rather than
  narrow `fields=`, which is the remedy for a read fault or the cap. A no-field note on one side only
  stays a line and is a shape difference.
- The agreed count counts only exact-path VALUE leaves read on BOTH sides — never a container
  summary, never a side's absent or null-link sentinel, because an absent field is not an agreement.
  Per-field presence is reliable only for nullable fields, whose absence the read engine spells, so a
  non-nullable scalar equalling the winner counts as agreement while the render never claims the
  contributor CARRIES it as a distinct subrecord.
- The shared-EditorID note (#1076): the game keeps one game setting per name, and the last plugin in load order carrying any GMST of that name sets it (checked in game: `GetGameSetting iAllyHitNonCombatAllowed` returns Requiem's value over USSEP's). Each FormID's winner is still its own winner; the note is an annotation, not a change to resolution. A row whose type is in `SharedEditorIds.EngineKeyed` (data, one entry: `IGameSettingGetter`) and whose FormID shares its winner's EditorID (case-insensitive) with another winner of that type in the active order carries one sentence from `ReadSentences.SharedEditorIdNote`: the other FormIDs (three named, the rest counted) and the copy the game uses, the winner loading last. A unique name carries nothing. text puts it on a `note:` line under the row, json in `editorid_note` beside `editorid`, and dense keeps the `editorid` cell the bare EditorID and lists the notes after the rows in `editorid_notes`, one `{formid, editorid_note}` per rendered record, the way it lists failed reads in `errors`; each note is charged against `max_chars` as its row is, so a dense answer whose notes would not fit is cut and says `truncated`. It rides the summary, fields, rows and everything forms on the formids=, scan and pole lanes, the tree and delta rows (one row per FormID, so the note is the FormID's, whatever version the row diffs), and the `to_file=` artifacts of all of these; walk chains do not carry it. A row read from a body that is not its FormID's active winner (a `plugins=` scope whose plugin is overridden, an off-order file, a delta subject off the order) carries no note: the game does not use that copy, and its EditorID may not be the winner's.
- The note's lookup is built once per call and dropped with the call's `ViewPin` (`ViewPin.SharedNames`); nothing is held at rest. A winner scan whose types cover every GMST arm (`types=[GMST]`, not a `plugins=` scope) builds it from the winners it already streams and hands it to the pin (`ViewPin.Seed`), so the scan walks the GMSTs once. Any other call builds it on the first keyed row a render asks about, by its own winner walk of the keyed types; a call with no keyed row never builds it. On ARR (1,865 GMSTs) that walk costs about 0.7 s, which a one-GMST read pays: the index maps a FormKey to its winner with no type or EditorID, so nothing cheaper answers which other FormIDs carry the name. An active plugin the index excluded (the plugins the scan's unjudged clause counts), one the walk cannot open, or one whose records stop partway on a parse fault (`PluginUnscannableException`, collected by `WinnerRecordsOfType` like the open fault) is named on every keyed row's note, since a missed member could hide a shared name; the read itself still answers. Only a parse fault is collected (`RecordParseFault`, the set the walk lane uses): a cancellation, running out of memory or a bug goes on up.

## Pinned by
- `RecordsScopeCopyOrderTests` — a two-plugin scope answers the same rows under either name order, and a filter only the lower copy passes matches nothing.
- `WriteProof` step 6, the read-proof oracle (`src/housecarl-generator`, the manual `read-proof` mode) — the round-trip no-op, over every coercible value leaf the write surface drives.
- `RecordsBulkSelectTests.AMalformedFormidIsAPerItemErrorRowWhileTheOtherRowsStillResolve` and `TheIdentityJsonCarriesOneRowPerInput` — a bad formid is a per-item error, and the batch renders one row per input.
- `RuntimeFormIdTests.AMissingRecordInAnEslFlaggedPluginIsToldAboutCompaction` and `RecordsRemedyRepairTests.AndDoesNotBlameEslCompactionOnAPluginThatIsNotEslFlagged` — the ESL clause is stated on a light-flagged plugin and NOT on a plain full master.
- `RecordsRenderCostTests.ATreeGathersItsProviderBodiesPerPluginNotPerRow` — the tree fold's one walk per provider plugin per chunk.
- `ComparisonBatchReadTests` and `ComparisonBatchOffOrderReadTests` — a delta walks no pole plugin for a row that pole holds nothing of (a named subject or reference, a post-state subject whose replay is unavailable, an off-order subject), and still gathers the reference for a post-state or off-order subject that holds its rows.
- `ComparisonBatchReadTests.AbsentIdsExplainTheirPluginOnce` — the view explains an absent plugin from the profile once per pin: tree, delta (winner, pre, post), `fields` (plain and post) and `info_order` each read it once for 50 ids.
- `RecordsRenderCostTests.ABatchBodyReadStopsWhenTheClientCancels` and `ACancelStopsAnEverythingRenderToo` — a cancelled batch and a cancelled render stop inside one record.
- `RecordsWalkCostTests.AWalkHoldsNoReachedBodiesPastTheGatherThatReadThem` and `AWalkSplitAcrossPassesReachesTheSameSetAndHoldsOnePass` — a walk holds one gather pass and nothing at return.
- `RecordsRemedyRepairTests.AListHopRemedyComesFromThePlanSoNoScannedRecordComputesIt` — the list-hop remedy comes from the plan, so no scanned record computes it.
- `ConditionSummaryLineTests` and `ConditionSummaryLinePresenceTests` — the compact condition line on both summary paths, the full form one level below it, and `Conditions[0] exists` still present.
- `RecordsFieldFoldTests` — `[*]` and `[*count]` columns, and the read's truncation note surviving the fold.
- `DoorGroupReadTests` — a `->` scan reads its target group once and a `*parent` scan its container group once across two scan chunks, a formid universe past one chunk reads its winner group once, and a typed error names the candidate-order record, not the door's first.
- `BodyGatherEquivalenceTests.AGatheredBodyIsTheBodyTheSingleFetchReturns` and `AFaultedPluginIsNamedAndFallsBackToTheSingleFetch` — a gathered body equals the one-at-a-time body, and a faulted plugin's fallback raises the same exception type and message the direct fetch does. `BodyGatherDoorTests` — faults are named in declaration order, what a faulted plugin yielded is kept, a gather over two plugins and two groups reads each (plugin, group) once, and a broken NPC_ group (which sorts before WEAP) still lets `WinnerBodies` and a `formids=`+`where=` scan return the plugin's weapons and name the plugin.
- `PresentNullLinkDiffTests.ACarriedHeadMarkerDeltasAgainstASideCarryingNothing` — the diff's
  present-null-link split, and `TwoAbsentSidesStillCollapse` the two-absent case.
- `SharedEditorIdNoteTests` — a shared GMST name names the other FormIDs and the last provider on text, json, dense, fields, tree, delta and scan rows; an overridden member is named by its winner, not its defining plugin; a scan's notes equal the walked ones; a parse fault partway through a plugin and an index-excluded plugin are named in the note, not thrown, while a cancellation inside the stream still throws; a scoped row the game does not use carries no note; a dense scan's notes count against `max_chars`; a unique name carries no note.
- `ReadPinTests.AProfileSwitchAfterThePinDoesNotSplitThePoleFromItsRoots` — a source= pole is located under the
  roots taken in its view's hold and opens with that hold's Data folder, across a mid-call profile switch.

## Where
`src/housecarl-core/`: `ReadEngine.cs` (leaf read, emit, deep walk), `BodyGather.cs` /
`WinnerBodies.cs` (bulk bodies), `OrderedGather.cs` (the read door), `RecordLinks.cs`, `RecordArms.cs`, `RecordNaming.cs`,
`PathFold.cs`, `PluginFile.cs`, `FieldsDiff.cs` (the deep comparison behind `project={"form":"tree"}`).
`src/housecarl-mcp/`: `RecordReads.cs` (resolve, the pinned per-match fill `ResolveReadOn`, `ResolveRefOne`,
`ArtifactEpochMismatch`, the pairwise record diff `DiffPole`, batch, the one-pole batch, info order, the dialogue
fold), `RecordPoles.cs` (comparison poles, the delta/tree batches), `RecordWalk.cs` (walk), `RecordQuery.cs`
(cross-plugin query, off-order scan, effect chain), `ScopeSplit.cs` (the scan's `plugins=` split),
`BodyPrefetch.cs` and `PoleGather.cs` (bulk bodies for a chunk of rows and of pole reads),
`TreeFold.cs`, `FieldFold.cs`, `ReverseWalkBatch.cs`, `ScanDetailReader.cs`, `SharedEditorIds.cs` (the shared-name note), `ReadSentences.cs`
(the read surface's prose; the check families' own sentences now live in `CheckSentences.cs`, under
`docs/architecture/check-families.md` and
`docs/architecture/check-scripts-and-dialogue-families.md`). The type lookup is in `TypeLookup.cs`,
under `docs/architecture/corpus-rulebook.md`. The pole lanes (`ProbeSourceArm`, `ResolveBatchFromPole`,
`DeltaBatch`, `TreeBatch`) and the overlay source's `OverlayPostBatch` run the seam `AfterReadPinForGuard` inside their hold;
the four pole lanes take the view and the MO2 roots in one hold through `CapturePinAndRoots` on the shared door,
and an off-order `PoleInfo` carries the `DataDir` it was located under. When a pole is the SkyPatcher overlay in its post state,
`DeltaBatch` and `TreeBatch` take `CapturePinAndAssets` instead and read the roots off that asset build, and
`OverlayPostBatch` (the overlay `source=`) always does, so the INI layer replays over the asset build pinned with the
winners; no other pole, the overlay's pre state included, pays for an asset build. An asset build that throws is
named by the replay's own sentence ("the SkyPatcher layer could not be discovered for the overlay pole/source"),
over a pin taken again so the rows and the epoch line still read.
Tool: `housecarl_records`.

The five service files are one class, `RecordReads`, which the head builds over itself as `_reads` and reaches
through one-line delegators with the old names and defaults, one per overload the product calls:
`ResolveReadOn`, `ResolveSummaryOn`, `ResolveBatch`, `ResolveBatchFromPole`, `ProbeSourceArm`,
`DeltaBatch`, `TreeBatch`, `OverlayPostBatch`, `WalkForwardBatch`, `InfoOrderBatch`, `CrossQuery`, `OffOrderQuery`,
`ResolveEffectChain`. Seven of them (`ResolveBatchFromPole`, `ProbeSourceArm`, `OffOrderQuery`, `DeltaBatch`,
`TreeBatch`, `WalkForwardBatch`, `InfoOrderBatch`) and the checks delegator `CheckDialogue` are `internal`, not
`public`, because their signatures carry a type nested in the internal `RecordReads`. `ReadArea` hands tests and probes the instance, for the members only they call
(`ResolveRead`, `ResolveTreePinned`, the single-type `CrossQuery`) and to set
the pole and overlay source lanes' seam `AfterReadPinForGuard`. The
statics and nested types (`PoleInfo`, `PoleSpec`, `DeltaRow`, `TreeRow`, `WalkSeedResult`, `InfoOrderRow`,
`LinkMemo`, `ArtifactEpochMismatch`, `UnresolvedFormId`, `OpenDialogueFold`, `FoldLabel` and the rest) are
addressed as `RecordReads.X`.

What the class needs from outside itself is the members of `IReadHost`, declared at the top of `RecordReads.cs`:
`OpenSkyPatcherReplay(captured, …)`, relayed from the assets area (the overload over an `AssetCapture` the caller
took in its pin's hold), which the SkyPatcher overlay pole and source read through. The resolver, the type lookup,
the corpus rulebook, the pin-and-roots capture and the pin-plus-assets hold are `Resolver`, `Types`, `Rulebook`,
`CapturePinAndRoots(afterPin)` and `CapturePinAndAssets(afterPin)` on the shared door. The members every area
shares come through the door it extends,
`ILoadOrderHost` in `src/housecarl-mcp/LoadOrderHost.cs` ([`load-order-service.md`](load-order-service.md)).
Outside the interface it calls one static of the output area, `OutputLocations.LocatePluginFileOnDisk`
in `OutputLocations.cs`, and names the head's `LoadOrderService.ViewPin` record. The path helpers it shares with
the write and output lanes (`LooksLikePath`, `SamePluginFile`, `ActiveNameForPath`) are the static class
`PluginPaths` in `src/housecarl-mcp/PluginPaths.cs`, which belongs to no area. That class also holds the two MO2-layer
helpers (`LayerOfInstallPath`, `InstallLayerOfPath`), used by the assets area (`NativePairingAudit`) and the copy lane
(`BuildSourceChain`).
