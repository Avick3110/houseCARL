# `SKI_ConfigBase`

**Source:** `skyui-sdk` (SkyUI) • **Extends:** `SKI_QuestBase`

---

## Properties

### `CurrentPage: String`

**Flags:** Hidden

### `LEFT_TO_RIGHT: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `1`

### `ModName: String`

**Flags:** Auto

**Accessors:** Get / Set

### `OPTION_FLAG_DISABLED: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x1`

### `OPTION_FLAG_HIDDEN: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x2`

@since 3

### `OPTION_FLAG_NONE: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x0`

### `OPTION_FLAG_WITH_UNMAP: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x4`

@since 3

### `Pages: String[]`

**Flags:** Auto

**Accessors:** Get / Set

### `TOP_TO_BOTTOM: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `2`

---

## Events

### `OnColorAcceptST(a_color)`

**Kind:** Event

Called when a new color has been accepted for this state option

@since 2

**Parameters**

| Name | Type |
|---|---|
| `a_color` | `Int` |

### `OnColorOpenST()`

**Kind:** Event

Called when a color state option has been selected

@since 2

### `OnConfigClose()`

**Kind:** Event

Called when this config menu is closed

### `OnConfigInit()`

**Kind:** Event

Called when this config menu is initialized

### `OnConfigOpen()`

**Kind:** Event

Called when this config menu is opened

### `OnConfigRegister()`

**Kind:** Event

Called when this config menu registered at the control panel

### `OnDefaultST()`

**Kind:** Event

Called when resetting a state option to its default value

@since 2

### `OnHighlightST()`

**Kind:** Event

Called when highlighting a state option

@since 2

### `OnInputAcceptST(a_input)`

**Kind:** Event

Called when a new text input has been accepted for this state option

@since 4

**Parameters**

| Name | Type |
|---|---|
| `a_input` | `String` |

### `OnInputOpenST()`

**Kind:** Event

Called when a text input state option has been selected

@since 4

### `OnKeyMapChangeST(a_keyCode, a_conflictControl, a_conflictName)`

**Kind:** Event

Called when a key has been remapped for this state option

@since 2

**Parameters**

| Name | Type |
|---|---|
| `a_keyCode` | `Int` |
| `a_conflictControl` | `String` |
| `a_conflictName` | `String` |

### `OnMenuAcceptST(a_index)`

**Kind:** Event

Called when a menu entry has been accepted for this state option

@since 2

**Parameters**

| Name | Type |
|---|---|
| `a_index` | `Int` |

### `OnMenuOpenST()`

**Kind:** Event

Called when a menu state option has been selected

@since 2

### `OnOptionColorAccept(a_option, a_color)`

**Kind:** Event

Called when a new color has been accepted

**Parameters**

| Name | Type |
|---|---|
| `a_option` | `Int` |
| `a_color` | `Int` |

### `OnOptionColorOpen(a_option)`

**Kind:** Event

Called when a color option has been selected

**Parameters**

| Name | Type |
|---|---|
| `a_option` | `Int` |

### `OnOptionDefault(a_option)`

**Kind:** Event

Called when resetting an option to its default value

**Parameters**

| Name | Type |
|---|---|
| `a_option` | `Int` |

### `OnOptionHighlight(a_option)`

**Kind:** Event

Called when highlighting an option

**Parameters**

| Name | Type |
|---|---|
| `a_option` | `Int` |

### `OnOptionInputAccept(a_option, a_input)`

**Kind:** Event

Called when a new text input has been accepted

@since 4

**Parameters**

| Name | Type |
|---|---|
| `a_option` | `Int` |
| `a_input` | `String` |

### `OnOptionInputOpen(a_option)`

**Kind:** Event

Called when a text input option has been selected

@since 4

**Parameters**

| Name | Type |
|---|---|
| `a_option` | `Int` |

### `OnOptionKeyMapChange(a_option, a_keyCode, a_conflictControl, a_conflictName)`

**Kind:** Event

Called when a key has been remapped

**Parameters**

| Name | Type |
|---|---|
| `a_option` | `Int` |
| `a_keyCode` | `Int` |
| `a_conflictControl` | `String` |
| `a_conflictName` | `String` |

### `OnOptionMenuAccept(a_option, a_index)`

**Kind:** Event

Called when a menu entry has been accepted

**Parameters**

| Name | Type |
|---|---|
| `a_option` | `Int` |
| `a_index` | `Int` |

### `OnOptionMenuOpen(a_option)`

**Kind:** Event

Called when a menu option has been selected

**Parameters**

| Name | Type |
|---|---|
| `a_option` | `Int` |

### `OnOptionSelect(a_option)`

**Kind:** Event

Called when a non-interactive option has been selected

**Parameters**

| Name | Type |
|---|---|
| `a_option` | `Int` |

### `OnOptionSliderAccept(a_option, a_value)`

**Kind:** Event

Called when a new slider value has been accepted

**Parameters**

| Name | Type |
|---|---|
| `a_option` | `Int` |
| `a_value` | `Float` |

### `OnOptionSliderOpen(a_option)`

**Kind:** Event

Called when a slider option has been selected

**Parameters**

| Name | Type |
|---|---|
| `a_option` | `Int` |

### `OnPageReset(a_page)`

**Kind:** Event

Called when a new page is selected, including the initial empty page

**Parameters**

| Name | Type |
|---|---|
| `a_page` | `String` |

### `OnSelectST()`

**Kind:** Event

Called when a non-interactive state option has been selected

@since 2

### `OnSliderAcceptST(a_value)`

**Kind:** Event

Called when a new slider state value has been accepted

@since 2

**Parameters**

| Name | Type |
|---|---|
| `a_value` | `Float` |

### `OnSliderOpenST()`

**Kind:** Event

Called when a slider state option has been selected

@since 2

### `OnVersionUpdate(aVersion)`

**Kind:** Event

Called when aVersion update of this script has been detected

**Parameters**

| Name | Type |
|---|---|
| `aVersion` | `Int` |

---

## Functions

### `AddColorOption(a_text, a_color, a_flags) → Int`

Adds an option that opens a color swatch dialog when selected

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_text` | `String` | ✓ |  |
| `a_color` | `Int` | ✓ |  |
| `a_flags` | `Int` |  | `0` |

### `AddColorOptionST(a_stateName, a_text, a_color, a_flags)`

Adds a state option that opens a color swatch dialog when selected

@since 2

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_stateName` | `String` | ✓ |  |
| `a_text` | `String` | ✓ |  |
| `a_color` | `Int` | ✓ |  |
| `a_flags` | `Int` |  | `0` |

### `AddEmptyOption() → Int`

Adds an empty option, which can be used for padding instead of manually re-positioning the cursor

### `AddHeaderOption(a_text, a_flags) → Int`

Adds a header option to group several options together

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_text` | `String` | ✓ |  |
| `a_flags` | `Int` |  | `0` |

### `AddInputOption(a_text, a_value, a_flags) → Int`

Adds a text input option

@since 4

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_text` | `String` | ✓ |  |
| `a_value` | `String` | ✓ |  |
| `a_flags` | `Int` |  | `0` |

### `AddInputOptionST(a_stateName, a_text, a_value, a_flags)`

Adds a state option that opens a text input dialog when selected

@since 4

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_stateName` | `String` | ✓ |  |
| `a_text` | `String` | ✓ |  |
| `a_value` | `String` | ✓ |  |
| `a_flags` | `Int` |  | `0` |

### `AddKeyMapOption(a_text, a_keyCode, a_flags) → Int`

Adds a key mapping option

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_text` | `String` | ✓ |  |
| `a_keyCode` | `Int` | ✓ |  |
| `a_flags` | `Int` |  | `0` |

### `AddKeyMapOptionST(a_stateName, a_text, a_keyCode, a_flags)`

Adds a key mapping state option

@since 2

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_stateName` | `String` | ✓ |  |
| `a_text` | `String` | ✓ |  |
| `a_keyCode` | `Int` | ✓ |  |
| `a_flags` | `Int` |  | `0` |

### `AddMenuOption(a_text, a_value, a_flags) → Int`

Adds an option that opens a menu dialog when selected

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_text` | `String` | ✓ |  |
| `a_value` | `String` | ✓ |  |
| `a_flags` | `Int` |  | `0` |

### `AddMenuOptionST(a_stateName, a_text, a_value, a_flags)`

Adds a state option that opens a menu dialog when selected

@since 2

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_stateName` | `String` | ✓ |  |
| `a_text` | `String` | ✓ |  |
| `a_value` | `String` | ✓ |  |
| `a_flags` | `Int` |  | `0` |

### `AddSliderOption(a_text, a_value, a_formatString, a_flags) → Int`

Adds an option that opens a slider dialog when selected

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_text` | `String` | ✓ |  |
| `a_value` | `Float` | ✓ |  |
| `a_formatString` | `String` |  | `"0}"` |
| `a_flags` | `Int` |  | `0` |

### `AddSliderOptionST(a_stateName, a_text, a_value, a_formatString, a_flags)`

Adds a state option that opens a slider dialog when selected

@since 2

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_stateName` | `String` | ✓ |  |
| `a_text` | `String` | ✓ |  |
| `a_value` | `Float` | ✓ |  |
| `a_formatString` | `String` |  | `"0}"` |
| `a_flags` | `Int` |  | `0` |

### `AddTextOption(a_text, a_value, a_flags) → Int`

Adds a generic text/value option

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_text` | `String` | ✓ |  |
| `a_value` | `String` | ✓ |  |
| `a_flags` | `Int` |  | `0` |

### `AddTextOptionST(a_stateName, a_text, a_value, a_flags)`

Adds a generic text/value state option

@since 2

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_stateName` | `String` | ✓ |  |
| `a_text` | `String` | ✓ |  |
| `a_value` | `String` | ✓ |  |
| `a_flags` | `Int` |  | `0` |

### `AddToggleOption(a_text, a_checked, a_flags) → Int`

Adds a check box option that can be toggled on and off

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_text` | `String` | ✓ |  |
| `a_checked` | `Bool` | ✓ |  |
| `a_flags` | `Int` |  | `0` |

### `AddToggleOptionST(a_stateName, a_text, a_checked, a_flags)`

Adds a check box state option that can be toggled on and off

@since 2

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_stateName` | `String` | ✓ |  |
| `a_text` | `String` | ✓ |  |
| `a_checked` | `Bool` | ✓ |  |
| `a_flags` | `Int` |  | `0` |

### `ForcePageReset()`

Forces a full reset of the current page

### `GetCustomControl(a_keyCode) → String`

Returns the name of a custom control mapped to given keyCode, or "" if the key is not in use by this config. Override if necessary

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_keyCode` | `Int` | ✓ |  |

### `GetVersion() → Int`

Returns version of this script. Override if necessary

### `LoadCustomContent(a_source, a_x, a_y)`

Loads an external file into the option panel

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_source` | `String` | ✓ |  |
| `a_x` | `Float` |  | `0` |
| `a_y` | `Float` |  | `0` |

### `SetColorDialogDefaultColor(a_color)`

Sets color dialog parameter(s)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_color` | `Int` | ✓ |  |

### `SetColorDialogStartColor(a_color)`

Sets color dialog parameter(s)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_color` | `Int` | ✓ |  |

### `SetColorOptionValue(a_option, a_color, a_noUpdate)`

Sets the value(s) of an existing option

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_option` | `Int` | ✓ |  |
| `a_color` | `Int` | ✓ |  |
| `a_noUpdate` | `Bool` |  | `false` |

### `SetColorOptionValueST(a_color, a_noUpdate, a_stateName)`

Sets the value(s) of an existing state option

@since 2

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_color` | `Int` | ✓ |  |
| `a_noUpdate` | `Bool` |  | `false` |
| `a_stateName` | `String` |  | `""` |

### `SetCursorFillMode(a_fillMode)`

Sets the fill direction of the cursor used for the option setters

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_fillMode` | `Int` | ✓ |  |

### `SetCursorPosition(a_position)`

Sets the position of the cursor used for the option setters

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_position` | `Int` | ✓ |  |

### `SetInfoText(a_text)`

Sets the text for the info text field below the option panel

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_text` | `String` | ✓ |  |

### `SetInputDialogStartText(a_text)`

Sets text input dialog parameter(s)

@since 4

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_text` | `String` | ✓ |  |

### `SetInputOptionValue(a_option, a_value, a_noUpdate)`

Sets the value(s) of an existing option

@since 4

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_option` | `Int` | ✓ |  |
| `a_value` | `String` | ✓ |  |
| `a_noUpdate` | `Bool` |  | `false` |

### `SetInputOptionValueST(a_value, a_noUpdate, a_stateName)`

Sets the value(s) of an existing state option

@since 4

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_value` | `String` | ✓ |  |
| `a_noUpdate` | `Bool` |  | `false` |
| `a_stateName` | `String` |  | `""` |

### `SetKeyMapOptionValue(a_option, a_keyCode, a_noUpdate)`

Sets the value(s) of an existing option

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_option` | `Int` | ✓ |  |
| `a_keyCode` | `Int` | ✓ |  |
| `a_noUpdate` | `Bool` |  | `false` |

### `SetKeyMapOptionValueST(a_keyCode, a_noUpdate, a_stateName)`

Sets the value(s) of an existing state option

@since 2

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_keyCode` | `Int` | ✓ |  |
| `a_noUpdate` | `Bool` |  | `false` |
| `a_stateName` | `String` |  | `""` |

### `SetMenuDialogDefaultIndex(a_value)`

Sets menu dialog parameter(s)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_value` | `Int` | ✓ |  |

### `SetMenuDialogOptions(a_options)`

Sets menu dialog parameter(s)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_options` | `String[]` | ✓ |  |

### `SetMenuDialogStartIndex(a_value)`

Sets menu dialog parameter(s)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_value` | `Int` | ✓ |  |

### `SetMenuOptionValue(a_option, a_value, a_noUpdate)`

Sets the value(s) of an existing option

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_option` | `Int` | ✓ |  |
| `a_value` | `String` | ✓ |  |
| `a_noUpdate` | `Bool` |  | `false` |

### `SetMenuOptionValueST(a_value, a_noUpdate, a_stateName)`

Sets the value(s) of an existing state option

@since 2

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_value` | `String` | ✓ |  |
| `a_noUpdate` | `Bool` |  | `false` |
| `a_stateName` | `String` |  | `""` |

### `SetOptionFlags(a_option, a_flags, a_noUpdate)`

Sets the option flags

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_option` | `Int` | ✓ |  |
| `a_flags` | `Int` | ✓ |  |
| `a_noUpdate` | `Bool` |  | `false` |

### `SetOptionFlagsST(a_flags, a_noUpdate, a_stateName)`

Sets the state option flags

@since 2

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_flags` | `Int` | ✓ |  |
| `a_noUpdate` | `Bool` |  | `false` |
| `a_stateName` | `String` |  | `""` |

### `SetSliderDialogDefaultValue(a_value)`

Sets slider dialog parameter(s)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_value` | `Float` | ✓ |  |

### `SetSliderDialogInterval(a_value)`

Sets slider dialog parameter(s)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_value` | `Float` | ✓ |  |

### `SetSliderDialogRange(a_minValue, a_maxValue)`

Sets slider dialog parameter(s)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_minValue` | `Float` | ✓ |  |
| `a_maxValue` | `Float` | ✓ |  |

### `SetSliderDialogStartValue(a_value)`

Sets slider dialog parameter(s)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_value` | `Float` | ✓ |  |

### `SetSliderOptionValue(a_option, a_value, a_formatString, a_noUpdate)`

Sets the value(s) of an existing option

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_option` | `Int` | ✓ |  |
| `a_value` | `Float` | ✓ |  |
| `a_formatString` | `String` |  | `"0}"` |
| `a_noUpdate` | `Bool` |  | `false` |

### `SetSliderOptionValueST(a_value, a_formatString, a_noUpdate, a_stateName)`

Sets the value(s) of an existing state option

@since 2

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_value` | `Float` | ✓ |  |
| `a_formatString` | `String` |  | `"0}"` |
| `a_noUpdate` | `Bool` |  | `false` |
| `a_stateName` | `String` |  | `""` |

### `SetTextOptionValue(a_option, a_value, a_noUpdate)`

Sets the value(s) of an existing option

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_option` | `Int` | ✓ |  |
| `a_value` | `String` | ✓ |  |
| `a_noUpdate` | `Bool` |  | `false` |

### `SetTextOptionValueST(a_value, a_noUpdate, a_stateName)`

Sets the value(s) of an existing state option

@since 2

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_value` | `String` | ✓ |  |
| `a_noUpdate` | `Bool` |  | `false` |
| `a_stateName` | `String` |  | `""` |

### `SetTitleText(a_text)`

Sets the title text of the control panel

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_text` | `String` | ✓ |  |

### `SetToggleOptionValue(a_option, a_checked, a_noUpdate)`

Sets the value(s) of an existing option

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_option` | `Int` | ✓ |  |
| `a_checked` | `Bool` | ✓ |  |
| `a_noUpdate` | `Bool` |  | `false` |

### `SetToggleOptionValueST(a_checked, a_noUpdate, a_stateName)`

Sets the value(s) of an existing state option

@since 2

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_checked` | `Bool` | ✓ |  |
| `a_noUpdate` | `Bool` |  | `false` |
| `a_stateName` | `String` |  | `""` |

### `ShowMessage(a_message, a_withCancel, a_acceptLabel, a_cancelLabel) → Bool`

Shows a message dialog and waits until the user has closed it

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_message` | `String` | ✓ |  |
| `a_withCancel` | `Bool` |  | `true` |
| `a_acceptLabel` | `String` |  | `"Accept"` |
| `a_cancelLabel` | `String` |  | `"Cancel"` |

### `UnloadCustomContent()`

Clears any custom content and re-enables the original option list


---

## `SKI_QuestBase`

**Source:** `skyui-sdk` (SkyUI) • **Extends:** `Quest` • **Flags:** Hidden

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
