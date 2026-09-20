---
updated: 2026-09-18
covers: [src/housecarl-core/DialogueInfoOrder.cs, src/housecarl-core/DialogueFold.cs, src/housecarl-core/DialogueValidate.cs, src/housecarl-core/DialogueCkParity.cs, src/housecarl-core/DialogueSubtype.cs, src/housecarl-core/DialogueScriptCheck.cs, src/housecarl-core/DialogueCheck.cs, src/housecarl-mcp/DialogueSweep.cs, src/housecarl-mcp/DialogueSweepRender.cs, src/housecarl-mcp/DialogueTools.cs, src/housecarl-mcp/DialogueKindChecks.cs]
---
# Dialogue: the merge, the fold, CK parity, and what a clean pass means

**Class:** LIVING. Subsystem: `DialogueInfoOrder`, `DialogueFold`, `DialogueValidate`, `DialogueCkParity`,
`DialogueSubtype`, `DialogueScriptCheck`, `DialogueCheck` (`src/housecarl-core`); `DialogueSweep`,
`DialogueSweepRender`, `DialogueWire`, `DialogueKindChecks` (`src/housecarl-mcp`).
Pinned by `DialogueInfoOrderProbe`, `DialogueCkParityGuardProbe`, `DialogueSubtypeMarkerGuardProbe` and
`DialogueValidateGuardProbe` (`src/housecarl-generator`), and by `DialogueFamilyTests`, `CheckDialogueFoldTests` and
`DialogBranchFlagsRefusalTests` (`src/housecarl-mcp-tests`).

The modder-facing page is [`docs/dialogue.md`](../dialogue.md): what decides which line plays, and where a line
lands. This note is the implementation side — the contracts the code cannot state about itself.

## The merged INFO order

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

## The fold

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

## CK parity

Mutagen omits a null/unset optional subrecord on write; the Creation Kit writes it unconditionally, nulls included.
A record authored through houseCARL that sets only the fields the author cared about therefore differs STRUCTURALLY
from a CK-authored one of the same content. `DialogueCkParity` closes that by default-populating the nullable fields
the CK always emits, at create time, inside the Mutagen model.

Three invariants hold for every default, and the DialogTopic SNAM marker follows the same pattern:

1. NON-OVERRIDE — fill only where the author left the field null; never clobber an explicit value.
2. NEVER SILENT — every fill returns a `CkParityFill` the create path surfaces as an `OpResult`.
3. BY CONSTRUCTION — the values are what a CK-authored record of the same content carries, byte-verified against
   vanilla reference plugins.

Every fill path and its read-only counterpart share one presence predicate, so a create that FILLS a field and a
validate that FLAGS its absence cannot disagree. `DialogueCkParityGuardProbe` pins the pairs.

Three tiers:

| tier | fields | consequence of omission |
|---|---|---|
| confirmed crash | INFO CNAM (FavorLevel), INFO ENAM (Flags); DLVW DNAM, ENAM | the Creation Kit crashes when the owning topic or the Dialogue Views editor is opened; the game tolerates it |
| byte parity | DLBR TNAM (Category); DIAL PNAM (Priority); QUST ANAM, objective FNAM, alias FNAM, reference-alias VTCK | a byte mismatch against a CK-authored record, no confirmed crash |
| in-game behaviour | DLBR DNAM (Flags) | an absent DNAM reads as `TopLevel`, so a branch the author never marked top-level is published to the player's menu |

There is no honest default for DLBR `Flags`, so the create path REFUSES a branch whose `Flags` no op set; why
neither value is honest, and what to pass, is on [`docs/dialogue.md`](../dialogue.md).

Two exceptions to the is-null signal:

- DIAL `Priority` is a non-nullable float, so "the author left it unset" cannot be read off the record. The create
  path decides it from the author's OP LIST and passes it in; an explicit value, `0` included, always wins. That is
  also why the validator never flags its absence — doing so would false-positive every legitimately priority-0 topic.
- Alias VTCK is scoped to REFERENCE aliases; a Location alias resolves to a place, not an actor, and the same gate
  guards the fill and the gap.

A `0`-fill materialises the subrecord only, so every named flag inside it — `OrWithPrevious` on an objective, the
alias flags, and the INFO `Flags` struct's `Goodbye`, which [`docs/dialogue.md`](../dialogue.md) covers — stays an
explicit authoring choice.

## The SNAM subtype marker

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

## What a clean pass means

`DialogueValidate` runs on demand over a whole topic resolved against the LOAD-ORDER WINNERS. The per-INFO body
checks walk the WINNING topic's child list, so an INFO another plugin contributes but this winner does not re-list is
not body-checked: a clean pass means "every line this winner lists is sound", not "every line in this topic is". The
effective ORDER view is the merge across all of them.

PNAM ABSENCE is never flagged — vanilla leaves it empty and selects intra-topic by Conditions — and only a SET but
unresolvable PNAM is reported. Deleted INFOs are skipped and tallied. Resolution scope is the active order;
validating within {plugin + its masters} alone is a deliberately deferred capability. The whole run is wrapped, so a
resolve or asset failure rides `CheckError` rather than being swallowed.

Standing limits a render must state rather than let "checks passed" read as "this will play": CTDA conditions are
semantic and only the game evaluates them, and lip-sync and audio content are outside the data layer.

Two ownership gates keep the noisy findings off content the modder neither wrote nor can act on. The SNAM Problem
escalation fires only where the winner IS the FormKey's defining master — a blank-SNAM override ships in working
mods, so an override is a Warning. The subtype-disagreement and unmodeled-marker warnings fire only on a record a
force-loaded plugin does not own, since a base master, a Creation Club plugin or `_ResourcePack.esl` carries
Bethesda's stale number and is not something the modder can act on. The subtype-disagreement warning carries a
second exemption the unmodeled-marker warning does not: an override that copies the base record's (Subtype, SNAM)
pair forward verbatim changed neither field, so it stays quiet, while an override of an unmodeled marker still
warns. The verdict rides the ungated `subtype_stale` and `subtype_from_marker` fields either way.

The condition lints are the data-layer-decidable subset and every one is a structural true positive; all emit
Warning. The load-bearing gate is the FLOI mode gate: a condition form parameter is a `FormLinkOrIndex`, a form only
when `UseAliases` and `UsePackageData` are both false. On the binary overlay an index-mode FLOI's `.Link` is a bogus
low FormKey synthesised from the index bytes rather than null, so reading it as a form would false-flag a well-formed
alias-mode gate. The dangling-parameter sweep reflects over the Data arm's properties rather than listing functions,
so it covers every function Mutagen models — the generated-coverage cornerstone.

Deliberately not linted, as semantic rather than structural: Run On Subject-vs-Target intent, the faction-rank gate
value, and intra-topic Info-variant ordering.

## The check family

The dialogue family on the merged `check` surface is SEEDED, not swept. Selection is by record — a quest expands into
every topic it owns — so `plugins=` and `exclude=` do not scope it, and the response says so. An empty seed list is a
REFUSAL, never a widening: resolving it to "the whole order" would run a whole-order dialogue sweep, which is refused
on cost. A seed that does not resolve is carried as a named refusal, never dropped, because the scope IS the seed
list and a discarded seed silently narrows it.

The effective merged INFO order is deliberately absent from this family. It is an ordered sequence over the
touching-plugin stack rather than a findings list, so it belongs to `records project=info_order`. Both surfaces share
ONE render, `DialogueWire.AppendInfoOrderView`; the family gates it off.

Which checks a seed's kind runs comes from one table, `DialogueKindChecks`, read both by the seed's own verdict line
and by the family's boundary claim, so the two cannot disagree. An unrecognised kind claims nothing rather than
defaulting to the widest set. The same table decides what the epoch stamp names: all three asset-substrate verdicts
(`.fuz`, `.pex`, `.seq`) live behind the graph checks, so a call whose every seed was a DLVW or DLBR is record
substrate throughout and the stamp caveats nothing.
