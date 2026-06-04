# `ArmorAddon`

**Source:** `skse` (Skyrim Script Extender x64) • **Extends:** `Form` • **Flags:** Hidden

---

## Global Functions

### `GetMaskForSlot(slot) → Int`

**Flags:** Global

calculates the equivalent mask value for the slot
This is a global function, use it directly from Armor as it is faster

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `slot` | `Int` | ✓ |  |

---

## Functions

### `AddSlotToMask(slotMask) → Int`

**Flags:** Native

adds the specified slotMask to the armor addon

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `slotMask` | `Int` | ✓ |  |

### `GetModelNthTextureSet(n, first, female) → TextureSet`

**Flags:** Native

returns the nth textureset for the particular model

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `n` | `Int` | ✓ |  |
| `first` | `Bool` | ✓ |  |
| `female` | `Bool` | ✓ |  |

### `GetModelNumTextureSets(first, female) → Int`

**Flags:** Native

returns the number of texturesets for the particular model

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `first` | `Bool` | ✓ |  |
| `female` | `Bool` | ✓ |  |

### `GetModelPath(firstPerson, female) → String`

**Flags:** Native

returns the model path of the particular model

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `firstPerson` | `Bool` | ✓ |  |
| `female` | `Bool` | ✓ |  |

### `GetNthAdditionalRace(n) → Race`

**Flags:** Native

returns the nth race this armor addon applies to

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `n` | `Int` | ✓ |  |

### `GetNumAdditionalRaces() → Int`

**Flags:** Native

returns the number of races this armor addon applies to

### `GetSlotMask() → Int`

**Flags:** Native

returns the slot mask for the armor addon.

### `RemoveSlotFromMask(slotMask) → Int`

**Flags:** Native

removes the specified slot masks from the armor addon

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `slotMask` | `Int` | ✓ |  |

### `SetModelNthTextureSet(texture, n, first, female)`

**Flags:** Native

sets the nth textureset for the particular model

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `texture` | `TextureSet` | ✓ |  |
| `n` | `Int` | ✓ |  |
| `first` | `Bool` | ✓ |  |
| `female` | `Bool` | ✓ |  |

### `SetModelPath(path, firstPerson, female)`

**Flags:** Native

sets the model path of the particular model

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `path` | `String` | ✓ |  |
| `firstPerson` | `Bool` | ✓ |  |
| `female` | `Bool` | ✓ |  |

### `SetSlotMask(slotMask)`

**Flags:** Native

sets the slot mask for the armor addon

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `slotMask` | `Int` | ✓ |  |
