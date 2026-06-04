# `Location`

**Source:** `vanilla` • **Extends:** `Form` • **Flags:** Hidden

---

## Functions

### `GetKeywordData(akKeyword) → Float`

**Flags:** Native

Returns the float value attached to the specified keyword attached to this location

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akKeyword` | `Keyword` | ✓ |  |

### `GetRefTypeAliveCount(akRefType) → Int`

**Flags:** Native

Returns the number of alive references matching the specified reference type

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRefType` | `LocationRefType` | ✓ |  |

### `GetRefTypeDeadCount(akRefType) → Int`

**Flags:** Native

Returns the number of dead references matching the specified reference type

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRefType` | `LocationRefType` | ✓ |  |

### `HasCommonParent(akOther, akFilter) → Bool`

**Flags:** Native

Returns if these two locations have a common parent - filtered with the keyword, if provided

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akOther` | `Location` | ✓ |  |
| `akFilter` | `Keyword` |  |  |

### `HasRefType(akRefType) → Bool`

**Flags:** Native

Returns if this location has the specified reference type

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRefType` | `LocationRefType` | ✓ |  |

### `IsChild(akOther) → Bool`

**Flags:** Native

Returns whether the other location is a child of this one

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akOther` | `Location` | ✓ |  |

### `IsCleared() → Bool`

**Flags:** Native

Returns whether this location is flagged as "cleared" or not

### `IsLoaded() → Bool`

**Flags:** Native

Is this location loaded in game?

### `IsSameLocation(akOtherLocation, akKeyword) → Bool`

Returns true if the calling location is the same as the supplied location - if an optional keyword is supplied, it also returns true if the locations share a parent with that keyword, or if either location is a child of the other and the other has that keyword.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akOtherLocation` | `Location` | ✓ |  |
| `akKeyword` | `Keyword` |  |  |

### `SetCleared(abCleared)`

**Flags:** Native

Sets this location as cleared or not

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abCleared` | `Bool` |  | `true` |

### `SetKeywordData(akKeyword, afData)`

**Flags:** Native

Sets the specified keyword's data on the location

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akKeyword` | `Keyword` | ✓ |  |
| `afData` | `Float` | ✓ |  |
