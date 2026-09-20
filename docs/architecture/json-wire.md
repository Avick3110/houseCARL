---
updated: 2026-09-18
covers: [src/housecarl-mcp/JsonWire.cs]
---
# The json wire: the shape a machine-readable response is allowed to take

**Class:** LIVING. Subsystem: `src/housecarl-mcp/JsonWire.cs`, the `format="json"` twin of the text `Wire` renderer.

This file is the home of the json transport's own contracts — what a document may say and how it may say it. The
budget those documents are written against is a different subject and lives in
`docs/architecture/render-budget.md`: `max_chars` counts characters, `CharCountedStream` is where a json lane takes
its length from, `JsonTextEncoder` is the one encoder, and `BoundedBody` is the one place a merged check appends
anything a cap can refuse. Nothing here restates any of that.

## One serializer per read tool, over the text lane's own outcome objects

Each renderer consumes the SAME outcome object the text `Wire` consumes, so text and json can differ only in
formatting, never in data. Field values are the same wire tokens the text mode emits, so a token read out of json is
still a value a write can reuse verbatim.

A json document is never a silently degraded mode. Truncation drops trailing ROWS and flags it (`truncated:true`
alongside `rendered`); it is never a cut of the serialized string at a byte budget, which would emit malformed JSON.
The accounting and the notes ride inside the document rather than beside it.

## `ok` is a discriminant, and it is document-level only

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

## Null, empty and zero are three different answers

- `null` says the value was NOT COMPUTED — a finding class the caller excluded, a split that was not made, a list
  nobody looked for. A class emitted as `0` would parse as one that came back clean.
- `[]` says it was computed and came back empty.
- A number says it was counted.

`WriteNullableStringArray` is the array twin of `WriteNullable` for members whose null carries that meaning.

## The epoch stamp and the degraded-order marker

`order_degraded` is a **sibling** of the epoch, never folded into it: the epoch string is opaque and compared only for
equality, so folding health into it would leave two builds that differ only in health comparing as merely "different".
The marker is silent on a healthy order.

The sentence is stated once per document, at the root. A family head inside the merged check carries the flag and the
count (`order_degraded_plugins`) instead, because the roster already names those plugins with their reasons and the
fixed part comes out of the budget the findings are listed from.

`epoch_covers_all_inputs` is false when off-order files were swept beside the index, or when the family reports
verdicts read off another substrate, which `epoch_uncovered` names.

**Pinned by** `DegradedOrderMarkerTests` — the marker rides on both transports on the read, scan and write lanes, and
`TheCheckDocumentCarriesTheMarkerAtItsRootAndOnTheErrorsFamily` pins the root sentence against the per-family flag and
count. The silence on a healthy order is `HealthyOrderMarkerTests.AHealthyBuildCarriesNoMarkerOnEitherLane`, its own
class because the healthy world is a different collection.

## A capped STRING list is an array plus a sibling count

Where a list of plain strings is bounded — the build-level caveat blocks, and `Wire.ContestedHostsShown` on the text
lane — what did not fit is a sibling `<name>_omitted` number, never a prose marker element inside the array. A marker
element would be handed to a consumer iterating the array as if it were an entry, and the array length would stop
matching the count the accounting states.

An array of OBJECTS is cut the other way, and that is not a violation of this: `WriteFieldsArray` closes a
field-truncated `fields` with a sentinel object (`{path:"…", note:"[truncated at max_chars: …]"}`), because a row
there is already an object with a `note` member and the sentinel reads as one more of them. Consumers and tests read
that row; do not replace it with a `fields_omitted` sibling.

**Pinned by** `AssetStatusJsonLaneTests.TheCaveatBlocksAreCappedByMaxCharsToo` — the array and the omitted count add up
to the whole, and no entry carries a prose marker; and `ADocumentWhoseCaveatsWereCutSaysItWasTruncated` in the same
class — a document that lost only caveat entries still reports `truncated`.

## Envelope keys must stay disjoint

Response-envelope pairs are written as top-level string fields at the START of a document. `Utf8JsonWriter` does not
dedupe, so an envelope key colliding with a renderer's own top-level key (`count`, `epoch`, `records`, `rendered`,
`truncated`, `total`, …) emits a duplicate-key document. The current set is disjoint; keep it that way when adding
pairs.

## Measuring a unit means writing it

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

## `landed_source` on the write lanes

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

## The read-back block

`readback_source` names the WRITTEN FILE's content, or a dry run's in-memory would-be content — **never load-order
truth**. `readback_full` describes THIS DOCUMENT: the json renders emit every field of every row, so a present
read-back is always the full one, and it must not be made to carry the caller's ask, which the in-place lanes
override. `readback_requested` is where the ask lives.

**Pinned by** `src/housecarl-generator/WriteSurfaceGuardProbe.cs` — "json: an in-place lane that FORCED the read-back
reports readback_full:true, ask kept separately".

The truncation remedy on a write document is lane-aware for the same reason. "Raise `max_chars` to see the rest" is
safe on `into=`, `in_place=` and a dry run, but on the default lane a re-issue auto-suffixes a second patch, a repeated
remove is refused outright, and a repeated create allocates the records again. Each write renderer names the remedy its
own lane can survive.

## Related

- `docs/architecture/render-budget.md` — what `max_chars` counts and how a merged response divides it.
- `docs/architecture/output-and-artifacts.md` — the spill dispositions a json document carries a marker for.
