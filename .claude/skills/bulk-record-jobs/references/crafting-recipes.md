# Crafting recipes — what the binary format does not say

A COBJ record stores `WorkbenchKeyword` as just a Keyword link. Which station that is, and what the
recipe *means* there, is Creation-Kit convention rather than data. Classify from the fields you
read; never from the EditorID.

## Enumerate stations from data, do not hardcode them

Crafting-station keywords are ordinary KYWD records, so they enumerate like anything else:

```
housecarl_records(types=["KYWD"], plugins={…Skyrim.esm, definitions only…},
                  where=["editorid contains Crafting"])
```

That lists them live — forge, skyforge, sharpening wheel, armor table, smelter, tanning rack,
cookpot. Ignore the `WICrafting*` ones: those are radiant-story event keywords, not stations. Drop
the plugin scope and the same call finds the stations mods add.

## Craft versus temper is structural

- **Craft** (forge, smelter, tanning rack, cookpot): `CreatedObject` is the item produced *from* the
  materials in `Items`.
- **Temper** (sharpening wheel for weapons, armor table for armor): `CreatedObject` is the very item
  being improved, count 1, and the canonical vanilla condition pair is
  `EPTemperingItemIsEnchanted != 1` (OR-flagged) plus `HasPerk(<arcane-smithing-class perk>)`.

Classify by `WorkbenchKeyword` together with that shape, and declare the result as a closed enum in
the deliverable — `craft` / `temper` / `other`.

## The data can be dirty

Vanilla itself ships `TemperWeaponSkyforgeBow`, whose `CreatedObject` is a battleaxe. An EditorID
that disagrees with the structure raises a flag in the deliverable; it does not win the argument.

A recipe that appears at no station — a null or odd `WorkbenchKeyword`, or a gate no condition can
pass — is usually a mod's own disablement idiom. Report the structure; the intent belongs to that
mod's skill lane, not here.

## The per-recipe deliverable entry

The row shape for a crafting-graph job. It obeys the same contract as every other row: wire tokens
verbatim, a resolved link as an object rather than a replacement, closed enums for classifications.

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
