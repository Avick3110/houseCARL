# Enchantment Patcher — ENCH

`SkyPatcher/enchantment/` · `iEnableEnchantmentPatching` · primary filter `filterByEnchs`
Shared mechanics → `grammar-core.md`. Shared enums → `value-tables.md`.

Patches object effects / enchantments (ENCH). To change which enchantment an *item* carries,
use the item patcher's `objectEffect` op (Weapon/Armor).

## Filters

- `filterByEnchs` / `…Excluded` — target enchantments by form.
- `filterByModNames` — restrict by source plugin.
- `filterByKeywords` / `…Or` / `…Excluded`.
- `filterByMgefs` / `…Or` / `…Excluded`.
- `filterByEditorIdContains` / `…Or` / `…Excluded`.

## Operations

- `fullName=~Name~`.
- `setFlags` / `removeFlags` — flags: `costoverride`, `extendduration`.
- `keywordsToAdd` / `keywordsToRemove`.
- `mgefsToAdd` / `mgefsToChange` / `mgefsToChangeAdd` / `mgefsToRemove` — see `grammar-core.md` §7.
- `baseCost=<n>`.
- `castType=<castType>` — see `value-tables.md`.
- `chargeTime=<n>`.
- `enchantmentAmount=<n>`.
- `clear=true` — remove all magic effects.

## Examples

```ini
filterByMgefs=Skyrim.esm|397E:mgefsToAdd=Skyrim.esm|B8587~20~5~0~sortFirst
filterByAlchs=Skyrim.esm|366BF:mgefsToChange=Skyrim.esm|397E~null~10~null~null
setFlags=costoverride, extendduration
baseCost=122
chargeTime=2.5
enchantmentAmount=24
clear=true
```

> **Corpus note — one example above is wrong.** The `filterByAlchs=` line is an Ingestible example
> carried over verbatim from the source article; `filterByAlchs` belongs to ALCH
> (`alchemy-ingestible.md`). On an ENCH record the primary filter is `filterByEnchs`. Where an
> example disagrees with the filter list, the filter list and the router table in `SKILL.md` win.
