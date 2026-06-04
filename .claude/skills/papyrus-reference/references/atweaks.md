# `ASR_PapyrusFunctions`

**Source:** `atweaks` (aTweaks and Utilities) • **Flags:** Hidden

---

## Global Functions

### `AdjustActiveEffectDuration(a_activeEffect, a_duration)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_activeEffect` | `ActiveMagicEffect` | ✓ |  |
| `a_duration` | `Float` | ✓ |  |

### `AdjustActiveEffectMagnitude(a_activeEffect, a_power)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_activeEffect` | `ActiveMagicEffect` | ✓ |  |
| `a_power` | `Float` | ✓ |  |

### `CastSpellItemMult(akSource, akSpell, akEnchantment, akPotion, akIngredient, akTarget, a_effectiveness, a_override)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSource` | `Actor` | ✓ |  |
| `akSpell` | `Spell` | ✓ |  |
| `akEnchantment` | `Enchantment` | ✓ |  |
| `akPotion` | `Potion` | ✓ |  |
| `akIngredient` | `Ingredient` | ✓ |  |
| `akTarget` | `ObjectReference` | ✓ |  |
| `a_effectiveness` | `Float` | ✓ |  |
| `a_override` | `Float` | ✓ |  |

### `CastSpellMult(akSource, MagicItem, akTarget, a_effectiveness, a_override)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSource` | `Actor` | ✓ |  |
| `MagicItem` | `Form` | ✓ |  |
| `akTarget` | `ObjectReference` | ✓ |  |
| `a_effectiveness` | `Float` | ✓ |  |
| `a_override` | `Float` | ✓ |  |

### `GetEffectWasDualCast(a_activeEffect) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_activeEffect` | `ActiveMagicEffect` | ✓ |  |

### `GetEnchantChargeOverrideValue(a_enchant) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_enchant` | `Enchantment` | ✓ |  |

### `GetEnchantCostOverrideFlag(a_enchant) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_enchant` | `Enchantment` | ✓ |  |

### `GetEnchantCostOverrideValue(a_enchant) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_enchant` | `Enchantment` | ✓ |  |

### `setEnchantChargeOverrideValue(a_enchant, ChargeOverride)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_enchant` | `Enchantment` | ✓ |  |
| `ChargeOverride` | `Int` | ✓ |  |

### `SetEnchantCostOverrideFlag(a_enchant, ValueToSet)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_enchant` | `Enchantment` | ✓ |  |
| `ValueToSet` | `Bool` | ✓ |  |

### `setEnchantCostOverrideValue(a_enchant, CostOverride)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_enchant` | `Enchantment` | ✓ |  |
| `CostOverride` | `Int` | ✓ |  |
