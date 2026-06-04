# Alchemy / Ingestible Patcher — ALCH

`SkyPatcher/ingestible/` · `iEnableIngestiblePatching` · primary filter `filterByAlchs`
Shared mechanics → `grammar-core.md`. Shared enums → `value-tables.md`.

Covers ALCH records — potions, food, and drink (ingestibles).

## Filters

- `filterByAlchs` / `…Excluded` — target ingestibles by form.
- `filterByModNames` — restrict by source plugin.
- `filterByAlternateTextures` — match items carrying a given texture set.
- `filterByKeywords` / `…Or` / `…Excluded`.
- `filterByMgefs` / `…Or` / `…Excluded`.
- `filterByEditorIdContains` / `…Or` / `…Excluded`.

## Operations

- `fullName=~Name~`.
- `keywordsToAdd` / `keywordsToRemove`.
- `mgefsToAdd` / `mgefsToChange` / `mgefsToChangeAdd` / `mgefsToRemove` — see `grammar-core.md` §7.
- `weight=<n>` · `value=<n>` (gold value).
- `clear=true` — remove all magic effects.
- `model=<form|path>`.
- `alternateTexturesToAdd=TextureSet~Name3D~Index3D` · `alternateTexturesToRemove` · `alternateTexturesClear=true`.

## Examples

```ini
filterByMgefs=Skyrim.esm|397E:mgefsToAdd=Skyrim.esm|B8587~20~5~0
filterByAlchs=Skyrim.esm|366BF:mgefsToChange=Skyrim.esm|397E~null~10~null~null

; Filter a magic effect on all alchs with effect AlchRestoreHealth and triple the magnitude:
filterByMgefs=Skyrim.esm|0003EB15:mgefsToChange=Skyrim.esm|0003EB15~null~null~null~3

weight=0.5
value=25
clear=true
```
