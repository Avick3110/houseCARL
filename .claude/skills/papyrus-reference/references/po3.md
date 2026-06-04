# `Debris`

**Source:** `po3` (powerofthree's Papyrus Extender) • **Extends:** `Form` • **Flags:** Hidden


---

## `FootstepSet`

**Source:** `po3` (powerofthree's Papyrus Extender) • **Extends:** `Form` • **Flags:** Hidden


---

## `LightingTemplate`

**Source:** `po3` (powerofthree's Papyrus Extender) • **Extends:** `Form` • **Flags:** Hidden


---

## `MaterialObject`

**Source:** `po3` (powerofthree's Papyrus Extender) • **Extends:** `Form` • **Flags:** Hidden


---

## `PO3_Events_Alias`

**Source:** `po3` (powerofthree's Papyrus Extender) • **Flags:** Hidden

---

## Events

### `OnActorFallLongDistance(akTarget, afFallDistance, afFallDamage)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akTarget` | `Actor` |
| `afFallDistance` | `Float` |
| `afFallDamage` | `Float` |

### `OnActorKilled(akVictim, akKiller)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akVictim` | `Actor` |
| `akKiller` | `Actor` |

### `OnActorReanimateStart(akTarget, akCaster)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akTarget` | `Actor` |
| `akCaster` | `Actor` |

### `OnActorReanimateStop(akTarget, akCaster)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akTarget` | `Actor` |
| `akCaster` | `Actor` |

### `OnActorResurrected(akTarget, abResetInventory)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akTarget` | `Actor` |
| `abResetInventory` | `Bool` |

### `OnBookRead(akBook)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akBook` | `Book` |

### `OnCellFullyLoaded(akCell)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akCell` | `Cell` |

### `OnCriticalHit(akAggressor, akWeapon, abSneakHit)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akAggressor` | `Actor` |
| `akWeapon` | `Weapon` |
| `abSneakHit` | `Bool` |

### `OnDisarmed(akSource, akTarget)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akSource` | `Actor` |
| `akTarget` | `Weapon` |

### `OnDragonSoulGained(afSouls)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `afSouls` | `Float` |

### `OnEnterFurniture(akRef)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akRef` | `ObjectReference` |

### `OnExitFurniture(akRef)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akRef` | `ObjectReference` |

### `OnFastTravelConfirmed(asMarkerReference)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `asMarkerReference` | `ObjectReference` |

### `OnFastTravelPrompt(asMarkerReference)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `asMarkerReference` | `ObjectReference` |

### `OnHitEx(akAggressor, akSource, akProjectile, abPowerAttack, abSneakAttack, abBashAttack, abHitBlocked)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akAggressor` | `ObjectReference` |
| `akSource` | `Form` |
| `akProjectile` | `Projectile` |
| `abPowerAttack` | `Bool` |
| `abSneakAttack` | `Bool` |
| `abBashAttack` | `Bool` |
| `abHitBlocked` | `Bool` |

### `OnItemCrafted(akBench, akLocation, akCreatedItem)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akBench` | `ObjectReference` |
| `akLocation` | `Location` |
| `akCreatedItem` | `Form` |

### `OnItemHarvested(akProduce)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akProduce` | `Form` |

### `OnLevelIncrease(aiLevel)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `aiLevel` | `Int` |

### `OnLocationDiscovery(asRegionName, asWorldspaceName)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `asRegionName` | `String` |
| `asWorldspaceName` | `String` |

### `OnMagicEffectApplyEx(akCaster, akEffect, akSource, abApplied)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akCaster` | `ObjectReference` |
| `akEffect` | `MagicEffect` |
| `akSource` | `Form` |
| `abApplied` | `Bool` |

### `OnMagicHit(akTarget, akSource, akProjectile)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akTarget` | `ObjectReference` |
| `akSource` | `Form` |
| `akProjectile` | `Projectile` |

### `OnObjectGrab(akObjectRef)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akObjectRef` | `ObjectReference` |

### `OnObjectLoaded(akRef, aiFormType)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akRef` | `ObjectReference` |
| `aiFormType` | `Int` |

### `OnObjectPoisoned(akObject, akPoison, aiDose)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akObject` | `Form` |
| `akPoison` | `Potion` |
| `aiDose` | `Int` |

### `OnObjectRelease(akObjectRef)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akObjectRef` | `ObjectReference` |

### `OnObjectUnloaded(akRef, aiFormType)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akRef` | `ObjectReference` |
| `aiFormType` | `Int` |

### `OnPlayerFastTravelEnd(afTravelGameTimeHours)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `afTravelGameTimeHours` | `Float` |

### `OnProjectileHit(akTarget, akSource, akProjectile)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akTarget` | `ObjectReference` |
| `akSource` | `Form` |
| `akProjectile` | `Projectile` |

### `OnQuestStageChange(akQuest, aiNewStage)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akQuest` | `Quest` |
| `aiNewStage` | `Int` |

### `OnQuestStart(akQuest)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akQuest` | `Quest` |

### `OnQuestStop(akQuest)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akQuest` | `Quest` |

### `OnShoutAttack(akShout)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akShout` | `Shout` |

### `OnSkillIncrease(aiSkill)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `aiSkill` | `Int` |

### `OnSoulTrapped(akVictim, akKiller)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akVictim` | `Actor` |
| `akKiller` | `Actor` |

### `OnSpellLearned(akSpell)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akSpell` | `Spell` |

### `OnWeaponHit(akTarget, akSource, akProjectile, aiHitFlagMask)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akTarget` | `ObjectReference` |
| `akSource` | `Form` |
| `akProjectile` | `Projectile` |
| `aiHitFlagMask` | `Int` |

### `OnWeatherChange(akOldWeather, akNewWeather)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akOldWeather` | `Weather` |
| `akNewWeather` | `Weather` |

---

## Global Functions

### `RegisterForActorFallLongDistance(akRefAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRefAlias` | `ReferenceAlias` | ✓ |  |

### `RegisterForActorKilled(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `RegisterForActorReanimateStart(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `RegisterForActorReanimateStop(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `RegisterForActorResurrected(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `RegisterForBookRead(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `RegisterForCellFullyLoaded(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `RegisterForCriticalHit(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `RegisterForDisarmed(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `RegisterForDragonSoulGained(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `RegisterForFastTravelConfirmed(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `RegisterForFastTravelPrompt(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `RegisterForFurnitureEvent(akRefAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRefAlias` | `ReferenceAlias` | ✓ |  |

### `RegisterForHitEventEx(akRefAlias, akAggressorFilter, akSourceFilter, akProjectileFilter, aiPowerFilter, aiSneakFilter, aiBashFilter, aiBlockFilter, abMatch)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRefAlias` | `ReferenceAlias` | ✓ |  |
| `akAggressorFilter` | `Form` |  |  |
| `akSourceFilter` | `Form` |  |  |
| `akProjectileFilter` | `Form` |  |  |
| `aiPowerFilter` | `Int` |  | `-1` |
| `aiSneakFilter` | `Int` |  | `-1` |
| `aiBashFilter` | `Int` |  | `-1` |
| `aiBlockFilter` | `Int` |  | `-1` |
| `abMatch` | `Bool` |  | `true` |

### `RegisterForItemCrafted(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `RegisterForItemHarvested(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `RegisterForLevelIncrease(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `RegisterForLocationDiscovery(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `RegisterForMagicEffectApplyEx(akRefAlias, akEffectFilter, abMatch)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRefAlias` | `ReferenceAlias` | ✓ |  |
| `akEffectFilter` | `Form` | ✓ |  |
| `abMatch` | `Bool` | ✓ |  |

### `RegisterForMagicHit(akRefAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRefAlias` | `ReferenceAlias` | ✓ |  |

### `RegisterForObjectGrab(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `RegisterForObjectLoaded(akAlias, formType)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |
| `formType` | `Int` | ✓ |  |

### `RegisterForObjectPoisoned(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `RegisterForOnPlayerFastTravelEnd(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `RegisterForProjectileHit(akRefAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRefAlias` | `ReferenceAlias` | ✓ |  |

### `RegisterForQuest(akAlias, akQuest)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |
| `akQuest` | `Quest` | ✓ |  |

### `RegisterForQuestStage(akAlias, akQuest)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |
| `akQuest` | `Quest` | ✓ |  |

### `RegisterForShoutAttack(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `RegisterForSkillIncrease(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `RegisterForSoulTrapped(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `RegisterForSpellLearned(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `RegisterForWeaponHit(akRefAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRefAlias` | `ReferenceAlias` | ✓ |  |

### `RegisterForWeatherChange(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `UnregisterForActorFallLongDistance(akRefAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRefAlias` | `ReferenceAlias` | ✓ |  |

### `UnregisterForActorKilled(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `UnregisterForActorReanimateStart(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `UnregisterForActorReanimateStop(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `UnregisterForActorResurrected(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `UnregisterForAllHitEventsEx(akRefAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRefAlias` | `ReferenceAlias` | ✓ |  |

### `UnregisterForAllMagicEffectApplyEx(akRefAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRefAlias` | `ReferenceAlias` | ✓ |  |

### `UnregisterForAllObjectsLoaded(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `UnregisterForAllQuests(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `UnregisterForAllQuestStages(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `UnregisterForBookRead(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `UnregisterForCellFullyLoaded(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `UnregisterForCriticalHit(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `UnregisterForDisarmed(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `UnregisterForDragonSoulGained(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `UnregisterForFastTravelConfirmed(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `UnregisterForFastTravelPrompt(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `UnregisterForFurnitureEvent(akRefAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRefAlias` | `ReferenceAlias` | ✓ |  |

### `UnregisterForHitEventEx(akRefAlias, akAggressorFilter, akSourceFilter, akProjectileFilter, aiPowerFilter, aiSneakFilter, aiBashFilter, aiBlockFilter, abMatch)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRefAlias` | `ReferenceAlias` | ✓ |  |
| `akAggressorFilter` | `Form` |  |  |
| `akSourceFilter` | `Form` |  |  |
| `akProjectileFilter` | `Form` |  |  |
| `aiPowerFilter` | `Int` |  | `-1` |
| `aiSneakFilter` | `Int` |  | `-1` |
| `aiBashFilter` | `Int` |  | `-1` |
| `aiBlockFilter` | `Int` |  | `-1` |
| `abMatch` | `Bool` |  | `true` |

### `UnregisterForItemCrafted(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `UnregisterForItemHarvested(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `UnregisterForLevelIncrease(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `UnregisterForLocationDiscovery(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `UnregisterForMagicEffectApplyEx(akRefAlias, akEffectFilter, abMatch)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRefAlias` | `ReferenceAlias` | ✓ |  |
| `akEffectFilter` | `Form` | ✓ |  |
| `abMatch` | `Bool` | ✓ |  |

### `UnregisterForMagicHit(akRefAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRefAlias` | `ReferenceAlias` | ✓ |  |

### `UnregisterForObjectGrab(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `UnregisterForObjectLoaded(akAlias, formType)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |
| `formType` | `Int` | ✓ |  |

### `UnregisterForObjectPoisoned(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `UnregisterForOnPlayerFastTravelEnd(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `UnregisterForProjectileHit(akRefAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRefAlias` | `ReferenceAlias` | ✓ |  |

### `UnregisterForQuest(akAlias, akQuest)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |
| `akQuest` | `Quest` | ✓ |  |

### `UnregisterForQuestStage(akAlias, akQuest)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |
| `akQuest` | `Quest` | ✓ |  |

### `UnregisterForShoutAttack(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `UnregisterForSkillIncrease(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `UnregisterForSoulTrapped(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `UnregisterForSpellLearned(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `UnregisterForWeaponHit(akRefAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRefAlias` | `ReferenceAlias` | ✓ |  |

### `UnregisterForWeatherChange(akAlias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |


---

## `PO3_Events_AME`

**Source:** `po3` (powerofthree's Papyrus Extender) • **Flags:** Hidden

---

## Events

### `OnActorFallLongDistance(akTarget, afFallDistance, afFallDamage)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akTarget` | `Actor` |
| `afFallDistance` | `Float` |
| `afFallDamage` | `Float` |

### `OnActorKilled(akVictim, akKiller)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akVictim` | `Actor` |
| `akKiller` | `Actor` |

### `OnActorReanimateStart(akTarget, akCaster)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akTarget` | `Actor` |
| `akCaster` | `Actor` |

### `OnActorReanimateStop(akTarget, akCaster)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akTarget` | `Actor` |
| `akCaster` | `Actor` |

### `OnActorResurrected(akTarget, abResetInventory)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akTarget` | `Actor` |
| `abResetInventory` | `Bool` |

### `OnBookRead(akBook)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akBook` | `Book` |

### `OnCellFullyLoaded(akCell)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akCell` | `Cell` |

### `OnCriticalHit(akAggressor, akWeapon, abSneakHit)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akAggressor` | `Actor` |
| `akWeapon` | `Weapon` |
| `abSneakHit` | `Bool` |

### `OnDisarmed(akSource, akTarget)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akSource` | `Actor` |
| `akTarget` | `Weapon` |

### `OnDragonSoulGained(afSouls)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `afSouls` | `Float` |

### `OnEnterFurniture(akRef)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akRef` | `ObjectReference` |

### `OnExitFurniture(akRef)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akRef` | `ObjectReference` |

### `OnFastTravelConfirmed(asMarkerReference)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `asMarkerReference` | `ObjectReference` |

### `OnFastTravelPrompt(asMarkerReference)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `asMarkerReference` | `ObjectReference` |

### `OnHitEx(akAggressor, akSource, akProjectile, abPowerAttack, abSneakAttack, abBashAttack, abHitBlocked)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akAggressor` | `ObjectReference` |
| `akSource` | `Form` |
| `akProjectile` | `Projectile` |
| `abPowerAttack` | `Bool` |
| `abSneakAttack` | `Bool` |
| `abBashAttack` | `Bool` |
| `abHitBlocked` | `Bool` |

### `OnItemCrafted(akBench, akLocation, akCreatedItem)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akBench` | `ObjectReference` |
| `akLocation` | `Location` |
| `akCreatedItem` | `Form` |

### `OnItemHarvested(akProduce)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akProduce` | `Form` |

### `OnLevelIncrease(aiLevel)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `aiLevel` | `Int` |

### `OnLocationDiscovery(asRegionName, asWorldspaceName)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `asRegionName` | `String` |
| `asWorldspaceName` | `String` |

### `OnMagicEffectApplyEx(akCaster, akEffect, akSource, abApplied)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akCaster` | `ObjectReference` |
| `akEffect` | `MagicEffect` |
| `akSource` | `Form` |
| `abApplied` | `Bool` |

### `OnMagicHit(akTarget, akSource, akProjectile)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akTarget` | `ObjectReference` |
| `akSource` | `Form` |
| `akProjectile` | `Projectile` |

### `OnObjectGrab(akObjectRef)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akObjectRef` | `ObjectReference` |

### `OnObjectLoaded(akRef, aiFormType)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akRef` | `ObjectReference` |
| `aiFormType` | `Int` |

### `OnObjectPoisoned(akObject, akPoison, aiDose)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akObject` | `Form` |
| `akPoison` | `Potion` |
| `aiDose` | `Int` |

### `OnObjectRelease(akObjectRef)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akObjectRef` | `ObjectReference` |

### `OnObjectUnloaded(akRef, aiFormType)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akRef` | `ObjectReference` |
| `aiFormType` | `Int` |

### `OnPlayerFastTravelEnd(afTravelGameTimeHours)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `afTravelGameTimeHours` | `Float` |

### `OnPlayerShoutAttack(akShout)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akShout` | `Shout` |

### `OnProjectileHit(akTarget, akSource, akProjectile)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akTarget` | `ObjectReference` |
| `akSource` | `Form` |
| `akProjectile` | `Projectile` |

### `OnQuestStageChange(akQuest, aiNewStage)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akQuest` | `Quest` |
| `aiNewStage` | `Int` |

### `OnQuestStart(akQuest)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akQuest` | `Quest` |

### `OnQuestStop(akQuest)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akQuest` | `Quest` |

### `OnSkillIncrease(aiSkill)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `aiSkill` | `Int` |

### `OnSoulTrapped(akVictim, akKiller)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akVictim` | `Actor` |
| `akKiller` | `Actor` |

### `OnSpellLearned(akSpell)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akSpell` | `Spell` |

### `OnWeaponHit(akTarget, akSource, akProjectile, aiHitFlagMask)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akTarget` | `ObjectReference` |
| `akSource` | `Form` |
| `akProjectile` | `Projectile` |
| `aiHitFlagMask` | `Int` |

### `OnWeatherChange(akOldWeather, akNewWeather)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akOldWeather` | `Weather` |
| `akNewWeather` | `Weather` |

---

## Global Functions

### `RegisterForActorFallLongDistance(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `RegisterForActorKilled(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `RegisterForActorReanimateStart(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `RegisterForActorReanimateStop(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `RegisterForActorResurrected(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `RegisterForBookRead(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `RegisterForCellFullyLoaded(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `RegisterForCriticalHit(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `RegisterForDisarmed(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `RegisterForDragonSoulGained(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `RegisterForFastTravelConfirmed(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `RegisterForFastTravelPrompt(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `RegisterForFurnitureEvent(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `RegisterForHitEventEx(akActiveEffect, akAggressorFilter, akSourceFilter, akProjectileFilter, aiPowerFilter, aiSneakFilter, aiBashFilter, aiBlockFilter, abMatch)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |
| `akAggressorFilter` | `Form` |  |  |
| `akSourceFilter` | `Form` |  |  |
| `akProjectileFilter` | `Form` |  |  |
| `aiPowerFilter` | `Int` |  | `-1` |
| `aiSneakFilter` | `Int` |  | `-1` |
| `aiBashFilter` | `Int` |  | `-1` |
| `aiBlockFilter` | `Int` |  | `-1` |
| `abMatch` | `Bool` |  | `true` |

### `RegisterForItemCrafted(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `RegisterForItemHarvested(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `RegisterForLevelIncrease(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `RegisterForLocationDiscovery(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `RegisterForMagicEffectApplyEx(akActiveEffect, akEffectFilter, abMatch)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |
| `akEffectFilter` | `Form` | ✓ |  |
| `abMatch` | `Bool` | ✓ |  |

### `RegisterForMagicHit(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `RegisterForObjectGrab(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `RegisterForObjectLoaded(akActiveEffect, formType)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |
| `formType` | `Int` | ✓ |  |

### `RegisterForObjectPoisoned(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `RegisterForOnPlayerFastTravelEnd(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `RegisterForProjectileHit(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `RegisterForQuest(akActiveEffect, akQuest)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |
| `akQuest` | `Quest` | ✓ |  |

### `RegisterForQuestStage(akActiveEffect, akQuest)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |
| `akQuest` | `Quest` | ✓ |  |

### `RegisterForShoutAttack(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `RegisterForSkillIncrease(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `RegisterForSoulTrapped(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `RegisterForSpellLearned(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `RegisterForWeaponHit(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `RegisterForWeatherChange(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterForActorFallLongDistance(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterForActorKilled(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterForActorReanimateStart(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterForActorReanimateStop(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterForActorResurrected(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterForAllHitEventsEx(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterForAllMagicEffectApplyEx(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterForAllObjectsLoaded(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterForAllQuests(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterForAllQuestStages(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterForBookRead(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterForCellFullyLoaded(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterForCriticalHit(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterForDisarmed(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterForDragonSoulGained(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterForFastTravelConfirmed(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterForFastTravelPrompt(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterForFurnitureEvent(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterForHitEventEx(akActiveEffect, akAggressorFilter, akSourceFilter, akProjectileFilter, aiPowerFilter, aiSneakFilter, aiBashFilter, aiBlockFilter, abMatch)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |
| `akAggressorFilter` | `Form` |  |  |
| `akSourceFilter` | `Form` |  |  |
| `akProjectileFilter` | `Form` |  |  |
| `aiPowerFilter` | `Int` |  | `-1` |
| `aiSneakFilter` | `Int` |  | `-1` |
| `aiBashFilter` | `Int` |  | `-1` |
| `aiBlockFilter` | `Int` |  | `-1` |
| `abMatch` | `Bool` |  | `true` |

### `UnregisterForItemCrafted(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterForItemHarvested(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterForLevelIncrease(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterForLocationDiscovery(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterForMagicEffectApplyEx(akActiveEffect, akEffectFilter, abMatch)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |
| `akEffectFilter` | `Form` | ✓ |  |
| `abMatch` | `Bool` | ✓ |  |

### `UnregisterForMagicHit(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterForObjectGrab(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterForObjectLoaded(akActiveEffect, formType)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |
| `formType` | `Int` | ✓ |  |

### `UnregisterForObjectPoisoned(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterForOnPlayerFastTravelEnd(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterForProjectileHit(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterForQuest(akActiveEffect, akQuest)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |
| `akQuest` | `Quest` | ✓ |  |

### `UnregisterForQuestStage(akActiveEffect, akQuest)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |
| `akQuest` | `Quest` | ✓ |  |

### `UnregisterForShoutAttack(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterForSkillIncrease(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterForSoulTrapped(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterForSpellLearned(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterForWeaponHit(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterForWeatherChange(akActiveEffect)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |


---

## `PO3_Events_Form`

**Source:** `po3` (powerofthree's Papyrus Extender) • **Flags:** Hidden

---

## Events

### `OnActorFallLongDistance(akTarget, afFallDistance, afFallDamage)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akTarget` | `Actor` |
| `afFallDistance` | `Float` |
| `afFallDamage` | `Float` |

### `OnActorKilled(akVictim, akKiller)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akVictim` | `Actor` |
| `akKiller` | `Actor` |

### `OnActorReanimateStart(akTarget, akCaster)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akTarget` | `Actor` |
| `akCaster` | `Actor` |

### `OnActorReanimateStop(akTarget, akCaster)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akTarget` | `Actor` |
| `akCaster` | `Actor` |

### `OnActorResurrected(akTarget, abResetInventory)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akTarget` | `Actor` |
| `abResetInventory` | `Bool` |

### `OnBookRead(akBook)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akBook` | `Book` |

### `OnCellFullyLoaded(akCell)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akCell` | `Cell` |

### `OnCriticalHit(akAggressor, akWeapon, abSneakHit)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akAggressor` | `Actor` |
| `akWeapon` | `Weapon` |
| `abSneakHit` | `Bool` |

### `OnDisarmed(akSource, akTarget)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akSource` | `Actor` |
| `akTarget` | `Weapon` |

### `OnDragonSoulGained(afSouls)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `afSouls` | `Float` |

### `OnEnterFurniture(akRef)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akRef` | `ObjectReference` |

### `OnExitFurniture(akRef)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akRef` | `ObjectReference` |

### `OnFastTravelConfirmed(asMarkerReference)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `asMarkerReference` | `ObjectReference` |

### `OnFastTravelPrompt(asMarkerReference)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `asMarkerReference` | `ObjectReference` |

### `OnHitEx(akAggressor, akSource, akProjectile, abPowerAttack, abSneakAttack, abBashAttack, abHitBlocked)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akAggressor` | `ObjectReference` |
| `akSource` | `Form` |
| `akProjectile` | `Projectile` |
| `abPowerAttack` | `Bool` |
| `abSneakAttack` | `Bool` |
| `abBashAttack` | `Bool` |
| `abHitBlocked` | `Bool` |

### `OnItemCrafted(akBench, akLocation, akCreatedItem)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akBench` | `ObjectReference` |
| `akLocation` | `Location` |
| `akCreatedItem` | `Form` |

### `OnItemHarvested(akProduce)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akProduce` | `Form` |

### `OnLevelIncrease(aiLevel)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `aiLevel` | `Int` |

### `OnLocationDiscovery(asRegionName, asWorldspaceName)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `asRegionName` | `String` |
| `asWorldspaceName` | `String` |

### `OnMagicEffectApplyEx(akCaster, akEffect, akSource, abApplied)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akCaster` | `ObjectReference` |
| `akEffect` | `MagicEffect` |
| `akSource` | `Form` |
| `abApplied` | `Bool` |

### `OnMagicHit(akTarget, akSource, akProjectile)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akTarget` | `ObjectReference` |
| `akSource` | `Form` |
| `akProjectile` | `Projectile` |

### `OnObjectGrab(akObjectRef)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akObjectRef` | `ObjectReference` |

### `OnObjectLoaded(akRef, aiFormType)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akRef` | `ObjectReference` |
| `aiFormType` | `Int` |

### `OnObjectPoisoned(akObject, akPoison, aiDose)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akObject` | `Form` |
| `akPoison` | `Potion` |
| `aiDose` | `Int` |

### `OnObjectRelease(akObjectRef)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akObjectRef` | `ObjectReference` |

### `OnObjectUnloaded(akRef, aiFormType)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akRef` | `ObjectReference` |
| `aiFormType` | `Int` |

### `OnPlayerFastTravelEnd(afTravelGameTimeHours)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `afTravelGameTimeHours` | `Float` |

### `OnPlayerShoutAttack(akShout)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akShout` | `Shout` |

### `OnProjectileHit(akTarget, akSource, akProjectile)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akTarget` | `ObjectReference` |
| `akSource` | `Form` |
| `akProjectile` | `Projectile` |

### `OnQuestStageChange(akQuest, aiNewStage)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akQuest` | `Quest` |
| `aiNewStage` | `Int` |

### `OnQuestStart(akQuest)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akQuest` | `Quest` |

### `OnQuestStop(akQuest)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akQuest` | `Quest` |

### `OnSkillIncrease(aiSkill)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `aiSkill` | `Int` |

### `OnSoulTrapped(akVictim, akKiller)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akVictim` | `Actor` |
| `akKiller` | `Actor` |

### `OnSpellLearned(akSpell)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akSpell` | `Spell` |

### `OnWeaponHit(akTarget, akSource, akProjectile, aiHitFlagMask)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akTarget` | `ObjectReference` |
| `akSource` | `Form` |
| `akProjectile` | `Projectile` |
| `aiHitFlagMask` | `Int` |

### `OnWeatherChange(akOldWeather, akNewWeather)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akOldWeather` | `Weather` |
| `akNewWeather` | `Weather` |

---

## Global Functions

### `RegisterForActorFallLongDistance(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `RegisterForActorKilled(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `RegisterForActorReanimateStart(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `RegisterForActorReanimateStop(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `RegisterForActorResurrected(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `RegisterForBookRead(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `RegisterForCellFullyLoaded(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `RegisterForCriticalHit(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `RegisterForDisarmed(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `RegisterForDragonSoulGained(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `RegisterForFastTravelConfirmed(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `RegisterForFastTravelPrompt(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `RegisterForFurnitureEvent(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `RegisterForHitEventEx(akForm, akAggressorFilter, akSourceFilter, akProjectileFilter, aiPowerFilter, aiSneakFilter, aiBashFilter, aiBlockFilter, abMatch)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `akAggressorFilter` | `Form` |  |  |
| `akSourceFilter` | `Form` |  |  |
| `akProjectileFilter` | `Form` |  |  |
| `aiPowerFilter` | `Int` |  | `-1` |
| `aiSneakFilter` | `Int` |  | `-1` |
| `aiBashFilter` | `Int` |  | `-1` |
| `aiBlockFilter` | `Int` |  | `-1` |
| `abMatch` | `Bool` |  | `true` |

### `RegisterForItemCrafted(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `RegisterForItemHarvested(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `RegisterForLevelIncrease(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `RegisterForLocationDiscovery(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `RegisterForMagicEffectApplyEx(akForm, akEffectFilter, abMatch)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `akEffectFilter` | `Form` | ✓ |  |
| `abMatch` | `Bool` | ✓ |  |

### `RegisterForMagicHit(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `RegisterForObjectGrab(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `RegisterForObjectLoaded(akForm, formType)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `formType` | `Int` | ✓ |  |

### `RegisterForObjectPoisoned(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `RegisterForOnPlayerFastTravelEnd(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `RegisterForProjectileHit(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `RegisterForQuest(akForm, akQuest)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `akQuest` | `Quest` | ✓ |  |

### `RegisterForQuestStage(akForm, akQuest)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `akQuest` | `Quest` | ✓ |  |

### `RegisterForShoutAttack(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `RegisterForSkillIncrease(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `RegisterForSoulTrapped(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `RegisterForSpellLearned(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `RegisterForWeaponHit(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `RegisterForWeatherChange(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `UnregisterForActorFallLongDistance(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `UnregisterForActorKilled(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `UnregisterForActorReanimateStart(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `UnregisterForActorReanimateStop(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `UnregisterForActorResurrected(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `UnregisterForAllHitEventsEx(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `UnregisterForAllMagicEffectApplyEx(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `UnregisterForAllObjectsLoaded(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `UnregisterForAllQuests(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `UnregisterForAllQuestStages(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `UnregisterForBookRead(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `UnregisterForCellFullyLoaded(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `UnregisterForCriticalHit(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `UnregisterForDisarmed(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `UnregisterForDragonSoulGained(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `UnregisterForFastTravelConfirmed(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `UnregisterForFastTravelPrompt(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `UnregisterForFurnitureEvent(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `UnregisterForHitEventEx(akForm, akAggressorFilter, akSourceFilter, akProjectileFilter, aiPowerFilter, aiSneakFilter, aiBashFilter, aiBlockFilter, abMatch)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `akAggressorFilter` | `Form` |  |  |
| `akSourceFilter` | `Form` |  |  |
| `akProjectileFilter` | `Form` |  |  |
| `aiPowerFilter` | `Int` |  | `-1` |
| `aiSneakFilter` | `Int` |  | `-1` |
| `aiBashFilter` | `Int` |  | `-1` |
| `aiBlockFilter` | `Int` |  | `-1` |
| `abMatch` | `Bool` |  | `true` |

### `UnregisterForItemCrafted(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `UnregisterForItemHarvested(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `UnregisterForLevelIncrease(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `UnregisterForLocationDiscovery(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `UnregisterForMagicEffectApplyEx(akForm, akEffectFilter, abMatch)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `akEffectFilter` | `Form` | ✓ |  |
| `abMatch` | `Bool` | ✓ |  |

### `UnregisterForMagicHit(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `UnregisterForObjectGrab(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `UnregisterForObjectLoaded(akForm, formType)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `formType` | `Int` | ✓ |  |

### `UnregisterForObjectPoisoned(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `UnregisterForOnPlayerFastTravelEnd(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `UnregisterForProjectileHit(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `UnregisterForQuest(akForm, akQuest)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `akQuest` | `Quest` | ✓ |  |

### `UnregisterForQuestStage(akForm, akQuest)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `akQuest` | `Quest` | ✓ |  |

### `UnregisterForShoutAttack(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `UnregisterForSkillIncrease(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `UnregisterForSoulTrapped(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `UnregisterForSpellLearned(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `UnregisterForWeaponHit(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `UnregisterForWeatherChange(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |


---

## `PO3_SKSEFunctions`

**Source:** `po3` (powerofthree's Papyrus Extender) • **Flags:** Hidden

---

## Global Functions

### `ActorInRangeHasEffect(akRef, afRadius, akEffect, abIgnorePlayer) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `afRadius` | `Float` | ✓ |  |
| `akEffect` | `MagicEffect` | ✓ |  |
| `abIgnorePlayer` | `Bool` | ✓ |  |

### `AddActorToArray(akActor, actorArray) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `actorArray` | `Actor[]` | ✓ |  |

### `AddAllEquippedItemsBySlotToArray(akActor, aiSlots) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `aiSlots` | `Int[]` | ✓ |  |

### `AddAllEquippedItemsToArray(akActor) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `AddAllItemsToArray(akRef, abNoEquipped, abNoFavorited, abNoQuestItem) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `abNoEquipped` | `Bool` |  | `true` |
| `abNoFavorited` | `Bool` |  | `false` |
| `abNoQuestItem` | `Bool` |  | `false` |

### `AddAllItemsToList(akRef, akList, abNoEquipped, abNoFavorited, abNoQuestItem)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `akList` | `Formlist` | ✓ |  |
| `abNoEquipped` | `Bool` |  | `true` |
| `abNoFavorited` | `Bool` |  | `false` |
| `abNoQuestItem` | `Bool` |  | `false` |

### `AddBasePerk(akActor, akPerk) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `akPerk` | `Perk` | ✓ |  |

### `AddBaseSpell(akActor, akSpell) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `akSpell` | `Spell` | ✓ |  |

### `AddEffectItemToEnchantment(akEnchantment, akEnchantmentToCopyFrom, aiIndex, afCost)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEnchantment` | `Enchantment` | ✓ |  |
| `akEnchantmentToCopyFrom` | `Enchantment` | ✓ |  |
| `aiIndex` | `Int` | ✓ |  |
| `afCost` | `Float` |  | `-1` |

### `AddEffectItemToPotion(akPotion, akPotionToCopyFrom, aiIndex, afCost)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akPotion` | `Potion` | ✓ |  |
| `akPotionToCopyFrom` | `Potion` | ✓ |  |
| `aiIndex` | `Int` | ✓ |  |
| `afCost` | `Float` |  | `-1` |

### `AddEffectItemToScroll(akScroll, akScrollToCopyFrom, aiIndex, afCost)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akScroll` | `Scroll` | ✓ |  |
| `akScrollToCopyFrom` | `Scroll` | ✓ |  |
| `aiIndex` | `Int` | ✓ |  |
| `afCost` | `Float` |  | `-1` |

### `AddEffectItemToSpell(akSpell, akSpellToCopyFrom, aiIndex, afCost)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSpell` | `Spell` | ✓ |  |
| `akSpellToCopyFrom` | `Spell` | ✓ |  |
| `aiIndex` | `Int` | ✓ |  |
| `afCost` | `Float` |  | `-1` |

### `AddItemsOfTypeToArray(akRef, aiFormType, abNoEquipped, abNoFavorited, abNoQuestItem) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `aiFormType` | `Int` | ✓ |  |
| `abNoEquipped` | `Bool` |  | `true` |
| `abNoFavorited` | `Bool` |  | `false` |
| `abNoQuestItem` | `Bool` |  | `false` |

### `AddItemsOfTypeToList(akRef, akList, aiFormType, abNoEquipped, abNoFavorited, abNoQuestItem)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `akList` | `Formlist` | ✓ |  |
| `aiFormType` | `Int` | ✓ |  |
| `abNoEquipped` | `Bool` |  | `true` |
| `abNoFavorited` | `Bool` |  | `false` |
| `abNoQuestItem` | `Bool` |  | `false` |

### `AddKeywordToForm(akForm, akKeyword)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `akKeyword` | `Keyword` | ✓ |  |

### `AddKeywordToRef(akRef, akKeyword)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `akKeyword` | `Keyword` | ✓ |  |

### `AddMagicEffectToEnchantment(akEnchantment, akMagicEffect, afMagnitude, aiArea, aiDuration, afCost, asConditionList)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEnchantment` | `Enchantment` | ✓ |  |
| `akMagicEffect` | `MagicEffect` | ✓ |  |
| `afMagnitude` | `Float` | ✓ |  |
| `aiArea` | `Int` | ✓ |  |
| `aiDuration` | `Int` | ✓ |  |
| `afCost` | `Float` |  | `0` |
| `asConditionList` | `String[]` | ✓ |  |

### `AddMagicEffectToPotion(akPotion, akMagicEffect, afMagnitude, aiArea, aiDuration, afCost, asConditionList)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akPotion` | `Potion` | ✓ |  |
| `akMagicEffect` | `MagicEffect` | ✓ |  |
| `afMagnitude` | `Float` | ✓ |  |
| `aiArea` | `Int` | ✓ |  |
| `aiDuration` | `Int` | ✓ |  |
| `afCost` | `Float` |  | `0` |
| `asConditionList` | `String[]` | ✓ |  |

### `AddMagicEffectToScroll(akScroll, akMagicEffect, afMagnitude, aiArea, aiDuration, afCost, asConditionList)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akScroll` | `Scroll` | ✓ |  |
| `akMagicEffect` | `MagicEffect` | ✓ |  |
| `afMagnitude` | `Float` | ✓ |  |
| `aiArea` | `Int` | ✓ |  |
| `aiDuration` | `Int` | ✓ |  |
| `afCost` | `Float` |  | `0` |
| `asConditionList` | `String[]` | ✓ |  |

### `AddMagicEffectToSpell(akSpell, akMagicEffect, afMagnitude, aiArea, aiDuration, afCost, asConditionList)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSpell` | `Spell` | ✓ |  |
| `akMagicEffect` | `MagicEffect` | ✓ |  |
| `afMagnitude` | `Float` | ✓ |  |
| `aiArea` | `Int` | ✓ |  |
| `aiDuration` | `Int` | ✓ |  |
| `afCost` | `Float` |  | `0` |
| `asConditionList` | `String[]` | ✓ |  |

### `AddPackageIdle(akPackage, akIdle)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akPackage` | `Package` | ✓ |  |
| `akIdle` | `Idle` | ✓ |  |

### `AddStringToArray(asString, asStrings) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asString` | `String` | ✓ |  |
| `asStrings` | `String[]` | ✓ |  |

### `ApplyMaterialShader(akRef, akMatObject, directionalThresholdAngle)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `akMatObject` | `MaterialObject` | ✓ |  |
| `directionalThresholdAngle` | `Float` | ✓ |  |

### `ApplyPoisonToEquippedWeapon(akActor, akPoison, aiCount, abLeftHand) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `akPoison` | `Potion` | ✓ |  |
| `aiCount` | `Int` | ✓ |  |
| `abLeftHand` | `Bool` | ✓ |  |

### `ArrayStringCount(asString, asStrings) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asString` | `String` | ✓ |  |
| `asStrings` | `String[]` | ✓ |  |

### `BlendColorWithSkinTone(akActor, akColor, aiBlendMode, abAutoLuminance, afOpacity)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `akColor` | `ColorForm` | ✓ |  |
| `aiBlendMode` | `Int` | ✓ |  |
| `abAutoLuminance` | `Bool` | ✓ |  |
| `afOpacity` | `Float` | ✓ |  |

### `CanActorBeDetected(akActor) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `CanActorDetect(akActor) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `CastEx(akRef, akSpell, akTarget, akBlameActor, aiSource)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `akSpell` | `Form` | ✓ |  |
| `akTarget` | `ObjectReference` | ✓ |  |
| `akBlameActor` | `Actor` | ✓ |  |
| `aiSource` | `Int` | ✓ |  |

### `ClearBookCantBeTakenFlag(akBook)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akBook` | `Book` | ✓ |  |

### `ClearCachedFactionFightReactions()`

**Flags:** Native Global

### `ClearEffectShaderFlag(akEffectShader, aiFlag)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEffectShader` | `EffectShader` | ✓ |  |
| `aiFlag` | `Int` | ✓ |  |

### `ClearHazardFlag(akHazard, aiFlag)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akHazard` | `Hazard` | ✓ |  |
| `aiFlag` | `Int` | ✓ |  |

### `ClearReadFlag(akBook)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akBook` | `Book` | ✓ |  |

### `ClearRecordFlag(akForm, aiFlag)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `aiFlag` | `Int` | ✓ |  |

### `DamageActorHealth(akActor, afHealthDamage, akSource) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `afHealthDamage` | `Float` | ✓ |  |
| `akSource` | `Actor` | ✓ |  |

### `DecapitateActor(akActor)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `DumpAnimationVariables(akActor, asAnimationVarPrefix)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `asAnimationVarPrefix` | `String` | ✓ |  |

### `EvaluateConditionList(akForm, akActionRef, akTargetRef) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `akActionRef` | `ObjectReference` | ✓ |  |
| `akTargetRef` | `ObjectReference` | ✓ |  |

### `FindAllReferencesOfFormType(akRef, formType, afRadius) → ObjectReference[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `formType` | `Int` | ✓ |  |
| `afRadius` | `Float` | ✓ |  |

### `FindAllReferencesOfType(akRef, akFormOrList, afRadius) → ObjectReference[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `akFormOrList` | `Form` | ✓ |  |
| `afRadius` | `Float` | ✓ |  |

### `FindAllReferencesWithKeyword(akRef, keywordOrList, afRadius, abMatchAll) → ObjectReference[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `keywordOrList` | `Form` | ✓ |  |
| `afRadius` | `Float` | ✓ |  |
| `abMatchAll` | `Bool` | ✓ |  |

### `FindFirstItemInList(akRef, akList) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `akList` | `FormList` | ✓ |  |

### `ForceActorDetecting(akActor)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `ForceActorDetection(akActor)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `FreezeActor(akActor, type, abFreeze)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `type` | `Int` | ✓ |  |
| `abFreeze` | `Bool` | ✓ |  |

### `GenerateRandomFloat(afMin, afMax) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afMin` | `Float` | ✓ |  |
| `afMax` | `Float` | ✓ |  |

### `GenerateRandomInt(afMin, afMax) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afMin` | `Int` | ✓ |  |
| `afMax` | `Int` | ✓ |  |

### `GetActivateChildren(akRef) → ObjectReference[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |

### `GetActiveAssociatedQuests(akRef, abAllowEmptyStages) → Quest[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `abAllowEmptyStages` | `Bool` |  | `true` |

### `GetActiveEffects(akActor, abShowInactive) → MagicEffect[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `abShowInactive` | `Bool` |  | `false` |

### `GetActiveEffectSpell(akActiveEffect) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `GetActiveGamebryoAnimation(akRef) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |

### `GetActiveMagicEffects(akRef, akMagicEffect) → ActiveMagicEffect[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `akMagicEffect` | `MagicEffect` | ✓ |  |

### `GetActorAlpha(akActor) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetActorCause(akRef) → Actor`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |

### `GetActorKnockState(akActor) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetActorRefraction(akActor) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetActorsByProcessingLevel(aiLevel) → Actor[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiLevel` | `Int` | ✓ |  |

### `GetActorsInScene(akScene) → Actor[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akScene` | `Scene` | ✓ |  |

### `GetActorSoulSize(akActor) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetActorState(akActor) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetActorValueModifier(akActor, aiModifier, asActorValue) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `aiModifier` | `Int` | ✓ |  |
| `asActorValue` | `String` | ✓ |  |

### `GetAddonModels(akEffectShader) → Debris`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEffectShader` | `EffectShader` | ✓ |  |

### `GetAllActorPlayableSpells(akActor) → Spell[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetAllActorsInFaction(akFaction) → Actor[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFaction` | `Faction` | ✓ |  |

### `GetAllArtObjects(akRef) → Art[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |

### `GetAllAssociatedQuests(akRef, abAllowEmptyStages) → Quest[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `abAllowEmptyStages` | `Bool` |  | `true` |

### `GetAllEffectShaders(akRef) → EffectShader[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |

### `GetAllEnchantments(akKeywords) → Enchantment[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akKeywords` | `Keyword[]` |  |  |

### `GetAllEnchantmentsInMod(asModName, akKeywords) → Enchantment[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asModName` | `String` | ✓ |  |
| `akKeywords` | `Keyword[]` |  |  |

### `GetAllForms(aiFormType, akKeywords) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiFormType` | `Int` | ✓ |  |
| `akKeywords` | `Keyword[]` |  |  |

### `GetAllFormsInMod(asModName, aiFormType, akKeywords) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asModName` | `String` | ✓ |  |
| `aiFormType` | `Int` | ✓ |  |
| `akKeywords` | `Keyword[]` |  |  |

### `GetAllQuestObjectives(akQuest) → Int[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akQuest` | `Quest` | ✓ |  |

### `GetAllQuestStages(akQuest) → Int[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akQuest` | `Quest` | ✓ |  |

### `GetAllRaces(akKeywords) → Race[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akKeywords` | `Keyword[]` |  |  |

### `GetAllRacesInMod(asModName, akKeywords) → Race[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asModName` | `String` | ✓ |  |
| `akKeywords` | `Keyword[]` |  |  |

### `GetAllSpells(akKeywords, abIsPlayable) → Spell[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akKeywords` | `Keyword[]` |  |  |
| `abIsPlayable` | `Bool` |  | `false` |

### `GetAllSpellsInMod(asModName, akKeywords, abIsPlayable) → Spell[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asModName` | `String` | ✓ |  |
| `akKeywords` | `Keyword[]` |  |  |
| `abIsPlayable` | `Bool` |  | `false` |

### `GetAnimationEventName(akIdle) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akIdle` | `Idle` | ✓ |  |

### `GetAnimationFileName(akIdle) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akIdle` | `Idle` | ✓ |  |

### `GetArtObject(akEffect) → Art`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEffect` | `VisualEffect` | ✓ |  |

### `GetArtObjectTotalCount(akEffect, abActive) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEffect` | `VisualEffect` | ✓ |  |
| `abActive` | `Bool` | ✓ |  |

### `GetAssociatedForm(akMagicEffect) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akMagicEffect` | `MagicEffect` | ✓ |  |

### `GetAssociationType(akBase1, akBase2) → AssociationType`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akBase1` | `Actorbase` | ✓ |  |
| `akBase2` | `Actorbase` | ✓ |  |

### `GetAttachedCells() → Cell[]`

**Flags:** Native Global

### `GetBaseAmmoEnchantment(akAmmo) → Enchantment`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAmmo` | `Ammo` | ✓ |  |

### `GetCellNorthRotation(akCell) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akCell` | `Cell` | ✓ |  |

### `GetClosestActorFromRef(akRef, abIgnorePlayer) → Actor`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `abIgnorePlayer` | `Bool` | ✓ |  |

### `GetCombatAllies(akActor) → Actor[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetCombatTargets(akActor) → Actor[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetCommandedActors(akActor) → Actor[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetCommandingActor(akActor) → Actor`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetConditionList(akForm, aiIndex) → String[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `aiIndex` | `Int` |  | `0` |

### `GetContentFromLeveledActor(akLeveledActor, akRef) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akLeveledActor` | `LeveledActor` | ✓ |  |
| `akRef` | `ObjectReference` | ✓ |  |

### `GetContentFromLeveledItem(akLeveledItem, akRef) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akLeveledItem` | `LeveledItem` | ✓ |  |
| `akRef` | `ObjectReference` | ✓ |  |

### `GetContentFromLeveledSpell(akLeveledSpell, akRef) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akLeveledSpell` | `LeveledSpell` | ✓ |  |
| `akRef` | `ObjectReference` | ✓ |  |

### `GetCriticalStage(akActor) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetDeathItem(akBase) → LeveledItem`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akBase` | `Actorbase` | ✓ |  |

### `GetDescription(akForm) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `GetDoorDestination(akRef) → ObjectReference`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |

### `GetEffectArchetypeAsInt(akMagicEffect) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akMagicEffect` | `MagicEffect` | ✓ |  |

### `GetEffectArchetypeAsString(akMagicEffect) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akMagicEffect` | `MagicEffect` | ✓ |  |

### `GetEffectShaderDuration(akRef, akShader) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `akShader` | `EffectShader` | ✓ |  |

### `GetEffectShaderTotalCount(akEffectShader, abActive) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEffectShader` | `EffectShader` | ✓ |  |
| `abActive` | `Bool` | ✓ |  |

### `GetEnchantmentType(akEnchantment) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEnchantment` | `Enchantment` | ✓ |  |

### `GetEquippedAmmo(akActor) → Ammo`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetEquippedAmmoEnchantment(akActor) → Enchantment`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetEquippedWeaponIsPoisoned(akActor, abLeftHand) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `abLeftHand` | `Bool` | ✓ |  |

### `GetEquippedWeaponPoison(akActor, abLeftHand) → Potion`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `abLeftHand` | `Bool` | ✓ |  |

### `GetEquippedWeaponPoisonCount(akActor, abLeftHand) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `abLeftHand` | `Bool` | ✓ |  |

### `GetEquippedWeight(akActor) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetFootstepSet(akArma) → FootstepSet`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArma` | `ArmorAddon` | ✓ |  |

### `GetFormEditorID(akForm) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `GetFormFromEditorID(asEditorID) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asEditorID` | `String` | ✓ |  |

### `GetFormModName(akForm, abLastModified) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `abLastModified` | `Bool` | ✓ |  |

### `GetFurnitureType(akFurniture) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFurniture` | `Furniture` | ✓ |  |

### `GetGameSettingBool(asGameSetting) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asGameSetting` | `String` | ✓ |  |

### `GetGodMode() → Bool`

**Flags:** Native Global

### `GetHairColor(akActor) → ColorForm`

**Flags:** Native Global

DEPRECATED

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetHairRGB(akActor) → Int[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetHazardArt(akHazard) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akHazard` | `Hazard` | ✓ |  |

### `GetHazardIMOD(akHazard) → ImageSpaceModifier`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akHazard` | `Hazard` | ✓ |  |

### `GetHazardIMODRadius(akHazard) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akHazard` | `Hazard` | ✓ |  |

### `GetHazardIPDS(akHazard) → ImpactDataSet`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akHazard` | `Hazard` | ✓ |  |

### `GetHazardLifetime(akHazard) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akHazard` | `Hazard` | ✓ |  |

### `GetHazardLight(akHazard) → Light`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akHazard` | `Hazard` | ✓ |  |

### `GetHazardLimit(akHazard) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akHazard` | `Hazard` | ✓ |  |

### `GetHazardRadius(akHazard) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akHazard` | `Hazard` | ✓ |  |

### `GetHazardSound(akHazard) → SoundDescriptor`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akHazard` | `Hazard` | ✓ |  |

### `GetHazardSpell(akHazard) → Spell`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akHazard` | `Hazard` | ✓ |  |

### `GetHazardTargetInterval(akHazard) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akHazard` | `Hazard` | ✓ |  |

### `GetHeadPartTextureSet(akActor, aiType) → TextureSet`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `aiType` | `Int` | ✓ |  |

### `GetLandHeight(afPosX, afPosY, afPosZ) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afPosX` | `Float` | ✓ |  |
| `afPosY` | `Float` | ✓ |  |
| `afPosZ` | `Float` | ✓ |  |

### `GetLandMaterialType(afPosX, afPosY, afPosZ) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afPosX` | `Float` | ✓ |  |
| `afPosY` | `Float` | ✓ |  |
| `afPosZ` | `Float` | ✓ |  |

### `GetLightColor(akLight) → ColorForm`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akLight` | `Light` | ✓ |  |

### `GetLightFade(akLight) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akLight` | `Light` | ✓ |  |

### `GetLightFOV(akLight) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akLight` | `Light` | ✓ |  |

### `GetLightingTemplate(akCell) → LightingTemplate`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akCell` | `Cell` | ✓ |  |

### `GetLightRadius(akLight) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akLight` | `Light` | ✓ |  |

### `GetLightRGB(akLight) → Int[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akLight` | `Light` | ✓ |  |

### `GetLightShadowDepthBias(akLightObject) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akLightObject` | `ObjectReference` | ✓ |  |

### `GetLightType(akLight) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akLight` | `Light` | ✓ |  |

### `GetLinkedChildren(akRef, akKeyword) → ObjectReference[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `akKeyword` | `Keyword` | ✓ |  |

### `GetLocalGravity() → Float[]`

**Flags:** Native Global

### `GetLocalGravityActor(akActor) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetMagicEffectSound(akMagicEffect, aiType) → SoundDescriptor`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akMagicEffect` | `MagicEffect` | ✓ |  |
| `aiType` | `Int` | ✓ |  |

### `GetMagicEffectSource(akRef, akEffect) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `akEffect` | `MagicEffect` | ✓ |  |

### `GetMaterialType(akRef, asNodeName) → String[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `asNodeName` | `String` |  | `""` |

### `GetMembraneFillTexture(akEffectShader) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEffectShader` | `EffectShader` | ✓ |  |

### `GetMembraneHolesTexture(akEffectShader) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEffectShader` | `EffectShader` | ✓ |  |

### `GetMembranePaletteTexture(akEffectShader) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEffectShader` | `EffectShader` | ✓ |  |

### `GetMenuContainer() → ObjectReference`

**Flags:** Native Global

### `GetMotionType(akRef) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |

### `GetMount(akActor) → Actor`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetNthPerk(akBase, aiIndex) → Perk`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akBase` | `Actorbase` | ✓ |  |
| `aiIndex` | `Int` | ✓ |  |

### `GetNumActorsInHigh() → Int`

**Flags:** Native Global

### `GetNumActorsWithEffectInRange(akRef, afRadius, akEffect, abignorePlayer) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `afRadius` | `Float` | ✓ |  |
| `akEffect` | `MagicEffect` | ✓ |  |
| `abignorePlayer` | `Bool` | ✓ |  |

### `GetObjectUnderFeet(akActor) → ObjectReference`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetOffersServices(akActor) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetPackageIdles(akPackage) → Idle[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akPackage` | `Package` | ✓ |  |

### `GetPackageType(akPackage) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akPackage` | `Package` | ✓ |  |

### `GetPapyrusExtenderVersion() → Int[]`

**Flags:** Native Global

(major,minor,patch / 5,10,0)

### `GetParentLocation(akLoc) → Location`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akLoc` | `Location` | ✓ |  |

### `GetParticleFullCount(akEffectShader) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEffectShader` | `EffectShader` | ✓ |  |

### `GetParticlePaletteTexture(akEffectShader) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEffectShader` | `EffectShader` | ✓ |  |

### `GetParticlePersistentCount(akEffectShader) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEffectShader` | `EffectShader` | ✓ |  |

### `GetParticleShaderTexture(akEffectShader) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEffectShader` | `EffectShader` | ✓ |  |

### `GetPerkCount(akBase) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akBase` | `Actorbase` | ✓ |  |

### `GetPlayerFollowers() → Actor[]`

**Flags:** Native Global

### `GetPrimaryActorValue(akMagicEffect) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akMagicEffect` | `MagicEffect` | ✓ |  |

### `GetProjectileGravity(akProjectile) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akProjectile` | `Projectile` | ✓ |  |

### `GetProjectileImpactForce(akProjectile) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akProjectile` | `Projectile` | ✓ |  |

### `GetProjectileRange(akProjectile) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akProjectile` | `Projectile` | ✓ |  |

### `GetProjectileSpeed(akProjectile) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akProjectile` | `Projectile` | ✓ |  |

### `GetProjectileType(akProjectile) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akProjectile` | `Projectile` | ✓ |  |

### `GetQuestItems(akRef, abNoEquipped, abNoFavorited) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `abNoEquipped` | `Bool` |  | `false` |
| `abNoFavorited` | `Bool` |  | `false` |

### `GetRandomActorFromRef(akRef, afRadius, abIgnorePlayer) → Actor`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `afRadius` | `Float` | ✓ |  |
| `abIgnorePlayer` | `Bool` | ✓ |  |

### `GetRefAliases(akRef) → Alias[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |

### `GetRefCount(akRef) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |

### `GetRelationships(akBase, akAssocType) → Actorbase[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akBase` | `Actorbase` | ✓ |  |
| `akAssocType` | `AssociationType` | ✓ |  |

### `GetRider(akActor) → Actor`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetRunningPackage(akActor) → Package`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetScriptsAttachedToActiveEffect(akActiveEffect) → String[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `GetScriptsAttachedToAlias(akAlias) → String[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `GetScriptsAttachedToForm(akForm) → String[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `GetSecondaryActorValue(akMagicEffect) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akMagicEffect` | `MagicEffect` | ✓ |  |

### `GetSkinColor(akActor) → ColorForm`

**Flags:** Native Global

DEPRECATED

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetSkinRGB(akActor) → Int[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetSortedActorNames(akKeyword, asPlural, abInvertKeyword) → String[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akKeyword` | `Keyword` | ✓ |  |
| `asPlural` | `String` |  | `"s)"` |
| `abInvertKeyword` | `Bool` | ✓ |  |

### `GetSortedNPCNames(aiActorBases, asPlural) → String[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiActorBases` | `ActorBase[]` | ✓ |  |
| `asPlural` | `String` |  | `"s)"` |

### `GetSpellType(akSpell) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSpell` | `Spell` | ✓ |  |

### `GetStoredSoulSize(akRef) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |

### `GetSurfaceMaterialType(afX, afY, afZ) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afX` | `Float` | ✓ |  |
| `afY` | `Float` | ✓ |  |
| `afZ` | `Float` | ✓ |  |

### `GetSystemTime() → Int[]`

**Flags:** Native Global

### `GetTimeDead(akActor) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetTimeOfDeath(akActor) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetVendorFaction(akActor) → Faction`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetVendorFactionContainer(akVendorFaction) → ObjectReference`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akVendorFaction` | `Faction` | ✓ |  |

### `GetWeatherType(akWeather) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akWeather` | `Weather` |  |  |

### `GetWindSpeedAsFloat(akWeather) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akWeather` | `Weather` | ✓ |  |

### `GetWindSpeedAsInt(akWeather) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akWeather` | `Weather` | ✓ |  |

### `GivePlayerSpellBook()`

**Flags:** Native Global

### `HasActiveMagicEffect(akActor, akEffect) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `akEffect` | `MagicEffect` | ✓ |  |

### `HasActiveSpell(akActor, akSpell) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `akSpell` | `Spell` | ✓ |  |

### `HasArtObject(akRef, akArtObject, abActive) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `akArtObject` | `Art` | ✓ |  |
| `abActive` | `Bool` |  | `false` |

### `HasDeferredKill(akActor) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `HasEffectShader(akRef, akShader, abActive) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `akShader` | `EffectShader` | ✓ |  |
| `abActive` | `Bool` |  | `false` |

### `HasMagicEffectWithArchetype(akActor, asArchetype) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `asArchetype` | `String` | ✓ |  |

### `HasNiExtraData(akRef, asName) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `asName` | `String` | ✓ |  |

### `HasSkin(akActor, akArmorToCheck) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `akArmorToCheck` | `Armor` | ✓ |  |

### `HideMenu(asMenuName)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asMenuName` | `String` | ✓ |  |

### `IntToString(aiValue, abHex) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiValue` | `Int` | ✓ |  |
| `abHex` | `Bool` | ✓ |  |

### `IsActorInScene(akScene, akActor) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akScene` | `Scene` | ✓ |  |
| `akActor` | `Actor` | ✓ |  |

### `IsActorInWater(akActor) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `IsActorUnderwater(akActor) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `IsCasting(akRef, akMagicItem) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `akMagicItem` | `Form` | ✓ |  |

### `IsDetectedByAnyone(akActor) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `IsEffectShaderFlagSet(akEffectShader, aiFlag) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEffectShader` | `EffectShader` | ✓ |  |
| `aiFlag` | `Int` | ✓ |  |

### `IsFormInMod(akForm, asModName) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `asModName` | `String` | ✓ |  |

### `IsGeneratedForm(akForm) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `IsHazardFlagSet(akHazard, aiFlag) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akHazard` | `Hazard` | ✓ |  |
| `aiFlag` | `Int` | ✓ |  |

### `IsLimbGone(akActor, aiLimb) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `aiLimb` | `Int` | ✓ |  |

### `IsLoadDoor(akRef) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |

### `IsPluginFound(akName) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akName` | `String` | ✓ |  |

### `IsPowerAttacking(akActor) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `IsQuadruped(akActor) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `IsQuestItem(akRef) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |

### `IsRecordFlagSet(akForm, aiFlag) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `aiFlag` | `Int` | ✓ |  |

### `IsRefInWater(akRef) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |

### `IsRefNodeInWater(akRef, asNodeName) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `asNodeName` | `String` | ✓ |  |

### `IsRefUnderwater(akRef) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |

### `IsScriptAttachedToActiveEffect(akActiveEffect, asScriptName) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |
| `asScriptName` | `String` | ✓ |  |

### `IsScriptAttachedToAlias(akAlias, asScriptName) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |
| `asScriptName` | `String` | ✓ |  |

### `IsScriptAttachedToForm(akForm, asScriptName) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `asScriptName` | `String` | ✓ |  |

### `IsShowingMenus() → Bool`

**Flags:** Native Global

### `IsSoulTrapped(akActor) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `IsSurvivalModeActive() → Bool`

**Flags:** Native Global

### `IsVIP(akRef) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |

### `KillNoWait(akActor)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `LaunchArrow(akActor, akAmmo, akWeapon, asNodeName, aiSource, akTarget, akPoison)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `akAmmo` | `Ammo` | ✓ |  |
| `akWeapon` | `Weapon` | ✓ |  |
| `asNodeName` | `String` |  | `""` |
| `aiSource` | `Int` |  | `-1` |
| `akTarget` | `ObjectReference` |  |  |
| `akPoison` | `Potion` |  |  |

### `LaunchSpell(akActor, akSpell, aiSource)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `akSpell` | `Spell` | ✓ |  |
| `aiSource` | `Int` | ✓ |  |

### `MarkItemAsFavorite(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `MixColorWithSkinTone(akActor, akColor, abManualMode, afPercentage)`

**Flags:** Native Global

DEPRECATED

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `akColor` | `ColorForm` | ✓ |  |
| `abManualMode` | `Bool` | ✓ |  |
| `afPercentage` | `Float` | ✓ |  |

### `MoveToNearestNavmeshLocation(akRef)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |

### `PlayDebugShader(akRef, afRGBA)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `afRGBA` | `Float[]` | ✓ |  |

### `PreventActorDetecting(akActor)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `PreventActorDetection(akActor)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `RemoveAddedSpells(akActor, modName, keywords, abMatchAll)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `modName` | `String` | ✓ |  |
| `keywords` | `Keyword[]` | ✓ |  |
| `abMatchAll` | `Bool` | ✓ |  |

### `RemoveAllModItems(akRef, asModName, abOnlyUnequip)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `asModName` | `String` | ✓ |  |
| `abOnlyUnequip` | `Bool` |  | `false` |

### `RemoveArmorOfType(akActor, afArmorType, aiSlotsToSkip, abEquippedOnly)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `afArmorType` | `Int` | ✓ |  |
| `aiSlotsToSkip` | `Int[]` | ✓ |  |
| `abEquippedOnly` | `Bool` | ✓ |  |

### `RemoveBasePerk(akActor, akPerk) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `akPerk` | `Perk` | ✓ |  |

### `RemoveBaseSpell(akActor, akSpell) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `akSpell` | `Spell` | ✓ |  |

### `RemoveConditionList(akForm, aiIndex, asConditionList)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `aiIndex` | `Int` | ✓ |  |
| `asConditionList` | `String[]` | ✓ |  |

### `RemoveEffectItemFromEnchantment(akEnchantment, akEnchantmentToMatchFrom, aiIndex)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEnchantment` | `Enchantment` | ✓ |  |
| `akEnchantmentToMatchFrom` | `Enchantment` | ✓ |  |
| `aiIndex` | `Int` | ✓ |  |

### `RemoveEffectItemFromPotion(akPotion, akPotionToMatchFrom, aiIndex)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akPotion` | `Potion` | ✓ |  |
| `akPotionToMatchFrom` | `Potion` | ✓ |  |
| `aiIndex` | `Int` | ✓ |  |

### `RemoveEffectItemFromScroll(akScroll, akScrollToMatchFrom, aiIndex)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akScroll` | `Scroll` | ✓ |  |
| `akScrollToMatchFrom` | `Scroll` | ✓ |  |
| `aiIndex` | `Int` | ✓ |  |

### `RemoveEffectItemFromSpell(akSpell, akSpellToMatchFrom, aiIndex)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSpell` | `Spell` | ✓ |  |
| `akSpellToMatchFrom` | `Spell` | ✓ |  |
| `aiIndex` | `Int` | ✓ |  |

### `RemoveKeywordFromRef(akRef, akKeyword) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `akKeyword` | `Keyword` | ✓ |  |

### `RemoveKeywordOnForm(akForm, akKeyword) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `akKeyword` | `Keyword` | ✓ |  |

### `RemoveListFromContainer(akRef, akList, abNoEquipped, abNoFavorited, abNoQuestItem, akDestination)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `akList` | `FormList` | ✓ |  |
| `abNoEquipped` | `Bool` |  | `false` |
| `abNoFavorited` | `Bool` |  | `false` |
| `abNoQuestItem` | `Bool` |  | `false` |
| `akDestination` | `ObjectReference` |  |  |

### `RemoveMagicEffectFromEnchantment(akEnchantment, akMagicEffect, afMagnitude, aiArea, aiDuration, afCost)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEnchantment` | `Enchantment` | ✓ |  |
| `akMagicEffect` | `MagicEffect` | ✓ |  |
| `afMagnitude` | `Float` | ✓ |  |
| `aiArea` | `Int` | ✓ |  |
| `aiDuration` | `Int` | ✓ |  |
| `afCost` | `Float` |  | `0` |

### `RemoveMagicEffectFromPotion(akPotion, akMagicEffect, afMagnitude, aiArea, aiDuration, afCost)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akPotion` | `Potion` | ✓ |  |
| `akMagicEffect` | `MagicEffect` | ✓ |  |
| `afMagnitude` | `Float` | ✓ |  |
| `aiArea` | `Int` | ✓ |  |
| `aiDuration` | `Int` | ✓ |  |
| `afCost` | `Float` |  | `0` |

### `RemoveMagicEffectFromScroll(akScroll, akMagicEffect, afMagnitude, aiArea, aiDuration, afCost)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akScroll` | `Scroll` | ✓ |  |
| `akMagicEffect` | `MagicEffect` | ✓ |  |
| `afMagnitude` | `Float` | ✓ |  |
| `aiArea` | `Int` | ✓ |  |
| `aiDuration` | `Int` | ✓ |  |
| `afCost` | `Float` |  | `0` |

### `RemoveMagicEffectFromSpell(akSpell, akMagicEffect, afMagnitude, aiArea, aiDuration, afCost)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSpell` | `Spell` | ✓ |  |
| `akMagicEffect` | `MagicEffect` | ✓ |  |
| `afMagnitude` | `Float` | ✓ |  |
| `aiArea` | `Int` | ✓ |  |
| `aiDuration` | `Int` | ✓ |  |
| `afCost` | `Float` |  | `0` |

### `RemovePackageIdle(akPackage, akIdle)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akPackage` | `Package` | ✓ |  |
| `akIdle` | `Idle` | ✓ |  |

### `ReplaceArmorTextureSet(akActor, akArmor, akSourceTXST, akTargetTXST, aiTextureType)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `akArmor` | `Armor` | ✓ |  |
| `akSourceTXST` | `TextureSet` | ✓ |  |
| `akTargetTXST` | `TextureSet` | ✓ |  |
| `aiTextureType` | `Int` |  | `-1` |

### `ReplaceFaceTextureSet(akActor, akMaleTXST, akFemaleTXST, aiTextureType)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `akMaleTXST` | `TextureSet` | ✓ |  |
| `akFemaleTXST` | `TextureSet` | ✓ |  |
| `aiTextureType` | `Int` |  | `-1` |

### `ReplaceKeywordOnForm(akForm, akKeywordAdd, akKeywordRemove)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `akKeywordAdd` | `Keyword` | ✓ |  |
| `akKeywordRemove` | `Keyword` | ✓ |  |

### `ReplaceKeywordOnRef(akRef, akKeywordAdd, akKeywordRemove)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `akKeywordAdd` | `Keyword` | ✓ |  |
| `akKeywordRemove` | `Keyword` | ✓ |  |

### `ReplaceSkinTextureSet(akActor, akMaleTXST, akFemaleTXST, aiSlotMask, aiTextureType)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `akMaleTXST` | `TextureSet` | ✓ |  |
| `akFemaleTXST` | `TextureSet` | ✓ |  |
| `aiSlotMask` | `Int` | ✓ |  |
| `aiTextureType` | `Int` |  | `-1` |

### `ResetActor3D(akActor, asFolderName) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `asFolderName` | `String` | ✓ |  |

### `ResetActorDetecting(akActor)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `ResetActorDetection(akActor)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `ScaleObject3D(akRef, asNodeName, afScale)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `asNodeName` | `String` | ✓ |  |
| `afScale` | `Float` | ✓ |  |

### `SetActorRefraction(akActor, afRefraction)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `afRefraction` | `Float` | ✓ |  |

### `SetAddonModels(akEffectShader, akDebris)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEffectShader` | `EffectShader` | ✓ |  |
| `akDebris` | `Debris` | ✓ |  |

### `SetArtObject(akEffect, akArt)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEffect` | `VisualEffect` | ✓ |  |
| `akArt` | `Art` | ✓ |  |

### `SetAssociatedForm(akMagicEffect, akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akMagicEffect` | `MagicEffect` | ✓ |  |
| `akForm` | `Form` | ✓ |  |

### `SetBaseObject(akRef, akBaseObject)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `akBaseObject` | `Form` | ✓ |  |

### `SetBookCantBeTakenFlag(akBook)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akBook` | `Book` | ✓ |  |

### `SetCellNorthRotation(akCell, afAngle)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akCell` | `Cell` | ✓ |  |
| `afAngle` | `Float` | ✓ |  |

### `SetCollisionLayer(akRef, asNodeName, aiCollisionLayer)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `asNodeName` | `String` | ✓ |  |
| `aiCollisionLayer` | `Int` | ✓ |  |

### `SetConditionList(akForm, aiIndex, asConditionList)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `aiIndex` | `Int` | ✓ |  |
| `asConditionList` | `String[]` | ✓ |  |

### `SetDeathItem(akBase, akLeveledItem)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akBase` | `Actorbase` | ✓ |  |
| `akLeveledItem` | `LeveledItem` | ✓ |  |

### `SetDoorDestination(akRef, akDoor) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `akDoor` | `ObjectReference` | ✓ |  |

### `SetEffectShaderDuration(akRef, akShader, afTime, abAbsolute)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `akShader` | `EffectShader` | ✓ |  |
| `afTime` | `Float` | ✓ |  |
| `abAbsolute` | `Bool` | ✓ |  |

### `SetEffectShaderFlag(akEffectShader, aiFlag)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEffectShader` | `EffectShader` | ✓ |  |
| `aiFlag` | `Int` | ✓ |  |

### `SetEnchantmentMagicEffect(akEnchantment, akMagicEffect, aiIndex)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEnchantment` | `Enchantment` | ✓ |  |
| `akMagicEffect` | `MagicEffect` | ✓ |  |
| `aiIndex` | `Int` | ✓ |  |

### `SetEquippedWeaponPoison(akActor, akPoison, abLeftHand) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `akPoison` | `Potion` | ✓ |  |
| `abLeftHand` | `Bool` | ✓ |  |

### `SetEquippedWeaponPoisonCount(akActor, aiCount, abLeftHand) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `aiCount` | `Int` | ✓ |  |
| `abLeftHand` | `Bool` | ✓ |  |

### `SetFastTravelDisabled(abDisable) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abDisable` | `Bool` | ✓ |  |

### `SetFastTravelTargetFormID(aiDestinationFormID) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiDestinationFormID` | `Int` | ✓ |  |

### `SetFastTravelTargetRef(akDestination) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akDestination` | `ObjectReference` | ✓ |  |

### `SetFastTravelTargetString(asDestination) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asDestination` | `String` | ✓ |  |

### `SetFastTravelWaitTimeout(afTimeout) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afTimeout` | `Float` | ✓ |  |

### `SetFootstepSet(akArma, akFootstepSet)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArma` | `ArmorAddon` | ✓ |  |
| `akFootstepSet` | `FootstepSet` | ✓ |  |

### `SetHairColor(akActor, akColor)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `akColor` | `ColorForm` | ✓ |  |

### `SetHazardArt(akHazard, asPath)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akHazard` | `Hazard` | ✓ |  |
| `asPath` | `String` | ✓ |  |

### `SetHazardFlag(akHazard, aiFlag)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akHazard` | `Hazard` | ✓ |  |
| `aiFlag` | `Int` | ✓ |  |

### `SetHazardIMOD(akHazard, akIMOD)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akHazard` | `Hazard` | ✓ |  |
| `akIMOD` | `ImageSpaceModifier` | ✓ |  |

### `SetHazardIMODRadius(akHazard, afRadius)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akHazard` | `Hazard` | ✓ |  |
| `afRadius` | `Float` | ✓ |  |

### `SetHazardIPDS(akHazard, akIPDS)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akHazard` | `Hazard` | ✓ |  |
| `akIPDS` | `ImpactDataSet` | ✓ |  |

### `SetHazardLifetime(akHazard, afLifetime)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akHazard` | `Hazard` | ✓ |  |
| `afLifetime` | `Float` | ✓ |  |

### `SetHazardLight(akHazard, akLight)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akHazard` | `Hazard` | ✓ |  |
| `akLight` | `Light` | ✓ |  |

### `SetHazardLimit(akHazard, aiLimit)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akHazard` | `Hazard` | ✓ |  |
| `aiLimit` | `Int` | ✓ |  |

### `SetHazardRadius(akHazard, afRadius)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akHazard` | `Hazard` | ✓ |  |
| `afRadius` | `Float` | ✓ |  |

### `SetHazardSound(akHazard, akSound)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akHazard` | `Hazard` | ✓ |  |
| `akSound` | `SoundDescriptor` | ✓ |  |

### `SetHazardSpell(akHazard, akspell)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akHazard` | `Hazard` | ✓ |  |
| `akspell` | `Spell` | ✓ |  |

### `SetHazardTargetInterval(akHazard, afInterval)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akHazard` | `Hazard` | ✓ |  |
| `afInterval` | `Float` | ✓ |  |

### `SetHeadPartAlpha(akActor, aiPartType, afAlpha)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `aiPartType` | `Int` | ✓ |  |
| `afAlpha` | `Float` | ✓ |  |

### `SetHeadPartTextureSet(akActor, headpartTXST, aiType)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `headpartTXST` | `TextureSet` | ✓ |  |
| `aiType` | `Int` | ✓ |  |

### `SetKey(akRef, akKey)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `akKey` | `Key` | ✓ |  |

### `SetLightColor(akLight, akColorform)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akLight` | `Light` | ✓ |  |
| `akColorform` | `ColorForm` | ✓ |  |

### `SetLightFade(akLight, afRange)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akLight` | `Light` | ✓ |  |
| `afRange` | `Float` | ✓ |  |

### `SetLightFOV(akLight, afFOV)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akLight` | `Light` | ✓ |  |
| `afFOV` | `Float` | ✓ |  |

### `SetLightingTemplate(akCell, akLightingTemplate)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akCell` | `Cell` | ✓ |  |
| `akLightingTemplate` | `LightingTemplate` | ✓ |  |

### `SetLightRadius(akLight, afRadius)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akLight` | `Light` | ✓ |  |
| `afRadius` | `Float` | ✓ |  |

### `SetLightRGB(akLight, aiRGB)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akLight` | `Light` | ✓ |  |
| `aiRGB` | `Int[]` | ✓ |  |

### `SetLightShadowDepthBias(akLightObject, afDepthBias)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akLightObject` | `ObjectReference` | ✓ |  |
| `afDepthBias` | `Float` | ✓ |  |

### `SetLightType(akLight, aiLightType)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akLight` | `Light` | ✓ |  |
| `aiLightType` | `Int` | ✓ |  |

### `SetLinearVelocity(akActor, afX, afY, afZ)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `afX` | `Float` | ✓ |  |
| `afY` | `Float` | ✓ |  |
| `afZ` | `Float` | ✓ |  |

### `SetLinkedRef(akRef, akTargetRef, akKeyword)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `akTargetRef` | `ObjectReference` | ✓ |  |
| `akKeyword` | `Keyword` |  |  |

### `SetLocalGravity(afXAxis, afYAxis, afZAxis)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afXAxis` | `Float` | ✓ |  |
| `afYAxis` | `Float` | ✓ |  |
| `afZAxis` | `Float` | ✓ |  |

### `SetLocalGravityActor(akActor, afValue, abDisableGravityOnGround)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `afValue` | `Float` | ✓ |  |
| `abDisableGravityOnGround` | `Bool` | ✓ |  |

### `SetMagicEffectSound(akMagicEffect, akSoundDescriptor, aiType)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akMagicEffect` | `MagicEffect` | ✓ |  |
| `akSoundDescriptor` | `SoundDescriptor` | ✓ |  |
| `aiType` | `Int` | ✓ |  |

### `SetMaterialType(akRef, asNewMaterial, asOldMaterial, asNodeName)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `asNewMaterial` | `String` | ✓ |  |
| `asOldMaterial` | `String` |  | `""` |
| `asNodeName` | `String` |  | `""` |

### `SetMembraneColorKeyData(akEffectShader, aiColorKey, aiRGB, afAlpha, afTime)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEffectShader` | `EffectShader` | ✓ |  |
| `aiColorKey` | `Int` | ✓ |  |
| `aiRGB` | `Int[]` | ✓ |  |
| `afAlpha` | `Float` | ✓ |  |
| `afTime` | `Float` | ✓ |  |

### `SetMembraneFillTexture(akEffectShader, asTextureName)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEffectShader` | `EffectShader` | ✓ |  |
| `asTextureName` | `String` | ✓ |  |

### `SetMembraneHolesTexture(akEffectShader, asTextureName)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEffectShader` | `EffectShader` | ✓ |  |
| `asTextureName` | `String` | ✓ |  |

### `SetMembranePaletteTexture(akEffectShader, asTextureName)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEffectShader` | `EffectShader` | ✓ |  |
| `asTextureName` | `String` | ✓ |  |

### `SetObjectiveText(akQuest, asText, aiIndex)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akQuest` | `Quest` | ✓ |  |
| `asText` | `String` | ✓ |  |
| `aiIndex` | `Int` | ✓ |  |

### `SetParentLocation(akLoc, akNewLoc)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akLoc` | `Location` | ✓ |  |
| `akNewLoc` | `Location` | ✓ |  |

### `SetParticleColorKeyData(akEffectShader, aiColorKey, aiRGB, afAlpha, afTime)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEffectShader` | `EffectShader` | ✓ |  |
| `aiColorKey` | `Int` | ✓ |  |
| `aiRGB` | `Int[]` | ✓ |  |
| `afAlpha` | `Float` | ✓ |  |
| `afTime` | `Float` | ✓ |  |

### `SetParticleFullCount(akEffectShader, afParticleCount)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEffectShader` | `EffectShader` | ✓ |  |
| `afParticleCount` | `Float` | ✓ |  |

### `SetParticlePaletteTexture(akEffectShader, asTextureName)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEffectShader` | `EffectShader` | ✓ |  |
| `asTextureName` | `String` | ✓ |  |

### `SetParticlePersistentCount(akEffectShader, afParticleCount)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEffectShader` | `EffectShader` | ✓ |  |
| `afParticleCount` | `Float` | ✓ |  |

### `SetParticleShaderTexture(akEffectShader, asTextureName)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEffectShader` | `EffectShader` | ✓ |  |
| `asTextureName` | `String` | ✓ |  |

### `SetPotionMagicEffect(akPotion, akMagicEffect, aiIndex)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akPotion` | `Potion` | ✓ |  |
| `akMagicEffect` | `MagicEffect` | ✓ |  |
| `aiIndex` | `Int` | ✓ |  |

### `SetProjectileGravity(akProjectile, afGravity)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akProjectile` | `Projectile` | ✓ |  |
| `afGravity` | `Float` | ✓ |  |

### `SetProjectileImpactForce(akProjectile, afImpactForce)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akProjectile` | `Projectile` | ✓ |  |
| `afImpactForce` | `Float` | ✓ |  |

### `SetProjectileRange(akProjectile, afRange)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akProjectile` | `Projectile` | ✓ |  |
| `afRange` | `Float` | ✓ |  |

### `SetProjectileSpeed(akProjectile, afSpeed)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akProjectile` | `Projectile` | ✓ |  |
| `afSpeed` | `Float` | ✓ |  |

### `SetReadFlag(akBook)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akBook` | `Book` | ✓ |  |

### `SetRecordFlag(akForm, aiFlag)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `aiFlag` | `Int` | ✓ |  |

### `SetScrollMagicEffect(akScroll, akMagicEffect, aiIndex)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akScroll` | `Scroll` | ✓ |  |
| `akMagicEffect` | `MagicEffect` | ✓ |  |
| `aiIndex` | `Int` | ✓ |  |

### `SetShaderType(akRef, akTemplate, asDiffusePath, aiShaderType, aiTextureType, abNoWeapons, abNoAlphaProperty)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `akTemplate` | `ObjectReference` | ✓ |  |
| `asDiffusePath` | `String` | ✓ |  |
| `aiShaderType` | `Int` | ✓ |  |
| `aiTextureType` | `Int` | ✓ |  |
| `abNoWeapons` | `Bool` | ✓ |  |
| `abNoAlphaProperty` | `Bool` | ✓ |  |

### `SetSkinAlpha(akActor, afAlpha)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `afAlpha` | `Float` | ✓ |  |

### `SetSkinColor(akActor, akColor)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `akColor` | `ColorForm` | ✓ |  |

### `SetSoulTrapped(akActor, abTrapped)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `abTrapped` | `Bool` | ✓ |  |

### `SetSoundDescriptor(akSound, akSoundDescriptor)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSound` | `Sound` | ✓ |  |
| `akSoundDescriptor` | `SoundDescriptor` | ✓ |  |

### `SetSpellCastingType(akSpell, aiType)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSpell` | `Spell` | ✓ |  |
| `aiType` | `Int` | ✓ |  |

### `SetSpellDeliveryType(akSpell, aiType)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSpell` | `Spell` | ✓ |  |
| `aiType` | `Int` | ✓ |  |

### `SetSpellMagicEffect(akSpell, akMagicEffect, aiIndex)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSpell` | `Spell` | ✓ |  |
| `akMagicEffect` | `MagicEffect` | ✓ |  |
| `aiIndex` | `Int` | ✓ |  |

### `SetSpellType(akSpell, aiType)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSpell` | `Spell` | ✓ |  |
| `aiType` | `Int` | ✓ |  |

### `SetupBodyPartGeometry(akRef, akActor)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `akActor` | `actor` | ✓ |  |

### `ShowBookMenu(akBook)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akBook` | `Book` | ✓ |  |

### `ShowMenu(asMenuName)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asMenuName` | `String` | ✓ |  |

### `SortArrayString(asStrings) → String[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asStrings` | `String[]` | ✓ |  |

### `StopAllShaders(akRef)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |

### `StopArtObject(akRef, akArt)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `akArt` | `Art` | ✓ |  |

### `StringToInt(asString) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asString` | `String` | ✓ |  |

### `ToggleChildNode(akRef, asNodeName, abDisable)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `asNodeName` | `String` | ✓ |  |
| `abDisable` | `Bool` | ✓ |  |

### `ToggleHairWigs(akActor, abDisable)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `abDisable` | `Bool` | ✓ |  |

### `ToggleOpenSleepWaitMenu(abOpenSleepMenu)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abOpenSleepMenu` | `Bool` | ✓ |  |

### `UnequipAllOfType(akActor, afArmorType, aiSlotsToSkip)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `afArmorType` | `Int` | ✓ |  |
| `aiSlotsToSkip` | `Int[]` | ✓ |  |

### `UnmarkItemAsFavorite(akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `UpdateCrosshairs()`

**Flags:** Native Global

### `UpdateHitEffectArtNode(akRef, akArt, asNewNode, afTranslate, afRotate, afRelativeScale)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `akArt` | `Art` | ✓ |  |
| `asNewNode` | `String` | ✓ |  |
| `afTranslate` | `Float[]` | ✓ |  |
| `afRotate` | `Float[]` | ✓ |  |
| `afRelativeScale` | `Float` |  | `1` |
