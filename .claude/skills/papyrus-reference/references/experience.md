# `Experience`

**Source:** `experience` (Experience) • **Flags:** Hidden

---

## Global Functions

### `AddExperience(points, meter)`

**Flags:** Native Global

Adds specified amount of experience points, optionally shows meter

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `points` | `Int` | ✓ |  |
| `meter` | `Bool` |  | `false` |

### `GetScriptVersion() → Int`

**Flags:** Global

### `GetSettingBool(setting) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `setting` | `String` | ✓ |  |

### `GetSettingFloat(setting) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `setting` | `String` | ✓ |  |

### `GetSettingInt(setting) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `setting` | `String` | ✓ |  |

### `GetSettingString(setting) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `setting` | `String` | ✓ |  |

### `GetSkillCap(skill) → Float`

**Flags:** Native Global

www.creationkit.com/index.php?title=ActorValueInfo_Script

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `skill` | `Int` | ✓ |  |

### `GetVersion() → String`

**Flags:** Native Global

Verion format: Major.Minor.Patch.Build

### `SaveSettings()`

**Flags:** Native Global

Saves all setting modifications to file

### `SetSettingBool(setting, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `setting` | `String` | ✓ |  |
| `value` | `Bool` | ✓ |  |

### `SetSettingFloat(setting, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `setting` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |

### `SetSettingInt(setting, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `setting` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |

### `SetSettingString(setting, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `setting` | `String` | ✓ |  |
| `value` | `String` | ✓ |  |

### `ShowNotification(text, status, sound)`

**Flags:** Native Global

Shows middle of screen notification, optionally plays sound

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `text` | `String` | ✓ |  |
| `status` | `String` | ✓ |  |
| `sound` | `String` | ✓ |  |
