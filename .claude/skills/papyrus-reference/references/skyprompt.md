# `SkyPrompt`

**Source:** `skyprompt` (SkyPrompt)

---

## Events

### `OnSkyPromptEvent(clientID, eventType, eventID, actionID, dx, dy, progress)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `clientID` | `Int` |
| `eventType` | `Int` |
| `eventID` | `Int` |
| `actionID` | `Int` |
| `dx` | `Float` |
| `dy` | `Float` |
| `progress` | `Float` |

---

## Global Functions

### `RegisterForSkyPromptEvent(akForm, a_major, a_minor) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `a_major` | `Int` | ✓ |  |
| `a_minor` | `Int` | ✓ |  |

### `RemovePrompt(clientID, eventID, actionID)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `clientID` | `Int` | ✓ |  |
| `eventID` | `Int` | ✓ |  |
| `actionID` | `Int` | ✓ |  |

### `RequestTheme(clientID, theme_name) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `clientID` | `Int` | ✓ |  |
| `theme_name` | `String` | ✓ |  |

### `SendPrompt(clientID, text, eventID, actionID, type, refForm, devices, keys, progress) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `clientID` | `Int` | ✓ |  |
| `text` | `String` | ✓ |  |
| `eventID` | `Int` | ✓ |  |
| `actionID` | `Int` | ✓ |  |
| `type` | `Int` | ✓ |  |
| `refForm` | `Form` | ✓ |  |
| `devices` | `Int[]` | ✓ |  |
| `keys` | `Int[]` | ✓ |  |
| `progress` | `Float` | ✓ |  |

### `UnregisterFromSkyPromptEvent(akForm) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
