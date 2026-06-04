# Race Hook Patcher — RACE (attack data)

`SkyPatcher/raceHook/` · `iEnableRaceHookPatching` · primary filter `filterByRaces`
Shared mechanics → `grammar-core.md`.

A **separate patcher from the normal Race Patcher** — its INIs go in the `raceHook/` folder.
It edits the **attack data** events attached via race. The NPC patcher's `attackData…` operations
use this same syntax.

## Filters

- `filterByRaces` / `…Excluded` — target races by form.
- `filterByModNames` — by source plugin.
- `filterByKeywords` / `…Or` / `…Excluded`.

## Operations

- `attackDataToAdd=<key=<event> ~ option=value ~ …>` — add an attack-data event. **`key` is
  required.** Omitted options fall back to defaults.
- `attackDataToChange=<key=<event> ~ option=value ~ …>` — change an event. **`key` is required.**
- `attackDataToRemove=<event,…>` — remove events by event name (e.g. `attackstart`).

**Options** (the `~`-separated fields):

```
damagemult      attackchance    attackspell   (Spell; null removes the spell)
attackangle     strikeangle     staggeroffset
attacktype      (Keyword; null removes the keyword)
knockdown       recoverytime    staminamult
setFlags        removeFlags     resetFlags    (flags below; resetFlags clears all)
```

**Defaults** (used when an option is omitted on `attackDataToAdd`):

```
damageMult = 1     attackChance = 1     attackSpell = nullptr
attackAngle = 0    strikeAngle = 50     staggerOffset = 0
attackType = nullptr   knockDown = 0    recoveryTime = 0
staminaMult = 1    flags = none
```

**Flags** (for `setFlags`/`removeFlags` inside an attack-data entry):

```
ignoreweapon  bashattack  powerattack  chargeattack  rotatingattack  continuousattack  overridedata
```

## Examples

```ini
attackDataToAdd=key=attackLeft~damagemult=1~attackchance=1~attackspell=null~attackangle=0~strikeangle=60~staggeroffset=0~attacktype=null~knockdown=0~recoverytime=0~staminamult=1

attackDataToChange=key=attackStart~damagemult=0~setFlags=powerattack=ignoreweapon

attackDataToRemove=attackstart

; Add a left-attack event to many humanoid races:
filterByRaces=Skyrim.esm|00013740,Skyrim.esm|00013741,Skyrim.esm|00013746:attackDataToAdd=key=attackLeft~damagemult=1~attackchance=1~attackspell=null~attackangle=0~strikeangle=60~staggeroffset=0~attacktype=null~knockdown=0~recoverytime=0~staminamult=1
```
