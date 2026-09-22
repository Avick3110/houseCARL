---
updated: 2026-09-23
covers: [src/housecarl-core/DialogueInfoOrder.cs, src/housecarl-core/DialogueFold.cs, src/housecarl-core/DialogueSubtype.cs, src/housecarl-mcp/DialogueTools.cs]
---
# Dialogue: the merged INFO order, the fold, and the SNAM marker

## What it is
The modder-facing page is [`docs/dialogue.md`](../dialogue.md): what decides which line plays, and where a line
lands. This note is the implementation side — the contracts the code cannot state about itself.
What houseCARL checks before it lets dialogue out — CK parity, a clean pass, the check family — is
[`dialogue-validation.md`](dialogue-validation.md).

## Contracts

### The merged INFO order

A topic's lines are ordered and the game plays the FIRST INFO whose conditions pass, so a pure reorder changes
behaviour with no field delta anywhere. No single record holds that order: each plugin's DIAL carries only its own
child list, and the effective sequence is the MERGE of every touching plugin's list in load order. xEdit's
`TwbGroupRecord.Sort`/`ProcessDIAL` is the reference implementation.

For each touching plugin in load order, for each INFO in that plugin's child list in its order:

1. EVICT every copy of that INFO already placed, so exactly one entry per FormKey survives and the last plugin to
   list an INFO owns its position.
2. Place it: PNAM absent → TAIL; PNAM present and unresolvable → HEAD; PNAM resolving to T → immediately after T
   (T placed first if absent, cycle-guarded).

Consequences the render depends on:

- Non-relisting drops nothing, it REORDERS. The winning topic's `Responses` is not the in-game INFO set, so any
  model treating the winner's child list as authoritative wholesale is wrong.
- "PNAM absent" and "PNAM present but zero" place at OPPOSITE ends, and `PnamZeroIsDistinguishable` says the reader
  does tell them apart. That is the fidelity ceiling of the whole merge. Re-verifying it needs a zero PNAM
  constructed ON DISK, because of the writer limit [`docs/dialogue.md`](../dialogue.md) states, so a round-trip
  fixture measures the writer rather than the reader. Pinned by `DialogueInfoOrderProbe`'s `PNAM-ZERO-AXIS` and
  `WRITER-DROPS-NULL`.
- `Moved` is not "the index changed" — moving one line to the bottom shifts every line after it. It flags only lines
  that changed RELATIVE order, the minimal set outside a longest common subsequence against the defining plugin's
  own list (`NO-FALSE-MOVE`, `REORDER-TO-TAIL`).
- Input is plain `InfoLine` data, not record getters: the caller projects each child list while the body is live, so
  the merge runs after every overlay is gone and one typed DIAL pass per plugin serves every topic in a batch.
- Pure, no I/O, never throws. Malformed input — a self-referencing PNAM, a cycle, a chain past the depth ceiling —
  degrades to a stated placement and is reported on the view's `Note`, never as an exception or a silent guess.
  Cycles are counted over the final placed set rather than off the recursion, because a cycle both of whose members
  an earlier plugin already placed never trips the placement-time guard (`CYCLE-PREPLACED`; `PNAM-CYCLE` pins that
  the recursion terminates).

A view's negative claims are gated: `Complete` (every touching plugin's list read) gates "nothing merges here",
`BaselineTrusted` gates every origin-derived claim including "added by a later plugin", and `MovesComputed` gates
reading an empty `Moved` as "nothing moved".

### The fold

`DialogueFold` projects ONE off-order plugin into a dialogue read as if it were enabled. Where it lands is read off
its header against one captured build, and the same `SlotIndex` answers both lanes:

| case | placed |
|---|---|
| the order already carries this FILENAME (a shadowed copy named by `{file, mod}`) | that plugin's own slot |
| a master (header Master flag, or a `.esm`/`.esl` name) | after the LAST master in the order |
| a regular plugin | the end of the order |

The ESL header flag alone does not put a file in the master block: an esp-fe is light in the FormID space and a
regular plugin in the order. The master block is not always a contiguous prefix, so the position comes off the
order index rather than a backward scan.

One rule covers all three: the fold wins what it carries only where nothing BELOW its slot touches the record. The
shadowed case sits AT the slot, so it never blocks its own replacement.

Two depths. `Read` projects the DIAL child lists and closes the file, keeping the rest of the read surface's rule
that no plugin is held open. `Open` also keeps the record BODIES, which are overlay-backed and live only while the
file is open — so that fold is disposable and the one lane taking it disposes it at the end of its run. `Open` holds
EVERY record, not just DIAL: a CTDA parameter can name any record type, and a type filter would leave a record the
folded file defines reading as "not in the active load order". Seeking on demand instead is filed as #795.

The fold's `Label` is what its rows are rendered under and differs from `Plugin` for a shadowed copy. `Plugin` is the
plain filename and is the value written as data — a `.seq` lint compares it, a render writes `winner_plugin`, an
artifact holds a column. Provenance rides beside the name, never inside it.

The fold never sets the move baseline unless it DEFINES the topic: a master-block fold can land ahead of the definer,
which would render the definer's own lines as "added by a later plugin" and half the topic as MOVED.

### The SNAM subtype marker

A DIAL carries its subtype twice: `DATA\Subtype`, a numeric enum, and SNAM, a 4-character text marker. The engine
buckets topics by the MARKER, and Mutagen writes SNAM verbatim rather than deriving it — so a create that sets only
`Subtype` leaves SNAM at 0000, a byte-valid plugin the engine can crash on at load.

`DialogueSubtype`'s table is sourced from xEdit's record definition, not derived: the name-to-marker mapping is not a
blind echo (`Custom`→`CUST` but `ShootBow`→`FIWE`), Mutagen does not model it, and it cannot be scraped from vanilla
because Bethesda's own numbers are stale. CI asserts `Enum.Parse(Name) == index` for every named row, so a transposed
row cannot pass silently.

Numbers go stale because the Dragonborn-era CK inserted six `FlyingMount*` values at index 20: a topic authored
before that stores a number six lower than the modern table, and every reader labels it six entries too early.
Nothing on the record distinguishes the two numberings — form version does not, since Dragonborn.esm mixes both at
FormVersion 43 — so SNAM is the only reliable statement and the disagreement is reported, never "fixed". Which advice
is given turns on the renumbering signature: exactly six below is vintage, anything else is two fields edited apart
and the `Subtype` edit is an in-game no-op until SNAM is synced.

The create path fills a BLANK marker only; the edit path syncs a stale one, but only after a call that set `Subtype`
and did not set `SubtypeName` — gating on "this call set Subtype" is the caller's job, and it is what keeps the sync
off the countless vanilla topics whose number is legitimately noisy.

## Pinned by
- `DialogueInfoOrderProbe` (`src/housecarl-generator`) — the merged INFO order, by the arms named beside its
  sentences above.
- `DialogueFamilyTests.FactD1_TheShippedRenderStatesTheMergeModel` — the `info_order` render states the merge
  model and never says a line is dropped; `FactD3_UnreadWired` — a touching plugin that could not be read makes
  the view INCOMPLETE and is named.
- `CheckDialogueFoldTests.AMasterFoldDoesNotWinWhatARegularPluginOverrides` and
  `AShadowedFoldTakesTheActiveSlotSoLowerPluginsStillWin` — the fold's placement and its one wins rule;
  `TheFoldedProvenanceIsRenderedInTextAndCarriedInJson` — provenance rides beside the name.
- `DialogueSubtypeMarkerGuardProbe` (`src/housecarl-generator`) arms TABLE-SHAPE / TABLE-ANCHOR — the marker
  table; AUTOFILL / DEFAULT-CUST / EXPLICIT-WINS — the create path fills a blank marker and
  never overrides an explicit one.
- `DialogueFamilyTests.ATopicWhoseSubtypeContradictsItsMarkerSaysTheMarkerWins` and
  `AMismatchWithoutTheRenumberingSignatureSaysTheSubtypeEditIsANoOp` — the disagreement is reported with the
  marker as authoritative, and the advice turns on the renumbering signature.

## Where
- `src/housecarl-core/DialogueInfoOrder.cs` — the merge over `InfoLine` data, and the view with its gates.
- `src/housecarl-core/DialogueFold.cs` — `DialogueFold`: `PlaceIn`, the `Read` and `Open` depths, `Label`.
- `src/housecarl-core/DialogueSubtype.cs` — the SNAM marker table and `MarkerDisagreesWithSubtype`.
- `src/housecarl-mcp/DialogueTools.cs` — `DialogueWire`, the composers a dialogue report is rendered from,
  including `AppendInfoOrderView`.
- Tool: `housecarl_records project=info_order`.
