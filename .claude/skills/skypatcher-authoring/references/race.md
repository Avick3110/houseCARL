# Race Patcher — RACE

`SkyPatcher/race/` · `iEnableRacePatching` · primary filter `filterByRaces`
Shared mechanics → `grammar-core.md`.

For attack-data edits attached via race, see `records/race-hook.md` (different folder).

## Filters

- `filterByRaces` / `…Excluded` — target races by form.
- `filterByModNames` — by source plugin.
- `filterByKeywords` / `…Or` / `…Excluded`.
- `filterByMaleModelContains=<text>` / `filterByFemaleModelContains=<text>` — match by a substring
  of the race's male/female skeleton/model path (e.g. `Character Assets, skeleton.nif`).
- `filterByVoiceTypeOr=<form,…>` — by voice type (OR).

## Operations

Each value op below also has a `…Mult` multiply variant (e.g. `baseMassMult`,
`startingHealthMult`, `regenHealthMult`).

- `heightMale` / `heightFemale` · `weightMale` / `weightFemale`.
- `baseMass` · `baseCarryweight`.
- `startingHealth` · `startingStamina` · `startingMagicka`.
- `regenHealth` · `regenStamina` · `regenMagicka`.
- `damageUnarmed` · `reachUnarmed`.
- `accelerationRate` · `decelerationRate` · `accelerationAngularRate` · `aimAngleTolerance`.
- `skin=<form>`.
- `keywordsToAdd` / `keywordsToRemove`.
- `spellsToAdd` / `spellsToRemove` · `levSpellsToAdd` / `levSpellsToRemove` ·
  `shoutsToAdd` / `shoutsToRemove`.

## Examples

```ini
filterByRaces=Skyrim.esm|D53:baseMass=1.2
filterByRaces=Skyrim.esm|D53:baseCarryweight=999
filterByRaces=Skyrim.esm|D53:startingHealth=250
filterByRaces=Skyrim.esm|D53:regenHealthMult=0.5
filterByRaces=Skyrim.esm|D53:skin=Skyrim.esm|16EE3
filterByRaces=Skyrim.esm|D53:spellsToAdd=Skyrim.esm|6F6FE, Skyrim.esm|C5A9F
filterByRaces=Skyrim.esm|D53:keywordsToAdd=Skyrim.esm|13796, Skyrim.esm|13795
```
