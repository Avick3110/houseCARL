---
updated: 2026-09-18
covers: [src/housecarl-mcp/RenderCap.cs, src/housecarl-mcp/RenderBudget.cs, src/housecarl-mcp/SweepEmission.cs, src/housecarl-mcp/SweepDemand.cs, src/housecarl-mcp/BodyAllocation.cs, src/housecarl-mcp/BatchRender.cs, src/housecarl-mcp/TransportAccounting.cs, src/housecarl-mcp/RowProjection.cs, src/housecarl-core/CharCountedStream.cs, src/housecarl-core/JsonTextEncoder.cs]
---
# The render budget: what `max_chars` counts, and who gets to spend it

**Class:** LIVING. Subsystem: the files in `covers:` above.

Two budgets share the word, and are not the same thing: **`max_chars`**, how WIDE the response may be, and
**the render bound** (`RenderBudget`), how LONG a call may spend rendering before it refuses up front. This
file is the home of both, cited from the code under ADR 0001.

## The unit is CHARACTERS, not bytes

`max_chars` counts UTF-16 code units — what `string.Length` returns for the finished response, and what the
text lane's StringBuilder measures. The json lane is written as UTF-8, so its stream's `Length` is a byte
count and is not the number the caller capped; `CharCountedStream` converts as the bytes go past and is the
one place that lane takes its length from, so no site can measure one unit and state the other.

`JsonTextEncoder` is the ONE encoder behind every json the server writes, inline response and spilled artifact
alike, so the same row cannot spell the same name two ways depending on where it landed. `Utf8JsonWriter`'s
default escapes every character above ASCII; `UnicodeRanges.All` widens the set left UNESCAPED to the Basic
Multilingual Plane (#754), and two constraints hold that bound:

- the HTML-sensitive set (`<`, `>`, `&`, `'`, `+`) is still escaped exactly as before;
- .NET offers no encoder that widens past the BMP without also unescaping that set
  (`UnsafeRelaxedJsonEscaping` does both), so the plane is where this stops. Above it a character rides as an
  escape pair, which is ASCII and counted as the characters it is, so the cap is right on both sides.

**Pinned by** `CheckCapCharsTests` — non-ASCII is carried unescaped, the overrun notice states its own length
and clears in one step, an astral character escapes and is counted as written, and both transports state the
same length about the same sweep.

## The bound holds by layout, never by trimming

`RenderCap` carries two numbers together so neither is used where the other belongs: `Cap` is the `max_chars`
the caller passed — the number a cut notice names and the finished response may not exceed — and `Budget` is
the room content has once everything written after it is charged. A unit is written only when it fits whole,
so nothing is cut mid-token, and what did not fit is counted in a notice.

One arm may exceed the cap, and says so: where `max_chars` is smaller than what the response must carry
whatever the budget, the answer ships and `RenderCap.Settle` appends an overrun notice naming the number that
clears it in one step. The notice is part of the response whose length it states, so it settles to a fixed
point. The merged sweep's `max_chars_overrun` has the same shape (#361).

## A merged response water-fills its body budget over measured demand

`housecarl_check` can run several families in one response, and the room its body may occupy is divided
**max-min fair**: every child gets `min(its demand, λ)`, where λ is the level at which the budget is
exhausted. The fill is **hierarchical** — one top-level participant per family, plus one for the response's
own subjects (the excluded-plugin roster) — and each participant's subjects then fill its share.

It is computed **before** anything is written, so each share is a function of the budget and the demand vector
alone, never of render order. `BodyAllocation.Done` is therefore deliberately a no-op.

**Every demand is measured, never estimated** — the cumulative width of a subject's actual units, composed by
the same helper that will write them, in the transport's own unit. Measurement is bounded: a subject whose
units exceed the room its parent could give it is `BodyAllocation.Unconstrained` and measuring stops there.
That is exact, not approximate, because such a subject is cut whatever λ turns out to be, and it makes the
pass cost O(budget) rather than O(all rows).

**The fixed part is outside allocation entirely** — the title, the scope sentence, each family's head, each
subject's unconditional lines and closing disclosure, the accounting, the boundary. It is measured, not
assembled from a roster of write sites: the render composes the whole response once through
`BoundedBody.Skeleton`, which admits one unit per subject, and what comes back less what those units wrote is
the fixed part. The pieces that vary with the cut go through `BoundedBody.Reserve` as an upper bound instead.

`BoundedBody` is the one place either transport appends anything `max_chars` can refuse; every body write goes
through `Emit`, and a subject's own share sits on top of the response-wide test, not instead of it.

### What the properties pin

In `src/housecarl-generator/CheckMergeProbe.cs` (`ci-all`):

| property | what it holds |
|---|---|
| `ALLOCATION-MONOTONE-IN-MAX-CHARS` | over every integer cap in a wide band, no subject spends fewer characters at a wider cap — what makes the response's own "raise `max_chars=`" remedy true |
| `ALLOCATION-NO-STRANDING` | a call whose whole demand fits its budget renders every unit and claims no cut, in both transports |
| `ALLOCATION-EQUALS-SPEND` | on a response with **nothing cut**, what a subject was allocated is what it spent, to the byte. At a biting cap a subject spends the largest whole-unit prefix under λ, so it spends less — the #398 granularity term |
| `ALLOCATION-SECOND-FAMILY-DOES-NOT-WAIT-ITS-TURN` | a later family is not starved by an earlier one spending first (#394) |
| `RESERVE-DECLARED-IS-RESERVE-DEMANDED` | the demand pass's reserve and the render's reserve are one number |
| `RESERVE-COVERS-WHAT-IT-RESERVES-FOR` | a reserve is wide enough for the sentence it was held back for |

`CheckShapeMatrix.cs` drives the same properties over a shape matrix
(`MATRIX-MONOTONE-IN-MAX-CHARS`, `MATRIX-JSON-PARSES-AT-EVERY-CAP`), and its one-budget arm **bounds**
`OutstandingHigh` by `ReservedForRows` — it fails only on `>`. Equality is the diagnostic reading, that the
up-front measurement was not exceeded, and not the asserted property.

### Known under-fills — open gaps, not design

No-stranding is a claim about the **allocation**, not about spending, and two measured shapes under-fill:

- **#398** — a biting cap leaves a few hundred characters no row can use: reserve that was held and never
  written, plus whole-unit granularity.
- **#400** — a child subject's demand counts units the render can never reach, because some units are only
  reachable through another subject's, and the render breaks the outer loop when the parent's subject stops.

Both are over-measures in the safe direction: monotonicity holds and no response overruns. Neither is intended
behaviour — they are filed gaps. `BoundedBody`'s `ReservedWrittenBy*` counters say which holder is sitting on
the unspent reserve; nothing in a response branches on them.

## The row-shape contract

Everything the caller may be refused is a **unit**, and a unit is emitted whole or not at all — per-plugin
sections, per-record sections, dialogue topic blocks, facegen finding rows and histogram rows alike.

- **A declared cost is an upper bound for the test; the charge is what was measured.** A site declaring 0
  overshoots by one unit and no more, because the response's length only ever grows. `BatchRender` writes an
  item and retracts it (`sb.Length = mark`) rather than pre-measuring at all.
- **An allocated subject's units are measured before the render**, in both transports, by the demand pass
  composing them with the same helper that will write them. For the dialogue topic blocks that measurement is
  exact: composing the blocks independently and concatenating them is byte-identical to a one-pass render, and
  the allocation depends on that holding.
- **Every cut is named.** `HistogramCut` is computed once from the emitting loop's own facts, consumed by both
  transports, and names the knob that stopped the axis. `BatchRender` names its cut, and names an oversize
  item separately with the `max_chars` that clears it. `TransportAccounting`'s block counts each of its four
  omission causes once, so `skipped + rendered + truncated + capped == total`, with `remaining` and the next
  page measured off what was RENDERED rather than off the window.
- **A closing disclosure is never refusable**, out of room `BoundedBody.Reserve` held back before the body
  renders, because the pressure that cut the rows would cut the line reporting the cut.

## Spilling to a file

The spill dispositions themselves — `to_file=` and the `ceiling` auto-spill — are documented where they live:
`src/housecarl-mcp/Artifacts.cs` (`SpillInfo`, `SpillState`). What a spilled `check` artifact contains is here.

**One artifact, one row shape, a `family` column.** A merged call runs several families and they find different
things. A file per family would leave the caller joining them, and a row shape per family would leave the manifest's
`row_schema` describing none of them — so every family's finding is flattened onto one wide row, and the columns a
family does not use are null, which is what a jsonl consumer greps on anyway.

**Rows are the SWEEP's own findings, not the render's.** The artifact is written from the results, so a row is never
missing because the inline body ran out of characters. What `limit=` already cut before the results were built is
cut here too, and the manifest says so by carrying `total` above `row_count`. The facegen family's own listing
budget is added back into `total` for the same reason.

The benign facegen class the RESPONSE withholds IS written to the file — "the complete findings" means every class
the sweep found, and the `class` column tells the two apart.

**The manifest-only render** states the scope sentence, each family's refusal or boundary, and the manifest; the
rows ARE the file. A family that refused states its ground there, because the scope sentence says a family refused
but never why. It is stated beside that family's boundary rather than refusing the whole call, since `exclude=` is
validated against each family's own scope and one family's refusal must not discard rows another family already
wrote. A FOLDED dialogue call carries its frame in both the manifest notes and this render: those rows hold verdicts
read against a plugin the order does not load, a manifest-only render is the ONLY render such a call gets, and a
projection without its frame reads as the live answer.

## What a merged response's accounting may claim

`CheckAccounting` is the one accounting of what a sweep response left out, shared by both transports so the text and
json answers cannot disagree. Its rules:

- **Every omission is a subtraction against the sweep's own totals, taken after emission stops**, so the separate
  causes sum to the total exactly rather than by two counters happening to agree. The accounting line and the
  boundary footer are reserved out of the caller's `max_chars` before the body renders, never appended past it.
- **A lane declares the SUBJECTS it actually has** (`SweepSubject`), and every sentence, json field, remedy and
  reserve derives from that set — so a lane without sections cannot claim about them and a lane with them cannot
  fail to. Subjects are lane facts, never a findings taxonomy. A json field named for a subject is present exactly
  where that subject is, never a zero standing in for "this lane has no such thing".
- **A refused family declares nothing.** A family-local refusal renders as its own section, so the writer is
  reachable with a failed result, and declaring subjects anyway would assert completeness over a sweep that never
  ran.
- **Registration happens where a unit LANDED**, from `BoundedBody.Emit`, never where a section is entered: a section
  total would claim entries for a section the cut left half-written. A subject the lane did not declare is ignored
  rather than counted, which lets the histogram rows share the one bounded-emission path without acquiring an
  accounting sentence of their own.
- **Exactly one accounting declares the excluded-plugin roster.** The roster is a scope fact emitted once per
  response however many families ran, so a second declarer would state the same cut twice.
- **The worst case is measured, not estimated.** The reserve is composed from the same predicate and the same
  composer the real line uses, with every count at the total's digit width and every optional clause present. The
  roster holds the LONGEST source names rather than the largest, because a partly-listed response can promote a
  long-named small source into it — "largest" is not a bound and "longest" is. The json lane measures by serializing
  at the depth the object actually lands at, because the document is indented and the two encodings differ.

The two transports need different slack over that worst case. The text lane composes each unit and tests
`length + cost` before appending, so it needs only the newlines its blocks are wrapped in; a `Utf8JsonWriter` cannot
measure an object without writing it, so the json lane's per-entry test is taken before the write, the last entry
lands over, and its slack has to cover one whole entry plus `BoundedBody`'s post-check.

**The overrun notice** names which of the two overruns happened — a `max_chars` too small to hold the response's
fixed part, or a body unit that ran past what the budget had left after that fixed part fit. One sentence cannot
cover both, because the fixed-part explanation is false of the second. It is asked of the FINISHED response's
length and answers only about that; predicted from a header length plus the reserve it would be a statement about
the worst case instead. Its remedy is not simply that length: raising the cap widens every `max_chars` this response
prints back, so the growth is added from two measured terms — how many places print it, counted in the finished
response rather than derived from the number of accountings, and how many digits the number gains. The notice's own
length is excluded, because it disappears the moment the response fits.

## The render bound is a time budget, not a width one

`RenderBudget` states up front what a scan's RENDER will cost and refuses past it, because the scan terms
bound the scan and nothing bounded the render (#582). Each lane carries its own measured per-row cost and its
own row bound, because the lanes are orders of magnitude apart: named fields, whole-record
(`form='everything'`), identity, the comparison forms (delta/tree), and asset-path resolution. Every bound is
ten minutes at that lane's per-row cost — a third of the 30-minute idle timeout a Claude Code client gives a
call — except the comparison forms, whose bound is about a minute because their row is. What a call spent
comes back as `render_ms`, which is how the estimates are checked against a real order. The bounds are
settable so a test can drive the seam; production never assigns them. `AccountingReserve` is held back from
`max_chars` so the accounting line is paid for inside the cap, **pinned by**
`RecordsRenderCostTests.TheAccountingLineIsReservedFromTheRowBudget`.

## Related

- `docs/architecture/check-family-tests.md` — which lane a check-family fact is driven from.
- `docs/architecture/tool-schema-publication.md` — the published-schema passes, including `SchemaDepthCap`.
