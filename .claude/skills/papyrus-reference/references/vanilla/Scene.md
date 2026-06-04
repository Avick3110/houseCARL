# `Scene`

**Source:** `vanilla` • **Extends:** `Form` • **Flags:** Hidden

---

## Functions

### `ForceStart()`

**Flags:** Native

Forces a scene to start and kills the current scenes running on any ref in that scene

### `GetOwningQuest() → Quest`

**Flags:** Native

Returns the quest that owns this scene

### `IsActionComplete(aiActionID) → Bool`

**Flags:** Native

Returns whether the specified action is complete or not

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiActionID` | `Int` | ✓ |  |

### `IsPlaying() → Bool`

**Flags:** Native

Is this scene currently playing?

### `Start()`

**Flags:** Native

Starts this scene

### `Stop()`

**Flags:** Native

Stops the scene
