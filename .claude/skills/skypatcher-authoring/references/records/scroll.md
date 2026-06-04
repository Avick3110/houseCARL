# Scroll Patcher — SCRL

`SkyPatcher/scroll/` · `iEnableScrollPatching` · primary filter `filterByScrolls`
Shared mechanics → `grammar-core.md`. Shared enums → `value-tables.md`.

## Filters

- `filterByScrolls` / `…Excluded` — target scrolls by form.
- `filterByModNames` — restrict by source plugin.
- `filterByAlternateTextures` — match scrolls carrying a given texture set.
- `filterByKeywords` / `…Or` / `…Excluded`.
- `filterByMgefs` / `…Or` / `…Excluded`.
- `filterByEditorIdContains` / `…Or` / `…Excluded`.

## Operations

- `fullName=~Name~`.
- `weight=<n>` · `weightMult=<n>` · `value=<n>` · `valueMult=<n>`.
- `setFlags` / `removeFlags` — comma-separated. (Article uses the spell-flag set:
  `costoverride, fooditem, extendduration, pcstartspell, instantcast, ignoreloscheck,
  ignoreresistance, noabsorb, nodualcastmods`.)
- `keywordsToAdd` / `keywordsToRemove`.
- `mgefsToAdd` / `mgefsToChange` / `mgefsToChangeAdd` / `mgefsToRemove` — see `grammar-core.md` §7.
- `baseCost=<n>` · `halfCostPerk=<form>` · `castType=<castType>` · `chargeTime=<n>`.
- `clear=true` — remove all magic effects.
- `model=<form|path>`.
- `alternateTexturesToAdd=TextureSet~Name3D~Index3D` · `alternateTexturesToRemove` · `alternateTexturesClear=true`.

## Examples

```ini
filterByMgefs=Skyrim.esm|397E:mgefsToAdd=Skyrim.esm|B8587~20~5~0~sortFirst
filterByAlchs=Skyrim.esm|366BF:mgefsToChange=Skyrim.esm|397E~null~10~null~null
baseCost=122
castType=scroll

; Model + texture swap (EditorID example):
filterByBooks=Book1CheapBiographyofBarenziahvI:model=clutter\books\BCSSEBookTall.nif:alternateTexturesToAdd=BCS_BookBiographyQueenBarenziah01~Cover~2:inventoryArt=BCS_Book_BQB01
filterByAlternateTextures=BCS_BookBiographyQueenBarenziah01:alternateTexturesToRemove=BCS_BookBiographyQueenBarenziah01
```
