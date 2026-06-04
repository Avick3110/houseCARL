# `Debug`

**Source:** `vanilla` • **Flags:** Hidden

---

## Global Functions

### `CenterOnCell(asCellname)`

**Flags:** Native Global

COC functionality

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asCellname` | `String` | ✓ |  |

### `CenterOnCellAndWait(asCellname) → Float`

**Flags:** Native Global

COC functionality

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asCellname` | `String` | ✓ |  |

### `CloseUserLog(asLogName)`

**Flags:** Native Global

Closes the specified user log

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asLogName` | `String` | ✓ |  |

### `DBSendPlayerPosition()`

**Flags:** Native Global

Prints out the players position to the database (non-release PC and Xenon builds only)

### `DebugChannelNotify(channel, message)`

**Flags:** Native Global

Outputs the string to a named debug channel (useful on the Xenon currently)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `channel` | `String` | ✓ |  |
| `message` | `String` | ✓ |  |

### `DumpAliasData(akQuest)`

**Flags:** Native Global

Dumps all alias fill information for the quest to the AliasDump log in Logs/Script/

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akQuest` | `Quest` | ✓ |  |

### `GetConfigName() → String`

**Flags:** Native Global

Returns the config name

### `GetPlatformName() → String`

**Flags:** Native Global

Returns the platform name

### `GetVersionNumber() → String`

**Flags:** Native Global

Returns the version number string

### `MessageBox(asMessageBoxText)`

**Flags:** Native Global

Displays an in-game message box

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asMessageBoxText` | `String` | ✓ |  |

### `Notification(asNotificationText)`

**Flags:** Native Global

Displays an in-game notification

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asNotificationText` | `String` | ✓ |  |

### `OpenUserLog(asLogName) → Bool`

**Flags:** Native Global

Opens a user log - fails if the log is already open

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asLogName` | `String` | ✓ |  |

### `PlayerMoveToAndWait(asDestRef) → Float`

**Flags:** Native Global

player.moveto functionality

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asDestRef` | `String` | ✓ |  |

### `QuitGame()`

**Flags:** Native Global

Quits the game

### `SendAnimationEvent(arRef, asEventName)`

**Flags:** Native Global

Forcibly sends an animation event to a reference's behavior graph
used to bypass actor limitation on the ObjectReference version

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arRef` | `ObjectReference` | ✓ |  |
| `asEventName` | `String` | ✓ |  |

### `SetFootIK(abFootIK)`

**Flags:** Native Global

Toggles Foot IK on/off

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abFootIK` | `Bool` | ✓ |  |

### `SetGodMode(abGodMode)`

**Flags:** Native Global

TGM functionality

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abGodMode` | `Bool` | ✓ |  |

### `ShowRefPosition(arRef)`

**Flags:** Native Global

Used to add a tripod to a reference (non-release builds only)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arRef` | `ObjectReference` | ✓ |  |

### `StartScriptProfiling(asScriptName)`

**Flags:** Native Global

Start profiing a specific script - setting doesn't persist across saves
Will do nothing on release console builds, and if the Papyrus:bEnableProfiling ini setting is off

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asScriptName` | `String` | ✓ |  |

### `StartStackProfiling()`

**Flags:** Native Global

Start profiling the calling stack - setting doesn't persist across saves
Will do nothing on release console builds, and if the Papyrus:bEnableProfiling ini setting is off

### `StopScriptProfiling(asScriptName)`

**Flags:** Native Global

Stop profiling a specific script - setting doesn't persist across saves
Will do nothing on release console builds, and if the Papyrus:bEnableProfiling ini setting is off

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asScriptName` | `String` | ✓ |  |

### `StopStackProfiling()`

**Flags:** Native Global

Stop profiling the calling stack - setting doesn't persist across saves
Will do nothing on release console builds, and if the Papyrus:bEnableProfiling ini setting is off

### `TakeScreenshot(asFilename)`

**Flags:** Native Global

Takes a screenshot (Xenon only)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asFilename` | `String` | ✓ |  |

### `ToggleAI()`

**Flags:** Native Global

ToggleAI

### `ToggleCollisions()`

**Flags:** Native Global

TCL functionality

### `ToggleMenus()`

**Flags:** Native Global

Toggles menus on/off

### `Trace(asTextToPrint, aiSeverity)`

**Flags:** Native Global

Outputs the string to the log
Severity is one of the following:
0 - Info
1 - Warning
2 - Error

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asTextToPrint` | `String` | ✓ |  |
| `aiSeverity` | `Int` |  | `0` |

### `TraceAndBox(asTextToPrint, aiSeverity)`

**Flags:** Global

A convenience function to both throw a message box AND write to the trace log, since message boxes sometimes stack in weird ways and won't show up reliably.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asTextToPrint` | `String` | ✓ |  |
| `aiSeverity` | `Int` |  | `0` |

### `TraceConditional(TextToPrint, ShowTrace)`

**Flags:** Global

As Trace() but takes a second parameter bool ShowTrace (which if false suppresses the message). Used to turn off and on traces that might be otherwise annoying.

Suppressable Trace

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `TextToPrint` | `String` | ✓ |  |
| `ShowTrace` | `Bool` | ✓ |  |

### `TraceStack(asTextToPrint, aiSeverity)`

**Flags:** Native Global

Outputs the current stack to the log

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asTextToPrint` | `String` |  | `"racing stack on request"` |
| `aiSeverity` | `Int` |  | `0` |

### `TraceUser(asUserLog, asTextToPrint, aiSeverity) → Bool`

**Flags:** Native Global

Outputs the string to a user log - fails if the log hasn't been opened

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asUserLog` | `String` | ✓ |  |
| `asTextToPrint` | `String` | ✓ |  |
| `aiSeverity` | `Int` |  | `0` |
