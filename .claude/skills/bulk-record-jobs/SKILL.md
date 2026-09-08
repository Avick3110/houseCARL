---
name: bulk-record-jobs
description: >-
  Catalogues, audits, spreadsheets, conflict surveys and link graphs over many records at once —
  plan the whole job before the first call and pin one deliverable schema. Use when the user wants
  an enumeration of records across a load order, asks who wins the contested ones, or wants a patch
  rebuilt against a new mod version — and in ANY subagent task told to "extract X and return JSON".
  Not for one record — a single read or edit needs no bulk plan. Load before the first call;
  per-record loops and a drifting output shape both lock in there.
compatibility: Requires the houseCARL MCP server and a configured Mod Organizer 2 instance.
---

# Bulk Record Jobs

Many records in, one structured deliverable out. Two costs lock in at the first call and neither is
recoverable afterwards: the **read plan** (a loop where one call takes the whole list; a window
paged deep where the whole set could have spilled to a file) and the **output shape** (every
extractor inventing its own encoding). Size the job, pin the deliverable schema, then read.

This body is one job: get a complete, self-accounting enumeration out and hand it over in one
schema. The three files under "Where the rest lives" carry the recipes; the last table says when
the job is not this skill's. MCP tools are written bare on both hosts (`housecarl_records`);
sibling skills as `housecarl:<skill>`, the Claude Code invocation — on Codex that is the bare
folder name.

## Size before you read

One cheap census bounds the job. `project={"form":"aggregate"}` counts every match — `limit=` never
windows its groups — and `counts_only=true` returns the accounting and the counts with no rows.

```
housecarl_records(types=["ARMO"], plugins={…the mod set…},
                  project={"form":"aggregate", "group_by":"defined_in"}, counts_only=true)
```
```
total 1412   epoch=7f3a1c
  ModA.esp  806    ModB.esp  512    Skyrim.esm  94
```

The count key answers a different question each way: `winner` — who wins the contested records
(the conflict survey); `type` — what a plugin is made of; `defined_in` — definitions split from
overrides. The number decides the plan: 40 records and 40,000 records are not the same job.

Write the deliverable schema down before any extraction starts, especially before handing work out.

## Getting the whole set out — one lane, not two

There is one lane, with two knobs. `max_chars` is a ceiling on the **render**, never on the result:
an over-ceiling result spills to a server-side JSONL artifact whose line 1 is the manifest (query
echo, row schema, epoch), and the response names the file. So the move for a big enumeration is a
**small** `max_chars`, not a large one — but what spills is what `limit=` let through (default 500),
and the marker says which: `spilled: complete result (N rows)` against `spilled: the returned WINDOW
(N rows of T total matches)`, whose matches beyond `limit=` are in no file at all.

`to_file="<absolute .jsonl>"` is what captures the **complete** result — it is the spelling that
lifts the window, writing every selected row and rendering only the manifest inline. It refuses
`offset=`: the artifact is never a window. Re-enter it later with `formids=["@<path>"]` or
`where=["formid in @<path>"]`, epoch-checked.

```
housecarl_records(types=["NPC_"], format="json",
                  project={"form":"fields", "fields":["EditorID","Name","Race"]},
                  to_file="C:/work/npc-winners.jsonl")
```
```
"total": 66856, "matches": [],
"spilled": {"path":"C:/work/npc-winners.jsonl", "reason":"to_file", "complete":true,
            "row_count":66856, "total":66856, "identity":"formid", "epoch":"7f3a1c",
            "row_schema":["formid","runtime_formid","type","editorid","winner","override_depth",
                          "source","matches?","fields"]}
```

The row schema is the artifact's own, not the fields you asked for: the requested paths sit **under
`fields`** on each row, never as top-level columns — `row.fields["EditorID"]`, not `row.EditorID`.

Run scripts against the file; never read a multi-MB artifact into context. If you page instead,
`offset=` **re-scans** the selection from the start, so every window pays the whole scan again and a
deep window costs more than a shallow one — narrowing `types=` / `plugins=` / `where=` beats paging
far into one, and windows tile only within one `epoch`.

The render bound is declared per form and refuses up front rather than going silent: ~300,000 rows
for a named-fields row against ~15,000 for `form="everything"`, whose row materialises the whole
record. Name the fields you need and the same selection fits.

**Annotating every link is the trap that kills the job.** An identity per FormLink is one winner
lookup per link — 66,856 of them on the catalogue above — and the client aborts on the idle timeout
with no partial artifact to salvage. Bound the set, or read the identity catalogue separately and
join it locally.

## Prove it whole

A persisted result is trustworthy only after these three checks, made **in the file**:

1. Line 1 is the manifest — a flat object keyed `housecarl_artifact`, `tool`, `query`, `identity`,
   `row_schema`, `sort`, `row_count`, `total`, `epoch` — and it is the thing you read, not the chat
   response's summary.
2. `row_count` equals `total`. Short of it means the file holds a **window**, not the result.
3. Every window carries the **same** `epoch`.

```
head -1 C:/work/npc-winners.jsonl   →  {"housecarl_artifact":1,"tool":"housecarl_records",…,
                                        "row_count":66856,"total":66856,"epoch":"7f3a1c"}
wc -l   C:/work/npc-winners.jsonl   →  66857     (manifest + 66,856 rows)
```

Differing epochs mean the load order changed mid-pagination: re-run from `offset=0`, never stitch
the windows. Any of the three failing means the deliverable is incomplete — re-read it, or say so
in `notes`. Never ship it as whole.

## Scope truth — two clauses

- **Definitions, not touches.** The `plugins` scope decides which records are *considered*, and a
  bare scope matches every record those plugins **touch** — their own definitions and their
  overrides of other plugins' records. "What this mod adds" is the definitions half, and the scope
  object carries that switch (spelling on `housecarl_records.plugins`). Without it a patch or
  replacer double-counts its overrides into the catalogue under the wrong mod.
- **Live values, not the scoped era.** Under a `plugins` scope, field values render the *scoped
  plugin's own* era — the defining esp's original number, not what the game uses after later
  overrides. A deliverable claiming live stats passes `fields_source="winner"`. Matching is a
  separate pole: `where_source="winner"` decides what a `where=` predicate matches on.

## The loop-killer map

Reach for the primitive, never the loop.

| About to… | Use instead |
|---|---|
| Run a reverse lookup once per target record | ONE `housecarl_records` call — `references=` takes the whole list (OR semantics; each match names which target(s) it hit) |
| Post-filter FormIDs by their `:Plugin.esp` suffix | the definitions half of the `plugins` scope (see Scope truth) |
| Dump every match and tally winners by hand | `project={"form":"aggregate","group_by":"winner"}` — counts all matches, ungrouped by `limit=` |
| Read records one call each | one call with `formids=` — the cost is the LIST's length, not the window's, so pass fewer ids rather than paging |
| Read whole records just to label FormIDs | `project={"form":"summary"}` — identity off a gathered read; `form="identity"` is the dearest row on the lane, not a free one |
| Annotate every link on a full render | bound the set, or read identity separately and join locally |
| Parse `path = token` text back into JSON | `format="json"` — same tokens, stable document, accounting in-band |
| Read two plugin versions and subtract by hand | `project={"form":"delta"}` with `source=` the subject and `versus=` the reference pole; either may be an on-disk, unticked plugin |
| Walk a link chain call by call | `walk=` — the traversal is a SELECT term, and any reading form consumes what it reaches |
| Filter matches after the read | `where=` — comparisons, flag tests, quantified list steps, `*parent`, one `->` link step, `formid in @<file>` |
| Re-type another version's field values into ops | `housecarl_apply` with an `ops[]` entry of `op="CopyFrom"` + `from_source=` — the field is taken from the plugin you name, not from the winner (the pole is CopyFrom's; any other verb refuses it by name) |
| Add list elements one op at a time | `composes` on one op — `op="Add"` appends N elements, `op="ReplaceAll"` rebuilds the whole list and `composes=[]` under it clears (the default `Set` has no list element to mean) |

## The canonical deliverable shape

One shape for "many records → one document". Add job-specific keys; do not restructure.

```json
{
  "job": "one line: what this deliverable is",
  "scope": {"types": ["WEAP"], "plugins": ["ModA.esp"], "definitions_only": true, "fields_source": "winner"},
  "total": 548,
  "complete": true,
  "epoch": "7f3a1c",
  "notes": ["tool notes + job caveats carried here, never dropped"],
  "records": [
    {
      "formid": "012EB7:Skyrim.esm",
      "type": "Weapon",
      "editorid": "IronSword",
      "name": "Iron Sword",
      "winner": "SomePlugin.esp",
      "fields": {"BasicStats.Damage": "7"}
    }
  ]
}
```

The rules that make it a contract:

- **`formid` is the full wire token** (`XXXXXX:Plugin.esp`), verbatim from tool output — never bare
  hex (it collides across plugins), never reformatted. A token read is a token a write can reuse.
- **Identity is four keys**: `type`, `editorid`, `name`, `winner`. `name` is `null` where the record
  has none; never back-fill it with the EditorID.
- **A resolved link is an object, not a replacement**: `{"formid": …, "editorid": …, "name": …}` —
  keep the token *and* the identity. A name-only column cannot be queried again.
- **Field values are wire tokens verbatim.** Rounding, unit conversion and splitting belong to a
  presentation layer, never to the extraction rows.
- **Accounting travels**: `total`, `complete`, `epoch` and `notes` ride with the document. A partial
  deliverable that says so is fine.
- **Closed enums for classifications**, declared in the deliverable (a recipe `kind` of `craft` /
  `temper` / `other`). Free text is where eight extractors drift eight ways.

## Where the rest lives

- Read `references/catalogue-and-link-graph.md` when the deliverable is an enumeration carrying
  field values, or a what-points-at-what graph over a set of records.
- Read `references/crafting-recipes.md` when the records are COBJ recipes, or when the deliverable
  has to say whether a recipe crafts or tempers.
- Read `references/patch-rebuild.md` when the job is re-deriving an existing patch against a new
  version of the mod it patches.

## Where this is not the skill

| The question is really… | Go to |
|---|---|
| "What fields does record type X have / what are the legal enum values?" | `housecarl:mutagen-reference` |
| One record to read or edit | the tools directly — a single read or edit needs no bulk plan |
| A whole-order facegen sweep — every NPC a plugin touches, deduped to winners | plan the sweep here, then take the flagged subset to `housecarl:facegen-diagnostics` |
| What a specific mod's keywords or conventions *mean* | that mod's own skill lane, never encoded here |
| Distributing spells, keywords or items at runtime instead of cataloguing them | `housecarl:spid-authoring` / `housecarl:kid-authoring` / `housecarl:skypatcher-authoring` |
