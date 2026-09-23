---
updated: 2026-09-23
covers: [src/housecarl-core/OwnedChildUnion.cs, src/housecarl-core/OwnedChildContent.cs, src/housecarl-core/OwnedChildLifecycle.cs, src/housecarl-mcp/ReadSentences.cs, src/housecarl-mcp/RecordReads.cs, src/housecarl-mcp/JsonWire.cs, src/housecarl-mcp/RecordsTools.cs, src/housecarl-mcp/Artifacts.cs]
---
# Owned-child content: the additive union, and one sentence source

## What it is

**Class:** LIVING. Subsystem: the files in `covers:` above. Pinned by `RecordsOwnedChildTests` (`src/housecarl-mcp-tests`) and `OwnedChildContentProbe`
(`src/housecarl-generator`).

## Contracts

### What a read of a child-bearing field answers

A child-bearing field (a cell's `Persistent`/`Temporary`, a topic's `Responses`, a worldspace's `SubCells`) is
declared per plugin and assembled by the game from every declarer. An override that touches the parent for an
unrelated reason (occlusion, lighting) carries none and deletes none, so reading it reports an empty collection
the game fills — the #342 bug.

So a read states two quantities, and they are not the same:

- the field's **VALUE** — the body the read was taken from, its own list, in its own order. Those are the
  addresses a write uses (`Temporary[0]` on a `Remove`), so the union is never spliced into them.
- the **additive union** beside it (`OwnedChildUnion.Compute`) — every distinct child every touching plugin
  declares, keyed by FormID so a child two overrides both declare counts once, with each contributor's own
  count and how much of it the read body carries. It rides `FieldValue.Display`, which reaches text, json and
  artifacts through one carrier, and json also carries it structurally as `owned_child_union` (the member
  FormIDs, capped at `JsonWire.ChildUnionMemberCap`).

A SINGULAR owned child (`Cell.Landscape`, `Worldspace.TopCell`) is not a union — its declarers override one
record — so the note says which plugin's copy is live instead of adding counts that would be a fiction.

The field set the read side asks about is `WriteEngine.ChildBearingProperties`, the same reflected walk the write
surface's child preservation runs on, so the two cannot diverge and a Mutagen bump that adds a child-bearing
property is picked up with no edit. The read engine hands it a GETTER, so the lookup maps getter to concrete
through `WriteEngine.PrimaryGetter` / `ConcreteOf` first. That hop is load-bearing rather than tidy: the walk
needs a SETTABLE property, and an overlay type exposes the LIST children settably while exposing the SINGULAR
one read-only — so asking the runtime type directly answers correctly for `Persistent`/`Temporary` and silently
drops `Cell.Landscape` and `Worldspace.TopCell`.

The union claims DECLARATION, not liveness: whether a member's own winner is deleted or initially disabled is a
fact about that child record, and asserting it here would cost one fetch per member.

### Which lanes assemble it

The union costs a body per touching plugin, so it runs only where the CALLER named the records: the single read,
`batch_record_detail`, and the `records` source pole. Those lanes are handed a formid list, and one
`LoadOrderService.ChildUnionMemo` per call assembles each record once however many times it is named.

The SCAN lanes — the `cross_plugin_query` detail rows, the dense grid, and the artifact spill of either —
discover their row count instead of being handed it, so a body per toucher per row is a cost nobody asked for.
They annotate with the index-only note (`ReadSentences.NotRead`): other plugins touch this record and their
declarations were not read, plus a clause naming the formids lane that does assemble the union. Annotated either
way — the field is a false-empty on both — but a scan never claims a quantity it did not compute.

### What it costs

One body per touching plugin, and only on a read that actually emitted a child-bearing field — a projection
naming none pays nothing. The bodies are seeked by the record's own type
(`LoadOrderResolver.IndexView.GetRecord` takes it), which is #354: Mutagen's typed enumeration walks only the
GRUPs that can hold that type, so finding a cell does not step over the placed references that outnumber it.

Measured on a #354-shaped synthetic order — one 19.2k-record master and nine ~2k plugins, a worldspace of 200
exterior cells each carrying 40 placed references, every plugin overriding the worldspace and every cell while
declaring no references (the #342 shape), so every cell has ten touchers:

| shape | without the union | with |
|---|---|---|
| one cell, `fields=["EditorID"]` / `fields=["Temporary"]` | 1.9 ms | 5.7 ms |
| the worldspace, `fields=["EditorID"]` / `fields=["SubCells"]` (union of 200 cells) | 1.8 ms | 8.8 ms |
| 200 cells named by `formids=` | — | 1.04 s |
| the same 200 cells as a `types=["CELL"]` SCAN | 0.24 s (index-only) | not run |

That is what the per-toucher walk was withdrawn over, re-measured. The withdrawal's five numbers were 27ms ->
588ms for a Dawnstar cell, 21ms -> 1.3s for Tamriel, a worldspace read to 2.5s, 6.3s -> 126s for a 200-cell
query, and an artifact job that never finished. The typed seek is what the first three were paying: a worldspace
read here is 8.8 ms, because the seek finds the parent without stepping over the references, and `ChildKeys`
stops at the cells rather than descending into them. The last two are answered by the lane split rather than by
the seek — a 200-cell query is a scan, and a scan states the index-only note, so neither it nor the artifact job
it spills to multiplies the union by N rows. Naming those 200 cells by formid still costs a second, which is one
body per toucher per named record and is what the caller asked for.

Both numbers are from this synthetic, not from a real load order; the shapes a real order adds are more touchers
per cell and bigger plugins, both of which the per-toucher cost scales with linearly.

#### One overlay cache per call, not per record

A session is the cache that keeps a plugin's overlay open, and `ResolveRead` used to open its own per record —
so a batch re-mmapped every toucher once per ROW. The `ChildUnionMemo` does not cover this: it dedupes a
repeated FORMID, and a batch's rows are distinct records over the SAME plugins. So the batch lanes now open one
session for the call and hand it down. On the synthetic above, the 200-cell `formids=` batch:

| | overlay opens | wall |
|---|---|---|
| a session per record | 2,050 | 2.12 s |
| one per call | 10 | 1.37 s |

The remaining second is the per-toucher body walk itself, which is the thing the caller asked for. Opens scale
with the ORDER's plugin count now instead of with rows, which is what makes the real-order shape — more
touchers, bigger plugins — cost more only in the walk. `RecordsOwnedChildTests` pins the count via
`LoadOrderResolver.SessionOverlayOpens`; nothing else reads it.

### The tree form still names WHICH

`records project={"form":"tree"}` opens every provider's body to build its diff, so it states per-provider
declaration there at no extra cost (`ReadSentences.DeclaredBy` / `CarriedBy` / `NoDeclarers`, composed per field
by `DeclarersNote`). The union answers "how much is here and where does it come from"; the tree answers "which
provider declares what".

### The negative is a sentence, not silence

The deleted tier said nothing at all when nobody declared, which a caller cannot tell apart from the tier never
having run. `ReadSentences.NoDeclarers` states it instead. It claims only over bodies that were READ — a
provider whose field could not be read is counted separately (`CouldNotRead`), never silently absorbed into
"nobody declares" (the #308 rule, one level down at the sentence layer).

The same rule holds at the value walk under it. `OwnedChildContent.DeclaresChild` and
`OwnedChildUnion.ChildKeys` answer NULL for "could not look" and never false: a body that would not read, a
container shape the walk does not know, and a nesting depth past the walk's own tripwire all answer null rather
than being reported as an empty field. The tripwire is a guard against a Mutagen shape nobody has seen, not a
limit the current model approaches. One case needs stating because Mutagen makes it look like an answer: a typed
containment enumeration Mutagen cannot route yields an EMPTY sequence rather than throwing, so an empty typed
walk over a container that holds records at all is read as a MISS, not as a negative.

`DeclaresChild` reads a BODY, and reading a body is not free — the resolver fetches one by enumerating a whole
overlay — so a caller asking it of every plugin touching a record pays per plugin. Only the lane that has already
fetched those bodies (the conflict tree) asks it; the default read answers the cheaper question the index alone
can settle.

### Two shapes

A COLLECTION field (`Persistent`, `Temporary`, `Responses`, `SubCells`) is assembled additively, so its line
NAMES declarers (capped at `DeclarerNameCap`, "+N more" past it — hundreds of names would be noise). A SINGULAR
field (`Cell.Landscape`, `Worldspace.TopCell`) is one record several plugins override, so naming every overrider
would be the same noise; the line is a COUNT instead. `DeclarersLead` states both shapes once per record, not
once per field — the same response/field split the cheap tier's own clause established.

The split has a write-side consequence, which is what `OwnedChildLifecycle` exists for. Mutagen's typed
`Remove(FormKey, Type)` is the blessed drop for every record in a group and reaches placed references and INFOs,
but a SINGULAR owned child is a plain property on its parent and belongs to no group, so the remove routing
finds nothing and returns without throwing — the silent no-op the survivor check catches. A delete therefore
finds the SLOT: the parent, the settable property, and, for a collection slot, the live `IList` the child is an
element of, which may sit below the property rather than being it (a worldspace's cells are under block
structs). The slot set is the same `ChildBearingProperties` walk, so nothing here names a record type. A detach
that cannot be made — a property that will not set, a child in no list the engine can drop it from — is
surfaced as a sentence and nothing is written. The records the child itself carries are what a detach takes with
it, which is why a descendant the caller did not name is a refusal rather than a side effect.

### The unit a count is in

A union counts RECORDS; a field's rendered value counts the field's own ELEMENTS. On every flat field those are
the same things. On `Worldspace.SubCells` they are not: the value counts blocks, and the cells sit two container
levels under them — a worldspace declaring no cells at all still renders `[list: 2 item(s)]` for empty block
scaffolding. Setting an own-share of 0 beside a value of 2 with nothing saying they differ is a contradiction on
the face of the line, so the nested shape names its unit (`ReadSentences.OwnShare`) and json carries `nested`.
Which fields are nested is `OwnedChildContent.NestedFields` — the element type of the property, off the same
reflected child-bearing set as the shape, never a list naming `SubCells`.

### Placement: above the diff, not inside it

The diff renders differences; a provider whose content in a child-bearing field equals the reference's is
omitted from it. The declarers block is a statement about declarations, so it sits with the provider list
rather than inside a view that would silently drop half its subjects. It is emitted for every row whose type
owns children, sole-toucher rows included — the block is not a diff and needs no second provider to be true.

### What it costs, and where it narrows

No extra record fetch: the tier asks its question of each body already open for the diff. The field set is
`OwnedChildContent.Fields(body)` — reflection over the type, never a hand list — narrowed to the top-level
field NAMES the caller's own `fields=` requested, not the paths the response actually emitted; a bracketed
path (`fields=["Temporary[0]"]`) narrows the block away entirely, matching the cheap tier's own narrowing.

Both text and json check `max_chars` at every point the block can grow the response, including the two tails
that are easy to miss: the block's own last line (text has no diff loop to notice it on a sole-provider row)
and json's response-level `child_declarers_note`, written after `truncated` is already computed. Either half
hitting the cap sets the response's own `truncated` flag, which triggers the standard auto-spill to a JSONL
artifact rather than a silent overrun.

The lead itself (`DeclarersLead`) is invariant framing text, so it is stated at most once per response on
every transport, and it is **reserved** rather than written and regretted — text checks its length against
the remaining budget before writing it, and the cheap tier reserves its own clause the same way
(`ReadSentences.ClauseReserve`). json reserves it because a `Utf8JsonWriter` cannot un-write a property once
appended, and the reserve there is every byte that still lands after the check, not just the sentence's own:
its encoded cost (`JsonWire.DeclarersLeadReserve`), the `truncated` boolean written between the check and the
note (`TruncatedPropertyReserve`), and the root close (`Framing.RootClose`). All three are measured off the
writer, never hand-counted. Content lines still overshoot the cap by at most one line, which is the whole
lane's existing tolerance; invariant framing does not.

A cut notice claims only what was cut. The text block's tail is reachable only when every declarer line was
written, so it says nothing about the declarers: it ends the row, and the caller — which knows whether the
row had a diff to lose — names the nodes it dropped, or stays silent on a sole-provider row that lost
nothing. All five of the lane's cut notices compose through one `RecordsTools.CutNotice`, so the
grammar guard that harvests one rendered notice covers the wording of all of them.

## Pinned by

- *What a read of a child-bearing field answers*: `RecordsOwnedChildTests.AChildBearingFieldStatesTheAdditiveUnionTheGameAssembles`
  — the #342 shape; `TheUnionNeverReplacesTheBodysOwnList` — the VALUE stays the body's own list;
  `AChildTwoPluginsBothDeclareIsCountedOnce_NotConcatenated` — the union is keyed by FormID;
  `ASingularChildSaysWhichPluginsCopyIsLive_NeverAUnionCount` — a SINGULAR child is not a union (all in the same
  class).
- *What a read of a child-bearing field answers*: `OwnedChildContentProbe` (ci probe `owned-child-content-guard`) —
  the getter-to-concrete hop resolves for every child-bearing type (the BY CONSTRUCTION arm).
- *Which lanes assemble it*: `RecordsOwnedChildTests.AScanStatesTheIndexOnlyNote_NotTheUnionItWouldPayPerRowFor` and
  `AScansClauseIsTheIndexOnlyOneAndNamesTheFormidsLane` — a scan annotates with the index-only note and names the
  formids lane; `TheSameCellNamedByFormidIsUnioned` — the formids lane assembles the union.
- *One overlay cache per call, not per record*: `RecordsOwnedChildTests.ABatchOpensEachPluginOnce_NotOncePerRecordItUnions`
  — the open count, read through `LoadOrderResolver.SessionOverlayOpens`.
- *The tree form still names WHICH*: `RecordsOwnedChildTests.ThePreciseTierNamesEveryProviderDeclaringInACollectionField`
  — per-provider declaration on the tree form.
- *The negative is a sentence, not silence*: `RecordsOwnedChildTests.AFieldNoProviderDeclaresInGetsTheNoneSentence_NeverSilence`;
  `OwnedChildContentProbe`'s UNREADABLE and SENTENCE arms — `DeclaresChild` answers null, never false, for "could
  not look", and "nobody declares" never absorbs a body that could not be read.
- *Two shapes*: `RecordsOwnedChildTests.ASingularChildFieldIsCountedNotNamed` — a SINGULAR field's line is a count;
  `OwnedChildContentProbe`'s SENTENCE arms — the collection note caps its names.
- *The unit a count is in*: `RecordsOwnedChildTests.ANestedFieldsNoteNamesItsUnit_TheValueCountsContainersAndTheUnionCountsRecords`
  and `JsonMarksTheNestedFieldAndLeavesTheFlatOneUnmarked`.
- *Placement: above the diff, not inside it*: `RecordsOwnedChildTests.TheBlockSitsWithTheProviderListNotInsideTheDiff`
  and `ASoleProviderRecordStillGetsTheBlock_ItIsNotADiff`.
- *What it costs, and where it narrows*: `RecordsOwnedChildTests.FieldsNarrowsTheBlockToTheNamedTopLevelField_NotTheWholeType`
  and `FieldsWithABracketedPathInsideAChildBearingFieldYieldsNoBlockAtAll` — the narrowing;
  `TheLeadIsStatedOnceAcrossMultipleRowsInText_NotPerRow` and `TheFramingLineIsReservedAgainstMaxChars_NotWrittenPastIt`
  — the lead is stated once and reserved; `Json_TheResponseLevelLeadIsDroppedRatherThanOverrunningCap_AndTruncatedIsSet`
  — json's reserve.

## Where

`src/housecarl-core/OwnedChildUnion.cs` computes the union (`Compute`, `ChildKeys`); `OwnedChildContent.cs` holds
`DeclaresChild`, `Fields` and `NestedFields`; `OwnedChildLifecycle.cs` finds the slot a delete detaches from.
`src/housecarl-mcp/ReadSentences.cs` is the one sentence source; `RecordReads.cs` is the service's read lanes and
the per-call `ChildUnionMemo`; `JsonWire.cs` writes `owned_child_union` and the json reserves; `RecordsTools.cs`
renders the declarers block and `CutNotice`; `Artifacts.cs` carries the annotation into a spilled artifact. Tool:
`housecarl_records` — `fields=`, and `project={"form":"tree"}` for the per-provider tier.
