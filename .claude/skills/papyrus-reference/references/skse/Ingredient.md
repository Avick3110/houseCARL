# `Ingredient`

**Source:** `skse` (Skyrim Script Extender x64) • **Extends:** `Form`

---

## Functions

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

### `GetIsNthEffectKnown(index) → Bool`

**Flags:** Native

determines whether the player knows this effect

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |

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

SKSE64 additions built 2024-01-17 20:01:40.731000 UTC
return the number of the effects

### `IsHostile() → Bool`

**Flags:** Native

Is this ingredient classified as hostile?

### `LearnAllEffects()`

**Flags:** Native

Flags the all effects as known by the player

### `LearnEffect(aiIndex)`

**Flags:** Native

Flags the effect with the given 0 based index as known by the player

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiIndex` | `Int` | ✓ |  |

### `LearnNextEffect() → Int`

**Flags:** Native

Flags the next unknown effect as known by the player, returning index of effect learned

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
