# `Race`

**Source:** `skse` (Skyrim Script Extender x64) • **Extends:** `Form` • **Flags:** Hidden

---

## Properties

### `kRace_AllowMultipleMembraneShaders: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x20000000`

### `kRace_AllowPCDialogue: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x200000`

### `kRace_AllowPickpocket: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x800000`

### `kRace_AllowRagdollCollision: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x40000`

### `kRace_AlwaysUseProxyController: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x1000000`

### `kRace_AvoidsRoads: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x80000000`

### `kRace_CantOpenDoors: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x100000`

### `kRace_Child: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x4`

### `kRace_FaceGenHead: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x2`

### `kRace_Flies: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x80`

### `kRace_Immobile: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x200`

### `kRace_NoCombatInWater: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x800`

### `kRace_NoKnockdowns: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x400000`

### `kRace_NoRotatingToHeadTrack: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x1000`

### `kRace_NoShadow: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x20`

### `kRace_NotPushable: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x400`

### `kRace_Playable: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x1`

### `kRace_SpellsAlignWithMagicNode: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x10000`

### `kRace_Swims: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x40`

### `kRace_TiltFrontBack: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x8`

### `kRace_TiltLeftRight: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x10`

### `kRace_UseHeadTrackAnim: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x8000`

### `kRace_UseWorldRaycasts: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x20000`

### `kRace_Walks: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0x100`

---

## Global Functions

### `GetNthPlayableRace(n) → Race`

**Flags:** Native Global

Returns the nth playable race

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `n` | `Int` | ✓ |  |

### `GetNumPlayableRaces() → Int`

**Flags:** Native Global

Returns the number of playable races

### `GetRace(editorId) → Race`

**Flags:** Native Global

Returns a race by it's editorId name

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `editorId` | `String` | ✓ |  |

---

## Functions

### `AllowPCDialogue() → Bool`

### `AllowPickpocket() → Bool`

### `AvoidsRoads() → Bool`

### `CanFly() → Bool`

### `CanSwim() → Bool`

### `CantOpenDoors() → Bool`

### `CanWalk() → Bool`

### `ClearAllowPCDialogue()`

### `ClearAllowPickpocket()`

### `ClearAvoidsRoads()`

### `ClearCantOpenDoors()`

### `ClearNoCombatInWater()`

### `ClearNoKNockdowns()`

### `ClearNoShadow()`

### `ClearRaceFlag(n)`

**Flags:** Native

clears the specified race flag

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `n` | `Int` | ✓ |  |

### `GetDefaultVoiceType(female) → VoiceType`

**Flags:** Native

Returns the races default voice type

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `female` | `Bool` | ✓ |  |

### `GetNthSpell(n) → Spell`

**Flags:** Native

returns the specified spell from the race

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `n` | `Int` | ✓ |  |

### `GetSkin() → Armor`

**Flags:** Native

Gets/sets the skin of the race

### `GetSpellCount() → Int`

**Flags:** Native

SKSE64 additions built 2024-01-17 20:01:40.731000 UTC
returns the number of spells for the race

### `IsChildRace() → Bool`

### `IsImmobile() → Bool`

### `IsNotPushable() → Bool`

### `IsPlayable() → Bool`

### `IsRaceFlagSet(n) → Bool`

**Flags:** Native

returns whether the specified race flag is set

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `n` | `Int` | ✓ |  |

### `MakeCanFly()`

### `MakeCanSwim()`

### `MakeCanWalk()`

### `MakeChildRace()`

### `MakeImmobile()`

### `MakeMobile()`

### `MakeNoKnockdowns()`

### `MakeNonChildRace()`

### `MakeNonFlying()`

### `MakeNonSwimming()`

### `MakeNonWalking()`

### `MakeNotPushable()`

### `MakePlayable()`

### `MakePushable()`

### `MakeUnplayable()`

### `NoCombatInWater() → Bool`

### `NoKnockdowns() → Bool`

### `NoShadow() → Bool`

### `SetAllowPCDialogue()`

### `SetAllowPickpocket()`

### `SetAvoidsRoads()`

### `SetCantOpenDoors()`

### `SetDefaultVoiceType(female, voice)`

**Flags:** Native

Sets the races default voice type

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `female` | `Bool` | ✓ |  |
| `voice` | `VoiceType` | ✓ |  |

### `SetNoCombatInWater()`

### `SetNoShadow()`

### `SetRaceFlag(n)`

**Flags:** Native

sets the specified race flag

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `n` | `Int` | ✓ |  |

### `SetSkin(skin)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `skin` | `Armor` | ✓ |  |
