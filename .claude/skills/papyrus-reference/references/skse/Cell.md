# `Cell`

**Source:** `skse` (Skyrim Script Extender x64) • **Extends:** `Form` • **Flags:** Hidden

---

## Functions

### `GetActorOwner() → actorbase`

**Flags:** Native

Gets the actor that owns this cell (or none if not owned by an actor)

### `GetActualWaterLevel() → Float`

**Flags:** Native

Returns water level of the cell, if default returns water level from worldspace

### `GetFactionOwner() → Faction`

**Flags:** Native

Gets the faction that owns this cell (or none if not owned by a faction)

### `GetNthRef(n, formTypeFilter) → ObjectReference`

**Flags:** Native

returns the ref at the specified index

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `n` | `Int` | ✓ |  |
| `formTypeFilter` | `Int` |  | `0` |

### `GetNumRefs(formTypeFilter) → Int`

**Flags:** Native

SKSE64 additions built 2024-01-17 20:01:40.731000 UTC
Returns the number of refs in the cell

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `formTypeFilter` | `Int` |  | `0` |

### `GetWaterLevel() → Float`

**Flags:** Native

Returns the water level of the cell (-2147483648 if no water)

### `IsAttached() → Bool`

**Flags:** Native

Is this cell "attached"? (In the loaded area)

### `IsInterior() → Bool`

**Flags:** Native

Is this cell an interior cell?

### `Reset()`

**Flags:** Native

Flags the cell for reset on next load

### `SetActorOwner(akActor)`

**Flags:** Native

Sets this cell's owner as the specified actor

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `ActorBase` | ✓ |  |

### `SetFactionOwner(akFaction)`

**Flags:** Native

Sets this cell's owner as the specified faction

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFaction` | `Faction` | ✓ |  |

### `SetFogColor(aiNearRed, aiNearGreen, aiNearBlue, aiFarRed, aiFarGreen, aiFarBlue)`

**Flags:** Native

Sets the fog color for this cell (interior, non-sky-lit cells only)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiNearRed` | `Int` | ✓ |  |
| `aiNearGreen` | `Int` | ✓ |  |
| `aiNearBlue` | `Int` | ✓ |  |
| `aiFarRed` | `Int` | ✓ |  |
| `aiFarGreen` | `Int` | ✓ |  |
| `aiFarBlue` | `Int` | ✓ |  |

### `SetFogPlanes(afNear, afFar)`

**Flags:** Native

Adjusts this cell's fog near and far planes (interior, non-sky-lit cells only)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afNear` | `Float` | ✓ |  |
| `afFar` | `Float` | ✓ |  |

### `SetFogPower(afPower)`

**Flags:** Native

Sets the fog power for this cell (interior, non-sky-lit cells only)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afPower` | `Float` | ✓ |  |

### `SetPublic(abPublic)`

**Flags:** Native

Sets this cell as public or private

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abPublic` | `Bool` |  | `true` |
