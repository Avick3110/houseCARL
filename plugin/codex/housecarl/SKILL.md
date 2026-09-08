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
reviews and enables in MO2. This file routes a job to the skill that owns its grammar, and gives the read
order every write depends on. What a tool takes is in that tool's own description.

The user's instructions take precedence over guidelines provided in a skill.

## Which skill owns the job

| The user is asking about | Load | When |
|---|---|---|
| A dark, grey or black NPC face; a face wrong after compacting or merging | `housecarl:facegen-diagnostics` | before judging the fix |
| What fields a record type has, or a legal enum value | `housecarl:mutagen-reference` | before the write |
| A no-ESP edit to a record's own fields, or to one NPC | `housecarl:skypatcher-authoring` | before the INI line |
| Spells, perks, items, outfits or factions onto NPCs by group | `housecarl:spid-authoring` | before the `_DISTR.ini` line |
| Keywords onto item records | `housecarl:kid-authoring` | before the `_KID.ini` line |
| Adding, wiring or auditing dialogue topics and lines | `housecarl:dialogue-authoring` | before the DIAL/INFO write |
| Gating animations by weapon, keyword, perk or race | `housecarl:open-animation-replacer` | before the condition |
| A Papyrus or SKSE function signature, or a `.psc` edit | `housecarl:papyrus-reference` | before the script edit |
| Writing or building a native SKSE plugin DLL in C++ | `housecarl:skse-plugin-authoring` | before the first C++ |

MCP tools are written bare on both hosts (`housecarl_records`); a sibling is written `housecarl:<skill>`, the
Claude Code form — on Codex it is the bare folder name (`facegen-diagnostics`) installed beside this one.

## Read before you write

1. **Confirm context when it matters.** `housecarl_load_order_status` says what is active;
   `housecarl_set_mo2_instance` when the user names another MO2 instance folder.
2. **Read the winner and the schema.** `housecarl:mutagen-reference` for the field path and its legal
   values — no entry for a type means say so, never guess; `housecarl_records` for the record
   as the order resolves it, `project={"form":"tree"}` for every provider when contested.
3. **Write with one verb.** `housecarl_apply` edits fields (`ops=`), `housecarl_create` mints records
   (`records=`), `housecarl_remove` drops them (`formids=`), `housecarl_forward` carries another plugin's
   record as an override. Every list is set-valued — one op is a set of one; no single/bulk pair.
4. **Read the written record back**, and say what happened if it did not take.

```
housecarl_apply(ops=[{"formid": "012EB7:Skyrim.esm", "field_path": "BasicStats.Damage", "value": "12"}], patch="SwordFix", readback=true)
```
```
wrote SwordFix.esp (new patch; 1284 bytes)
mod folder: SwordFix  — enable + sort it in MO2 to use the patch
full read-back — … NOT load-order truth; the patch wins nothing until enabled + sorted in MO2:
  Weapon 012EB7:Skyrim.esm  editorid=IronSword
    BasicStats.Damage = 12
    ...
```

Report the patch name and mod folder back — the name is auto-suffixed when taken. A refused call wrote
nothing; a failed in-place write says so in its message.

## Lanes and FormIDs

A FormID is `XXXXXX:Plugin.esp` — six hex digits, then the defining master's filename. The runtime form a log
or the console prints is read-only: `housecarl_records` takes it, every write refuses it and names the
`XXXXXX:Plugin.esp` form. SkyPatcher, SPID and KID write their own syntax.

`housecarl_apply`, `housecarl_create` and `housecarl_forward` take three lanes: `patch=` a new plugin, `into=`
an existing houseCARL patch, `in_place=` the file it names. `housecarl_remove` edits only what exists: `into=`
or `in_place=`, no `patch=`. In place is consent-gated per plugin by `acknowledge=`.

## Where this does not apply

Another game; installing or configuring MO2; gameplay advice with no record, file or INI. Two reaches the
surface lacks today, both filed: `housecarl_check` with `findings=["dialogue"]` resolves against the active
order, so a plugin not yet enabled cannot be dialogue-checked (#615); and a hand-composed raw mods-folder path
is not refused in one sentence by `housecarl_place` or `housecarl_nif_inspect` (#617) — pass the mod folder a
read-back named.

## The sidecar

Codex reads `agents/openai.yaml` beside this file for the display name and invocation policy; nothing here
depends on it.
