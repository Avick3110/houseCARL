# Spell Patcher — SPEL

`SkyPatcher/spell/` · `iEnableSpellPatching` · primary filter `filterBySpells`
Shared mechanics → `grammar-core.md`. Shared enums → `value-tables.md`.

## Filters

- `filterBySpells` / `…Excluded` — target spells by form. (AND / exclude.)
- `filterByModNames` — restrict by source plugin.
- `filterByKeywords` / `…Or` / `…Excluded` — by attached keyword.
- `filterByMgefs` / `…Or` / `…Excluded` — by attached magic effect.
- `filterByEditorIdContains` / `…Or` / `…Excluded` — EditorID substring.
- `restrictToCastingType=<castType>` — narrow to a cast type (post-match). Values → `value-tables.md` (cast type).
- `filterByFlags` / `…Or` / `…Excluded` — by spell flags (AND / OR / exclude). Flags below.

**Spell flags** (also the legal values for `setFlags`/`removeFlags`):

```
costoverride   fooditem      extendduration   pcstartspell   instantcast
ignoreloscheck ignoreresistance   noabsorb    nodualcastmods
```

## Operations

- `fullName=~Name~` — rename.
- `setFlags` / `removeFlags` — comma-separated, from the spell-flags list above.
- `keywordsToAdd` / `keywordsToRemove`.
- `mgefsToAdd` / `mgefsToChange` / `mgefsToChangeAdd` / `mgefsToRemove` — add/modify/remove
  magic effects. The `~Magnitude~Duration~Area[~…]` shape is in `grammar-core.md` §7.
- `baseCost=<n>` — base magicka cost.
- `halfCostPerk=<form>` — half-cost perk.
- `castType=<castType>` — see `value-tables.md`.
- `chargeTime=<n>`.
- `clear=true` — remove all magic effects.

## Examples

```ini
filterByFlags=instantcast, ignoreloscheck
filterByFlagsOr=instantcast, noabsorb
filterByFlagsExcluded=fooditem, extendduration

filterByMgefs=Skyrim.esm|397E:mgefsToAdd=Skyrim.esm|B8587~20~5~0
filterByMgefs=Skyrim.esm|397E:mgefsToAdd=Skyrim.esm|B8587~20~5~0~sortFirst
filterBySpells=Skyrim.esm|366BF:mgefsToChangeAdd=Skyrim.esm|397E~10~5~null
mgefsToRemove=Skyrim.esm|397E

setFlags=costoverride, fooditem, extendduration, pcstartspell, instantcast, ignoreloscheck, ignoreresistance, noabsorb, nodualcastmods
baseCost=122
castType=fireandforget
chargeTime=2.5
clear=true
```
