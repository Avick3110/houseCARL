# `UI`

**Source:** `skse` (Skyrim Script Extender x64) • **Flags:** Hidden

---

## Global Functions

### `CloseCustomMenu()`

**Flags:** Native Global

close the custom menu if it's currently open.

### `GetBool(menuName, target) → Bool`

**Flags:** Native Global

Gets bool/number/string from target location, or false/0/none if the value doesn't exist.

Examples:
	bool	visible	= UI.GetBool("Inventory Menu", "_root.Menu_mc._visible")
	float	height	= UI.GetNumber("Magic Menu", "_root.Menu_mc._height")

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `target` | `String` | ✓ |  |

### `GetFloat(menuName, target) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `target` | `String` | ✓ |  |

### `GetInt(menuName, target) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `target` | `String` | ✓ |  |

### `GetNumber(menuName, target) → Float`

**Flags:** Global

DEPRECIATED

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `target` | `String` | ✓ |  |

### `GetString(menuName, target) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `target` | `String` | ✓ |  |

### `Invoke(menuName, target)`

**Flags:** Global

Invokes the ActionScript function at given target location.

Examples:
	UI.InvokeString("InventoryMenu", "_global.skse.Log", "Printed to logfile")
	UI.InvokeStringA("InventoryMenu", "_global.myFunction", myArray)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `target` | `String` | ✓ |  |

### `InvokeBool(menuName, target, arg)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `target` | `String` | ✓ |  |
| `arg` | `Bool` | ✓ |  |

### `InvokeBoolA(menuName, target, args)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `target` | `String` | ✓ |  |
| `args` | `Bool[]` | ✓ |  |

### `InvokeFloat(menuName, target, arg)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `target` | `String` | ✓ |  |
| `arg` | `Float` | ✓ |  |

### `InvokeFloatA(menuName, target, args)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `target` | `String` | ✓ |  |
| `args` | `Float[]` | ✓ |  |

### `InvokeForm(menuName, target, arg)`

**Flags:** Native Global

Sends Form data to Scaleform as a Flash object, FormLists included.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `target` | `String` | ✓ |  |
| `arg` | `Form` | ✓ |  |

### `InvokeInt(menuName, target, arg)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `target` | `String` | ✓ |  |
| `arg` | `Int` | ✓ |  |

### `InvokeIntA(menuName, target, args)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `target` | `String` | ✓ |  |
| `args` | `Int[]` | ✓ |  |

### `InvokeNumber(menuName, target, arg)`

**Flags:** Global

DEPRECIATED

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `target` | `String` | ✓ |  |
| `arg` | `Float` | ✓ |  |

### `InvokeNumberA(menuName, target, args)`

**Flags:** Global

DEPRECIATED

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `target` | `String` | ✓ |  |
| `args` | `Float[]` | ✓ |  |

### `InvokeString(menuName, target, arg)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `target` | `String` | ✓ |  |
| `arg` | `String` | ✓ |  |

### `InvokeStringA(menuName, target, args)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `target` | `String` | ✓ |  |
| `args` | `String[]` | ✓ |  |

### `IsMenuOpen(menuName) → Bool`

**Flags:** Native Global

Returns if the menu is currently open.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |

### `IsTextInputEnabled() → Bool`

**Flags:** Native Global

returns if scaleform is in 'text input' mode
this is useful for ignoring keys that should get swallowed by an editable text box

### `OpenCustomMenu(swfPath, flags)`

**Flags:** Native Global

open a custom menu named "CustomMenu" by loading the given swf from the interface folder
(filename without extension)
there can only be a single custom menu open at the same time

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `swfPath` | `String` | ✓ |  |
| `flags` | `Int` |  | `0` |

### `SetBool(menuName, target, value)`

**Flags:** Native Global

Sets bool/number/string value at target location.
Target value must already exist.

Examples:
	UI.SetBool("InventoryMenu", "_root.Menu_mc._visible", false)
	UI.SetString("FavoritesMenu", "_root.Menu_mc.panel.message.text", "My Text")

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `target` | `String` | ✓ |  |
| `value` | `Bool` | ✓ |  |

### `SetFloat(menuName, target, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `target` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |

### `SetInt(menuName, target, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `target` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |

### `SetNumber(menuName, target, value)`

**Flags:** Global

DEPRECIATED

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `target` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |

### `SetString(menuName, target, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `target` | `String` | ✓ |  |
| `value` | `String` | ✓ |  |
