# `Form`

**Source:** `skse` (Skyrim Script Extender x64) • **Flags:** Hidden

---

## Events

### `OnActorAction(actionType, akActor, source, slot)`

**Kind:** Event

ActionTypes
0 - Weapon Swing (Melee weapons that are swung, also barehand)
1 - Spell Cast (Spells and staves)
2 - Spell Fire (Spells and staves)
3 - Voice Cast
4 - Voice Fire
5 - Bow Draw
6 - Bow Release
7 - Unsheathe Begin
8 - Unsheathe End
9 - Sheathe Begin
10 - Sheathe End
Slots
0 - Left Hand
1 - Right Hand
2 - Voice

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

Note: ref is none for no target

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

Update event, sent every X seconds while this form is registered for them

### `OnUpdateGameTime()`

**Kind:** Event

Update event, sent every X hours of game time while this form is registered for them

---

## Functions

### `GetFormID() → Int`

**Flags:** Native

Returns the formID for this object

### `GetGoldValue() → Int`

**Flags:** Native

Obtains this form's value in gold. Will return -1 if the form doesn't have any value (like a quest)

### `GetKeywords() → Keyword[]`

**Flags:** Native

returns all keywords of the form

### `GetName() → String`

**Flags:** Native

returns the form's name, full name if possible

### `GetNthKeyword(index) → Keyword`

**Flags:** Native

returns the keyword at the specified index

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |

### `GetNumKeywords() → Int`

**Flags:** Native

returns the number of keywords on the form

### `GetType() → Int`

**Flags:** Native

Returns the typecode for this form object

### `GetWeight() → Float`

**Flags:** Native

returns the weight of the form

### `GetWorldModelNthTextureSet(n) → TextureSet`

**Flags:** Native

Returns the Nth texture set of the world model, if the textures can be swapped

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `n` | `Int` | ✓ |  |

### `GetWorldModelNumTextureSets() → Int`

**Flags:** Native

Returns the number of texture sets the world model has, if its textures can be swapped

### `GetWorldModelPath() → String`

**Flags:** Native

Returns the world model path of this Form, if it has a world model

### `HasKeyword(akKeyword) → Bool`

**Flags:** Native

Returns if this form has the specified keyword attached

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akKeyword` | `Keyword` | ✓ |  |

### `HasKeywordString(s) → Bool`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |

### `HasWorldModel() → Bool`

**Flags:** Native

Returns whether this Form has a World Model (fast)

### `IsPlayable() → Bool`

**Flags:** Native

Returns whether this Form is playable, only applied to Forms with the playable flag

### `PlayerKnows() → Bool`

**Flags:** Native

Is the "Known" flag set for this form?

### `RegisterForActorAction(actionType)`

**Flags:** Native

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

Registers for OnPlayerCameraState events

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

Registers for OnCrosshairRefChange events

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

Register for a single OnUpdate event, in afInterval seconds. All scripts attached to this form will get the update events
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

Register for a single OnUpdateGameTime event, in afInterval hours of game time. All scripts attached to this form will get the update events

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afInterval` | `Float` | ✓ |  |

### `RegisterForSleep()`

**Flags:** Native

Registers this form to receive events when the player sleeps and wakes up

### `RegisterForTrackedStatsEvent()`

**Flags:** Native

Registers this form to receive events when tracked stats are updated

### `RegisterForUpdate(afInterval)`

**Flags:** Native

Register for OnUpdate events, every X seconds, where X is the interval. All scripts attached to this form will get the update events

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afInterval` | `Float` | ✓ |  |

### `RegisterForUpdateGameTime(afInterval)`

**Flags:** Native

Register for OnUpdateGameTime events, every X hours of game time, where X is the interval. All scripts attached to this form will get the update events

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

### `SetGoldValue(value)`

**Flags:** Native

sets the gold value of the form

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `value` | `Int` | ✓ |  |

### `SetName(name)`

**Flags:** Native

sets the name of the form

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `name` | `String` | ✓ |  |

### `SetPlayerKnows(knows)`

**Flags:** Native

Sets whether the player knows this form
Should only be used for Magic Effects,
Words of Power, and Enchantments

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `knows` | `Bool` | ✓ |  |

### `SetWeight(weight)`

**Flags:** Native

sets the weight of the form

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `weight` | `Float` | ✓ |  |

### `SetWorldModelNthTextureSet(nSet, n)`

**Flags:** Native

Sets the world models Nth texture set, if the textures can be set

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `nSet` | `TextureSet` | ✓ |  |
| `n` | `Int` | ✓ |  |

### `SetWorldModelPath(path)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `path` | `String` | ✓ |  |

### `StartObjectProfiling()`

**Flags:** Native

Turns on profiling for this specific object and all scripts attached to it - setting doesn't persist across saves
Will do nothing on release console builds, and if the Papyrus:bEnableProfiling ini setting is off

### `StopObjectProfiling()`

**Flags:** Native

Turns off profiling for this specific object and all scripts attached to it - setting doesn't persist across saves
Will do nothing on release console builds, and if the Papyrus:bEnableProfiling ini setting is off

### `TempClone() → Form`

**Flags:** Native

Returns a temporary clone of this form

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

Unregisters this form to receive events when the player sleeps and wakes up

### `UnregisterForTrackedStatsEvent()`

**Flags:** Native

Unregisters this form from receiving events when tracked stats are updated

### `UnregisterForUpdate()`

**Flags:** Native

Unregister for OnUpdate events, all attached scripts will stop getting update events

### `UnregisterForUpdateGameTime()`

**Flags:** Native

Unregister for OnUpdateGameTime events, all attached scripts will stop getting update game time events
