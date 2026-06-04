# `PerkEntryPointExtender`

**Source:** `perkentrypoint` (Perk Entry Point Extender) • **Flags:** Hidden

---

## Global Functions

### `ApplyPerkEntryPoint(target, entry_point, args, category, channel, handle)`

**Flags:** Native Global

The void perk entry point. Currently, applying a leveled list is the only known entry point

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `target` | `Actor` | ✓ |  |
| `entry_point` | `String` | ✓ |  |
| `args` | `Form[]` | ✓ |  |
| `category` | `String` |  | `""` |
| `channel` | `Int` |  | `1` |
| `handle` | `Int` |  | `0` |

### `ApplyPerkEntryPointFloat(target, entry_point, args, base_value, category, channel, handle) → Float`

**Flags:** Native Global

Gets the float value of a perk entry point.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `target` | `Actor` | ✓ |  |
| `entry_point` | `String` | ✓ |  |
| `args` | `Form[]` | ✓ |  |
| `base_value` | `Float` | ✓ |  |
| `category` | `String` |  | `""` |
| `channel` | `Int` |  | `1` |
| `handle` | `Int` |  | `0` |

### `ApplyPerkEntryPointForm(target, entry_point, args, category, channel, handle) → Form[]`

**Flags:** Native Global

Gets the form/forms value of a perk entry point. Should Kernals patch to allow multiple entries exist, this will return multiple entries.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `target` | `Actor` | ✓ |  |
| `entry_point` | `String` | ✓ |  |
| `args` | `Form[]` | ✓ |  |
| `category` | `String` |  | `""` |
| `channel` | `Int` |  | `1` |
| `handle` | `Int` |  | `0` |

### `ApplyPerkEntryPointSpell(target, entry_point, args, category, channel, handle) → Spell[]`

**Flags:** Native Global

Identical to the form version, just uses it as spells (there will never likely be a difference, but this will cast them for you)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `target` | `Actor` | ✓ |  |
| `entry_point` | `String` | ✓ |  |
| `args` | `Form[]` | ✓ |  |
| `category` | `String` |  | `""` |
| `channel` | `Int` |  | `1` |
| `handle` | `Int` |  | `0` |

### `ApplyPerkEntryPointString(target, entry_point, args, out_value, category, channel, handle) → String`

**Flags:** Native Global

Get's the string result of a perk entry point. Set activation label is currently the only known entry point

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `target` | `Actor` | ✓ |  |
| `entry_point` | `String` | ✓ |  |
| `args` | `Form[]` | ✓ |  |
| `out_value` | `String` |  | `""` |
| `category` | `String` |  | `""` |
| `channel` | `Int` |  | `1` |
| `handle` | `Int` |  | `0` |

### `CloseHandle(handle) → Bool`

**Flags:** Native Global

Closes a condition function handle.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `handle` | `Int` | ✓ |  |

### `CreateHandle() → Int`

**Flags:** Native Global

Creates a handle to add condition function items to

### `GetVersion() → Int[]`

**Flags:** Native Global

Returns a 4 length array of integers representing the current version.

### `GetVersionInt() → Int`

**Flags:** Global

### `SetHandleItemForm(handle, name, value) → Int`

**Flags:** Native Global

Sets a handle field to be a specific form value.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `handle` | `Int` | ✓ |  |
| `name` | `String` | ✓ |  |
| `value` | `Form` | ✓ |  |

### `SetHandleItemString(handle, name, value) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `handle` | `Int` | ✓ |  |
| `name` | `String` | ✓ |  |
| `value` | `String` | ✓ |  |

### `ValidateHandle(handle) → Int`

**Flags:** Native Global

Checks if a handle is valid and returns either it or a new handle id

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `handle` | `Int` | ✓ |  |
