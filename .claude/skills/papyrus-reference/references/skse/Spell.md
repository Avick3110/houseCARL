# `Spell`

**Source:** `skse` (Skyrim Script Extender x64) • **Extends:** `Form` • **Flags:** Hidden

---

## Functions

### `Cast(akSource, akTarget)`

**Flags:** Native

Cast this spell from an ObjectReference, optionally toward another.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSource` | `ObjectReference` | ✓ |  |
| `akTarget` | `ObjectReference` |  |  |

### `GetCastTime() → Float`

**Flags:** Native

SKSE64 additions built 2024-01-17 20:01:40.731000 UTC
return the casting time

### `GetCostliestEffectIndex() → Int`

**Flags:** Native

return the index of the costliest effect

### `GetEffectAreas() → Int[]`

**Flags:** Native

Returns all the areas of this object in order

### `GetEffectDurations() → Int[]`

**Flags:** Native

Returns all the durations of this object in order

### `GetEffectiveMagickaCost(caster) → Int`

**Flags:** Native

return the effective magicka cost of the spell for given caster

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `caster` | `Actor` | ✓ |  |

### `GetEffectMagnitudes() → Float[]`

**Flags:** Native

Returns all the magnitudes of this object in order

### `GetEquipType() → EquipSlot`

**Flags:** Native

Returns the particular equipslot type

### `GetMagicEffects() → MagicEffect[]`

**Flags:** Native

Returns all the magic effects of this object in order

### `GetMagickaCost() → Int`

**Flags:** Native

return the base magicka cost of the spell

### `GetNthEffectArea(index) → Int`

**Flags:** Native

return the area of the specified effect

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |

### `GetNthEffectDuration(index) → Int`

**Flags:** Native

return the duration of the specified effect

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |

### `GetNthEffectMagicEffect(index) → MagicEffect`

**Flags:** Native

return the magic effect of the specified effect

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |

### `GetNthEffectMagnitude(index) → Float`

**Flags:** Native

return the magnitude of the specified effect

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |

### `GetNumEffects() → Int`

**Flags:** Native

return the number of the effects

### `GetPerk() → Perk`

**Flags:** Native

return the perk associated with the spell

### `IsHostile() → Bool`

**Flags:** Native

Is this spell classified as hostile?

### `Preload()`

**Flags:** Native

Preload the art for this spell. Useful for spells you equip & unequip on the player.
Warning: Misuse of this function can lead to erroneous behavior as well as excessive
memory consumption. It's best to avoid using this. This function will likely be
deprecated in the future.

### `RemoteCast(akSource, akBlameActor, akTarget)`

**Flags:** Native

Cast this spell from an ObjectReference, optionally toward another, and blame it on a particular actor.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSource` | `ObjectReference` | ✓ |  |
| `akBlameActor` | `Actor` | ✓ |  |
| `akTarget` | `ObjectReference` |  |  |

### `SetEquipType(type)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `type` | `EquipSlot` | ✓ |  |

### `SetNthEffectArea(index, value)`

**Flags:** Native

sets the area of the specified effect

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |
| `value` | `Int` | ✓ |  |

### `SetNthEffectDuration(index, value)`

**Flags:** Native

sets the duration of the specified effect

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |
| `value` | `Int` | ✓ |  |

### `SetNthEffectMagnitude(index, value)`

**Flags:** Native

sets the magnitude of the specified effect

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |
| `value` | `Float` | ✓ |  |

### `Unload()`

**Flags:** Native

Unload the art for this spell. Call this only if you've previously called Preload.
Warning: Misuse of this function can lead to erroneous behavior including spell art
being unloaded while in use, and excessive memory consumption. It's best to avoid using this.
This function will likely be deprecated in the future.
