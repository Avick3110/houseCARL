# `HeadPart`

**Source:** `skse` (Skyrim Script Extender x64) • **Extends:** `Form` • **Flags:** Hidden

---

## Contents

Entry blocks are reached from `index.jsonl` at their `line_start`..`line_end`;
this table is for a fallback read, not the lookup path.

| Section | Entries | Line |
|---|---|---|
| Properties | 7 | 20 |
| Global Functions | 1 | 80 |
| Functions | 9 | 94 |

---

## Properties

### `Type_Brows: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `6`

### `Type_Eyes: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `2`

### `Type_Face: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `1`

### `Type_FacialHair: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `4`

### `Type_Hair: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `3`

### `Type_Misc: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0`

### `Type_Scar: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `5`

---

## Global Functions

### `GetHeadPart(name) → HeadPart`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `name` | `String` | ✓ |  |

---

## Functions

### `GetIndexOfExtraPart(p) → Int`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `p` | `HeadPart` | ✓ |  |

### `GetNthExtraPart(n) → HeadPart`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `n` | `Int` | ✓ |  |

### `GetNumExtraParts() → Int`

**Flags:** Native

### `GetPartName() → String`

**Flags:** Native

Returns the EditorID of the HeadPart

### `GetType() → Int`

**Flags:** Native

Returns the head part type

### `GetValidRaces() → FormList`

**Flags:** Native

Returns a formlist of the valid races for this head part

### `HasExtraPart(p) → Bool`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `p` | `HeadPart` | ✓ |  |

### `IsExtraPart() → Bool`

**Flags:** Native

Returns whether the head part is an extra part

### `SetValidRaces(vRaces)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `vRaces` | `FormList` | ✓ |  |
