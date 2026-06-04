# `Faction`

**Source:** `vanilla` • **Extends:** `Form` • **Flags:** Hidden

---

## Functions

### `CanPayCrimeGold() → Bool`

**Flags:** Native

Checks to see if the player can pay the crime gold for this faction

### `GetCrimeGold() → Int`

**Flags:** Native

Gets the amount of gold the player is to pay to this faction for crimes

### `GetCrimeGoldNonViolent() → Int`

**Flags:** Native

Gets the amount of gold the player is to pay to this faction for non-violent crimes

### `GetCrimeGoldViolent() → Int`

**Flags:** Native

Gets the amount of gold the player is to pay to this faction for violent crimes

### `GetInfamy() → Int`

**Flags:** Native

Get the player's "infamy" with this faction (accumulated crime gold)

### `GetInfamyNonViolent() → Int`

**Flags:** Native

Get the player's "non-violent infamy" with this faction (accumulated non-violent crime gold)

### `GetInfamyViolent() → Int`

**Flags:** Native

Get the player's "violent infamy" with this faction (accumulated violent crime gold)

### `GetReaction(akOther) → Int`

**Flags:** Native

Gets this faction's reaction towards the other

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akOther` | `Faction` | ✓ |  |

### `GetStolenItemValueCrime() → Int`

**Flags:** Native

Obtains the value of all items stolen by the player from this faction that was witnessed

### `GetStolenItemValueNoCrime() → Int`

**Flags:** Native

Obtains the value of all items stolen by the player from this faction that was NOT witnessed

### `IsFactionInCrimeGroup(akOther) → Bool`

**Flags:** Native

Is the passed in faction in this faction's crime group

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akOther` | `Faction` | ✓ |  |

### `IsPlayerExpelled() → Bool`

**Flags:** Native

Is the player expelled from this faction?

### `ModCrimeGold(aiAmount, abViolent)`

**Flags:** Native

Modifies the amount of crime gold for this faction - violent or non-violent

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiAmount` | `Int` | ✓ |  |
| `abViolent` | `Bool` |  | `false` |

### `ModReaction(akOther, aiAmount)`

**Flags:** Native

Modifies this faction's reaction towards the other faction

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akOther` | `Faction` | ✓ |  |
| `aiAmount` | `Int` | ✓ |  |

### `PlayerPayCrimeGold(abRemoveStolenItems, abGoToJail)`

**Flags:** Native

Has the player pay the crime gold for this faction

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abRemoveStolenItems` | `Bool` |  | `true` |
| `abGoToJail` | `Bool` |  | `true` |

### `SendAssaultAlarm()`

**Flags:** Native

Finds a nearby NPC in this faction and has them behave as if assaulted

### `SendPlayerToJail(abRemoveInventory, abRealJail)`

**Flags:** Native

Sends the player to this faction's jail - removing inventory if requested, and to a "real" jail or not

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abRemoveInventory` | `Bool` |  | `true` |
| `abRealJail` | `Bool` |  | `true` |

### `SetAlly(akOther, abSelfIsFriendToOther, abOtherIsFriendToSelf)`

**Flags:** Native

Sets this faction and the other as allies or friends - if the friend booleans are true - the specified one-way relationship
is a friend instead of an ally

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akOther` | `Faction` | ✓ |  |
| `abSelfIsFriendToOther` | `Bool` |  | `false` |
| `abOtherIsFriendToSelf` | `Bool` |  | `false` |

### `SetCrimeGold(aiGold)`

**Flags:** Native

Sets the non-violent crime gold on this faction

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiGold` | `Int` | ✓ |  |

### `SetCrimeGoldViolent(aiGold)`

**Flags:** Native

Sets the violent crime gold on this faction

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiGold` | `Int` | ✓ |  |

### `SetEnemy(akOther, abSelfIsNeutralToOther, abOtherIsNeutralToSelf)`

**Flags:** Native

Sets this faction and the other as enemies or neutral - if the friend booleans are true - the specified one-way relationship
is a neutral instead of an enemy

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akOther` | `Faction` | ✓ |  |
| `abSelfIsNeutralToOther` | `Bool` |  | `false` |
| `abOtherIsNeutralToSelf` | `Bool` |  | `false` |

### `SetPlayerEnemy(abIsEnemy)`

**Flags:** Native

Sets or clears the player as an enemy of this faction

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abIsEnemy` | `Bool` |  | `true` |

### `SetPlayerExpelled(abIsExpelled)`

**Flags:** Native

Sets or clears the expelled flag for this faction on the player

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abIsExpelled` | `Bool` |  | `true` |

### `SetReaction(akOther, aiNewValue)`

**Flags:** Native

Sets this faction's reaction towards the other

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akOther` | `Faction` | ✓ |  |
| `aiNewValue` | `Int` | ✓ |  |
