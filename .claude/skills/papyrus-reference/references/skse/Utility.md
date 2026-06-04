# `Utility`

**Source:** `skse` (Skyrim Script Extender x64) • **Flags:** Hidden

---

## Global Functions

### `CaptureFrameRate(numFrames) → String`

**Flags:** Native Global

Gets you a string describing the frame rate for a certain number of frames
(String will be no longer than 1K characters long, separated by commas)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `numFrames` | `Int` | ✓ |  |

### `CreateAliasArray(size, fill) → Alias[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `size` | `Int` | ✓ |  |
| `fill` | `Alias` |  |  |

### `CreateBoolArray(size, fill) → Bool[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `size` | `Int` | ✓ |  |
| `fill` | `Bool` |  | `false` |

### `CreateFloatArray(size, fill) → Float[]`

**Flags:** Native Global

Size is treated as unsigned, negative numbers will result
extremely large positive numbers, USE WITH CARE

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `size` | `Int` | ✓ |  |
| `fill` | `Float` |  | `0` |

### `CreateFormArray(size, fill) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `size` | `Int` | ✓ |  |
| `fill` | `Form` |  |  |

### `CreateIntArray(size, fill) → Int[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `size` | `Int` | ✓ |  |
| `fill` | `Int` |  | `0` |

### `CreateStringArray(size, fill) → String[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `size` | `Int` | ✓ |  |
| `fill` | `String` |  | `""` |

### `EndFrameRateCapture()`

**Flags:** Native Global

### `GameTimeToString(afGameTime) → String`

**Flags:** Native Global

Converts a float game time (in terms of game days passed) to a string detailing the date
and time it represents in "MM/DD/YYYY HH:MM" format. A 24-hour clock is used, and the function
is latent (due to issues in the current architecture with returning strings from code)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afGameTime` | `Float` | ✓ |  |

### `GetAverageFrameRate() → Float`

**Flags:** Native Global

### `GetBudgetCount() → Int`

**Flags:** Native Global

### `GetBudgetName(aiBudgetNumber) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiBudgetNumber` | `Int` | ✓ |  |

### `GetCurrentBudget(aiBudgetNumber) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiBudgetNumber` | `Int` | ✓ |  |

### `GetCurrentGameTime() → Float`

**Flags:** Native Global

Obtains the current game time in terms of game days passed (same as the global variable)

### `GetCurrentMemory() → Int`

**Flags:** Native Global

Memory tracking functions - only available if memory tracking is turned on

### `GetCurrentRealTime() → Float`

**Flags:** Native Global

Obtains the number of seconds since the application started (the same timer that WaitMenuMode uses)
Does not take into account menu-mode, or VM frozen time
Most useful for determining how long something took to run

### `GetINIBool(ini) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ini` | `String` | ✓ |  |

### `GetINIFloat(ini) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ini` | `String` | ✓ |  |

### `GetINIInt(ini) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ini` | `String` | ✓ |  |

### `GetINIString(ini) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ini` | `String` | ✓ |  |

### `GetMaxFrameRate() → Float`

**Flags:** Native Global

### `GetMinFrameRate() → Float`

**Flags:** Native Global

### `IsInMenuMode() → Bool`

**Flags:** Native Global

Returns whether the game is currently in menu mode or not

### `OverBudget(aiBudgetNumber) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiBudgetNumber` | `Int` | ✓ |  |

### `RandomFloat(afMin, afMax) → Float`

**Flags:** Native Global

Generates a random floating point number between afMin and afMax (inclusive)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afMin` | `Float` |  | `0` |
| `afMax` | `Float` |  | `1` |

### `RandomInt(aiMin, aiMax) → Int`

**Flags:** Native Global

Generates a random integer between aiMin and aiMax (inclusive)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiMin` | `Int` |  | `0` |
| `aiMax` | `Int` |  | `100` |

### `ResizeAliasArray(source, size, fill) → Alias[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `source` | `Alias[]` | ✓ |  |
| `size` | `Int` | ✓ |  |
| `fill` | `Alias` |  |  |

### `ResizeBoolArray(source, size, fill) → Bool[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `source` | `Bool[]` | ✓ |  |
| `size` | `Int` | ✓ |  |
| `fill` | `Bool` |  | `false` |

### `ResizeFloatArray(source, size, fill) → Float[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `source` | `Float[]` | ✓ |  |
| `size` | `Int` | ✓ |  |
| `fill` | `Float` |  | `0` |

### `ResizeFormArray(source, size, fill) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `source` | `Form[]` | ✓ |  |
| `size` | `Int` | ✓ |  |
| `fill` | `Form` |  |  |

### `ResizeIntArray(source, size, fill) → Int[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `source` | `Int[]` | ✓ |  |
| `size` | `Int` | ✓ |  |
| `fill` | `Int` |  | `0` |

### `ResizeStringArray(source, size, fill) → String[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `source` | `String[]` | ✓ |  |
| `size` | `Int` | ✓ |  |
| `fill` | `String` |  | `""` |

### `SetINIBool(ini, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ini` | `String` | ✓ |  |
| `value` | `Bool` | ✓ |  |

### `SetINIFloat(ini, value)`

**Flags:** Native Global

Set the given INI by type

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ini` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |

### `SetINIInt(ini, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ini` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |

### `SetINIString(ini, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ini` | `String` | ✓ |  |
| `value` | `String` | ✓ |  |

### `StartFrameRateCapture()`

**Flags:** Native Global

Starts or ends a frame rate capture -- then you can get the min or max since
frame capture started at any time

### `Wait(afSeconds)`

**Flags:** Native Global

Waits for the specified amount of time (latent). Timer will not run during menu mode

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afSeconds` | `Float` | ✓ |  |

### `WaitGameTime(afHours)`

**Flags:** Native Global

Waits for the specified amount of game time (latent)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afHours` | `Float` | ✓ |  |

### `WaitMenuMode(afSeconds)`

**Flags:** Native Global

Waits for the specified amount of time (latent) - Timer WILL run during menu mode

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afSeconds` | `Float` | ✓ |  |
