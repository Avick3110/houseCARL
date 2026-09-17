# The render budget: what `max_chars` counts, and who gets to spend it

**Class:** LIVING. Subsystem: `src/housecarl-mcp/{RenderCap, RenderBudget, BoundedBody (SweepEmission.cs),
SweepDemand, BodyAllocation, BatchRender, TransportAccounting, RowProjection}.cs` and
`src/housecarl-core/{CharCountedStream, JsonTextEncoder}.cs`.

Two different budgets share the word "budget" and are not the same thing:

- **`max_chars`** — how WIDE the response may be. Divided among the things competing for it, enforced at
  every write.
- **the render bound** (`RenderBudget`) — how LONG a call may spend rendering before it refuses up front
  rather than going quiet past a client's idle timeout. Row counts per lane, not characters.

This file is the home of both, cited from the code under ADR 0001.

## The unit is CHARACTERS, not bytes

`max_chars` counts UTF-16 code units — what `string.Length` returns for the finished response, and what the
text lane's StringBuilder was always measuring. Shipped in 2.0.2.

The json lane is written as UTF-8, so its stream's `Length` is a byte count and is not the number the caller
capped or the number the overrun notice states back. `CharCountedStream` converts as the bytes go past (a
character starts at every non-continuation byte; a four-byte sequence counts as its surrogate pair) and is the
one place the json lane takes its length from. The two units agreed only while every non-ASCII character was
escaped to `\uXXXX`, which stopped when `JsonTextEncoder` widened the escaping to the Basic Multilingual Plane
(#754). Above the BMP a character still rides as an escape pair; an escape is ASCII and is counted as the
characters it is, so a character-measured cap is right on both sides of that line.

**Pinned by** `CheckCapCharsTests` — the fixture really carries non-ASCII unescaped, the overrun notice states
its own length and clears in one step, an astral character still escapes and is counted as written, and both
transports state the same length about the same sweep.

## The bound holds by layout, never by trimming

`RenderCap` carries the two numbers together so neither is used where the other belongs: `Cap` is the
`max_chars` the caller passed — the number a cut notice names and the ceiling the finished response may not
exceed — and `Budget` is the room content has once everything written after it is charged. A unit is written
only when it fits whole, so no rendered string is ever cut mid-token, and what did not fit is counted in a
notice rather than dropped.

One arm may still exceed the cap, and says so: where `max_chars` is smaller than what the response must carry
whatever the budget, the answer ships and `RenderCap.Settle` appends an overrun notice naming the number that
clears it in one step. The notice is part of the response whose length it states, so it settles to a fixed
point instead of quoting a number it then invalidates. The merged sweep's `max_chars_overrun` has the same
shape (#537).

## A merged response divides its budget by water-filling over measured demand

`housecarl_check` can run several families in one response. The room its body may occupy is divided
**max-min fair (water-filling)**: every child gets `min(its demand, λ)`, where λ is the level at which the
budget is exhausted. It is **hierarchical** — the top-level participants are one per family plus one for the
response's own subjects (the excluded-plugin roster), and each participant's subjects then water-fill that
participant's share. A flat fill over leaf subjects would hand a subject-rich family more of the budget purely
for having more parts.

The allocation is computed **before** anything is written (`BodyAllocation`'s constructor, off
`SweepDemand`'s result), so each child's share is a function of the budget and the demand vector alone and
never of render order. Discovering a child's demand at render time and handing the leftover to whoever came
next is what makes an allocation order-dependent and non-monotone, which is why `BodyAllocation.Done` is
deliberately a no-op.

**Every demand is measured, never estimated.** A demand is the cumulative width of a subject's actual units,
composed by the same helper that will write them, in the transport's own unit — no row count times a mean
width. Measurement is bounded: a subject whose units exceed the room its parent could give it is
`BodyAllocation.Unconstrained` and measuring stops there, which is exact rather than approximate, because such
a subject is cut whatever λ turns out to be. The pass therefore costs O(budget), not O(all rows).

**The fixed part is outside allocation entirely** — the title, the scope sentence, each family's head, each
subject's unconditional lines and closing disclosure, the accounting, the boundary. It is measured, not
assembled from a roster of write sites: the render composes the whole response once through
`BoundedBody.Skeleton`, which admits one unit per subject, and what comes back less what those units wrote is
the fixed part. The pieces that vary with the cut cannot be skeleton-composed and go through
`BoundedBody.Reserve` as an upper bound instead. Leaving any of it inside the row budget would put the
response-wide test ahead of the allocation, and render order would decide who loses again.

`BoundedBody` is the one place either transport appends anything `max_chars` can refuse: every body write goes
through `Emit`, so the bound is enforced there rather than promised by each caller. A subject's own share sits
on top of the response-wide test, not instead of it.

### What the properties pin

In `src/housecarl-generator/CheckMergeProbe.cs` (`ci-all`):

| property | what it holds |
|---|---|
| `ALLOCATION-MONOTONE-IN-MAX-CHARS` | across every integer cap in a wide band, no subject ever spends fewer characters at a wider cap — what makes the response's own "raise `max_chars=`" remedy true |
| `ALLOCATION-NO-STRANDING` | a call whose whole demand fits its budget renders every unit and claims no cut, in both transports |
| `ALLOCATION-EQUALS-SPEND` | what a subject was allocated is what it spent — a demand measurement that drifted from the write shows up as the gap |
| `ALLOCATION-SECOND-FAMILY-DOES-NOT-WAIT-ITS-TURN` | a later family is not starved by an earlier one spending first (#394) |
| `RESERVE-DECLARED-IS-RESERVE-DEMANDED` | the demand pass's reserve and the render's reserve are one number (`ReserveDemanded` vs `ReserveDeclared`) |
| `RESERVE-COVERS-WHAT-IT-RESERVES-FOR` | a reserve is wide enough for the sentence it was held back for |

`CheckShapeMatrix.cs` drives the same properties over a shape matrix (`MATRIX-MONOTONE-IN-MAX-CHARS`,
`MATRIX-JSON-PARSES-AT-EVERY-CAP`), and its one-budget arm holds `OutstandingHigh` to `ReservedForRows` —
equal exactly when the up-front measurement was not exceeded, and therefore when the response-wide test never
bit before the allocation did.

### Known under-fills — open gaps, not design

No-stranding is a claim about the **allocation**, not about spending, and two measured shapes under-fill:

- **#398** — a biting cap leaves a few hundred characters no row can use: reserve that was held and never
  written (a reserve is an upper bound), plus whole-unit granularity.
- **#400** — a child subject's demand counts units the render can never reach, because some units are only
  reachable through another subject's (a plugin's dangling entries through its section, a seed's topic blocks
  through its head), and the render breaks the outer loop when the parent's subject stops.

Both are over-measures in the safe direction: monotonicity still holds and no response overruns. Neither is
intended behaviour — they are filed gaps. `BoundedBody`'s `ReservedWrittenBy*` counters exist to say which
holder is sitting on the unspent reserve; nothing in a response branches on them.

## When a sweep spills to a file

Two dispositions, and they are different:

- **`to_file=` (asked for).** `CheckArtifact` writes every finding as one JSONL row under a manifest and the
  response renders the manifest plus each family's refusal or boundary. The file carries classes the response
  withholds, and its `total` counts findings the listing budget cut, so `row_count` cannot read as the whole
  answer. `to_file=` is refused with `counts_only=`.
- **`ceiling` (auto-spill).** Where an inline `records` or `asset_status` render hits `max_chars`, the complete
  result is written to an artifact and the `spilled` marker rides in-band in both formats (`SpillInfo`,
  `SpillState`). `housecarl_check` has no auto-spill: it spills only on `to_file=`.

A promised artifact that could not be written is stated, not swallowed — `SpillState.WriteFailed` says the
complete result exists nowhere and names the recovery moves.

## The row-shape contract

Everything the caller may be refused is a **unit**, and a unit is emitted whole or not at all. That holds for
per-plugin sections, per-record sections, dialogue topic blocks, facegen finding rows and histogram rows alike:
everything inside one is a finding in its own right, and a per-line "append if it fits" would drop half of it
under a label claiming more than it shows.

- **A declared cost is an upper bound for the test; the charge is what was measured.** `Emit` takes a cost,
  tests against it, then charges what `commit` actually appended. A site that declares 0 overshoots by one
  unit and no more, because the response's length only ever grows.
- **A unit's width is computable before it is written, in both transports.** Composing blocks independently
  and concatenating them is identical to a one-pass render, and every json row's pre-write cost bounds its
  write. The allocation depends on that holding.
- **Every cut is named.** `HistogramCut` is computed once from the emitting loop's own facts and consumed by
  both transports, and it names the knob that actually stopped the axis. `BatchRender` names its cut, and
  names an oversize item separately with the `max_chars` that clears it in one step. `TransportAccounting`
  states one in-band block whose four omission causes are counted once each, so
  `skipped + rendered + truncated + capped == total`, with `remaining` and the next page measured off what was
  RENDERED rather than off the window.
- **A closing disclosure is never refusable.** Its room comes out of `BoundedBody.Reserve` before the body
  renders, because the pressure that cut the rows would cut the line reporting the cut.
- **`project.form='rows'`** folds a depth-expanded list read to one line per element: the element's own
  summary followed by every sub-field the read FOUND, with only an ABSENT optional omitted. A row is carried
  structurally too (`FieldValue.Cells`), so the json render emits one object per element rather than a
  sentence a consumer would have to parse.

## The render bound is a time budget, not a width one

`RenderBudget` states up front what a scan's RENDER will cost and refuses past it, because the scan terms
bound the scan and nothing bounded the render (#582). Each lane carries its own measured per-row cost and its
own row bound, because the lanes are orders of magnitude apart: named fields, whole-record
(`form='everything'`), identity, the comparison forms (delta/tree), and asset-path resolution. Every bound is
ten minutes at that lane's per-row cost — a third of the 30-minute idle timeout a Claude Code client gives a
call — except the comparison forms, whose bound is about a minute because their row is. What a call actually
spent comes back as `render_ms`, which is how the estimates are checked against a real order rather than
trusted. The bounds are settable so a test can drive the seam over a few records; production never assigns
them.

`RenderBudget.AccountingReserve` is held back from `max_chars` so the accounting line is paid for inside the
cap rather than appended past it. **Pinned by** `RecordsRenderCostTests.TheAccountingLineIsReservedFromTheRowBudget`.

## Related

- `docs/architecture/check-family-tests.md` — which lane a check-family fact is driven from.
- `docs/architecture/tool-schema-publication.md` — the published-schema passes, including the optional
  depth cap (`SchemaDepthCap`).
