# `Potion`

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

### `GetUseSound() → SoundDescriptor`

**Flags:** Native

gets the use sound of this potion

### `IsFood() → Bool`

**Flags:** Native

SKSE64 additions built 2024-01-17 20:01:40.731000 UTC
Is this potion classified as Food?

### `IsHostile() → Bool`

**Flags:** Native

Is this postion classified as hostile?

### `IsPoison() → Bool`

**Flags:** Native

Is this potion classified as Poison?

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
