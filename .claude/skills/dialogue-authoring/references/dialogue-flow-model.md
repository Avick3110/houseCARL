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
2. **Which line fires within a topic:** the INFO's **`Conditions`** decide which lines are *eligible* —
   primarily `GetStage` (quest progress), `GetIsID` / alias checks (who is speaking), etc. (Real example:
   a DA03 line gated by `GetStage(DA03) ∈ [100,155)` AND `GetIsID(Barbas)`.) **Then ORDER settles it:** the
   game walks the topic top to bottom and plays the **first** eligible line, so when two lines both pass,
   position decides — not specificity. Within one plugin that order is its `Responses` list; across plugins
   it is the merge described under *Resolution model* below. This is why a line can stop playing without any
   field of it changing.
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
- If you want lines to play in a fixed forced order **within one plugin**, the levers are the `Responses`
  list order and the `Conditions` — reach for PNAM only for a genuine forced sequence.
- **The one case where PNAM is not optional: RE-LISTING an existing line.** Everything above describes
  authoring a topic in a single plugin. The moment you override a line another plugin already carries, its
  PNAM becomes the thing that holds its position — without one it is appended to the bottom of the merged
  topic (see *Resolution model* below). "Vanilla leaves PNAM empty" stays true and is still not a defect;
  it just means a vanilla line you override carries no anchor, and you must supply it.

## Resolution model — lines MERGE; what a conflict changes is ORDER

Overriding an INFO **pulls its parent DIAL in automatically** (the DIAL must be present first; an INFO
cannot stand alone), so an INFO and its DIAL always travel together.

What that does **not** mean is that the winning DIAL's `Responses` replaces the topic's line set. It does
not. Every plugin that touches a topic contributes its own child list and the game **merges** them: a line
another plugin adds, and the winning override does not re-list, **still plays**. (Measured on a live load
order: a topic whose winning record lists *one* line plays *eight*.)

What actually changes is **order** — and order decides which line answers, because the game walks the topic
top to bottom and plays the **first** line whose `Conditions` pass. The merge rule:

- lines are placed per plugin, in load order;
- **re-listing a line MOVES it** — it is evicted from where it was and **appended to the bottom**, unless
  that plugin also carries the line's `PreviousDialog` (PNAM), which puts it back after its predecessor;
- so **the last plugin to list a line owns that line's position.**

For authoring this means the **opposite** of the advice this section used to give. Do **not** "carry forward
every line" into your override: re-listing lines you didn't change appends them to the bottom in your order,
which *is* the reordering bug. List only the lines you actually add or change.

And mind the asymmetry between those two: **adding** a line moves nothing, but **changing** an existing line
is itself a re-list (an override must sit in your plugin's copy of the topic), so it moves that line to the
bottom unless you carry its PNAM. When you edit an existing line, set its `PreviousDialog` to the line that
preceded it — vanilla carries none, so there is nothing to inherit. Omitting a line no longer removes it: to
stop one playing, mark it deleted in your override or condition it out.

`housecarl_validate_dialogue` prints the effective merged order for a topic and flags any line whose
position moved, naming the plugin that moved it.

> Corrected 2026-07-27 (#275). This section previously stated the "DIAL wins wholesale" model — that a line
> the winning topic doesn't re-list is dropped in game. That is false, and the "carry forward every line"
> advice that followed from it causes the very conflict it was meant to prevent.
