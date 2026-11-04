# SkyPatcher Grammar — Core

The shared spine every per-record reference builds on: how a patch string is shaped, how records
are addressed, how filters combine, and the operation conventions that recur across record types.
Per-record files (`<type>.md`) list the filters and operations specific to one type and assume this
file for the mechanics. Shared value enumerations (cast types, actor values, biped slots, …) live in
`value-tables.md`; everything about the file rather than the line — folders, filenames, conflicts,
global switches — is in `placement-and-conflicts.md`.

Sections: 1. What SkyPatcher does · 2. File system (moved) · 3. Patch string structure ·
4. Addressing · 5. Filter system · 6. Common filters · 7. Operation conventions ·
8. Global settings (moved).

> **Availability varies by record type.** A filter or operation named here as "common" is
> not guaranteed on every record — each record's reference file is authoritative for what
> that patcher actually documents. When in doubt, the per-record file wins.

---

## 1. What SkyPatcher does

SkyPatcher is an SKSE plugin that edits Bethesda records **at runtime** from plain-text INI
files — no ESP/ESL is produced for the edit itself. Patches are read and applied once at
`kDataLoaded` (game load), in a fixed per-type order. Because edits are applied in memory
over the live load order, two INIs that touch *different* fields of the same record do not
conflict, which is what makes SkyPatcher low-conflict compared to ESP overrides.

The set of record types it can patch is fixed by the DLL and surfaced as `iEnable<Type>Patching`
toggles in `SkyPatcher.ini` (`placement-and-conflicts.md` §6). This corpus documents 28 of those
types (Light is new in 7.1.0); Object Modification (OMOD) has a toggle but "has no effect in
Skyrim" — see `object-modification.md`.

Patchers run in a fixed order, and a later patcher sees the changes of an earlier one: Magic Effect,
Ammo, Weapon, Armor, Race, NPC, Alchemy, Ingredient, Spell, Enchantment, Projectile, Light, Outfit,
Scroll, Misc, Soul Gem, Faction, Constructible Object, Book, Movement Type, Leveled List, Container,
FormList, Reference, Cell, Location, Encounter Zone. The Race Hook patcher runs earlier, while the
races load.

---

## 2. File system & discovery

Moved to `placement-and-conflicts.md`: the SkyPatcher folder tree, the per-type subfolder and its
casing, the two INI filename behaviours, the mod-manager same-path collision, and read-order
conflict resolution (the type folder's own files, then each depth of subfolders, A to Z). The record type -> subfolder mapping is the router table in `SKILL.md`.

---

## 3. Patch string structure

One line = one patch string, built from a **filter** part and an **operation** part, joined
by `:` segments:

```
filter1=val1 : filter2=val2 : op1=val1 : op2=val2
```

- `:` separates every segment (filter or operation).
- Strings are **modular** — include only the segments you need. A line with no operation does
  nothing; a line with no filter targets *all* records of that type, with the exceptions in §5.
- A space after a comma is fine; do not put spaces around `|` (§4). A value can never contain `:`;
  only a name written between `~` and `~` can.
- Write each key once per line: a second copy of the same key is ignored.
- A comment starts with `;`. Since 8.0.1 a comment may also follow a rule on the same line
  (`level=10 ; Nazeem`).
- `null`, `true` and `false` work in any case. `false`, `no`, `0` and `off` stay off. A value that
  is not a number skips only its own key, not the whole line (8.0.1).
- A key the patcher does not know is ignored without a message, so a misspelled filter does
  nothing and the line can then patch every record of its type. Write every key exactly as in the
  record file.

Example (weapon): filter two weapons, change damage and weight, add a keyword:

```ini
filterByWeapons=Skyrim.esm|00012EB7, Skyrim.esm|00013790:attackDamage=99:weight=0:keywordsToAdd=myMod.esp|20000223
```

---

## 4. Addressing — FormID & EditorID

A form is referenced as **`PluginName|FormID`**:

```
Skyrim.esm|0001396B        ; full 8-hex FormID
myMod.esl|FE012800         ; ESL-flagged plugin: only the last three digits count
myMod.esl|800              ; the same form, written with only the last three digits
```

- **Copy the full FormID from xEdit or the Creation Kit** to avoid transcription errors.
- Leading zeros can be trimmed (`myMod.esp|08000223` → `myMod.esp|223`), but the full form is
  safest and always correct.
- For ESL-flagged (light) plugins only the last three digits count: write the whole number or only
  those three digits.
- Do not put spaces around `|`.
- **EditorID** can stand in place of `Plugin|FormID` (`filterByAmmos=IronArrow:weight=0.5`), but
  the game keeps EditorIDs only for keywords, globals, races, quests, voice types, head parts,
  cells, worldspaces, idles and music types. For every other record — NPCs, weapons, armors,
  spells, … — an EditorID needs **powerofthree's Tweaks** (`po3_Tweaks.dll`, Nexus 51073, with
  `[Fixes] bLoadEditorIDs` left on). Without it the EditorID is not found and the entry is skipped
  without a message. The same holds for `filterByEditorIdContains` / `…Or` / `…Excluded`: without
  Tweaks they find nothing for most record types.

### FormID-only operations (EditorID not supported)

| Patcher | Key |
|---|---|
| NPC | `objectsToAdd`, `factionsToAdd` |
| Reference | `filterByRefs`, `filterByRefsExcluded` |

`formsToReplace` in the Outfit and FormList patchers takes `Plugin|FormID` and EditorID.

### The player

In the NPC patcher keyword, race, faction and EditorID filters never find the player, and a line
without filters skips him too. Outfit (`filterByDefaultOutfits`), class, combat style and template
filters can find him, and naming him in `filterByNpcs=Skyrim.esm|7` patches him; other filters may
sit on the same line. The Excluded keys of keywords, factions and EditorIDs never remove the
player; `filterByNpcsExcluded`, `filterByClassExclude` and restrictions can. `setEssential`,
`setProtected`, `setAutoCalcStats` and `setPcLevelMult` never change the player.
(The NPC article also says the player "is only patched through filterByNpcs"; the filters article's
rule above is the fuller one.)

---

## 5. Filter system

Most filters come in three connectives, by suffix:

| Suffix | Logic |
|---|---|
| `filterByX` | **AND** — every listed value must match. |
| `filterByXOr` | **OR** — at least one listed value must match. |
| `filterByXExcluded` | **NOT** — if any listed value matches, the record is skipped. |

> **Shorthand used in record files.** The per-record references write the three connectives
> compactly as `filterByX` / `…Or` / `…Excluded` — expand `…Or` to the literal token
> `filterByXOr` and `…Excluded` to `filterByXExcluded`. The same `…Mult` shorthand denotes the
> multiply variant of a value operation (`weight` → `weightMult`, `startingHealth` →
> `startingHealthMult`). A few filters use `…Exclude` (no "d") — the record file shows the exact
> spelling.

### How the filters of one line combine

Every filter key has one of five roles. Where a record file does not mark a key's role, this table gives it.

| Role | Keys | What it does |
|---|---|---|
| **direct** | the type's own `filterBy<Type>s` (the router table's primary filter) | These records are always patched. Excluded keys, restrictions and `filterByModNames` do not apply to them. |
| **finder** | every other `filterByX` / `filterByXOr` | Finds records on its own. |
| **narrower** | `restrictTo…`, every `…Excluded` / `…Exclude`, `filterByModNames`, `skipRecordBy…`, `modNamesLastOverridden…`; NPC `filterByGender`, `filterByPCLevelMult`, `filterByAutoCalc`, `filterByEssential`, `filterByProtected`, `filterByTemplateFlagExclude` | Never finds records; only removes them from what the other filters found. |
| **gate** | `hasPlugins` / `hasPluginsOr` (Weapon, Armor and Ammo only) | When the plugins are missing, the line patches nothing, its direct list included. |
| **all** | `noFilterLL`, `noFilterLLNPC` (Leveled List) | Finds every leveled item list / leveled NPC list. |

The order of checks, for one record against one line:

1. **Gate:** `hasPlugins` (all present) / `hasPluginsOr` (one present) fails → nothing.
2. **Direct list:** the record is named there → patched. Nothing else is checked for it.
3. **`filterByModNames`:** records added by other plugins are out.
4. **Finders:** the record must match at least one finder. A second filter does not narrow the
   first one, it adds its own records: `filterByRaces=<Nord>:filterByFactions=<Bandit>` patches all
   Nords **and** all bandits, not only Nord bandits.
5. **No finder and no direct list on the line:** every record passes.
6. **Narrowers:** each one can only remove records; several restrictions on one line must all
   match.

A line with only a direct list patches only those records; a direct list next to other filters
patches the named records plus what the other filters find. A record is patched once per line,
even when several filters find it.

Within one filter family the AND key and the Or key work as a pair: when a line has both
`filterByKeywords` and `filterByKeywordsOr`, a record must match both.

When none of the records a filter names exist, that filter finds nothing. A missing keyword in
`filterByKeywords` does not stop the other keywords from working.

**Multi-value** filters take a comma-separated list: `filterByKeywords=a,b,c`.

### Differences between patchers

| Patcher | Rule |
|---|---|
| Leveled List, Reference | A line without filters patches nothing. Leveled List uses `noFilterLL=true` for all leveled item lists and `noFilterLLNPC=true` for all leveled NPC lists. |
| NPC | A line without filters skips the player (§4). |
| Cell | A line without any filter patches all cells (8.0.1). |
| Cell, Leveled List, Reference | Do not read `filterByModNames`. |
| FormList | `filterByModNames` also limits the direct list. |
| Cell | `filterByCellsExcluded`, `skipRecordByLightingTemplateFromMod` and `skipRecordByModNameContains` also skip cells of `filterByCells`. |
| Race Hook | The Excluded keys and `filterByModNames` also remove races of `filterByRaces`. |
| Reference | `filterByRefsExcluded` removes references from `filterByRefs` on the same line. |
| Leveled List | `noFilterLL`, `noFilterLLNPC` and the EditorID filters skip lists with more than 120 entries; lists named in `filterByLLs` are always patched. |
| NPC | Keyword filters also look at the keywords of the NPC's race. |
| Spell | Keyword filters also look at the keywords of the spell's magic effects. |
| Magic Effect | `filterByMgefs` is the direct list; in other patchers it finds records that use these magic effects. |
| Constructible Object | `filterByKeywords` looks at the keywords of the created item; `filterByIngredients` looks at the items of the recipe. |

Soul gems, lights and projectiles have no keyword filters.

---

## 6. Common filters (availability varies — see each record file)

| Filter | Meaning |
|---|---|
| `filterBy<Type>s` / `…Excluded` | The record's **primary filter** (e.g. `filterByWeapons`). See the router table in `SKILL.md`. |
| `filterByModNames` | Narrower: only patches records added by these plugins. Write the names with the exact upper and lower case of the file names. Not read by Cell, Leveled List and Reference; `…Excluded` exists only in Magic Effect. |
| `filterByEditorIdContains` / `…Or` / `…Excluded` | Substring match on the record's EditorID. |
| `filterByKeywords` / `…Or` / `…Excluded` | Match by attached keywords. |
| `filterByNameContains` / `…Or` / `…Excluded` | Substring match on the record's full name. |
| `filterByMgefs` / `…Or` / `…Excluded` | Match by attached magic effects (spell/scroll/ench/alch/ingredient/mgef). |
| `filterByAlternateTextures` | Match items carrying a given texture set (ammo/alch/book/ingredient/misc/scroll/soulGem). |
| `hasPlugins` / `hasPluginsOr` | Gate the **line** on the user having plugin(s) in the load order (AND / OR), the direct list included. Weapon, Armor and Ammo only. |

**Override-aware filters** (advanced, on a few types): `modNamesLastOverriddenExcluded` (skip
records whose last override is from a named mod — Magic Effect), `skipRecordByModNameContains`
and `skipRecordByLightingTemplateFromMod` (Cell). These let a patch yield to other mods'
overrides instead of fighting them.

> `filterByModNames` (filters records **by their source plugin**) is different from `hasPlugins`
> (gates the line on a plugin merely **being present**) and from the `Plugin.esp.ini` filename
> gate (decides whether the file loads at all).

---

## 7. Common operation conventions

Per-record files list each type's actual operations; these are the recurring *shapes*:

- **Set a value:** `prop=value` (e.g. `weight=1.5`, `baseCost=122`).
- **`…Mult` — multiply** the current value: `weightMult=0.5`, `attackDamageMult=2`.
- **`…ToAdd` — add to** the current value: `attackDamageToAdd=35`.
- **`…Match` / `mirror…` — copy from another form:** `damageResistMatch=Skyrim.esm|0001396B`,
  `dwMatch=…` (damage+weight), `modelMatch=…`, `mirrorArmor=…`, `mirrorWeapon=…`.
- **`fullName=~New Name~`** — rename. The new name is wrapped in `~…~`.
- **`null`** — clear a form-valued field: `objectEffect=null`, `musicType=null`, `perkToApply=null`.
- **`setFlags` / `removeFlags`** — comma-separated flag names; the legal flags are per record
  (see each file). Race Hook also has `resetFlags`.
- **`keywordsToAdd` / `keywordsToRemove`** — comma-separated keyword forms.
- **Object bounds:** `minX` `minY` `minZ` `maxX` `maxY` `maxZ`.
- **Model + textures:** `model=Plugin|id` (or a `.nif` path), plus the alternate-texture family
  `alternateTexturesToAdd=TextureSet~Name3D~Index3D`, `alternateTexturesToRemove=…`,
  `alternateTexturesClear=true`.

### Collection operations (lists, containers, recipes, leveled lists, outfits, formlists)

| Op | Shape | Meaning |
|---|---|---|
| `formsToAdd` / `formsToRemove` | `form, form` | Add/remove entries (FormList, Outfit). |
| `formsToReplace` | `formA~formB` *(FormID only)* | Replace in place. |
| `addToX` / `addOnceToX` | `obj~count` (containers) · `obj~level~count` (LLs) · `item~count` (cobj) | Add (the `Once` form skips if already present). |
| `removeFromX` | `obj` (+ optional `~level~count`, operators `<,>,<=,>=`) | Remove matching entries. |
| `removeFromXByCount` | `obj~count` | Remove a specific count. |
| `replaceInX` | `formA~formB` | Replace all instances, count preserved. |
| `objectMultCount` | `obj~mult` or `mult` | Multiply entry counts. |
| `clear` | `=true` / `=yes` | Empty the list/container/recipe. |

### The `~` sub-argument separator

Compound operations pack several arguments per value with `~`. The recurring ones:

```
mgefsToAdd       = Form|id ~ Magnitude ~ Duration ~ Area [ ~ sortFirst ]
mgefsToChange    = Form|id ~ Magnitude ~ Duration ~ Area ~ MagnitudeMult     (use null to skip a slot)
mgefsToChangeAdd = Form|id ~ Magnitude ~ Duration ~ Area
attackDataToAdd  = key=<event> ~ damagemult=1 ~ attackchance=1 ~ … (key required)
addToLLs         = Form|id ~ level ~ count
addToContainers  = Form|id ~ count
addToCobjs       = Form|id ~ count
alternateTexturesToAdd = TextureSet ~ Name3D ~ Index3D
```

Set a sub-slot to `null` to leave it unchanged where the op supports it (e.g.
`mgefsToChange=Skyrim.esm|397E~null~10~null~null` changes only Duration).

---

## 8. Global settings — `SkyPatcher.ini`

Moved to `placement-and-conflicts.md` §6: the `[Patcher]` per-type toggles, `[Log]`, and the
`[Features]` switches (`iAllowLeveledListsAddedToContainers`, `iUpdateNPC`, `iRefreshNPCStats`,
`iUpdateRefs`, …) that decide whether a patch takes effect on an existing save.
