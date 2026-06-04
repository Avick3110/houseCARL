# `SkyMessage`

**Source:** `messagebox` (Papyrus MessageBox) • **Flags:** Hidden

---

## Global Functions

### `Delete(messageBoxId)`

**Flags:** Native Global

VS Code has a bug where the documentation of the first function isn't shown.
This is that function:

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `messageBoxId` | `Int` | ✓ |  |

### `GetResultIndex(messageBoxId, deleteResultOnAccess) → Int`

**Flags:** Native Global

Returns the result index from a message box created using `Show_NonBlocking()` or `ShowArray_NonBlocking()`

If `IsMessageResultAvailable` is `false` or the messageBoxId is invalid, this returns `-1`
else it returns the `int` index of the button which was selected.

Note: calling this function cleans up the stored messageBox and it will no longer be available
unless you pass `deleteResultOnAccess = false`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `messageBoxId` | `Int` | ✓ |  |
| `deleteResultOnAccess` | `Bool` |  | `true` |

### `GetResultText(messageBoxId, deleteResultOnAccess) → String`

**Flags:** Native Global

Returns the result text from a message box created using `Show_NonBlocking()` or `ShowArray_NonBlocking()`

If `IsMessageResultAvailable` is `false` or the messageBoxId is invalid, this returns `""`
else it returns the name of the button which was selected.

Note: calling this function cleans up the stored messageBox and it will no longer be available
unless you pass `deleteResultOnAccess = false`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `messageBoxId` | `Int` | ✓ |  |
| `deleteResultOnAccess` | `Bool` |  | `true` |

### `IsMessageResultAvailable(messageBoxId) → Bool`

**Flags:** Native Global

Returns true if a messagebox created via `Show_NonBlocking()` or `ShowArray_NonBlocking()` has had a button selected.
Returns false in all other scenarios.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `messageBoxId` | `Int` | ✓ |  |

### `Show(bodyText, button1, button2, button3, button4, button5, button6, button7, button8, button9, button10, getIndex, waitInterval, timeoutSeconds) → String`

**Flags:** Global

Open a messagebox and return the name of the selected button.

### Get name of selected button
```psc
string selectedButtonName = SkyMessage.Show("body text of message", "Ok", "Cancel", "More buttons...")
```

### Get index of selected button
If you would prefer to get the `int` index of the button:
```psc
string selectedButtonIndex = SkyMessage.Show("body text of message", "Ok", "Cancel", "More buttons...", getIndex = true)
```

### Button names
`Show()` accepts up to 10 button names. If you would prefer to specify the button names via an array, see: `SkyMessage.ShowArray()`

### Advanced
You can also specify a timeout. This is the number of seconds after which the function will return with the text "TIMED_OUT":
```psc
string selectedButtonName = SkyMessage.Show("body text of message", "Ok", "Cancel", "More buttons...", timeoutSeconds = 69.0)
```

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `bodyText` | `String` | ✓ |  |
| `button1` | `String` | ✓ |  |
| `button2` | `String` |  | `""` |
| `button3` | `String` |  | `""` |
| `button4` | `String` |  | `""` |
| `button5` | `String` |  | `""` |
| `button6` | `String` |  | `""` |
| `button7` | `String` |  | `""` |
| `button8` | `String` |  | `""` |
| `button9` | `String` |  | `""` |
| `button10` | `String` |  | `""` |
| `getIndex` | `Bool` |  | `false` |
| `waitInterval` | `Float` |  | `0.1` |
| `timeoutSeconds` | `Float` |  | `0` |

### `Show_NonBlocking(bodyText, button1, button2, button3, button4, button5, button6, button7, button8, button9, button10) → Int`

**Flags:** Native Global

This created a messagebox, displays it, and **returns immediately** (_while the message is still being displayed on the screen_)

To discover when the player has selected an option, you are expected to 'poll' using the function `IsMessageResultAvailable`:

### Example
```psc
int messageBoxId = SkyrMessage.Show_NonBlocking("body text", "button 1", "button 2", ...)
while ! SkyMessage.IsMessageResultAvailable(messageBoxId)
  Utility.WaitMenuMode(1.0)
endWhile
```

Once `IsMessageResultAvailable` returns true, you can then find out which option the player selected via `GetResultText()` or `GetResultIndex()`

### Get name of selected button
```psc
string buttonName = SkyMessage.GetResultText(messageboxId)
```

### Get index of selected button
```psc
string buttonIndex = SkyMessage.GetResultIndex(messageboxId)
```

### Gotcha!
After calling either `GetResultText()` or `GetResultIndex()`, the `messageBoxId` will become invalid, so you cannot get **BOTH** the index **and** the text. _Unless_ you use `deleteResultOnAccess = false`
```psc
; Get the index -but- keep the messageBoxId valid!
string buttonIndex = SkyMessage.GetResultIndex(messageboxId, deleteResultOnAccess = false)

; So now you can get the name as well
string buttonName = SkyMessage.GetResultText(messageboxId)
```

If you ever have a reason to delete the `messageBoxId` without getting the result, you can call `SkyMessage.Delete(messageBoxId)`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `bodyText` | `String` | ✓ |  |
| `button1` | `String` | ✓ |  |
| `button2` | `String` |  | `""` |
| `button3` | `String` |  | `""` |
| `button4` | `String` |  | `""` |
| `button5` | `String` |  | `""` |
| `button6` | `String` |  | `""` |
| `button7` | `String` |  | `""` |
| `button8` | `String` |  | `""` |
| `button9` | `String` |  | `""` |
| `button10` | `String` |  | `""` |

### `ShowArray(bodyText, buttons, getIndex, waitInterval, timeoutSeconds) → String`

**Flags:** Global

Open a messagebox and return the name of the selected button, given a string array of button names.

_Useful for when you are not hard-coding the names of the buttons!_

#### Button names
Any string array should work for button names.
```psc
string[] buttons = new string[2]
buttons[0] = "Ok"
buttons[1] = "Cancel"
```

### Get name of selected button
```psc
string selectedButtonName = SkyMessage.Show("body text of message", buttons)
```

### Get index of selected button
If you would prefer to get the `int` index of the button:
```psc
string selectedButtonIndex = SkyMessage.Show("body text of message", buttons, getIndex = true)
```

### Advanced
You can also specify a timeout. This is the number of seconds after which the function will return with the text "TIMED_OUT":
```psc
string selectedButtonName = SkyMessage.Show("body text of message", buttons, timeoutSeconds = 69.0)
```

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `bodyText` | `String` | ✓ |  |
| `buttons` | `String[]` | ✓ |  |
| `getIndex` | `Bool` |  | `false` |
| `waitInterval` | `Float` |  | `0.1` |
| `timeoutSeconds` | `Float` |  | `0` |

### `ShowArray_NonBlocking(bodyText, buttons) → Int`

**Flags:** Native Global

This created a messagebox, displays it, and **returns immediately** (_while the message is still being displayed on the screen_)

To discover when the player has selected an option, you are expected to 'poll' using the function `IsMessageResultAvailable`:

_Useful for when you are not hard-coding the names of the buttons!_

#### Button names
Any string array should work for button names.
```psc
string[] buttons = new string[2]
buttons[0] = "Ok"
buttons[1] = "Cancel"
```

### Example
```psc
int messageBoxId = SkyrMessage.Show_NonBlocking("body text", buttons)
while ! SkyMessage.IsMessageResultAvailable(messageBoxId)
  Utility.WaitMenuMode(1.0)
endWhile
```

Once `IsMessageResultAvailable` returns true, you can then find out which option the player selected via `GetResultText()` or `GetResultIndex()`

### Get name of selected button
```psc
string buttonName = SkyMessage.GetResultText(messageboxId)
```

### Get index of selected button
```psc
string buttonIndex = SkyMessage.GetResultIndex(messageboxId)
```

### Gotcha!
After calling either `GetResultText()` or `GetResultIndex()`, the `messageBoxId` will become invalid, so you cannot get **BOTH** the index **and** the text. _Unless_ you use `deleteResultOnAccess = false`
```psc
; Get the index -but- keep the messageBoxId valid!
string buttonIndex = SkyMessage.GetResultIndex(messageboxId, deleteResultOnAccess = false)

; So now you can get the name as well
string buttonName = SkyMessage.GetResultText(messageboxId)
```

If you ever have a reason to delete the `messageBoxId` without getting the result, you can call `SkyMessage.Delete(messageBoxId)`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `bodyText` | `String` | ✓ |  |
| `buttons` | `String[]` | ✓ |  |
