# `LeveledActor`

**Source:** `skse` (Skyrim Script Extender x64) • **Extends:** `Form` • **Flags:** Hidden

---

## Functions

### `AddForm(apForm, aiLevel)`

**Flags:** Native

Adds the given count of the given form to the under the given level in this leveled list

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `apForm` | `Form` | ✓ |  |
| `aiLevel` | `Int` | ✓ |  |

### `GetNthCount(n) → Int`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `n` | `Int` | ✓ |  |

### `GetNthForm(n) → Form`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `n` | `Int` | ✓ |  |

### `GetNthLevel(n) → Int`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `n` | `Int` | ✓ |  |

### `GetNumForms() → Int`

**Flags:** Native

SKSE64 additions built 2024-01-17 20:01:40.731000 UTC

### `Revert()`

**Flags:** Native

Removes all script added forms from this leveled list

### `SetNthCount(n, count)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `n` | `Int` | ✓ |  |
| `count` | `Int` | ✓ |  |

### `SetNthLevel(n, level)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `n` | `Int` | ✓ |  |
| `level` | `Int` | ✓ |  |
