# `Input`

**Source:** `skse` (Skyrim Script Extender x64) • **Flags:** Hidden

---

## Global Functions

### `GetMappedControl(keycode) → String`

**Flags:** Native Global

returns name of control bound to given keycode, or "" if unbound

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `keycode` | `Int` | ✓ |  |

### `GetMappedKey(control, deviceType) → Int`

**Flags:** Native Global

returns keycode bound to a control for given device

Valid controls:
"Forward", "Back", "Strafe Left", "Strafe Right", "Move", "Look", "Left Attack/Block", "Right Attack/Block"
"Activate", "Ready Weapon", "Tween Menu", "Toggle POV", "Zoom Out", "Zoom In", "Jump", "Sprint", "Shout",
"Sneak", "Run", "Toggle Always Run", "Auto-Move", "Favorites", "Hotkey1", "Hotkey2", "Hotkey3", "Hotkey4",
"Hotkey5", "Hotkey6", "Hotkey7", "Hotkey8", "Quicksave", "Quickload", "Wait", "Journal", "Pause", "Screenshot",
"Multi-Screenshot", "Console", "CameraPath", "Quick Inventory", "Quick Magic", "Quick Stats", "Quick Map"

Valid device types:
(default)	auto detect
0			keyboard
1			mouse
2			gamepad

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `control` | `String` | ✓ |  |
| `deviceType` | `Int` |  | `0xFF` |

### `GetNthKeyPressed(n) → Int`

**Flags:** Native Global

for walking over the pressed keys

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `n` | `Int` | ✓ |  |

### `GetNumKeysPressed() → Int`

**Flags:** Native Global

how many keys are pressed

### `HoldKey(dxKeycode)`

**Flags:** Native Global

holds down the specified key until released

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `dxKeycode` | `Int` | ✓ |  |

### `IsKeyPressed(dxKeycode) → Bool`

**Flags:** Native Global

returns whether a key is pressed

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `dxKeycode` | `Int` | ✓ |  |

### `ReleaseKey(dxKeycode)`

**Flags:** Native Global

releases the specified key

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `dxKeycode` | `Int` | ✓ |  |

### `TapKey(dxKeycode)`

**Flags:** Native Global

taps the specified key

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `dxKeycode` | `Int` | ✓ |  |
