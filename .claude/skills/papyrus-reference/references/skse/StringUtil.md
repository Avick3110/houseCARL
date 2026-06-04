# `StringUtil`

**Source:** `skse` (Skyrim Script Extender x64) • **Flags:** Hidden

---

## Global Functions

### `AsChar(c) → String`

**Flags:** Native Global

returns a single character string interpreting c as a character

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `c` | `Int` | ✓ |  |

### `AsOrd(c) → Int`

**Flags:** Native Global

returns the numeric value of the first character as an int

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `c` | `String` | ✓ |  |

### `Find(s, toFind, startIndex) → Int`

**Flags:** Native Global

returns the index of the first character of toFind inside string s
returns -1 if toFind is not part of the string or if startIndex is invalid

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `toFind` | `String` | ✓ |  |
| `startIndex` | `Int` |  | `0` |

### `GetLength(s) → Int`

**Flags:** Native Global

return the length of the string

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |

### `GetNthChar(s, index) → String`

**Flags:** Native Global

returns a single character string with the character at index

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `IsDigit(c) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `c` | `String` | ✓ |  |

### `IsLetter(c) → Bool`

**Flags:** Native Global

Functions to work on Chars
returns information about a specific character
assumes a single character string.  If a multicharacter string is passed
the information about the first character is returned

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `c` | `String` | ✓ |  |

### `IsPrintable(c) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `c` | `String` | ✓ |  |

### `IsPunctuation(c) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `c` | `String` | ✓ |  |

### `Split(s, delim) → String[]`

**Flags:** Native Global

returns array of strings separated by the specified delimiter

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `delim` | `String` | ✓ |  |

### `Substring(s, startIndex, len) → String`

**Flags:** Native Global

returns a substring of the specified string starting at startIndex and going for len characters
or until the end of the string.  Default len of 0 means for the entire string

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `startIndex` | `Int` | ✓ |  |
| `len` | `Int` |  | `0` |
