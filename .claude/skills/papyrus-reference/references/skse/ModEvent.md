# `ModEvent`

**Source:** `skse` (Skyrim Script Extender x64) • **Flags:** Hidden

---

## Global Functions

### `Create(eventName) → Int`

**Flags:** Native Global

Creates a new ModEvent and returns the handle.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventName` | `String` | ✓ |  |

### `PushBool(handle, value)`

**Flags:** Native Global

Push single parameter.

For arguments 1 .. N, the signature of the receiving event callback has to look like this:

event MyCallback(TYPE_1 PARAM_1, ... , TYPE_N PARAM_N)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `handle` | `Int` | ✓ |  |
| `value` | `Bool` | ✓ |  |

### `PushFloat(handle, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `handle` | `Int` | ✓ |  |
| `value` | `Float` | ✓ |  |

### `PushForm(handle, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `handle` | `Int` | ✓ |  |
| `value` | `Form` | ✓ |  |

### `PushInt(handle, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `handle` | `Int` | ✓ |  |
| `value` | `Int` | ✓ |  |

### `PushString(handle, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `handle` | `Int` | ✓ |  |
| `value` | `String` | ✓ |  |

### `Release(handle)`

**Flags:** Native Global

Releases the ModEvent without sending it.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `handle` | `Int` | ✓ |  |

### `Send(handle) → Bool`

**Flags:** Native Global

Sends the ModEvent and releases it.
Returns true, if it was sent successfully, false if an error happened.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `handle` | `Int` | ✓ |  |
