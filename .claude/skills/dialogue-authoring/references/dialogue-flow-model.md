# Skyrim dialogue flow model

How Skyrim's dialogue records connect and what actually drives the in-game flow. Empirically confirmed
by reading a live load order through houseCARL (a generic-goodbye topic, a DA03 branch trace, and a
DialogBranch / DialogView census). Read this before authoring or auditing any dialogue — several of the
rules here are counter-intuitive, and one (PNAM) is the opposite of what a naive insert assumes.

Field names below are Mutagen spellings (what `housecarl_create_record` / `housecarl_set_field` and the
`mutagen-reference` skill use), with the xEdit signature alongside. Confirm any exact field path against
`mutagen-reference` before composing a write.

## Record hierarchy (xEdit signature ↔ Mutagen type)

- **DLVW — Dialog View** (`DialogView`): a pure Creation-Kit ORGANIZER, not a runtime driver. Holds
  `Quest` + a `Branches[]` list of DLBR. It groups a quest's branches for the editor; the game does not
  consult it to pick lines. Usually unreferenced — you rarely need to author one.
- **DLBR — Dialog Branch** (`DialogBranch`): a conversation / choice ENTRY POINT.
  `Quest` (QNAM) · `Category` (TNAM — `Player` for player choice menus) · `StartingTopic` (SNAM → the
  first DIAL of the branch) · `Flags` (TopLevel / Blocking / Exclusive).
- **DIAL — Dialog Topic** (`DialogTopic`): groups INFOs. Key fields:
  `Branch` (BNAM — back-link to its DLBR; **often unset** — generic topics have none) · `Quest` (QNAM) ·
  `Subtype` / `SubtypeName` (Custom for branch topics; Goodbye / Hello / … for generic — confirm exact
  values via the `mutagen-reference` skill; note `Service` is a `Category`, not a `Subtype`) ·
  `Category` · `Priority` · `Responses[]` = the INFOs under this topic.
- **INFO — Dialog Info** (`DialogResponses`): THE CONTENT — one entry in a topic's `Responses` list.
  `Conditions` (CTDA) · `Responses[]` (the spoken row(s) — **one INFO can hold several `DialogResponse`
  rows, or none**) · `Speaker` · `VirtualMachineAdapter` (the result script) · `LinkTo[]` (→ the next
  DIAL topic(s)) · `PreviousDialog` (PNAM → a previous INFO) · `Prompt` (the player-menu text) · `Flags`.
  An INFO **cannot exist without its parent DIAL.**

## What drives the flow

1. **Entry into a conversation:** `DLBR.StartingTopic`, or — for generic chatter — a topic matched by its
   `Subtype` (Hello / Goodbye / …).
2. **Which line fires within a topic:** the INFO's **`Conditions`** — primarily `GetStage` (quest
   progress), `GetIsID` / alias checks (who is speaking), etc. **NOT PNAM, NOT the list order.** (Real
   example: a DA03 line gated by `GetStage(DA03) ∈ [100,155)` AND `GetIsID(Barbas)`.)
3. **Topic → next topic:** **`INFO.LinkTo`** is the real conversation chain (proven: DA03Greet → LinkTo →
   DA03ConvincePlayer). This — not PNAM — is how one topic leads to the next.
4. **Quest tie:** ownership (the `Quest` field on DLVW / DLBR / DIAL) **plus** conditions reading the
   quest's **stage**. The quest stage is the master driver; dialogue is a set of conditioned views onto it.
   This is why a topic that never fires is so often a `GetStage` condition mismatch, not a wiring fault.

## Authoring traps the model implies (non-lint)

These follow from the flow model above but are author-side traps `housecarl_validate_dialogue` cannot
catch — it reads records, not the running game's stage state or CK's condition editor. Knowing them is the
difference between a line that plays and one that is silently dead.

- **`Stop()` resets a quest's stage to 0, and a stopped quest's dialogue is never evaluated.** Because the
  quest stage is the master driver (above), gating post-completion dialogue on `GetStage` / `GetStageDone`
  of a quest that *stops* fails twice over: the stage is back at 0 *and* the quest is not running to be
  checked. Carry a **persistent signal** instead — a granted spell, a global, a faction membership, an item
  — and put the post-completion dialogue on an **always-running** quest, gated on that signal.
- **A monologue is multiple `Responses` rows in ONE Info, not multiple Infos.** The INFO bullet above notes
  one INFO can hold several `DialogResponse` rows; that *is* the multi-line-speech idiom — the rows play
  sequentially and automatically. Do **not** split a speech across sibling Infos: by the flow model, only
  the **first valid Info** in a topic plays (selected top-down by `Conditions`), so everything after the
  first is dropped. Multiple Infos in one topic are for stage/condition *variants*, not consecutive lines.
- **CK conditions cannot express `(A AND B) OR (C AND D)`.** There are no parentheses; OR only joins
  adjacent condition rows. To gate a line on a real sum-of-products, **duplicate the whole Info — one per
  AND-clause** — and copy the response text into each (CK will not share it). This is why you will sometimes
  see the same line authored two or three times under one topic with different condition blocks; it is
  deliberate, not a redundant override.
- **`GetStageDone` (ever reached) is not `GetStage` (current).** `GetStageDone N` is true once stage N has
  *ever* been set, even after the quest moved past it; `GetStage` returns the *current* stage only. A
  "stage is within a window" gate therefore needs **two `GetStage` rows** (`>= N` and `<= M`), not a single
  equality.

## PNAM (`DialogResponses.PreviousDialog`) — the corrected fact

PNAM is an INFO→INFO back-link that forces an intra-topic SEQUENCE. It is **empty across effectively all
vanilla content**: a census found 2,757 / 2,757 multi-INFO `Skyrim.esm` topics have an empty mid-chain
PNAM. Vanilla orders the lines within a topic by the `Responses` list + their `Conditions`, never by a
PNAM chain.

PNAM is only meaningful when an **author deliberately** chains forced lines (houseCARL's create path can
set it via a sibling `@editorid` FormLink). Consequences for authoring and auditing:

- **Absence is the universal norm.** Never treat a missing or non-chained PNAM as a defect, and never
  "complete the chain" by adding PNAMs a topic was never meant to have. `housecarl_validate_dialogue`
  deliberately does not flag an empty PNAM for this reason.
- **Only a SET-but-unresolvable PNAM is a real (dangling) defect** — a previous-link pointing at an INFO
  that doesn't exist. That is what the validator flags.
- If you want lines to play in a fixed forced order, the levers are the `Responses` list order and the
  `Conditions` — reach for PNAM only for a genuine forced sequence, and expect to set it yourself.

## Resolution model — "DIAL wins wholesale"

Overriding an INFO **pulls its parent DIAL in automatically** (the DIAL must be present first; an INFO
cannot stand alone), so an INFO and its DIAL always travel together. The consequence: **the winning DIAL's
`Responses` is the authoritative in-game INFO set.** A line that another plugin adds, but that the winning
topic override does not re-list, is **dropped in game** — this is exactly the classic "two mods touch one
topic and lines disappear" dialogue conflict.

For authoring this means: when you override an existing topic to add a line, the override must carry **every
line the topic should still have**, not just your new one. `housecarl_validate_dialogue` validates the
*winning* topic's `Responses` (what actually plays) and prints a standing warning about this drop, because
a line silently missing from the winner is invisible to a record-only check.
