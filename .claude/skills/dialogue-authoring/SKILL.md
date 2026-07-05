---
name: dialogue-authoring
description: Author or interpret Skyrim dialogue at the data layer via houseCARL — create dialogue topics (DIAL) and lines (INFO) in a new plugin, wire them to a branch and quest, attach result (TIF) scripts, write the start-game-enabled-quest .seq, check voiced lines for their .fuz, and validate a whole topic's or quest's dialogue graph. Use when the user wants to add or write dialogue, add a line to a topic, create an NPC greeting or conversation, author a dialogue branch or quest dialogue, wire dialogue to a quest stage, attach a result script to a line, make a start-game-enabled quest's dialogue actually start, fix dialogue that never plays / never fires / went silent, or resolve a dropped-line dialogue conflict — or asks what a dialogue topic does, why a line won't fire, or to audit a mod's dialogue. This authors the dialogue RECORDS themselves — distributing forms to NPCs is SPID, keywords to items is KID, editing a record's own fields is SkyPatcher. Load this before composing or judging any DIAL/INFO — Skyrim dialogue plays nothing unless five Creation-Kit bookkeeping jobs a byte-valid insert skips are done, and the rules are counter-intuitive (PNAM is ~unused; conditions, not list order, pick the line; the winning topic silently drops any line it doesn't re-list).
---

# Dialogue Authoring

## Overview

Authoring dialogue means creating `DialogTopic` (DIAL) and `DialogResponses` (INFO) records — by default in
a new plugin (originals untouched), or, with the write tools' in-place lane, straight into an existing
plugin — and doing the bookkeeping that makes them actually play. The load-bearing
truth: **a byte-valid INFO that passes xEdit but skips the Creation Kit's bookkeeping plays nothing in
game** — the exact silent-failure class houseCARL refuses (Q3). This skill drives houseCARL's tools through
the five jobs and then validates the result; the dialogue *policy* lives here, the mechanism lives in the
tools.

**What houseCARL CAN do:**
- Create topics and lines, including a topic AND all its lines in ONE call with sibling cross-links
  (`housecarl_bulk_create`), or one nested line under an existing topic (`housecarl_create_record`).
- Compose a line's contents — conditions, spoken responses, prompt, speaker, `LinkTo` chain, the result-
  script `VirtualMachineAdapter` — all as ordinary field writes (confirm field paths with
  `mutagen-reference`).
- Compile a result (TIF) script `.psc` → `.pex` (`housecarl_compile_script`).
- Write the start-game-enabled-quest `.seq` (`housecarl_write_seq`).
- Check each voiced line for its `.fuz` on disk and report a **WILL BE SILENT** line (built into create;
  re-checked by `housecarl_validate_dialogue`).
- Validate a topic's or quest's whole dialogue graph and read the load-order-winning records
  (`housecarl_validate_dialogue`, `housecarl_read_record`, `housecarl_cross_plugin_query`).

**What houseCARL CANNOT do — say it, never paper over it:**
- **Evaluate `Conditions` (CTDA).** Conditions decide *when* a line fires. houseCARL **can** statically catch a
  meaningful subset of **malformed** conditions — `housecarl_validate_dialogue` flags a dangling form reference,
  a dead quest-alias index, an unset Run On reference, and a `GetIsID` pointed at a placed instance (it recognizes
  the engine-implicit `PlayerRef` `000014`/`Player` `000007` forms, so a standard player-state gate — `HasSpell` or
  an actor-value check Run On `PlayerRef` — validates clean, not false-flagged as missing) — and it can
  read a condition back and **decode** it (the function, its parameters, its Run On scope —
  [`references/condition-functions.md`](references/condition-functions.md)) so you can check it by eye. What it
  **cannot** do is **evaluate** whether a *well-formed* condition passes — only the running game can. So a
  well-formed but *wrong* condition (the wrong stage number, the wrong Run On for the intent) still silently
  stops a line forever — that remains the single most common silent-dead-dialogue cause and on the author to get
  right.
- **Record voice audio or verify lip-sync.** Voice presence is an on-disk file check; the audio content and
  voice *acting* are out of scope.
- **Promise "this will play" from a clean structural pass.** A green validate means the wiring resolves —
  not that the conditions are correct, the audio exists, or the conversation reads well.

The Skyrim-specific knowledge lives in the `references/` files — read the one your task touches:
- [`references/dialogue-flow-model.md`](references/dialogue-flow-model.md) — how DLVW/DLBR/DIAL/INFO connect
  and what drives the flow (conditions, LinkTo, quest stage, PNAM, DIAL-wins-wholesale). **Read this before
  authoring or auditing anything.**
- [`references/condition-functions.md`](references/condition-functions.md) — how to **decode** a condition
  (CTDA): function + params + Run On (Subject vs Target), the operators and the `OR` flag, a curated table of
  the dialogue/quest condition functions, and CTDA-vs-Papyrus-only. Read before reading or composing any
  `Conditions`.
- [`references/dialogue-branch.md`](references/dialogue-branch.md) — the DLBR entry point: its fields and the
  `TopLevel`/`Blocking`/`Exclusive` flags (one of which can silently lock an NPC out of all dialogue).
- [`references/quest-objectives-tab.md`](references/quest-objectives-tab.md) — stages vs objectives vs log
  entries, and why a line gates on the stage while the journal is driven by objectives.
- [`references/seq-file-format.md`](references/seq-file-format.md) — why a start-game-enabled quest needs a
  `.seq` and what the file is.
- [`references/voice-file-naming.md`](references/voice-file-naming.md) — the `.fuz`/`.lip` path template and
  the override folder trap.

The condition / branch / quest references are **hand-curated** (sourced from the CK wiki + Mutagen's record
model, not the by-construction generator), so they carry a staleness duty — provenance and the re-check
checklist live in `references/_CORPUS_STATUS.md` (dev-side; not shipped in the plugin).

## Read the flow model first

Before composing or judging a line, internalise four counter-intuitive facts from the flow model — getting
any of them wrong produces dialogue that looks right and plays wrong:

- **Conditions pick the line, not list order and not PNAM.** A line fires when its `Conditions` pass
  (usually `GetStage` + a speaker check). Ordering is mostly the `Responses` list + conditions.
- **`PreviousDialog` (PNAM) is ~unused.** It is empty across effectively all vanilla content. Set it ONLY
  to force a deliberate intra-topic sequence; never "complete a chain", and never read a missing PNAM as a
  bug. Only a SET-but-dangling PNAM is a defect.
- **`LinkTo` is the real conversation chain** (topic → next topic), not PNAM.
- **DIAL wins wholesale.** When you override an existing topic, the winning topic's `Responses` is the whole
  in-game line set — any line you don't re-list is dropped. See the editing section below.

## Player-topic semantics — where each piece of text goes

A player-choice topic can be byte-perfect — every subrecord present, `housecarl_validate_dialogue` green —
and still play absurdly, because these rules govern *what the player sees and hears*, which no data-layer
check can evaluate. They cost a shipped dialogue mod a full rework; internalise them before composing a
conversation, then reason each line through by hand.

- **In a player topic, the two text fields swap speakers.** `INFO.Prompt` is the **player's** menu line
  (the button they click); `INFO.Responses` is the **NPC's** reply to it — *not* player-spoken text. Put the
  prompt text into `Responses` and the NPC parrots the player's own words back.
- **`LinkTo` sets which player options appear NEXT, not what the NPC says.** The linked topic's `Name`
  becomes the next menu button. Wiring an NPC reply as a `LinkTo` target produces a clickable option
  literally labelled with that topic's name ("Wench reply"). The NPC's reply belongs in the current INFO's
  `Responses` (one INFO can hold several `DialogResponse` rows — that *is* the multi-line-speech idiom);
  `LinkTo` is only for the *player's* follow-up choices.
- **A conversation ender needs `Flags.Flags = Goodbye` on the INFO.** Without it the menu reopens after
  every reply and the player can never leave the exchange. The create tools materialize the `Flags` struct
  for CK-parity (job 1c) but leave `Goodbye` **unset** — it's an authoring semantic, not a default; set it
  on the INFO that ends the exchange.
- **Keep an INFO's `Conditions` mutually exclusive across the topic, so intra-topic order never matters.**
  Only the first condition-passing INFO in a topic plays; when two INFOs can both pass, which one wins falls
  back to file group order — a fragile thing to lean on. Exclusive conditions make the outcome
  order-independent, and it's *why* pure houseCARL output needs no CK ordering pass (see "Do you need to
  open the Creation Kit?" below).

**Branching an NPC's reply on a single click.** A single player click evaluates every candidate INFO's
`Conditions` *once*, when the option is chosen — so an INFO's result-script fragment cannot roll a random or
branching outcome and *then* have sibling INFOs condition on it (their conditions were already tested). The
clean pattern, proven in a shipped mod: **pre-roll the outcome before the click.** Register the deciding
actor's script for the `Dialogue Menu` open (`RegisterForMenu("Dialogue Menu")`), write the result into
globals as the menu opens, then let one topic hold every outcome INFO, condition-disambiguated on those
globals — each a complete exchange. The click just selects the INFO the pre-roll already satisfied.

## The five jobs a silent INFO insert skips

| # | Job | How | Gotcha |
|---|-----|-----|--------|
| 1 | Wire topic ↔ branch ↔ quest | set `DialogTopic.Quest`/`Subtype`/`Category`; author a `DialogBranch` whose `StartingTopic` points at the topic (its entry point) | a `Custom` topic with no inbound branch or `LinkTo` is byte-valid but **never entered**; `Subtype`/`Category` enum values via `mutagen-reference` |
| 1b | SNAM subtype marker | just set `Subtype` — `housecarl_bulk_create`/`housecarl_create_record` **auto-fill** the `SubtypeName` (SNAM) marker from it (Custom→`CUST`, Hello→`HELO`, Goodbye→`GBYE`…) and report it | the game buckets topics by this 4-char marker, so a **new** topic with a `Subtype` but a **blank** marker (0000) is a **load CTD**; only set `SubtypeName` by hand to override, or when editing a topic outside the create path (then `housecarl_validate_dialogue` flags a blank one) |
| 1c | CK-parity subrecords | the create tools **auto-fill** the nullable subrecords the CK always writes but Mutagen omits, and report each — **crash-fixers:** INFO `FavorLevel` (CNAM)→`None` + `Flags` (ENAM)→empty, DLVW `DNAM`→`00` / `ENAM`→`00000000`; **byte-parity:** DLBR `Category` (TNAM)→`Player`, DIAL `Priority` (PNAM)→`50`, QUST `NextAliasID` (ANAM) + each objective's `Flags` (FNAM)→`0` | an INFO with no CNAM/ENAM **crashes the Creation Kit** when its topic is opened (the game tolerates it); a bare DLVW crashes the CK **Dialogue Views** editor. All are **non-override** — an explicit value always wins (incl. `Priority=0`, or `Category=Command` for a bribe/intimidate branch). `Branch` (BNAM) can't be auto-derived, so it stays yours — set it to the owning `DialogBranch`; `housecarl_validate_dialogue` **warns** a `Custom` topic with no BNAM (the same CK-views crash). `Goodbye` enders still need `Flags.Flags=Goodbye` set by hand |
| 2 | Order / chain the lines | `Responses` list order + `Conditions`; `LinkTo` for topic→topic | set `PreviousDialog` ONLY for a real forced sequence |
| 3 | Result (TIF) scripts | compose the line's `VirtualMachineAdapter` binding, then `housecarl_compile_script` | create-time teeth check the binding + `.pex` presence |
| 4 | SEQ for start-game-enabled quests | set the quest's Start-Game-Enabled flag, then `housecarl_write_seq` | ticking the flag alone does nothing |
| 5 | Voice | provide the `.fuz`/`.lip`; heed the WILL BE SILENT note | the folder is the **defining** plugin, not the conflict winner |

Jobs 1–2 are ordinary field writes you supply; the engine never guesses the right quest or condition for
you. Jobs 3–5 compose existing houseCARL tools. The orchestration — which jobs apply, in what order — is
this skill's job.

## Workflow — author a conversation

1. **Resolve the targets.** Identify the speaker (an NPC or a quest alias — its voice type matters for the
   voice check), the quest the dialogue is gated on, and whether you need a new branch or are attaching to
   an existing topic. Read existing records with `housecarl_read_record` / `housecarl_cross_plugin_query`
   and confirm every field path with `mutagen-reference` before composing.

2. **Author the topic and its lines in one call** with `housecarl_bulk_create` — declare the `DialogTopic`
   first, then each `DialogResponses` with `parent` naming the topic's editorid (that nests the line into
   the topic's `Responses`, so a line cannot stand alone). A worked example — a player-choice topic with two
   lines, the second chaining on to another topic:

   ```json
   records=[
     { "record_type": "DialogTopic", "editorid": "MyMod_AskRing",
       "operations": [
         { "field_path": "Quest",   "value": "001A2B:MyMod.esp" },
         { "field_path": "Subtype", "value": "Custom" },
         { "field_path": "Name",    "value": "Tell me about the ring." } ] },

     { "record_type": "DialogResponses", "editorid": "MyMod_AskRing_L1", "parent": "MyMod_AskRing",
       "operations": [
         { "field_path": "Prompt",    "value": "Tell me about the ring." },
         { "field_path": "Speaker",   "value": "0008F2:MyMod.esp" },
         { "field_path": "Responses", "verb": "Add",
           "compose": { "type": "DialogResponse",
                        "fields": { "Text": "It is older than this city.", "ResponseNumber": "1" } } } ] },

     { "record_type": "DialogResponses", "editorid": "MyMod_AskRing_L2", "parent": "MyMod_AskRing",
       "operations": [
         { "field_path": "Speaker",   "value": "0008F2:MyMod.esp" },
         { "field_path": "Responses", "verb": "Add",
           "compose": { "type": "DialogResponse",
                        "fields": { "Text": "Take it, and be careful.", "ResponseNumber": "1" } } },
         { "field_path": "LinkTo",    "verb": "Add", "value": "00C3D4:MyMod.esp" } ] }
   ]
   ```

   Three points about the shape, each checked against the write surface:
   - **`parent`** nests a line into its topic's `Responses` — how the one-shot says "this line belongs to
     that topic."
   - **A spoken row is a composed struct:** `verb:"Add"` with `compose:{ "type":"DialogResponse", "fields":{…} }`.
     The `type` is required and the values sit under `fields` as **strings** (coerced server-side), so
     `"ResponseNumber":"1"`, not `1`. (`DialogResponse`, singular, is the spoken-row struct; `DialogResponses`
     is the INFO record.)
   - **Same-call links use `@editorid`.** A record created in the same call has no FormKey yet, so reference it
     as `@editorid` — valid **only** as a `Set` value on a **singular** FormLink (`Topic`, `PreviousDialog`,
     `Branch`). E.g. to force L2 after L1 (the rare deliberate-sequence case — see the flow model) add
     `{ "field_path":"PreviousDialog", "value":"@MyMod_AskRing_L1" }` to L2. A **list** FormLink like `LinkTo`,
     and any existing or external record, takes a `XXXXXX:Plugin.esp` FormID instead — as the cross-topic
     `LinkTo` above does.

   The call is **all-or-nothing**: if any spec is malformed, nothing is written.

   **Reachability — this example is not yet enterable.** `MyMod_AskRing` is a `Custom` topic with no entry
   point, so as written it is byte-valid but the game never reaches it (only generic subtypes like Hello /
   Goodbye are matched without one — see the flow model). A new player-choice menu needs a `DialogBranch`
   (DLBR) whose `StartingTopic` points at the topic; author it in the **same** call, declared *after* the
   topic, with `StartingTopic` set to `@MyMod_AskRing`. The reverse `DialogTopic.Branch → DLBR` back-link
   can't be set in the same call (`@editorid` resolves only *earlier* siblings) — set it with a follow-up
   `into=` edit if you want it, though `Branch` is usually left unset. (Adding a line to an *existing* topic
   needs none of this — its entry point already exists.)

3. **Author the conditions deliberately** — the validator can check them for *malformedness* but never
   *evaluate* whether a well-formed one passes. A line with no
   conditions fires whenever its topic is reached; gate it with `GetStage` (quest progress) and a speaker
   check (`GetIsID`/alias) as the flow model describes. Compose each `Condition` per `mutagen-reference`
   (it is a polymorphic list); [`references/condition-functions.md`](references/condition-functions.md) has
   the function param shapes and the Run On (Subject vs Target) scoping. Wrong conditions are the #1
   silent-dead-dialogue cause — there is no tool that will catch them, so reason them through.

4. **Result scripts, if the line does something.** Compose the line's `VirtualMachineAdapter` script binding
   (the `TIF_`-style fragment), author the `.psc`, and compile it with `housecarl_compile_script` (never
   hand-roll `PapyrusCompiler.exe` — the tool sets the import paths and quotes spaced paths). The create-
   time check flags a line whose result script isn't bound + compiled (**WILL NOT FIRE**). Use
   `papyrus-reference` for function signatures.

5. **Voice.** For each voiced line, houseCARL computes the expected `.fuz` path and reports **WILL BE
   SILENT** if it's absent. Provide the audio yourself (acting is out of scope). On an override, remember
   the folder is the INFO's *defining* plugin, not the winner — see the voice reference.

6. **SEQ, if the quest starts at game start.** Set the quest's Start-Game-Enabled flag, then run
   `housecarl_write_seq` against the plugin. Without it the quest — and all its dialogue — silently never
   starts. (A plugin with no such quests needs no `.seq`; the tool reports that.) If a later **in-place** edit
   or removal prunes a master, the on-disk FormIDs shift and the existing `.seq` goes stale — houseCARL flags
   this in the write's read-back note so you re-run `housecarl_write_seq` (it flags, never silently rewrites).

7. **Validate, then verify.** Run `housecarl_validate_dialogue` on the topic (a DIAL FormID) or the whole
   quest (a QUST FormID). It checks what it can — quest/branch wiring, `LinkTo` and PNAM resolve, voice
   present, scripts bound, and every `<Global=X>` text-replacement tag backed by a global in the quest's
   `TextDisplayGlobals` (an unbacked tag renders as `[...]` in game — a silent failure it now warns on) — and
   **prints a standing-limits footer for what it cannot** (the CTDA conditions,
   lip-sync, and the dropped-line caveat). Treat the footer as real: a clean pass is not "this will play."
   Read the new records back (`full_readback` on the create call) before telling the user to enable + sort
   the patch in MO2.

## Do you need to open the Creation Kit? No.

A dialogue plugin authored entirely through houseCARL plays without ever opening the CK — measured, not
asserted. A byte-diff of pure-houseCARL output against the *same* plugin after a CK open+save came to **+90
bytes = 9 INFO `PNAM` subrecords and nothing else** — the CK's intra-topic order bookkeeping. No TNAM,
TIFC, SNAM/BNAM/DNAM/ENAM/CNAM, or re-layout changed: houseCARL's CK-parity auto-fills (job 1c) already
wrote every subrecord the CK would. The reference mod ran from pure houseCARL output across multiple
in-game sessions *before* any CK save existed.

The one thing a CK save adds — the PNAM chains — only matters for a topic that depends on **first-valid-wins
ordering among overlapping conditions**, and the fix is at authoring time, not in the CK: keep the topic's
INFO `Conditions` mutually exclusive (above), or set `PreviousDialog` (PNAM) chains yourself —
houseCARL's `@editorid` sibling links set a forced intra-topic sequence in one call. So the CK is never
*required*; reach for it only if you specifically want the editor's flowchart view, knowing houseCARL has
already written the bytes.

## Editing an existing topic — the dropped-line trap

Because DIAL wins wholesale, overriding a vanilla or modded topic to add one line means your override
becomes the authoritative line set — and any line the original topic had that you don't re-list is **dropped
in game**. So when extending an existing topic, carry forward every line it should still have, not just your
new one. `housecarl_validate_dialogue` validates the *winning* topic's `Responses` and warns about this,
but a record-only glance won't show the loss. This is the classic "two mods touched one topic and lines
vanished" conflict.

**The in-place lane sidesteps this trap.** If the topic lives in a plugin you own (or are willing to edit
directly), the write tools' in-place lane (`target=<plugin>`, `in_place=true`, `acknowledge=`) edits the
original DIAL/INFO records instead of authoring an override — so there is no override line-set to keep
complete and no line can be dropped. The override lane above (the default, originals untouched) is still the
right choice for patching a *third-party* plugin you don't want to rewrite — there you must carry forward
every line the topic should keep.

## Write-side recipes — clone a condition gate, write a CK-refused subtype

Three repeatedly-needed edits to *existing* dialogue ride the write tools you already have —
`housecarl_bulk_apply` and `housecarl_set_field` — each with one sharp edge worth stating once.

**Recipe A — clone a verified condition gate onto N empty Infos. NEVER hand-synthesize the operator bytes.**
A `Condition` (CTDA) is a polymorphic struct — a `ConditionFloat` carrying a `CompareOperator`, a
`ComparisonValue`, and a polymorphic `Data` (the function + its params). *Computing* that encoded
operator/comparison by hand is exactly what once wrote 26 broken conditions onto one gate. So don't — **read
a known-good gate back and replay its rows verbatim**:

1. Build the gate once (in CK, or on one Info you've validated) and read it back with `housecarl_read_record`
   (`Conditions`, deep). That array is your source of truth — every field below is **copied, nothing computed**.
2. For each target Info, **read it first and skip any that already carry `Conditions`** — there is no
   idempotent verb, so the read-then-skip is yours to do, and it is what makes a re-run safe.
3. Replay each source row as a composed `Add` into the target's `Conditions`. One `Add` per row; the
   polymorphic element composes by its concrete arm, with `Data` composed by *its* arm:

   ```json
   operations=[
     { "formid": "0A12C4:MyMod.esp", "field_path": "Conditions", "verb": "Add",
       "compose": { "type": "ConditionFloat",
                    "fields": { "CompareOperator": "GreaterThanOrEqualTo", "ComparisonValue": "20" },
                    "sets": [ { "path": "Data",
                                "compose": { "type": "GetStageConditionData",
                                             "fields": { "Quest": "001234:MyMod.esp", "RunOnType": "Subject" } } } ] }
     }
     // ...one more Add per source row — arm type, CompareOperator, ComparisonValue, the Data arm + its
     //    params copied verbatim from the read-back; confirm arm/field names via mutagen-reference...
   ]
   ```

   Pass `full_readback=true` and confirm the written rows match the source before enabling the patch.
   (Conditions-only edits do **not** need a `.seq` regen.)

**Recipe B — write an INFO subtype CK's dropdown refuses to offer.** CK's player-dialogue subtype dropdown
only lists subtypes already present in the branch, so you cannot pick e.g. `ForceGreet` there. The subtype
lives on the **topic, not the line** — it is `DialogTopic.Subtype` (the DIAL); `DialogResponses` (the INFO)
has no `Subtype` field. Copy the exact value from a known-good ForceGreet topic and write it with
`housecarl_set_field`:

   ```json
   housecarl_set_field( formid="0B77E0:MyMod.esp", field_path="Subtype", value="ForceGreet" )
   ```

   (`ForceGreet` is the Mutagen spelling of xEdit's `PFGT` subtype — confirm the enum value in
   `mutagen-reference`.) The write is non-destructive: it lands in a reviewable patch; read it back before
   enabling + sorting in MO2.

**Recipe C — un-bind a result-script fragment from an INFO.** Clearing a fragment binding is a supported
`Remove` now — no `remove_record` + recreate. `Remove` the whole result-script adapter:

   ```json
   housecarl_set_field( formid="0A12C4:MyMod.esp", field_path="VirtualMachineAdapter", verb="Remove" )
   ```

   That nulls the entire `VirtualMachineAdapter` (all scripts + fragments) on the INFO. To drop only the
   fragment binding while keeping any attached scripts, `Remove` the fragment field itself
   (`field_path="VirtualMachineAdapter.ScriptFragments"`). Both are nullable-field clears the write engine
   allows by construction (the adapter is a nullable substruct, `ScriptFragments` a nullable polymorphic
   field); an explicit non-nullable/required field would refuse a `Remove` instead. Read back to confirm the
   binding is gone before enabling the patch.

## Common mistakes

- **Building a PNAM chain, or flagging a missing one.** Vanilla topics have empty PNAM; ordering is the
  `Responses` list + conditions. Set `PreviousDialog` only for a genuine forced sequence; never add one to
  "fix" a topic, and never report its absence as a defect.
- **Forgetting the SEQ.** A Start-Game-Enabled quest with no `.seq` never starts, and neither does its
  dialogue. Ticking the flag is half the job — write the `.seq`.
- **Reading a clean validate as "it'll play."** A green validate catches *malformed* conditions but never
  proves a *well-formed* one is *correct* — a wrong `GetStage` value passes validation and is silent in game.
  Always carry the standing-limits footer to the user.
- **Computing the voice folder from the conflict winner.** It is the plugin that *defines* the INFO. For a
  new plugin that's yours (clean); for an override it's the original's folder, where the audio lives.
- **Putting the player's line in `Responses`, or an NPC reply behind `LinkTo`.** In a player topic `Prompt`
  is the player's menu button and `Responses` is the NPC's reply; `LinkTo` sets the *next player options*,
  not what the NPC says. Getting this backwards is byte-valid and plays absurdly — see the player-topic
  semantics section.
- **Overriding a topic and dropping its other lines** (the DIAL-wins-wholesale trap above).
- **Hand-synthesizing CTDA operator/comparison bytes** instead of cloning a verified `Conditions` array
  verbatim — computing the encoded operator once wrote 26 broken conditions. Read a good gate back with
  `housecarl_read_record` and replay its rows (the write-side recipe above).
- **Hand-rolling the Papyrus compile** instead of `housecarl_compile_script` — hand-rolled calls mangle
  spaced paths and can hit originals; the tool quotes them and lands a reviewable `.pex`.
- **Reaching for this skill when the user means distribution or a field edit.** Distributing a form to NPCs
  is `spid-authoring`; a keyword onto items is `kid-authoring`; editing a record's own fields is
  `skypatcher-authoring`. This skill authors the dialogue records.

## Notes

- **Field names and enums via `mutagen-reference`.** `DialogTopic.Subtype`/`Category` are enums and
  `Conditions`/`Responses` are composed lists — confirm spellings and legal values there, don't guess.
- **Quest scaffolding rides along.** A flat `QUST` and its stages/aliases/objectives are createable today
  with `housecarl_create_record`/`housecarl_bulk_create`; this skill is the dialogue layer that wires onto
  it. Set the quest up first, then author the topics that reference it.
- **Result-script review.** For the TIF fragment's Papyrus, `papyrus-reference` has the signatures and
  `papyrus-optimization` grades the script — a result script that stack-dumps is its own silent failure.
- **Out of lane.** Exterior-cell-keyed placement and runtime-spawned (`FFxxxxxx`) speakers are separate
  capabilities, not dialogue authoring — name the limit rather than guessing a path.
