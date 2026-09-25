---
updated: 2026-09-25
covers: [src/housecarl-mcp/RecordsTools.cs, src/housecarl-mcp/RecordsTextRender.cs, src/housecarl-mcp/ReadTools.cs]
---
# The records tool front

## What it is
Above the read engine (`docs/architecture/read-engine.md`) sit the two tool-front files. `RecordsTools` is `housecarl_records` itself: it parses the
four axes, validates them against each other, decides which lane answers, and assembles the response
envelope every render carries. `ReadTools` is the text render that lane and the check families share —
`Wire`. Their own contracts are the form scoping, the refusal shape and the lane routing below, and the
argument grammar's shared tokenizer is `docs/architecture/read-engine.md`'s;
the json twin of the same responses is `docs/architecture/json-wire.md`.

## Contracts
- `project`'s sub-parameters are form-scoped: one passed outside the form that reads it is refused by name rather than accepted and dropped, and the rule is on the parameter, not its value, so `depth=1` refuses where `depth=2` does.
- A refusal is stated once, in the text spelling, and `Wire.Refuse` gives it the shape the caller asked for — `error: ` on text, the same bare sentence in an `error` property on json — so a refusal raised where no transport is in scope still reaches a json caller as a refusal document.
- One FormID door per call: `formids=`, `references=` and the walk seeds parse against one captured build, and the scan runs on that same build, so a call's own tokens cannot name records in two orders.
- A response carries exactly ONE `source` statement, first writer wins, so a lane that names its specific source and then falls through to a general pipeline cannot state two.
- The remedy and truncation vocabulary is a function of (tool, FORM), not of the tool: a notice never offers a lever the form it was written for refuses.
- Wherever two captures meet in one call — a walk or scan deriving a selection a reading form then reads, a source-arm probe ahead of a scan, an off-order fold ahead of a merge — the epochs are compared and a divergence refuses loud rather than answering from two builds.
- `limit=`/`offset=` are per lane. On the LIST lane they window the RENDER, while the census, the aggregate and every artifact write still cover the whole list. On the SCAN lane they window the scan's own SELECTION before any body is read, so a `form='fields'` scan at `limit=10` reads ten bodies and no more. On the derived-selection forms — the comparisons, `info_order`, a walk — the scan itself is uncapped and the window falls on their own rows, which on the comparison forms means the KEYS, windowed before any body is read because a delta or tree row reads every provider of its record. A census and a `to_file=` artifact are never windowed on any lane, since both state the whole selection by definition.
- The accounting beside a read states the BODIES READ, not the list's length and not the window's: a malformed token and an id the named pole holds no version of never reach a read.
- The reverse direction has two walks and `walk.follow` tells them apart, never the form; `RecordsWalk.Ceiling` bounds only the PER-SEED reading of `walk.max_nodes` (the forward and carrier walks), not the transitive walk's one shared budget.
- The owned-child clause is registered where an annotated field line LANDED in the response, not where the annotation was decided, and one set per TIER, so no response states a clause over a field the cut took nor one clause covering both tiers; the room it may still take is reserved out of `max_chars` rather than appended past it.
- `project.form='info_order'` takes ONE off-order file on `source=`, folded where MO2 would load it, and every statement of that answer names it as a projection of the order with that file enabled rather than the live order; an active plugin is refused, because it is already in the merge.

## Pinned by
- `RecordsRenderCostTests.LimitBoundsWhatATreeOverAScanReads_NotOnlyWhatItRenders` — the window bounds what the fold READS, not only what it renders.
- `RecordsRowsFormTests.GroupByStaysOnTheAggregateForm` and `RecordsFieldFoldTests.TheTokenBelongsToTheFieldsForm` — the form-scoping bullet: a sub-parameter outside its form is refused by name.
- `RecordsScanLaneTests.FormScoping_DepthOutsideTheFieldsAndEverythingFormsRefusesNamingTheRule` and `AnExplicitDepthOutsideItsFormsRefusesRegardlessOfValue` — the same bullet's second clause: `project.depth` on a form that does not read it is refused whatever the value, the second driving `depth=1`.
- `RecordsRefusalGrammarTests.ARefusalRaisedInANoTransportHelperStillReachesTheCallerAsJson` and `TheJsonSentenceCarriesNoTextLaneErrorPrefix_ThePropertyNameAlreadySaysWhatItIs` — one refusal sentence, two transport shapes.
- `RecordsComparisonFormTests.Fold3F1_AScanLaneDeltasJsonEnvelopeCarriesExactlyOneSourceProperty` and `Fold3F1_AScanSeededWalksReEnteredSummaryStatesOneSourceArm` — one source statement per response, on both transports.
- `RecordsRemedyRepairTests.TheEverythingFormsTruncationNoticeNamesNoFieldSelector_ThatFormRefusesOne` and `TheFieldsFormStillNamesItsSelector_TheVocabularyIsPerFormNotPerTool` — the lever vocabulary is per form, not per tool.
- `RecordsRenderCostTests.AFormidsPoleReadCountsOnlyTheBodiesThePoleHeld` and `AnIdentityReadCountsOnlyTheIdsThatResolved` — the accounting counts bodies read, not the list.
- `RecordsRenderCostTests.AWindowedTreeSaysOnlyTheseRowsWereRead` and `PagingATreeReadsTheRowsTheWindowNoteNames` — the comparison window is taken on the keys, so only the windowed rows are read.
- `RecordsComparisonFormTests.ReReview_LimitWindowsTheSeedsOnly_BothCarriersOfTheOneSeedRender` — `limit=` windows the reverse carrier walk's seeds and not its carrier rows.
- `RecordsOwnedChildTests.ACapThatTruncatesTheAnnotatedFieldAwayStatesNoClauseOverIt`, `EachTiersClauseNamesOnlyItsOwnFields` and `AnAnnotatedResponseAnswersInsideItsMaxChars_TheClauseIsReservedNotAppended` — the owned-child clause bullet: earned at emission, one set per tier, reserved not appended.
- `RecordsInfoOrderFoldTests.AnActivePluginIsRefusedWithWhatToDoInstead`, `TwoOffOrderFilesAreRefused`, `AFoldedResponseSaysTheFileIsNotActiveAndWasPlacedLast` and `AShadowedCopyIsLabelledApartAndTheBannerSaysTheFilenameIsActive` — the `info_order` fold's one-file rule and its projection statement.

## Where
`src/housecarl-mcp/`: `RecordsTools.cs` (the tool front: the four axes, the lane decision, the response
envelope), `RecordsTextRender.cs` (the same `RecordsTools` class: the delta/tree/chain/effect-chain/info_order/
summary/aggregate text renders), `ReadTools.cs` (`Wire` — the
shared text render: the resolve, batch and scan renders, the `info_order` body (`AppendInfoOrderView`), and the
owned-child clause bookkeeping; the transport helpers every tool front shares, the epoch stamp among them, are in
`ToolFrontWire.cs`, under `docs/architecture/json-wire.md`;
the check families' render now lives in `CheckTextRender.cs`, under `docs/architecture/check-families.md`).
Tool: `housecarl_records`.
