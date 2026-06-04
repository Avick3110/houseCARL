# `WornObject`

**Source:** `skse` (Skyrim Script Extender x64) • **Flags:** Hidden

---

## Global Functions

### `CreateEnchantment(akActor, handSlot, slotMask, maxCharge, effects, magnitudes, areas, durations)`

**Flags:** Native Global

Creates a new enchantment on the item given the specified parameters
all arrays must be the same size
created enchantments are not purged from the save when removed or overwritten
exact same enchantments are re-used by the game

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `handSlot` | `Int` | ✓ |  |
| `slotMask` | `Int` | ✓ |  |
| `maxCharge` | `Float` | ✓ |  |
| `effects` | `MagicEffect[]` | ✓ |  |
| `magnitudes` | `Float[]` | ✓ |  |
| `areas` | `Int[]` | ✓ |  |
| `durations` | `Int[]` | ✓ |  |

### `GetDisplayName(akActor, handSlot, slotMask) → String`

**Flags:** Native Global

Returns the name of this reference
this is the name that is displayed

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `handSlot` | `Int` | ✓ |  |
| `slotMask` | `Int` | ✓ |  |

### `GetEnchantment(akActor, handSlot, slotMask) → Enchantment`

**Flags:** Native Global

Returns the player-made enchantment if there is one

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `handSlot` | `Int` | ✓ |  |
| `slotMask` | `Int` | ✓ |  |

### `GetItemCharge(akActor, handSlot, slotMask) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `handSlot` | `Int` | ✓ |  |
| `slotMask` | `Int` | ✓ |  |

### `GetItemHealthPercent(akActor, handSlot, slotMask) → Float`

**Flags:** Native Global

Tempering

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `handSlot` | `Int` | ✓ |  |
| `slotMask` | `Int` | ✓ |  |

### `GetItemMaxCharge(akActor, handSlot, slotMask) → Float`

**Flags:** Native Global

Works on any enchanted item

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `handSlot` | `Int` | ✓ |  |
| `slotMask` | `Int` | ✓ |  |

### `GetNthReferenceAlias(akActor, handSlot, slotMask, n) → ReferenceAlias`

**Flags:** Native Global

Returns the nth ReferenceAlias holding this reference

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `handSlot` | `Int` | ✓ |  |
| `slotMask` | `Int` | ✓ |  |
| `n` | `Int` | ✓ |  |

### `GetNumReferenceAliases(akActor, handSlot, slotMask) → Int`

**Flags:** Native Global

Returns the number of ref aliases holding this reference

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `handSlot` | `Int` | ✓ |  |
| `slotMask` | `Int` | ✓ |  |

### `GetPoison(akActor, handSlot, slotMask) → Potion`

**Flags:** Native Global

Returns the poison on the specified item

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `handSlot` | `Int` | ✓ |  |
| `slotMask` | `Int` | ✓ |  |

### `GetReferenceAliases(akActor, handSlot, slotMask) → ReferenceAlias[]`

**Flags:** Native Global

Returns all of the ReferenceAlias holding this reference

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `handSlot` | `Int` | ✓ |  |
| `slotMask` | `Int` | ✓ |  |

### `SetDisplayName(akActor, handSlot, slotMask, name, force) → Bool`

**Flags:** Native Global

Sets a reference's display name
returns false if force is false and the reference
is held by an alias using 'Stored Text' or 'Uses Stored Text'
Text Replacement does not use this name and may be lost if forced

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `handSlot` | `Int` | ✓ |  |
| `slotMask` | `Int` | ✓ |  |
| `name` | `String` | ✓ |  |
| `force` | `Bool` |  | `false` |

### `SetEnchantment(akActor, handSlot, slotMask, source, maxCharge)`

**Flags:** Native Global

Changes an item's player-made enchantment to something else
None enchantment will remove the existing enchantment
does not delete the custom enchantment, only removes it

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `handSlot` | `Int` | ✓ |  |
| `slotMask` | `Int` | ✓ |  |
| `source` | `Enchantment` | ✓ |  |
| `maxCharge` | `Float` | ✓ |  |

### `SetItemHealthPercent(akActor, handSlot, slotMask, health)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `handSlot` | `Int` | ✓ |  |
| `slotMask` | `Int` | ✓ |  |
| `health` | `Float` | ✓ |  |

### `SetItemMaxCharge(akActor, handSlot, slotMask, maxCharge)`

**Flags:** Native Global

Charges
Only works on items that have user-enchants

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `handSlot` | `Int` | ✓ |  |
| `slotMask` | `Int` | ✓ |  |
| `maxCharge` | `Float` | ✓ |  |
