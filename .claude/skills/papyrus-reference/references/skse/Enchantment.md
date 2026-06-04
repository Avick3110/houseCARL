# `Enchantment`

**Source:** `skse` (Skyrim Script Extender x64) • **Extends:** `Form` • **Flags:** Hidden

---

## Functions

### `GetBaseEnchantment() → Enchantment`

**Flags:** Native

returns the base enchantment of this enchantment

### `GetCostliestEffectIndex() → Int`

**Flags:** Native

return the index of the costliest effect

### `GetKeywordRestrictions() → FormList`

**Flags:** Native

Returns a Formlist of Keywords

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

Is this enchantment classified as hostile?

### `SetKeywordRestrictions(newKeywordList)`

**Flags:** Native

Sets the FormList of keywords

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `newKeywordList` | `FormList` | ✓ |  |

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
