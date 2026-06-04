# `AhzMoreHud`

**Source:** `morehud` (moreHUD) • **Flags:** Hidden

---

## Global Functions

### `AddIconItem(aItemId, aIconName)`

**Flags:** Native Global

iEquip Only - Add an Item ID with the icon that you want to display

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aItemId` | `Int` | ✓ |  |
| `aIconName` | `String` | ✓ |  |

### `AddIconItems(aItemIds, aIconNames)`

**Flags:** Native Global

iEquip Only - Adds an array of Item ID's with the icon that you want to display

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aItemIds` | `Int[]` | ✓ |  |
| `aIconNames` | `String[]` | ✓ |  |

### `GetVersion() → Int`

**Flags:** Native Global

Gets the version e.g 10008 for 1.0.8

### `GetVersionString() → String`

**Flags:** Global

Gets the version as a string for viewing

### `IsBetaPlugin(aVersion) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aVersion` | `Int` | ✓ |  |

### `IsIconFormListRegistered(aKey) → Bool`

**Flags:** Native Global

Returns true if the form list is registered with the key. The key is the name of the icon.
The icon must exist in the 'Data/Interface/exported/moreHUD/baseIcons.swf'

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aKey` | `String` | ✓ |  |

### `IsIconItemRegistered(aItemId) → Bool`

**Flags:** Native Global

iEquip Only - Returns true if the Item ID is registered

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aItemId` | `Int` | ✓ |  |

### `RegisterIconFormList(aKey, alist)`

**Flags:** Native Global

Registers a form list with this key. The key is the name of the icon.
The icon must exist in the 'Data/Interface/exported/moreHUD/baseIcons.swf'

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aKey` | `String` | ✓ |  |
| `alist` | `FormList` | ✓ |  |

### `RemoveIconItem(aItemId)`

**Flags:** Native Global

iEquip Only - Removes and Item ID from the icon list

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aItemId` | `Int` | ✓ |  |

### `RemoveIconItems(aItemIds)`

**Flags:** Native Global

iEquip Only - Removes an array of Item ID's from the icon list

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aItemIds` | `Int[]` | ✓ |  |

### `UnRegisterIconFormList(aKey)`

**Flags:** Native Global

Unregisters a form list with this key. The key is the name of the icon.
The icon must exist in the 'Data/Interface/exported/moreHUD/baseIcons.swf'

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aKey` | `String` | ✓ |  |
