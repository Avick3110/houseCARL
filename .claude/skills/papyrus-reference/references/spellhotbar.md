# `SpellHotbar`

**Source:** `spellhotbar` (Spell Hotbar) • **Flags:** Hidden

---

## Global Functions

### `bindMenuMoveBarLeft()`

**Flags:** Native Global

### `bindMenuMoveBarRight()`

**Flags:** Native Global

### `clearBars()`

**Flags:** Native Global

### `fileExists(filenname) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `filenname` | `String` | ✓ |  |

### `getBarEnabled(id) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `id` | `Int` | ✓ |  |

### `getCurrentSelectedSpellInMenu() → form`

**Flags:** Native Global

### `getHudBarShowMode() → Int`

**Flags:** Native Global

### `getHudBarShowModeVampireLord() → Int`

**Flags:** Native Global

### `getHudBarShowModeWerewolf() → Int`

**Flags:** Native Global

### `getInheritMode(id) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `id` | `Int` | ✓ |  |

### `getNumberOfSlots() → Int`

**Flags:** Native Global

### `getOffsetX() → Float`

**Flags:** Native Global

### `getOffsetY() → Float`

**Flags:** Native Global

### `getSlotScale() → Float`

**Flags:** Native Global

### `getSlotSpacing() → Float`

**Flags:** Native Global

### `getSlottedSpell(index) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |

### `getTextShowMode() → Int`

**Flags:** Native Global

### `highlightSlot(slot, error, duration)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `slot` | `Int` | ✓ |  |
| `error` | `Bool` | ✓ |  |
| `duration` | `Float` | ✓ |  |

### `isAltBarEnabled() → Bool`

**Flags:** Native Global

### `isCtrlBarEnabled() → Bool`

**Flags:** Native Global

### `isDefaultBarWhenSheathed() → Bool`

**Flags:** Native Global

### `isDisableMenuRendering() → Bool`

**Flags:** Native Global

### `isPlayerOnCD(form) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `form` | `Form` | ✓ |  |

### `isPlayerOnGCD() → Bool`

**Flags:** Native Global

### `isShiftBarEnabled() → Bool`

**Flags:** Native Global

### `isTransformedFavMenuBind() → Bool`

**Flags:** Native Global

### `loadBarsFromFile(filename_mod_dir, filename_user_dir) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `filename_mod_dir` | `String` | ✓ |  |
| `filename_user_dir` | `String` | ✓ |  |

### `reloadData()`

**Flags:** Native Global

### `reloadResources()`

**Flags:** Native Global

### `saveBarsToFile(filenname) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `filenname` | `String` | ✓ |  |

### `setHudBarShowMode(mode) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `mode` | `Int` | ✓ |  |

### `setHudBarShowModeVampireLord(mode) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `mode` | `Int` | ✓ |  |

### `setHudBarShowModeWerewolf(mode) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `mode` | `Int` | ✓ |  |

### `setInheritMode(id, mode) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `id` | `Int` | ✓ |  |
| `mode` | `Int` | ✓ |  |

### `setNumberOfSlots(num) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `num` | `Int` | ✓ |  |

### `setOffsetX(value) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `value` | `Float` | ✓ |  |

### `setOffsetY(value) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `value` | `Float` | ✓ |  |

### `setPlayerGCD(time)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `time` | `Float` | ✓ |  |

### `setSlotScale(scale) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `scale` | `Float` | ✓ |  |

### `setSlotSpacing(spacing) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `spacing` | `Float` | ✓ |  |

### `setTextShowMode(mode) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `mode` | `Int` | ✓ |  |

### `setupCastAndGetCasttime(form) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `form` | `Form` | ✓ |  |

### `showDragBar()`

**Flags:** Native Global

### `slotSpell(form, index, type) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `form` | `Form` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `type` | `Int` | ✓ |  |

### `toggleAltBarEnabled() → Bool`

**Flags:** Native Global

### `toggleBarEnabled(id) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `id` | `Int` | ✓ |  |

### `toggleCtrlBarEnabled() → Bool`

**Flags:** Native Global

### `toggleDefaultBarWhenSheathed() → Bool`

**Flags:** Native Global

### `toggleDisableMenuRendering() → Bool`

**Flags:** Native Global

### `toggleShiftBarEnabled() → Bool`

**Flags:** Native Global

### `triggerSkillCooldown(form)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `form` | `Form` | ✓ |  |
