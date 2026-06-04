# `ActiveMagicEffect`

**Source:** `vanilla` • **Flags:** Hidden

---

## Events

### `OnActivate(akActionRef)`

**Kind:** Event

Event received when this reference is activated

**Parameters**

| Name | Type |
|---|---|
| `akActionRef` | `ObjectReference` |

### `OnAnimationEvent(akSource, asEventName)`

**Kind:** Event

Animation event, sent when an object we are listening to hits one of the events we are listening for

**Parameters**

| Name | Type |
|---|---|
| `akSource` | `ObjectReference` |
| `asEventName` | `String` |

### `OnAnimationEventUnregistered(akSource, asEventName)`

**Kind:** Event

Event sent when you have been unregistered from receiving an animation event because the target
object's animation graph has been unloaded

**Parameters**

| Name | Type |
|---|---|
| `akSource` | `ObjectReference` |
| `asEventName` | `String` |

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

Event that is triggered when this actor finishes dying

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

Event that is triggered when this actor begins dying

**Parameters**

| Name | Type |
|---|---|
| `akKiller` | `Actor` |

### `OnEffectFinish(akTarget, akCaster)`

**Kind:** Event

Event received when this effect is finished (effect may already be deleted, calling
functions on this effect will fail)

**Parameters**

| Name | Type |
|---|---|
| `akTarget` | `Actor` |
| `akCaster` | `Actor` |

### `OnEffectStart(akTarget, akCaster)`

**Kind:** Event

Event received when this effect is first started (OnInit may not have been run yet!)

**Parameters**

| Name | Type |
|---|---|
| `akTarget` | `Actor` |
| `akCaster` | `Actor` |

### `OnEquipped(akActor)`

**Kind:** Event

Event received when this object is equipped by an actor

**Parameters**

| Name | Type |
|---|---|
| `akActor` | `Actor` |

### `OnGainLOS(akViewer, akTarget)`

**Kind:** Event

LOS event, sent whenever the viewer first sees the target (after registering)

**Parameters**

| Name | Type |
|---|---|
| `akViewer` | `Actor` |
| `akTarget` | `ObjectReference` |

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

Event that is triggered when this actor changes from one location to another

**Parameters**

| Name | Type |
|---|---|
| `akOldLoc` | `Location` |
| `akNewLoc` | `Location` |

### `OnLockStateChanged()`

**Kind:** Event

Event received when the lock on this object changes

### `OnLostLOS(akViewer, akTarget)`

**Kind:** Event

Lost LOS event, sent whenever the viewer first loses sight of the target (after registering)

**Parameters**

| Name | Type |
|---|---|
| `akViewer` | `Actor` |
| `akTarget` | `ObjectReference` |

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

### `OnOpen(akActionRef)`

**Kind:** Event

Event received when this object is opened

**Parameters**

| Name | Type |
|---|---|
| `akActionRef` | `ObjectReference` |

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

### `OnSleepStart(afSleepStartTime, afDesiredSleepEndTime)`

**Kind:** Event

Received when the player sleeps. Start and desired end time are in game time days (after registering)

**Parameters**

| Name | Type |
|---|---|
| `afSleepStartTime` | `Float` |
| `afDesiredSleepEndTime` | `Float` |

### `OnSleepStop(abInterrupted)`

**Kind:** Event

Received when the player stops sleeping - whether naturally or interrupted (after registering)

**Parameters**

| Name | Type |
|---|---|
| `abInterrupted` | `Bool` |

### `OnSpellCast(akSpell)`

**Kind:** Event

Event received when a spell is cast by this object

**Parameters**

| Name | Type |
|---|---|
| `akSpell` | `Form` |

### `OnTrackedStatsEvent(arStatName, aiStatValue)`

**Kind:** Event

Event received when a tracked stat is updated for the player

**Parameters**

| Name | Type |
|---|---|
| `arStatName` | `String` |
| `aiStatValue` | `Int` |

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

### `OnUpdate()`

**Kind:** Event

Update event, sent every X seconds while this magic effect is registered for them

### `OnUpdateGameTime()`

**Kind:** Event

Update event, sent every X hours of game time while this magic effect is registered for them

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

Add an inventory event filter to this effect. Item added/removed events matching the
specified form (or in the specified form list) will now be let through.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFilter` | `Form` | ✓ |  |

### `Dispel()`

**Flags:** Native

Dispel this effect

### `GetBaseObject() → MagicEffect`

**Flags:** Native

Get the base MagicEffect this active effect is using

### `GetCasterActor() → Actor`

**Flags:** Native

Get the actor that cast this spell

### `GetTargetActor() → Actor`

**Flags:** Native

Get the actor this spell is targeting (is attached to)

### `RegisterForAnimationEvent(akSender, asEventName) → Bool`

**Flags:** Native

Register for the specified animation event from the specified object - returns true if it successfully registered

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSender` | `ObjectReference` | ✓ |  |
| `asEventName` | `String` | ✓ |  |

### `RegisterForLOS(akViewer, akTarget)`

**Flags:** Native

Register for LOS gain and lost events between the viewer and the target
A loss or gain event will be sent immediately, depending on whether or not the viewer is already looking at the target or not
If the viewer is not the player, the target must be another actor

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akViewer` | `Actor` | ✓ |  |
| `akTarget` | `ObjectReference` | ✓ |  |

### `RegisterForSingleLOSGain(akViewer, akTarget)`

**Flags:** Native

Register for only the first LOS gain event between the viewer and the target
If the viewer is already looking at the target, an event will be received almost immediately
If the viewer is not the player, the target must be another actor

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akViewer` | `Actor` | ✓ |  |
| `akTarget` | `ObjectReference` | ✓ |  |

### `RegisterForSingleLOSLost(akViewer, akTarget)`

**Flags:** Native

Register for only the first LOS lost event between the viewer and the target
If the viewer is already not looking at the target, an event will be received almost immediately
If the viewer is not the player, the target must be another actor

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akViewer` | `Actor` | ✓ |  |
| `akTarget` | `ObjectReference` | ✓ |  |

### `RegisterForSingleUpdate(afInterval)`

**Flags:** Native

Register for a single OnUpdate event, in afInterval seconds. All scripts attached to this magic effect will get the update events
Of course, this means you don't need to call UnregisterForUpdate()
If you find yourself doing this:
Event OnUpdate()
    UnregisterForUpdate()
    {Do some stuff}
endEvent
Then you should use RegisterForSingleUpdate instead

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afInterval` | `Float` | ✓ |  |

### `RegisterForSingleUpdateGameTime(afInterval)`

**Flags:** Native

Register for a single OnUpdateGameTime event, in afInterval hours of game time. All scripts attached to this magic effect will get the update events

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afInterval` | `Float` | ✓ |  |

### `RegisterForSleep()`

**Flags:** Native

Registers this magic effect to receive events when the player sleeps and wakes up

### `RegisterForTrackedStatsEvent()`

**Flags:** Native

Registers this alias to receive events when tracked stats are updated

### `RegisterForUpdate(afInterval)`

**Flags:** Native

Register for OnUpdate events, every X seconds, where X is the interval. All scripts attached to this magic effect will get the update events

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afInterval` | `Float` | ✓ |  |

### `RegisterForUpdateGameTime(afInterval)`

**Flags:** Native

Register for OnUpdateGameTime events, every X hours of game time, where X is the interval. All scripts attached to this magic effect will get the update events

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afInterval` | `Float` | ✓ |  |

### `RemoveAllInventoryEventFilters()`

**Flags:** Native

Remove all inventory event filters from this effect - all item added/removed events will now be received

### `RemoveInventoryEventFilter(akFilter)`

**Flags:** Native

Remove an inventory event filter from this effect. Item added/removed events matching the
specified form (or in the specified form list) will no longer be let through.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFilter` | `Form` | ✓ |  |

### `StartObjectProfiling()`

**Flags:** Native

Turns on profiling for this specific object and all scripts attached to it - setting doesn't persist across saves
Will do nothing on release console builds, and if the Papyrus:bEnableProfiling ini setting is off

### `StopObjectProfiling()`

**Flags:** Native

Turns off profiling for this specific object and all scripts attached to it - setting doesn't persist across saves
Will do nothing on release console builds, and if the Papyrus:bEnableProfiling ini setting is off

### `UnregisterForAnimationEvent(akSender, asEventName)`

**Flags:** Native

Unregister for the specified animation event from the specified object

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSender` | `ObjectReference` | ✓ |  |
| `asEventName` | `String` | ✓ |  |

### `UnregisterForLOS(akViewer, akTarget)`

**Flags:** Native

Unregister for any LOS events between the viewer and target

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akViewer` | `Actor` | ✓ |  |
| `akTarget` | `ObjectReference` | ✓ |  |

### `UnregisterForSleep()`

**Flags:** Native

Unregisters this magic effect to receive events when the player sleeps and wakes up

### `UnregisterForTrackedStatsEvent()`

**Flags:** Native

Unregisters this magic effect from receiving events when tracked stats are updated

### `UnregisterForUpdate()`

**Flags:** Native

Unregister for OnUpdate events, all attached scripts will stop getting update events

### `UnregisterForUpdateGameTime()`

**Flags:** Native

Unregister for OnUpdateGameTime events, all attached scripts will stop getting update game time events
