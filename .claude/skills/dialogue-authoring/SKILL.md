---
name: dialogue-authoring
description: >-
  Authors and interprets Skyrim dialogue records through houseCARL — DIAL topics, INFO lines,
  dialogue branches, quest wiring, TIF result scripts, the .seq file, .fuz voice checks and
  dialogue-graph validation. Use when adding, writing or auditing dialogue, or when a line never
  plays or never fires. Not for distributing a form to NPCs (SPID), a keyword to items (KID), or
  editing a record's own fields (SkyPatcher). Load before composing or judging any DIAL or INFO —
  a byte-valid insert that skips the Creation Kit's bookkeeping plays nothing in game.
compatibility: Requires the houseCARL MCP server and a configured Mod Organizer 2 instance.
---

# Dialogue Authoring

## Overview

This skill owns Skyrim's dialogue records: `DialogTopic` (DIAL), `DialogResponses` (INFO), the
`DialogBranch` that lets a topic be entered, the result script a line runs, its voice file, and the
`.seq` that makes a start-game-enabled quest exist at all. The load-bearing truth is that **a
byte-valid INFO that passes xEdit but skips the Creation Kit's bookkeeping plays nothing in game** —
so the job is never "write the record", it is "write the record and the five things around it".

Distribution is somebody else's lane: a form onto NPCs is `housecarl:spid-authoring`, a keyword onto
items is `housecarl:kid-authoring`, a record's own fields with no ESP is `housecarl:skypatcher-authoring`.
(On a Codex install those siblings appear under their bare folder names — `spid-authoring`, and so on.)

## Adding a line to an existing NPC — the default route: your own quest

For a new line on an NPC who already has lines, **do not append into the vanilla topic.** Author your
own start-game-enabled `QUST` at a priority above the incumbent dialogue quest's, give it its own DIAL
topic of the same subtype, hang your INFO under that, and write the `.seq`. No vanilla record is
touched, so nothing already in the vanilla topic is re-listed or reordered. Be clear about what that
does not buy: the higher quest priority is what gets your topic reached at all — **which topic the
game enters is decided across quests by priority**, before intra-topic order is even consulted — so
whenever your line's gate passes, the engine enters *your* topic and the vanilla greetings do not
play for that activation. That is preemption, not damage: the vanilla records are untouched and the
old lines return the moment your gate fails. Gate the line so it passes only when you mean it to; a
gate that passes on every activation replaces the NPC's whole greeting pool. The append route puts your line at the bottom of a topic that is
often ten plugins deep, where whether it is ever selected is not knowable from the data layer.

Three cheap reads settle the design.

1. **Find a real line of the kind you are writing.** `references=` is how you find an NPC's lines:
   `housecarl_records(types=["INFO"], references=["<npc formid>"], project={"form":"fields",
   "fields":["*parent.EditorID","*parent.SubtypeName","*parent.Quest","Responses[0].Text"]})`.
   A `where=["Speaker->editorid = …"]` scan is a dead end — vanilla greetings carry a **null**
   `Speaker`, and a null link reads back as unreadable, not as a match.
2. **Read that exemplar whole** (`project={"form":"everything"}`) and take its field set: which
   conditions gate it, and the spoken row's `Emotion`, `EmotionValue` and `Flags`. Carry all three
   onto your row — a response left at `EmotionValue = 0` and `Flags = 0` diverges from every vanilla
   line around it.
3. **Read the incumbent quest** — the `*parent.Quest` from step 1 — and take its `Priority`. Yours
   goes comfortably above it. Vanilla `DialogueWhiterun` is `Priority = 30`, so 65 clears it. Do not
   guess the number; measure it.

**Skyrim's activation greeting is the `HELO` subtype** — `SubtypeName = HELO`, written as `Subtype = "Hello"`.
There is no `GREE` subtype; do not go looking for one. **A DIAL's `SNAM` marker (`SubtypeName`) is
authoritative for its subtype, and the numeric `Subtype` enum can disagree with it — and the copy to
distrust is the master's own** (issue #660). Records authored before the Dragonborn CK, which inserted
enum members, decode six positions off: `02707A` read straight out of `Skyrim.esm` prints
`Subtype = RechargeExit` while its later winner prints `Subtype = Hello`, for one unchanged `HELO`
marker (`0904AC` prints `Recharge` for `GBYE` the same way). Steps 1-2 above send you into `Skyrim.esm`,
which is exactly the read that prints the wrong name — read `SubtypeName`, not `Subtype`.

Then one create call. The quest, topic and line are three records in one all-or-nothing write, linked
by `@<editorid>` same-call sibling references and by `parent`:

```json
housecarl_create(
  patch="MyGreeting", readback=true,
  records=[
    { "record_type": "Quest", "editorid": "MyMod_BelethorGreetQuest",
      "ops": [ { "field_path": "Name",     "value": "My Belethor Greeting" },
               { "field_path": "Flags",    "value": "StartGameEnabled" },
               { "field_path": "Priority", "value": "65" } ] },

    { "record_type": "DialogTopic", "editorid": "MyMod_BelethorGreetTopic",
      "ops": [ { "field_path": "Quest",    "value": "@MyMod_BelethorGreetQuest" },
               { "field_path": "Subtype",  "value": "Hello" },
               { "field_path": "Priority", "value": "50" },
               { "field_path": "Category", "value": "Misc" } ] },

    { "record_type": "DialogResponses", "editorid": "MyMod_BelethorGreetInfo",
      "parent": "MyMod_BelethorGreetTopic",
      "ops": [ { "field_path": "Speaker",   "value": "013BA1:Skyrim.esm" },
               { "field_path": "Responses", "op": "Add",
                 "compose": { "type": "DialogResponse",
                              "fields": { "Text": "Browse as you like.", "ResponseNumber": "1",
                                          "Emotion": "Neutral", "EmotionValue": "50",
                                          "Flags": "UseEmotionAnimation" } } } ] }
  ])
```

`Speaker` goes in that create call, not after it: your new quest has no aliases for the voice type to
come from at runtime, and voice and result-script coverage are checked on create, so setting it later
does not backfill the `.fuz` path.

Then **clone the gate** onto the new line from the vanilla exemplar you read in step 2 — never
hand-synthesize the operator bytes (Recipe A in `references/write-side-recipes.md`). One call:

```json
housecarl_apply(into="<the filename the create call reported>", readback=true,
                bundle=["Conditions"],
                assignments=[{ "target": "<the new INFO>", "from": "02848F:Skyrim.esm" }])
```

Copy only the entries that belong: a vanilla greeting's gate is often a speaker check *plus* a cell
check, and a line meant to play anywhere wants the speaker check alone.

Then the `.seq`, which is not optional:
`housecarl_write_seq(source="<the filename the create call reported>")` — the reported filename, never
the stem you passed, or the `.seq` lands on an older plugin of that name and your quest gets none. A
start-game-enabled quest with no `.seq` never starts, and its dialogue never exists. Finish with the
pre-enable sweep below, and note the `#615` limit under "Validate, then hand off" — the dialogue check
cannot see a plugin that is not yet enabled.

## The exception — appending to an existing vanilla topic

Take this route only when the line genuinely belongs *inside* an existing topic: a reply under a
player-choice topic you or the mod already own, a line in a topic that is yours or nearly
uncontested, or an edit the user has asked for in those terms. It is the cheaper write and the
riskier placement. **Point `parent` at the existing topic's FormID.** It resolves an existing record,
not just a sibling created in the same call, so the topic and its bookkeeping already exist and you
author one INFO — with its conditions cloned from a vanilla sibling INFO in the same topic, and the
sibling's emotion fields carried:

```json
housecarl_create(
  patch="MyGreeting", readback=true,
  records=[{ "record_type": "DialogResponses", "editorid": "MyMod_BelethorHelloNew",
             "parent": "02707A:Skyrim.esm",
             "ops": [
               { "field_path": "Speaker", "value": "013BA1:Skyrim.esm" },
               { "field_path": "Responses", "op": "Add",
                 "compose": { "type": "DialogResponse",
                              "fields": { "Text": "Browse as you like.", "ResponseNumber": "1",
                                          "Emotion": "Neutral", "EmotionValue": "50",
                                          "Flags": "UseEmotionAnimation" } } } ] }])
```

```json
housecarl_apply(into="<the filename the create call reported>", readback=true,
                bundle=["Conditions"],
                assignments=[{ "target": "<the new INFO>", "from": "02848F:Skyrim.esm" }])
```

**The conditions are not optional here.** A line appended to a shared vanilla topic with no gate
fires for every speaker that reaches the topic. Clone them from a sibling INFO in that same topic —
the one you read in step 2 above — and drop only the entries you positively do not want, such as a
cell check on a line meant to play anywhere.

Then sweep the patch before it is enabled — this lane works off-order, the dialogue one does not
(section "Validate, then hand off"). `patch=` is a *base* name, auto-suffixed if that stem is taken,
so sweep the filename the create call reports back, never the stem you passed:

```json
housecarl_check(plugins=["<the filename the create call reported>"], findings=["errors","scripts"])
```

Set **no `PreviousDialog`** and re-list nothing: adding a line moves nothing already in the topic.
Set `Speaker` **in the create call** if you want houseCARL to print the expected `.fuz` path — with
no speaker the voice type comes from the quest alias at runtime and cannot be computed, and setting
it afterwards does not backfill, because voice and result-script coverage are checked on create.

The write reports a **lean host**: the parent topic is hosted from the plugin that *defines* it, and
names whichever plugin currently wins it. That is information, not a defect — the new child is
carried whichever way you sort. Do not "fix" it by forwarding the winning parent in; that drags that
plugin's re-listed INFO children into your patch and re-opens the reorder trap. One call settles
whether it matters at all: `housecarl_records(formids=["<topic>"], source="<winner>.esp",
versus="<definer>.esm", project={"form":"delta"})` — differences confined to `FormVersion` and the
child list mean the lean host costs nothing.

## The five jobs a silent INFO insert skips

| # | Job | How | Gotcha |
|---|-----|-----|--------|
| 1 | Wire topic ↔ branch ↔ quest | set `DialogTopic.Quest` / `Subtype` / `Category`; author a `DialogBranch` whose `StartingTopic` points at the topic | a `Custom` topic with no inbound branch and no inbound `LinkTo` is byte-valid and **never entered** |
| 1b | The SNAM subtype marker | set `Subtype` on the topic and let the create path derive the marker | a new topic with a `Subtype` but a **blank** marker is a **load CTD**; set `SubtypeName` by hand only to override, or when editing a topic outside the create path |
| 1c | CK-parity subrecords | let `housecarl_create` fill them; it reports each field it filled | an INFO with no CNAM/ENAM **crashes the Creation Kit** when its topic is opened (the game tolerates it); a bare DLVW crashes the CK's Dialogue Views editor. `Branch` (BNAM) cannot be derived — set it yourself on a **`Custom`** topic (generic topics carry none, and a missing one there is not a defect), and `Goodbye` enders still need `Flags.Flags = Goodbye` |
| 2 | Order and chain the lines | the `Responses` list order plus each INFO's `Conditions`; `LinkTo` for topic → topic | `PreviousDialog` is for a real forced sequence, or a line you re-list — never a chain across a topic you wrote |
| 3 | Result (TIF) scripts | compose the line's `VirtualMachineAdapter` binding, then `housecarl_compile_script` | the create reports a binding that is unwired or uncompiled |
| 4 | The `.seq` for a start-game-enabled quest | set the quest's flag, then `housecarl_write_seq` | ticking the flag alone does nothing: the quest, and all its dialogue, never starts |
| 5 | Voice | provide the `.fuz` / `.lip` yourself | the folder is the **defining** plugin, never the conflict winner |

Jobs 1–2 are field writes you supply; nothing guesses the right quest or condition for you. Jobs 3–5
compose existing tools. Deciding which apply, and in what order, is this skill's work.

## Order, and the reorder trap

**Lines are not dropped by a dialogue conflict.** Every plugin that touches a topic contributes its
lines and the game merges them, so a line you do not re-list still plays. What a conflict changes is
**order**. "Lines vanished" almost always means a line is still there but now sits behind a broader
one that also passes.

**Where position decides, and where it does not.** In a topic whose lines are disambiguated by their
`Conditions`, the eligible set is what matters and order settles a tie: the game walks the topic and
plays the first line whose conditions pass. That rule does **not** extend to generic greeting
subtypes. Belethor's six vanilla `Hello` lines carry no `Random` flag and demonstrably cycle in game;
houseCARL says the same from the other side, treating an empty previous-link as normal because
vanilla selects among a topic's lines by their conditions rather than a chain, and keeping the merged
order out of its findings entirely. **How the engine picks among eligible generic greetings is not
something this skill or houseCARL can tell you**, and which *topic* is reached at all is decided
across topics by quest priority. So do not tell a user a bottom-appended `Hello` line is starved; say
what is known and what is not.

**The trap is the reverse of how it looks: re-listing a line moves it to the bottom** unless the
re-listing plugin also carries that line's `PreviousDialog` (PNAM). "Carry forward every line to be
safe" reorders the whole topic into your override's order and *causes* the bug. The rule is two
halves, and they are not the same job:

- **Adding a line** — list only your new line. Nothing else moves. Give it a PNAM only if it must sit
  at a particular spot rather than last.
- **Changing an existing line** — an override of an existing INFO *is* a re-list structurally, so
  editing one line's text moves it to the bottom unless you **carry its PNAM**. Treat that as the
  default. Vanilla lines carry none, so there is nothing to inherit and you must supply it — which is
  exactly what a well-behaved patch like USSEP does when it re-lists six lines and moves none.

**Removing a line is not done by omitting it,** and `housecarl_remove` usually is not the tool.
Omission is a no-op. `housecarl_remove` has no default lane — every call names exactly one. With
`into="<your patch>.esp"` it drops *your patch's* override, reverting the line to the underlying
winner, so it plays as before; it genuinely removes an INFO only for a record your own plugin
created, or with `in_place="<plugin>.esp"` plus `acknowledge=true` on the first such write. The lever that is verifiable from the data layer is **conditioning the line out** —
`housecarl_apply` an entry onto `Conditions` that cannot pass, then read it back. The other lever is
marking the INFO deleted (`housecarl_apply` on `IsDeleted`); treat that as **inferred, not measured**
— prefer conditioning-out, and if you use it, verify in game.

**The in-place lane sidesteps the trap entirely.** Where the topic lives in a plugin you own,
`in_place="<plugin>.esp"` edits the original records, so nothing is re-listed and nothing moves.

**You do not need the Creation Kit for any of this.** A byte-diff of pure houseCARL output against
the same plugin after a CK open-and-save came to **+90 bytes = 9 INFO `PNAM` subrecords and nothing
else** — every other subrecord the CK writes, houseCARL had already written.

## Workflow — author a new conversation

1. **Resolve the targets.** The speaker (an NPC or a quest alias — its voice type decides the voice
   folder), the quest the dialogue gates on, and whether you need a new branch or are attaching to an
   existing topic. Read a real vanilla line of the kind you are writing first — find it with
   `housecarl_records(types=["INFO"], references=["<npc formid>"])`, not by scanning on `Speaker`.
2. **Author the topic and its lines in one call** with `housecarl_create`. Declare the `DialogTopic`
   first, then each `DialogResponses` with `parent` naming the topic's editorid — that nests the line
   into the topic's `Responses`, and a line cannot stand alone:

   ```json
   records=[
     { "record_type": "DialogTopic", "editorid": "MyMod_AskRing",
       "ops": [ { "field_path": "Quest",   "value": "001A2B:MyMod.esp" },
                { "field_path": "Subtype", "value": "Custom" },
                { "field_path": "Name",    "value": "Tell me about the ring." } ] },

     { "record_type": "DialogResponses", "editorid": "MyMod_AskRing_L1", "parent": "MyMod_AskRing",
       "ops": [ { "field_path": "Prompt",  "value": "Tell me about the ring." },
                { "field_path": "Speaker", "value": "0008F2:MyMod.esp" },
                { "field_path": "Responses", "op": "Add",
                  "compose": { "type": "DialogResponse",
                               "fields": { "Text": "It is older than this city.",
                                           "ResponseNumber": "1" } } } ] }
   ]
   ```

   A spoken row is a composed struct: `op:"Add"` with `compose:{ "type":"DialogResponse", "fields":{…} }`,
   values as strings (`"ResponseNumber":"1"`, not `1`). `DialogResponse` singular is the spoken row;
   `DialogResponses` is the INFO record.
3. **Make it reachable.** A `Custom` topic with no entry point is byte-valid and never entered — only
   generic subtypes are matched without one. Author a `DialogBranch` whose `StartingTopic` names the
   topic, in the **same** `housecarl_create` call and declared *after* it: `"value": "@<the topic's
   editorid>"` on a FormLink field is a same-call sibling reference, and the create path substitutes
   the FormID it allocated. Across two calls instead, each new FormID is reported back and a second
   `housecarl_apply` on the same lane (`into="<this patch>.esp"`) sets the link — `housecarl_apply`,
   because the record exists by then and this sets a field on it.
4. **Author the conditions deliberately.** A line with no conditions fires whenever its topic is
   reached; gate it with `GetStage` and a speaker check. A well-formed but *wrong* condition is the
   single most common cause of permanently silent dialogue, and no tool can catch it — houseCARL can
   decode a condition and flag a malformed one, but only the running game can evaluate one.
5. **Result scripts, if the line does something.** Compose the `VirtualMachineAdapter` binding, author
   the `.psc`, compile it with `housecarl_compile_script`.
6. **Voice.** Provide the audio. On an override the folder is the INFO's *defining* plugin, not the
   winner.
7. **The `.seq`, if the quest starts at game start.** Set the flag, then run `housecarl_write_seq`
   against the plugin. After an in-place edit the `.esp` sits in the mod's own folder, so pass
   `output_dir=` that folder and the `.seq` lands beside it.

**Player-choice topics have their own semantics, and they are easy to get backwards** — `Prompt` is
the *player's* button, `Responses` is the *NPC's* reply to it, and `LinkTo` sets the player's next
options rather than anything the NPC says. Read `references/player-topics.md` before composing a menu.

## Validate, then hand off

`housecarl_check(findings=["dialogue"], seeds=["<DIAL or QUST FormID>"])` walks the graph: the topic
is wired to a quest, the branch resolves, the `LinkTo` chain and every previous-link resolve, each
voiced line's `.fuz` is on disk, each result script is bound and compiled, and a start-game-enabled
quest's `.seq` is present and current. A DIAL seed checks one topic; a QUST seed checks every topic
that quest owns. It refuses to claim more: it cannot evaluate a well-formed condition and does not
check lip-sync or audio, so a clean pass is never "this will play".

**It cannot see the plugin you just wrote.** The dialogue family is seeded, a seed must resolve in the
active load order, and no plugin scope narrows it — so before the mod is enabled the coverage you can
actually get is `housecarl_check(plugins=["<patch>.esp"], findings=["errors","scripts"])`, which does
sweep off-order. Run that first, then have the user enable the mod and seed the dialogue check
afterwards. Issue **#615** is the gap.

The effective merged INFO order — which line moved and which plugin moved it — is not a finding at
all. It is a projection: `housecarl_records(formids=["<topic>"], project={"form":"info_order"})`.
That is the check for the reorder trap, and the one a reader most often looks for in the wrong place.

Read the new records back (`readback=true` on the write), then tell the user what was written and
where.

## Common mistakes, and the rule that replaces each

- **Give a new line its own quest and topic; append into a vanilla topic only for a reason you can
  state.** Priority across quests decides which topic is entered; order inside a contested topic is
  not something the data layer can settle for you.
- **Carry the exemplar's `Emotion`, `EmotionValue` and `Flags` onto the response row.** The defaults
  are 0 and none, which no vanilla line uses.
- **Set `PreviousDialog` on every line you re-list, and re-list nothing else.** Vanilla topics have
  empty PNAM and that is never a defect, so never "complete the chain" on a topic you wrote. Carrying
  lines forward "to keep the topic complete" appends each to the bottom and reorders the topic — the
  exact conflict it was meant to avoid.
- **Write the `.seq` whenever a quest is start-game-enabled.** Ticking the flag is half the job.
- **Report a clean check as "the wiring resolves", never as "it will play".** A wrong `GetStage`
  value is well-formed, passes every check, and is silent in game forever. Carry that limit to the
  user rather than letting a green result speak for itself.
- **Derive the voice folder from the plugin that defines the INFO.** For a new plugin that is yours;
  for an override it is the original's folder, where the audio lives. The winner is the wrong answer.
- **Clone a verified condition gate; never compute the operator bytes.** A condition is a polymorphic
  struct, and hand-synthesizing its encoded operator once wrote 26 broken conditions onto one gate.
  Read a known-good gate back and copy it — the mechanics are in `references/write-side-recipes.md`.

## `references/`

- `references/dialogue-flow-model.md` — how the records connect and what actually drives the flow:
  conditions, `LinkTo`, quest stage, PNAM, and the cross-plugin merge that decides order. **Read it
  before authoring or auditing anything**, and before trusting any claim about which line plays.
- `references/condition-functions.md` — decoding a condition you read back: function, parameters, Run
  On, operators, the `OR` flag. Read it before reading or composing any `Conditions`.
- `references/player-topics.md` — player-choice topics: which text the player sees, which the NPC
  speaks, the ender flag, and how to branch an NPC's reply on one click. Read it before composing a
  player menu; a line that is byte-perfect here still plays absurdly if these are backwards.
- `references/write-side-recipes.md` — repeatedly-needed edits to *existing* dialogue: cloning a
  verified condition gate onto many lines, writing a subtype the CK will not offer, un-binding a
  result script. Read it when the job is an edit rather than an authoring pass.
- `references/dialogue-branch.md` — the DLBR entry point, its fields and the three flags. Read it when
  a `Custom` topic needs an entry point, or when an NPC has gone silent across the board.
- `references/quest-objectives-tab.md` — stages, objectives and log entries, and which of the three a
  line actually gates on. Read it when the dialogue is right and the journal is wrong.
- `references/seq-file-format.md` — what a `.seq` is and why a start-game-enabled quest is dormant
  without one. Read it when job 4 applies.
- `references/voice-file-naming.md` — the `.fuz` / `.lip` path template and the override-folder trap.
  Read it before deriving a voice path by hand, or when a line is silent in game.

The condition, branch and quest references are hand-curated from the Creation Kit wiki and Mutagen's
record model, not generator output — confirm a spelling against `housecarl:mutagen-reference` or a
real record before relying on it.

## Notes and out of lane

- **Read an exemplar before reaching for a schema.** One `project={"form":"everything"}` read of a
  real vanilla line of the kind you are writing gives every field name, every enum value and the exact
  condition shape in one call. Where there is no exemplar — a field nothing nearby carries, an enum
  you need the full legal set for — `housecarl:mutagen-reference` has it. Do not guess either way.
  For the Papyrus in a TIF fragment, `housecarl:papyrus-reference` has the function signatures.
- **Quest scaffolding rides along.** A flat `QUST` with its stages, aliases and objectives is
  createable with `housecarl_create`; this skill is the dialogue layer that wires onto it. Set the
  quest up first, then author the topics that reference it.
- **Out of lane.** Exterior-cell-keyed placement and runtime-spawned (`FFxxxxxx`) speakers are
  separate capabilities, not dialogue authoring — name the limit rather than guessing a path.
