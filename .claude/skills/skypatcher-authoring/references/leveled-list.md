# Leveled List Patcher — LVLI / LVLN

`SkyPatcher/leveledList/` · `iEnableLeveledListPatching`
Primary filters: `filterByLLs` (item lists, LVLI) · `filterByLLNPCs` (character lists, LVLN)
Shared mechanics → `grammar-core.md`.

## Filters

**Item lists (LVLI):**
- `filterByLLs` — target item leveled lists by form.
- `noFilterLL=true` — apply to all item lists (role *all*). Only `true` or `yes` work; every
  other value, also `1` or `on`, does nothing. Lists with more than 120 entries are skipped.
- `filterByEditorIdContains` / `…Or` / `…Excluded`.

**Character lists (LVLN):**
- `filterByLLNPCs` — target character leveled lists by form.
- `noFilterLLNPC=true` — apply to all character lists (same values and 120-entry rule).

A line without filters patches nothing here. The Leveled List patcher does not read
`filterByModNames`. Lists named in `filterByLLs` are always patched.

## Operations (shared by item & character lists)

- `addToLLs=<form~level~count>` — add an entry at a level with a count.
- `addOnceToLLs=<form~level~count>` — add only if not already present.
- `removeFromLLs=<form[~level~count]>` — remove entries. Supports advanced operators
  `<`, `>`, `<=`, `>=` on level/count; use `none` to skip a slot.
- `removeObjectsByKeyword=<form>` — remove entries by keyword.
- `objectMultCount=<form~mult>` or `=<mult>` — multiply entry counts.
- `formsToReplace=<formA~formB>` — replace in place. **FormID only.** (Note: FormList/Outfit
  write this op's pair with `=`; Leveled List uses `~`.)
- `chanceNone=<n>` — set the list's Chance None.
- `chanceGlobal=<form>` — set the chance global.
- `clear=yes` — clear all entries.
- **Flags (only one at a time):** `calcForLevel=true` (calc from all levels ≤ player),
  `calcEachItem=true` (calc for each item in count), `calcLevelAndEachItem=true` (both),
  `calcUseAll=true` (use all), `clearFlags=true` (remove all flags).

> Adding leveled lists to **containers** can CTD for some users; the global
> `iAllowLeveledListsAddedToContainers` (in `SkyPatcher.ini`) is **on by default** (1, since
> 7.1.3); 0 skips them. See `container.md`.

- `addTemplateKeywords` is read but does nothing in this version.
- Since 8.0.1 a leveled list is changed only once per line, and `removeFromLLs` works with `<=`
  and `>=`.

## Examples

```ini
filterByLLs=myTestESP.esp|00001733:addToLLs=Skyrim.esm|4822~1~1
filterByLLs=myTestESP.esp|00001733:addOnceToLLs=Skyrim.esm|4822~1~1
filterByLLs=myTestESP.esp|00001733:removeFromLLs=Skyrim.esm|39BE4~none~>3   ; count > 3
filterByLLs=myTestESP.esp|00001734:removeFromLLs=Skyrim.esm|39BE4~<=3~none  ; level <= 3
filterByLLs=myTestESP.esp|00001733:removeObjectsByKeyword=Skyrim.esm|1E715 ; e.g. all bows
filterByLLs=Skyrim.esm|246EE7:formsToReplace=Skyrim.esm|59A71~Skyrim.esm|59A72
filterByLLs=myTestESP.esp|00001734:chanceNone=15
filterByLLs=myTestESP.esp|00001734:calcForLevel=true
filterByLLs=myTestESP.esp|00001734:clear=yes
```
