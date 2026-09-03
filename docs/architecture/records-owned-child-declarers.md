# Owned-child declarers: two tiers, one sentence source

**Class:** LIVING. Subsystem: `ReadSentences` (the owned-child consts), `LoadOrderService.AnnotateOwnedChildContent`
/ `ResolveTreePinned`, `RecordsTools.AppendChildDeclarers`, `JsonWire.WriteTreeRow`, `Artifacts.WriteTree`.
Pinned by `RecordsOwnedChildTests` (`src/housecarl-mcp-tests`) and `OwnedChildContentProbe`
(`src/housecarl-generator`).

## Why two tiers

A child-bearing field (a cell's `Persistent`/`Temporary`, a topic's `Responses`, a worldspace's `SubCells`) is
declared per plugin and assembled by the game from every declarer. An override that touches the parent for an
unrelated reason (occlusion, lighting) carries none and deletes none, so reading it reports an empty collection
the game fills — the #342 bug.

Naming WHICH plugins declare requires reading their bodies. Doing that per toucher on every read was built,
measured on a real load order, and withdrawn: 27ms -> 588ms for one Dawnstar cell, 21ms -> 1.3s for Tamriel, a
worldspace read to 2.5s, 6.3s -> 126s for a 200-cell query, and an unbounded artifact job that never finished. So
the DEFAULT read states only the cheap fact the index gives for free (`ReadSentences.NotRead` and friends): other
plugins touch the record, their declarations were not read.

`records project={"form":"tree"}` already opens every provider's body to build its diff, so it states the
precise fact there at no extra cost: which plugins actually declare (`ReadSentences.DeclaredBy` / `CarriedBy` /
`NoDeclarers`, composed per field by `DeclarersNote`).

## History

Until the 1.x cut, the precise tier lived on `read_record conflict_tree=true`. No 2.0 surface sets that flag, so
it had no caller and was deleted with its sentences (gap #485, PR #484's cut). #485 restored it on the tree form
instead — the 2.0 lane that already pays the cost the tier needs, with no new parameter, pole, or vocabulary.

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
