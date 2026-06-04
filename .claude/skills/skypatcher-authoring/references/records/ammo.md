# Ammo Patcher — AMMO

`SkyPatcher/ammo/` · `iEnableAmmoPatching` · primary filter `filterByAmmos`
Shared mechanics → `grammar-core.md`.

## Filters

- `filterByAmmos` — target ammo by form.
- `filterByModNames` — restrict by source plugin.
- `filterByAlternateTextures` — match ammo carrying a given texture set.
- `filterByEditorIdContains` / `…Or` / `…Excluded`.
- `filterByWeightLessThan=<n>` — match ammo lighter than N (e.g. to zero out arrow weight).
- `hasPlugins` / `hasPluginsOr`.
- `restrictToBolts=<true|false>` — `true` = bolts only; `false` = non-bolts (arrows).

## Operations

- `fullName=~Name~`.
- `attackDamage=<n>` — damage the ammo adds to the weapon · `attackDamageMult` · `attackDamageToAdd`.
- `range=<n>` · `rangeMult` · `speed` · `speedMult` · `gravity` · `gravityMult`.
- `weight` · `weightMult` · `value` · `valueMult`.
- `setNewProjectile=<form>` — change the ammo's projectile.
- `keywordsToAdd` / `keywordsToRemove`.
- `model=<form|path>` · `alternateTexturesToAdd=TextureSet~Name3D~Index3D` · `alternateTexturesToRemove` · `alternateTexturesClear=true`.
- `setFlags` / `removeFlags` — flags: `ignoresnormalweaponresistance`, `nonplayable`, `nonbolt`.

## Examples

```ini
filterByWeightLessThan=1:weight=0
filterByAmmos=Skyrim.esm|13988:range=9001
filterByKeywords=Skyrim.esm|000917E7:attackDamage=25
filterByKeywords=Skyrim.esm|000917E7:setNewProjectile=myMod.esp|331155
restrictToBolts=false
filterByAmmos=Skyrim.esm|6BBD2:setFlags=nonplayable
```
