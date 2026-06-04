# `Scroll`

**Source:** `skse` (Skyrim Script Extender x64) • **Extends:** `Form` • **Flags:** Hidden

---

## Functions

### `Cast(akSource, akTarget)`

**Flags:** Native

Cast this scroll from an ObjectReference, optionally toward another.

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

### `GetEffectMagnitudes() → Float[]`

**Flags:** Native

Returns all the magnitudes of this object in order

### `GetEquipType() → EquipSlot`

**Flags:** Native

Returns the particular equipslot type

### `GetMagicEffects() → MagicEffect[]`

**Flags:** Native

Returns all the magic effects of this object in order

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
