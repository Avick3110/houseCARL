---
name: housecarl
description: >-
  Use whenever a task touches a Mod Organizer 2 modlist, load order, plugins,
  conflicts, ESP patches or overrides, a record type (ARMO, WEAP, NPC_, LVLI,
  MGEF), leveled lists, dark faces or facegen, dialogue, NIF meshes, SkyPatcher,
  SPID or KID lines, SKSE plugins, Papyrus, or BSA archives. Routes Skyrim
  Special Edition data-layer work through the houseCARL MCP server — a live MO2
  instance read at the true load-order winner, written into reviewable patch
  plugins — and names the specialist skill that owns each grammar. Not for
  another game, for installing MO2, or for gameplay advice with no data-layer
  step.
compatibility: Requires the houseCARL MCP server and a configured Mod Organizer 2 instance.
---

# houseCARL

**In:** a live Mod Organizer 2 instance, read through the houseCARL MCP server. **The work:** read the
record at its true load-order winner, check the schema, then write. **Out:** a patch plugin the user
reviews and enables in MO2. This file does two things and defers the rest — it routes a job to the skill
that owns its grammar, and it gives the read order every write depends on. What a tool takes is in that
tool's own description.

The user's instructions take precedence over guidelines provided in a skill.

## Which skill owns the job

| The user is asking about | Load | When |
|---|---|---|
| A dark, grey or black NPC face; a face wrong after compacting or merging | `housecarl:facegen-diagnostics` | before judging the fix |
| Copying one NPC's face onto another, or cloning a standalone follower | `housecarl:npc-appearance-copy` | before the copy |
| What fields a record type has, or a legal enum value | `housecarl:mutagen-reference` | before the write |
| A no-ESP edit to a record's own fields, or to one NPC | `housecarl:skypatcher-authoring` | before the INI line |
| Spells, perks, items, outfits or factions onto NPCs by group | `housecarl:spid-authoring` | before the `_DISTR.ini` line |
| Keywords onto item records | `housecarl:kid-authoring` | before the `_KID.ini` line |
| Adding, wiring or auditing dialogue topics and lines | `housecarl:dialogue-authoring` | before the DIAL/INFO write |
| Gating animations by weapon, keyword, perk or race | `housecarl:open-animation-replacer` | before the condition |
| A Papyrus or SKSE function signature, or a `.psc` edit | `housecarl:papyrus-reference` | before the script edit |
| Writing or building a native SKSE plugin DLL in C++ | `housecarl:skse-plugin-authoring` | before the first C++ |
| A catalogue, audit, conflict survey or link graph over many records | `housecarl:bulk-record-jobs` | before the first call |

MCP tools are written bare on both hosts (`housecarl_records`); a sibling is written `housecarl:<skill>`, the
Claude Code invocation — on Codex it is the bare folder name (`facegen-diagnostics`), installed beside this one.

## Read before you write

1. **Confirm context when it matters.** `housecarl_load_order_status` says what is active;
   `housecarl_set_mo2_instance` when the user names a different MO2 instance folder.
2. **Read the winner and the schema.** `housecarl:mutagen-reference` for the field path and its legal
   values — if it has no entry for a type, say so rather than guessing; `housecarl_records` for the record
   as the order resolves it, `project={"form":"tree"}` for every provider when the winner is contested.
3. **Write with one verb.** `housecarl_apply` edits fields (`ops=`), `housecarl_create` mints records
   (`records=`), `housecarl_remove` drops them (`formids=`), `housecarl_forward` carries another plugin's
   record as an override. Every list is set-valued — one op is a set of one — there is no single/bulk pair.
4. **Read the written record back**, and say what happened when it did not take.

```
housecarl_apply(ops=[{"formid": "013BA3:Skyrim.esm", "field_path": "BasicStats.Damage", "value": 12}], patch="SwordFix", readback=true)
```
```
wrote SwordFix.esp   1 record, 1 op   epoch=7f3a1c
  013BA3:Skyrim.esm  IronSword  BasicStats.Damage  10 -> 12
```

The read-back is the written FILE, not load-order truth. Report the patch name back — it is auto-suffixed
when taken — and tell the user to enable it. A refused call wrote nothing: fix the path and send it again.

## Lanes and FormIDs

A FormID is `XXXXXX:Plugin.esp` — six hex digits, then the filename of the master that defines the record.
The runtime form a log or the console prints is taken too, wherever a parameter holds nothing but FormIDs.
SkyPatcher, SPID and KID each write their own syntax; their skills say so.

Every write tool has the same three lanes: `patch=` writes a new plugin, `into=` extends an existing houseCARL
patch, `in_place=` overwrites the file it names. In place is consent-gated at the server, per plugin, by
`acknowledge=` — it refuses rather than asking this skill to police it.

## Where this does not apply

Another game; installing or configuring MO2; gameplay advice with no record, file or INI in it. Two reaches the
surface does not have today, both filed: `housecarl_check` with `findings=["dialogue"]` resolves against the
active order, so a plugin not yet enabled cannot be dialogue-checked (#615); and a hand-composed raw mods-folder
path is not refused in one sentence by `housecarl_place` or `housecarl_nif_inspect` (#617) — pass the mod
folder a read-back named instead.

## The sidecar

Codex reads `agents/openai.yaml` beside this file for the display name and invocation policy; nothing in
this body depends on it.
