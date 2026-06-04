# `MCM`

**Source:** `mcmhelper` (MCM Helper) • **Flags:** Hidden

---

## Global Functions

### `GetModSettingBool(a_modName, a_settingName) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_modName` | `String` | ✓ |  |
| `a_settingName` | `String` | ✓ |  |

### `GetModSettingFloat(a_modName, a_settingName) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_modName` | `String` | ✓ |  |
| `a_settingName` | `String` | ✓ |  |

### `GetModSettingInt(a_modName, a_settingName) → Int`

**Flags:** Native Global

Obtains the value of a mod setting.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_modName` | `String` | ✓ |  |
| `a_settingName` | `String` | ✓ |  |

### `GetModSettingString(a_modName, a_settingName) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_modName` | `String` | ✓ |  |
| `a_settingName` | `String` | ✓ |  |

### `GetVersionCode() → Int`

**Flags:** Native Global

Returns the version code of MCM Helper. This value is incremented for every public release of MCM Helper.

### `IsInstalled() → Bool`

**Flags:** Native Global

Checks to see whether MCM Helper is installed.

### `SetModSettingBool(a_modName, a_settingName, a_value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_modName` | `String` | ✓ |  |
| `a_settingName` | `String` | ✓ |  |
| `a_value` | `Bool` | ✓ |  |

### `SetModSettingFloat(a_modName, a_settingName, a_value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_modName` | `String` | ✓ |  |
| `a_settingName` | `String` | ✓ |  |
| `a_value` | `Float` | ✓ |  |

### `SetModSettingInt(a_modName, a_settingName, a_value)`

**Flags:** Native Global

Sets the value of a mod setting.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_modName` | `String` | ✓ |  |
| `a_settingName` | `String` | ✓ |  |
| `a_value` | `Int` | ✓ |  |

### `SetModSettingString(a_modName, a_settingName, a_value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_modName` | `String` | ✓ |  |
| `a_settingName` | `String` | ✓ |  |
| `a_value` | `String` | ✓ |  |


---

## `MCM_ConfigBase`

**Source:** `mcmhelper` (MCM Helper) • **Extends:** `SKI_ConfigBase`

---

## Events

### `OnConfigClose()`

**Kind:** Event

Event raised when a config menu is closed.

### `OnConfigInit()`

**Kind:** Event

Event raised when a config menu is first initialized.

### `OnConfigOpen()`

**Kind:** Event

Event raised when a config menu is opened.

### `OnPageSelect(a_page)`

**Kind:** Event

Event raised when a new page is selected, including the initial empty page.

**Parameters**

| Name | Type |
|---|---|
| `a_page` | `String` |

### `OnSettingChange(a_ID)`

**Kind:** Event

Event raised when an MCM setting is changed.

**Parameters**

| Name | Type |
|---|---|
| `a_ID` | `String` |

---

## Functions

### `GetModSettingBool(a_settingName) → Bool`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_settingName` | `String` | ✓ |  |

### `GetModSettingFloat(a_settingName) → Float`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_settingName` | `String` | ✓ |  |

### `GetModSettingInt(a_settingName) → Int`

**Flags:** Native

Obtains the value of a mod setting.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_settingName` | `String` | ✓ |  |

### `GetModSettingString(a_settingName) → String`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_settingName` | `String` | ✓ |  |

### `RefreshMenu()`

**Flags:** Native

Refreshes currently displayed values in the MCM if it is currently open.
Call this if you have changed values in response to an OnSettingChange event.

### `SetMenuOptions(a_ID, a_options, a_shortNames)`

**Flags:** Native

Dynamically override menu options via script.
The supplied ID must refer to a menu control.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_ID` | `String` | ✓ |  |
| `a_options` | `String[]` | ✓ |  |
| `a_shortNames` | `String[]` |  |  |

### `SetModSettingBool(a_settingName, a_value)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_settingName` | `String` | ✓ |  |
| `a_value` | `Bool` | ✓ |  |

### `SetModSettingFloat(a_settingName, a_value)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_settingName` | `String` | ✓ |  |
| `a_value` | `Float` | ✓ |  |

### `SetModSettingInt(a_settingName, a_value)`

**Flags:** Native

Sets the value of a mod setting.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_settingName` | `String` | ✓ |  |
| `a_value` | `Int` | ✓ |  |

### `SetModSettingString(a_settingName, a_value)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_settingName` | `String` | ✓ |  |
| `a_value` | `String` | ✓ |  |


---

## `SKI_ConfigBase`

**Source:** `mcmhelper` (MCM Helper) • **Extends:** `SKI_QuestBase` • **Flags:** Hidden

---

## Properties

### `CurrentPage: String`

**Flags:** Hidden

### `ModName: String`

**Flags:** Auto

**Accessors:** Get / Set

### `Pages: String[]`

**Flags:** Auto

**Accessors:** Get / Set

---

## Events

### `OnConfigClose()`

**Kind:** Event

Called when this config menu is closed

### `OnConfigInit()`

**Kind:** Event

Called when this config menu is initialized

### `OnConfigOpen()`

**Kind:** Event

Called when this config menu is opened

### `OnVersionUpdate(aVersion)`

**Kind:** Event

Called when aVersion update of this script has been detected

**Parameters**

| Name | Type |
|---|---|
| `aVersion` | `Int` |

---

## Functions

### `ForcePageReset()`

Forces a full reset of the current page

### `GetVersion() → Int`

Returns version of this script. Override if necessary

### `SetTitleText(a_text)`

Sets the title text of the control panel

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_text` | `String` | ✓ |  |

### `ShowMessage(a_message, a_withCancel, a_acceptLabel, a_cancelLabel) → Bool`

Shows a message dialog and waits until the user has closed it

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_message` | `String` | ✓ |  |
| `a_withCancel` | `Bool` |  | `true` |
| `a_acceptLabel` | `String` |  | `"Accept"` |
| `a_cancelLabel` | `String` |  | `"Cancel"` |


---

## `SKI_QuestBase`

**Source:** `mcmhelper` (MCM Helper) • **Extends:** `Quest` • **Flags:** Hidden

---

## Properties

### `CurrentVersion: Int`

**Flags:** Auto Hidden

**Accessors:** Get / Set

---

## Events

### `OnGameReload()`

**Kind:** Event

### `OnInit()`

**Kind:** Event

### `OnVersionUpdate(a_version)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `a_version` | `Int` |

---

## Functions

### `CheckVersion()`

### `GetVersion() → Int`
