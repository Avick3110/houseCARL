# Location Patcher — LCTN

`SkyPatcher/location/` · `iEnableLocationPatching` · primary filter `filterByLocations`
Shared mechanics → `grammar-core.md`.

## Filters

- `filterByLocations` / `…Excluded` — target locations by form.
- `filterByModNames` — restrict to plugin(s) (all listed must be present).
- `filterByKeywords` / `…Or` / `…Excluded`.
- `filterByMusicType=<form>` — by the location's BGSMusicType.
- `filterByParentLocation=<form,…>` — by parent location (any match).
- `filterByUnreportedCrimeFaction=<form>` — by the location's unreported-crime faction.
- `filterByEditorIdContains` / `…Or` / `…Excluded`.

## Operations

- `fullName=~Name~`.
- `keywordsToAdd` / `keywordsToRemove`.
- `musicType=<form>` (or `null`).
- `unreportedCrimeFaction=<form>` (or `null`).
- `parentLocation=<form>` (or `null`).

## Examples

```ini
filterByLocations=Skyrim.esm|00018A56, Dawnguard.esm|0004C89D:fullName=~Ancient Dwemer Ruin~
filterByKeywordsOr=Skyrim.esm|00013797, Skyrim.esm|0001A4F2:keywordsToAdd=MyMod.esp|00012345
filterByLocations=Skyrim.esm|00018A56:musicType=Skyrim.esm|000B46F1
parentLocation=null
```
