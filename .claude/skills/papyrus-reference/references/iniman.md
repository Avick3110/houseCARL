# `PapyrusIniManipulator`

**Source:** `iniman` (Papyrus Ini Manipulator) • **Flags:** Hidden

---

## Global Functions

### `ClearIniData(iLevel, sPath, sSection, sKey) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `iLevel` | `Int` | ✓ |  |
| `sPath` | `String` | ✓ |  |
| `sSection` | `String` |  | `""` |
| `sKey` | `String` |  | `""` |

### `DestroyIniData(iLevel, sPath, sSection, sKey) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `iLevel` | `Int` | ✓ |  |
| `sPath` | `String` | ✓ |  |
| `sSection` | `String` |  | `""` |
| `sKey` | `String` |  | `""` |

### `GetIniData(iLevel, sPath, sSection, sKey) → String[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `iLevel` | `Int` | ✓ |  |
| `sPath` | `String` | ✓ |  |
| `sSection` | `String` |  | `""` |
| `sKey` | `String` |  | `""` |

### `GetTranslation(sKey) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sKey` | `String` | ✓ |  |

### `GetVersion() → String`

**Flags:** Native Global

### `IniDataExists(iLevel, sPath, sSection, sKey) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `iLevel` | `Int` | ✓ |  |
| `sPath` | `String` | ✓ |  |
| `sSection` | `String` |  | `""` |
| `sKey` | `String` |  | `""` |

### `PullBoolFromIni(sPath, sSection, sKey, bDefault) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sPath` | `String` | ✓ |  |
| `sSection` | `String` | ✓ |  |
| `sKey` | `String` | ✓ |  |
| `bDefault` | `Bool` |  | `false` |

### `PullFloatFromIni(sPath, sSection, sKey, fDefault) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sPath` | `String` | ✓ |  |
| `sSection` | `String` | ✓ |  |
| `sKey` | `String` | ✓ |  |
| `fDefault` | `Float` |  | `0` |

### `PullIntFromIni(sPath, sSection, sKey, iDefault) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sPath` | `String` | ✓ |  |
| `sSection` | `String` | ✓ |  |
| `sKey` | `String` | ✓ |  |
| `iDefault` | `Int` |  | `0` |

### `PullStringFromIni(sPath, sSection, sKey, sDefault) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sPath` | `String` | ✓ |  |
| `sSection` | `String` | ✓ |  |
| `sKey` | `String` | ✓ |  |
| `sDefault` | `String` |  | `""` |

### `PushBoolToIni(sPath, sSection, sKey, bValue, bForce) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sPath` | `String` | ✓ |  |
| `sSection` | `String` |  | `""` |
| `sKey` | `String` |  | `""` |
| `bValue` | `Bool` |  | `false` |
| `bForce` | `Bool` |  | `false` |

### `PushFloatToIni(sPath, sSection, sKey, fValue, bForce) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sPath` | `String` | ✓ |  |
| `sSection` | `String` |  | `""` |
| `sKey` | `String` |  | `""` |
| `fValue` | `Float` |  | `0` |
| `bForce` | `Bool` |  | `false` |

### `PushIntToIni(sPath, sSection, sKey, iValue, bForce) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sPath` | `String` | ✓ |  |
| `sSection` | `String` |  | `""` |
| `sKey` | `String` |  | `""` |
| `iValue` | `Int` |  | `0` |
| `bForce` | `Bool` |  | `false` |

### `PushStringToIni(sPath, sSection, sKey, sValue, bForce) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sPath` | `String` | ✓ |  |
| `sSection` | `String` |  | `""` |
| `sKey` | `String` |  | `""` |
| `sValue` | `String` |  | `""` |
| `bForce` | `Bool` |  | `false` |
