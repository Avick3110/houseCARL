# `CustomSkills`

**Source:** `custom-skills` (Custom Skills Framework) • **Flags:** Hidden

---

## Global Functions

### `AdvanceSkill(asSkillId, afMagnitude)`

**Flags:** Native Global

Advance the given skill by the provided amount of skill usage.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asSkillId` | `String` | ✓ |  |
| `afMagnitude` | `Float` | ✓ |  |

### `DebugReload()`

**Flags:** Native Global

Reload configurations. For debug usage only.

### `GetAPIVersion() → Int`

**Flags:** Native Global

Get the current Custom Skills API version.
Current version: 3

### `GetSkillLevel(asSkillId) → Int`

**Flags:** Native Global

Get the current level of the given skill.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asSkillId` | `String` | ✓ |  |

### `GetSkillName(asSkillId) → String`

**Flags:** Native Global

Get the display name of the given skill.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asSkillId` | `String` | ✓ |  |

### `IncrementSkill(asSkillId)`

**Flags:** Native Global

Increment the given skill by one point.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asSkillId` | `String` | ✓ |  |

### `IncrementSkillBy(asSkillId, aiCount)`

**Flags:** Native Global

Increment the given skill by the given number of points.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asSkillId` | `String` | ✓ |  |
| `aiCount` | `Int` | ✓ |  |

### `OpenCustomSkillMenu(asSkillId)`

**Flags:** Native Global

Open the custom skill menu for the given skill or group (config file).

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asSkillId` | `String` | ✓ |  |

### `ShowSkillIncreaseMessage(asSkillId, aiSkillLevel)`

**Flags:** Native Global

Displays the skill increase message on the HUD for the given skill and level.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asSkillId` | `String` | ✓ |  |
| `aiSkillLevel` | `Int` | ✓ |  |

### `ShowTrainingMenu(asSkillId, aiMaxLevel, akTrainer)`

**Flags:** Native Global

Displays the training menu for the given skill, maximum level, and trainer actor.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asSkillId` | `String` | ✓ |  |
| `aiMaxLevel` | `Int` | ✓ |  |
| `akTrainer` | `Actor` | ✓ |  |


---

## `CustomSkills_ActiveMagicEffectExt`

**Source:** `custom-skills` (Custom Skills Framework) • **Flags:** Hidden

---

## Events

### `OnCustomSkillBookRead(asSkillId, aiIncrement)`

**Kind:** Event

Copy this event to your script if you register for it.

**Parameters**

| Name | Type |
|---|---|
| `asSkillId` | `String` |
| `aiIncrement` | `Int` |

### `OnCustomSkillIncrease(asSkillId)`

**Kind:** Event

Copy this event to your script if you register for it.

**Parameters**

| Name | Type |
|---|---|
| `asSkillId` | `String` |

---

## Global Functions

### `RegisterForCustomSkillBookRead(akReceiver, abReplaceDefault)`

**Flags:** Native Global

Register a magic effect for the custom skill book read event.
The default skill book behavior can optionally be replaced.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akReceiver` | `ActiveMagicEffect` | ✓ |  |
| `abReplaceDefault` | `Bool` |  | `false` |

### `RegisterForCustomSkillIncrease(akReceiver)`

**Flags:** Native Global

Register a magic effect for the custom skill increase event.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akReceiver` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterForCustomSkillBookRead(akReceiver)`

**Flags:** Native Global

Unregisters a magic effect from receiving the custom skill book read event.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akReceiver` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterForCustomSkillIncrease(akReceiver)`

**Flags:** Native Global

Unregisters a magic effect from receiving the custom skill increase event.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akReceiver` | `ActiveMagicEffect` | ✓ |  |


---

## `CustomSkills_AliasExt`

**Source:** `custom-skills` (Custom Skills Framework) • **Flags:** Hidden

---

## Events

### `OnCustomSkillBookRead(asSkillId, aiIncrement)`

**Kind:** Event

Copy this event to your script if you register for it.

**Parameters**

| Name | Type |
|---|---|
| `asSkillId` | `String` |
| `aiIncrement` | `Int` |

### `OnCustomSkillIncrease(asSkillId)`

**Kind:** Event

Copy this event to your script if you register for it.

**Parameters**

| Name | Type |
|---|---|
| `asSkillId` | `String` |

---

## Global Functions

### `RegisterForCustomSkillBookRead(akReceiver, abReplaceDefault)`

**Flags:** Native Global

Register an alias for the custom skill book read event.
The default skill book behavior can optionally be replaced.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akReceiver` | `Alias` | ✓ |  |
| `abReplaceDefault` | `Bool` |  | `false` |

### `RegisterForCustomSkillIncrease(akReceiver)`

**Flags:** Native Global

Register an alias for the custom skill increase event.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akReceiver` | `Alias` | ✓ |  |

### `UnregisterForCustomSkillBookRead(akReceiver)`

**Flags:** Native Global

Unregisters an alias from receiving the custom skill book read event.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akReceiver` | `Alias` | ✓ |  |

### `UnregisterForCustomSkillIncrease(akReceiver)`

**Flags:** Native Global

Unregisters an alias from receiving the custom skill increase event.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akReceiver` | `Alias` | ✓ |  |


---

## `CustomSkills_FormExt`

**Source:** `custom-skills` (Custom Skills Framework) • **Flags:** Hidden

---

## Events

### `OnCustomSkillBookRead(asSkillId, aiIncrement)`

**Kind:** Event

Copy this event to your script if you register for it.

**Parameters**

| Name | Type |
|---|---|
| `asSkillId` | `String` |
| `aiIncrement` | `Int` |

### `OnCustomSkillIncrease(asSkillId)`

**Kind:** Event

Copy this event to your script if you register for it.

**Parameters**

| Name | Type |
|---|---|
| `asSkillId` | `String` |

---

## Global Functions

### `RegisterForCustomSkillBookRead(akReceiver, abReplaceDefault)`

**Flags:** Native Global

Register a form for the custom skill book read event.
The default skill book behavior can optionally be replaced.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akReceiver` | `Form` | ✓ |  |
| `abReplaceDefault` | `Bool` |  | `false` |

### `RegisterForCustomSkillIncrease(akReceiver)`

**Flags:** Native Global

Register a form for the custom skill increase event.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akReceiver` | `Form` | ✓ |  |

### `UnregisterForCustomSkillBookRead(akReceiver)`

**Flags:** Native Global

Unregisters a form from receiving the custom skill book read event.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akReceiver` | `Form` | ✓ |  |

### `UnregisterForCustomSkillIncrease(akReceiver)`

**Flags:** Native Global

Unregisters a form from receiving the custom skill increase event.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akReceiver` | `Form` | ✓ |  |
