# OAR Config Reference

Exhaustive, source-verified reference for Open Animation Replacer (OAR) config files. Generated
from OAR's own source (`ersh1/OpenAnimationReplacer`, branch `main`) cross-checked against live,
real-world configs. Load this when you need an exact field name, condition name, enum value, or the DAR
legacy grammar. SKILL.md is the playbook; this is the lookup table.

> **OAR version studied:** 3.0.0 (the DLL shipped in the Open Animation Replacer mod, Nexus 92109).
> Source files cited: `src/Parsing.cpp`, `src/ReplacerMods.h`, `src/Conditions.h`,
> `src/Conditions.cpp`, `src/BaseConditions.h`, `src/API/OpenAnimationReplacerAPI-Conditions.h`.

## Table of contents
1. Folder layout
2. Mod-level `config.json` schema
3. Submod-level `config.json` schema
4. Condition object schema + value components
5. `IsEquippedType` enum (authoritative)
6. Built-in condition roster (~120)
7. Addon conditions (Math, RaySense, Detection, Dialogue, IED)
8. DAR legacy compatibility (`_conditions.txt`, the two folder forms)
9. `user.json` overrides
10. Priority & winner resolution
11. Global `OpenAnimationReplacer.ini`
12. FormID & gotchas

---

## 1 — Folder layout

```
<mod>/meshes/actors/<project>/animations/OpenAnimationReplacer/
  <ModName>/                       (a "replacer mod")
    config.json                    (mod-level: name/author/description/conditionPresets)
    user.json                      (optional mod-level override — rare)
    <SubModName>/                  (a "submod" = one conditional animation set)
      config.json                  (submod-level: priority + conditions + the rest)
      user.json                    (optional submod override — written by the editor)
      <project>/<original.hkx>     (replacement animation files, mirroring the original path)
```

- `<project>` is almost always `character` for humanoids (`meshes/actors/character/animations/...`).
  Creatures use their own project folder (`canine`, `draugr`, `dragon`, …).
- The `.hkx` files inside a submod mirror the **original animation's path**; that path-match is how
  OAR knows which base animation the submod can replace. The **conditions** then gate *when*.
- A submod with **no `.hkx`** either uses `overrideAnimationsFolder` (points at another submod's
  files) or exists only to host conditions/variants — this is normal, not a bug.

---

## 2 — Mod-level `config.json` (directly under `OpenAnimationReplacer/<ModName>/`)

Source: `Parsing.cpp` ~L312-L360.

| Key | Type | Meaning |
|---|---|---|
| `name` | string | Display name of the replacer mod (shown in the editor). |
| `author` | string | Author. |
| `description` | string | Description. |
| `conditionPresets` | array | Reusable named condition sets. Each item: `{ "name": "...", "description": "...", "conditions": [ … ] }`. Referenced from a submod by the `PRESET` condition. |

Real example:
```json
{
    "name": "Conditional Armor Type Animations",
    "author": "Verolevi",
    "description": "Dynamically replaces the player and NPCs animations depending on their armor type."
}
```

---

## 3 — Submod `config.json` (under `OpenAnimationReplacer/<ModName>/<SubModName>/`)

Every key the parser reads, source `Parsing.cpp` ~L402-L650. All keys optional except that a
submod is only useful with a `priority` and (usually) `conditions`.

### Core
| Key | Type | Default | Meaning |
|---|---|---|---|
| `name` | string | — | Submod display name. |
| `description` | string | — | Description. |
| `priority` | int32 | 0 | **Higher wins.** Range −2147483648…2147483647. Decides winners, **not** load order. |
| `disabled` | bool | false | Submod is parsed but never applies. |
| `conditions` | array | — | The condition set (see §4). **Lowercase `conditions` at the submod top level.** |

### Animations & variants
| Key | Type | Meaning |
|---|---|---|
| `disabledAnimations` | array | Per-file opt-outs: `[{ "projectName": "...", "path": "..." }]`. |
| `replacementAnimDatas` | array | Per-file variant config (see below). |
| `overrideAnimationsFolder` | string | Use another submod's `.hkx` files instead of shipping duplicates. |
| `requiredBehaviorProjectName` | string | Restrict the submod to a named behavior project. |

`replacementAnimDatas[]` item shape (the randomized-variant system):
```
{ "projectName": "...", "path": "...", "disabled": false,
  "variants": [ { "filename": "a.hkx", "disabled": false, "weight": 1.0, "playOnce": false }, … ],
  "variantMode": 0, "variantStateScope": 0, "blendBetweenVariants": false,
  "resetRandomOnLoopOrEcho": false, "sharePlayedHistory": false }
```
`variantMode` selects random vs sequential; `weight` biases random picks; `playOnce` removes a
variant from the pool after it plays once.

### Triggers / annotations
| Key | Type | Meaning |
|---|---|---|
| `ignoreDontConvertAnnotationsToTriggersFlag` | bool | (legacy alias `ignoreNoTriggersFlag`). |
| `triggersFromAnnotationsOnly` | bool | Only use triggers derived from annotations. |

### Blend / interrupt / loop / echo
| Key | Type | Default | Meaning |
|---|---|---|---|
| `interruptible` | bool | false | Replacement can be interrupted mid-play and re-evaluated. |
| `hasCustomBlendTimeOnInterrupt` / `blendTimeOnInterrupt` | bool / number | — | Custom interrupt blend. |
| `replaceOnLoop` | bool | **true** | Re-evaluate conditions each loop (modern replacement for `keepRandomResultsOnLoop`). |
| `hasCustomBlendTimeOnLoop` / `blendTimeOnLoop` | bool / number | — | Custom loop blend. |
| `replaceOnEcho` | bool | false | Re-evaluate on echo. |
| `hasCustomBlendTimeOnEcho` / `blendTimeOnEcho` | bool / number | — | Custom echo blend. |
| `runFunctionsOnLoop` | bool | true | Run attached functions each loop. |
| `runFunctionsOnEcho` | bool | true | Run attached functions on echo. |

### Paired / synchronized animations
| Key | Type | Meaning |
|---|---|---|
| `pairedConditions` | array | Conditions evaluated against the **partner** actor in a synchronized/paired animation. |

### Deprecated (still parsed for back-compat — do not author new)
| Key | Replaced by |
|---|---|
| `keepRandomResultsOnLoop` | `replaceOnLoop` + per-variant `resetRandomOnLoopOrEcho`. Global fallback: INI `bLegacyKeepRandomResultsByDefault`. |
| `shareRandomResults` | per-variant `sharePlayedHistory`. |

---

## 4 — Condition object schema + value components

Each entry in a `conditions` (or nested `Conditions`) array:

| Key | Type | Meaning |
|---|---|---|
| `condition` | string (**required**) | Condition name, e.g. `"IsEquippedType"`, `"AND"`, or an addon name like `"MathStatement"`. |
| `requiredVersion` | string | Minimum OAR version that provides this condition, e.g. `"1.0.0.0"`. |
| `negated` | bool | Invert the result. |
| `disabled` | bool | Exclude this condition from evaluation. |
| *(component params)* | varies | One key per the condition's named argument(s) — see below. |

**Value component shapes** (the named-argument values):

| Component | JSON shape | Example |
|---|---|---|
| Form | `{ "pluginName": "X.esp", "formID": "0ABCDE" }` **or** `{ "editorID": "SomeID" }` | `"Actor base": { "pluginName": "Skyrim.esm", "formID": "7" }` |
| Keyword | `{ "editorID": "ArmorHeavy" }` or a form | `"Keyword": { "editorID": "ArmorShieldLarge" }` |
| Numeric | `{ "value": 7.0 }` (static). Can also reference a **global variable / actor value / behavior-graph variable / Math expression** — generate those in the editor. | `"Type": { "value": 7.0 }` |
| Bool | a raw `true`/`false` on the named key | `"Left hand": false` |
| Text | a raw string | graph-variable name, or the Math plugin's expression |
| Comparison | operator: one of `==`, `!=`, `>`, `>=`, `<`, `<=` | used by `CompareValues`, `Level`, `Random`, etc. |
| NiPoint3 | a 3-component vector | rare; positional conditions |
| **Multi** (`AND`/`OR`/`XOR`/`PRESET`/`PLAYER`/`TARGET`/`MOUNT`) | `{ "Conditions": [ … ] }` | **capital-C `Conditions`** |

> **Gotcha — two different keys:** the submod's top-level array is **`conditions`** (lowercase),
> but the child array inside a multi-condition (`AND`, `OR`, `XOR`, `PRESET`, `PLAYER`, `TARGET`,
> `MOUNT`) is **`Conditions`** (capital C). Mixing them up silently produces an empty child set.

Verified nested example (war axe in the right hand AND not a shield in the left):
```json
{
    "condition": "AND",
    "requiredVersion": "1.0.0.0",
    "Conditions": [
        { "condition": "IsEquippedType", "requiredVersion": "1.0.0.0",
          "Type": { "value": 3.0 }, "Left hand": false },
        { "condition": "IsEquippedType", "requiredVersion": "1.0.0.0", "negated": true,
          "Type": { "value": 11.0 }, "Left hand": true }
    ]
}
```

**Reference-scope conditions** (`PLAYER`, `TARGET`, `MOUNT`): a multi-condition whose children are
evaluated against a *different* reference — the player, the actor's combat target, or its mount —
instead of the animating actor. Example: "my target is a dragon" = `TARGET { IsRace Dragon }`.

**`PRESET`**: a multi-condition that points at a `conditionPresets` entry defined in the parent
mod-level `config.json`, so several submods can share one condition set.

---

## 5 — `IsEquippedType` enum (authoritative)

The single most-used condition. Mapping is from OAR source `Conditions.cpp`
(`IsEquippedTypeCondition::GetEquippedType` + `GetEnumMap`) — **trust this over the vanilla
`GetEquippedItemType` enum (what most web searches surface), which OAR deliberately differs from at
values 6/9/10/11.**

| value | label | notes |
|---:|---|---|
| -1 | Other | a non-weapon/non-listed form is equipped |
| 0 | Unarmed | hand-to-hand **or** nothing equipped in that hand |
| 1 | Sword | one-handed |
| 2 | Dagger | |
| 3 | War Axe | one-handed axe |
| 4 | Mace | one-handed |
| 5 | Greatsword | two-handed sword |
| 6 | **Battleaxe** | two-handed axe **without** the Warhammer keyword |
| 7 | Bow | |
| 8 | Staff | |
| 9 | **Crossbow** | (not "spell" — spells are 12-16) |
| 10 | **Warhammer** | two-handed axe **with** `WeapTypeWarhammer` keyword |
| 11 | **Shield** | any armor equipped in the hand slot |
| 12 | Alteration Spell | |
| 13 | Illusion Spell | |
| 14 | Destruction Spell | |
| 15 | Conjuration Spell | |
| 16 | Restoration Spell | |
| 17 | Scroll | |
| 18 | Torch | an equipped light |

> **Battleaxe vs Warhammer:** both are engine type `kTwoHandAxe`; OAR splits them by keyword —
> `6` = battleaxe, `10` = warhammer. A moveset that wants both must test `6` **OR** `10`.
> `"Left hand"` selects which hand to inspect (default right = `false`).

---

## 6 — Built-in condition roster (~120)

Authoritative name list from `Conditions.h` (`GetName()` of each condition class). Grouped for
lookup. Each takes its own component params; open the in-game editor (or read an existing config)
to see the exact argument names for a given condition.

**Structural / logical:** `AND`, `OR`, `XOR`, `PRESET`
**Reference scope (evaluate children vs another ref):** `PLAYER`, `TARGET`, `MOUNT`
**Equipment / inventory:** `IsForm`, `IsEquipped`, `IsEquippedType`, `IsEquippedHasKeyword`,
`IsEquippedPower`, `IsEquippedShout`, `IsEquippedHasEnchantment`,
`IsEquippedHasEnchantmentWithKeyword`, `IsWorn`, `IsWornHasKeyword`, `IsWornInSlot`,
`IsWornInSlotHasKeyword`, `EquippedObjectWeight`, `InventoryCount`, `InventoryCountHasKeyword`,
`InventoryWeight`
**Actor identity / traits:** `IsFemale`, `IsChild`, `IsUnique`, `IsPlayerTeammate`, `IsActorBase`,
`IsRace`, `IsClass`, `IsCombatStyle`, `IsVoiceType`, `IsGuard`, `IsSummoned`, `IsGhost`,
`IsInFaction`, `FactionRank`, `HasKeyword`, `HasPerk`, `HasSpell`, `HasMagicEffect`,
`HasMagicEffectWithKeyword`, `Level`, `Scale`, `Height`, `Weight`
**Combat / action state:** `IsAttacking`, `AttackState`, `IsBlocking`, `IsCombatState`,
`IsInCombat`, `IsWeaponDrawn`, `CastingSpell`, `CurrentCastingType`, `CurrentDeliveryType`,
`IsStaggered`, `IsAttackTypeKeyword`, `IsAttackTypeFlag`, `IsCrimeSearching`, `IsCombatSearching`
**Movement / locomotion:** `IsRunning`, `IsSneaking`, `IsSprinting`, `IsInAir`,
`IsMovementDirection`, `MovementSpeed`, `CurrentMovementSpeed`, `CurrentRotationSpeed`,
`IsSwimming`, `IsAboveWater`, `SubmergeLevel`, `IsOnStairs`, `MovementSurfaceAngle`,
`SurfaceMaterial`, `FallDistance`, `FallDamage`
**Targeting (TDM-style):** `HasTarget`, `CurrentTargetDistance`, `CurrentTargetRelationship`,
`CurrentTargetRelativeAngle`, `CurrentTargetLineOfSight`
**Mounted:** `IsOnMount`, `IsRiding`, `IsRidingHasKeyword`, `IsBeingRidden`, `IsBeingRiddenBy`
**Furniture / idle / life:** `CurrentFurniture`, `CurrentFurnitureHasKeyword`, `IdleTime`,
`SitSleepState`, `LifeState`
**World / location / weather / crime:** `IsInInterior`, `IsInLocation`, `LocationHasKeyword`,
`LocationCleared`, `HasRefType`, `IsParentCell`, `IsWorldSpace`, `CurrentWeather`,
`CurrentWeatherHasFlag`, `WindSpeed`, `WindAngleDifference`, `CurrentGameTime`, `LightLevel`,
`IsTrespassing`, `CrimeGold`, `IsOverEncumbered`
**Package / AI:** `IsCurrentPackage`, `CurrentPackageType`
**Dialogue / scene / menu:** `IsTalking`, `IsGreetingPlayer`, `IsInScene`, `IsInSpecifiedScene`,
`IsScenePlaying`, `IsDoingFavor`, `IsMenuOpen`
**Quest:** `IsQuestStageDone`
**Values / graph / misc:** `CompareValues`, `Random`, `HasGraphVariable`,
`MagicEffectElapsedTime`, `IsReplacerEnabled`

> Some names (e.g. `IsInScene`, `IsTalking`) were once addon-only and are now core. The editor is
> the live source of truth for *which* conditions exist in a given install (core + whatever addons
> are present).

---

## 7 — Addon conditions (extend OAR's logic)

Addons are **separate SKSE plugins** that register custom conditions through OAR's API
(`OAR_API::Conditions`, interface V3). A plugin calls `AddCustomCondition<T>()` at/before SKSE
`kMessage_PostLoad`; after that OAR finalizes its factory map. Each custom condition has a
`CONDITION_NAME` and then appears in the editor and is usable in `config.json` by that name.

**Common condition addons** (each a separate SKSE plugin under `*/SKSE/Plugins/`):

| Addon | DLL | Nexus | Adds |
|---|---|---|---|
| Math Plugin | `OpenAnimationReplacer-Math.dll` | 92607 | **`MathStatement`** — write an expression (e.g. `x + y > 20`); each variable becomes a numeric value component; the condition is true when the result ≠ 0. Brings arithmetic/boolean math the built-ins lack. |
| RaySense | `OpenAnimationReplacer-RaySense.dll` | 175498 | Ray-cast conditions (obstacle/ledge/wall detection in front of the actor). Used by parkour/vault mods (e.g. "RaySense - Jumping over obstacles"). |
| Detection Plugin | `OpenAnimationReplacer-DetectionPlugin.dll` | 104806 | Stealth/detection-sensor conditions. |
| Dialogue Plugin | `OpenAnimationReplacer-DialoguePlugin.dll` | — | Dialogue/conversation-state conditions. |
| IED Conditions | `OpenAnimationReplacer-IEDConditionExtensions.dll` | 98308 | Immersive Equipment Displays conditions (e.g. `IED_GearNodePlacementHint`). |

> **Missing-addon behavior (hard dependency):** a `config.json` references an addon condition by
> **name only**. If the providing DLL isn't installed, OAR's `CreateCondition(name)` fails and
> substitutes an **INVALID** condition (`"! INVALID !"` / "The condition was not found!"). The
> submod still loads, but that line never evaluates as intended and the editor/Detected-Problems UI
> flags it. So an OAR mod that uses `MathStatement`, `IED_*`, RaySense, etc. **requires** its addon.

---

## 8 — DAR legacy compatibility

OAR reads **Dynamic Animation Replacer** layouts at runtime and converts each into an in-memory
"Legacy" submod that competes in the same priority space as native OAR. Two forms (source
`Parsing.cpp`, `ConfigSource::kLegacy` / `kLegacyActorBase`):

### Form A — `_CustomConditions/<priority>/` (with `_conditions.txt`)
```
…/animations/DynamicAnimationReplacer/_CustomConditions/<priority>/
    _conditions.txt
    <project>/<original.hkx>
```
- The **folder name is the priority** (integer; higher wins; same global priority space as OAR).
- `_conditions.txt` grammar: `(NOT) FunctionName("Plugin.esp" | 0xFormID, args…) (AND | OR) …`,
  one logical chain (DAR has **no parentheses grouping** — a real limitation OAR's nested
  `AND`/`OR` fixes). Missing `_conditions.txt` ⇒ the folder is skipped with a warning.
- Common functions: `IsActorBase`, `IsPlayerTeammate`, `IsEquippedRight`, `IsEquippedLeft`,
  `IsEquippedRightType`, `IsEquippedLeftType`, `IsEquippedRightHasKeyword`,
  `IsEquippedLeftHasKeyword`, `IsEquippedShout`, `IsWorn`, `IsWornHasKeyword`, `IsInFaction`,
  `HasKeyword`, `HasMagicEffect`, `HasPerk`, `HasSpell`, `IsClass`, `IsRace`, `IsChild`,
  `IsInInterior`, `IsActorValueEqualTo`, `IsActorValueLessThan`, `ValueEqualTo`, `CurrentWeather`,
  `IsCombatStyle`, `Random`, …

Real-world example (`ADXP I MCO ER Spear …/_CustomConditions/225000/_conditions.txt`):
```
IsEquippedRightHasKeyword("EldenRing_Spear.esp" | 0x00080E) OR
IsEquippedRightHasKeyword("NewArmoury.esp" | 0xE457E) OR
IsEquippedRightHasKeyword("NewArmoury.esp" | 0xE457F) AND
NOT IsEquippedRight("EldenRing_Spear.esp" | 0x00000813) AND
NOT IsEquippedLeftType(1) AND
NOT IsEquippedLeftType(2) AND
NOT IsEquippedLeftType(3) AND
NOT IsEquippedLeftType(4)
```

### Form B — `<Plugin.esp>/<FormID>/` (no `_conditions.txt`)
```
…/animations/DynamicAnimationReplacer/<Plugin.esp>/<8-hex-formid>/
    <project>/<original.hkx>
```
- Per-**actor-base** override (e.g. a unique follower). OAR auto-synthesizes a single
  `IsActorBase(<Plugin.esp>, <FormID>)` condition from the folder names (source `Parsing.cpp`
  ~L1310/L1380, `ConfigSource::kLegacyActorBase`).

**Converting legacy → OAR:** in the in-game editor, a legacy submod can be converted to OAR format
(writes a `config.json`), after which you can edit it normally. Leaving it legacy is fine — it still
loads and competes by priority.

---

## 9 — `user.json` overrides

Source: `ReplacerMods.h` (`enum ConfigSource { kUser, kAuthor, kLegacy, kLegacyActorBase }`,
`enum EditMode { kNone, kUser, kAuthor }`, `IsFromUserConfig()`, `SaveConfig(EditMode)`).

- `config.json` = **author** config (`kAuthor`). `user.json` = **user** override (`kUser`), in the
  **same submod folder** beside `config.json`.
- When `user.json` is present, OAR uses it **instead of** `config.json` for that submod — it is a
  **full-document shadow**, not a per-field merge. So a `user.json` must contain the complete submod
  config you want (priority + the whole `conditions` array + any flags).
- The **in-game editor** writes `user.json` when you edit in **User** mode (it copies the current
  state to `user.json`); **Author** mode rewrites `config.json` (for mod authors shipping a mod).
- Because MO2 overlays files through USVFS, the `user.json` can live in a **separate MO2 mod** that
  loads after the original animation mod. The override then appears beside the original `config.json`
  and wins — letting you keep **all** your OAR tweaks in one mod without editing any original files.
  (A common modlist pattern: one dedicated overrides mod that contains *only* `user.json` files and
  loads after the originals.)

Real-world example — original vs overlay for the same submod
(`Bow Rapid Combo V3 / Base`):
```json
// ORIGINAL  …/Bow Rapid Combo V3 - Archer Combat Overhaul/…/Base/config.json
{ "name": "Base", "priority": 9901000, "keepRandomResultsOnLoop": true,
  "conditions": [
    { "condition": "IsActorBase", "Actor base": { "pluginName": "Skyrim.esm", "formID": "7" } },
    { "condition": "IsEquippedType", "Type": { "value": 7.0 }, "Left hand": false } ] }

// OVERRIDE  …/<your overrides mod>/…/Base/user.json
{ "priority": 9901000,
  "conditions": [
    { "condition": "IsActorBase", "Actor base": { "pluginName": "Skyrim.esm", "formID": "7" } },
    { "condition": "IsEquippedType", "Type": { "value": 7.0 }, "Left hand": false },
    { "condition": "HasPerk", "Perk": { "pluginName": "<the perk-adding mod>.esp",
        "formID": "<local hex>" } } ] }
```
The override is a near-copy that **adds a `HasPerk` gate** so the player only gets that archery
moveset after earning a specific perk — without touching the original mod.

> **Gotcha:** `user.json` matches by **folder path** (`<ModName>/<SubModName>`). If the original
> mod's folder names change, or the overlay's paths drift out of sync, the override silently stops
> matching and the author's `config.json` takes back over.

---

## 10 — Priority & winner resolution

- For each **original** animation, OAR gathers every replacement that targets it across **all**
  submods of **all** replacer mods (native OAR + converted legacy DAR), and sorts them by `priority`
  **descending**.
- **Plugin/ESP load order is irrelevant.** Two animation mods both with submod priority `100000`
  collide regardless of which ESP loads first.
- At runtime, when the original plays on an actor, OAR walks the sorted list top-down and picks the
  **first** submod whose `conditions` are **true** for that actor. Its animation (or a random
  variant) plays. If none pass, the base-game animation plays.
- **Equal priorities are ambiguous** (no load-order tiebreak) — keep priorities unique for
  deterministic winners. This is why authors use large, spread-out integers (`9007010`,
  `83030317`) to slot a submod cleanly between others.

---

## 11 — Global `OpenAnimationReplacer.ini` (`SKSE/Plugins/`)

Controls the whole framework (not per-mod). Representative live values from a real install:
```ini
[General]
uAnimationLimit = 32767
uHavokHeapSize = 1073741824
bAsyncParsing = true
bLoadDefaultBehaviorsInMainMenu = true
[Filtering]
bFilterOutDuplicateAnimations = true
[UI]
bEnableUI = true
uToggleUIKey = 45          ; + uToggleUIKeyCtrl / Shift / Alt — the in-game editor hotkey
fUIScale = 1.000000
bEnableAnimationLog = false
[Workarounds]
bLegacyKeepRandomResultsByDefault = true   ; back-compat for deprecated keepRandomResultsOnLoop
[Experimental]
bDisablePreloading = false
bIncreaseAnimationLimit = false
[Debug]
bEnableDebugDraws = false
```

---

## 12 — FormID & gotchas

- **FormID form in configs:** `{ "pluginName": "Plugin.esp", "formID": "<local hex>" }`. The
  `formID` is the record's **local** id within that plugin (strip the load-order byte): player =
  `Skyrim.esm` / `"7"`; a TDM effect = `TrueDirectionalMovement.esp` / `"804"`. OAR resolves the
  real runtime FormID via the named plugin (ESL-aware). Editor-picked forms may also store an
  `editorID` instead.
- **An embedded null in a formID string** — the in-game editor sometimes writes a `U+0000` between hex digits, so `"BB5B5"` is stored as `BB5`+`U+0000`+`B5` and displays as `"BB5 B5"`. The engine reads the hex digits and ignores it; don't "fix" it by hand, it's harmless.
- **Lowercase `conditions` vs capital `Conditions`** — see §4. The #1 silent-no-op.
- **`IsEquippedType` 6 vs 10** (battleaxe vs warhammer) — see §5.
- **A submod with only `config.json` (no `.hkx`)** is usually fine (`overrideAnimationsFolder` or a
  conditions-only host) — don't assume it's broken.
- **Addon conditions are hard dependencies** — see §7.
