# SkyPatcher Grammar — Core

The shared spine every per-record reference builds on: how SkyPatcher loads INIs, how a
patch string is shaped, how records are addressed, how filters combine, and the operation
conventions that recur across record types. Per-record files in `records/` list the filters
and operations specific to one type and assume this file for the mechanics. Shared value
enumerations (cast types, actor values, biped slots, …) live in `value-tables.md`.

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
toggles in `SkyPatcher.ini` (§8). This corpus documents 27 of those types; the one gap is Object
Modification (OMOD) — see `records/object-modification.md`.

---

## 2. File system & discovery

```
Data/
└── SKSE/
    └── Plugins/
        ├── SkyPatcher.dll
        ├── SkyPatcher.ini            ← global settings (§8)
        └── SkyPatcher/
            ├── npc/                   ← one subfolder per record type
            ├── weapon/
            ├── armor/
            └── … (see table below)
```

- An INI goes in the **subfolder for its record type**. A race patch must live under `race/`,
  a weapon patch under `weapon/`, etc. INIs in the wrong subfolder are read by the wrong patcher
  (or ignored).
- You may **nest freely** inside a type folder to organize by mod:
  `SkyPatcher/npc/MyMod/bandits/file.ini`.
- **Comments** start with `;`.

### Record type → subfolder / toggle / primary filter / signature

| Record type | Subfolder | Toggle (`iEnable…Patching`) | Primary filter | xEdit sig | Reference |
|---|---|---|---|---|---|
| NPC | `npc` | `NPC` | `filterByNpcs` | NPC_ | `records/npc.md` |
| Weapon | `weapon` | `Weapon` | `filterByWeapons` | WEAP | `records/weapon.md` |
| Armor | `armor` | `Armor` | `filterByArmors` | ARMO | `records/armor.md` |
| Ammo | `ammo` | `Ammo` | `filterByAmmos` | AMMO | `records/ammo.md` |
| Spell | `spell` | `Spell` | `filterBySpells` | SPEL | `records/spell.md` |
| Scroll | `scroll` | `Scroll` | `filterByScrolls` | SCRL | `records/scroll.md` |
| Enchantment | `enchantment` | `Enchantment` | `filterByEnchs` | ENCH | `records/enchantment.md` |
| Magic Effect | `magicEffect` | `MagicEffect` | `filterByMgefs` | MGEF | `records/magic-effect.md` |
| Alchemy / Ingestible | `ingestible` | `Ingestible` | `filterByAlchs` | ALCH | `records/alchemy-ingestible.md` |
| Ingredient | `ingredient` | `Ingredient` | `filterByIngs` | INGR | `records/ingredient.md` |
| Book | `book` | `Book` | `filterByBooks` | BOOK | `records/book.md` |
| Misc Item | `misc` | `Misc` | `filterByMiscs` | MISC | `records/misc.md` |
| Soul Gem | `soulGem` | `SoulGem` | `filterBySoulGems` | SLGM | `records/soul-gem.md` |
| Outfit | `outfit` | `Outfit` | `filterByOutfits` | OTFT | `records/outfit.md` |
| FormList | `formList` | `Formlist` | `filterByFormLists` | FLST | `records/formlist.md` |
| Leveled List | `leveledList` | `LeveledList` | `filterByLLs` / `filterByLLNPCs` | LVLI / LVLN | `records/leveled-list.md` |
| Container | `container` | `Container` | `filterByContainers` | CONT | `records/container.md` |
| Constructible Object | `constructibleObject` | `ConstructibleObject` | `filterByCobjs` | COBJ | `records/constructible-object.md` |
| Cell | `cell` | `Cell` | `filterByCells` | CELL | `records/cell.md` |
| Location | `location` | `Location` | `filterByLocations` | LCTN | `records/location.md` |
| Encounter Zone | `encounterzone` | `EncounterZone` | `filterByEncounterZones` | ECZN | `records/encounter-zone.md` |
| Reference | `reference` | `Reference` | `filterByRefs` | REFR | `records/reference.md` |
| Faction | `faction` | `Faction` | `filterByFactions` | FACT | `records/faction.md` |
| Movement Type | `movementType` | `MovementType` | `filterByMovementTypes` | MOVT | `records/movement-type.md` |
| Projectile | `projectile` | `Projectile` | `filterByProjectiles` | PROJ | `records/projectile.md` |
| Race | `race` | `Race` | `filterByRaces` | RACE | `records/race.md` |
| Race Hook | `raceHook` | `RaceHook` | `filterByRaces` | RACE (hook) | `records/race-hook.md` |
| Object Modification | *(see status)* | `ObjectModification` | *(undocumented)* | OMOD | `records/object-modification.md` |

> Subfolder casing is taken from the shipped mod (`constructibleObject`, `formList`,
> `magicEffect`, `movementType`, `raceHook`, `soulGem` are camelCase; `encounterzone` is all
> lowercase). Windows file systems are case-insensitive, but match the shipped casing.

### Two INI filename behaviors

| Filename shape | When it loads |
|---|---|
| `anything.ini` (e.g. `myEdits.ini`) | **Always** loaded. |
| `Plugin.esm.ini` / `Plugin.esp.ini` (name = a plugin filename) | **Only** when that plugin is active in the load order; otherwise skipped. |

The plugin-gated form is how you ship conditional patches: name the file after the plugin
whose records you patch, and it self-disables when that plugin is absent. This is distinct
from the `hasPlugins` filter (§6) — the filename gate decides whether the *file* is read at
all; `hasPlugins` decides whether a *line* applies.

> **Mod-manager collision warning:** two mods that both ship `Skyrim.esm.ini` in the same
> SkyPatcher subfolder will overwrite each other (same path). Always nest plugin-named INIs
> in a mod-specific subfolder: `SkyPatcher/npc/MyMod/Skyrim.esm.ini`.

### Conflict resolution

INIs within a type folder are read in filename order `0`→`z`. If two lines set the **same
field** of the same record, the later-sorted file wins (`zPatch.ini` beats `mPatch.ini`).
Add/remove operations (e.g. `keywordsToAdd`, `formsToAdd`) **accumulate** rather than
overwrite, so multiple INIs can add to the same record without conflict.

---

## 3. Patch string structure

One line = one patch string, built from a **filter** part and an **operation** part, joined
by `:` segments:

```
filter1=val1 : filter2=val2 : op1=val1 : op2=val2
```

- `:` separates every segment (filter or operation).
- Strings are **modular** — include only the segments you need. A line with no operation does
  nothing; a line with no filter targets *all* records of that type (§5).
- Whitespace around values is generally tolerated, but copy FormIDs exactly (§4).

Example (weapon): filter two weapons, change damage and weight, add a keyword:

```ini
filterByWeapons=Skyrim.esm|00012EB7, Skyrim.esm|00013790:attackDamage=99:weight=0:keywordsToAdd=myMod.esp|20000223
```

---

## 4. Addressing — FormID & EditorID

A form is referenced as **`PluginName|FormID`**:

```
Skyrim.esm|0001396B        ; full 8-hex FormID
myMod.esp|800123           ; ESL-flagged plugins keep their full load-indexed FormID
```

- **Copy the full FormID from xEdit or the Creation Kit** to avoid transcription errors.
- Leading zeros can be trimmed (`myMod.esp|08000223` → `myMod.esp|223`), but the full form is
  safest and always correct.
- **EditorID** is accepted in place of `Plugin|FormID` for almost every filter and operation:
  `filterByAmmos=IronArrow:weight=0.5` is equivalent to `filterByAmmos=Skyrim.esm|1397D:weight=0.5`.

### FormID-only operations (EditorID not supported)

| Patcher | Operation |
|---|---|
| NPC | `objectsToAdd`, `factionsToAdd` |
| Outfit | `formsToReplace` |
| FormList | `formsToReplace` |
| Leveled List | `formsToReplace` |

### The player

The player actor is **always excluded** from race and keyword filtering. To patch the player,
use `filterByNpcs=Skyrim.esm|7` **alone** (no other filter).

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

Rules:

- **Different filter families are independent**, but **every filter family present on the line
  must pass** for the operation to run. `filterByKeywords` (AND, 2 keywords) +
  `filterByKeywordsOr` (5 keywords) + `filterByKeywordsExcluded` (10 keywords) all evaluate;
  if the AND group fails, the line fails even when the others pass.
- **No filter set → every record of that type is patched.** (For collection patchers like
  FormList/Outfit, that means *all* lists — usually not what you want.)
- **Multi-value** filters take a comma-separated list: `filterByKeywords=a,b,c`.
- `restrictTo…` filters are a post-match narrowing: when no match is found the record is
  *ignored* rather than failing the whole line. Common forms: `restrictToKeywords`,
  `restrictToFlags`, `restrictToRaces`, `restrictToGender`, `restrictToBipedSlots`,
  `restrictToCastingType`. Per-record files list the ones each type supports.

---

## 6. Common filters (availability varies — see each record file)

| Filter | Meaning |
|---|---|
| `filterBy<Type>s` / `…Excluded` | The record's **primary filter** (e.g. `filterByWeapons`). See the table in §2. |
| `filterByModNames` / `…Excluded` | Restrict to records that come from / aren't from the named plugin(s). |
| `filterByEditorIdContains` / `…Or` / `…Excluded` | Substring match on the record's EditorID. |
| `filterByKeywords` / `…Or` / `…Excluded` | Match by attached keywords. |
| `filterByNameContains` / `…Or` / `…Excluded` | Substring match on the record's full name. |
| `filterByMgefs` / `…Or` / `…Excluded` | Match by attached magic effects (spell/scroll/ench/alch/ingredient/mgef). |
| `filterByAlternateTextures` | Match items carrying a given texture set (ammo/alch/book/ingredient/misc/scroll/soulGem). |
| `hasPlugins` / `hasPluginsOr` | Gate the **line** on the user having plugin(s) in the load order (AND / OR). |

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

`Data/SKSE/Plugins/SkyPatcher.ini` holds three sections:

- **`[Patcher]`** — one `iEnable<Type>Patching=1|0` per record type (all on by default). Turning
  a type off skips its whole subfolder.
- **`[Log]`** — `iEnablelog=0|1`.
- **`[Features]`** — global behaviors. The load-bearing ones:
  - `iAllowLeveledListsAddedToContainers=0` — off by default; LLs added to containers can CTD
    for some users (see `records/container.md` / `records/leveled-list.md`).
  - `iEnableUnlevelNPCs=0` — unlevels NPCs and encounter zones when on.
  - `iEnableSetLevelDirectlyByPCMult=0` — controls how a delevelled NPC's level is computed.
  - `iUpdateNPC=1` — apply NPC changes to already-spawned actors while playing (visuals,
    perks, spells). `iUpdateNPCExclude` + `iUpdateNPCExcludeList` carve out exceptions.
  - `iRefreshNPCStats=1` — refresh NPC stats at runtime when mods are added/updated/removed.
  - `iUpdateRefs=1` — enable REFR patching (see `records/reference.md`).
  - `iUpdateNPCVisualsOnLoad` — 0 none / 1 by function / 2 by disable+enable.

These are user/global settings, not per-patch — a patch author rarely ships them, but should
know `iAllowLeveledListsAddedToContainers` and the `iUpdateNPC`/`iRefreshNPCStats` behaviors
because they change whether a patch takes effect on an existing save.
