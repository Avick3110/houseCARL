---
updated: 2026-09-29
covers: [src/housecarl-mcp/RenderCap.cs, src/housecarl-mcp/RenderBudget.cs, src/housecarl-mcp/SweepEmission.cs, src/housecarl-mcp/SweepDemand.cs, src/housecarl-mcp/BodyAllocation.cs, src/housecarl-mcp/BatchRender.cs, src/housecarl-mcp/TransportAccounting.cs, src/housecarl-mcp/RowProjection.cs, src/housecarl-core/CharCountedStream.cs, src/housecarl-core/JsonTextEncoder.cs]
---
# The render budget: what `max_chars` counts, and who gets to spend it

## What it is

Two budgets share the word, and are not the same thing: **`max_chars`**, how WIDE the response may be, and
**the render bound** (`RenderBudget`), how LONG a call may spend rendering before it refuses up front. This
file is the home of both, cited from the code under ADR 0001.

## Contracts

### The unit is CHARACTERS, not bytes

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

### The bound holds by layout, never by trimming

`RenderCap` carries two numbers together so neither is used where the other belongs: `Cap` is the `max_chars`
the caller passed — the number a cut notice names and the finished response may not exceed — and `Budget` is
the room content has once everything written after it is charged. A unit is written only when it fits whole,
so nothing is cut mid-token, and what did not fit is counted in a notice.

One arm may exceed the cap, and says so: where `max_chars` is smaller than what the response must carry
whatever the budget, the answer ships and `RenderCap.Settle` appends an overrun notice naming the number that
clears it in one step. The notice is part of the response whose length it states, so it settles to a fixed
point. The merged sweep's `max_chars_overrun` has the same shape (#361).

### A merged response water-fills its body budget over measured demand

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

#### Known under-fills — open gaps, not design

No-stranding is a claim about the **allocation**, not about spending, and two measured shapes under-fill:

- **#398** — a biting cap leaves a few hundred characters no row can use: reserve that was held and never
  written, plus whole-unit granularity.
- **#400** — a child subject's demand counts units the render can never reach, because some units are only
  reachable through another subject's, and the render breaks the outer loop when the parent's subject stops.

Both are over-measures in the safe direction: monotonicity holds and no response overruns. Neither is intended
behaviour — they are filed gaps. `BoundedBody`'s `ReservedWrittenBy*` counters say which holder is sitting on
the unspent reserve; nothing in a response branches on them.

### The row-shape contract

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

### Spilling to a file

The spill dispositions themselves — `to_file=` and the `ceiling` auto-spill — are stated in
[output-and-artifacts.md](output-and-artifacts.md), the note covering `src/housecarl-mcp/Artifacts.cs`. What a
spilled `check` artifact contains is here.

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

### What a merged response's accounting may claim

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
`length + cost` before appending, so it needs only the newlines its blocks are wrapped in; the merged check's json
entries are tested before the write rather than measured, so the last entry lands over and its slack has to cover one
whole entry plus `BoundedBody`'s post-check. That slack is this lane's own arrangement, not a licence to overshoot: a
`Utf8JsonWriter` cannot be asked what an object would cost, but it can be made to write one into a throwaway document,
which is what `JsonWire.MeasureUnit` does and what the `housecarl_skse` family documents admit their rows on (#859).

**The overrun notice** names which of the two overruns happened — a `max_chars` too small to hold the response's
fixed part, or a body unit that ran past what the budget had left after that fixed part fit. One sentence cannot
cover both, because the fixed-part explanation is false of the second. It is asked of the FINISHED response's
length and answers only about that; predicted from a header length plus the reserve it would be a statement about
the worst case instead. Its remedy is not simply that length: raising the cap widens every `max_chars` this response
prints back, so the growth is added from two measured terms — how many places print it, counted in the finished
response rather than derived from the number of accountings, and how many digits the number gains. The notice's own
length is excluded, because it disappears the moment the response fits.

### The render bound is a time budget, not a width one

`RenderBudget` states up front what a scan's RENDER will cost and refuses past it, because the scan terms
bound the scan and nothing bounded the render (#582). Each lane carries its own measured per-row cost and its
own row bound, because the lanes are orders of magnitude apart: named fields, whole-record
(`form='everything'`), identity, the comparison forms (delta/tree), and asset-path resolution. Every bound is
ten minutes at that lane's per-row cost — a third of the 30-minute idle timeout a Claude Code client gives a
call. What a call spent comes back as `render_ms`, which is how the estimates are checked against a real order.

The comparison forms (delta/tree) get the same ten minutes, but their cost is not one number per row: it is
priced from the versions the call reads (`RenderBudget.ComparisonShape`), and the row bound is ten minutes divided
by the mean price of a row. What moves it:

- **Versions read.** Each row is counted as its batch reads it (`ComparisonCount`), on the batch's own chunk
  boundaries. A tree reads every in-order provider, plus one for a `versus=` that is not the winner; a named in-order
  `versus=` that holds no version stops the row at the winner, which is then the one version read; a key the index
  does not hold is settled from the index and reads nothing, and its absent plugin is explained from the profile
  once per call, not per row. A delta reads its subject, then its reference only if the subject read, and a row whose
  in-order subject holds no version is not declared to the reference's walk at all; each pole reads one version
  when it resolves (the winner, a named plugin that holds the record, the provider below the subject) and none when
  it does not. An off-order pole is read row by row from one sweep of its own file, so it is charged a version per
  row and that file's size once, never per chunk.
- **fields=.** A whole version costs tens of milliseconds; a narrowed one a fraction of a millisecond.
- **Contained records.** A version of a record a cell or topic contains is dearer: whole, it is charged at the
  dearest type measured (LAND and NAVM); narrowed, at the REFR figure.
- **Plugin walks** (narrowed only). The comparison reads in chunks of 32 rows, and each chunk walks the plugins its
  rows read: every provider for a tree (the winner and the `versus=` plugin for a row stopped at the winner), and
  the two poles' plugins for a delta, where a named subject's plugin is walked for every row. With `fields=` named that walk is most of the cost, and it grows with the size of the
  plugins walked, not with the rows: 1,000 NPC_ rows that all live in `Skyrim.esm` cost 3.5 ms a row, while 1,276
  catalogue ids spread over small plugins cost 0.12. So a narrowed comparison is charged per megabyte of provider
  plugin each chunk walks.
- **Walks to a plugin's end.** A walk stops once it has every record it was asked for, so a plugin asked for a
  record it lacks is walked to its end. Only a named pole does that (a named subject or reference, a named
  `versus=` on a tree), and that walk is charged on every comparison, whole or narrowed, at its own rate per
  megabyte: the whole-record prices were measured on walks that stop early.
- **SkyPatcher post-state.** A pole that replays the SkyPatcher layer adds a fixed amount per row.

The scan lanes count all of this exactly, off the build the scan matched (`outcome.Pin`). The off-order lane also
takes the file's own containment, which the active order's index does not hold. The formids= list lane first
refuses on the list's length at the cheapest a resolved row can be (top-level, one version for a tree), before the
index is built, and says both its time and its bound are bounds: "at least" the time, "could fit at most" the rows. Once that fits it counts exactly, and the comparison is checked
against the build that was counted on. The comparison responses carry no `render_ms` of their own, so the figures
below are client wall clock. The old rule, one 250 ms floor against a 250-row bound (#716, measured on REFR scans
before the per-plugin gather of #765), refused the battery's 1,276-record post-state delta as "about 5
minutes" (#932): that estimate was near the truth, but a one-minute budget refused a five-minute job every
other lane would have run, and it refused a narrowed comparison that takes a third of a second.

Measurements, all 2026-09-29 on `E:/Authoria - Requiem Reforged` (3,254 plugins) through a private Release server
over stdio, warm. "Loaded" runs had other sessions on the same machine; the quiet column is the blind review's
re-run on PR #976, 1.5 to 2.3 times faster row for row. Versions are the mean versions read a row; MB walked is the
sum, over chunks, of the provider plugins each chunk walks.

| form | source / reference | fields | records | versions | loaded s (ms a row) | quiet s (ms a row) |
|---|---|---|---|---|---|---|
| delta | SkyPatcher post vs winner | whole | 1,276 ARMO/WEAP | 2 | 297.7 (233) | 300: 38.8 (129) |
| delta | winner vs previous_provider | whole | 1,276 ARMO/WEAP | 2 | 180.6 (142) | 300: 24.8 (83) |
| tree | every provider vs winner | whole | 1,276 ARMO/WEAP | 2.34 | 237.0 (186) | — |
| tree | every provider vs winner | whole | 5,798 REFR | 1.52 | 894.8 (154) | 300: 28.7 (96) |
| tree | every provider vs winner | whole | 536 NPC_, 6–14 providers | 6.95 | 115.3 (215) | — |
| tree | every provider vs winner | whole | 536 NPC_, 2 providers | 2 | 37.3 (70) | — |
| tree | every provider vs winner | whole | 536 NPC_, 1 provider | 1 | 18.8 (35) | — |
| tree | every provider vs winner | whole | 300 LAND, Skyrim.esm | 1 | 30.6 (102) | — |
| tree | every provider vs winner | whole | 300 NAVM, Skyrim.esm | 1 | 29.1 (97) | — |
| delta | SkyPatcher post vs winner | Keywords | 1,276 ARMO/WEAP | 2 | 54.4 (43) | — |
| delta | winner vs previous_provider | Keywords | 1,276 ARMO/WEAP | 2 | 0.33 (0.26) | 1,276: 0.18 (0.14) |
| tree | every provider vs winner | EditorID | 1,276 ARMO/WEAP, 263 MB walked | 2.34 | 0.15 (0.12) | — |
| tree | every provider vs winner | Base | 1,000 REFR, 6,784 MB walked | 1.45 | 20.7 (21) | 14.1 (14) |
| tree | every provider vs winner | Base | 5,798 REFR, 39,483 MB walked | 1.52 | 72.4, then 64.3 (12) | — |
| tree | every provider vs winner | EditorID | first 1,000 NPC_, 7,992 MB walked | 1 | 3.52, then 2.88 (3.2) | — |
| tree | every provider vs winner | EditorID | first 10,000 NPC_, 27,761 MB walked | 1.66 | 9.48, then 8.09 (0.88) | — |
| tree | every provider vs winner | EditorID | all 65,748 NPC_, 204,967 MB walked | 1.36 | 199.7, 215.7, 158.6, 173.2 (2.9) | — |
| delta | winner vs previous_provider | EditorID | all 65,748 NPC_, 178,020 MB walked | 1.20 | 153.1, then 184.1 (2.6) | — |
| delta | HearthFires.esm vs winner | EditorID | all 65,748 NPC_ ids, 42 resolving; HearthFires walked to its end 2,055 times (8,174 MB) | 0.0013 | 38.3 (0.58) | — |
| tree | every provider vs HearthFires.esm | EditorID | all 65,748 NPC_, 35 held by HearthFires; 148,435 MB walked, 8,174 to the end | 1.0 | 193.0 (2.9) | — |
| tree | every provider vs winner | EditorID | 2,000 ids of an absent plugin | 0 | 0.09 | — |

Per version, the whole-record figures are 31 to 42 ms on top-level records (NPC_ trees loaded, the quiet delta) and
62 to 102 ms on contained ones (quiet REFR, loaded LAND and NAVM). The narrowed figures come to 0.23 to 1.05 ms per
megabyte walked on top-level records (median about 0.6), where a price per row would have ranged over thirty-fold.
File size is the only cheap driver found: the count of the type's records in each walked plugin spreads further
(2.5 to 21 µs a record), because a walk stops at the last record it wants. The whole-record prices are rounded up
from their figures; the walk is priced at the median. A walk to a plugin's end came to 4.7 ms a megabyte on the
HearthFires delta and, after the tree's early-stopping walks are priced, about 7 to 12 on the HearthFires tree; it is
priced at 6:

| price | per | figure |
|---|---|---|
| whole version, top-level | version read | 50 ms |
| whole version, contained | version read | 100 ms |
| narrowed version, top-level | version read | 0.1 ms |
| narrowed version, contained | version read | 10 ms |
| narrowed plugin walk | megabyte walked | 0.6 ms |
| walk to a plugin's end | megabyte walked | 6 ms |
| SkyPatcher post-state | row | 45 ms |

So the battery's call is priced at 145 ms a row, about 3 minutes, and runs. The 5,798-REFR whole tree is priced at
about 15 minutes (1.52 versions at 100 ms) and is refused; it ran in 9.3 minutes quiet and 15 loaded. A narrowed
tree over every NPC_ is priced at about 2.2 minutes and runs in 2.6 to 3.6. The flat 0.2 ms a row this replaced
priced that same call at 13 seconds, so a narrowed comparison 3,000,000 rows long, the old bound, could have run for
hours. The whole-record prices were measured with the plugin walks inside them, which is why the walk is charged to
narrowed comparisons only. At the median walk price a narrowed estimate runs from about 1.7 times low (the all-NPC_
delta and tree, which walk many mid-sized plugins) to about 2.3 times high (the first 10,000 NPC_, which walk
Skyrim.esm again and again): so a narrowed comparison refused at "about 10 minutes" may take anywhere from about 4 to
17. A loaded machine runs whole-record calls up to about twice the price, still inside the 30-minute client timeout
at the ten-minute bound. The bounds are
per-service settings (`Bounds` on the service, `MaxAssetPaths` on the assets area) so a test lowers only its
own world's; production never assigns them. `AccountingReserve` is held back from
`max_chars` so the accounting line is paid for inside the cap.

## Pinned by

- *The unit is CHARACTERS, not bytes*: `CheckCapCharsTests` — non-ASCII is carried unescaped, the overrun notice
  states its own length and clears in one step, and an astral character escapes and is counted as written. Each
  transport states its own length about the same sweep (`TheTextLaneStatesItsOwnLengthOnTheSameSweep` for text); no
  test compares the two lengths with each other.
- *A merged response water-fills its body budget over measured demand*: the properties, in
  `src/housecarl-mcp-tests/CheckMergeAllocationTests.cs` (the arm names below are the retired `check-guard`
  probe's, kept as one-line comments above each test):

| property | what it holds |
|---|---|
| `ALLOCATION-MONOTONE-IN-MAX-CHARS` | over every integer cap in a wide band, no subject spends fewer characters at a wider cap — what makes the response's own "raise `max_chars=`" remedy true |
| `ALLOCATION-NO-STRANDING` | a call whose whole demand fits its budget renders every unit and claims no cut, in both transports |
| `ALLOCATION-EQUALS-SPEND` | on a response with **nothing cut**, what a subject was allocated is what it spent, to the byte. At a biting cap a subject spends the largest whole-unit prefix under λ, so it spends less — the #398 granularity term |
| `ALLOCATION-SECOND-FAMILY-DOES-NOT-WAIT-ITS-TURN` | a later family is not starved by an earlier one spending first (#394) |
| `RESERVE-DECLARED-IS-RESERVE-DEMANDED` | the demand pass's reserve and the render's reserve are one number |
| `RESERVE-COVERS-WHAT-IT-RESERVES-FOR` | a reserve is wide enough for the sentence it was held back for |

- *A merged response water-fills its body budget over measured demand*: `CheckMergeShapeMatrixTests` drives the
  same properties over a shape matrix (`MATRIX-MONOTONE-IN-MAX-CHARS`, `MATRIX-JSON-PARSES-AT-EVERY-CAP`), every
  eleventh cap per shape, and its one-budget fact **bounds** `OutstandingHigh` by `ReservedForRows` — it
  fails only on `>`. Equality is the diagnostic reading, that the up-front measurement was not exceeded, and not the
  asserted property.
- *What a merged response's accounting may claim*:
  `CheckCapCharsTests.TheOverrunNoticeStatesItsOwnLengthAndClearsInOneStep` — the overrun notice states the finished
  response's length, and its remedy clears the overrun in one step.
- *The render bound is a time budget, not a width one*: `RecordsRenderCostTests.TheAccountingLineIsReservedFromTheRowBudget`
  — the accounting line at its widest fits inside `AccountingReserve`. It renders nothing against a cap, so that the
  reserve is taken out of `max_chars` is not asserted.
- *The render bound is a time budget, not a width one* (the comparison forms): `ComparisonBoundTests` — the battery's
  post-state delta and a narrowed catalogue fit, a REFR-scale tree is refused at a price within a factor of two of
  both measured rates, a tree is priced per version it reads, contained records are charged their own price whole
  or narrowed, nine measured narrowed NPC_ runs are priced inside the stated band (1.7 times low to 2.3 times high)
  by the plugins they walk, and a refusal on the floor says its time and bound are bounds; `ComparisonBoundCallSiteTests` — through
  `housecarl_records` with the derived bound, `fields=`, a placed reference, a post-state pole, the weapon's
  three providers, a named `versus=`, the plugins its chunk walks and a delta's two poles each move the quoted
  price, a scan counts exactly, and a list refused on its length says so; `ComparisonBoundOffOrderTests` — a
  switched-off file's new placed references are priced as contained at one version each, and a tree over 10,000 of
  them reads nothing and runs; `ComparisonBoundSeamTests` — a plugin changed between the list lane's price and its
  batch refuses, for both forms.
  The counted rows themselves: a tree against a `versus=` that holds nothing reads the winner alone, a delta
  whose subject holds nothing walks no reference (one plugin walk), and 50 absent ids explain their plugin once
  (`AbsenceExplains`), tree and delta.

## Where

`src/housecarl-mcp/RenderCap.cs` holds `Cap`, `Budget` and `RenderCap.Settle`; `RenderBudget.cs` is the render bound;
`SweepDemand.cs` is the demand pass and `BodyAllocation.cs` the max-min fill; `SweepEmission.cs` holds `SweepSubject`
and `BoundedBody`; `BatchRender.cs` is the write-and-retract batch render; `TransportAccounting.cs` is the four-cause
omission block; `RowProjection.cs` is the `rows` project form. `src/housecarl-core/CharCountedStream.cs` is where the
json lane takes its length from, and `src/housecarl-core/JsonTextEncoder.cs` is the one encoder. Entry points:
`max_chars=` on every tool that takes it, the merged `housecarl_check` response, and the render bound on
`housecarl_records` and `housecarl_asset_status`.

## Related

- `docs/architecture/check-family-tests.md` — which lane a check-family fact is driven from.
- `docs/architecture/tool-schema-publication.md` — the published-schema passes, including `SchemaDepthCap`.
