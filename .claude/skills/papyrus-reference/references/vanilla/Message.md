# `Message`

**Source:** `vanilla` • **Extends:** `Form` • **Flags:** Hidden

---

## Global Functions

### `ResetHelpMessage(asEvent)`

**Flags:** Native Global

Resets help message status for an input event so a new message can be displayed for that event.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asEvent` | `String` | ✓ |  |

---

## Functions

### `Show(afArg1, afArg2, afArg3, afArg4, afArg5, afArg6, afArg7, afArg8, afArg9) → Int`

**Flags:** Native

Show this message on the screen, substituting the values as appropriate. If a message box, it will wait until the user closes the box
before returning - returning the button the user hit. If not a message box, or something went wrong, it will return -1

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afArg1` | `Float` |  | `0` |
| `afArg2` | `Float` |  | `0` |
| `afArg3` | `Float` |  | `0` |
| `afArg4` | `Float` |  | `0` |
| `afArg5` | `Float` |  | `0` |
| `afArg6` | `Float` |  | `0` |
| `afArg7` | `Float` |  | `0` |
| `afArg8` | `Float` |  | `0` |
| `afArg9` | `Float` |  | `0` |

### `ShowAsHelpMessage(asEvent, afDuration, afInterval, aiMaxTimes)`

**Flags:** Native

Shows help message for a user action on screen.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asEvent` | `String` | ✓ |  |
| `afDuration` | `Float` | ✓ |  |
| `afInterval` | `Float` | ✓ |  |
| `aiMaxTimes` | `Int` | ✓ |  |
