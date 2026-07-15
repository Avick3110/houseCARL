---
name: bulk-record-jobs
description: >-
  Plan and run bulk record jobs over the Skyrim load order via houseCARL — catalogues, audits, item/recipe/link graphs, conflict surveys, and batch patch rebuilds: any job that turns MANY records into ONE structured deliverable. Routes per-record loops to the bulk primitives (defined_in= scope, list-valued references=, group_by= counts, winner_fields=, resolve_names=, format=json, housecarl_resolve, housecarl_diff_record, CopyFrom/composes= batch writes) and standardizes the deliverable on one canonical JSON shape instead of an invented schema. Use when the user wants a catalogue, index, spreadsheet, audit, or book of armors/weapons/NPCs/recipes/spells, asks what crafts or references what across plugins, who wins contested records, wants a compatibility patch rebuilt against a new mod version — and in ANY fan-out subagent task told to "extract X and return JSON/a table". Load this BEFORE the first query or the first line of an output schema — per-record loops and schema drift are locked in by the first call, not discovered at the end.
---

# Bulk Record Jobs

## Overview

A bulk record job is any task shaped "many records in, one structured deliverable out": a gear
catalogue, a crafting-graph, a conflict survey, a whole-mod audit, a compatibility-patch rebuild,
a fan-out extraction returning JSON. The houseCARL tool surface has one primitive for every loop
these jobs used to improvise — this skill maps job shapes to those primitives, carries the few
game-level conventions the binary format doesn't (what a crafting-station keyword means, how a
temper recipe differs structurally from a craft recipe), and defines the one canonical deliverable
shape so ten agents extracting in parallel produce one schema, not ten.

Two costs dominate badly-planned bulk jobs, and both are locked in by the first call: per-record
loops (a reverse lookup run 548 times instead of once; ~400 one-element list ops instead of ~40
batched ones) and schema drift (each extractor inventing its own output encoding). Plan the calls
and fix the output shape before touching a record.

## First step — size the job, then fix the shape

Before the first detail read:

1. **Size the scope with a count, not a dump.** `group_by=` on `housecarl_cross_plugin_query`
   aggregates ALL matches into a count table in one cheap call:

   ```
   housecarl_cross_plugin_query(type="ARMO", plugins=["SomeMod.esp"], group_by="defined_in")
   ```

   `group_by="winner"` opens a conflict survey ("who wins the contested records, by plugin");
   `"type"` profiles a plugin's contents; `"defined_in"` splits definitions from overrides.
   The counts tell you whether the job is 40 records or 4,000 before you commit to a per-row plan.

2. **Write the deliverable schema down** (see "The canonical deliverable shape" below) before any
   extraction starts — especially before handing work to subagents. The schema is the contract;
   extraction fills it.

## Scope truth — two flags that decide what your numbers mean

These two are the proven silent-wrong traps of bulk work. Neither errors when missed; both
corrupt the deliverable.

- **`defined_in=true` — definitions, not touches.** `plugins=["X.esp"]` alone matches every
  record X.esp *touches* — its own definitions AND its overrides of other plugins' records. A
  catalogue scope ("the items this mod adds") means *defined in*. A replacer or patch plugin can
  return dozens of matches on a bare `plugins=` scope, every one an override that double-counts
  into your catalogue under the wrong mod. Pass `defined_in=true` whenever the question is "what
  does this plugin add"; leave it off only when overrides are genuinely part of the question.

- **`winner_fields=true` — live values, not the scoped plugin's era.** Under a `plugins=` scope,
  `fields=` renders the *scoped plugin's own* version of each value — the defining esp's original
  armor rating, not the number the game uses after later overrides. The output says so in a note,
  but a bulk consumer that never reads notes ships plausible stats from the wrong era. For any
  deliverable claiming live stats, pass `winner_fields=true`; the scoped-values default exists for
  the other question ("what did THIS plugin set").

## The loop-killer map

Reach for the primitive, never the loop. Each row below was a real improvisation in a real fleet
run before the primitive existed:

| About to… | Use instead |
|---|---|
| Run `references=` once per target record | ONE call — `references=` takes a LIST (OR semantics; each match reports which target(s) it hit via `matches=`) |
| Post-filter FormIDs by `:Plugin.esp` suffix | `defined_in=true` on the query |
| Dump all matches and tally winners by hand | `group_by="winner"` (counts ALL matches, not capped by `limit=`) |
| Read records one call each | `housecarl_batch_record_detail` with the whole FormID list |
| Read full records just to label FormIDs with names | `housecarl_resolve` — one identity line per FormID (type, editorid, name, winner), per-item error isolation |
| Maintain a FormID→name cache by hand | `resolve_names=true` on the read — every FormLink annotated inline, cached server-side across the batch |
| Parse `path = token` text back into JSON | `format="json"` on the read/query — same tokens, stable document, accounting in-band |
| Read two plugin versions and subtract by hand | `housecarl_diff_record(formid, plugin_a, plugin_b)` — either side may be an on-disk, not-enabled plugin |
| Re-type another version's field values into ops | `verb:"CopyFrom"` with `from_plugin=` in `housecarl_bulk_apply` — transplants the field, source may be off-order |
| Add list elements one op at a time | `composes=[...]` — one op appends N elements (`verb:"Add"`) or replaces the whole list (`verb:"ReplaceAll"`; `composes=[]` clears it) |

## Recipe: the catalogue

"Enumerate everything of type T that mod set S adds, with live stats and resolved names."

1. **Size**: `group_by="defined_in"` over the scope (First step).
2. **Enumerate + flat fields in one pass** (scalar fields only — Name, ArmorRating, weights…):

   ```
   housecarl_cross_plugin_query(
       type="ARMO", plugins=["ModA.esp", "ModB.esp"], defined_in=true,
       fields=["Name", "ArmorRating"], winner_fields=true, format="json")
   ```

3. **Expand list-bearing rows in a second, batched pass.** `housecarl_cross_plugin_query` has no
   `depth=` — a list field renders as a count note (`[list: 5 item(s)]`). Feed the enumerated
   FormIDs to `housecarl_batch_record_detail` with `depth=2`, `resolve_names=true`,
   `format="json"`: each list element comes back indexed (`Keywords[0]`…) with a structured
   `link` sibling (`{resolved, type, editorid, name}`) beside the raw token.
4. **Respect the accounting.** Every JSON document carries `total` / `rendered` / `capped` /
   `truncated` / `notes` in-band. `capped=true` means raise `limit=` or page by narrowing scope —
   never ship a deliverable whose `total` exceeds its row count without saying so.

## Recipe: the link graph

"For each record, what points at it / what does it point at, as names not hex." Works for any
link-shaped question — crafting recipes, outfits, leveled lists, dialogue — because the reflection
layer knows every FormLink on every type. One graph level = two batched calls:

1. **Reverse edge** (who points at these?): `references=[...]` with the whole target list, bounded
   by `type=` — e.g. every recipe involving a list of items is ONE call with `type="COBJ"`.
2. **Contents + identity**: `housecarl_batch_record_detail` on the matches, `depth=` deep enough
   for the paths you need, `resolve_names=true` so every link is labeled inline.

Worked example — the crafting graph for one weapon (verified live; vanilla data):

```
housecarl_cross_plugin_query(type="COBJ", references=["012EB7:Skyrim.esm"])
→ TemperWeaponIronSword, RecipeWeaponIronSword, … (every recipe touching Iron Sword, one scan)

housecarl_batch_record_detail(
    formids=[…those COBJs…], depth=5, resolve_names=true, format="json",
    fields=["EditorID", "Items", "WorkbenchKeyword", "CreatedObject", "CreatedObjectCount", "Conditions"])
```

The load-bearing COBJ paths (only visible at depth ≥ 4–5):

- materials: `Items[i].Item.Item` (the ingredient link) + `Items[i].Item.Count`
- station: `WorkbenchKeyword` (a Keyword link — see conventions below)
- product: `CreatedObject` + `CreatedObjectCount`
- gates: `Conditions[i].Data.Function` names the condition kind; `Conditions[i].Data.Perk`
  carries the perk link when the function is `HasPerk`

`resolve_names` annotations resolve against the **live load order**: reading a vanilla body on a
modded order can label a link with a mod's renamed identity for that FormID. That's the truthful
in-game identity — but if the deliverable documents the *original* plugin's era, resolve names
from that era's values instead of assuming the annotation matches the source.

## Crafting-station conventions (what the format doesn't say)

The binary format stores `WorkbenchKeyword` as just a Keyword link; which station it is and what
the recipe *means* there is Creation-Kit convention:

- **Enumerate stations from data, don't hardcode**: crafting-station keywords are vanilla KYWD
  records — `housecarl_cross_plugin_query(type="KYWD", editorid_contains="Crafting",
  plugins=["Skyrim.esm"], defined_in=true)` lists them live (forge, skyforge, sharpening wheel,
  armor table, smelter, tanning rack, cookpot…). Ignore the `WICrafting*` ones — those are
  radiant-story event keywords, not stations. Mods add their own station keywords; the same query
  without the plugin scope finds them.
- **Craft vs temper is structural, not naming.** A *craft* recipe (forge/smelter/tanning/cookpot):
  `CreatedObject` = the item produced from the materials. A *temper* recipe (sharpening wheel for
  weapons, armor table for armor): `CreatedObject` = the very item being *improved*, count 1, and
  the canonical vanilla condition pair is `EPTemperingItemIsEnchanted != 1` (OR-flagged) +
  `HasPerk(<arcane-smithing-class perk>)`. Classify by `WorkbenchKeyword` + this shape.
- **Conventions are conventions — the data can be dirty.** Vanilla itself ships
  `TemperWeaponSkyforgeBow` whose `CreatedObject` is a *battleaxe*. Classify from the fields you
  read, and let an EditorID that disagrees with the structure raise a flag in the deliverable
  rather than win the argument.
- **A recipe that appears at no station** (null/odd `WorkbenchKeyword`, or gated by an
  impossible condition) is a common *mod-specific* disablement idiom — whose meaning belongs to
  that mod's own skill lane, not here. Report the structure; don't guess the intent.

## Recipe: the patch rebuild

"Re-derive an old compatibility patch against a new version of its target mod." The job is
delta-analysis then batched re-application; the old patch is usually *disabled*, which is fine —
the diff and copy primitives read on-disk plugins that aren't in the load order.

1. **Survey the contest**: `group_by="winner"` over the target mod's records — one call says how
   much of the mod is contested and by whom.
2. **Extract the old delta per record**: `housecarl_diff_record(formid, plugin_a="OldPatch.esp",
   plugin_b="TargetMod.esp")` — field-level deltas, each labeled by plugin_b's name; `fields=`
   scopes the comparison; a truncated read reports itself rather than claiming "identical".
3. **Forward the new baseline** (`housecarl_forward_record`), then **re-apply the still-valid
   deltas** with `housecarl_bulk_apply into=` on the forwarded copies — ops target the patch's
   own already-forwarded body, not the stale winner (the forward-then-edit precedence both tools'
   docs state).
4. **Batch the re-application**: whole-field transplants use `verb:"CopyFrom"` +
   `from_plugin="OldPatch.esp"` (works from a disabled plugin on disk); list rebuilds use
   `composes=[...]` — N perk placements in one op, `ReplaceAll` + `composes` to rebuild a modeled
   list wholesale, `ReplaceAll` + `composes=[]` to empty it. All-or-nothing per call: one bad
   element refuses the whole call with per-element reasons.
5. **Verify before enabling**: `housecarl_check_errors` and `housecarl_compact_plugin` accept the
   not-yet-enabled patch; `housecarl_diff_record` with the new patch as a pole confirms the
   deltas landed.

## Recipe: the fan-out

When the job fans out to subagents, schema drift is the failure mode — N agents told "extract X,
return JSON" will invent N schemas unless the orchestrator pins one. In every subagent prompt:

- **Hand the agent the deliverable schema** (below), filled with the job's field list — not a
  prose description of it. Agents hardwire against what you show them.
- Name the exact calls: the scope flags (`defined_in`, `winner_fields`), `format="json"`,
  `resolve_names=true`, and the field paths with their depths. A recipe the orchestrator verified
  once beats eight agents re-deriving it.
- Require the accounting fields in each agent's return, so a truncated extraction surfaces at
  merge time instead of shipping silently incomplete.

## The canonical deliverable shape

One blessed shape for "many records → one document". Use it as the return contract for subagents
and the final deliverable alike; add job-specific keys rather than restructuring.

```json
{
  "job": "one line: what this deliverable is",
  "scope": {"type": "WEAP", "plugins": ["ModA.esp"], "defined_in": true, "winner_fields": true},
  "total": 548,
  "complete": true,
  "notes": ["tool notes + job caveats carried here, never dropped"],
  "records": [
    {
      "formid": "012EB7:Skyrim.esm",
      "type": "Weapon",
      "editorid": "IronSword",
      "name": "Iron Sword",
      "winner": "SomePlugin.esp",
      "fields": {"Damage": "7"}
    }
  ]
}
```

The rules that make it a contract:

- **`formid` is the full wire token** (`XXXXXX:Plugin.esp`), verbatim from tool output — never
  bare hex (collides across plugins), never reformatted (a token read is a token a write can reuse).
- **Identity is four keys**: `type`, `editorid`, `name`, `winner` — straight from
  `housecarl_resolve` / the read header. `name` is `null` where the type has none; don't substitute
  the editorid.
- **A resolved link is an object, not a replacement**: `{"formid": "…", "editorid": "…",
  "name": "…"}` — keep the token AND the identity; a name-only column can't be queried again.
- **Field values are wire tokens verbatim.** Reformatting (unit conversion, rounding, splitting)
  happens in a separate presentation layer, never in the extraction rows.
- **Accounting is mandatory**: `total` (true match count), `complete` (every row present?), and
  `notes` travel with the document. A partial deliverable that says so is fine; one that doesn't
  is a silent wrong answer.
- **Closed enums for classifications**, declared in the deliverable (e.g. a recipe `kind` of
  `craft` / `temper` / `other`) — free-text classification is where eight agents drift eight ways.

For crafting-graph jobs specifically, the blessed per-recipe entry:

```json
{
  "recipe": "0DA769:Skyrim.esm",
  "kind": "craft",
  "station": "CraftingSmithingForge",
  "creates": {"formid": "012EB7:Skyrim.esm", "editorid": "IronSword", "name": "Iron Sword", "count": 1},
  "materials": [{"formid": "05ACE4:Skyrim.esm", "editorid": "IngotIron", "name": "Iron Ingot", "count": 2}],
  "requires_perks": [{"formid": "…", "editorid": "…", "name": "…"}],
  "other_conditions": 1
}
```

## Common mistakes

- **Looping a primitive that takes a list.** `references=`, `housecarl_resolve`,
  `housecarl_batch_record_detail`, `composes=` — all batch. The single-item call in a loop is
  the old world.
- **Shipping scoped values as live stats.** The `plugins=` + `fields=` default renders that
  plugin's own era; a catalogue built without `winner_fields=true` is plausibly-wrong everywhere
  a later plugin rebalanced.
- **Counting overrides as content.** A bare `plugins=` scope on a patch/replacer returns
  overrides; without `defined_in=true` they double-count into the catalogue under the wrong mod.
- **Parsing text output when `format="json"` exists.** Hand-parsing is where schema variance
  creeps in; the JSON document carries the same tokens plus the accounting.
- **Dropping the accounting.** `capped`, `truncated`, `rendered < total`, and `notes` exist to be
  propagated. A deliverable that silently ignores them claims completeness it doesn't have.
- **Trusting names over structure.** EditorIDs and display names are conventions; classify from
  fields (station keyword, `CreatedObject` relationship, condition functions) and flag
  disagreements — vanilla itself contains miswired records.
- **Re-typing what `CopyFrom` can transplant.** Reading a value out of one plugin and composing it
  back by hand re-introduces transcription risk the primitive exists to remove.

## Sub-topic routing

| The question is really… | Go to |
|---|---|
| "What fields does record type X have / what are the legal enum values?" | `mutagen-reference` |
| "Which armors sit on biped slot N?" | `biped-slot-reference` |
| What a specific mod's keywords/conventions *mean* (any per-mod semantics) | that mod's own skill lane (e.g. a Requiem companion skill), never encoded here |
| One record to read or edit | the tools directly — no bulk planning needed |
| Distributing spells/keywords/items at runtime instead of cataloguing them | `spid-authoring` / `kid-authoring` / `skypatcher-authoring` |

## Notes

- `housecarl_cross_plugin_query` has no `depth=` — list expansion always goes through
  `housecarl_batch_record_detail` (or `housecarl_read_record` for one record). Budget the second
  call into any list-bearing catalogue plan.
- `where=` accepts FormLink equality against a wire token (e.g.
  `"WorkbenchKeyword = 088108:Skyrim.esm"`) — a station filter is one predicate, no post-filtering.
- Off-order (disabled, on-disk) plugins are first-class poles for `housecarl_diff_record`,
  sources for `CopyFrom`, and targets for `housecarl_read_plugin_file` / `housecarl_check_errors`
  / `housecarl_compact_plugin` — "the old patch is disabled" blocks nothing.
- Aggregation (`group_by=`) counts ALL matches regardless of `limit=`; only row *rendering* is
  capped. Use counts freely for sizing.
