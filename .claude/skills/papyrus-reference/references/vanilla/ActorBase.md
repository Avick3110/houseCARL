# `ActorBase`

**Source:** `vanilla` • **Extends:** `Form` • **Flags:** Hidden

---

## Functions

### `GetClass() → class`

**Flags:** Native

Returns this actor's class

### `GetDeadCount() → Int`

**Flags:** Native

Gets the number of actors of this type that have been killed

### `GetGiftFilter() → FormList`

**Flags:** Native

Returns this actor's gift filter formlist

### `GetRace() → Race`

**Flags:** Native

Returns this actor's race

### `GetSex() → Int`

**Flags:** Native

Returns this actor's sex. Values for sex are:
-1 - None
0 - Male
1 - Female

### `IsEssential() → Bool`

**Flags:** Native

Is this actor essential?

### `IsInvulnerable() → Bool`

**Flags:** Native

Is this actor invulnerable?

### `IsProtected() → Bool`

**Flags:** Native

Is this actor protected (can only be killed by player)?

### `IsUnique() → Bool`

**Flags:** Native

Is this actor base unique?

### `SetEssential(abEssential)`

**Flags:** Native

Sets this actor as essential or not - if set as essential, will UNSET protected

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abEssential` | `Bool` |  | `true` |

### `SetInvulnerable(abInvulnerable)`

**Flags:** Native

Sets this actor as invulnerable or not

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abInvulnerable` | `Bool` |  | `true` |

### `SetOutfit(akOutfit, abSleepOutfit)`

**Flags:** Native

Sets the actors outfit

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akOutfit` | `Outfit` | ✓ |  |
| `abSleepOutfit` | `Bool` |  | `false` |

### `SetProtected(abProtected)`

**Flags:** Native

Sets this actor as protected or not - if set as protected, will UNSET essential

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abProtected` | `Bool` |  | `true` |
