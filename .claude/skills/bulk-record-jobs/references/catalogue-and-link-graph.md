# The catalogue and the link graph

The two enumeration recipes end to end. Both are `housecarl_records` calls: one scan for the
catalogue, two calls per graph level. The costly mistake in both is annotating identities on a full
render — the rule is at the bottom and it is the one that decides whether the job finishes.

## Recipe: the catalogue

"Enumerate everything of type T that mod set S adds, with live values."

1. **Census first.** `project={"form":"aggregate","group_by":"defined_in"}` with
   `counts_only=true`, over the scope. The count decides whether this is one call or a spilled
   artifact.

2. **One scan carries the enumeration and the fields.** Name the field paths in the form that uses
   them; `fields_source="winner"` for live values under a `plugins` scope; `format="json"` so the
   accounting stays in-band; `to_file=` when the whole set is the deliverable.

   ```
   housecarl_records(
       types=["ARMO"], plugins={…the mod set, definitions only…}, fields_source="winner",
       project={"form":"fields", "fields":["Name","ArmorRating","Keywords"], "depth":2},
       format="json", to_file="C:/work/armo-catalogue.jsonl")
   ```

   `depth` belongs to the form, not to the call: `depth: 2` on a list field returns each element
   indexed (`Keywords[0]`…) across every match. `format="dense"` is positional columnar cells 1:1
   with the requested fields, so depth expansion and `form="everything"` are inexpressible in it —
   use `"json"` for expanded scans.

3. **Respect the accounting.** The manifest carries `total`, the epoch and the row schema. Rows
   short of `total` is a partial deliverable and has to say so.

## Recipe: the link graph

"For each record, what points at it / what does it point at, as names not hex." Works for any
link-shaped question — recipes, outfits, leveled lists, dialogue — because the reflection layer
knows every FormLink on every type. One graph level is two calls:

1. **Reverse edge** (who points at these?): `references=` with the whole target list, bounded by
   `types=`. A `!` before an entry negates it; `["@<absolute path>"]` reads the list from a file.

   ```
   housecarl_records(types=["COBJ"], references=["012EB7:Skyrim.esm"])
   → TemperWeaponIronSword, RecipeWeaponIronSword, … (every recipe touching Iron Sword, one scan)
   ```

2. **Contents**: a `formids=` read on the matches, with the depth the paths need. On that lane the
   cost is the LIST's length, not the window's — the ids are read before `limit=` and `offset=`
   apply, so pass fewer ids rather than paging.

   ```
   housecarl_records(
       formids=[…those COBJs…], format="json",
       project={"form":"fields", "depth":5,
                "fields":["EditorID","Items","WorkbenchKeyword","CreatedObject","Conditions"]})
   ```

The load-bearing COBJ paths, only visible at depth 4–5 (live-verified against vanilla):

- materials: `Items[i].Item.Item` (the ingredient link) + `Items[i].Item.Count`
- station: `WorkbenchKeyword` — a Keyword link
- product: `CreatedObject` + `CreatedObjectCount`
- gates: `Conditions[i].Data.Function` names the condition kind; `Conditions[i].Data.Perk` carries
  the perk link when the function is `HasPerk`

A link filter needs no post-filtering: `where=["WorkbenchKeyword = 088108:Skyrim.esm"]` takes a wire
token, and `where=["Conditions[*any].Data.Function = HasPerk"]` folds a list into a boolean.

## The identity-annotation cost rule

Annotating every FormLink inline costs **one winner lookup per link**. On a 66,856-row catalogue
that is 66,856 lookups: the measured control run of exactly this job sent no response for 1,800 s,
the client aborted it, and it left no partial artifact to salvage.

Two ways out, in order of preference:

- **Read identity separately and join locally.** The identity catalogue for the distinct link
  targets is small — a few thousand rows against tens of thousands — and joining is free.
- **Bound the set first**, so the annotated render is one you can afford.

Identity annotations resolve against the **live load order**: reading a vanilla body on a modded
order labels a link with whatever mod now owns that FormID. That is the truthful in-game identity,
but a deliverable documenting an earlier plugin's era has to resolve names from that era instead.
