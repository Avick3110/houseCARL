# `Form`

**Source:** `vanilla` • **Flags:** Hidden

---

## Events

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

### `OnGainLOS(akViewer, akTarget)`

**Kind:** Event

LOS event, sent whenever the viewer first sees the target (after registering)

**Parameters**

| Name | Type |
|---|---|
| `akViewer` | `Actor` |
| `akTarget` | `ObjectReference` |

### `OnLostLOS(akViewer, akTarget)`

**Kind:** Event

Lost LOS event, sent whenever the viewer first loses sight of the target (after registering)

**Parameters**

| Name | Type |
|---|---|
| `akViewer` | `Actor` |
| `akTarget` | `ObjectReference` |

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

### `HasKeyword(akKeyword) → Bool`

**Flags:** Native

Returns if this form has the specified keyword attached

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akKeyword` | `Keyword` | ✓ |  |

### `PlayerKnows() → Bool`

**Flags:** Native

Is the "Known" flag set for this form?

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
