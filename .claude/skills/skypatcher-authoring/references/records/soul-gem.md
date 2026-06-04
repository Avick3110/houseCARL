# Soul Gem Patcher — SLGM

`SkyPatcher/soulGem/` · `iEnableSoulGemPatching` · primary filter `filterBySoulGems`
Shared mechanics → `grammar-core.md`. Shared enums → `value-tables.md` (soul types).

## Filters

- `filterBySoulGems` / `…Excluded` — target soul gems by form.
- `filterByModNames` — restrict by source plugin.
- `filterByAlternateTextures` — match gems carrying a given texture set.
- `filterByKeywords` / `…Or` / `…Excluded`.

## Operations

- `fullName=~Name~`.
- `weight` · `weightMult` · `value` · `valueMult`.
- `keywordsToAdd` / `keywordsToRemove`.
- `pickUpSound=<form>` · `putDownSound=<form>`.
- `currentSoul=<soulType>` — the soul currently held (→ `value-tables.md`).
- `soulCapacity=<soulType>` — the gem's capacity; `null` sets to empty.
- `model=<form|path>` · `alternateTexturesToAdd=TextureSet~Name3D~Index3D` · `alternateTexturesToRemove` · `alternateTexturesClear=true`.

## Examples

```ini
filterBySoulGems=Skyrim.esm|13988:currentSoul=common
filterBySoulGems=Skyrim.esm|13988:soulCapacity=null
filterBySoulGems=Skyrim.esm|12EB7, Skyrim.esm|13790:value=99:weight=0:keywordsToAdd=myMod.esp|223
```
