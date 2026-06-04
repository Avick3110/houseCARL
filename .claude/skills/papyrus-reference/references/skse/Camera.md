# `Camera`

**Source:** `skse` (Skyrim Script Extender x64) • **Flags:** Hidden

---

## Global Functions

### `GetCameraState() → Int`

**Flags:** Native Global

Returns the character's current camera state
0 - first person
1 - auto vanity
2 - VATS
3 - free
4 - iron sights
5 - furniture
6 - transition
7 - tweenmenu
8 - third person 1
9 - third person 2
10 - horse
11 - bleedout
12 - dragon

### `GetFirstPersonFieldOfView() → Float`

**Flags:** Native Global

Returns the player's camera FOV

### `GetFirstPersonFOV() → Float`

**Flags:** Global

### `GetWorldFieldOfView() → Float`

**Flags:** Native Global

Returns the player's camera FOV

### `GetWorldFOV() → Float`

**Flags:** Global

### `SetFirstPersonFieldOfView(fov)`

**Flags:** Native Global

Sets the player's camera FOV

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fov` | `Float` | ✓ |  |

### `SetFirstPersonFOV(fov)`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fov` | `Float` | ✓ |  |

### `SetWorldFieldOfView(fov)`

**Flags:** Native Global

Sets the player's camera FOV

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fov` | `Float` | ✓ |  |

### `SetWorldFOV(fov)`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fov` | `Float` | ✓ |  |

### `UpdateThirdPerson()`

**Flags:** Native Global

Updates the camera when changing Shoulder positions
