# `SkyRegEx`

**Source:** `skyregex` (SkyRegEx - Skyrim Regular Expressions SKSE) • **Flags:** Hidden

---

## Global Functions

### `GetVersion() → String`

**Flags:** Native Global

### `IsMatching(sInput, sFilename, iPatternLine, iModLineA, iModLineB) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sInput` | `String` | ✓ |  |
| `sFilename` | `String` | ✓ |  |
| `iPatternLine` | `Int` | ✓ |  |
| `iModLineA` | `Int` | ✓ |  |
| `iModLineB` | `Int` | ✓ |  |

### `MatchCount(sInput, sFilename, iPatternLine, iModLineA, iModLineB) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sInput` | `String` | ✓ |  |
| `sFilename` | `String` | ✓ |  |
| `iPatternLine` | `Int` | ✓ |  |
| `iModLineA` | `Int` | ✓ |  |
| `iModLineB` | `Int` | ✓ |  |

### `MatchData(sInput, sFilename, iPatternLine, iModLineA, iModLineB) → String[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sInput` | `String` | ✓ |  |
| `sFilename` | `String` | ✓ |  |
| `iPatternLine` | `Int` | ✓ |  |
| `iModLineA` | `Int` | ✓ |  |
| `iModLineB` | `Int` | ✓ |  |

### `MatchInfo(sInput, sFilename, iPatternLine, iModLineA, iModLineB) → Int[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sInput` | `String` | ✓ |  |
| `sFilename` | `String` | ✓ |  |
| `iPatternLine` | `Int` | ✓ |  |
| `iModLineA` | `Int` | ✓ |  |
| `iModLineB` | `Int` | ✓ |  |

### `MatchResult(aInfo, aData, iMatch, iGroup) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aInfo` | `Int[]` | ✓ |  |
| `aData` | `String[]` | ✓ |  |
| `iMatch` | `Int` | ✓ |  |
| `iGroup` | `Int` | ✓ |  |

### `ReplaceWith(sInput, sFilename, iPatternLine, iReplaceLine, iModLineA, iModLineB) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sInput` | `String` | ✓ |  |
| `sFilename` | `String` | ✓ |  |
| `iPatternLine` | `Int` | ✓ |  |
| `iReplaceLine` | `Int` | ✓ |  |
| `iModLineA` | `Int` | ✓ |  |
| `iModLineB` | `Int` | ✓ |  |
