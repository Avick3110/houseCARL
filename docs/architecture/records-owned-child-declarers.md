# Owned-child content: the additive union, and one sentence source

**Class:** LIVING. Subsystem: `OwnedChildUnion` and `OwnedChildContent` (`src/housecarl-core`), `ReadSentences`
(the owned-child consts), `LoadOrderService.AnnotateOwnedChildContent` / `ResolveTreePinned`,
`JsonWire.WriteChildUnion`, `RecordsTools.AppendChildDeclarers`, `JsonWire.WriteTreeRow`, `Artifacts.WriteTree`.
Pinned by `RecordsOwnedChildTests` (`src/housecarl-mcp-tests`) and `OwnedChildContentProbe`
(`src/housecarl-generator`).

## What a read of a child-bearing field answers

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

The union claims DECLARATION, not liveness: whether a member's own winner is deleted or initially disabled is a
fact about that child record, and asserting it here would cost one fetch per member.

## Which lanes assemble it

The union costs a body per touching plugin, so it runs only where the CALLER named the records: the single read,
`batch_record_detail`, and the `records` source pole. Those lanes are handed a formid list, and one
`LoadOrderService.ChildUnionMemo` per call assembles each record once however many times it is named.

The SCAN lanes — the `cross_plugin_query` detail rows, the dense grid, and the artifact spill of either —
discover their row count instead of being handed it, so a body per toucher per row is a cost nobody asked for.
They annotate with the index-only note (`ReadSentences.NotRead`): other plugins touch this record and their
declarations were not read, plus a clause naming the formids lane that does assemble the union. Annotated either
way — the field is a false-empty on both — but a scan never claims a quantity it did not compute.

## What it costs

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

## The tree form still names WHICH

`records project={"form":"tree"}` opens every provider's body to build its diff, so it states per-provider
declaration there at no extra cost (`ReadSentences.DeclaredBy` / `CarriedBy` / `NoDeclarers`, composed per field
by `DeclarersNote`). The union answers "how much is here and where does it come from"; the tree answers "which
provider declares what".

## History

Until the 1.x cut, the per-provider tier lived on `read_record conflict_tree=true`. No 2.0 surface sets that
flag, so it had no caller and was deleted with its sentences (gap #485, PR #484's cut). #485 restored it on the
tree form instead — the 2.0 lane that already pays the cost the tier needs, with no new parameter, pole, or
vocabulary.

#342 stage 1 (PR #355) was the index-only tier: the default read said other plugins touch this record and their
declarations were not read. #342 stage 2 / #487 replaced it with the union above, once #354's typed seek made
the bodies affordable.

## The negative is a sentence, not silence

The deleted tier said nothing at all when nobody declared, which a caller cannot tell apart from the tier never
having run. `ReadSentences.NoDeclarers` states it instead. It claims only over bodies that were READ — a
provider whose field could not be read is counted separately (`CouldNotRead`), never silently absorbed into
"nobody declares" (the #308 rule, one level down at the sentence layer).

## Two shapes

A COLLECTION field (`Persistent`, `Temporary`, `Responses`, `SubCells`) is assembled additively, so its line
NAMES declarers (capped at `DeclarerNameCap`, "+N more" past it — hundreds of names would be noise). A SINGULAR
field (`Cell.Landscape`, `Worldspace.TopCell`) is one record several plugins override, so naming every overrider
would be the same noise; the line is a COUNT instead. `DeclarersLead` states both shapes once per record, not
once per field — the same response/field split the cheap tier's own clause established.

## The unit a count is in

A union counts RECORDS; a field's rendered value counts the field's own ELEMENTS. On every flat field those are
the same things. On `Worldspace.SubCells` they are not: the value counts blocks, and the cells sit two container
levels under them — a worldspace declaring no cells at all still renders `[list: 2 item(s)]` for empty block
scaffolding. Setting an own-share of 0 beside a value of 2 with nothing saying they differ is a contradiction on
the face of the line, so the nested shape names its unit (`ReadSentences.OwnShare`) and json carries `nested`.
Which fields are nested is `OwnedChildContent.NestedFields` — the element type of the property, off the same
reflected child-bearing set as the shape, never a list naming `SubCells`.

## Placement: above the diff, not inside it

The diff renders differences; a provider whose content in a child-bearing field equals the reference's is
omitted from it. The declarers block is a statement about declarations, so it sits with the provider list
rather than inside a view that would silently drop half its subjects. It is emitted for every row whose type
owns children, sole-toucher rows included — the block is not a diff and needs no second provider to be true.

## What it costs, and where it narrows

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
nothing. All five of the lane's cut notices compose through one `RecordsTools.AppendCutNotice`, so the
grammar guard that harvests one rendered notice covers the wording of all of them.
