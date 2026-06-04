# `Armor`

**Source:** `skse` (Skyrim Script Extender x64) • **Extends:** `Form` • **Flags:** Hidden

---

## Properties

### `kSlotMask30: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x1`

### `kSlotMask31: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x2`

### `kSlotMask32: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x4`

### `kSlotMask33: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x8`

### `kSlotMask34: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x10`

### `kSlotMask35: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x20`

### `kSlotMask36: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x40`

### `kSlotMask37: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x80`

### `kSlotMask38: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x100`

### `kSlotMask39: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x200`

### `kSlotMask40: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x400`

### `kSlotMask41: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x800`

### `kSlotMask42: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x1000`

### `kSlotMask43: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x2000`

### `kSlotMask44: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x4000`

### `kSlotMask45: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x8000`

### `kSlotMask46: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x10000`

### `kSlotMask47: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x20000`

### `kSlotMask48: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x40000`

### `kSlotMask49: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x80000`

### `kSlotMask50: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x100000`

### `kSlotMask51: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x200000`

### `kSlotMask52: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x400000`

### `kSlotMask53: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x800000`

### `kSlotMask54: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x1000000`

### `kSlotMask55: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x2000000`

### `kSlotMask56: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x4000000`

### `kSlotMask57: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x8000000`

### `kSlotMask58: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x10000000`

### `kSlotMask59: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x20000000`

### `kSlotMask60: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x40000000`

### `kSlotMask61: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x80000000`

---

## Global Functions

### `GetMaskForSlot(slot) → Int`

**Flags:** Native Global

calculates the equivalent value for the properties below

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `slot` | `Int` | ✓ |  |

---

## Functions

### `AddSlotToMask(slotMask) → Int`

**Flags:** Native

adds the specified slotMask to the armor

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `slotMask` | `Int` | ✓ |  |

### `GetAR() → Int`

### `GetArmorRating() → Int`

**Flags:** Native

SKSE64 additions built 2024-01-17 20:01:40.731000 UTC

### `GetEnchantment() → Enchantment`

**Flags:** Native

works on the enchantment associated with the armor

### `GetIconPath(bFemalePath) → String`

**Flags:** Native

works on the path to the nif file representing the icon for the weapon in the inventory

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `bFemalePath` | `Bool` | ✓ |  |

### `GetMessageIconPath(bFemalePath) → String`

**Flags:** Native

works on the path to the file representing the message icon for the weapon

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `bFemalePath` | `Bool` | ✓ |  |

### `GetModelPath(bFemalePath) → String`

**Flags:** Native

works on the path to the nif file representing the in-game model of the weapon

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `bFemalePath` | `Bool` | ✓ |  |

### `GetNthArmorAddon(n) → ArmorAddon`

**Flags:** Native

returns the nth armor addon for this armor

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `n` | `Int` | ✓ |  |

### `GetNumArmorAddons() → Int`

**Flags:** Native

returns the number of armor addons for this armor

### `GetSlotMask() → Int`

**Flags:** Native

returns the slot mask for the armor.

### `GetWarmthRating() → Float`

**Flags:** Native

Returns the "warmth rating" for this armor

### `GetWeightClass() → Int`

**Flags:** Native

Weight Class
0 = Light Armor
1 = Heavy Armor
2 = None

### `IsBoots() → Bool`

### `IsClothing() → Bool`

### `IsClothingBody() → Bool`

### `IsClothingFeet() → Bool`

### `IsClothingHands() → Bool`

### `IsClothingHead() → Bool`

### `IsClothingPoor() → Bool`

### `IsClothingRich() → Bool`

### `IsClothingRing() → Bool`

### `IsCuirass() → Bool`

### `IsGauntlets() → Bool`

### `IsHeavyArmor() → Bool`

### `IsHelmet() → Bool`

### `IsJewelry() → Bool`

### `IsLightArmor() → Bool`

Armor info by keyword

### `IsShield() → Bool`

### `ModAR(modBy)`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `modBy` | `Int` | ✓ |  |

### `ModArmorRating(modBy)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `modBy` | `Int` | ✓ |  |

### `RemoveSlotFromMask(slotMask) → Int`

**Flags:** Native

removes the specified slot masks from the armor

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `slotMask` | `Int` | ✓ |  |

### `SetAR(armorRating)`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `armorRating` | `Int` | ✓ |  |

### `SetArmorRating(armorRating)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `armorRating` | `Int` | ✓ |  |

### `SetEnchantment(e)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `e` | `Enchantment` | ✓ |  |

### `SetIconPath(path, bFemalePath)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `path` | `String` | ✓ |  |
| `bFemalePath` | `Bool` | ✓ |  |

### `SetMessageIconPath(path, bFemalePath)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `path` | `String` | ✓ |  |
| `bFemalePath` | `Bool` | ✓ |  |

### `SetModelPath(path, bFemalePath)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `path` | `String` | ✓ |  |
| `bFemalePath` | `Bool` | ✓ |  |

### `SetSlotMask(slotMask)`

**Flags:** Native

sets the slot mask for the armor

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `slotMask` | `Int` | ✓ |  |

### `SetWeightClass(weightClass)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `weightClass` | `Int` | ✓ |  |
