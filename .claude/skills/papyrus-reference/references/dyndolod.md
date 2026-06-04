# `SHESON_DynDOLOD_Plugin`

**Source:** `dyndolod` (DynDOLOD DLL) • **Flags:** Hidden

---

## Global Functions

### `GetBoNData() → String`

**Flags:** Native Global

Bunch of numbers current LOD world data file

### `GetBoNWorlds() → String`

**Flags:** Native Global

Bunch of numbers worlds file

### `GetConfig(file, key, default) → String`

**Flags:** Native Global

Gets the value for key from [config] section, none for global or LODName file

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `file` | `String` |  | `"one"` |
| `key` | `String` | ✓ |  |
| `default` | `String` |  | `""` |

### `GetDragonDistance() → Float`

**Flags:** Native Global

Get DragonDistance

### `GetESMVersion() → Int`

**Flags:** Native Global

Get version of ESM plugin

### `GetESPVersion() → Int`

**Flags:** Native Global

Get version of ESP plugin

### `GetGlowMultiple() → Float`

**Flags:** Native Global

Get GlowMultiple

### `GetINIUnsigned(name) → Int`

**Flags:** Native Global

Get unsigned INI setting

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `name` | `String` | ✓ |  |

### `GetLargeRefChildWorlds() → Bool`

**Flags:** Native Global

get enable/disable large references in child worlds using parent world for LOD

### `GetLastMessage() → String`

**Flags:** Native Global

Get last message

### `GetMyActive() → Bool`

**Flags:** Native Global

get dynamic LOD enabled/disabled

### `GetMyLODWorld() → String`

**Flags:** Native Global

Get the current LOD worldspace EditorID

### `GetMyLODWorldX() → Int`

**Flags:** Native Global

get LOD origin cell X

### `GetMyLODWorldY() → Int`

**Flags:** Native Global

get LOD origin cell Y

### `GetMyMaster() → ObjectReference`

**Flags:** Native Global

Get current master reference

### `GetPluginLimit() → Int`

**Flags:** Native Global

Get plugin limit

### `GetResetAt(value) → ObjectReference`

**Flags:** Native Global

Get reference to reset at index

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `value` | `Int` | ✓ |  |

### `GetResetsCount() → Int`

**Flags:** Native Global

Get count of references to reset

### `GetScriptVersion() → Int`

**Flags:** Global

Get version of papyrus scripts

### `GetSKSEVersion() → Int`

**Flags:** Native Global

Get required version of SKSE

### `GetSuccessful() → Bool`

**Flags:** Native Global

get if checks were successful

### `GetVersion() → Int`

**Flags:** Native Global

Get version of DynDOLOD.DLL

### `GetWorldspaceLODNameStr(file) → String`

**Flags:** Native Global

Get the LOD filename for EditorID of worldspace

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `file` | `String` | ✓ |  |

### `IsPluginInstalled(file) → Bool`

**Flags:** Native Global

Copy of SKSE64 function

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `file` | `String` | ✓ |  |

### `SetDragonDistance(value)`

**Flags:** Native Global

Set DragonDistance

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `value` | `Float` | ✓ |  |

### `SetGlowMultiple(value)`

**Flags:** Native Global

Set GlowMultiple

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `value` | `Float` | ✓ |  |

### `SetINIUnsigned(name, value)`

**Flags:** Native Global

Set unsigned INI setting

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `name` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |

### `SetLargeRefChildWorlds(value)`

**Flags:** Native Global

Set enable/disable large references in child worlds using parent world for LOD

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `value` | `Bool` | ✓ |  |

### `SetMyActive(value)`

**Flags:** Native Global

set dynamic LOD enable/disable

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `value` | `Bool` | ✓ |  |
