# `GameData`

**Source:** `skse` (Skyrim Script Extender x64) • **Flags:** Hidden

---

## Properties

### `WeaponTypeBow: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `128`

### `WeaponTypeCrossbow: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `512`

### `WeaponTypeHandToHand: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `1`

### `WeaponTypeOneHandAxe: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `8`

### `WeaponTypeOneHandDagger: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `4`

### `WeaponTypeOneHandMace: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `16`

### `WeaponTypeOneHandSword: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `2`

### `WeaponTypeStaff: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `256`

### `WeaponTypeTwoHandAxe: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `64`

### `WeaponTypeTwoHandSword: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `32`

---

## Global Functions

### `GetAllAmmo(modName, keywords, playable) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `modName` | `String` | ✓ |  |
| `keywords` | `Keyword[]` |  |  |
| `playable` | `Bool` |  | `true` |

### `GetAllArmor(modName, keywords, playable, ignoreTemplates, ignoreEnchantments, onlyEnchanted, ignoreSkin) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `modName` | `String` | ✓ |  |
| `keywords` | `Keyword[]` |  |  |
| `playable` | `Bool` |  | `true` |
| `ignoreTemplates` | `Bool` |  | `true` |
| `ignoreEnchantments` | `Bool` |  | `true` |
| `onlyEnchanted` | `Bool` |  | `false` |
| `ignoreSkin` | `Bool` |  | `true` |

### `GetAllBooks(modName, keywords, regular, spell, skill) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `modName` | `String` | ✓ |  |
| `keywords` | `Keyword[]` |  |  |
| `regular` | `Bool` |  | `true` |
| `spell` | `Bool` |  | `false` |
| `skill` | `Bool` |  | `false` |

### `GetAllIngredients(modName, keywords) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `modName` | `String` | ✓ |  |
| `keywords` | `Keyword[]` |  |  |

### `GetAllKeys(modName, keywords) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `modName` | `String` | ✓ |  |
| `keywords` | `Keyword[]` |  |  |

### `GetAllMiscItems(modName, keywords) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `modName` | `String` | ✓ |  |
| `keywords` | `Keyword[]` |  |  |

### `GetAllPotions(modName, keywords, potions, food, poison) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `modName` | `String` | ✓ |  |
| `keywords` | `Keyword[]` |  |  |
| `potions` | `Bool` |  | `true` |
| `food` | `Bool` |  | `false` |
| `poison` | `Bool` |  | `false` |

### `GetAllScrolls(modName, keywords) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `modName` | `String` | ✓ |  |
| `keywords` | `Keyword[]` |  |  |

### `GetAllWeapons(modName, keywords, playable, ignoreTemplates, ignoreEnchantments, onlyEnchanted, weaponTypes) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `modName` | `String` | ✓ |  |
| `keywords` | `Keyword[]` |  |  |
| `playable` | `Bool` |  | `true` |
| `ignoreTemplates` | `Bool` |  | `true` |
| `ignoreEnchantments` | `Bool` |  | `true` |
| `onlyEnchanted` | `Bool` |  | `false` |
| `weaponTypes` | `Int` |  | `0xFFFFFFFF` |
