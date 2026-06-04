# Encounter Zone Patcher — ECZN

`SkyPatcher/encounterzone/` · `iEnableEncounterZonePatching` · primary filter `filterByEncounterZones`
Shared mechanics → `grammar-core.md`.

(Subfolder is all-lowercase `encounterzone`.) See also the global `iEnableUnlevelNPCs` feature,
which unlevels encounter zones.

## Filters

- `filterByEncounterZones` / `…Excluded` — target encounter zones by form.
- `filterByModNames` — restrict to plugin(s).
- `filterByEditorIdContains` / `…Or` / `…Excluded`.

## Operations

- `minLevel=<n>` — set MinLevel directly · `minLevelAdd=<n>` — add to it · `minLevelMult=<n>` — multiply it.
- `maxLevel=<n>` — set MaxLevel directly · `maxLevelAdd=<n>` — add to it · `maxLevelMult=<n>` — multiply it.
- `location=<form>` — assign a Location record (or `null` to clear).

## Examples

```ini
filterByEncounterZones=Skyrim.esm|0001D4C3, MyMod.esp|0003AB9D:minLevel=20:maxLevel=120
minLevelAdd=5
maxLevelMult=2.0
location=Skyrim.esm|00018A56
```
