# `FISSFactory`

**Source:** `fisses` (FileAccess Interface for Skyrim SE Scripts - FISSES)

Decompiled by Champollion V1.0.0
Source   : FISSFactory.psc
Modified : 2013-10-25 18:33:55
Compiled : 2013-10-25 18:33:58
User     : GrafConti
Computer : GRAFCONTI-PC

---

## Global Functions

### `getFISS() → FISSInterface`

**Flags:** Global

---

## Functions

### `onBeginState()`

Event received when this state is switched to

### `onEndState()`

Event received when this state is switched away from


---

## `FISSInterface`

**Source:** `fisses` (FileAccess Interface for Skyrim SE Scripts - FISSES) • **Extends:** `Quest`

Decompiled by Champollion V1.0.0
Source   : FISSInterface.psc
Modified : 2013-11-28 22:55:22
Compiled : 2013-11-28 22:55:24
User     : GrafConti
Computer : GRAFCONTI-PC

---

## Functions

### `beginLoad(filename)`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `filename` | `String` | ✓ |  |

### `beginSave(filename, modname)`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `filename` | `String` | ✓ |  |
| `modname` | `String` | ✓ |  |

### `blockInput(block)`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `block` | `Bool` | ✓ |  |

### `endLoad() → String`

### `endSave() → String`

### `forceClose()`

### `getAllFilenamesInFolder(path) → String`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `path` | `String` | ✓ |  |

### `getInterfaceVersion() → Float`

### `getModName() → String`

### `getVersion() → Float`

### `hideInfoText(hide)`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `hide` | `Bool` | ✓ |  |

### `loadBool(name) → Bool`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `name` | `String` | ✓ |  |

### `loadFloat(name) → Float`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `name` | `String` | ✓ |  |

### `loadInt(name) → Int`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `name` | `String` | ✓ |  |

### `loadString(name) → String`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `name` | `String` | ✓ |  |

### `requestFilename() → String`

### `requestUserInput(titleMessage) → String`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `titleMessage` | `String` | ✓ |  |

### `saveBool(name, b)`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `name` | `String` | ✓ |  |
| `b` | `Bool` | ✓ |  |

### `saveFloat(name, f)`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `name` | `String` | ✓ |  |
| `f` | `Float` | ✓ |  |

### `saveInt(name, i)`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `name` | `String` | ✓ |  |
| `i` | `Int` | ✓ |  |

### `saveString(name, S)`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `name` | `String` | ✓ |  |
| `S` | `String` | ✓ |  |

### `saveTextToTxtFile(filename, text) → String`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `filename` | `String` | ✓ |  |
| `text` | `String` | ✓ |  |

### `setInfoText(text)`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `text` | `String` | ✓ |  |

### `setText(text)`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `text` | `String` | ✓ |  |

### `setTheme(fissInputTheme, fissInputScale, fissInputAplha)`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fissInputTheme` | `String` | ✓ |  |
| `fissInputScale` | `Float` | ✓ |  |
| `fissInputAplha` | `Float` | ✓ |  |

### `setTitle(title)`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `title` | `String` | ✓ |  |


---

## `FISSScript`

**Source:** `fisses` (FileAccess Interface for Skyrim SE Scripts - FISSES) • **Extends:** `FISSInterface`

Decompiled by Champollion V1.0.0
Source   : FISSScript.psc
Modified : 2014-02-04 03:19:06
Compiled : 2014-02-04 03:21:56
User     : GrafConti
Computer : GRAFCONTI-PC

---

## Properties

### `LoadCObject: String`

**Flags:** Auto

**Accessors:** Get / Set

**Default:** `"ULL"`

### `SaveCObject: String`

**Flags:** Auto

**Accessors:** Get / Set

**Default:** `"ULL"`

---

## Global Functions

### `CFissBeginLoad(filename) → String`

**Flags:** Native Global

-- Native Functions ---------------------------------------

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `filename` | `String` | ✓ |  |

### `CFissBeginSave(filename, modname) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `filename` | `String` | ✓ |  |
| `modname` | `String` | ✓ |  |

### `CFissEndLoad(cobj) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `cobj` | `String` | ✓ |  |

### `CFissEndSave(cobj) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `cobj` | `String` | ✓ |  |

### `CFissLoadBool(cobj, name) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `cobj` | `String` | ✓ |  |
| `name` | `String` | ✓ |  |

### `CFissLoadFloat(cobj, name) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `cobj` | `String` | ✓ |  |
| `name` | `String` | ✓ |  |

### `CFissLoadInt(cobj, name) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `cobj` | `String` | ✓ |  |
| `name` | `String` | ✓ |  |

### `CFissLoadString(cobj, name) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `cobj` | `String` | ✓ |  |
| `name` | `String` | ✓ |  |

### `CFissSaveBool(cobj, name, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `cobj` | `String` | ✓ |  |
| `name` | `String` | ✓ |  |
| `value` | `Bool` | ✓ |  |

### `CFissSaveFloat(cobj, name, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `cobj` | `String` | ✓ |  |
| `name` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |

### `CFissSaveInt(cobj, name, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `cobj` | `String` | ✓ |  |
| `name` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |

### `CFissSaveString(cobj, name, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `cobj` | `String` | ✓ |  |
| `name` | `String` | ✓ |  |
| `value` | `String` | ✓ |  |

### `CFissSaveTextToTxtFile(filename, text) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `filename` | `String` | ✓ |  |
| `text` | `String` | ✓ |  |

---

## Functions

### `beginLoad(filename)`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `filename` | `String` | ✓ |  |

### `beginSave(filename, modname)`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `filename` | `String` | ✓ |  |
| `modname` | `String` | ✓ |  |

### `endLoad() → String`

### `endSave() → String`

### `getVersion() → Float`

### `loadBool(name) → Bool`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `name` | `String` | ✓ |  |

### `loadFloat(name) → Float`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `name` | `String` | ✓ |  |

### `loadInt(name) → Int`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `name` | `String` | ✓ |  |

### `loadString(name) → String`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `name` | `String` | ✓ |  |

### `saveBool(name, b)`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `name` | `String` | ✓ |  |
| `b` | `Bool` | ✓ |  |

### `saveFloat(name, f)`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `name` | `String` | ✓ |  |
| `f` | `Float` | ✓ |  |

### `saveInt(name, i)`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `name` | `String` | ✓ |  |
| `i` | `Int` | ✓ |  |

### `saveString(name, S)`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `name` | `String` | ✓ |  |
| `S` | `String` | ✓ |  |

### `saveTextToTxtFile(filename, text) → String`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `filename` | `String` | ✓ |  |
| `text` | `String` | ✓ |  |
