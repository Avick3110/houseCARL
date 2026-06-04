# `ConstructibleObject`

**Source:** `skse` (Skyrim Script Extender x64) • **Extends:** `MiscObject` • **Flags:** Hidden

---

## Functions

### `GetNthIngredient(n) → Form`

**Flags:** Native

Gets/Sets the Nth ingredient required

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `n` | `Int` | ✓ |  |

### `GetNthIngredientQuantity(n) → Int`

**Flags:** Native

Gets/Sets the quantity of Nth ingredient required

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `n` | `Int` | ✓ |  |

### `GetNumIngredients() → Int`

**Flags:** Native

Gets the number of ingredients

### `GetResult() → form`

**Flags:** Native

Gets/Sets the result of this recipe

### `GetResultQuantity() → Int`

**Flags:** Native

Gets/Sets the amount of results of this recipe

### `GetWorkbenchKeyword() → Keyword`

**Flags:** Native

Gets/Sets the Workbench keyword (Which apparatus creates this)

### `SetNthIngredient(required, n)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `required` | `Form` | ✓ |  |
| `n` | `Int` | ✓ |  |

### `SetNthIngredientQuantity(value, n)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `value` | `Int` | ✓ |  |
| `n` | `Int` | ✓ |  |

### `SetResult(result)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `result` | `Form` | ✓ |  |

### `SetResultQuantity(quantity)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `quantity` | `Int` | ✓ |  |

### `SetWorkbenchKeyword(aKeyword)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aKeyword` | `Keyword` | ✓ |  |
