# `LeveledSpell`

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

### `GetChanceNone() → Int`

**Flags:** Native

SKSE64 additions built 2024-01-17 20:01:40.731000 UTC

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

### `Revert()`

**Flags:** Native

Removes all script added forms from this leveled list

### `SetChanceNone(chance)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `chance` | `Int` | ✓ |  |

### `SetNthLevel(n, level)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `n` | `Int` | ✓ |  |
| `level` | `Int` | ✓ |  |
