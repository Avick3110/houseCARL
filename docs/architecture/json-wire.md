---
updated: 2026-09-23
covers: [src/housecarl-mcp/JsonWire.cs, src/housecarl-mcp/RenderCap.cs, src/housecarl-mcp/SkseJsonDoc.cs]
---
# The json wire: the shape a machine-readable response is allowed to take

## What it is

This file is the home of the json transport's own contracts — what a document may say and how it may say it. The
budget those documents are written against is a different subject and lives in
`docs/architecture/render-budget.md`: `max_chars` counts characters, `CharCountedStream` is where a json lane takes
its length from, `JsonTextEncoder` is the one encoder, and `BoundedBody` is the one place a merged check appends
anything a cap can refuse. Nothing here restates any of that.

## Contracts

### One serializer per read tool, over the text lane's own outcome objects

Each renderer consumes the SAME outcome object the text `Wire` consumes, so text and json can differ only in
formatting, never in data. Field values are the same wire tokens the text mode emits, so a token read out of json is
still a value a write can reuse verbatim.

A json document is never a silently degraded mode. Truncation drops trailing ROWS and flags it (`truncated:true`
alongside `rendered`); it is never a cut of the serialized string at a byte budget, which would emit malformed JSON.
The accounting and the notes ride inside the document rather than beside it.

### `ok` is a discriminant, and it is document-level only

- A whole-call refusal **on the read surface** writes `ok:false` and the message through `WriteRefusal`, so the shape
  cannot be stated one way in one renderer and another in the next. The write lanes write `ok` themselves, on both
  outcomes (below), and must not also call `WriteRefusal` — that would put a second `ok` in the document.
- A per-ROW `error` — a malformed FormID in a batch, a seed that did not resolve — is **not** a refusal. The call
  succeeded and rendered a row that failed. Those sites keep a bare `error` and must never gain `ok`.
- On the **read** surface `ok` marks refusals ONLY: a served read document carries no `ok`, and its absence means the
  call was answered. The **write** surface writes the flag on both outcomes. The asymmetry is deliberate.
- An integer never goes under `ok`: the census document names its resolved count `resolved`, because an integer under
  the discriminant would read as a refusal to one consumer and fail to parse for another.
- The epoch is left to the call site. Some refusals stamp the build they consulted, some state it as null, and
  pre-capture validation refusals omit it because they consulted no build.

### Null, empty and zero are three different answers

- `null` says the value was NOT COMPUTED — a finding class the caller excluded, a split that was not made, a list
  nobody looked for. A class emitted as `0` would parse as one that came back clean.
- `[]` says it was computed and came back empty.
- A number says it was counted.

`WriteNullableStringArray` is the array twin of `WriteNullable` for members whose null carries that meaning.

### The epoch stamp and the degraded-order marker

`order_degraded` is a **sibling** of the epoch, never folded into it: the epoch string is opaque and compared only for
equality, so folding health into it would leave two builds that differ only in health comparing as merely "different".
The marker is silent on a healthy order.

The sentence is stated once per document, at the root. A family head inside the merged check carries the flag and the
count (`order_degraded_plugins`) instead, because the roster already names those plugins with their reasons and the
fixed part comes out of the budget the findings are listed from.

`epoch_covers_all_inputs` is false when off-order files were swept beside the index, or when the family reports
verdicts read off another substrate, which `epoch_uncovered` names.

### A capped STRING list is an array plus a sibling count

Where a list of plain strings is bounded — the build-level caveat blocks, and `Wire.ContestedHostsShown` on the text
lane — what did not fit is a sibling `<name>_omitted` number, never a prose marker element inside the array. A marker
element would be handed to a consumer iterating the array as if it were an entry, and the array length would stop
matching the count the accounting states.

An array of OBJECTS is cut the other way, and that is not a violation of this: `WriteFieldsArray` closes a
field-truncated `fields` with a sentinel object (`{path:"…", note:"[truncated at max_chars: …]"}`), because a row
there is already an object with a `note` member and the sentinel reads as one more of them. Consumers and tests read
that row; do not replace it with a `fields_omitted` sibling.

### A document that overran its cap says so, in one member

A json document that could not fit `max_chars` ships over the ceiling, as the text lane's does, and closes with
`max_chars_overrun` — ONE member, written at every capped document's root close by `JsonWire.WriteCapOverrun`, whose
sentence is `RenderCap.Overran`, the same sentence the text lane's `RenderCap.Settle` appends. It names three
numbers: the document's own length, the `max_chars` it was given, and the cap THIS document would have fitted in. The
member is part of the length it states, so it is settled to a fixed point.

The third number is about **this** answer. A wider cap admits more rows, so the next call's document can overrun again
on its own terms — that is a bigger answer, not a broken remedy. The merged `check` document is the one that promises
one-step clearing, because its accounting adds the growth term for every place the response prints the cap back.

**One member, one grammar for the numbers.** The merged `check` document writes the same member from its own
`CheckAccounting.CapTooSmall`, because it tells the two overruns apart — a `max_chars` too small for the fixed part,
and a body unit measured after it was written — and adds the cap-print-site growth term to its remedy. Those are
facts the other documents do not have, so the sentence between the numbers differs; the three NUMBERS are worded
identically (`response is N chars`, `over the max_chars=C it was given`, `raise max_chars to at least R`), so one
parser reads the retry number off every document. Any new sentence under this member keeps that wording. The leading
capital is per document — `check`'s notice opens mid-paragraph — so a reader of this member matches case-insensitively,
which is what `JsonOverrun.Stated` does.

`truncated`/`truncated_note` are a different fact and stay: they say content was CUT to stay inside the cap.
`max_chars_overrun` says the cap was MISSED. A document can carry both. A refusal document is not capped and carries
neither: it ships whole, so "raise `max_chars`" would be a remedy that changes nothing, and the text twin says nothing
either. Most renderers' refusal arms return before the root close, which is why nothing has to be said there; the two
scan renderers render a refusal through the SAME close a served answer uses, so they guard the member on `q.Error`, as
the spill write beside it already does.

**Every renderer that takes a cap writes it, and taking a cap is the test.** A document with no rows to cut still
has a cap it can miss outright: the `counts_only=` census renderers (`RenderCounts`, `RenderNamedCounts`) took no
`max_chars` at all, which left the json census over the ceiling in silence while its text twin settled. They take one
now. `RenderError` is the one renderer without a cap and stays so — a refusal is not bounded by `max_chars`.

The `housecarl_skse` family documents are written by `SkseJsonDoc.Write` rather than by a `JsonWire` renderer, and
they get the member there, from the CALLER's `max_chars` rather than the budget left after their tail reserve. Their
row loops admit a row through `SkseJsonDoc.Fits`, on a cost measured by `JsonWire.MeasureUnit` before the row is
written, so the member says the FIXED part did not fit and never that a row crossed the ceiling (#859). A config file
row holds its own close back the same way, because its references are cut inside the row and a cut row still has to
close inside the cap. A family's row ARRAYS are named to `SkseJsonDoc.TailReserve` for the same reason, and naming them
is required rather than optional: an array's CLOSE is written past the last row the budget admitted, and the reserve
covers it by composing the array empty, which also charges the open the document already paid for — a floor of tens of
chars, in the safe direction, rather than an exact figure.
`SkseTools.Dispatch` must not run `RenderCap.Settle` over a json body: the text notice would land past the root close
and the document would stop being json. `AssetTools`'s manifest-only lane guards the same seam the same way.

### Envelope keys must stay disjoint

Response-envelope pairs are written as top-level string fields at the START of a document. `Utf8JsonWriter` does not
dedupe, so an envelope key colliding with a renderer's own top-level key (`count`, `epoch`, `records`, `rendered`,
`truncated`, `total`, …) emits a duplicate-key document. The current set is disjoint; keep it that way when adding
pairs.

### Measuring a unit means writing it

A `Utf8JsonWriter` cannot be asked what something would cost, and it BUFFERS — a length reading must flush first, or it
misses what the writer still holds. Two consequences:

- Every unit cost is taken by writing the unit into a throwaway document positioned exactly as the live one will be:
  the same writer options, the same nesting depth, and the same sibling position (a later array element pays a
  separator the first does not). What comes back is the DELTA the unit appended, not the scratch document's length.
- Depth is load-bearing rather than cosmetic. The response is written INDENTED, so every nesting level costs two
  spaces on every line of every unit inside it, and a cost measured shallower than the write under-measures by more
  the bigger the unit gets. `JsonUnitDepths` states the offsets once, from one anchor, and both the demand pass and
  the write read them from there.
- The writer's own punctuation — the root open, the root close, the separator — is measured off the writer
  (`MeasureFraming`), not kept by hand: an indented writer closes the root with a platform newline and a brace, so
  that close costs three characters on Windows, where the newline is a CR/LF pair, and two where it is not. The
  number is never hand-written for that reason.

A helper that returns from INSIDE the writer's `using` block must flush first, or the buffered document is still
unwritten and the caller gets an empty string. The write lanes' refusal arms all do.

### `landed_source` on the write lanes

The per-op read-back names WHERE its clause came from, as a word rather than a verdict:

| value | what it claims |
|---|---|
| `written_file` | the file answered for this op — `landed_on_disk` is its reading |
| `superseded` | a later op in this call wrote the same field, so the file's final state is that op's result and cannot speak for this one |
| `record_absent` | the file was re-opened and WALKED and does not contain this op's record at all — the one reading that says the edit is not in the file |
| `no_answer` | the file was re-opened and did not yield this op's leaf, or the read failed |
| `not_checked` | this op was never asked — a dry run, which writes nothing, or an op appended after the resolved edits |

It is deliberately NOT a judgement about whether the write "landed": a real difference cannot be told reliably from a
representational one (a byte-quantised Percent, an overlay's type name), and the attempt tells callers to re-issue
writes that did land. Both readings are in the document; the caller decides.

The created-record `verified` flag gates it: `verified:false` says the walk threw before reaching that record, so
nothing below it was checked, and **no op under a `verified:false` record may carry `landed_source:"written_file"`**.
`verified:true` says the walk reached the record OR completed, which reaches every record it did not find — so
`verified:true` beside `absent_from_file:true` is the ordinary miss, not a contradiction.

### The read-back block

`readback_source` names the WRITTEN FILE's content, or a dry run's in-memory would-be content — **never load-order
truth**. `readback_full` describes THIS DOCUMENT: the json renders emit every field of every row, so a present
read-back is always the full one, and it must not be made to carry the caller's ask, which the in-place lanes
override. `readback_requested` is where the ask lives.

The truncation remedy on a write document is lane-aware for the same reason. "Raise `max_chars` to see the rest" is
safe on `into=`, `in_place=` and a dry run, but on the default lane a re-issue auto-suffixes a second patch, a repeated
remove is refused outright, and a repeated create allocates the records again. Each write renderer names the remedy its
own lane can survive.

## Pinned by

- *The epoch stamp and the degraded-order marker*: `DegradedOrderMarkerTests` — the marker rides on both transports on
  the read, scan and write lanes, and `TheCheckDocumentCarriesTheMarkerAtItsRootAndOnTheErrorsFamily` pins the root
  sentence against the per-family flag and count. The write lane is asserted on the text transport only
  (`TheWriteLaneCarriesTheClauseBesideItsStamp`); no json write document is checked.
- *The epoch stamp and the degraded-order marker*: the silence on a healthy order is
  `HealthyOrderMarkerTests.AHealthyBuildCarriesNoMarkerOnEitherLane`, its own class because the healthy world is a
  different collection.
- *A capped STRING list is an array plus a sibling count*: `AssetStatusJsonLaneTests.TheCaveatBlocksAreCappedByMaxCharsToo`
  — the array and the omitted count add up to the whole, and no entry carries a prose marker; and
  `ADocumentWhoseCaveatsWereCutSaysItWasTruncated` in the same class — a document that lost only caveat entries still
  reports `truncated`.
- *A document that overran its cap says so, in one member*: `AssetStatusJsonOverrunTests` and `RecordsJsonOverrunTests`
  (both in `JsonCapOverrunTests.cs`, which also holds the shared `JsonOverrun` assertion),
  `SkseTransportTests.AnOverCapJsonFamilyDocumentStaysJsonAndSaysItOverran` for the skse families and
  `PlaceJsonRenderTests.AnOverCapWriteOutcomeSaysItOverranAndNamesTheCapThatClearsIt` for a write outcome — each
  asserting the three numbers and that the member's own length is counted; and
  `CheckCapCharsTests.TheOverrunNoticeStatesItsOwnLengthAndClearsInOneStep` for the merged check's own twin. The other
  side of the skse arm is `SkseTransportTests.EachFamilysJsonDocumentFilledPastItsCapAnswersInsideIt` — a document
  filled past its cap comes back inside it and carries no member at all.
- *A document that overran its cap says so, in one member*:
  `RecordsJsonOverrunTests.ARefusedScanCarriesNoOverrunMemberHoweverSmallTheCap` — a refusal document carries no
  member; `TheCensusDocumentSaysItOverranAndNamesTheCapThatClearsIt` and
  `TheNamedCounterCensusSaysItOverranAndNamesTheCapThatClearsIt` in the same class — the `counts_only=` census
  renderers take a cap.
- *The read-back block*: `WriteSurfaceInPlaceTransportTests.ForcedReadbackIsReportedFull` — "json: an in-place
  lane that FORCED the read-back reports readback_full:true, ask kept separately".

## Where

`src/housecarl-mcp/JsonWire.cs` holds the json renderers and their shared writers (`WriteRefusal`, `WriteNullable`,
`WriteNullableStringArray`, `WriteCapOverrun`, `MeasureUnit`); `src/housecarl-mcp/RenderCap.cs` holds
`RenderCap.Overran`, the sentence the overrun member carries; `src/housecarl-mcp/SkseJsonDoc.cs` holds
`SkseJsonDoc.Write`, `Fits` and `TailReserve` for the `housecarl_skse` family documents. Entry point:
`format="json"` on every tool that takes it.

## Related

- `docs/architecture/render-budget.md` — what `max_chars` counts and how a merged response divides it.
- `docs/architecture/output-and-artifacts.md` — the spill dispositions a json document carries a marker for.
