---
updated: 2026-10-01
covers: [src/housecarl-mcp/RenderCap.cs, src/housecarl-mcp/RenderBudget.cs, src/housecarl-mcp/RenderBounds.cs, src/housecarl-mcp/ComparisonMeter.cs, src/housecarl-mcp/SweepEmission.cs, src/housecarl-mcp/SweepDemand.cs, src/housecarl-mcp/BodyAllocation.cs, src/housecarl-mcp/BatchRender.cs, src/housecarl-mcp/TransportAccounting.cs, src/housecarl-mcp/RowProjection.cs, src/housecarl-core/CharCountedStream.cs, src/housecarl-core/JsonTextEncoder.cs]
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

A text render that lays its body inside what the cap leaves makes two moves (#986): the SkyPatcher layer,
`housecarl_records`' text forms and census, the merged check, `asset_status` and its census, `nif_inspect`, and the
three `housecarl_skse` families.

- **Whole first.** The complete answer is laid with no reserve; if it fits the cap it is served. A render holds
  back the widest its notices can be before its body, so without this pass an answer of N chars could be cut, and
  refused, at `max_chars=N`. The pass only has to say whether the answer fits the cap, so where a render can stop
  early it is bounded by the cap (`RenderCap.Capped`): the render is handed a `WholePass` carrying the cap as its
  bound, renders at `RenderCap.Whole` where no reserve bites, and each of its unit loops asks `WholePass.Past` before
  laying a unit. `Past` stops the loop once the render is past the bound and records it on the pass, and
  `RenderCap.WholeAt` never serves a pass that stopped, whatever its length once its trailing newline is trimmed: the
  fact is reported by the loop, not inferred from the width. The pass is a value made for one render, so nothing
  outlives it, a throw included. So a 5,000-record batch at `max_chars=2000` costs what 2,000 chars cost, not what the
  answer does. The records renders, the scan, batch, `nif_inspect`, `asset_status` and the SkyPatcher layer stop
  early. The three `housecarl_skse` families, the merged check and the asset census do not
  (`RenderCap.CappedOnce`): their data is already read, their width is bounded by the install's own lists and by
  `histogram_limit`, not by a selection, and their whole answer is the same at every cap, so it is laid once per call
  and reused by every grow round.
- **A sufficient cap, not the least.** Where the answer does not fit whole and the render at the cap is still over
  it, that render is its header and the notices it owes, and the call is refused in one sentence naming a cap the
  same call was measured to fit (`RenderCap.Hold`). The cap is found by growing only, from the render at the cap
  (sufficient rather than tight, the #546 ruling): each round first asks whether the complete answer fits the round's
  cap, and names that answer's own width once it does (whole first serves it at any cap it fits); otherwise it
  re-renders at the length the render came back at until it fits the length it was given. If eight rounds do not
  settle, the call is refused saying so, never shipped over the cap. The number is named as the value to pass, not as
  a floor: a render whose caveats take a share of the cap admits a whole caveat line as the cap grows, so a cap a
  little above it can be refused again, naming one that serves. What is re-rendered closes over data read once:
  nothing re-enters the service, and a scan's rows are read once for every render of the call (`ScanRows`), so a cut
  scan reads the bodies of the rows it lays and the one its whole pass stops on, as before the pass existed. On the
  records and `asset_status` lanes the named cap leaves `RenderCap.NextCallGrowth` free, for the next call printing its
  read timing three digits wider and its spill file's name with a `-NN` counter in the three places the block prints
  it; an allowance, not a bound. A census names its own width plus the same allowance, since its header can carry a
  note that changes between calls.

The contract is enforced once, at the exit, on the reply actually served: `RenderCap.Capped` and `CappedOnce` serve
only a whole answer that fits or a render at the cap that `Hold` accepted, and a spilling lane
(`Artifacts.CeilingText`) checks its served reply again after the write. Everything before that check is a hint that
only has to be close.

A spilling lane measures the render the next call makes: the render with no spill where it cuts nothing, and with its
spill block where it cuts, since a cap the whole answer does not fit cuts it (an uncut render is the whole answer).
The block's width depends only on the artifact's manifest: the file name, the row count and total, the epoch and its
caveats, the row schema and sort, and the count per record type, which are counts of the selection. So the lane's
artifact writer runs against a sizing target (`ArtifactTarget.Sizing`) at the name a reservation would take now
(`ResultsStore.NameFor`), counting rows and types without serializing a row or writing anything; `asset_status`, whose
stamp needs the record index, sizes as a healthy build with an epoch of full width
(`LoadOrderResolver.EpochOfWidth`). A scan's rows are counted by the type its summary already carries where the scan
prefilled one (`types=`, `plugins=`, a FormID set); otherwise by the type of each row's body in the chunked gather the
write itself uses (one walk per source plugin), never a fetch per row. A row the chunk missed, because its plugin's
walk faulted, is typed off its own read, as the write types it. No field is read to size the block. A refused call
reserves nothing, so it leaves no file and, on `asset_status`, builds no record index. A served cut reserves the name
(#770: the reservation is the file), writes the artifact once through the reserved handle, and renders with the block
the write stamped; if that reply is over the cap (the written block drifted wider than the sized one), it is held
again with the written block, and refused, the file removed, when it does not fit. A write that fails is stated in
the reply, or, where its warning does not fit, is itself the refusal; a named cap is never sized off that warning.

`or omit filter=` is offered beside a filtered refusal only where the unfiltered call is served at the cap given
(`RenderCap.Serves`: the render at the cap first, then the whole pass), and never beside SKSE's `peek=`, which needs
its filter. A json document names its own overrun in `max_chars_overrun`, so nothing is appended past its root close.

Three text renders take `max_chars` as a point to stop at rather than a ceiling, and overshoot it whenever they cut:
`housecarl_load_order_status`, `housecarl_update_status` and `housecarl_bsa_list` (#1016). The json lanes keep
`max_chars_overrun`. Two text renders are not refused, because their write already happened: `housecarl_place`'s
report, and a `to_file=` manifest. There the answer ships and `RenderCap.Settle` appends an overrun notice naming the
number that clears it in one step. The notice is part of the response whose length it states, so it settles to a
fixed point. The merged sweep's `max_chars_overrun` has the same shape (#361).

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
length is excluded, because it disappears the moment the response fits. The text lane refuses any response over its
cap instead (#986), so the notice is the json document's alone.

### The render bound is a time budget, not a width one

`RenderBudget` states up front what a scan's RENDER will cost and refuses past it, because the scan terms
bound the scan and nothing bounded the render (#582). Each lane carries its own measured per-row cost and its
own row bound, because the lanes are orders of magnitude apart: named fields, whole-record
(`form='everything'`), identity, and asset-path resolution; the comparison forms are metered instead (below). Every bound is
ten minutes at that lane's per-row cost — a third of the 30-minute idle timeout a Claude Code client gives a
call. What a call spent comes back as `render_ms`, which is how the estimates are checked against a real order. The bounds are
per-service settings (`Bounds` on the service, `MaxAssetPaths` on the assets area) so a test lowers only its
own world's; production never assigns them. `AccountingReserve` is held back from
`max_chars` so the accounting line is paid for inside the cap.

The comparison forms (delta/tree) are **measured, not predicted** (#932). The first reset dropped a predicted
per-row cost, because each of four reviews found a read path it missed. The second dropped a flat worst-row price,
because it was no ceiling either (a tree over records with 30–40 providers, a heavy `fields=` list, both poles
replaying) while it over-refused deltas about threefold.

So a comparison first meets a **floor** (30 ms a row reading whole records, 0.04 ms with `fields=` named, each under
the cheapest row #976 measured; a SkyPatcher post-state pole adds nothing), which refuses before any read only a count
past ten minutes even at that price, so it can never refuse a feasible job.

What passes is **metered** (`ComparisonMeter`):

- **Only rows that read are chunked.** Both batches settle up front, with no body read, the rows whose pole holds no
  version (a malformed id, one the order does not hold, one a named `source=` does not touch, one the off-order file
  lacks), and chunk only the live rows. Each pole's one-time work (the off-order sweep, the SkyPatcher replay's open)
  runs before the first chunk, and only when a row will read.
- **The check.** After a chunk, the batch takes its rate so far (time since its first chunk began over the rows read),
  projects it over the rows left, and adds the time spent since the call began, so the scan, the index build and that
  one-time work count as time spent, not as rate. The moment that passes ten minutes it refuses naming the rate, with
  every row dropped and nothing further read; under it the batch runs on, so nothing is read twice.
- **When it checks.** Never on the first chunk alone, since on ARR a cold first chunk measured about 1.4 times the
  steady rate: at most two chunks (64 rows) are read before the first check can refuse. Never with one chunk or less
  left, since that work is kept rather than thrown away to save it. Otherwise after every chunk, so warm-up the first
  chunk pays (lazy plugin opens, the replay's INI parse and EditorID sweep) dilutes, and a list sorted cheap-first is
  caught at the chunk that shows it. A refused call spends at most the budget plus one chunk. The budget covers the call
  up to the batch's end; the render after it is not charged.
- **The figure it names.** "About K rows fit" is 90% of the budget left after the time spent before the batch, at that
  rate, rounded down, so feeding it back as `limit=` or as fewer ids clears the refusal on a steady rate. A census or a
  `to_file=` artifact says "narrow the selection to about K matches" instead, since `limit=` does not lower what it
  reads. When the time before the batch leaves no room, the refusal names that time as the cause and gives no figure.

`RenderBounds.ComparisonRows` overrides the floor for tests, and `ComparisonMeter.TestClock` (an `AsyncLocal`, so it
reaches only the calling test's flow) is the meter's clock for tests.

## Pinned by

- *The unit is CHARACTERS, not bytes*: `CheckCapCharsTests` — non-ASCII is carried unescaped, the json lane's overrun
  member states its own length and clears in one step, and an astral character escapes and is counted as written.
  Below its floor the text lane refuses the same sweep and names a cap it is served at, in characters
  (`TheTextLaneNamesItsFloorInCharactersOnTheSameSweep`); no test compares the two transports' numbers.
- *Whole first, and a sufficient cap*: `WideCutLaneTests.AWholeAnswerIsServedAtItsOwnWidth` (each lane that gained
  the pass serves its complete answer at its own width), `ARefusedSpillingCallWritesNoFileAndNamesACapMeasuredWithItsSpillBlock`,
  `AFailedSpillIsStatedAndNeverSizesTheNamedCap`, `ACensusOverItsCapNamesItsOwnWidthPlusTheNextCallsRoom`,
  `NoCapAtARowBoundaryServesFewerRowsWithoutANotice` (a whole pass that stopped is never served);
  `ServedReplyCheckTests` (a reply over its cap after its write is refused and its file removed, `truncated` comes
  from the served render, a summary scan sizes with no resolve, a chunk miss is typed as the write types it, a refused
  `asset_status` builds no record index, a render that cannot stop lays its whole answer once); `RenderFloorHoldTests` (a floor
  that never settles is refused; a floor that grows a digit names a cap it fits; a failed spill too wide to state is
  the refusal); `SkyPatcherLayerFloorTests` and `SkseTransportTests` (each view refused below its floor naming a cap it
  fits, and `omit filter=` offered only where the unfiltered view fits); `BoundedPassTests` (no pass is wider than the
  cap it asks about plus a row, a cut scan reads only the bodies it lays, a refused cut hands its artifact writer a
  sizing target only, and a scan's sized block is the written one with no body read);
  `CheckMergeShapeMatrixTests.TheTextLaneRefusesEveryResponseOverItsCap`. Each asserts the refusal names a cap at which
  the same call is served, never the number itself; a test that reads a cut notice reads it at a cap the server serves
  cut (`RenderFloorAssert.ServedCut`).
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
  `CheckCapCharsTests.TheOverrunNoticeStatesItsOwnLengthAndClearsInOneStep` — the json lane's overrun member states the
  finished document's length, and its remedy clears the overrun in one step.
- *The comparison forms' floor and meter*: `ComparisonBoundTests` — the battery delta, the 5,798-REFR whole tree and the
  all-NPC_ narrowed tree pass the floor, a count past it refuses before any read, and a whole-record refusal leads with
  `fields=`. `ComparisonMeterTests`, on a clock read in order or driven by the read counters:
  - on each lane (list delta, list tree, scan, off-order), a projection past ten minutes refuses after two chunks naming
    the clock's rate, with nothing read past them;
  - the first chunk alone never refuses, a dear first chunk then a cheap second runs, and one chunk left is never
    refused;
  - a list dear in the middle refuses at the third chunk;
  - the named figure runs when fed back, through two checks;
  - a projection just past the budget reads 10.1 minutes, not 10;
  - time before the batch is named as the cause, and a census names a narrower selection;
  - a delta chunks only rows that read: malformed and unresolved ids, ids the off-order file lacks, and a named source
    that does not touch an id walks no plugin for it;
  - a call under the budget reads every row once.

  `RecordsRenderCostTests.TheEstimateReadsProperlyJustOverTheComparisonBound` — the floor constants: 20,000 whole and
  15,000,000 narrowed rows run, one more refuses.
- *The render bound is a time budget, not a width one*: `RecordsRenderCostTests.TheAccountingLineIsReservedFromTheRowBudget`
  — the accounting line at its widest fits inside `AccountingReserve`. It renders nothing against a cap, so that the
  reserve is taken out of `max_chars` is not asserted.

## Where

`src/housecarl-mcp/RenderCap.cs` holds `Cap`, `Budget`, the whole-first `RenderCap.Capped` and `CappedOnce`, the `WholePass` a bounded pass reports on,
the floor check `RenderCap.Hold` and `RenderCap.Settle`; `RenderBudget.cs` is the render bound;
`SweepDemand.cs` is the demand pass and `BodyAllocation.cs` the max-min fill; `SweepEmission.cs` holds `SweepSubject`
and `BoundedBody`; `BatchRender.cs` is the write-and-retract batch render; `TransportAccounting.cs` is the four-cause
omission block; `RowProjection.cs` is the `rows` project form. `src/housecarl-core/CharCountedStream.cs` is where the
json lane takes its length from, and `src/housecarl-core/JsonTextEncoder.cs` is the one encoder. Entry points:
`max_chars=` on every tool that takes it, the merged `housecarl_check` response, and the render bound on
`housecarl_records` and `housecarl_asset_status`.

## Related

- `docs/architecture/check-family-tests.md` — which lane a check-family fact is driven from.
- `docs/architecture/tool-schema-publication.md` — the published-schema passes, including `SchemaDepthCap`.
