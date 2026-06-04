# Ingredient Patcher — INGR

`SkyPatcher/ingredient/` · `iEnableIngredientPatching` · primary filter `filterByIngs`
Shared mechanics → `grammar-core.md`. Shared enums → `value-tables.md`.

## Filters

- `filterByIngs` / `…Excluded` — target ingredients by form.
- `filterByModNames` — restrict by source plugin.
- `filterByAlternateTextures` — match items carrying a given texture set.
- `filterByKeywords` / `…Or` / `…Excluded`.
- `filterByMgefs` / `…Or` / `…Excluded`.

## Operations

- `fullName=~Name~`.
- `keywordsToAdd` / `keywordsToRemove`.
- `mgefsToAdd` / `mgefsToChange` / `mgefsToRemove` — see `grammar-core.md` §7.
- `weight=<n>` · `weightMult=<n>` · `value=<n>` (gold value) · `valueMult=<n>`.
- `clear=true` — remove all magic effects.
- `model=<form|path>`.
- `alternateTexturesToAdd=TextureSet~Name3D~Index3D` · `alternateTexturesToRemove` · `alternateTexturesClear=true`.

## Examples

```ini
filterByMgefs=Skyrim.esm|397E:mgefsToAdd=Skyrim.esm|B8587~20~5~0
filterByAlchs=Skyrim.esm|366BF:mgefsToChange=Skyrim.esm|397E~null~10~null~null
weight=0.5
value=25
clear=true
```
