# Misc Item Patcher — MISC

`SkyPatcher/misc/` · `iEnableMiscPatching` · primary filter `filterByMiscs`
Shared mechanics → `grammar-core.md`.

## Filters

- `filterByMiscs` / `…Excluded` — target misc items by form.
- `filterByModNames` — restrict by source plugin.
- `filterByAlternateTextures` — match items carrying a given texture set.
- `filterByKeywords` / `…Or` / `…Excluded`.
- `filterByEditorIdContains` / `…Or` / `…Excluded`.

## Operations

- `fullName=~Name~`.
- `weight` · `weightMult` · `value` · `valueMult`.
- `keywordsToAdd` / `keywordsToRemove`.
- `pickUpSound=<form>` · `putDownSound=<form>`.
- `model=<form|path>` · `alternateTexturesToAdd=TextureSet~Name3D~Index3D` · `alternateTexturesToRemove` · `alternateTexturesClear=true`.

## Examples

```ini
filterByMiscs=Skyrim.esm|12EB7, Skyrim.esm|13790:value=99:weight=0:keywordsToAdd=myMod.esp|223
```
