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
toggles in `SkyPatcher.ini` (`placement-and-conflicts.md` §6). This corpus documents 27 of those
types; the one gap is Object Modification (OMOD) — see `object-modification.md`.

---

## 2. File system & discovery

Moved to `placement-and-conflicts.md`: the SkyPatcher folder tree, the per-type subfolder and its
casing, the two INI filename behaviours, the mod-manager same-path collision, and filename-order
conflict resolution. The record type -> subfolder mapping is the router table in `SKILL.md`.

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
| `filterBy<Type>s` / `…Excluded` | The record's **primary filter** (e.g. `filterByWeapons`). See the router table in `SKILL.md`. |
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

Moved to `placement-and-conflicts.md` §6: the `[Patcher]` per-type toggles, `[Log]`, and the
`[Features]` switches (`iAllowLeveledListsAddedToContainers`, `iUpdateNPC`, `iRefreshNPCStats`,
`iUpdateRefs`, …) that decide whether a patch takes effect on an existing save.
