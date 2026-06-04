# `ReferenceAlias`

**Source:** `vanilla` • **Extends:** `Alias` • **Flags:** Hidden

**Imports:** `ObjectReference`

---

## Events

### `OnActivate(akActionRef)`

**Kind:** Event

Event received when this reference is activated

**Parameters**

| Name | Type |
|---|---|
| `akActionRef` | `ObjectReference` |

### `OnAttachedToCell()`

**Kind:** Event

Event received when this object has moved to an attached cell from a detached one

### `OnCellAttach()`

**Kind:** Event

Event received when this object's parent cell is attached

### `OnCellDetach()`

**Kind:** Event

Event received when this object's parent cell is detached

### `OnCellLoad()`

**Kind:** Event

Event received when every object in this object's parent cell is loaded (TODO: Find restrictions)

### `OnClose(akActionRef)`

**Kind:** Event

Event received when this object is closed

**Parameters**

| Name | Type |
|---|---|
| `akActionRef` | `ObjectReference` |

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

### `OnContainerChanged(akNewContainer, akOldContainer)`

**Kind:** Event

Event received when this object enters, exits, or changes containers

**Parameters**

| Name | Type |
|---|---|
| `akNewContainer` | `ObjectReference` |
| `akOldContainer` | `ObjectReference` |

### `OnDeath(akKiller)`

**Kind:** Event

Event that is triggered when this actor finishes dying (only if this alias points at an actor)

**Parameters**

| Name | Type |
|---|---|
| `akKiller` | `Actor` |

### `OnDestructionStageChanged(aiOldStage, aiCurrentStage)`

**Kind:** Event

Event received when this reference's destruction stage has changed

**Parameters**

| Name | Type |
|---|---|
| `aiOldStage` | `Int` |
| `aiCurrentStage` | `Int` |

### `OnDetachedFromCell()`

**Kind:** Event

Event recieved when this object moves to a detached cell from an attached one

### `OnDying(akKiller)`

**Kind:** Event

Event that is triggered when this actor begins dying (only if this alias points at an actor)

**Parameters**

| Name | Type |
|---|---|
| `akKiller` | `Actor` |

### `OnEnterBleedout()`

**Kind:** Event

Event received when an actor enters bleedout. (only if this alias points at an actor)

### `OnEquipped(akActor)`

**Kind:** Event

Event received when this object is equipped by an actor

**Parameters**

| Name | Type |
|---|---|
| `akActor` | `Actor` |

### `OnGetUp(akFurniture)`

**Kind:** Event

Event that is triggered when this actor leaves the furniture

**Parameters**

| Name | Type |
|---|---|
| `akFurniture` | `ObjectReference` |

### `OnGrab()`

**Kind:** Event

Event received when this object is grabbed by the player

### `OnHit(akAggressor, akSource, akProjectile, abPowerAttack, abSneakAttack, abBashAttack, abHitBlocked)`

**Kind:** Event

Event received when this object is hit by a source (weapon, spell, explosion) or projectile attack

**Parameters**

| Name | Type |
|---|---|
| `akAggressor` | `ObjectReference` |
| `akSource` | `Form` |
| `akProjectile` | `Projectile` |
| `abPowerAttack` | `Bool` |
| `abSneakAttack` | `Bool` |
| `abBashAttack` | `Bool` |
| `abHitBlocked` | `Bool` |

### `OnItemAdded(akBaseItem, aiItemCount, akItemReference, akSourceContainer)`

**Kind:** Event

Event received when an item is added to this object's inventory. If the item is a persistant reference, akItemReference will
point at it - otherwise the parameter will be None

**Parameters**

| Name | Type |
|---|---|
| `akBaseItem` | `Form` |
| `aiItemCount` | `Int` |
| `akItemReference` | `ObjectReference` |
| `akSourceContainer` | `ObjectReference` |

### `OnItemRemoved(akBaseItem, aiItemCount, akItemReference, akDestContainer)`

**Kind:** Event

Event received when an item is removed from this object's inventory. If the item is a persistant reference, akItemReference
will point at it - otherwise the parameter will be None

**Parameters**

| Name | Type |
|---|---|
| `akBaseItem` | `Form` |
| `aiItemCount` | `Int` |
| `akItemReference` | `ObjectReference` |
| `akDestContainer` | `ObjectReference` |

### `OnLoad()`

**Kind:** Event

Event recieved when this object is completely loaded - will be fired every time this object is loaded

### `OnLocationChange(akOldLoc, akNewLoc)`

**Kind:** Event

Event that is triggered when this actor changes from one location to another (only if this alias points at an actor)

**Parameters**

| Name | Type |
|---|---|
| `akOldLoc` | `Location` |
| `akNewLoc` | `Location` |

### `OnLockStateChanged()`

**Kind:** Event

Event received when the lock on this object changes

### `OnLycanthropyStateChanged(abIsWerewolf)`

**Kind:** Event

Received when the lycanthropy state of this actor changes (when SendLycanthropyStateChanged is called)

**Parameters**

| Name | Type |
|---|---|
| `abIsWerewolf` | `Bool` |

### `OnMagicEffectApply(akCaster, akEffect)`

**Kind:** Event

Event received when a magic affect is being applied to this object

**Parameters**

| Name | Type |
|---|---|
| `akCaster` | `ObjectReference` |
| `akEffect` | `MagicEffect` |

### `OnObjectEquipped(akBaseObject, akReference)`

**Kind:** Event

Event received when this actor equips something - akReference may be None if object is not persistent (only if this alias points at an actor)

**Parameters**

| Name | Type |
|---|---|
| `akBaseObject` | `Form` |
| `akReference` | `ObjectReference` |

### `OnObjectUnequipped(akBaseObject, akReference)`

**Kind:** Event

Event received when this actor unequips something - akReference may be None if object is not persistent (only if this alias points at an actor)

**Parameters**

| Name | Type |
|---|---|
| `akBaseObject` | `Form` |
| `akReference` | `ObjectReference` |

### `OnOpen(akActionRef)`

**Kind:** Event

Event received when this object is opened

**Parameters**

| Name | Type |
|---|---|
| `akActionRef` | `ObjectReference` |

### `OnPackageChange(akOldPackage)`

**Kind:** Event

Event received when this actor's package changes (only if this alias points at an actor)

**Parameters**

| Name | Type |
|---|---|
| `akOldPackage` | `Package` |

### `OnPackageEnd(akOldPackage)`

**Kind:** Event

Event received when this actor's package ends (only if this alias points at an actor)

**Parameters**

| Name | Type |
|---|---|
| `akOldPackage` | `Package` |

### `OnPackageStart(akNewPackage)`

**Kind:** Event

Event received when this actor starts a new package (only if this alias points at an actor)

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

### `OnRead()`

**Kind:** Event

Event received when this object, if a book, is read

### `OnRelease()`

**Kind:** Event

Event received when this object is released by the player

### `OnReset()`

**Kind:** Event

Event received when this reference is reset

### `OnSell(akSeller)`

**Kind:** Event

Event received when this reference is sold by an actor

**Parameters**

| Name | Type |
|---|---|
| `akSeller` | `Actor` |

### `OnSit(akFurniture)`

**Kind:** Event

Event that is triggered when this actor sits in the furniture

**Parameters**

| Name | Type |
|---|---|
| `akFurniture` | `ObjectReference` |

### `OnSpellCast(akSpell)`

**Kind:** Event

Event received when a spell is cast by this object

**Parameters**

| Name | Type |
|---|---|
| `akSpell` | `Form` |

### `OnTranslationComplete()`

**Kind:** Event

Event received when translation is complete (from a call to TranslateTo)

### `OnTranslationFailed()`

**Kind:** Event

Event received when translation is aborted (from a call to StopTranslateTo)

### `OnTrapHit(akTarget, afXVel, afYVel, afZVel, afXPos, afYPos, afZPos, aeMaterial, abInitialHit, aeMotionType)`

**Kind:** Event

Event recieved when this reference hits a target

**Parameters**

| Name | Type |
|---|---|
| `akTarget` | `ObjectReference` |
| `afXVel` | `Float` |
| `afYVel` | `Float` |
| `afZVel` | `Float` |
| `afXPos` | `Float` |
| `afYPos` | `Float` |
| `afZPos` | `Float` |
| `aeMaterial` | `Int` |
| `abInitialHit` | `Bool` |
| `aeMotionType` | `Int` |

### `OnTrapHitStart(akTarget, afXVel, afYVel, afZVel, afXPos, afYPos, afZPos, aeMaterial, abInitialHit, aeMotionType)`

**Kind:** Event

Event recieved when this starts hitting a target

**Parameters**

| Name | Type |
|---|---|
| `akTarget` | `ObjectReference` |
| `afXVel` | `Float` |
| `afYVel` | `Float` |
| `afZVel` | `Float` |
| `afXPos` | `Float` |
| `afYPos` | `Float` |
| `afZPos` | `Float` |
| `aeMaterial` | `Int` |
| `abInitialHit` | `Bool` |
| `aeMotionType` | `Int` |

### `OnTrapHitStop(akTarget)`

**Kind:** Event

Event recieved when this stops hitting a target

**Parameters**

| Name | Type |
|---|---|
| `akTarget` | `ObjectReference` |

### `OnTrigger(akActionRef)`

**Kind:** Event

Event received when a this trigger is tripped

**Parameters**

| Name | Type |
|---|---|
| `akActionRef` | `ObjectReference` |

### `OnTriggerEnter(akActionRef)`

**Kind:** Event

Event received when this trigger volume is entered

**Parameters**

| Name | Type |
|---|---|
| `akActionRef` | `ObjectReference` |

### `OnTriggerLeave(akActionRef)`

**Kind:** Event

Event received when this trigger volume is left

**Parameters**

| Name | Type |
|---|---|
| `akActionRef` | `ObjectReference` |

### `OnUnequipped(akActor)`

**Kind:** Event

Event received when this object is unequipped by an actor

**Parameters**

| Name | Type |
|---|---|
| `akActor` | `Actor` |

### `OnUnload()`

**Kind:** Event

Event recieved when this object is being unloaded - will be fired every time this object is unloaded

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

### `OnWardHit(akCaster, akSpell, aiStatus)`

**Kind:** Event

Event received when this object's Ward is hit by a spell

**Parameters**

| Name | Type |
|---|---|
| `akCaster` | `ObjectReference` |
| `akSpell` | `Spell` |
| `aiStatus` | `Int` |

---

## Functions

### `AddInventoryEventFilter(akFilter)`

**Flags:** Native

Add an inventory event filter to this alias. Item added/removed events matching the
specified form (or in the specified form list) will now be let through.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFilter` | `Form` | ✓ |  |

### `Clear()`

**Flags:** Native

Clears the alias - fails on non-optional aliases

### `ForceRefIfEmpty(akNewRef) → Bool`

SJML -- tries to force a reference into the alias, but only if it's already empty.
 returns true if the alias now holds the passed reference, false if it was already filled.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akNewRef` | `ObjectReference` | ✓ |  |

### `ForceRefTo(akNewRef)`

**Flags:** Native

Forces this alias to use the specified reference

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akNewRef` | `ObjectReference` | ✓ |  |

### `GetActorRef() → Actor`

Convenience fucntion

### `GetActorReference() → Actor`

Autocast to Actor is applicable

### `GetRef() → ObjectReference`

Convenience function

### `GetReference() → ObjectReference`

**Flags:** Native

Get the object reference this alias refers to

### `RemoveAllInventoryEventFilters()`

**Flags:** Native

Remove all inventory event filters from this alias - all item added/removed events will now be received

### `RemoveInventoryEventFilter(akFilter)`

**Flags:** Native

Remove an inventory event filter from this alias. Item added/removed events matching the
specified form (or in the specified form list) will no longer be let through.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFilter` | `Form` | ✓ |  |

### `TryToAddToFaction(FactionToAddTo) → Bool`

Convenience function - jduvall

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FactionToAddTo` | `Faction` | ✓ |  |

### `TryToClear() → Bool`

Convenience function - sjml

### `TryToDisable() → Bool`

Convenience function - jduvall

### `TryToDisableNoWait() → Bool`

Convenience function - wshen

### `TryToEnable() → Bool`

Convenience function - jduvall

### `TryToEnableNoWait() → Bool`

Convenience function - wshen

### `TryToEvaluatePackage() → Bool`

Convenience function - jduvall

### `TryToKill() → Bool`

Convenience function - jduvall

### `TryToMoveTo(RefToMoveTo) → Bool`

Convenience function - jduvall

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `RefToMoveTo` | `ObjectReference` | ✓ |  |

### `TryToRemoveFromFaction(FactionToRemoveFrom) → Bool`

Convenience function - jduvall

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FactionToRemoveFrom` | `Faction` | ✓ |  |

### `TryToReset() → Bool`

Convenience function - jduvall

### `TryToStopCombat() → Bool`

Convenience function - jduvall
