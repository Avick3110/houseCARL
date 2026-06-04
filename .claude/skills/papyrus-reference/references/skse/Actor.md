# `Actor`

**Source:** `skse` (Skyrim Script Extender x64) • **Extends:** `ObjectReference` • **Flags:** Hidden

---

## Properties

### `CritStage_DisintegrateEnd: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `4`

### `CritStage_DisintegrateStart: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `3`

### `CritStage_GooEnd: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `2`

### `CritStage_GooStart: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `1`

### `CritStage_None: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0`

### `EquipSlot_Default: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `0`

### `EquipSlot_LeftHand: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `2`

### `EquipSlot_RightHand: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `1`

---

## Events

### `OnCombatStateChanged(akTarget, aeCombatState)`

**Kind:** Event

Event that is triggered when this actor's combat state against the target changes
State is as follows:
0 - not in combat
1 - in combat
2 - searching

**Parameters**

| Name | Type |
|---|---|
| `akTarget` | `Actor` |
| `aeCombatState` | `Int` |

### `OnDeath(akKiller)`

**Kind:** Event

Event that is triggered when this actor finishes dying

**Parameters**

| Name | Type |
|---|---|
| `akKiller` | `Actor` |

### `OnDying(akKiller)`

**Kind:** Event

Event that is triggered when this actor begins to die

**Parameters**

| Name | Type |
|---|---|
| `akKiller` | `Actor` |

### `OnEnterBleedout()`

**Kind:** Event

Event received when an actor enters bleedout.

### `OnGetUp(akFurniture)`

**Kind:** Event

Event that is triggered when this actor leaves the furniture

**Parameters**

| Name | Type |
|---|---|
| `akFurniture` | `ObjectReference` |

### `OnLocationChange(akOldLoc, akNewLoc)`

**Kind:** Event

Event that is triggered when this actor changes from one location to another

**Parameters**

| Name | Type |
|---|---|
| `akOldLoc` | `Location` |
| `akNewLoc` | `Location` |

### `OnLycanthropyStateChanged(abIsWerewolf)`

**Kind:** Event

Received when the lycanthropy state of this actor changes (when SendLycanthropyStateChanged is called)

**Parameters**

| Name | Type |
|---|---|
| `abIsWerewolf` | `Bool` |

### `OnObjectEquipped(akBaseObject, akReference)`

**Kind:** Event

Event received when this actor equips something - akReference may be None if object is not persistent

**Parameters**

| Name | Type |
|---|---|
| `akBaseObject` | `Form` |
| `akReference` | `ObjectReference` |

### `OnObjectUnequipped(akBaseObject, akReference)`

**Kind:** Event

Event received when this actor unequips something - akReference may be None if object is not persistent

**Parameters**

| Name | Type |
|---|---|
| `akBaseObject` | `Form` |
| `akReference` | `ObjectReference` |

### `OnPackageChange(akOldPackage)`

**Kind:** Event

Event received when this actor's package changes

**Parameters**

| Name | Type |
|---|---|
| `akOldPackage` | `Package` |

### `OnPackageEnd(akOldPackage)`

**Kind:** Event

Event received when this actor's package ends

**Parameters**

| Name | Type |
|---|---|
| `akOldPackage` | `Package` |

### `OnPackageStart(akNewPackage)`

**Kind:** Event

Event received when this actor starts a new package

**Parameters**

| Name | Type |
|---|---|
| `akNewPackage` | `Package` |

### `OnPlayerBowShot(akWeapon, akAmmo, afPower, abSunGazing)`

**Kind:** Event

Received when the player fires a bow. akWeapon will be a bow, akAmmo is the ammo or None,
afPower will be 1.0 for a full-power shot, less for a dud, and abSunGazing will be true if the player is looking at the sun.

**Parameters**

| Name | Type |
|---|---|
| `akWeapon` | `Weapon` |
| `akAmmo` | `Ammo` |
| `afPower` | `Float` |
| `abSunGazing` | `Bool` |

### `OnPlayerFastTravelEnd(afTravelGameTimeHours)`

**Kind:** Event

Received when the player finishes fast travel, gives the duration of game time the travel took

**Parameters**

| Name | Type |
|---|---|
| `afTravelGameTimeHours` | `Float` |

### `OnPlayerLoadGame()`

**Kind:** Event

Received immediately after the player has loaded a save game. A good time to check for additional content.

### `OnRaceSwitchComplete()`

**Kind:** Event

Event received when this actor finishes changing its race

### `OnSit(akFurniture)`

**Kind:** Event

Event that is triggered when this actor sits in the furniture

**Parameters**

| Name | Type |
|---|---|
| `akFurniture` | `ObjectReference` |

### `OnVampireFeed(akTarget)`

**Kind:** Event

Received when StartVampireFeed is called on an actor

**Parameters**

| Name | Type |
|---|---|
| `akTarget` | `Actor` |

### `OnVampirismStateChanged(abIsVampire)`

**Kind:** Event

Received when the vampirism state of this actor changes (when SendVampirismStateChanged is called)

**Parameters**

| Name | Type |
|---|---|
| `abIsVampire` | `Bool` |

---

## Functions

### `AddPerk(akPerk)`

**Flags:** Native

Adds the specified perk to this actor

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akPerk` | `Perk` | ✓ |  |

### `AddShout(akShout) → Bool`

**Flags:** Native

Adds the specified shout to this actor - returns true on success

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akShout` | `Shout` | ✓ |  |

### `AddSpell(akSpell, abVerbose) → Bool`

**Flags:** Native

Adds the specified spell to this actor - returns true on success

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSpell` | `Spell` | ✓ |  |
| `abVerbose` | `Bool` |  | `true` |

### `AddToFaction(akFaction)`

Adds this actor to a faction at rank 0 if they aren't already in it

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFaction` | `Faction` | ✓ |  |

### `AllowBleedoutDialogue(abCanTalk)`

**Flags:** Native

Sets this a essential actors ability to talk when in a bleedout state

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abCanTalk` | `Bool` | ✓ |  |

### `AllowPCDialogue(abTalk)`

**Flags:** Native

overrides the race flag on an actor and determines if he can talk to the player in dialogue menu

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abTalk` | `Bool` | ✓ |  |

### `AttachAshPile(akAshPileBase)`

**Flags:** Native

Attaches an "ash pile" to this actor, placing it at this actor's location and using the specified
base object (or leveled item list) to represent the pile. If None is passed, it will use the
default ash pile object

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAshPileBase` | `Form` |  |  |

### `CanFlyHere() → Bool`

**Flags:** Native

Can this actor fly here?

### `ChangeHeadPart(hPart)`

**Flags:** Native

Adds a headpart, if the type exists it will replace, must not be misc type
Beware: This function also affects the ActorBase

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `hPart` | `HeadPart` | ✓ |  |

### `ClearArrested()`

**Flags:** Native

Clears this actor's arrested state

### `ClearExpressionOverride()`

**Flags:** Native

Clears any expression override on the actor

### `ClearExtraArrows()`

**Flags:** Native

Clears this actor's extra arrows 3D

### `ClearForcedLandingMarker()`

Remove the obligation to use a particular marker when this actor has to land.

### `ClearForcedMovement()`

**Flags:** Native

Clears any forced movement on the actor and return it to its standard state

### `ClearKeepOffsetFromActor()`

**Flags:** Native

Clear any keep offset from actor settings

### `ClearLookAt()`

**Flags:** Native

Clears this actor's look at target

### `DamageActorValue(asValueName, afDamage)`

**Flags:** Native

Damages the specified actor value

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asValueName` | `String` | ✓ |  |
| `afDamage` | `Float` | ✓ |  |

### `DamageAV(asValueName, afDamage)`

Alias for DamageActorValue - damages the specified actor value

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asValueName` | `String` | ✓ |  |
| `afDamage` | `Float` | ✓ |  |

### `Dismount() → Bool`

**Flags:** Native

Initiates a dismount.

### `DispelAllSpells()`

**Flags:** Native

Dispel all spells from this actor

### `DispelSpell(akSpell) → Bool`

**Flags:** Native

Dispel a spell from this actor

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSpell` | `Spell` | ✓ |  |

### `DoCombatSpellApply(akSpell, akTarget)`

**Flags:** Native

Apply a spell to a target in combat

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSpell` | `Spell` | ✓ |  |
| `akTarget` | `ObjectReference` | ✓ |  |

### `DrawWeapon()`

**Flags:** Native

Makes this actor draw his weapon

### `EnableAI(abEnable)`

**Flags:** Native

Enables or disable's this actor's AI

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abEnable` | `Bool` |  | `true` |

### `EndDeferredKill()`

**Flags:** Native

End the Deferred Kill state. This must only be called if StartDeferredKill was called first.

### `EquipItem(akItem, abPreventRemoval, abSilent)`

**Flags:** Native

Forces this actor to equip the specified item, preventing removal if requested

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akItem` | `Form` | ✓ |  |
| `abPreventRemoval` | `Bool` |  | `false` |
| `abSilent` | `Bool` |  | `false` |

### `EquipItemById(item, itemId, equipSlot, preventUnequip, equipSound)`

**Flags:** Native

equips item with matching itemId at the given slot

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `item` | `Form` | ✓ |  |
| `itemId` | `Int` | ✓ |  |
| `equipSlot` | `Int` |  | `0` |
| `preventUnequip` | `Bool` |  | `false` |
| `equipSound` | `Bool` |  | `true` |

### `EquipItemEx(item, equipSlot, preventUnequip, equipSound)`

**Flags:** Native

equips item at the given slot

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `item` | `Form` | ✓ |  |
| `equipSlot` | `Int` |  | `0` |
| `preventUnequip` | `Bool` |  | `false` |
| `equipSound` | `Bool` |  | `true` |

### `EquipShout(akShout)`

**Flags:** Native

Forces this actor to equip the specified shout

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akShout` | `Shout` | ✓ |  |

### `EquipSpell(akSpell, aiSource)`

**Flags:** Native

Forces this actor to equip the specified spell. The casting source can be:
0 - Left hand
1 - Right hand

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSpell` | `Spell` | ✓ |  |
| `aiSource` | `Int` | ✓ |  |

### `EvaluatePackage()`

**Flags:** Native

Forces the AI to re-evaluate its package stack

### `ForceActorValue(asValueName, afNewValue)`

**Flags:** Native

Force the specified actor value to a specified value

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asValueName` | `String` | ✓ |  |
| `afNewValue` | `Float` | ✓ |  |

### `ForceAV(asValueName, afNewValue)`

Alias for ForceActorValue - force the specified actor value to a specified value

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asValueName` | `String` | ✓ |  |
| `afNewValue` | `Float` | ✓ |  |

### `ForceMovementDirection(afXAngle, afYAngle, afZAngle)`

**Flags:** Native

**** For Debugging Movement Animations (not in release builds) ****
Forces the movement direction on the actor
afXAngle, afYAngle and afZAngle are in degrees

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afXAngle` | `Float` |  | `0` |
| `afYAngle` | `Float` |  | `0` |
| `afZAngle` | `Float` |  | `0` |

### `ForceMovementDirectionRamp(afXAngle, afYAngle, afZAngle, afRampTime)`

**Flags:** Native

Ramps the movement direction on the actor to the passed in value over the passed in time
afXAngle, afYAngle and afZAngle are in degrees
afRampTime is in seconds

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afXAngle` | `Float` |  | `0` |
| `afYAngle` | `Float` |  | `0` |
| `afZAngle` | `Float` |  | `0` |
| `afRampTime` | `Float` |  | `0.1` |

### `ForceMovementRotationSpeed(afXMult, afYMult, afZMult)`

**Flags:** Native

Forces the movement rotation speed on the actor
Each component of the rotation speed is a multiplier following these rules:
- 0 -> 1 Scales between 0 and the Walk speed
- 1 -> 2 Scales between Walk speed and Run Speed
- 2 and above is a multiplier of the run speed (less 1.0 since Run is 2.0)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afXMult` | `Float` |  | `0` |
| `afYMult` | `Float` |  | `0` |
| `afZMult` | `Float` |  | `0` |

### `ForceMovementRotationSpeedRamp(afXMult, afYMult, afZMult, afRampTime)`

**Flags:** Native

Ramps the movement rotation speed on the actor to the passed in value over the passed in time
Each component of the rotation speed is a multiplier following these rules:
- 0 -> 1 Scales between 0 and the Walk speed
- 1 -> 2 Scales between Walk speed and Run Speed
- 2 and above is a multiplier of the run speed (less 1.0 since Run is 2.0)
afRampTime is in seconds

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afXMult` | `Float` |  | `0` |
| `afYMult` | `Float` |  | `0` |
| `afZMult` | `Float` |  | `0` |
| `afRampTime` | `Float` |  | `0.1` |

### `ForceMovementSpeed(afSpeedMult)`

**Flags:** Native

Forces the movement speed on the actor
afSpeedMult is a speed multiplier based on the current max speeds
- 0 -> 1 Scales between 0 and the Walk speed
- 1 -> 2 Scales between Walk speed and Run Speed
- 2 and above is a multiplier of the run speed (less 1.0 since Run is 2.0)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afSpeedMult` | `Float` | ✓ |  |

### `ForceMovementSpeedRamp(afSpeedMult, afRampTime)`

**Flags:** Native

Ramps the movement speed on the actor to the passed in value over the passed in time
afSpeedMult is a speed multiplier based on the current max speeds
- 0 -> 1 Scales between 0 and the Walk speed
- 1 -> 2 Scales between Walk speed and Run Speed
- 2 and above is a multiplier of the run speed (less 1.0 since Run is 2.0)
afRampTime is in seconds

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afSpeedMult` | `Float` | ✓ |  |
| `afRampTime` | `Float` |  | `0.1` |

### `ForceTargetAngle(afXAngle, afYAngle, afZAngle)`

**Flags:** Native

Sets the target facing angle on the actor
afXAngle, afYAngle and afZAngle are in degrees

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afXAngle` | `Float` |  | `0` |
| `afYAngle` | `Float` |  | `0` |
| `afZAngle` | `Float` |  | `0` |

### `ForceTargetDirection(afXAngle, afYAngle, afZAngle)`

**Flags:** Native

Sets the target movement direction on the actor
afXAngle, afYAngle and afZAngle are in degrees

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afXAngle` | `Float` |  | `0` |
| `afYAngle` | `Float` |  | `0` |
| `afZAngle` | `Float` |  | `0` |

### `ForceTargetSpeed(afSpeed)`

**Flags:** Native

Sets the target movement speed on the actor
afSpeedMult is a speed multiplier based on the current max speeds
- 0 -> 1 Scales between 0 and the Walk speed
- 1 -> 2 Scales between Walk speed and Run Speed
- 2 and above is a multiplier of the run speed (less 1.0 since Run is 2.0)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afSpeed` | `Float` | ✓ |  |

### `GetActorBase() → ActorBase`

returns the ActorBase

### `GetActorValue(asValueName) → Float`

**Flags:** Native

Gets the specified actor value - returns 0 and logs an error if the value is unknown

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asValueName` | `String` | ✓ |  |

### `GetActorValueMax(asValueName) → Float`

**Flags:** Native

Gets the specified actor value's max, taking into account buffs/debuffs

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asValueName` | `String` | ✓ |  |

### `GetActorValuePercentage(asValueName) → Float`

**Flags:** Native

Gets the specified actor value as a percentage of its max value - from 0 to 1

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asValueName` | `String` | ✓ |  |

### `GetAV(asValueName) → Float`

Alias for GetActorValue - retrives the specified actor value

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asValueName` | `String` | ✓ |  |

### `GetAVMax(asValueName) → Float`

Alias of GetActorValueMax - retrives actor value's max, taking into account buffs/debuffs

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asValueName` | `String` | ✓ |  |

### `GetAVPercentage(asValueName) → Float`

Alias for GetActorValuePercentage - gets the actor value as a percent of max

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asValueName` | `String` | ✓ |  |

### `GetBaseActorValue(asValueName) → Float`

**Flags:** Native

Gets the base value of the specified actor value - returns 0 and logs an error if the value is unknown

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asValueName` | `String` | ✓ |  |

### `GetBaseAV(asValueName) → Float`

Alias for GetBaseActorValue - retrieves the specified actor value's base value

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asValueName` | `String` | ✓ |  |

### `GetBribeAmount() → Int`

**Flags:** Native

Obtains how much it would cost to bribe this actor

### `GetCombatState() → Int`

**Flags:** Native

Gets this actor's current combat state

### `GetCombatTarget() → Actor`

**Flags:** Native

Gets this actor's current combat target

### `GetCrimeFaction() → Faction`

**Flags:** Native

Get the faction this actor reports crimes to

### `GetCurrentPackage() → Package`

**Flags:** Native

Gets this actor's current AI package

### `GetDialogueTarget() → Actor`

**Flags:** Native

Gets this actor's current dialogue target

### `GetEquippedArmorInSlot(aiSlot) → Armor`

**Flags:** Native

Obtain the armor currently equipped in the specified slot

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiSlot` | `Int` | ✓ |  |

### `GetEquippedItemId(location) → Int`

**Flags:** Native

returns the itemId of the object currently equipped in the specified hand
0 - left hand
1 - right hand

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `location` | `Int` | ✓ |  |

### `GetEquippedItemType(aiHand) → Int`

**Flags:** Native

Obtains the item quipped in the specified hand (0 - Left hand, 1 - Right hand)
Return values are:
-1 - Error
0 - Nothing
1 - One-handed sword
2 - One-handed dagger
3 - One-handed axe
4 - One-handed mace
5 - Two-handed sword
6 - Two-handed axe
7 - Bow
8 - Staff
9 - Magic spell
10 - Shield
11 - Torch

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiHand` | `Int` | ✓ |  |

### `GetEquippedObject(location) → Form`

**Flags:** Native

returns the object currently equipped in the specified location
0 - left hand
1 - right hand
2 - shout

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `location` | `Int` | ✓ |  |

### `GetEquippedShield() → Armor`

**Flags:** Native

Gets this actor's currently equipped shield

### `GetEquippedShout() → Shout`

**Flags:** Native

Gets this actor's currently equipped shout

### `GetEquippedSpell(aiSource) → Spell`

**Flags:** Native

Gets the spell currently equipped in the specified source
0 - Left Hand
1 - Right Hand
2 - Other
3 - Instant

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiSource` | `Int` | ✓ |  |

### `GetEquippedWeapon(abLeftHand) → Weapon`

**Flags:** Native

Gets this actor's currently equipped weapon
false - Default - Right Hand
true - Left Hand

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abLeftHand` | `Bool` |  | `false` |

### `GetFactionRank(akFaction) → Int`

**Flags:** Native

Obtains this actor's rank with the specified faction - returns -1 if the actor is not a member

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFaction` | `Faction` | ✓ |  |

### `GetFactionReaction(akOther) → Int`

**Flags:** Native

Obtains this actor's faction-based reaction to the other actor
0 - Neutral
1 - Enemy
2 - Ally
3 - Friend

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akOther` | `Actor` | ✓ |  |

### `GetFactions(minRank, maxRank) → Faction[]`

**Flags:** Native

Returns all factions with the specified min and max ranks (-128 to 127)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `minRank` | `Int` | ✓ |  |
| `maxRank` | `Int` | ✓ |  |

### `GetFlyingState() → Int`

**Flags:** Native

Obtains this actor's current flight state
0 - Not flying
1 - Taking off
2 - Cruising
3 - Hovering
4 - Landing

### `GetForcedLandingMarker() → ObjectReference`

**Flags:** Native

Get the ref at which this actor is obliged to land, if one is set (or none, if not).

### `GetFurnitureReference() → ObjectReference`

**Flags:** Native

Returns the reference of the furniture the actor is currently using

### `GetGoldAmount() → Int`

**Flags:** Native

Retrieves the amount of gold this actor has

### `GetHighestRelationshipRank() → Int`

**Flags:** Native

Gets this actor's highest relationship rank - returns 0 if they have no relationships

### `GetKiller() → Actor`

**Flags:** Native

Returns this actor's killer - or None if this actor is still alive

### `GetLevel() → Int`

**Flags:** Native

Returns this actor's current level.

### `GetLeveledActorBase() → ActorBase`

**Flags:** Native

Obtains a leveled actor's "fake" base (the one generated by the game when the
actor is leveled. This differs from GetActorBase which will return the editor base
object)

### `GetLightLevel() → Float`

**Flags:** Native

Returns this actor's current light level.

### `GetLowestRelationshipRank() → Int`

**Flags:** Native

Gets this actor's highest relationship rank - returns 0 if they have no relationships

### `GetNoBleedoutRecovery() → Bool`

**Flags:** Native

Queries whether this actor has no bleedout recovery flag set.

### `GetNthSpell(n) → Spell`

**Flags:** Native

returns the specified added spell for the actor

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `n` | `Int` | ✓ |  |

### `GetPlayerControls() → Bool`

**Flags:** Native

Queries whether this actor receives player input

### `GetRace() → Race`

**Flags:** Native

Returns this actor's race

### `GetRelationshipRank(akOther) → Int`

**Flags:** Native

Obtains the relationship rank between this actor and another

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akOther` | `Actor` | ✓ |  |

### `GetSitState() → Int`

**Flags:** Native

Obtains this actor's sit state, which is one of the following:
0 - Not sitting
2 - Not sitting, wants to sit
3 - Sitting
4 - Sitting, wants to stand

### `GetSleepState() → Int`

**Flags:** Native

Obtains this actor's sleep state, which is one of the following:
0 - Not sleeping
2 - Not sleeping, wants to sleep
3 - Sleeping
4 - Sleeping, wants to wake

### `GetSpellCount() → Int`

**Flags:** Native

returns the number of added spells for the actor

### `GetVoiceRecoveryTime() → Float`

**Flags:** Native

Gets the voice recovery timer from the actor

### `GetWarmthRating() → Float`

**Flags:** Native

Gets the total "warmth rating" for this actor

### `GetWornForm(slotMask) → Form`

**Flags:** Native

SKSE64 additions built 2024-01-17 20:01:40.731000 UTC
returns the form for the item worn at the specified slotMask
use Armor.GetMaskForSlot() to generate appropriate slotMask

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `slotMask` | `Int` | ✓ |  |

### `GetWornItemId(slotMask) → Int`

**Flags:** Native

returns the itemId for the item worn at the specified slotMask

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `slotMask` | `Int` | ✓ |  |

### `HasAssociation(akAssociation, akOther) → Bool`

**Flags:** Native

Checks to see if this actor has the specified association with the other actor - or anyone (if no actor is passed)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAssociation` | `AssociationType` | ✓ |  |
| `akOther` | `Actor` |  |  |

### `HasFamilyRelationship(akOther) → Bool`

**Flags:** Native

Checks to see if this actor has a family relationship with the other actor - or anyone (if no actor is passed)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akOther` | `Actor` |  |  |

### `HasLOS(akOther) → Bool`

**Flags:** Native

Sees if this actor has line-of-sight to another object. Only the player can check LOS to a non-actor

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akOther` | `ObjectReference` | ✓ |  |

### `HasMagicEffect(akEffect) → Bool`

**Flags:** Native

Checks to see if this actor is currently being affected by the given Magic Effect

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEffect` | `MagicEffect` | ✓ |  |

### `HasMagicEffectWithKeyword(akKeyword) → Bool`

**Flags:** Native

Checks to see if this actor is currently being affected by a Magic Effect with the given Keyword

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akKeyword` | `Keyword` | ✓ |  |

### `HasParentRelationship(akOther) → Bool`

**Flags:** Native

Checks to see if this actor has a parent relationship with the other actor

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akOther` | `Actor` | ✓ |  |

### `HasPerk(akPerk) → Bool`

**Flags:** Native

Checks to see if this actor has the given Perk

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akPerk` | `Perk` | ✓ |  |

### `HasSpell(akForm) → Bool`

**Flags:** Native

Checks to see if this actor has the given Spell or Shout

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `IsAIEnabled() → Bool`

**Flags:** Native

Returns whether the actors AI is enabled

### `IsAlarmed() → Bool`

**Flags:** Native

Returns if this actor is alarmed or not

### `IsAlerted() → Bool`

**Flags:** Native

Returns if this actor is alerted or not

### `IsAllowedToFly() → Bool`

**Flags:** Native

Is this actor allowed to fly?

### `IsArrested() → Bool`

**Flags:** Native

Is this actor currently arrested?

### `IsArrestingTarget() → Bool`

**Flags:** Native

Is this actor currently arresting his target? (Must be a guard and alarmed)

### `IsBeingRidden() → Bool`

**Flags:** Native

Is the actor being ridden?

### `IsBleedingOut() → Bool`

**Flags:** Native

Is this actor currently bleeding out?

### `IsBribed() → Bool`

**Flags:** Native

Queries whether this actor has player bribe flag set.

### `IsChild() → Bool`

**Flags:** Native

Is this actor a child?

### `IsCommandedActor() → Bool`

**Flags:** Native

Is this actor a commanded by another?

### `IsDead() → Bool`

**Flags:** Native

Returns if this actor is dead or not

### `IsDetectedBy(akOther) → Bool`

**Flags:** Native

Returns if this actor is detected by the other one

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akOther` | `Actor` | ✓ |  |

### `IsDoingFavor() → Bool`

**Flags:** Native

Is this actor doing a favor for the player?

### `IsEquipped(akItem) → Bool`

**Flags:** Native

Returns if the specified object is equipped on this actor

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akItem` | `Form` | ✓ |  |

### `IsEssential() → Bool`

**Flags:** Native

Is this actor essential?

### `IsFlying() → Bool`

**Flags:** Native

Returns if this actor is flying or not

### `IsGhost() → Bool`

**Flags:** Native

Is this actor flagged as a ghost?

### `IsGuard() → Bool`

**Flags:** Native

Returns if this actor is a guard or not

### `IsHostileToActor(akActor) → Bool`

**Flags:** Native

Is this actor hostile to another actor?

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `IsInCombat() → Bool`

**Flags:** Native

Returns if this actor is currently in combat

### `IsInFaction(akFaction) → Bool`

**Flags:** Native

Checks to see if this actor is a member of the specified faction

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFaction` | `Faction` | ✓ |  |

### `IsInKillMove() → Bool`

**Flags:** Native

Returns if this actor is in a kill move or not

### `IsIntimidated() → Bool`

**Flags:** Native

Queries whether this actor has player intimidated flag set.

### `IsOnMount() → Bool`

**Flags:** Native

Is the actor on a mount?

### `IsOverEncumbered() → Bool`

**Flags:** Native

Is the actor over-encumbered?

### `IsPlayersLastRiddenHorse() → Bool`

**Flags:** Native

Checks to see if this actor the last ridden horse of the player

### `IsPlayerTeammate() → Bool`

**Flags:** Native

Is this actor currently a teammate of the player?

### `IsRunning() → Bool`

**Flags:** Native

Is this actor currently running?

### `IsSneaking() → Bool`

**Flags:** Native

Is this actor currently sneaking?

### `IsSprinting() → Bool`

**Flags:** Native

Is this actor currently sprinting?

### `IsSwimming() → Bool`

**Flags:** Native

Returns whether the actor is currently swimming

### `IsTrespassing() → Bool`

**Flags:** Native

Is this actor trespassing?

### `IsUnconscious() → Bool`

**Flags:** Native

Is this actor unconscious?

### `IsWeaponDrawn() → Bool`

**Flags:** Native

Does this actor have his weapon and/or magic drawn?

### `KeepOffsetFromActor(arTarget, afOffsetX, afOffsetY, afOffsetZ, afOffsetAngleX, afOffsetAngleY, afOffsetAngleZ, afCatchUpRadius, afFollowRadius)`

**Flags:** Native

Sets the actor to a mode where it will keep a given offset from another actor

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arTarget` | `Actor` | ✓ |  |
| `afOffsetX` | `Float` | ✓ |  |
| `afOffsetY` | `Float` | ✓ |  |
| `afOffsetZ` | `Float` | ✓ |  |
| `afOffsetAngleX` | `Float` |  | `0` |
| `afOffsetAngleY` | `Float` |  | `0` |
| `afOffsetAngleZ` | `Float` |  | `0` |
| `afCatchUpRadius` | `Float` |  | `20` |
| `afFollowRadius` | `Float` |  | `5` |

### `Kill(akKiller)`

**Flags:** Native

Kills this actor with the killer being the guilty party

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akKiller` | `Actor` |  |  |

### `KillEssential(akKiller)`

Kills this actor even if essential

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akKiller` | `Actor` |  |  |

### `KillSilent(akKiller)`

**Flags:** Native

Kills this actor without a kill event with the killer being the guilty party

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akKiller` | `Actor` |  |  |

### `MakePlayerFriend()`

this function will make an actor a friend of the player if allowed

### `ModActorValue(asValueName, afAmount)`

**Flags:** Native

Modifies the specified actor value

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asValueName` | `String` | ✓ |  |
| `afAmount` | `Float` | ✓ |  |

### `ModAV(asValueName, afAmount)`

Alias for ModActorValue - modifies the specified actor value

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asValueName` | `String` | ✓ |  |
| `afAmount` | `Float` | ✓ |  |

### `ModFactionRank(akFaction, aiMod)`

**Flags:** Native

Modifies this actor's rank in the faction

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFaction` | `Faction` | ✓ |  |
| `aiMod` | `Int` | ✓ |  |

### `ModFavorPoints(iFavorPoints)`

DEPRECATED - use MakePlayerFriend() instead
replacement for ModFavorPoints
if iFavorPoints > 0, will setRelationshipRank to 1 if 0
otherwise, won't do anything

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `iFavorPoints` | `Int` |  | `1` |

### `ModFavorPointsWithGlobal(FavorPointsGlobal)`

also DEPRECATED

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FavorPointsGlobal` | `GlobalVariable` | ✓ |  |

### `MoveToPackageLocation()`

**Flags:** Native

Pop this actor to the initial location for a package. Mainly for use on
disabled actors, since they would normally start at their editor locations.

### `OpenInventory(abForceOpen)`

**Flags:** Native

Opens this actor's inventory, as if you were pick-pocketing them. Only works on teammates, or anyone if forced.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abForceOpen` | `Bool` |  | `false` |

### `PathToReference(aTarget, afWalkRunPercent) → Bool`

**Flags:** Native

Make the actor path to a reference, latent version
Note: this method doesn't return until the goal is reached or pathing
failed or was interrupted (by another request for instance)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aTarget` | `ObjectReference` | ✓ |  |
| `afWalkRunPercent` | `Float` | ✓ |  |

### `PlayIdle(akIdle) → Bool`

**Flags:** Native

Send an idle to the actor to load in and play.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akIdle` | `Idle` | ✓ |  |

### `PlayIdleWithTarget(akIdle, akTarget) → Bool`

**Flags:** Native

Send an idle to the actor to play, overriding its target with the specified reference

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akIdle` | `Idle` | ✓ |  |
| `akTarget` | `ObjectReference` | ✓ |  |

### `PlaySubGraphAnimation(asEventName)`

**Flags:** Native

Send an event to the subgraphs of an actor.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asEventName` | `String` | ✓ |  |

### `QueueNiNodeUpdate()`

**Flags:** Native

Updates an Actors meshes (Used for Armor mesh/texture changes and face changes)
DO NOT USE WHILE MOUNTED

### `RegenerateHead()`

**Flags:** Native

Updates an Actors head mesh

### `RemoveFromAllFactions()`

**Flags:** Native

Removes this actor from all factions

### `RemoveFromFaction(akFaction)`

**Flags:** Native

Removes this actor from the specified faction

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFaction` | `Faction` | ✓ |  |

### `RemovePerk(akPerk)`

**Flags:** Native

Removes the specified perk from this actor

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akPerk` | `Perk` | ✓ |  |

### `RemoveShout(akShout) → Bool`

**Flags:** Native

Removes the specified shout from this actor - returns true on success

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akShout` | `Shout` | ✓ |  |

### `RemoveSpell(akSpell) → Bool`

**Flags:** Native

Removes the specified spell from this actor - returns true on success

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSpell` | `Spell` | ✓ |  |

### `ReplaceHeadPart(oPart, newPart)`

**Flags:** Native

Replaces a headpart on the loaded mesh does not affect ActorBase
Both old and new must exist, and be of the same type

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `oPart` | `HeadPart` | ✓ |  |
| `newPart` | `HeadPart` | ✓ |  |

### `ResetAI()`

**Flags:** Native

Resets Actor AI

### `ResetExpressionOverrides()`

**Flags:** Native

Resets all expression, phoneme, and modifiers

### `ResetHealthAndLimbs()`

**Flags:** Native

Resets this actor's health and limb state

### `RestoreActorValue(asValueName, afAmount)`

**Flags:** Native

Restores damage done to the actor value (up to 0 damage)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asValueName` | `String` | ✓ |  |
| `afAmount` | `Float` | ✓ |  |

### `RestoreAV(asValueName, afAmount)`

Alias for RestoreActorValue - restores damage done to the actor value

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asValueName` | `String` | ✓ |  |
| `afAmount` | `Float` | ✓ |  |

### `Resurrect()`

**Flags:** Native

Resurrects this actor

### `SendAssaultAlarm()`

**Flags:** Native

Has this actor behave as if assaulted

### `SendLycanthropyStateChanged(abIsWerewolf)`

**Flags:** Native

Tell anyone who cares that the lycanthropy state of this actor has changed

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abIsWerewolf` | `Bool` | ✓ |  |

### `SendTrespassAlarm(akCriminal)`

**Flags:** Native

Has this actor behave as if they caught the target trespassing

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akCriminal` | `Actor` | ✓ |  |

### `SendVampirismStateChanged(abIsVampire)`

**Flags:** Native

Tell anyone who cares that the vampirism state of this actor has changed

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abIsVampire` | `Bool` | ✓ |  |

### `SetActorValue(asValueName, afValue)`

**Flags:** Native

Sets the specified actor value

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asValueName` | `String` | ✓ |  |
| `afValue` | `Float` | ✓ |  |

### `SetAlert(abAlerted)`

**Flags:** Native

Sets the actor in an alerted state

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abAlerted` | `Bool` |  | `true` |

### `SetAllowFlying(abAllowed)`

**Flags:** Native

Sets whether this actor is allowed to fly or not - if not, will land the actor

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abAllowed` | `Bool` |  | `true` |

### `SetAllowFlyingEx(abAllowed, abAllowCrash, abAllowSearch)`

**Flags:** Native

Sets whether this actor is allowed to fly or not - if not, will land the actor

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abAllowed` | `Bool` |  | `true` |
| `abAllowCrash` | `Bool` |  | `true` |
| `abAllowSearch` | `Bool` |  | `false` |

### `SetAlpha(afTargetAlpha, abFade)`

**Flags:** Native

Sets this actor's alpha - with an optional fade to that alpha
The alpha will be clamped between 0 and 1

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afTargetAlpha` | `Float` | ✓ |  |
| `abFade` | `Bool` |  | `false` |

### `SetAttackActorOnSight(abAttackOnSight)`

**Flags:** Native

Sets this actor to be attacked by all other actors on sight

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abAttackOnSight` | `Bool` |  | `true` |

### `SetAV(asValueName, afValue)`

Alias for SetActorValue - sets the specified actor value

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asValueName` | `String` | ✓ |  |
| `afValue` | `Float` | ✓ |  |

### `SetBribed(abBribe)`

**Flags:** Native

Flags/unflags this actor as bribed by the player

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abBribe` | `Bool` |  | `true` |

### `SetCrimeFaction(akFaction)`

**Flags:** Native

Sets the faction this actor reports crimes to

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFaction` | `Faction` | ✓ |  |

### `SetCriticalStage(aiStage)`

**Flags:** Native

Sets this actor's critical stage, which is one of the following (properties below also match this)
0 - None
1 - Goo start
2 - Goo end
3 - Disintegrate start
4 - Disintegrate end

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiStage` | `Int` | ✓ |  |

### `SetDoingFavor(abDoingFavor)`

**Flags:** Native

Flag this actor as currently doing a favor for the player

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abDoingFavor` | `Bool` |  | `true` |

### `SetDontMove(abDontMove)`

**Flags:** Native

Sets this actor as "don't move" or not

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abDontMove` | `Bool` |  | `true` |

### `SetExpressionModifier(index, value)`

**Flags:** Native

0 - "BlinkLeft"
1 - "BlinkRight"
2 - "BrowDownLeft"
3 - "BrowDownRight"
4 - "BrowInLeft"
5 - "BrowInRight"
6 - "BrowUpLeft"
7 - "BrowUpRight"
8 - "LookDown"
9 - "LookLeft"
10 - "LookRight"
11 - "LookUp"
12 - "SquintLeft"
13 - "SquintRight"
14 - "HeadPitch"
15 - "HeadRoll"
16 - "HeadYaw"

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |
| `value` | `Float` | ✓ |  |

### `SetExpressionOverride(aiMood, aiStrength)`

**Flags:** Native

Sets an expression to override any other expression other systems may give this actor.
						7 - Mood Neutral
0 - Dialogue Anger		8 - Mood Anger		15 - Combat Anger
1 - Dialogue Fear			9 - Mood Fear		16 - Combat Shout
2 - Dialogue Happy		10 - Mood Happy
3 - Dialogue Sad			11 - Mood Sad
4 - Dialogue Surprise		12 - Mood Surprise
5 - Dialogue Puzzled		13 - Mood Puzzled
6 - Dialogue Disgusted	14 - Mood Disgusted
aiStrength is from 0 to 100 (percent)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiMood` | `Int` | ✓ |  |
| `aiStrength` | `Int` |  | `100` |

### `SetExpressionPhoneme(index, value)`

**Flags:** Native

0 - "Aah"
1 - "BigAah"
2 - "BMP"
3 - "ChJSh"
4 - "DST"
5 - "Eee"
6 - "Eh"
7 - "FV"
8 - "I"
9 - "K"
10 - "N"
11 - "Oh"
12 - "OohQ"
13 - "R"
14 - "Th"
15 - "W"

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |
| `value` | `Float` | ✓ |  |

### `SetEyeTexture(akNewTexture)`

**Flags:** Native

forces the eye texture for this actor to the give texture set

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akNewTexture` | `TextureSet` | ✓ |  |

### `SetFactionRank(akFaction, aiRank)`

**Flags:** Native

Sets this actor's rank with the specified faction

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFaction` | `Faction` | ✓ |  |
| `aiRank` | `Int` | ✓ |  |

### `SetForcedLandingMarker(aMarker)`

**Flags:** Native

Set a specific marker as the place at which this actor must land from flight.
params:
- aMarker:  The ObjectReference to set as this actor's landing marker

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aMarker` | `ObjectReference` | ✓ |  |

### `SetGhost(abIsGhost)`

**Flags:** Native

Flags/unflags this actor as a ghost

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abIsGhost` | `Bool` |  | `true` |

### `SetHeadTracking(abEnable)`

**Flags:** Native

Turns on/off headtracking on this actor

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abEnable` | `Bool` |  | `true` |

### `SetIntimidated(abIntimidate)`

**Flags:** Native

Flags/unflags this actor as intimidated by the player

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abIntimidate` | `Bool` |  | `true` |

### `SetLookAt(akTarget, abPathingLookAt)`

**Flags:** Native

Sets this actor's head tracking target, optionally forcing it as their pathing look-at target

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akTarget` | `ObjectReference` | ✓ |  |
| `abPathingLookAt` | `Bool` |  | `false` |

### `SetNoBleedoutRecovery(abAllowed)`

**Flags:** Native

Set the no bleedout recovery flag on this actor

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abAllowed` | `Bool` | ✓ |  |

### `SetNotShowOnStealthMeter(abNotShow)`

**Flags:** Native

Sets this actor to not effect the detection level on the stealth meter if he is not hostile to the player

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abNotShow` | `Bool` | ✓ |  |

### `SetOutfit(akOutfit, abSleepOutfit)`

**Flags:** Native

Sets the actors outfit and makes him wear it

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akOutfit` | `Outfit` | ✓ |  |
| `abSleepOutfit` | `Bool` |  | `false` |

### `SetPlayerControls(abControls)`

**Flags:** Native

Set/reset whether player input being sent to the actor

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abControls` | `Bool` | ✓ |  |

### `SetPlayerResistingArrest()`

**Flags:** Native

Sets the player as resisting arrest from this actor's faction

### `SetPlayerTeammate(abTeammate, abCanDoFavor)`

**Flags:** Native

Sets or clears this actor as a teammate of the player
abCanDoFavor - OPTIONAL default is true the teammate can do favors

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abTeammate` | `Bool` |  | `true` |
| `abCanDoFavor` | `Bool` |  | `true` |

### `SetRace(akRace)`

**Flags:** Native

Sets the actors race
akRace - OPTIONAL (Def=None) New race for this actor. Default, no race, to switch back to the original race.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRace` | `Race` |  |  |

### `SetRelationshipRank(akOther, aiRank)`

**Flags:** Native

Sets the relationship rank between this actor and another (See GetRelationshipRank for the ranks)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akOther` | `Actor` | ✓ |  |
| `aiRank` | `Int` | ✓ |  |

### `SetRestrained(abRestrained)`

**Flags:** Native

Sets this actor as restrained or not

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abRestrained` | `Bool` |  | `true` |

### `SetSubGraphFloatVariable(asVariableName, afValue)`

**Flags:** Native

Set a variable on all of an actor's subgraphs

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asVariableName` | `String` | ✓ |  |
| `afValue` | `Float` | ✓ |  |

### `SetUnconscious(abUnconscious)`

**Flags:** Native

Sets this actor as unconscious or not

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abUnconscious` | `Bool` |  | `true` |

### `SetVehicle(akVehicle)`

**Flags:** Native

Attach the actor to (or detach it from) a horse, cart, or other vehicle.
akVehicle is the vehicle ref.  To detach the actor from its current vehicle, set akVehicle to None (or to the Actor itself).

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akVehicle` | `ObjectReference` | ✓ |  |

### `SetVoiceRecoveryTime(afTime)`

**Flags:** Native

Sets the voice recovery timer on the actor
afTime is recovery time in seconds

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afTime` | `Float` | ✓ |  |

### `SheatheWeapon()`

**Flags:** Native

Sheathes the actors weapon

### `ShowBarterMenu()`

**Flags:** Native

Opens the Barter menu

### `ShowGiftMenu(abGivingGift, apFilterList, abShowStolenItems, abUseFavorPoints) → Int`

**Flags:** Native

Opens the Gift menu
Params:
- abGivingGift: True if we're giving a gift to this Actor, false if the player is taking a gift from this Actor
- apFilterList: OPTIONAL (Def=None) -- If present, this form list is used to filter the item list.  Only items
that match keywords / items in the list will get shown
- abShowStolenItems: OPTIONAL (Def=false) -- If true, stolen items are shown
- abUseFavorPoints: OPTIONAL (Def=true) -- If true, favor points are added / subtracted with each transaction.  If false, FPs aren't used at all.
Returns: The number of favor points spent / gained while in the menu.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abGivingGift` | `Bool` | ✓ |  |
| `apFilterList` | `FormList` |  |  |
| `abShowStolenItems` | `Bool` |  | `false` |
| `abUseFavorPoints` | `Bool` |  | `true` |

### `StartCannibal(akTarget)`

**Flags:** Native

Starts Cannibal with the target

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akTarget` | `Actor` | ✓ |  |

### `StartCombat(akTarget)`

**Flags:** Native

Starts combat with the target

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akTarget` | `Actor` | ✓ |  |

### `StartDeferredKill()`

**Flags:** Native

Start the Deferred Kill state. Be sure to call EndDeferredKill or the actor will be invulnerable.

### `StartSneaking()`

**Flags:** Native

Makes this actor start sneaking

### `StartVampireFeed(akTarget)`

**Flags:** Native

Starts vampire feed with the target

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akTarget` | `Actor` | ✓ |  |

### `StopCombat()`

**Flags:** Native

Removes this actor from combat

### `StopCombatAlarm()`

**Flags:** Native

Stops all combat and alarms against this actor

### `TrapSoul(akTarget) → Bool`

**Flags:** Native

Returns whether the actor can trap the soul of the given actor.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akTarget` | `Actor` | ✓ |  |

### `UnequipAll()`

**Flags:** Native

Unequips the all items from this actor

### `UnequipItem(akItem, abPreventEquip, abSilent)`

**Flags:** Native

Unequips the specified item from this actor

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akItem` | `Form` | ✓ |  |
| `abPreventEquip` | `Bool` |  | `false` |
| `abSilent` | `Bool` |  | `false` |

### `UnequipItemEx(item, equipSlot, preventEquip)`

**Flags:** Native

unequips item at the given slot

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `item` | `Form` | ✓ |  |
| `equipSlot` | `Int` |  | `0` |
| `preventEquip` | `Bool` |  | `false` |

### `UnequipItemSlot(aiSlot)`

**Flags:** Native

Unequips the all items in this slot for the actor

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiSlot` | `Int` | ✓ |  |

### `UnequipShout(akShout)`

**Flags:** Native

Forces this actor to unequip the specified shout

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akShout` | `Shout` | ✓ |  |

### `UnequipSpell(akSpell, aiSource)`

**Flags:** Native

Forces this actor to unequip the specified spell. The casting source can be:
0 - Left hand
1 - Right hand

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSpell` | `Spell` | ✓ |  |
| `aiSource` | `Int` | ✓ |  |

### `UnLockOwnedDoorsInCell()`

**Flags:** Native

This actor will unlock all the doors that he qualifies for ownership in his current parentcell

### `UpdateWeight(neckDelta)`

**Flags:** Native

Visually updates the actors weight
neckDelta = (oldWeight / 100) - (newWeight / 100)
Neck changes are player persistent, but actor per-session
Weight itself is persistent either way so keep track of your
original weight if you use this for Actors other than the player
DO NOT USE WHILE MOUNTED

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `neckDelta` | `Float` | ✓ |  |

### `WillIntimidateSucceed() → Bool`

**Flags:** Native

Returns whether intimidate will succeed against this actor or not

### `WornHasKeyword(akKeyword) → Bool`

**Flags:** Native

Returns whether anything the actor is wearing has the specified keyword

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akKeyword` | `Keyword` | ✓ |  |
