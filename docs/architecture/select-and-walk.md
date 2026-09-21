---
updated: 2026-09-21
covers: [src/housecarl-core/FieldPredicate.cs, src/housecarl-core/ContainmentIndex.cs, src/housecarl-core/ClosureWalk.cs, src/housecarl-core/SourceChain.cs, src/housecarl-core/ReverseReferenceIndex.cs, src/housecarl-core/FieldsDiff.cs, src/housecarl-core/LocalizedStrings.cs]
---
# Select and walk

## What it is

The selection half of the tool surface: the `where=` value predicate, the two edge kinds it and the
walk construct travel (the form link, and containment), the closure walk and its ordered source
universe, and the whole-order reverse index behind an unbounded `references=`. Beside them sit the
two things a selection's answer is rendered against: the deep field comparison behind the conflict
tree, and the localized-strings classifier that decides what a refusal may say about a plugin's text.

Nothing here carries a record-type vocabulary. The predicate extracts through the read engine's own
path walk, the walk expands through Mutagen's `EnumerateFormLinks`, containment comes off Mutagen's
context walk, and the localized language set is Mutagen's `Language` enum.

## Contracts

- A filterable path is exactly a readable path: the predicate pulls each candidate's value through
  `ReadEngine.ReadLeaf`, and the comparison vocabulary is the token forms that walk emits — enum
  name, invariant numeric round-trip, `True`/`False`, `XXXXXX:Plugin.esp`.
- A predicate never returns a silently wrong answer: every candidate that produced no value is
  accounted per cause (no such field, list hop, non-list quantified step, no containing record,
  container, read fault, unresolved link target, genuinely unset), and a numeric operator on a
  non-numeric field is a named `FatalError` on the first value-bearing candidate.
- The value operators take scalar-leaf paths only. `exists`/`missing` are the exception that matches
  a carried substruct or non-empty list; `in`/`not in` on `formid` test identity and read no body.
- An operand or list entry that mixes the two FormID notations is refused at parse; a bare runtime
  FormID is resolved through the call's own FormID door, so it compares as the record it addresses.
- A leading `not` flips a string operator's verdict only where that verdict is DEFINITE, so a
  mistyped path under `not contains` cannot match everything.
- A quantified step (`[*any]`, `[*all]`, `[*none]`, `[*count]`) folds elements into a boolean or a
  number; the bare `[*]` set token is refused. An empty list is a definite verdict, and one element
  that could not be judged sinks a fold the judged elements have not already decided.
- `*parent` is the containment step and leads a path by definition. Its grammar is
  `ContainmentIndex.SplitHops`, shared with the read walk, so both surfaces refuse the same mistake
  in the same words. Everything below the hop reads the containing record with no second rule.
- The containment climb is index-only: a chain reads a body for the record its terms run on, and
  what carries across candidates is the VERDICT, not the body. The link-target cache is deliberately
  not shared with the hop — `types=` bounds the child population, not the parent one (#720).
- Containment is captured at index build from Mutagen's context walk, merged per plugin in priority
  order with the last declaration standing, and a plugin that throws part-way merges nothing.
- The closure walk's source universe is an ORDERED list of poles tried in order, first hit wins. It
  is fallback, never merge, and never the set-valued SOURCE a comparison uses. An arm that HAS the
  record but cannot parse it STOPS the chain rather than answering with a later arm's bytes; a miss
  names every arm consulted, not the last one tried.
- Walk provenance is per node: each reached node records which arm produced its body and the full
  pull chain from a seed, which is what makes a cap refusal actionable.
- A seed path's shape is decided off the DECLARED type before any value is read, so a null cannot be
  mistaken for an absence of shape; null and empty are states of a shape, not shapes. An unknown or
  unsupported seed path is a refusal, never zero seeds.
- Cycles are found by a post-walk pass over the recorded edge set, not by the BFS parent map: a
  mutual reference between two siblings is a real cycle no ancestry test can see. Any other repeat
  is a re-convergence and is deduped. A refusal carries nothing usable rather than a partial copy.
- The reverse index is lazy, partitioned per plugin and keyed on (path, mtime) beside the order-wide
  epoch, generational so a read is never torn, and plugin-atomic so a half-read plugin leaves no
  partial edges. It answers in CANDIDATES; the scan that follows still judges the body the caller
  means. A plugin the walk could not read makes the positive question SHORT and the orphan sweep
  OVER-inclusive, and both readings are stated. Its measured build cost and held size are in
  `dev/projects/tool-surface-2.0/SPEC.md` §3.2 and its 2026-09-05 amendment.
- A transitive reverse walk spends its node budget BEFORE a candidate is verified, so a spent budget
  stops the body reads as well as the reach and a raised budget on a retry sees the same graph; the
  hop the cut landed on is marked rather than reading as a hop that reached nothing.
- The conflict-tree diff compares DEEP reads, not depth-1 rendered lines. Positional lists compare as
  order-insensitive multisets of whole elements, with a pure reorder reported as its own delta; dict
  brackets are semantic keys and compare by exact path. A CAP suppresses one-sided deltas and the
  agreed count record-wide; an UNREADABLE leaf suppresses only that path and the list element
  comparison it sits in. An empty delta list with `Complete` false must never render as identical.
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

- `WhereGrammarTests` — the operator, operand and pseudo-path refusals at parse.
- `WhereQuantifierTests` — the quantified step's folds and its refusals.
- `WhereContainmentTests` / `RecordsContainmentTests` — the `*parent` step and its no-verdict rollups.
- `WhereContainmentCostTests` — `ParentBodiesHeld` / `ParentBodyHighWater` / `ParentBodyFetches`, the
  #720 invariant that no containing record outlives the candidate that read it.
- `WhereAccountingCauseTests` — the per-cause accounting sentences.
- `WhereNearMissTests` — `ExactEditorId` and the near-miss hint's sole-term rule.
- `ValuePredicateProbe` (`src/housecarl-generator`) — the by-construction extraction over the corpus.
- `ClosureWalkProbe` / `SourceChainProbe` — the walk's caps, cycles and refusals, and the chain's
  first-hit-wins, fault-stops and miss-names-every-arm rules.
- `RecordsWalkCycleTests` / `RecordsWalkCostTests` / `RecordsWalkUnscannableTests` — the walk lanes.
- `RecordsReverseIndexTests` — the unbounded reverse selection, the orphan sweep and the index's
  in-band accounting line.
- `PresentNullLinkRenderTests.ACarriedHeadMarkerDeltasAgainstASideCarryingNothing` — the diff's
  present-null-link split, and `TwoAbsentSidesStillCollapse` the two-absent case.
- `LocalizedStringsSourceTests` / `LocalizedModFolderUnreadableTests` / `StatusLocalizedLookupTests`,
  and `StringsResolveProbe` / `StringsDecisionProbe` / `LocalizedShapeSweep` — the shapes and the
  three-answer folder reads.

## Where

`src/housecarl-core/FieldPredicate.cs` (the `where=` grammar and its accounting),
`ContainmentIndex.cs` (the child-to-parent map and the `*parent` grammar),
`ClosureWalk.cs` and `SourceChain.cs` (the walk and its ordered source universe),
`ReverseReferenceIndex.cs` (the reverse edge and the unbounded reverse selection),
`FieldsDiff.cs` (the deep comparison behind `project={"form":"tree"}`),
`LocalizedStrings.cs` (the strings-shape classifier).
Entry points: `housecarl_records` (`where=`, `references=`, `walk=`), and the write lanes' pre-flights
that call `LocalizedStrings.RefusalFor`.
