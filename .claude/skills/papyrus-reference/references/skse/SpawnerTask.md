# `SpawnerTask`

**Source:** `skse` (Skyrim Script Extender x64) • **Flags:** Hidden

---

## Global Functions

### `AddSpawn(handle, formToPlace, target, positionOffset, rotation, count, bForcePersist, bInitiallyDisabled)`

**Flags:** Native Global

Adds a spawn to the task identified by the given handle.
Running the task places a new instance of formToPlace at target reference with given rotation and position offset. Parameters are analogously defined to PlaceAtMe.
Multiple spawns can be added to the same task to be executed in a batch (which is the purpose).
(Function type: non-delayed)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `handle` | `Int` | ✓ |  |
| `formToPlace` | `Form` | ✓ |  |
| `target` | `ObjectReference` | ✓ |  |
| `positionOffset` | `Float[]` | ✓ |  |
| `rotation` | `Float[]` | ✓ |  |
| `count` | `Int` |  | `1` |
| `bForcePersist` | `Bool` |  | `false` |
| `bInitiallyDisabled` | `Bool` |  | `false` |

### `Cancel(handle)`

**Flags:** Native Global

Cancels a task before running it and frees its allocated resources.
Tasks cannot be canceled once they have been started with Run, and vice versa.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `handle` | `Int` | ✓ |  |

### `Create() → Int`

**Flags:** Native Global

Creates a new SpawnerTask and returns a handle, which is an identifier for the created task.
The task handle is valid until the task has been run or canceled, or until the calling stack has exited.
(Function type: non-delayed)

### `Run(handle) → ObjectReference[]`

**Flags:** Native Global

Runs the task and returns the spawned references in an array. May return arrays with a size larger than 128.
The resources allocated to the task are freed in the process, so the same task handle cannot be run twice.
(Function type: latent)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `handle` | `Int` | ✓ |  |
