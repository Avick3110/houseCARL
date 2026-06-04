# `Alias`

**Source:** `skse` (Skyrim Script Extender x64) • **Flags:** Hidden

---

## Events

### `OnActorAction(actionType, akActor, source, slot)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `actionType` | `Int` |
| `akActor` | `Actor` |
| `source` | `Form` |
| `slot` | `Int` |

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

### `OnControlDown(control)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `control` | `String` |

### `OnControlUp(control, holdTime)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `control` | `String` |
| `holdTime` | `Float` |

### `OnCrosshairRefChange(ref)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `ref` | `ObjectReference` |

### `OnGainLOS(akViewer, akTarget)`

**Kind:** Event

LOS event, sent whenever the viewer first sees the target (after registering)

**Parameters**

| Name | Type |
|---|---|
| `akViewer` | `Actor` |
| `akTarget` | `ObjectReference` |

### `OnKeyDown(keyCode)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `keyCode` | `Int` |

### `OnKeyUp(keyCode, holdTime)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `keyCode` | `Int` |
| `holdTime` | `Float` |

### `OnLostLOS(akViewer, akTarget)`

**Kind:** Event

Lost LOS event, sent whenever the viewer first loses sight of the target (after registering)

**Parameters**

| Name | Type |
|---|---|
| `akViewer` | `Actor` |
| `akTarget` | `ObjectReference` |

### `OnMenuClose(menuName)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `menuName` | `String` |

### `OnMenuOpen(menuName)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `menuName` | `String` |

### `OnNiNodeUpdate(akActor)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akActor` | `ObjectReference` |

### `OnPlayerCameraState(oldState, newState)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `oldState` | `Int` |
| `newState` | `Int` |

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

### `OnTrackedStatsEvent(arStatName, aiStatValue)`

**Kind:** Event

Event received when a tracked stat is updated for the player

**Parameters**

| Name | Type |
|---|---|
| `arStatName` | `String` |
| `aiStatValue` | `Int` |

### `OnUpdate()`

**Kind:** Event

Update event, sent every X seconds while this alias is registered for them

### `OnUpdateGameTime()`

**Kind:** Event

Update event, sent every X hours of game time while this alias is registered for them

---

## Functions

### `GetID() → Int`

**Flags:** Native

return the id of the alias

### `GetName() → String`

**Flags:** Native

SKSE64 additions built 2024-01-17 20:01:40.731000 UTC
return the name of the alias

### `GetOwningQuest() → quest`

**Flags:** Native

Returns the quest that owns this alias

### `RegisterForActorAction(actionType)`

**Flags:** Native

See Form.psc

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `actionType` | `Int` | ✓ |  |

### `RegisterForAnimationEvent(akSender, asEventName) → Bool`

**Flags:** Native

Register for the specified animation event from the specified object - returns true if it successfully registered

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSender` | `ObjectReference` | ✓ |  |
| `asEventName` | `String` | ✓ |  |

### `RegisterForCameraState()`

**Flags:** Native

See Form.psc

### `RegisterForControl(control)`

**Flags:** Native

Registers for OnControlDown and OnControlUp events for the given control.
For a list of valid controls, see Input.psc.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `control` | `String` | ✓ |  |

### `RegisterForCrosshairRef()`

**Flags:** Native

See Form.psc

### `RegisterForKey(keyCode)`

**Flags:** Native

Registers for OnKeyDown and OnKeyUp events for the given keycode.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `keyCode` | `Int` | ✓ |  |

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

### `RegisterForMenu(menuName)`

**Flags:** Native

Registers for OnMenuOpen and OnMenuClose events for the given menu.
Registrations have to be refreshed after each game load.
For a list of valid menu names, see UI.psc.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |

### `RegisterForModEvent(eventName, callbackName)`

**Flags:** Native

Registers a custom event callback for given event name.
Registrations have to be refreshed after each game load.

Examples:
	RegisterForModEvent("myCustomEvent", "MyModEventCallback")

Event signature of custom event callbacks:
	Event MyModEventCallback(string eventName, string strArg, float numArg, Form sender)
	endEvent

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventName` | `String` | ✓ |  |
| `callbackName` | `String` | ✓ |  |

### `RegisterForNiNodeUpdate()`

**Flags:** Native

Registers the script for when a QueueNiNodeUpdate is called

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

Register for a single OnUpdate event, in afInterval seconds. All scripts attached to this alias will get the update events
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

Register for a single OnUpdateGameTime event, in afInterval hours of game time. All scripts attached to this alias will get the update events

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afInterval` | `Float` | ✓ |  |

### `RegisterForSleep()`

**Flags:** Native

Registers this alias to receive events when the player sleeps and wakes up

### `RegisterForTrackedStatsEvent()`

**Flags:** Native

Registers this alias to receive events when tracked stats are updated

### `RegisterForUpdate(afInterval)`

**Flags:** Native

Register for OnUpdate events, every X seconds, where X is the interval. All scripts attached to this alias will get the update events

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afInterval` | `Float` | ✓ |  |

### `RegisterForUpdateGameTime(afInterval)`

**Flags:** Native

Register for OnUpdateGameTime events, every X hours of game time, where X is the interval. All scripts attached to this alias will get the update events

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afInterval` | `Float` | ✓ |  |

### `SendModEvent(eventName, strArg, numArg)`

**Flags:** Native

Sends custom event with given generic parameters.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventName` | `String` | ✓ |  |
| `strArg` | `String` |  | `""` |
| `numArg` | `Float` |  | `0` |

### `StartObjectProfiling()`

**Flags:** Native

Turns on profiling for this specific object and all scripts attached to it - setting doesn't persist across saves
Will do nothing on release console builds, and if the Papyrus:bEnableProfiling ini setting is off

### `StopObjectProfiling()`

**Flags:** Native

Turns off profiling for this specific object and all scripts attached to it - setting doesn't persist across saves
Will do nothing on release console builds, and if the Papyrus:bEnableProfiling ini setting is off

### `UnregisterForActorAction(actionType)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `actionType` | `Int` | ✓ |  |

### `UnregisterForAllControls()`

**Flags:** Native

### `UnregisterForAllKeys()`

**Flags:** Native

### `UnregisterForAllMenus()`

**Flags:** Native

### `UnregisterForAllModEvents()`

**Flags:** Native

### `UnregisterForAnimationEvent(akSender, asEventName)`

**Flags:** Native

Unregister for the specified animation event from the specified object

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSender` | `ObjectReference` | ✓ |  |
| `asEventName` | `String` | ✓ |  |

### `UnregisterForCameraState()`

**Flags:** Native

### `UnregisterForControl(control)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `control` | `String` | ✓ |  |

### `UnregisterForCrosshairRef()`

**Flags:** Native

### `UnregisterForKey(keyCode)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `keyCode` | `Int` | ✓ |  |

### `UnregisterForLOS(akViewer, akTarget)`

**Flags:** Native

Unregister for any LOS events between the viewer and target

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akViewer` | `Actor` | ✓ |  |
| `akTarget` | `ObjectReference` | ✓ |  |

### `UnregisterForMenu(menuName)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |

### `UnregisterForModEvent(eventName)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventName` | `String` | ✓ |  |

### `UnregisterForNiNodeUpdate()`

**Flags:** Native

### `UnregisterForSleep()`

**Flags:** Native

Unregisters this alias to receive events when the player sleeps and wakes up

### `UnregisterForTrackedStatsEvent()`

**Flags:** Native

Unregisters this alias from receiving events when tracked stats are updated

### `UnregisterForUpdate()`

**Flags:** Native

Unregister for OnUpdate events, all attached scripts will stop getting update events

### `UnregisterForUpdateGameTime()`

**Flags:** Native

Unregister for OnUpdateGameTime events, all attached scripts will stop getting update game time events
