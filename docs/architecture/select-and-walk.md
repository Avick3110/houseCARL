---
updated: 2026-09-23
covers: [src/housecarl-core/FieldPredicate.cs, src/housecarl-core/ContainmentIndex.cs]
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
- The operators: `=` and `!=` compare across that whole token vocabulary; `>` `>=` `<` `<=` are
  numeric-only; `contains` and `startswith` are case-insensitive substring and prefix; `has`,
  `has_any` and `has_none` are BITWISE set-tests over a `[Flags]` enum or plain integer leaf — every
  bit of the operand set, at least one set, none set — which `=` (exact value) and the range ops
  cannot express, and whose operand is a bit value or a flag name; `exists` and `missing` take no
  operand; `in` and `not in` take a list. A leading `not` negates a string operator only.
- `editorid` reads the record's EditorID off the early EDID subrecord, never a reflection walk, so it
  answers on a record whose deep body Mutagen cannot parse. `winner` is the PROVENANCE term — which
  plugin wins the record, not its content — evaluated through the scan's bound resolution, so naming
  it forces winner resolution over the whole scanned scope.
- A membership list separates on commas and newlines ONLY, never a bare space, because a plugin
  filename can legally contain one; brackets and quotes are stripped per token, so a pasted JSON
  array parses as written. `@<absolute path>` reads a list FILE, and when that file is a result
  ARTIFACT its identity column IS the list and carries an epoch obligation the consuming scan checks
  against its own captured build, refusing loud on a mismatch and naming both epochs.
- A predicate never returns a silently wrong answer: every candidate that produced no value is
  accounted per cause (no such field, list hop, non-list quantified step, no containing record,
  container, read fault, unresolved link target, genuinely unset), and a numeric operator on a
  non-numeric field is a named `FatalError` on the first value-bearing candidate.
- The value operators take scalar-leaf paths only. `exists`/`missing` are the exception that matches
  a carried substruct or non-empty list; `in`/`not in` on `formid` test identity and read no body.
- `exists`/`missing` read a PRESENT subrecord carrying FormID zero as PRESENT rather than absent, so
  `missing:PreviousDialog` never matches a head-marked INFO (#697). An empty modeled list or dict is
  absent; a carried substruct is present; a no-such-field or a read fault matches neither operator.
- The accounting is loud or soft by ratio. No readable value on ANY candidate is a LOUD line saying
  the zero is not a confirmed nothing-matches; no value on MORE THAN HALF of them is a soft note; and
  a read FAULT on any candidate is said whatever the ratio, because those records could not be judged
  at all and passing them off as non-matches is the silently degraded answer.
- A predicate set whose every term is header-only — `editorid`, `winner`, `formid` membership, or a
  side led by a `*parent` hop — must still see DELETED records. A deleted record has no live body for
  a content filter, but its EditorID, its winner resolution and its containing record are real facts,
  which is what keeps a patch-deleted placed reference in the results of
  `where=["*parent.EditorID = SomeCell"]`, the crash-log lookup that step exists for.
- The near-miss hint fires only on a zero-row scan whose predicate set is nothing but one exact,
  un-negated `editorid =` term read on the candidate itself. With a second term ANDed in the zero has
  another candidate cause, so the hint would assert one it has not established.
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

## Pinned by

- `WhereGrammarTests` — the operator, operand and pseudo-path refusals at parse.
- `WhereQuantifierTests` — the quantified step's folds and its refusals, and its `has` / `has_any` /
  `has_none` block pins the bitwise set-test bullet including the zero-mask refusal.
- `PresentNullLinkRenderTests.PresenceSweepsTellTheTwoShapesApart` — the #697 bullet: a head-marked
  INFO answers `exists` and only a plain one answers `missing`.
- `RecordsContainmentTests.ADeletedPlacedReferenceIsStillFilteredByTheCellThatHoldsIt` — the
  header-only-set-still-sees-deleted-records bullet, with a body-reading predicate dropping it beside.
- `ValuePredicateProbe` test 10b (`src/housecarl-generator`) — the loud-versus-soft accounting bullet:
  a path wrong for more than half a mixed scan gets "had no value on", not the loud line.
- `RecordsArtifactTests` — `formid in @<artifact>` re-entry, its epoch check, and
  `APlainAtFileListStillEntersWithNoManifestAndNoEpochClaim` for the plain list file beside it.
- `WhereContainmentTests` / `RecordsContainmentTests` — the `*parent` step and its no-verdict rollups.
- `WhereContainmentCostTests` — `ParentBodiesHeld` / `ParentBodyHighWater` / `ParentBodyFetches`, the
  #720 invariant that no containing record outlives the candidate that read it.
- `WhereAccountingCauseTests` — the per-cause accounting sentences.
- `WhereNearMissTests.ASecondPredicateGetsNoSentence_TheZeroHasAnotherCandidateCause` and
  `AContainsTermGetsNoSentence_TheHintIsForTheExactSpellingOnly` — the near-miss hint's sole-term
  rule. They assert the rendered sentence, so they pin that bullet and not `ExactEditorId` itself,
  which no test names.
- `ValuePredicateProbe` (`src/housecarl-generator`) — the by-construction extraction over the corpus.

## Where

`src/housecarl-core/FieldPredicate.cs` (the `where=` grammar and its accounting),
`ContainmentIndex.cs` (the child-to-parent map and the `*parent` grammar).
Entry points: `housecarl_records` (`where=`).
