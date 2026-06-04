# `UICallback`

**Source:** `skse` (Skyrim Script Extender x64) • **Flags:** Hidden

---

## Global Functions

### `Create(menuName, target) → Int`

**Flags:** Native Global

Creates a new UICallback and returns the handle.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `target` | `String` | ✓ |  |

### `PushBool(handle, value)`

**Flags:** Native Global

Push single parameter. Maximum number of parameters per callback is 128.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `handle` | `Int` | ✓ |  |
| `value` | `Bool` | ✓ |  |

### `PushBoolA(handle, args)`

**Flags:** Native Global

Push parameters from array. Maximum number of parameters per callback is 128.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `handle` | `Int` | ✓ |  |
| `args` | `Bool[]` | ✓ |  |

### `PushFloat(handle, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `handle` | `Int` | ✓ |  |
| `value` | `Float` | ✓ |  |

### `PushFloatA(handle, args)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `handle` | `Int` | ✓ |  |
| `args` | `Float[]` | ✓ |  |

### `PushInt(handle, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `handle` | `Int` | ✓ |  |
| `value` | `Int` | ✓ |  |

### `PushIntA(handle, args)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `handle` | `Int` | ✓ |  |
| `args` | `Int[]` | ✓ |  |

### `PushString(handle, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `handle` | `Int` | ✓ |  |
| `value` | `String` | ✓ |  |

### `PushStringA(handle, args)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `handle` | `Int` | ✓ |  |
| `args` | `String[]` | ✓ |  |

### `Release(handle)`

**Flags:** Native Global

Releases the UICallback without sending it.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `handle` | `Int` | ✓ |  |

### `Send(handle) → Bool`

**Flags:** Native Global

Invokes the UICallback and releases it.
Returns true, if it was executed, false if an error happened.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `handle` | `Int` | ✓ |  |
