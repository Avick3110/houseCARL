# `DbActiveMagicEffectTimer`

**Source:** `dylbills` (Dylbills Papyrus Functions) • **Flags:** Hidden

---

## Events

### `OnTimer(aiTimerID)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `aiTimerID` | `Int` |

### `OnTimerGameTime(aiTimerID)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `aiTimerID` | `Int` |

### `OnTimerMenuMode(aiTimerID)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `aiTimerID` | `Int` |

### `OnTimerNoMenuMode(aiTimerID)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `aiTimerID` | `Int` |

---

## Global Functions

### `CancelGameTimer(eventReceiver, aiTimerID)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `ActiveMagicEffect` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `CancelMenuModeTimer(eventReceiver, aiTimerID)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `ActiveMagicEffect` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `CancelNoMenuModeTimer(eventReceiver, aiTimerID)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `ActiveMagicEffect` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `CancelTimer(eventReceiver, aiTimerID)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `ActiveMagicEffect` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `GetTimeElapsedOnGameTimer(eventReceiver, aiTimerID) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `ActiveMagicEffect` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `GetTimeElapsedOnMenuModeTimer(eventReceiver, aiTimerID) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `ActiveMagicEffect` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `GetTimeElapsedOnNoMenuModeTimer(eventReceiver, aiTimerID) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `ActiveMagicEffect` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `GetTimeElapsedOnTimer(eventReceiver, aiTimerID) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `ActiveMagicEffect` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `GetTimeLeftOnGameTimer(eventReceiver, aiTimerID) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `ActiveMagicEffect` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `GetTimeLeftOnMenuModeTimer(eventReceiver, aiTimerID) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `ActiveMagicEffect` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `GetTimeLeftOnNoMenuModeTimer(eventReceiver, aiTimerID) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `ActiveMagicEffect` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `GetTimeLeftOnTimer(eventReceiver, aiTimerID) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `ActiveMagicEffect` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `StartGameTimer(eventReceiver, gameHours, aiTimerID)`

**Flags:** Native Global

GameTime, like utility.waitGameTime
Does NOT require the bMenuOpenCloseEventSinkEnabled setting in Data/SKSE/Plugins/DbSkseFunctions.ini to be enabled.
Uses frame update function to detect elapsed time. Interval determined by the iFrameUpdateInterval setting in Data/SKSE/Plugins/DbSkseFunctions.ini

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `ActiveMagicEffect` | ✓ |  |
| `gameHours` | `Float` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `StartMenuModeTimer(eventReceiver, seconds, aiTimerID)`

**Flags:** Native Global

MenuMode, No restrictions on time. Time while the game is paused or a menu is open does count - like Utility.WaitMenuMode.
Does NOT require the bMenuOpenCloseEventSinkEnabled setting in Data/SKSE/Plugins/DbSkseFunctions.ini to be enabled.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `ActiveMagicEffect` | ✓ |  |
| `seconds` | `Float` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `StartNoMenuModeTimer(eventReceiver, seconds, aiTimerID)`

**Flags:** Native Global

NoMenuMode, time while any menu is open, regardless if the game is paused or not is discounted.
Requires the bMenuOpenCloseEventSinkEnabled setting in Data/SKSE/Plugins/DbSkseFunctions.ini to be enabled.
Uses frame update function to detect elapsed time. Interval determined by the iFrameUpdateInterval setting in Data/SKSE/Plugins/DbSkseFunctions.ini

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `ActiveMagicEffect` | ✓ |  |
| `seconds` | `Float` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `StartTimer(eventReceiver, seconds, aiTimerID)`

**Flags:** Native Global

Time while the game is paused is discounted - like Utility.Wait
Requires the bMenuOpenCloseEventSinkEnabled setting in Data/SKSE/Plugins/DbSkseFunctions.ini to be enabled.
Uses frame update function to detect elapsed time. Interval determined by the iFrameUpdateInterval setting in Data/SKSE/Plugins/DbSkseFunctions.ini

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `ActiveMagicEffect` | ✓ |  |
| `seconds` | `Float` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |


---

## `DbAliasTimer`

**Source:** `dylbills` (Dylbills Papyrus Functions) • **Flags:** Hidden

---

## Events

### `OnTimer(aiTimerID)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `aiTimerID` | `Int` |

### `OnTimerGameTime(aiTimerID)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `aiTimerID` | `Int` |

### `OnTimerMenuMode(aiTimerID)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `aiTimerID` | `Int` |

### `OnTimerNoMenuMode(aiTimerID)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `aiTimerID` | `Int` |

---

## Global Functions

### `CancelGameTimer(eventReceiver, aiTimerID)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Alias` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `CancelMenuModeTimer(eventReceiver, aiTimerID)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Alias` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `CancelNoMenuModeTimer(eventReceiver, aiTimerID)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Alias` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `CancelTimer(eventReceiver, aiTimerID)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Alias` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `GetTimeElapsedOnGameTimer(eventReceiver, aiTimerID) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Alias` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `GetTimeElapsedOnMenuModeTimer(eventReceiver, aiTimerID) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Alias` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `GetTimeElapsedOnNoMenuModeTimer(eventReceiver, aiTimerID) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Alias` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `GetTimeElapsedOnTimer(eventReceiver, aiTimerID) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Alias` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `GetTimeLeftOnGameTimer(eventReceiver, aiTimerID) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Alias` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `GetTimeLeftOnMenuModeTimer(eventReceiver, aiTimerID) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Alias` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `GetTimeLeftOnNoMenuModeTimer(eventReceiver, aiTimerID) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Alias` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `GetTimeLeftOnTimer(eventReceiver, aiTimerID) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Alias` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `StartGameTimer(eventReceiver, gameHours, aiTimerID)`

**Flags:** Native Global

GameTime, like utility.waitGameTime
Does NOT require the bMenuOpenCloseEventSinkEnabled setting in Data/SKSE/Plugins/DbSkseFunctions.ini to be enabled.
Uses frame update function to detect elapsed time. Interval determined by the iFrameUpdateInterval setting in Data/SKSE/Plugins/DbSkseFunctions.ini

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Alias` | ✓ |  |
| `gameHours` | `Float` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `StartMenuModeTimer(eventReceiver, seconds, aiTimerID)`

**Flags:** Native Global

MenuMode, No restrictions on time. Time while the game is paused or a menu is open does count - like Utility.WaitMenuMode.
Does NOT require the bMenuOpenCloseEventSinkEnabled setting in Data/SKSE/Plugins/DbSkseFunctions.ini to be enabled.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Alias` | ✓ |  |
| `seconds` | `Float` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `StartNoMenuModeTimer(eventReceiver, seconds, aiTimerID)`

**Flags:** Native Global

NoMenuMode, time while any menu is open, regardless if the game is paused or not is discounted.
Requires the bMenuOpenCloseEventSinkEnabled setting in Data/SKSE/Plugins/DbSkseFunctions.ini to be enabled.
Uses frame update function to detect elapsed time. Interval determined by the iFrameUpdateInterval setting in Data/SKSE/Plugins/DbSkseFunctions.ini

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Alias` | ✓ |  |
| `seconds` | `Float` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `StartTimer(eventReceiver, seconds, aiTimerID)`

**Flags:** Native Global

Time while the game is paused is discounted - like Utility.Wait
Requires the bMenuOpenCloseEventSinkEnabled setting in Data/SKSE/Plugins/DbSkseFunctions.ini to be enabled.
Uses frame update function to detect elapsed time. Interval determined by the iFrameUpdateInterval setting in Data/SKSE/Plugins/DbSkseFunctions.ini

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Alias` | ✓ |  |
| `seconds` | `Float` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |


---

## `DbBigActorArray`

**Source:** `dylbills` (Dylbills Papyrus Functions) • **Extends:** `ObjectReference`

---

## Properties

### `ActorArrays: DynamicActorArrays`

**Flags:** Auto

**Accessors:** Get / Set

### `Array0: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array1: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array10: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array100: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array101: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array102: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array103: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array104: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array105: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array106: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array107: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array108: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array109: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array11: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array110: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array111: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array112: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array113: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array114: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array115: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array116: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array117: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array118: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array119: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array12: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array120: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array121: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array122: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array123: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array124: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array125: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array126: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array127: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array13: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array14: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array15: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array16: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array17: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array18: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array19: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array2: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array20: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array21: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array22: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array23: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array24: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array25: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array26: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array27: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array28: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array29: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array3: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array30: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array31: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array32: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array33: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array34: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array35: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array36: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array37: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array38: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array39: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array4: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array40: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array41: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array42: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array43: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array44: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array45: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array46: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array47: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array48: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array49: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array5: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array50: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array51: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array52: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array53: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array54: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array55: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array56: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array57: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array58: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array59: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array6: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array60: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array61: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array62: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array63: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array64: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array65: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array66: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array67: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array68: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array69: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array7: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array70: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array71: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array72: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array73: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array74: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array75: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array76: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array77: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array78: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array79: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array8: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array80: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array81: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array82: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array83: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array84: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array85: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array86: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array87: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array88: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array89: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array9: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array90: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array91: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array92: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array93: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array94: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array95: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array96: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array97: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array98: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array99: Actor[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `iterating: Bool`

**Flags:** Auto

**Accessors:** Get / Set

**Default:** `false`

### `MaxNumberOfArrays: Int`

**Flags:** Auto Hidden

**Accessors:** Get

**Default:** `128`

### `MaxSize: Int`

**Flags:** Auto Hidden

**Accessors:** Get

**Default:** `16384`

### `NumberOfArrays: Int`

**Flags:** Auto Hidden

**Accessors:** Get / Set

**Default:** `128`

### `size: Int`

**Flags:** Auto Hidden

**Accessors:** Get / Set

**Default:** `0`

---

## Events

### `OnInit()`

**Kind:** Event

---

## Global Functions

### `Create(BigArrayForm, akSize, fillElement, persistent, abInitiallyDisabled) → DbBigActorArray`

**Flags:** Global

create a big Actor array with a max size of 16384
this script and the DynamicActorArrays script should be attached to the BigArrayForm

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `BigArrayForm` | `Form` | ✓ |  |
| `akSize` | `Int` |  | `0` |
| `fillElement` | `Actor` |  |  |
| `persistent` | `Bool` |  | `true` |
| `abInitiallyDisabled` | `Bool` |  | `true` |

### `CreateMultiArray(BigArrayForm, numberOfSubArrays, subArraySize, fillElement, persistent, abInitiallyDisabled) → DbBigActorArray`

**Flags:** Global

create a multi dimensional array with numberOfSubArrays of subArraySize
BigArrayForm should have this script attached and the DynamicActorArrays script attached

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `BigArrayForm` | `Form` | ✓ |  |
| `numberOfSubArrays` | `Int` |  | `1` |
| `subArraySize` | `Int` |  | `1` |
| `fillElement` | `Actor` |  |  |
| `persistent` | `Bool` |  | `true` |
| `abInitiallyDisabled` | `Bool` |  | `true` |

---

## Functions

### `Clear()`

### `Destroy()`

### `Find(toFind) → Int`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `toFind` | `Actor` | ✓ |  |

### `GetArray(akSize) → Actor[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSize` | `Int` |  | `0` |

### `GetAt(index) → Actor`

Set the element at the index in the big array and set CurrentIndex to index (for getNext, setNext, GetPrevious and SetPrevious functions)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |

### `GetCurrentArrayIndex() → Int`

CurrentIndex is used for getNext, SetNext, GetPrevious, SetPrevious functions

### `GetCurrentIndex() → Int`

CurrentIndex is used for getNext, SetNext, GetPrevious, SetPrevious functions

### `GetCurrentSubArray() → Actor[]`

current sub array set internally. (Array0, Array1, Array2 ect. Matches the CurrentSubArrayIndex)
used for getNext, SetNext, GetPrevious, SetPrevious functions

### `GetCurrentSubArrayIndex() → Int`

CurrentIndex is used for getNext, SetNext, GetPrevious, SetPrevious functions

### `GetMaxNumberOfArrays() → Int`

### `GetMaxSize() → Int`

### `GetNext() → Actor`

get the next element in the big array. Add 1 to currentIndex and get the element
if the current index is already at the last valid element (size - 1), goes to the first index in the array (0).

### `GetNthSubArray(index, akSize) → Actor[]`

get Nth array in this object (0 to 100)
if akSize > 0, set's the size of subArray to akSize before returning

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |
| `akSize` | `Int` |  | `0` |

### `GetNumberOfArrays() → Int`

### `GetPrevious() → Actor`

Get the previous element in the big array. Subtract 1 to currentIndex and get the element
if the current index is at the first element (0), goes to the last valid index in the array (size - 1).

### `GetSize() → Int`

### `GetSubIndexesForIndex(index) → Int[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |

### `InsertAt(index, element) → Bool`

use sparingly, pushback is much faster.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |
| `element` | `Actor` | ✓ |  |

### `isBusy() → Bool`

### `IsIterating() → Bool`

### `Pop() → Actor`

get the last element of the big array and remove it, reducing the size by 1

### `pushBack(element) → Bool`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `element` | `Actor` | ✓ |  |

### `RemoveAt(index) → Actor`

remove the Actor at the index, reducing the size by 1 and moving each element after the index back by 1.
returns the Actor that's currently at the index

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |

### `resize(newSize, fillElement) → Bool`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `newSize` | `Int` | ✓ |  |
| `fillElement` | `Actor` |  |  |

### `RFind(toFind) → Int`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `toFind` | `Actor` | ✓ |  |

### `SetAt(index, element) → Bool`

Set the element at the index in the large array and set CurrentIndex (for getNext, setNext, GetPrevious and SetPrevious functions)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |
| `element` | `Actor` | ✓ |  |

### `SetCurrentIndex(index)`

set the current index (between -1 and size)
for use with getNext, SetNext, GetPrevious, SetPrevious functions

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |

### `SetCurrentSizeVariables(forceSetArray)`

don't use, for internal use only

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `forceSetArray` | `Bool` |  | `false` |

### `SetNext(element)`

set the next element in the big array. Add 1 to currentIndex and set the element
if the current index is already at the last valid element (size - 1), goes to the first index in the array (0).

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `element` | `Actor` | ✓ |  |

### `SetPrevious(element)`

Set the previous element in the big array. Subtract 1 to currentIndex and set the element
if the current index is at the first element (0), goes to the last valid index in the array (size - 1).

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `element` | `Actor` | ✓ |  |

### `waitForState(akState, waitInterval)`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akState` | `Actor` | ✓ |  |
| `waitInterval` | `Float` |  | `0.1` |

### `waitWhileBusy(waitInterval)`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `waitInterval` | `Float` |  | `0.1` |

### `waitWhileIterating(waitInterval)`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `waitInterval` | `Float` |  | `0.1` |


---

## `DbBigStringArray`

**Source:** `dylbills` (Dylbills Papyrus Functions) • **Extends:** `ObjectReference`

---

## Properties

### `Array0: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array1: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array10: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array100: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array101: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array102: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array103: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array104: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array105: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array106: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array107: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array108: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array109: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array11: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array110: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array111: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array112: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array113: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array114: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array115: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array116: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array117: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array118: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array119: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array12: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array120: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array121: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array122: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array123: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array124: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array125: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array126: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array127: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array13: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array14: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array15: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array16: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array17: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array18: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array19: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array2: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array20: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array21: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array22: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array23: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array24: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array25: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array26: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array27: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array28: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array29: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array3: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array30: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array31: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array32: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array33: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array34: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array35: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array36: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array37: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array38: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array39: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array4: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array40: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array41: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array42: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array43: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array44: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array45: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array46: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array47: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array48: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array49: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array5: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array50: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array51: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array52: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array53: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array54: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array55: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array56: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array57: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array58: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array59: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array6: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array60: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array61: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array62: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array63: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array64: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array65: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array66: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array67: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array68: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array69: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array7: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array70: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array71: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array72: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array73: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array74: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array75: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array76: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array77: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array78: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array79: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array8: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array80: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array81: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array82: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array83: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array84: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array85: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array86: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array87: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array88: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array89: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array9: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array90: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array91: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array92: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array93: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array94: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array95: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array96: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array97: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array98: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `Array99: String[]`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `iterating: Bool`

**Flags:** Auto

**Accessors:** Get / Set

**Default:** `false`

### `MaxNumberOfArrays: Int`

**Flags:** Auto Hidden

**Accessors:** Get

**Default:** `128`

### `MaxSize: Int`

**Flags:** Auto Hidden

**Accessors:** Get

**Default:** `16384`

### `NumberOfArrays: Int`

**Flags:** Auto Hidden

**Accessors:** Get / Set

**Default:** `128`

### `size: Int`

**Flags:** Auto Hidden

**Accessors:** Get / Set

**Default:** `0`

### `stringArrays: DynamicStringArrays`

**Flags:** Auto

**Accessors:** Get / Set

---

## Events

### `OnInit()`

**Kind:** Event

---

## Global Functions

### `Create(BigArrayForm, akSize, fillElement, persistent, abInitiallyDisabled) → DbBigStringArray`

**Flags:** Global

create a big string array with a max size of 16384
this script and the DynamicStringArrays script should be attached to the BigArrayForm

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `BigArrayForm` | `Form` | ✓ |  |
| `akSize` | `Int` |  | `0` |
| `fillElement` | `String` |  | `""` |
| `persistent` | `Bool` |  | `true` |
| `abInitiallyDisabled` | `Bool` |  | `true` |

### `CreateMultiArray(BigArrayForm, numberOfSubArrays, subArraySize, fillElement, persistent, abInitiallyDisabled) → DbBigStringArray`

**Flags:** Global

create a multi dimensional array with numberOfSubArrays of subArraySize
BigArrayForm should have this script attached and the DynamicStringArrays script attached

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `BigArrayForm` | `Form` | ✓ |  |
| `numberOfSubArrays` | `Int` |  | `1` |
| `subArraySize` | `Int` |  | `1` |
| `fillElement` | `String` |  | `""` |
| `persistent` | `Bool` |  | `true` |
| `abInitiallyDisabled` | `Bool` |  | `true` |

---

## Functions

### `Clear()`

### `Destroy()`

### `Find(toFind) → Int`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `toFind` | `String` | ✓ |  |

### `GetArray(akSize) → String[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSize` | `Int` |  | `0` |

### `GetAt(index) → String`

Set the element at the index in the big array and set CurrentIndex to index (for getNext, setNext, GetPrevious and SetPrevious functions)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |

### `GetCurrentArrayIndex() → Int`

CurrentIndex is used for getNext, SetNext, GetPrevious, SetPrevious functions

### `GetCurrentIndex() → Int`

CurrentIndex is used for getNext, SetNext, GetPrevious, SetPrevious functions

### `GetCurrentSubArray() → String[]`

current sub array set internally. (Array0, Array1, Array2 ect. Matches the CurrentSubArrayIndex)
used for getNext, SetNext, GetPrevious, SetPrevious functions

### `GetCurrentSubArrayIndex() → Int`

CurrentIndex is used for getNext, SetNext, GetPrevious, SetPrevious functions

### `GetMaxNumberOfArrays() → Int`

### `GetMaxSize() → Int`

### `GetNext() → String`

get the next element in the big array. Add 1 to currentIndex and get the element
if the current index is already at the last valid element (size - 1), goes to the first index in the array (0).

### `GetNthSubArray(index, akSize) → String[]`

get Nth array in this object (0 to 100)
if akSize > 0, set's the size of subArray to akSize before returning

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |
| `akSize` | `Int` |  | `0` |

### `GetNumberOfArrays() → Int`

### `GetPrevious() → String`

Get the previous element in the big array. Subtract 1 to currentIndex and get the element
if the current index is at the first element (0), goes to the last valid index in the array (size - 1).

### `GetSize() → Int`

### `GetSubIndexesForIndex(index) → Int[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |

### `InsertAt(index, element) → Bool`

use sparingly, pushback is much faster.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |
| `element` | `String` | ✓ |  |

### `isBusy() → Bool`

### `IsIterating() → Bool`

### `Pop() → String`

get the last element of the big array and remove it, reducing the size by 1

### `pushBack(element) → Bool`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `element` | `String` | ✓ |  |

### `RemoveAt(index) → String`

remove the string at the index, reducing the size by 1 and moving each element after the index back by 1.
returns the string that's currently at the index

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |

### `resize(newSize, fillElement) → Bool`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `newSize` | `Int` | ✓ |  |
| `fillElement` | `String` |  | `""` |

### `RFind(toFind) → Int`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `toFind` | `String` | ✓ |  |

### `SetAt(index, element) → Bool`

Set the element at the index in the large array and set CurrentIndex (for getNext, setNext, GetPrevious and SetPrevious functions)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |
| `element` | `String` | ✓ |  |

### `SetCurrentIndex(index)`

set the current index (between -1 and size)
for use with getNext, SetNext, GetPrevious, SetPrevious functions

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |

### `SetCurrentSizeVariables(forceSetArray)`

don't use, for internal use only

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `forceSetArray` | `Bool` |  | `false` |

### `SetNext(element)`

set the next element in the big array. Add 1 to currentIndex and set the element
if the current index is already at the last valid element (size - 1), goes to the first index in the array (0).

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `element` | `String` | ✓ |  |

### `SetPrevious(element)`

Set the previous element in the big array. Subtract 1 to currentIndex and set the element
if the current index is at the first element (0), goes to the last valid index in the array (size - 1).

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `element` | `String` | ✓ |  |

### `waitForState(akState, waitInterval)`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akState` | `String` | ✓ |  |
| `waitInterval` | `Float` |  | `0.1` |

### `waitWhileBusy(waitInterval)`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `waitInterval` | `Float` |  | `0.1` |

### `waitWhileIterating(waitInterval)`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `waitInterval` | `Float` |  | `0.1` |


---

## `DbColorFunctions`

**Source:** `dylbills` (Dylbills Papyrus Functions) • **Flags:** Hidden

---

## Global Functions

### `AddColorFont(s, iColor) → String`

**Flags:** Global

for use in text replacement, such as in books, or MCM text.
Adds color font to string

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `iColor` | `Int` | ✓ |  |

### `colorHexToRGB(colorHex) → Int[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `colorHex` | `String` | ✓ |  |

### `ColorIntToHSL(iColor) → Int[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `iColor` | `Int` | ✓ |  |

### `GetRandomHSL() → Int[]`

**Flags:** Global

get a random hsl color in int array.

### `GetRandomRGB() → Int[]`

**Flags:** Global

get random rgb color in int array.

### `HSLToRGB(H, S, L) → Int[]`

**Flags:** Global

convert HSL to RGB format and return in int array. [0] = R [1] = G [2] = B
S and L input should be between 0 and 100 (percent)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `H` | `Int` | ✓ |  |
| `S` | `Int` | ✓ |  |
| `L` | `Int` | ✓ |  |

### `IntToRGB(RGBInt) → Int[]`

**Flags:** Global

Opposite of RGBToInt. Convert RGBInt (base 10) to seperate R G B values and return float array. [0] = R, [1] = G, [2] = B

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `RGBInt` | `Int` | ✓ |  |

### `RGB_ClampBetween0and1(f) → Float`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `f` | `Float` | ✓ |  |

### `RGBChannel(t1, t2, c) → Float`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `t1` | `Float` | ✓ |  |
| `t2` | `Float` | ✓ |  |
| `c` | `Float` | ✓ |  |

### `RGBMax(r, g, b) → Float`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `r` | `Float` | ✓ |  |
| `g` | `Float` | ✓ |  |
| `b` | `Float` | ✓ |  |

### `RGBMin(r, g, b) → Float`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `r` | `Float` | ✓ |  |
| `g` | `Float` | ✓ |  |
| `b` | `Float` | ✓ |  |

### `RGBToColorHex(rgb) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `rgb` | `Int[]` | ✓ |  |

### `RGBToHSL(R, G, B) → Int[]`

**Flags:** Global

rgb / hsl conversion functions I wrote from following this guide:
https://www.niwa.nu/2013/05/math-behind-colorspace-conversions-rgb-hsl/
convert RGB to HSL format and return in int array. [0] = H [1] = S [2] = L
S and L are ints between 0 and 100 (percent)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `R` | `Int` | ✓ |  |
| `G` | `Int` | ✓ |  |
| `B` | `Int` | ✓ |  |

### `RGBToInt(R, G, B) → Int`

**Flags:** Global

Convert R G B to single int (base 10 instead of base 16 for hex)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `R` | `Int` | ✓ |  |
| `G` | `Int` | ✓ |  |
| `B` | `Int` | ✓ |  |


---

## `DbConditionFunctions`

**Source:** `dylbills` (Dylbills Papyrus Functions) • **Flags:** Hidden

---

## Global Functions

### `ConditionEventExists(conditionId, target) → Bool`

**Flags:** Native Global

Does the condition event for the condition with the conditionId and target exist?

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `conditionId` | `String` | ✓ |  |
| `target` | `ObjectReference` |  |  |

### `ConditionExists(conditionId) → Bool`

**Flags:** Native Global

Does the condition with the conditionId exist?

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `conditionId` | `String` | ✓ |  |

### `CountConditionEvents(conditionId) → Int`

**Flags:** Native Global

Count how many condition events were created with the CreateConditionEvent function for the condition with the conditionId.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `conditionId` | `String` | ✓ |  |

### `CreateCondition(conditionId, conditionFunction, comparison, value)`

**Flags:** Native Global

create a new condition. ConditionId should be unique, so for example use "modPrefix_conditionName".
int comparison options are:
0 = "=="
1 = "!="
2 = ">"
3 = ">="
4 = "<"
5 = "<="
If the condition with the conditionId already exists, this will overwrite the condition with a new one. If overwritten, the params will have to be set again.
Use the ConditionExists function first to prevent this from happening.
See below for the available int conditionFunctions.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `conditionId` | `String` | ✓ |  |
| `conditionFunction` | `Int` | ✓ |  |
| `comparison` | `Int` |  | `0` |
| `value` | `Float` |  | `1` |

### `CreateConditionEvent(conditionId, target) → Bool`

**Flags:** Native Global

Create a condition event for the condition with the conditionId on the optional target.
Returns false if the event already exists or the condition with the conditionId wasn't found.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `conditionId` | `String` | ✓ |  |
| `target` | `ObjectReference` |  |  |

### `DestroyAllConditionEvents(conditionId) → Int`

**Flags:** Native Global

Destroy all condition events created with the CreateConditionEvent function for the condition with the conditionId and return the number destroyed.
Note that using DestroyCondition(string conditionId) will DestroyAllConditionEvents for the conditionId as well.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `conditionId` | `String` | ✓ |  |

### `DestroyCondition(conditionId)`

**Flags:** Native Global

Destroy the condition with the conditionId created with the CreateCondition function.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `conditionId` | `String` | ✓ |  |

### `DestroyConditionEvent(conditionId, target) → Bool`

**Flags:** Native Global

Destroy the condition event previously created for the condition with the conditionId on the optional target.
Returns true if the event exists and was destroyed.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `conditionId` | `String` | ✓ |  |
| `target` | `ObjectReference` |  |  |

### `EvaluateCondition(conditionId, target) → Bool`

**Flags:** Native Global

Run the condition with the conditionId on the optional target and evaluate. Return true if the condition is met.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `conditionId` | `String` | ✓ |  |
| `target` | `ObjectReference` |  |  |

### `SetConditionComparison(conditionId, comparison) → Bool`

**Flags:** Native Global

Set the condition with the conditionId's comparison for the conditionId.
int comparison options are:
0 = "=="
1 = "!="
2 = ">"
3 = ">="
4 = "<"
5 = "<="

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `conditionId` | `String` | ✓ |  |
| `comparison` | `Int` | ✓ |  |

### `SetConditionParameterAlias(conditionId, param, paramIndex) → Bool`

**Flags:** Native Global

Set the nth parameter for the condition with the conditionId to the Alias param. paramIndex must be between 0 and 2.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `conditionId` | `String` | ✓ |  |
| `param` | `Alias` | ✓ |  |
| `paramIndex` | `Int` |  | `0` |

### `SetConditionParameterBool(conditionId, param, paramIndex) → Bool`

**Flags:** Native Global

Set the nth parameter for the condition with the conditionId to the Bool param. paramIndex must be between 0 and 2.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `conditionId` | `String` | ✓ |  |
| `param` | `Bool` | ✓ |  |
| `paramIndex` | `Int` |  | `0` |

### `SetConditionParameterFloat(conditionId, param, paramIndex) → Bool`

**Flags:** Native Global

Set the nth parameter for the condition with the conditionId to the Float param. paramIndex must be between 0 and 2.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `conditionId` | `String` | ✓ |  |
| `param` | `Float` | ✓ |  |
| `paramIndex` | `Int` |  | `0` |

### `SetConditionParameterForm(conditionId, param, paramIndex) → Bool`

**Flags:** Native Global

Set the nth parameter for the condition with the conditionId to the form param. paramIndex must be between 0 and 2.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `conditionId` | `String` | ✓ |  |
| `param` | `form` | ✓ |  |
| `paramIndex` | `Int` |  | `0` |

### `SetConditionParameterInt(conditionId, param, paramIndex) → Bool`

**Flags:** Native Global

Set the nth parameter for the condition with the conditionId to the Int param. paramIndex must be between 0 and 2.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `conditionId` | `String` | ✓ |  |
| `param` | `Int` | ✓ |  |
| `paramIndex` | `Int` |  | `0` |

### `SetConditionParameterString(conditionId, param, paramIndex) → Bool`

**Flags:** Native Global

Set the nth parameter for the condition with the conditionId to the String param. paramIndex must be between 0 and 2.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `conditionId` | `String` | ✓ |  |
| `param` | `String` | ✓ |  |
| `paramIndex` | `Int` |  | `0` |

### `SetConditionValue(conditionId, value) → Bool`

**Flags:** Native Global

Set the condition with the conditionId's comparison value.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `conditionId` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |


---

## `DbFormTimer`

**Source:** `dylbills` (Dylbills Papyrus Functions) • **Flags:** Hidden

---

## Events

### `OnTimer(aiTimerID)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `aiTimerID` | `Int` |

### `OnTimerGameTime(aiTimerID)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `aiTimerID` | `Int` |

### `OnTimerMenuMode(aiTimerID)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `aiTimerID` | `Int` |

### `OnTimerNoMenuMode(aiTimerID)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `aiTimerID` | `Int` |

---

## Global Functions

### `CancelGameTimer(eventReceiver, aiTimerID)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Form` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `CancelMenuModeTimer(eventReceiver, aiTimerID)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Form` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `CancelNoMenuModeTimer(eventReceiver, aiTimerID)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Form` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `CancelTimer(eventReceiver, aiTimerID)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Form` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `GetTimeElapsedOnGameTimer(eventReceiver, aiTimerID) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Form` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `GetTimeElapsedOnMenuModeTimer(eventReceiver, aiTimerID) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Form` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `GetTimeElapsedOnNoMenuModeTimer(eventReceiver, aiTimerID) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Form` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `GetTimeElapsedOnTimer(eventReceiver, aiTimerID) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Form` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `GetTimeLeftOnGameTimer(eventReceiver, aiTimerID) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Form` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `GetTimeLeftOnMenuModeTimer(eventReceiver, aiTimerID) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Form` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `GetTimeLeftOnNoMenuModeTimer(eventReceiver, aiTimerID) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Form` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `GetTimeLeftOnTimer(eventReceiver, aiTimerID) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Form` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `StartGameTimer(eventReceiver, gameHours, aiTimerID)`

**Flags:** Native Global

GameTime, like utility.waitGameTime
Does NOT require the bMenuOpenCloseEventSinkEnabled setting in Data/SKSE/Plugins/DbSkseFunctions.ini to be enabled.
Uses frame update function to detect elapsed time. Interval determined by the iFrameUpdateInterval setting in Data/SKSE/Plugins/DbSkseFunctions.ini

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Form` | ✓ |  |
| `gameHours` | `Float` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `StartMenuModeTimer(eventReceiver, seconds, aiTimerID)`

**Flags:** Native Global

MenuMode, No restrictions on time. Time while the game is paused or a menu is open does count - like Utility.WaitMenuMode.
Does NOT require the bMenuOpenCloseEventSinkEnabled setting in Data/SKSE/Plugins/DbSkseFunctions.ini to be enabled.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Form` | ✓ |  |
| `seconds` | `Float` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `StartNoMenuModeTimer(eventReceiver, seconds, aiTimerID)`

**Flags:** Native Global

NoMenuMode, time while any menu is open, regardless if the game is paused or not is discounted.
Requires the bMenuOpenCloseEventSinkEnabled setting in Data/SKSE/Plugins/DbSkseFunctions.ini to be enabled.
Uses frame update function to detect elapsed time. Interval determined by the iFrameUpdateInterval setting in Data/SKSE/Plugins/DbSkseFunctions.ini

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Form` | ✓ |  |
| `seconds` | `Float` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |

### `StartTimer(eventReceiver, seconds, aiTimerID)`

**Flags:** Native Global

Time while the game is paused is discounted - like Utility.Wait
Requires the bMenuOpenCloseEventSinkEnabled setting in Data/SKSE/Plugins/DbSkseFunctions.ini to be enabled.
Uses frame update function to detect elapsed time. Interval determined by the iFrameUpdateInterval setting in Data/SKSE/Plugins/DbSkseFunctions.ini

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Form` | ✓ |  |
| `seconds` | `Float` | ✓ |  |
| `aiTimerID` | `Int` |  | `0` |


---

## `DbIniFunctions`

**Source:** `dylbills` (Dylbills Papyrus Functions) • **Flags:** Hidden

---

## Global Functions

### `ConvertFilePathFromSEtoLE(sFilePath) → String`

**Flags:** Global

used for the above functions
Requires DbMiscFunctions and skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sFilePath` | `String` | ✓ |  |

### `GetIniBool(sFilePath, sSection, sKey, Default) → Bool`

**Flags:** Global

Getters============================================================================================

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sFilePath` | `String` | ✓ |  |
| `sSection` | `String` | ✓ |  |
| `sKey` | `String` | ✓ |  |
| `Default` | `Bool` |  | `false` |

### `GetIniFloat(sFilePath, sSection, sKey, Default) → Float`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sFilePath` | `String` | ✓ |  |
| `sSection` | `String` | ✓ |  |
| `sKey` | `String` | ✓ |  |
| `Default` | `Float` |  | `0` |

### `GetIniInt(sFilePath, sSection, sKey, Default) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sFilePath` | `String` | ✓ |  |
| `sSection` | `String` | ✓ |  |
| `sKey` | `String` | ✓ |  |
| `Default` | `Int` |  | `0` |

### `GetIniString(sFilePath, sSection, sKey, Default) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sFilePath` | `String` | ✓ |  |
| `sSection` | `String` | ✓ |  |
| `sKey` | `String` | ✓ |  |
| `Default` | `String` |  | `""` |

### `HasIniBool(sFilePath, sSection, sKey) → Bool`

**Flags:** Global

Has functions==================================================================

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sFilePath` | `String` | ✓ |  |
| `sSection` | `String` | ✓ |  |
| `sKey` | `String` | ✓ |  |

### `HasIniFloat(sFilePath, sSection, sKey) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sFilePath` | `String` | ✓ |  |
| `sSection` | `String` | ✓ |  |
| `sKey` | `String` | ✓ |  |

### `HasIniInt(sFilePath, sSection, sKey) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sFilePath` | `String` | ✓ |  |
| `sSection` | `String` | ✓ |  |
| `sKey` | `String` | ✓ |  |

### `HasIniString(sFilePath, sSection, sKey) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sFilePath` | `String` | ✓ |  |
| `sSection` | `String` | ✓ |  |
| `sKey` | `String` | ✓ |  |

### `SetIniBool(sFilePath, sSection, sKey, Value, bForce) → Bool`

**Flags:** Global

Setters =======================================================================================================
if bForce == true, will add the sKey to the sSection if it doesn't exist.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sFilePath` | `String` | ✓ |  |
| `sSection` | `String` | ✓ |  |
| `sKey` | `String` | ✓ |  |
| `Value` | `Bool` | ✓ |  |
| `bForce` | `Bool` |  | `false` |

### `SetIniFloat(sFilePath, sSection, sKey, Value, bForce) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sFilePath` | `String` | ✓ |  |
| `sSection` | `String` | ✓ |  |
| `sKey` | `String` | ✓ |  |
| `Value` | `Float` | ✓ |  |
| `bForce` | `Bool` |  | `false` |

### `SetIniInt(sFilePath, sSection, sKey, Value, bForce) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sFilePath` | `String` | ✓ |  |
| `sSection` | `String` | ✓ |  |
| `sKey` | `String` | ✓ |  |
| `Value` | `Int` | ✓ |  |
| `bForce` | `Bool` |  | `false` |

### `SetIniString(sFilePath, sSection, sKey, Value, bForce) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sFilePath` | `String` | ✓ |  |
| `sSection` | `String` | ✓ |  |
| `sKey` | `String` | ✓ |  |
| `Value` | `String` | ✓ |  |
| `bForce` | `Bool` |  | `false` |

### `WriteForceSetIniFunction(inputFilePath, outputFilePath, functionName, onlyInisWithDefaults)`

**Flags:** Global

used to write a function in your script to create an ini file for your mod.
Let's say you have a script Data/Scripts/Source/MyScript.psc
In the script you have a bunch of GetIni functions from this script like:
String MyString = DbIniFunctions.GetIniString("Data/Interface/MyMod/Settings.ini", "Strings", "MyString", "My String")
int MyInt = DbIniFunctions.GetIniInt("Data/Interface/MyMod/Settings.ini", "Main", "MyInt", 42)
Using this function: 'WriteForceSetIniFunction("Data/Scripts/Source/MyScript.psc", "Data/Scripts/Source/MyScript.psc", "CreateIniFile")'
Will write this function in your script:
Function CreateIniFile()
    DbIniFunctions.SetIniString("Data/Interface/MyMod/Settings.ini", "Strings", "MyString", "My String", true)
    DbIniFunctions.SetIniInt("Data/Interface/MyMod/Settings.ini", "Main", "MyInt", 42, true)
Endfunction
You can then use this function to create your ini file and write all the inis in one go.
requires skse and papyrusUtil

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `inputFilePath` | `String` | ✓ |  |
| `outputFilePath` | `String` | ✓ |  |
| `functionName` | `String` | ✓ |  |
| `onlyInisWithDefaults` | `Bool` |  | `true` |


---

## `DbMiscFunctions`

**Source:** `dylbills` (Dylbills Papyrus Functions) • **Flags:** Hidden

---

## Global Functions

### `ActorHasFormEquiped(akActor, akForm) → Bool`

**Flags:** Global

the IsEquipped function doesn't work for spells, hence the need for this function.
no requirements

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `akForm` | `Form` | ✓ |  |

### `AddFormArrayFormsToList(akArray, akList)`

**Flags:** Global

Add all forms in akArray to akList

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Form[]` | ✓ |  |
| `akList` | `Formlist` | ✓ |  |

### `AddPrefixToFormName(akForm, Prefix, OnlyIfNotPresent)`

**Flags:** Global

Add prefix to akForm's name
If OnlyIfNotPresent == true (default) only adds the prefix if it's not already present.
Requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `Prefix` | `String` | ✓ |  |
| `OnlyIfNotPresent` | `Bool` |  | `true` |

### `AddPrefixToFormNames(akForms, Prefix, OnlyIfNotPresent)`

**Flags:** Global

Same as above but adds to all form names in array
Requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForms` | `Form[]` | ✓ |  |
| `Prefix` | `String` | ✓ |  |
| `OnlyIfNotPresent` | `Bool` |  | `true` |

### `AddPrefixToString(s, Prefix, OnlyIfNotPresent) → String`

**Flags:** Global

Add prefix to string s and return new string
If OnlyIfNotPresent == true (default) only adds the prefix if it's not already present.
Requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `Prefix` | `String` | ✓ |  |
| `OnlyIfNotPresent` | `Bool` |  | `true` |

### `AddPrefixToStrings(s, Prefix, OnlyIfNotPresent) → String[]`

**Flags:** Global

Same as above but adds to all strings in array
Requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String[]` | ✓ |  |
| `Prefix` | `String` | ✓ |  |
| `OnlyIfNotPresent` | `Bool` |  | `true` |

### `AddSuffixToFormName(akForm, Suffix, OnlyIfNotPresent)`

**Flags:** Global

Add Suffix to akForm's name
If OnlyIfNotPresent == true (default) only adds the Suffix if it's not already present.
Requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `Suffix` | `String` | ✓ |  |
| `OnlyIfNotPresent` | `Bool` |  | `true` |

### `AddSuffixToFormNames(akForms, Suffix, OnlyIfNotPresent)`

**Flags:** Global

Same as above but adds to all form names in array
Requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForms` | `Form[]` | ✓ |  |
| `Suffix` | `String` | ✓ |  |
| `OnlyIfNotPresent` | `Bool` |  | `true` |

### `AddSuffixToString(s, Suffix, OnlyIfNotPresent) → String`

**Flags:** Global

Add suffix to string s and return new string
If OnlyIfNotPresent == true (default) only adds the suffix if it's not already present.
Requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `Suffix` | `String` | ✓ |  |
| `OnlyIfNotPresent` | `Bool` |  | `true` |

### `AddSuffixToStrings(s, Suffix, OnlyIfNotPresent) → String[]`

**Flags:** Global

Same as above but adds to all strings in array
Requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String[]` | ✓ |  |
| `Suffix` | `String` | ✓ |  |
| `OnlyIfNotPresent` | `Bool` |  | `true` |

### `akFormHasKeywordString(akForm, akString) → Bool`

**Flags:** Global

Like HasKeywordString but returns true if multiple esp's have keyWords with the same name added.
Requires SKSE

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `akString` | `String` | ✓ |  |

### `ApplyHavokImpulseLocal(Ref, Angle, afZ, afMagnitude)`

**Flags:** Global

Apply Havok Impulse from left / right angle + Z direction. No requirements.
Examples:
ApplyHavokImpulseLocal(MyRef, 0, 5, 10) applies havok impulse so the ref flies forward and up
ApplyHavokImpulseLocal(MyRef, 90, -5, 10) applies havok impulse so the ref flies to the right and down

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Ref` | `ObjectReference` | ✓ |  |
| `Angle` | `Float` | ✓ |  |
| `afZ` | `Float` | ✓ |  |
| `afMagnitude` | `Float` | ✓ |  |

### `AttachPapyrusScript(akScript, Ref)`

**Flags:** Global

Attach the akScript to the Ref.
Mostly for LE as on SE you can use the No Esp mod instead
if ref == none, attachs akScript to the player
requires skse and consoleUtil or DbSkseFunctions.
DbSkseFunctions (included with this mod) only works on SE and AE

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akScript` | `String` | ✓ |  |
| `Ref` | `ObjectReference` | ✓ |  |

### `CanInteractWith(ref, checkAshPile) → Bool`

**Flags:** Global

requires skse and DbSkseFunctions.psc version 6.7 or greater

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `checkAshPile` | `Bool` |  | `true` |

### `ClampFloat(f, Min, Max) → Float`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `f` | `Float` | ✓ |  |
| `Min` | `Float` | ✓ |  |
| `Max` | `Float` | ✓ |  |

### `ClampInt(i, Min, Max) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `i` | `Int` | ✓ |  |
| `Min` | `Int` | ✓ |  |
| `Max` | `Int` | ✓ |  |

### `CloseMenu(menuName)`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |

### `ConvertHexToInt(hex, TreatAsNegative) → Int`

**Flags:** Global

requires skse
If TreatAsNegative == true, returns hex as negative number.
Example:
ConvertHexToInt("FD4", true) returns -44
ConvertHexToInt("FD4", false) returns 4052
Note that if the hex is 8 digits in length (such as form IDs) and starts with "F" it is always treated as negative natively by papyrus.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `hex` | `String` | ✓ |  |
| `TreatAsNegative` | `Bool` |  | `false` |

### `ConvertIntToHex(i, minDigits) → String`

**Flags:** Global

requires skse. Convert int to hex string.
if result string length is less than minDigits,
adds 0's to the start for positive numbers, or f's to the start for negative numbers.
default is 8 (for form IDs)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `i` | `Int` | ✓ |  |
| `minDigits` | `Int` |  | `8` |

### `CopykeywordsToForm(A, B)`

**Flags:** Global

Copy keywords from form A onto form B.
Requires skse and papyrus extender

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `A` | `Form` | ✓ |  |
| `B` | `Form` | ✓ |  |

### `CopyStringArray(akArray) → String[]`

**Flags:** Global

requires skse.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `String[]` | ✓ |  |

### `CountDecimalPlaces(f) → Int`

**Flags:** Global

requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `f` | `Float` | ✓ |  |

### `CountStringsInString(s, ToFind, WholeWordsOnly) → Int`

**Flags:** Global

Count the number of String ToFind that occures in String s.
If WholeWordsOnly == true, only counts where the string ToFind occures is surrounded by whiteSpace.
requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `ToFind` | `String` | ✓ |  |
| `WholeWordsOnly` | `Bool` |  | `false` |

### `CountWhiteSpaces(s, IncludeSpaces, IncludeTabs, IncludeNewLines) → Int`

**Flags:** Global

Requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `IncludeSpaces` | `Bool` |  | `true` |
| `IncludeTabs` | `Bool` |  | `true` |
| `IncludeNewLines` | `Bool` |  | `true` |

### `CreateRandomWord(WordLength, letters, vowels, pairs) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `WordLength` | `Int` | ✓ |  |
| `letters` | `String` |  | `"cdfghjklmnpqrstvwxyz"` |
| `vowels` | `String` |  | `"eiou"` |
| `pairs` | `String` |  | `"t gr ea ie ei pr qw fr cr tr vr br pl cl "` |

### `CreateXMarkerRef(PersistentRef, PlaceAtMeRef) → ObjectReference`

**Flags:** Global

create new xMarker ObjectReference
if PlaceAtMeRef == none (default) places new marker at the player.
no requirements

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `PersistentRef` | `Bool` |  | `false` |
| `PlaceAtMeRef` | `ObjectReference` |  |  |

### `DisableRefsInCell(akCell, formTypeFilter)`

**Flags:** Global

Disable all the object refs in akCell that match formTypeFilter.
If formTypeFilter is 0 (default) disable all refs in the cell.
Requires skse.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akCell` | `cell` | ✓ |  |
| `formTypeFilter` | `Int` |  | `0` |

### `DisableThenEnablePlayerControls(Delay)`

**Flags:** Global

Useful for force closing the inventory menu, or forcing the player to sheathe their weapon.
No requirements

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Delay` | `Float` |  | `1` |

### `DropAllItems(Ref, dropIndividual, delay)`

**Flags:** Global

Drop all items from ref, Ref must be a container or actor.
If dropIndividual is true, drops multiple of the same item time individually so they don't stack. If false, items are dropped stacked.
Requires SKSE

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Ref` | `ObjectReference` | ✓ |  |
| `dropIndividual` | `Bool` |  | `false` |
| `delay` | `Float` |  | `0.01` |

### `DropIndividualItems(Ref, Item, NumOfItems, delay)`

**Flags:** Global

Drop the NumofItems from ref individually so they don't stack.
If NumofItems == 0, drops all of the item from ref.
No requirements

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Ref` | `ObjectReference` | ✓ |  |
| `Item` | `Form` | ✓ |  |
| `NumOfItems` | `Int` |  | `0` |
| `delay` | `Float` |  | `0.01` |

### `EnableRefsInCell(akCell, formTypeFilter)`

**Flags:** Global

Enable all the object refs in akCell that match formTypeFilter.
If formTypeFilter is 0 (default) enable all refs in the cell.
Requires skse.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akCell` | `cell` | ✓ |  |
| `formTypeFilter` | `Int` |  | `0` |

### `FindLastStringIndex(s, ToFind) → Int`

**Flags:** Global

Finds the last index of String ToFind in string s
Example: FindLastStringIndex("The dog is the coolest dog in the world", "The") returns 30, the last instance of "the"
Requires skse.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `ToFind` | `String` | ✓ |  |

### `FindNextNonWhiteSpaceCharIndexInString(s, startIndex) → Int`

**Flags:** Global

requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `startIndex` | `Int` |  | `0` |

### `FindNextNonWhiteSpaceCharInString(s, startIndex) → String`

**Flags:** Global

requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `startIndex` | `Int` |  | `0` |

### `FindNextWhiteSpaceCharIndexInString(s, startIndex) → Int`

**Flags:** Global

requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `startIndex` | `Int` |  | `0` |

### `FindNextWhiteSpaceCharInString(s, startIndex) → String`

**Flags:** Global

requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `startIndex` | `Int` |  | `0` |

### `FindNextWordInString(s, startIndex) → String`

**Flags:** Global

find next word in string. (Next string of non white space) from startIndex
Examples:
String A = FindNextWordInString("This is some text ")      ;A = "This"
String B = FindNextWordInString("This is some text ", 12)  ;B = "text"
String C = FindNextWordInString("This is some text ", 9)   ;C = "ome"
requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `startIndex` | `Int` |  | `0` |

### `findNthInstanceInString(s, toFind, nthInstance, startIndex) → Int`

**Flags:** Global

find the index of the nth instance of string toFind in string s
if nthInstance == -1 (default), finds the last instance in string s
requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `toFind` | `String` | ✓ |  |
| `nthInstance` | `Int` |  | `-1` |
| `startIndex` | `Int` |  | `0` |

### `FindWholeWordString(s, ToFind, StartIndex) → Int`

**Flags:** Global

returns the index of the first character of toFind inside string s
 returns -1 if toFind is not part of the string or if startIndex is invalid or if the characters preceding and following ToFind in s are not whitespace
 Example
FindWholeWordString("TestString", "String") returns -1
FindWholeWordString("Test String", "String") returns 5

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `ToFind` | `String` | ✓ |  |
| `StartIndex` | `Int` |  | `0` |

### `FormHasKeywordInArray(akForm, akList, AllKeywords) → Bool`

**Flags:** Global

If AllKeywords == false (default) returns true if the akForm has any keyword in the akList array.
If allKeywords == true, only returns true if the akForm has all keywords in the akList.
No requirements

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `akList` | `Keyword[]` | ✓ |  |
| `AllKeywords` | `Bool` |  | `false` |

### `FormHasKeywordInFormList(akForm, akList, AllKeywords) → Bool`

**Flags:** Global

If AllKeywords == false (default) returns true if the akForm has any keyword in the akList formlist.
If allKeywords == true, only returns true if the akForm has all keywords in the akList.
No requirements

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `akList` | `Formlist` | ✓ |  |
| `AllKeywords` | `Bool` |  | `false` |

### `FormHasKeywordInJsonUtilList(akForm, JsonFilePath, ListKeyName, AllKeywords) → Bool`

**Flags:** Global

If AllKeywords == false (default) returns true if the akForm has any keyword in the JsonUtil Form list.
If allKeywords == true, only returns true if the akForm has all keywords in the List.
Requires skse and PapyrusUtil

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `JsonFilePath` | `String` | ✓ |  |
| `ListKeyName` | `String` | ✓ |  |
| `AllKeywords` | `Bool` |  | `false` |

### `FormHasKeywordInStorageUtilList(akForm, ObjKey, ListKeyName, AllKeywords) → Bool`

**Flags:** Global

If AllKeywords == false (default) returns true if the akForm has any keyword in the StorageUtil Form list.
If allKeywords == true, only returns true if the akForm has all keywords in the List.
Requires skse and PapyrusUtil

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `ObjKey` | `Form` | ✓ |  |
| `ListKeyName` | `String` | ✓ |  |
| `AllKeywords` | `Bool` |  | `false` |

### `FormlistToArray(akList) → Form[]`

**Flags:** Global

Requires skse. Add all forms in akList to new form array.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akList` | `Formlist` | ✓ |  |

### `FormNamesInFormListToString(akList, divider, nullName) → String`

**Flags:** Global

requires skse, put all Form names of Forms in akList to a single string seperated by divider. Default new line.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akList` | `Formlist` | ✓ |  |
| `divider` | `String` |  | `"n"` |
| `nullName` | `String` |  | `"ull"` |

### `GetActorFormType(F) → Int`

**Flags:** Global

get common form types without SKSE =======================================================================
These functions mimic the main Categories in the creation kit.
GetActorFormType corresponds to the Actor category. GetAudioFormType corresponds to the Audo category ect.
Note, can pass in ObjectReference's and it will auto get baseObject and return type.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `F` | `Form` | ✓ |  |

### `GetActorFormTypeString(F, TypeStrings) → String`

**Flags:** Global

Same as above but returns strings instead of ints==============================================================================
If TypeStrings == none, returns the ScriptName of type. Can pass in string array to return different strings for translations.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `F` | `Form` | ✓ |  |
| `TypeStrings` | `String[]` |  |  |

### `GetActorNames(akArray) → String[]`

**Flags:** Global

requires skse, put all actor names of actors in akArray to a string array and return.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Actor[]` | ✓ |  |

### `GetActorSoulSize(akActor) → Int`

**Flags:** Global

Get the actor soul size. 0 = petty, 1 = lesser, 2 = Common, 3 = Greater, 4 = Grand, 5 = Black (for NPCs)
No requirements

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetActorSoulSizeString(akActor, sBlackSize) → String`

**Flags:** Global

Get actor soul size as string.
no requirements

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `sBlackSize` | `String` |  | `"lack"` |

### `GetActorValues(akActor, ActorValues, DArrays) → Float[]`

**Flags:** Global

return all actor values in ActorValueStrings to float array.
if DArrays == none, requires skse. Can pass in DynamicArrays form to not use skse.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `ActorValues` | `String[]` | ✓ |  |
| `DArrays` | `DynamicArrays` |  |  |

### `GetActorValuesFromFile(akActor, filePath) → Float[]`

**Flags:** Global

return actor values of akActor from actor values in file to float array.
can specify your own file path. Look at the structure of DbActorValues.txt to make another file.
requires skse and papyrusUtil.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `filePath` | `String` |  | `"ata/interface/DbMiscFunctions/DbActorValues.txt"` |

### `GetActorValueStrings(akActor, ActorValues, ActorValueStrings, DArrays) → String[]`

**Flags:** Global

same as GetActorValues but returns for instance "Health = 100.0" in string array instead of float values
if ActorValueStrings == none, uses ActorValues for strings.
can specify ActorValueStrings for translations. Indexes in ActorValues and ActorValuesStrings should match.
if DArrays == none, requires skse. Can pass in DynamicArrays form to not use skse.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `ActorValues` | `String[]` | ✓ |  |
| `ActorValueStrings` | `String[]` |  |  |
| `DArrays` | `DynamicArrays` |  |  |

### `GetActorValueStringsFromFile(akActor, filePath) → String[]`

**Flags:** Global

same as GetActorValuesFromFile but returns for instance "Health = 100.0" in string array instead of float values
can specify your own file path. Look at the structure of DbActorValues.txt to make another file.
requires skse and papyrusUtil.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `filePath` | `String` |  | `"ata/interface/DbMiscFunctions/DbActorValues.txt"` |

### `GetAllFloatsFromFile(FileContents, FilePath, RangeStart, RangeEnd, StartKey, EndKey, Default) → Float[]`

**Flags:** Global

Same as GetAllStringsFromFile but saves values as floats to float array
requires SKSE and PapyrusUtil

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileContents` | `String` |  | `""` |
| `FilePath` | `String` |  | `""` |
| `RangeStart` | `String` |  | `""` |
| `RangeEnd` | `String` |  | `""` |
| `StartKey` | `String` |  | `""` |
| `EndKey` | `String` |  | `""` |
| `Default` | `Float[]` |  |  |

### `GetAllIntsFromFile(FileContents, FilePath, RangeStart, RangeEnd, StartKey, EndKey, Default) → Int[]`

**Flags:** Global

Same as GetAllStringsFromFile but saves values as ints to int array
requires SKSE and PapyrusUtil

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileContents` | `String` |  | `""` |
| `FilePath` | `String` |  | `""` |
| `RangeStart` | `String` |  | `""` |
| `RangeEnd` | `String` |  | `""` |
| `StartKey` | `String` |  | `""` |
| `EndKey` | `String` |  | `""` |
| `Default` | `Int[]` |  |  |

### `GetAllKeysPressed() → Int[]`

**Flags:** Global

requires skse

### `GetAllStringsFromFile(FileContents, FilePath, RangeStart, RangeEnd, StartKey, EndKey, Default) → String[]`

**Flags:** Global

Save All strings in the StartKey and Endkey brackets, between the RangeStart and RangeEnd strings from the FileContents or FilePath to a string array.
Example: you have a file Data/interface/MyStrings.txt that contains:
MyStringA = [My String A]
MyStringB = [My String B]

String[] MyStrings = GetAllStringsFromFile("Data/interface/MyStrings.txt")
MyStrings[0] will equal "My String A" and MyStrings[1] will equal "My String B"

to specify a range to search you can do this:

File contains:

StringsA
MyStringA = [My String A]
MyStringB = [My String B]
StringsAEnd
MyStringC = [My String C]

Using String[] MyStrings = GetAllStringsFromFile(FilePath = "Data/interface/MyStrings.txt", "StringsA", "StringsAEnd")
Only My String A and My String B are saved to the array.

If RangeStart is "", starts search at the beginning of the file. If RangeEnd is "", stops searching at the end of the file.

requires SKSE and PapyrusUtil

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileContents` | `String` |  | `""` |
| `FilePath` | `String` |  | `""` |
| `RangeStart` | `String` |  | `""` |
| `RangeEnd` | `String` |  | `""` |
| `StartKey` | `String` |  | `""` |
| `EndKey` | `String` |  | `""` |
| `Default` | `String[]` |  |  |

### `GetAudioFormType(F) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `F` | `Form` | ✓ |  |

### `GetAudioFormTypeString(F, TypeStrings) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `F` | `Form` | ✓ |  |
| `TypeStrings` | `String[]` |  |  |

### `GetBaseActorValues(akActor, ActorValues, DArrays) → Float[]`

**Flags:** Global

return all Base actor values in ActorValueStrings to float array.
if DArrays == none, requires skse. Can pass in DynamicArrays form to not use skse.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `ActorValues` | `String[]` | ✓ |  |
| `DArrays` | `DynamicArrays` |  |  |

### `GetBaseActorValuesFromFile(akActor, filePath) → Float[]`

**Flags:** Global

return all Base actor values in file to float array.
can specify your own file path. Look at the structure of DbActorValues.txt to make another file.
requires skse and papyrusUtil.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `filePath` | `String` |  | `"ata/interface/DbMiscFunctions/DbActorValues.txt"` |

### `GetBaseActorValueStrings(akActor, ActorValues, ActorValueStrings, DArrays) → String[]`

**Flags:** Global

same as GetBaseActorValues but returns for instance "Health = 100.0" in string array instead of float values
if ActorValueStrings == none, uses ActorValues for strings.
can specify ActorValueStrings for translations. Indexes in ActorValues and ActorValuesStrings should match.
if DArrays == none, requires skse. Can pass in DynamicArrays form to not use skse.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `ActorValues` | `String[]` | ✓ |  |
| `ActorValueStrings` | `String[]` |  |  |
| `DArrays` | `DynamicArrays` |  |  |

### `GetBaseActorValueStringsFromFile(akActor, filePath) → String[]`

**Flags:** Global

same as GetBaseActorValuesFromFile but returns for instance "Health = 100.0" in string array instead of float values
can specify your own file path. Look at the structure of DbActorValues.txt to make another file.
requires skse and papyrusUtil.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `filePath` | `String` |  | `"ata/interface/DbMiscFunctions/DbActorValues.txt"` |

### `GetCharacterFormType(F) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `F` | `Form` | ✓ |  |

### `GetCharacterFormTypeString(F, TypeStrings) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `F` | `Form` | ✓ |  |
| `TypeStrings` | `String[]` |  |  |

### `GetFloatFromFile(StringKey, FileContents, FilePath, StartKey, EndKey, Default, StartIndex) → Float`

**Flags:** Global

same as GetStringFromFile but returns value as float.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `StringKey` | `String` | ✓ |  |
| `FileContents` | `String` |  | `""` |
| `FilePath` | `String` |  | `""` |
| `StartKey` | `String` |  | `""` |
| `EndKey` | `String` |  | `""` |
| `Default` | `Float` |  | `-1` |
| `StartIndex` | `Int` |  | `0` |

### `GetFormIDHex(akForm) → String`

**Flags:** Global

For convenience. Returns the akForm ID as a hex string.
requires SKSE.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `GetFormName(akForm, nullString, NoNameString) → String`

**Flags:** Global

requires skse. Get name of form. Checks for ObjectReference. return default nullString if not found.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `nullString` | `String` |  | `"ull"` |
| `NoNameString` | `String` |  | `"o name"` |

### `GetFormNames(akArray) → String[]`

**Flags:** Global

requires skse, put all Form names of Forms in akArray to a string array and return.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Form[]` | ✓ |  |

### `GetFormNamesFromList(akList) → String[]`

**Flags:** Global

requires skse, put all Form names of Forms in akList to a string array and return.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akList` | `Formlist` | ✓ |  |

### `GetFormTypeAll(F) → Int`

**Flags:** Global

includes all of the above form types

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `F` | `Form` | ✓ |  |

### `GetFormTypeString(Type, sFilePath) → String`

**Flags:** Global

requires skse and papyrusUtil. Get the string for a form type int.
Exampe: FormTypeToString(SomeMiscObj.GetType()) returns "Misc"
can specify another file other than "Data/interface/DbFormTypeStrings.txt" if desired

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Type` | `Int` | ✓ |  |
| `sFilePath` | `String` |  | `"ata/interface/DbMiscFunctions/DbFormTypeStrings.txt"` |

### `GetFormTypeStringAll(F, TypeStrings) → String`

**Flags:** Global

includes all of the above types.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `F` | `Form` | ✓ |  |
| `TypeStrings` | `String[]` |  |  |

### `GetGameActorSoulLevels() → Int[]`

**Flags:** Global

Get game settings for soul levels
no requirements

### `GetGameSoulLevelNames() → String[]`

**Flags:** Global

Get game setting soul level names
no requirements

### `GetIntFromFile(StringKey, FileContents, FilePath, StartKey, EndKey, Default, StartIndex) → Int`

**Flags:** Global

same as GetStringFromFile but returns value as int.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `StringKey` | `String` | ✓ |  |
| `FileContents` | `String` |  | `""` |
| `FilePath` | `String` |  | `""` |
| `StartKey` | `String` |  | `""` |
| `EndKey` | `String` |  | `""` |
| `Default` | `Int` |  | `-1` |
| `StartIndex` | `Int` |  | `0` |

### `GetInventoryItemFormType(F) → Int`

**Flags:** Global

for base item types in player inventory

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `F` | `Form` | ✓ |  |

### `GetInventoryItemFormTypeString(F, TypeStrings) → String`

**Flags:** Global

for base item types in player inventory

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `F` | `Form` | ✓ |  |
| `TypeStrings` | `String[]` |  |  |

### `GetItemFormType(F) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `F` | `Form` | ✓ |  |

### `GetItemFormTypeString(F, TypeStrings) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `F` | `Form` | ✓ |  |
| `TypeStrings` | `String[]` |  |  |

### `GetKeyCodeString(keyCode, sFilePath) → String`

**Flags:** Global

requires skse and papyrusUtil. Get the string for keycode.
Exampe: GetKeyCodeString(28) returns "Enter"
can specify another file other than "Data/interface/DbKeyCodeStrings.txt" if desired

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `keyCode` | `Int` | ✓ |  |
| `sFilePath` | `String` |  | `"ata/interface/DbMiscFunctions/DbKeyCodeStrings.txt"` |

### `GetKeyCodeStrings(keys, startBracket, endBracket, divider, includeInts) → String`

**Flags:** Global

requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `keys` | `Int[]` | ✓ |  |
| `startBracket` | `String` |  | `""` |
| `endBracket` | `String` |  | `""` |
| `divider` | `String` |  | `"n"` |
| `includeInts` | `Bool` |  | `true` |

### `GetKeyCodeStringsInRange(minKey, maxKey, startBracket, endBracket, includeInts) → String[]`

**Flags:** Global

requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `minKey` | `Int` |  | `1` |
| `maxKey` | `Int` |  | `281` |
| `startBracket` | `String` |  | `""` |
| `endBracket` | `String` |  | `""` |
| `includeInts` | `Bool` |  | `true` |

### `GetLoremipsum() → String`

**Flags:** Global

### `GetLoremipsumNoPunctuation() → String`

**Flags:** Global

### `GetMagicFormType(F) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `F` | `Form` | ✓ |  |

### `GetMagicFormTypeString(F, TypeStrings) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `F` | `Form` | ✓ |  |
| `TypeStrings` | `String[]` |  |  |

### `GetMiscFormType(F) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `F` | `Form` | ✓ |  |

### `GetMiscFormTypeString(F, TypeStrings) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `F` | `Form` | ✓ |  |
| `TypeStrings` | `String[]` |  |  |

### `GetModOriginFromHexID(FormID) → String`

**Flags:** Global

Return mod name string that the FormID comes from. e.g "Skyrim.esm"
assumes FormID is 8 digits long.
Requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FormID` | `String` | ✓ |  |

### `GetModOriginName(akForm) → String`

**Flags:** Global

Return mod name string that the akForm comes from. e.g "Skyrim.esm"
Requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `GetObjectRefNames(akArray) → String[]`

**Flags:** Global

requires skse, put all ObjectReference names of ObjectReferences in akArray to a string array and return.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `ObjectReference[]` | ✓ |  |

### `GetPositionsOfStringInStrings(s, ToFind, WholeWordsOnly) → Int[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `ToFind` | `String` | ✓ |  |
| `WholeWordsOnly` | `Bool` |  | `false` |

### `GetPscDataDefinitionsFromFile(SourceFilePath, NameType, Divider, StartIndex) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `SourceFilePath` | `String` | ✓ |  |
| `NameType` | `String` | ✓ |  |
| `Divider` | `String` |  | `"n"` |
| `StartIndex` | `Int` |  | `0` |

### `GetPscDataNamesFromFile(SourceFilePath, NameType, Divider, StartIndex) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `SourceFilePath` | `String` | ✓ |  |
| `NameType` | `String` | ✓ |  |
| `Divider` | `String` |  | `"n"` |
| `StartIndex` | `Int` |  | `0` |

### `GetPscEventDefinitionsFromFile(SourceFilePath, Divider, StartIndex) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `SourceFilePath` | `String` | ✓ |  |
| `Divider` | `String` |  | `"n"` |
| `StartIndex` | `Int` |  | `0` |

### `GetPscEventNamesFromFile(SourceFilePath, Divider, StartIndex) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `SourceFilePath` | `String` | ✓ |  |
| `Divider` | `String` |  | `"n"` |
| `StartIndex` | `Int` |  | `0` |

### `GetPscFunctionDefinitionsFromFile(SourceFilePath, Divider, StartIndex) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `SourceFilePath` | `String` | ✓ |  |
| `Divider` | `String` |  | `"n"` |
| `StartIndex` | `Int` |  | `0` |

### `GetPscFunctionNamesFromFile(SourceFilePath, Divider, StartIndex) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `SourceFilePath` | `String` | ✓ |  |
| `Divider` | `String` |  | `"n"` |
| `StartIndex` | `Int` |  | `0` |

### `GetRandomFormFromRef(Ref, TypeArrayFilter, ListFilter, TypeFilterHasType, akListHasForm) → Form`

**Flags:** Global

Get random form from Ref's inventory.
If TypeArrayFilter != none, filters for form types in TypeArrayFilter. If TypeFilterHasType == true (default) only allows for types in the TypeArrayFilter. If false, only allows for types NOT in the TypeArrayFilter.
If ListFilter != none, filters for base forms in the ListFilter formlist. If akListHasForm == true (default) only allows for forms in the ListFilter formlist. If false, only allows for forms NOT in the formlist.
Requires SKSE

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Ref` | `ObjectReference` | ✓ |  |
| `TypeArrayFilter` | `Int[]` |  |  |
| `ListFilter` | `Formlist` |  |  |
| `TypeFilterHasType` | `Bool` |  | `true` |
| `akListHasForm` | `Bool` |  | `true` |

### `GetRandomFormFromRefA(Ref, TypeArrayFilter, ListFilter, TypeFilterHasType, akListHasForm) → Form`

**Flags:** Global

Get random form from Ref's inventory.
If TypeArrayFilter != none, filters for form types in TypeArrayFilter. If TypeFilterHasType == true (default) only allows for types in the TypeArrayFilter. If false, only allows for types NOT in the TypeArrayFilter.
If ListFilter != none, filters for base forms in the ListFilter form array. If akListHasForm == true (default) only allows for forms in the ListFilter form array. If false, only allows for forms NOT in the form array.
Requires SKSE

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Ref` | `ObjectReference` | ✓ |  |
| `TypeArrayFilter` | `Int[]` |  |  |
| `ListFilter` | `Form[]` |  |  |
| `TypeFilterHasType` | `Bool` |  | `true` |
| `akListHasForm` | `Bool` |  | `true` |

### `GetRandomFormFromRefJ(Ref, TypeArrayFilter, JsonFilePath, ListKeyName, TypeFilterHasType, akListHasForm) → Form`

**Flags:** Global

Get random form from Ref's inventory.
If TypeArrayFilter != none, filters for form types in TypeArrayFilter. If TypeFilterHasType == true (default) only allows for types in the TypeArrayFilter. If false, only allows for types NOT in the TypeArrayFilter.
If JsonFilePath != none && ListKeyName != none, filters for base forms in the JsonUtil Formlist defined by the JsonFilePath and ListKeyName. If akListHasForm == true (default) only allows for forms in the JsonUtil Formlist. If false, only allows for forms NOT in the JsonUtil Formlist.
Requires SKSE and PapyrusUtil.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Ref` | `ObjectReference` | ✓ |  |
| `TypeArrayFilter` | `Int[]` |  |  |
| `JsonFilePath` | `String` |  | `""` |
| `ListKeyName` | `String` |  | `""` |
| `TypeFilterHasType` | `Bool` |  | `true` |
| `akListHasForm` | `Bool` |  | `true` |

### `GetRandomFormFromRefS(Ref, TypeArrayFilter, ObjKey, ListKeyName, TypeFilterHasType, akListHasForm) → Form`

**Flags:** Global

Get random form from Ref's inventory.
If TypeArrayFilter != none, filters for form types in TypeArrayFilter. If TypeFilterHasType == true (default) only allows for types in the TypeArrayFilter. If false, only allows for types NOT in the TypeArrayFilter.
If ListKeyName != none, filters for base forms in the StorageUtil Formlist defined by the ObjKey and ListKeyName. If akListHasForm == true (default) only allows for forms in the StorageUtil Formlist. If false, only allows for forms NOT in the StorageUtil Formlist.
Requires SKSE and PapyrusUtil

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Ref` | `ObjectReference` | ✓ |  |
| `TypeArrayFilter` | `Int[]` |  |  |
| `ObjKey` | `Form` |  |  |
| `ListKeyName` | `String` |  | `""` |
| `TypeFilterHasType` | `Bool` |  | `true` |
| `akListHasForm` | `Bool` |  | `true` |

### `GetRandomWordFromString(s, Divider) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `Divider` | `String` |  | `""` |

### `GetRandomWordsFromString(s, numOfWords, Divider) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `numOfWords` | `Int` | ✓ |  |
| `Divider` | `String` |  | `""` |

### `GetRandomWordsFromStringA(s, numOfWords, Divider) → String[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `numOfWords` | `Int` | ✓ |  |
| `Divider` | `String` |  | `""` |

### `GetRealMinutesPassed() → Float`

**Flags:** Global

### `GetRealSecondsPassed() → Float`

**Flags:** Global

### `GetScreenResolution() → Int[]`

**Flags:** Global

get the current screen resolution.
[0] = X or width
[1] = Y or height
requires skse

### `GetSKSEVersion() → String`

**Flags:** Global

requires skse

### `GetSkyrimVersion() → String`

**Flags:** Global

Get the Skyrim Version Text displayed in the journal menu, e.g "1.5.97"
The Journal Menu system page must be open for this function to work.
Use RegisterForMenu("Journal Menu") and the OnMenuOpen event to ensure the Journal Menu is open.
Or, if your mod has an MCM, this will work when the MCM Menu is open, so use OnConfigOpen in your MCM script.
requires skse

### `GetSkyrimVersionFullString() → String`

**Flags:** Global

Get the full Version Text displayed in the journal menu e.g "1.5.97.0.8 (SKSE64 2.0.20 rel 65)"
The Journal Menu system page must be open for this function to work.
Use RegisterForMenu("Journal Menu") and the OnMenuOpen event to ensure the Journal Menu is open.
Or, if your mod has an MCM, this will work when the MCM Menu is open, so use OnConfigOpen in your MCM script.
requires skse

### `GetSpecialEffectFormType(F) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `F` | `Form` | ✓ |  |

### `GetSpecialEffectFormTypeString(F, TypeStrings) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `F` | `Form` | ✓ |  |
| `TypeStrings` | `String[]` |  |  |

### `GetStringBetweenOuterCharacters(s, startIndex, leftChar, rightChar) → String`

**Flags:** Global

get the string between leftChar (outer) and rightChar (outer) strings.
leftChar and rightChar should both be a single character string.
For instance:
string sExample = "(A, (B + (C)), D)"
GetStringBetweenOuterCharacters(sExample) ;returns "A, (B + (C)), D"
GetStringBetweenOuterCharacters(sExample, 1) ;returns "B + (C)"
requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `startIndex` | `Int` |  | `0` |
| `leftChar` | `String` |  | `""` |
| `rightChar` | `String` |  | `""` |

### `GetStringFromFile(StringKey, FileContents, FilePath, StartKey, EndKey, Default, StartIndex) → String`

**Flags:** Global

GetStringFromFile get custom string from external file or string
finds first string between Startkey and EndKey after StringKey.
similar to localization but no need to worry about nesting strings in a translation file this way.
Example: Let's say you have a file Data/interface/MyStrings.txt that contains:
MyStringA = [My String A]
MyStringB = [My String B]

String MyStringB = GetStringFromFile("MyStringB", FilePath = "Data/interface/MyStrings.txt") ;Returns "My String B"

To search a string instead of a file path, you can do this:
String MyStrings
MyStrings = MiscUtil.ReadFromFile("Data/interface/MyStrings.txt")
String MyStringB = GetStringFromFile("MyStringB", MyStrings) ;Returns "My String B"
Note, if using the function this way, don't store MyStrings in the script, outside of events or functions. Storing large strings can cause CTD on game load.
If storing this way, be sure to clear the string by doing: MyStrings = "" after it's finished being used.

This method will be better for performance if you need to get a lot of strings from your file, as it won't use MiscUtil.ReadFromFile everytime you use the function.

StringKeys contained in the file must be unique.

If the stringkey wasn't found and the Default is "", it returns the stringKey
Let's say you have:
Some Custom Message = [Some Custome Message]
in the "Data/interface/MyStrings.txt file"

You can do:
debug.notification(GetStringFromFile("Some Custom Message", FilePath = "Data/interface/MyStrings.txt"))
And it will still show "Some Custom Message" if something went wrong and it wasn't found in the file
You can specify a default if you want something else to return if the stringKey wasn't found.

Requires PapyrusUtil && SKSE

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `StringKey` | `String` | ✓ |  |
| `FileContents` | `String` |  | `""` |
| `FilePath` | `String` |  | `""` |
| `StartKey` | `String` |  | `""` |
| `EndKey` | `String` |  | `""` |
| `Default` | `String` |  | `""` |
| `StartIndex` | `Int` |  | `0` |

### `GetStringIfNull(s, nullString) → String`

**Flags:** Global

no requirements.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `nullString` | `String` |  | `"ull"` |

### `GetWorldDataFormType(F) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `F` | `Form` | ✓ |  |

### `GetWorldDataFormTypeString(F, TypeStrings) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `F` | `Form` | ✓ |  |
| `TypeStrings` | `String[]` |  |  |

### `GetWorldObjectFormType(F) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `F` | `Form` | ✓ |  |

### `GetWorldObjectFormTypeString(F, TypeStrings) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `F` | `Form` | ✓ |  |
| `TypeStrings` | `String[]` |  |  |

### `HotkeysPressed(hotkeys, onlyTheseKeys) → Bool`

**Flags:** Global

Are all the hotkeys pressed?
If onlyTheseKeys is true, (default), returns true only if all the hotkeys are pressed and no other keys are pressed.
requires skse.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `hotkeys` | `Int[]` | ✓ |  |
| `onlyTheseKeys` | `Bool` |  | `true` |

### `HoursToMinutes(Hours) → Float`

**Flags:** Global

no requirements

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Hours` | `Float` | ✓ |  |

### `HoursToSeconds(Hours) → Float`

**Flags:** Global

no requirements

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Hours` | `Float` | ✓ |  |

### `IntAbs(i) → Int`

**Flags:** Global

returns absolute value of i.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `i` | `Int` | ✓ |  |

### `IntPow(x, y) → Int`

**Flags:** Global

like Math.Pow, calculates x to the y power, but uses only integers which is more accurate if not needing floats.
Only works for positive y values.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `x` | `Int` | ✓ |  |
| `y` | `Int` | ✓ |  |

### `IntSqrt(i) → Int`

**Flags:** Global

returns floor of the square root of i.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `i` | `Int` | ✓ |  |

### `IsActorMoving(akActor) → Bool`

**Flags:** Global

Returns true if akActor is moving.
I've only tested on NPC humanoid actors. Not sure if it works for other types.
No requirements

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `IsActorNPC(akActor) → Bool`

**Flags:** Global

Is the akActor an NPC?
no requirements

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `IsCharWhiteSpace(C) → Bool`

**Flags:** Global

returns true if the single character string C is whitespace.
requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `C` | `String` | ✓ |  |

### `IsFloatInRange(f, Min, Max) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `f` | `Float` | ✓ |  |
| `Min` | `Float` | ✓ |  |
| `Max` | `Float` | ✓ |  |

### `IsIndexInBlockComment(s, index, blockCommentStart, blockCommentEnd) → Bool`

**Flags:** Global

is the index in string s between a block comment start and end
requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `blockCommentStart` | `String` |  | `"/"` |
| `blockCommentEnd` | `String` |  | `";"` |

### `IsIntInRange(I, Min, Max) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `I` | `Int` | ✓ |  |
| `Min` | `Int` | ✓ |  |
| `Max` | `Int` | ✓ |  |

### `IsNumber(akString, AllowForDecimals, AllowNegativeNumbers) → Bool`

**Flags:** Global

Requires SKSE

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akString` | `String` | ✓ |  |
| `AllowForDecimals` | `Bool` |  | `true` |
| `AllowNegativeNumbers` | `Bool` |  | `true` |

### `IsStringIndexBetween(s, Index, StartKey, EndKey) → Bool`

**Flags:** Global

is the index in string s between the StartKey and EndKey.
Example:
String s = "() (Some String)"
Bool b = DbMiscFunctions.IsStringIndexBetween(s, 4, "(", ")") ;true
Bool bb = DbMiscFunctions.IsStringIndexBetween(s, 2, "(", ")") ;false
Bool bbb = DbMiscFunctions.IsStringIndexBetween(s, 0, "(", ")") ;false
requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `Index` | `Int` | ✓ |  |
| `StartKey` | `String` | ✓ |  |
| `EndKey` | `String` | ✓ |  |

### `JoinAllStrings(s1, s2, s3, s4, s5, s6, s7, s8, s9, s10, s11, s12, s13, s14, s15, s16, s17, s18, s19, s20, Divider) → String`

**Flags:** Global

Join all 20 strings into a single string seperated by the Divider
no requirements

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s1` | `String` |  | `""` |
| `s2` | `String` |  | `""` |
| `s3` | `String` |  | `""` |
| `s4` | `String` |  | `""` |
| `s5` | `String` |  | `""` |
| `s6` | `String` |  | `""` |
| `s7` | `String` |  | `""` |
| `s8` | `String` |  | `""` |
| `s9` | `String` |  | `""` |
| `s10` | `String` |  | `""` |
| `s11` | `String` |  | `""` |
| `s12` | `String` |  | `""` |
| `s13` | `String` |  | `""` |
| `s14` | `String` |  | `""` |
| `s15` | `String` |  | `""` |
| `s16` | `String` |  | `""` |
| `s17` | `String` |  | `""` |
| `s18` | `String` |  | `""` |
| `s19` | `String` |  | `""` |
| `s20` | `String` |  | `""` |
| `Divider` | `String` |  | `""` |

### `JoinFloatArray(akArray, Divider, IgnoreDuplicates) → String`

**Flags:** Global

If IgnoreDuplicates == true, only adds an element to the string once

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Float[]` | ✓ |  |
| `Divider` | `String` |  | `"|"` |
| `IgnoreDuplicates` | `Bool` |  | `false` |

### `JoinIntArray(akArray, Divider, IgnoreDuplicates) → String`

**Flags:** Global

If IgnoreDuplicates == true, only adds an element to the string once

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Int[]` | ✓ |  |
| `Divider` | `String` |  | `"|"` |
| `IgnoreDuplicates` | `Bool` |  | `false` |

### `JoinStringArray(akArray, Divider, IgnoreDuplicates) → String`

**Flags:** Global

Opposite of StringUtil.Split. Convert string array to single string, elements separated by divider.
If IgnoreDuplicates == true, only adds an element to the string once

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `String[]` | ✓ |  |
| `Divider` | `String` |  | `"|"` |
| `IgnoreDuplicates` | `Bool` |  | `false` |

### `JoinStrings(s1, s2, s3, s4, s5, s6, s7, s8, s9, s10, s11, s12, s13, s14, s15, s16, s17, s18, s19, s20, Divider) → String`

**Flags:** Global

Join up to 20 strings into a single string seperated by the Divider
Stops joining at the first empty "" string it finds.
no requirements

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s1` | `String` |  | `""` |
| `s2` | `String` |  | `""` |
| `s3` | `String` |  | `""` |
| `s4` | `String` |  | `""` |
| `s5` | `String` |  | `""` |
| `s6` | `String` |  | `""` |
| `s7` | `String` |  | `""` |
| `s8` | `String` |  | `""` |
| `s9` | `String` |  | `""` |
| `s10` | `String` |  | `""` |
| `s11` | `String` |  | `""` |
| `s12` | `String` |  | `""` |
| `s13` | `String` |  | `""` |
| `s14` | `String` |  | `""` |
| `s15` | `String` |  | `""` |
| `s16` | `String` |  | `""` |
| `s17` | `String` |  | `""` |
| `s18` | `String` |  | `""` |
| `s19` | `String` |  | `""` |
| `s20` | `String` |  | `""` |
| `Divider` | `String` |  | `""` |

### `JsonFloatListPluck(FileName, KeyName, index, default) → Float`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `default` | `Float` |  | `0` |

### `JsonFloatListPop(FileName, KeyName, default) → Float`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `default` | `Float` |  | `0` |

### `JsonFloatListRemoveAllDuplicates(FileName, KeyName, Acending)`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `Acending` | `Bool` |  | `true` |

### `JsonFloatListShift(FileName, KeyName, default) → Float`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `default` | `Float` |  | `0` |

### `JsonFormListPluck(FileName, KeyName, index, default) → Form`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `default` | `Form` |  |  |

### `JsonFormListPop(FileName, KeyName, default) → Form`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `default` | `Form` |  |  |

### `JsonFormListRemoveAllDuplicates(FileName, KeyName, Acending)`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `Acending` | `Bool` |  | `true` |

### `JsonFormListShift(FileName, KeyName, default) → Form`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `default` | `Form` |  |  |

### `JsonIntListPluck(FileName, KeyName, index, default) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `default` | `Int` |  | `0` |

### `JsonIntListPop(FileName, KeyName, default) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `default` | `Int` |  | `0` |

### `JsonIntListRemoveAllDuplicates(FileName, KeyName, Acending)`

**Flags:** Global

remove all duplicates in Json int / float / string / form lists, leaving only 1 of each element in the list.
If asceding == true (default) removes duplicate entries from the start of the list first,
Else removes duplicates from end of list first. ======================================================================================

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `Acending` | `Bool` |  | `true` |

### `JsonintListShift(FileName, KeyName, default) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `default` | `Int` |  | `0` |

### `JsonJoinFloatList(FileName, KeyName, Divider) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `Divider` | `String` |  | `"|"` |

### `JsonJoinIntList(FileName, KeyName, Divider) → String`

**Flags:** Global

Opposite of String.Split()
Join all elements in Json Int List to a single string seperated by divider. =================================================================================

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `Divider` | `String` |  | `"|"` |

### `JsonJoinStringList(FileName, KeyName, Divider) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `Divider` | `String` |  | `"|"` |

### `JsonStringListPluck(FileName, KeyName, index, default) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `default` | `String` |  | `""` |

### `JsonStringListPop(FileName, KeyName, default) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `default` | `String` |  | `""` |

### `JsonStringListRemoveAllDuplicates(FileName, KeyName, Acending)`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `Acending` | `Bool` |  | `true` |

### `JsonStringListShift(FileName, KeyName, default) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `default` | `String` |  | `""` |

### `LocationOrParentsHasKeyword(akLocation, akKeyword) → Bool`

**Flags:** Global

check if location or any of it's parents has the keyword
Requires Papyrus Extender && SKSE

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akLocation` | `Location` | ✓ |  |
| `akKeyword` | `Keyword` | ✓ |  |

### `MinutesToHours(minutes) → Float`

**Flags:** Global

no requirements

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `minutes` | `Float` | ✓ |  |

### `MinutesToSeconds(minutes) → Float`

**Flags:** Global

no requirements

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `minutes` | `Float` | ✓ |  |

### `ModActorValues(akActor, ActorValues, Values)`

**Flags:** Global

Mod all ActorValues with Values on akActor
no requirements

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `ActorValues` | `String[]` | ✓ |  |
| `Values` | `Float[]` | ✓ |  |

### `ModHasFormType(modName, type) → Bool`

**Flags:** Global

Returns true if the Mod has at least 1 form of type
Requires papyrus extender and skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `modName` | `String` | ✓ |  |
| `type` | `Int` | ✓ |  |

### `MoveToLocalOffset(RefToMove, CenterRef, Angle, Distance, afZOffset, abMatchRotation)`

**Flags:** Global

Like MoveTo, but can specifify local angle / distance offset.
If angle == 0.0, moves object in front of CenterRef by Distance
If angle == 90.0 moves object the right of CenterRef by Distance
If angle == -90, moves object to the left of centerRef  by Distance
If angle == 180, moves object behind centerRef by Distance ect.
Example: MoveToLocalOffset(MyRef, Game.GetPlayer(), 90.0, 500.0) moves MyRef 500 units to the right of the player.
No requirements

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `RefToMove` | `ObjectReference` | ✓ |  |
| `CenterRef` | `ObjectReference` | ✓ |  |
| `Angle` | `Float` | ✓ |  |
| `Distance` | `Float` | ✓ |  |
| `afZOffset` | `Float` |  | `0` |
| `abMatchRotation` | `Bool` |  | `true` |

### `OpenMenu(menuName)`

**Flags:** Global

menuName must match a valid menu name in UI.psc
requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |

### `PlaceAndMoveToLocalOffset(PlaceAtMeRef, akFormToPlace, aiCount, abForcePersist, abInitiallyDisabled, Angle, Distance, afZOffset, abMatchRotation) → ObjectReference`

**Flags:** Global

PlaceAtMe but moves placed ref using MoveToLocalOffset function. PlaceAtMeRef is centerRef, new placed ref is RefToMove.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `PlaceAtMeRef` | `ObjectReference` | ✓ |  |
| `akFormToPlace` | `Form` | ✓ |  |
| `aiCount` | `Int` |  | `1` |
| `abForcePersist` | `Bool` |  | `false` |
| `abInitiallyDisabled` | `Bool` |  | `false` |
| `Angle` | `Float` |  | `0` |
| `Distance` | `Float` |  | `100` |
| `afZOffset` | `Float` |  | `0` |
| `abMatchRotation` | `Bool` |  | `true` |

### `PrintContainerItemsToFile(akContainer, FilePath, ConfirmMessage)`

**Flags:** Global

Print all items in a container to the FilePath with the mod they come from included.
Requires SKSE

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akContainer` | `ObjectReference` | ✓ |  |
| `FilePath` | `String` | ✓ |  |
| `ConfirmMessage` | `String` |  | `""` |

### `PrintEvm(s1, s2, s3, s4, s5, s6, s7, s8, s9, s10, s11, s12, s13, s14, s15, s16, s17, s18, s19, s20, Divider)`

**Flags:** Global

ExtendedVanillaMenus.MessageBox
requires ExtendedVanillaMenus and skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s1` | `String` |  | `""` |
| `s2` | `String` |  | `""` |
| `s3` | `String` |  | `""` |
| `s4` | `String` |  | `""` |
| `s5` | `String` |  | `""` |
| `s6` | `String` |  | `""` |
| `s7` | `String` |  | `""` |
| `s8` | `String` |  | `""` |
| `s9` | `String` |  | `""` |
| `s10` | `String` |  | `""` |
| `s11` | `String` |  | `""` |
| `s12` | `String` |  | `""` |
| `s13` | `String` |  | `""` |
| `s14` | `String` |  | `""` |
| `s15` | `String` |  | `""` |
| `s16` | `String` |  | `""` |
| `s17` | `String` |  | `""` |
| `s18` | `String` |  | `""` |
| `s19` | `String` |  | `""` |
| `s20` | `String` |  | `""` |
| `Divider` | `String` |  | `""` |

### `PrintF(FilePath, s1, s2, s3, s4, s5, s6, s7, s8, s9, s10, s11, s12, s13, s14, s15, s16, s17, s18, s19, s20, Divider)`

**Flags:** Global

WriteToFile
requires PapyrusUtil and skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FilePath` | `String` | ✓ |  |
| `s1` | `String` |  | `""` |
| `s2` | `String` |  | `""` |
| `s3` | `String` |  | `""` |
| `s4` | `String` |  | `""` |
| `s5` | `String` |  | `""` |
| `s6` | `String` |  | `""` |
| `s7` | `String` |  | `""` |
| `s8` | `String` |  | `""` |
| `s9` | `String` |  | `""` |
| `s10` | `String` |  | `""` |
| `s11` | `String` |  | `""` |
| `s12` | `String` |  | `""` |
| `s13` | `String` |  | `""` |
| `s14` | `String` |  | `""` |
| `s15` | `String` |  | `""` |
| `s16` | `String` |  | `""` |
| `s17` | `String` |  | `""` |
| `s18` | `String` |  | `""` |
| `s19` | `String` |  | `""` |
| `s20` | `String` |  | `""` |
| `Divider` | `String` |  | `""` |

### `PrintM(s1, s2, s3, s4, s5, s6, s7, s8, s9, s10, s11, s12, s13, s14, s15, s16, s17, s18, s19, s20, Divider)`

**Flags:** Global

MessageBox
No requirements.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s1` | `String` |  | `""` |
| `s2` | `String` |  | `""` |
| `s3` | `String` |  | `""` |
| `s4` | `String` |  | `""` |
| `s5` | `String` |  | `""` |
| `s6` | `String` |  | `""` |
| `s7` | `String` |  | `""` |
| `s8` | `String` |  | `""` |
| `s9` | `String` |  | `""` |
| `s10` | `String` |  | `""` |
| `s11` | `String` |  | `""` |
| `s12` | `String` |  | `""` |
| `s13` | `String` |  | `""` |
| `s14` | `String` |  | `""` |
| `s15` | `String` |  | `""` |
| `s16` | `String` |  | `""` |
| `s17` | `String` |  | `""` |
| `s18` | `String` |  | `""` |
| `s19` | `String` |  | `""` |
| `s20` | `String` |  | `""` |
| `Divider` | `String` |  | `""` |

### `PrintN(s1, s2, s3, s4, s5, s6, s7, s8, s9, s10, s11, s12, s13, s14, s15, s16, s17, s18, s19, s20, Divider)`

**Flags:** Global

Notification
No requirements.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s1` | `String` |  | `""` |
| `s2` | `String` |  | `""` |
| `s3` | `String` |  | `""` |
| `s4` | `String` |  | `""` |
| `s5` | `String` |  | `""` |
| `s6` | `String` |  | `""` |
| `s7` | `String` |  | `""` |
| `s8` | `String` |  | `""` |
| `s9` | `String` |  | `""` |
| `s10` | `String` |  | `""` |
| `s11` | `String` |  | `""` |
| `s12` | `String` |  | `""` |
| `s13` | `String` |  | `""` |
| `s14` | `String` |  | `""` |
| `s15` | `String` |  | `""` |
| `s16` | `String` |  | `""` |
| `s17` | `String` |  | `""` |
| `s18` | `String` |  | `""` |
| `s19` | `String` |  | `""` |
| `s20` | `String` |  | `""` |
| `Divider` | `String` |  | `""` |

### `PrintStringKeysToFile(FilePathToSearch, FilePathToPrintTo, StartKey, EndKey, FinishedMsg) → Bool`

**Flags:** Global

PrintStringKeysToFile Finds and Prints all GetString / int / floatFromString StringKeys, from FilePathToSearch to FilePathToPrintTo
To speed up the process of making your StringKeys.txt file.
You can write GetStringFromFile() functions in your .psc file, and when you're finished with your script, have this function print the string keys to another .txt file.

Example, if in MyScript.psc you have:
Debug.Notification(GetStringFromFile("My Message A"))
Debug.Notification(GetStringFromFile("My Message B"))
Use the function:

PrintStringKeysToFile("Data/Scripts/Source/MyScript.psc", "Data/interface/MyStrings.txt")

In the MyStrings.txt file it will write:

"My Message A" = ["My Message A"]
"My Message B" = ["My Message B"]

note that the quotes are included. You'll want to get rid of them by pressing ctrl H in your text editor and replace all " with nothing so it looks like:

My Message A = [My Message A]
My Message B = [My Message B]

Otherwise the GetStringFromFile functions won't work correctly when reading from your .txt file.

Requires SKSE and PapyrusUtil

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FilePathToSearch` | `String` | ✓ |  |
| `FilePathToPrintTo` | `String` | ✓ |  |
| `StartKey` | `String` |  | `""` |
| `EndKey` | `String` |  | `""` |
| `FinishedMsg` | `String` |  | `"one Printing"` |

### `PrintT(s1, s2, s3, s4, s5, s6, s7, s8, s9, s10, s11, s12, s13, s14, s15, s16, s17, s18, s19, s20, Divider, aiSeverity)`

**Flags:** Global

Trace
No requirements.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s1` | `String` |  | `""` |
| `s2` | `String` |  | `""` |
| `s3` | `String` |  | `""` |
| `s4` | `String` |  | `""` |
| `s5` | `String` |  | `""` |
| `s6` | `String` |  | `""` |
| `s7` | `String` |  | `""` |
| `s8` | `String` |  | `""` |
| `s9` | `String` |  | `""` |
| `s10` | `String` |  | `""` |
| `s11` | `String` |  | `""` |
| `s12` | `String` |  | `""` |
| `s13` | `String` |  | `""` |
| `s14` | `String` |  | `""` |
| `s15` | `String` |  | `""` |
| `s16` | `String` |  | `""` |
| `s17` | `String` |  | `""` |
| `s18` | `String` |  | `""` |
| `s19` | `String` |  | `""` |
| `s20` | `String` |  | `""` |
| `Divider` | `String` |  | `""` |
| `aiSeverity` | `Int` |  | `0` |

### `PrintTU(asUserLog, s1, s2, s3, s4, s5, s6, s7, s8, s9, s10, s11, s12, s13, s14, s15, s16, s17, s18, s19, s20, Divider, aiSeverity)`

**Flags:** Global

TraceUser
No requirements.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asUserLog` | `String` |  | `""` |
| `s1` | `String` |  | `""` |
| `s2` | `String` |  | `""` |
| `s3` | `String` |  | `""` |
| `s4` | `String` |  | `""` |
| `s5` | `String` |  | `""` |
| `s6` | `String` |  | `""` |
| `s7` | `String` |  | `""` |
| `s8` | `String` |  | `""` |
| `s9` | `String` |  | `""` |
| `s10` | `String` |  | `""` |
| `s11` | `String` |  | `""` |
| `s12` | `String` |  | `""` |
| `s13` | `String` |  | `""` |
| `s14` | `String` |  | `""` |
| `s15` | `String` |  | `""` |
| `s16` | `String` |  | `""` |
| `s17` | `String` |  | `""` |
| `s18` | `String` |  | `""` |
| `s19` | `String` |  | `""` |
| `s20` | `String` |  | `""` |
| `Divider` | `String` |  | `""` |
| `aiSeverity` | `Int` |  | `0` |

### `refreshMenus()`

**Flags:** Global

used to update menus while they are open.
Example, changing a map marker property (name, icon or visibility) while the map menu is open

### `RegisterActiveMagicEffectForAnimationEvents(akActiveMagicEffect, akSender, AnimationEvents)`

**Flags:** Global

register the akActiveMagicEffect to recieve all AnimationEvents from the akSender
no requirements

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveMagicEffect` | `ActiveMagicEffect` | ✓ |  |
| `akSender` | `ObjectReference` | ✓ |  |
| `AnimationEvents` | `String[]` | ✓ |  |

### `RegisterActiveMagicEffectForAnimationEventsFromFile(akActiveMagicEffect, akSender, FilePath)`

**Flags:** Global

Register the akActiveMagicEffect to recieve all Animation Events from akSender found in File specified by FilePath.
Events in the file should be separated by new line.
Requires SKSE and PapyrusUtil

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveMagicEffect` | `ActiveMagicEffect` | ✓ |  |
| `akSender` | `ObjectReference` | ✓ |  |
| `FilePath` | `String` |  | `"ata/interface/DbMiscFunctions/DbAnimationEvents.txt"` |

### `RegisterActiveMagicEffectForKeys(akActiveMagicEffect, min, Max)`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveMagicEffect` | `ActiveMagicEffect` | ✓ |  |
| `min` | `Int` |  | `1` |
| `Max` | `Int` |  | `281` |

### `RegisterActiveMagicEffectForMenus(akActiveMagicEffect, Menus)`

**Flags:** Global

register the akActiveMagicEffect for all Menus in string array.
Requires SKSE

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveMagicEffect` | `ActiveMagicEffect` | ✓ |  |
| `Menus` | `String[]` | ✓ |  |

### `RegisterActiveMagicEffectForMenusFromFile(akActiveMagicEffect, FilePath)`

**Flags:** Global

Register the ActiveMagicEffect for all menus found in File specified by FilePath.
Events in the file should be separated by new line.
Requires SKSE and PapyrusUtil

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveMagicEffect` | `ActiveMagicEffect` | ✓ |  |
| `FilePath` | `String` |  | `"ata/interface/DbMiscFunctions/DbMenus.txt"` |

### `RegisterAliasForAnimationEvents(akAlias, akSender, AnimationEvents)`

**Flags:** Global

register the akAlias to recieve all AnimationEvents from the akSender
no requirements

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |
| `akSender` | `ObjectReference` | ✓ |  |
| `AnimationEvents` | `String[]` | ✓ |  |

### `RegisterAliasForAnimationEventsFromFile(akAlias, akSender, FilePath)`

**Flags:** Global

Register the akAlias to recieve all Animation Events from akSender found in File specified by FilePath.
Events in the file should be separated by new line.
Requires SKSE and PapyrusUtil

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |
| `akSender` | `ObjectReference` | ✓ |  |
| `FilePath` | `String` |  | `"ata/interface/DbMiscFunctions/DbAnimationEvents.txt"` |

### `RegisterAliasForKeys(akAlias, min, Max)`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |
| `min` | `Int` |  | `1` |
| `Max` | `Int` |  | `281` |

### `RegisterAliasForMenus(akAlias, Menus)`

**Flags:** Global

register the akAlias for all Menus in string array.
Requires SKSE

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |
| `Menus` | `String[]` | ✓ |  |

### `RegisterAliasForMenusFromFile(akAlias, FilePath)`

**Flags:** Global

Register the akAlias for all menus found in File specified by FilePath.
Events in the file should be separated by new line.
Requires SKSE and PapyrusUtil

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |
| `FilePath` | `String` |  | `"ata/interface/DbMiscFunctions/DbMenus.txt"` |

### `RegisterFormForAnimationEvents(akForm, akSender, AnimationEvents)`

**Flags:** Global

register the akForm to recieve all AnimationEvents from the akSender
no requirements

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `akSender` | `ObjectReference` | ✓ |  |
| `AnimationEvents` | `String[]` | ✓ |  |

### `RegisterFormForAnimationEventsFromFile(akForm, akSender, FilePath)`

**Flags:** Global

Register the akForm to recieve all Animation Events from akSender found in File specified by FilePath.
Events in the file should be separated by new line.
Requires SKSE and PapyrusUtil

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `akSender` | `ObjectReference` | ✓ |  |
| `FilePath` | `String` |  | `"ata/interface/DbMiscFunctions/DbAnimationEvents.txt"` |

### `RegisterFormForKeys(akForm, min, Max)`

**Flags:** Global

requires skse, register for all keys between min and max. Default is 1 to 281, or all keys.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `min` | `Int` |  | `1` |
| `Max` | `Int` |  | `281` |

### `RegisterFormForMenus(akForm, Menus)`

**Flags:** Global

register the akForm for all Menus in string array.
Requires SKSE

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `Menus` | `String[]` | ✓ |  |

### `RegisterFormForMenusFromFile(akForm, FilePath)`

**Flags:** Global

Register the akForm for all menus found in File specified by FilePath.
Events in the file should be separated by new line.
Requires SKSE and PapyrusUtil

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `FilePath` | `String` |  | `"ata/interface/DbMiscFunctions/DbMenus.txt"` |

### `RemoveAllDuplicateStrings(TargetStr, Divider, IncludeDividersInResult) → String`

**Flags:** Global

Remove All Duplicate strings separated by Divider. Example:
RemoveAllDuplicateStrings("TestString||TestString||Hmmm||TestString|| Test String ||Hmm||TestString") returns "TestString||Hmmm|| Test String ||"
RemoveAllDuplicateStrings("TestString||TestString||Hmmm||TestString|| Test String ||Hmm||TestString", IncludeDividersInResult = false) returns "TestStringHmmm Test String "
requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `TargetStr` | `String` | ✓ |  |
| `Divider` | `String` |  | `"|"` |
| `IncludeDividersInResult` | `Bool` |  | `true` |

### `RemoveAllItems(Ref, otherContainer, abSilent, delay, abNoEquipped, abNoFavorited, abNoQuestItem)`

**Flags:** Global

remove all items from ref which must be a container or actor to optional otherContainer
requires skse and papyrus extender and SSE
use Form[] Items = PO3_SKSEFunctions.AddAllInventoryItemsToArray(Ref, abNoEquipped, abNoFavorited, abNoQuestItem) for LE

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Ref` | `ObjectReference` | ✓ |  |
| `otherContainer` | `ObjectReference` |  |  |
| `abSilent` | `Bool` |  | `true` |
| `delay` | `Float` |  | `0.01` |
| `abNoEquipped` | `Bool` |  | `false` |
| `abNoFavorited` | `Bool` |  | `false` |
| `abNoQuestItem` | `Bool` |  | `true` |

### `RemoveDuplicateStrings(TargetStr, SearchStr) → String`

**Flags:** Global

remove all SearchStr instances from TragetStr except for the first instance
requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `TargetStr` | `String` | ✓ |  |
| `SearchStr` | `String` | ✓ |  |

### `RemovePrefixFromFormName(akForm, Prefix)`

**Flags:** Global

Remove prefix from akForm's name if it exists
Requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `Prefix` | `String` | ✓ |  |

### `RemovePrefixFromFormNames(akForms, Prefix, OnlyIfNotPresent)`

**Flags:** Global

Same as above but removes from all form names in array
Requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForms` | `Form[]` | ✓ |  |
| `Prefix` | `String` | ✓ |  |
| `OnlyIfNotPresent` | `Bool` |  | `true` |

### `RemovePrefixFromString(s, Prefix) → String`

**Flags:** Global

Remove prefix from string s, if it exists and return new string, or return s if not present
Requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `Prefix` | `String` | ✓ |  |

### `RemovePrefixFromStrings(s, Prefix) → String[]`

**Flags:** Global

Same as above but removes from all strings in array
Requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String[]` | ✓ |  |
| `Prefix` | `String` | ✓ |  |

### `RemoveSuffixFromFormName(akForm, Suffix)`

**Flags:** Global

Remove Suffix from akForm's name if it exists
Requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `Suffix` | `String` | ✓ |  |

### `RemoveSuffixFromFormNames(akForms, Suffix, OnlyIfNotPresent)`

**Flags:** Global

Same as above but removes from all form names in array
Requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForms` | `Form[]` | ✓ |  |
| `Suffix` | `String` | ✓ |  |
| `OnlyIfNotPresent` | `Bool` |  | `true` |

### `RemoveSuffixFromString(s, Suffix) → String`

**Flags:** Global

Remove suffix from string s, if it exists and return new string, or return s if not present
Requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `Suffix` | `String` | ✓ |  |

### `RemoveSuffixFromStrings(s, Suffix) → String[]`

**Flags:** Global

Same as above but removes from all strings in array
Requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String[]` | ✓ |  |
| `Suffix` | `String` | ✓ |  |

### `RemoveWhiteSpaces(s, IncludeSpaces, IncludeTabs, IncludeNewLines) → String`

**Flags:** Global

requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `IncludeSpaces` | `Bool` |  | `true` |
| `IncludeTabs` | `Bool` |  | `true` |
| `IncludeNewLines` | `Bool` |  | `true` |

### `RFindNextNonWhiteSpaceCharIndexInString(s, startIndex) → Int`

**Flags:** Global

same as above but in reverse
if startIndex is -1, startIndex is string length
requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `startIndex` | `Int` |  | `-1` |

### `RFindNextNonWhiteSpaceCharInString(s, startIndex) → String`

**Flags:** Global

same as above but in reverse
if startIndex is -1, startIndex is string length
requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `startIndex` | `Int` |  | `-1` |

### `RFindNextWhiteSpaceCharIndexInString(s, startIndex) → Int`

**Flags:** Global

same as above but in reverse
if startIndex is -1, startIndex is string length
requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `startIndex` | `Int` |  | `-1` |

### `RFindNextWhiteSpaceCharInString(s, startIndex) → String`

**Flags:** Global

same as above but in reverse
if startIndex is -1, startIndex is string length
requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `startIndex` | `Int` |  | `-1` |

### `RFindNextWordInString(s, startIndex) → String`

**Flags:** Global

same as above but in reverse
if startIndex is -1, startIndex is string length
requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `startIndex` | `Int` |  | `-1` |

### `RoundAsFloat(f) → Float`

**Flags:** Global

rounds the float input and returns float
5.4 returns 5.0
5.5 returns 6.0

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `f` | `Float` | ✓ |  |

### `RoundAsInt(f) → Int`

**Flags:** Global

rounds the float input and returns int
5.4 returns 5
5.5 returns 6

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `f` | `Float` | ✓ |  |

### `RoundDownToDec(f, DecimalPlaces) → Float`

**Flags:** Global

rounds the float down to the specified decimal places
Example:
RoundDownToDec(1.2345, 2) returns 1.23
RoundDownToDec(4.5678, 3) return 4.567
not 100% accurate, limited by string as float conversion.
Example: RoundDownToDec(100.78945, 2) returns 100.779999999

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `f` | `Float` | ✓ |  |
| `DecimalPlaces` | `Int` |  | `0` |

### `RoundDownToDecString(f, DecimalPlaces) → String`

**Flags:** Global

Same as RoundDownToDec but returns string instead of float. In this case RoundDownToDecString(100.78945, 2) returns "100.78"

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `f` | `Float` | ✓ |  |
| `DecimalPlaces` | `Int` |  | `0` |

### `SecondsToHours(seconds) → Float`

**Flags:** Global

no requirements

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `seconds` | `Float` | ✓ |  |

### `SecondsToMinutes(seconds) → Float`

**Flags:** Global

no requirements

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `seconds` | `Float` | ✓ |  |

### `SetActorValues(akActor, ActorValues, Values)`

**Flags:** Global

Set all ActorValues to Values on akActor
no requirements

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `ActorValues` | `String[]` | ✓ |  |
| `Values` | `Float[]` | ✓ |  |

### `sGetActorValueStrings(akActor, ActorValues, ActorValueStrings, Divider) → String`

**Flags:** Global

same as GetActorValueStrings but puts all values in a single string divided by Divider.
if ActorValueStrings == none, uses ActorValues for strings.
can specify ActorValueStrings for translations. Indexes in ActorValues and ActorValuesStrings should match.
no requirements

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `ActorValues` | `String[]` | ✓ |  |
| `ActorValueStrings` | `String[]` |  |  |
| `Divider` | `String` |  | `"|"` |

### `sGetActorValueStringsFromFile(akActor, Divider, filePath) → String`

**Flags:** Global

same as GetActorValueStringsFromFile but puts all the values in a single string seperated by Divider
can specify your own file path. Look at the structure of DbActorValues.txt to make another file.
requires skse and papyrusUtil.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `Divider` | `String` |  | `"|"` |
| `filePath` | `String` |  | `"ata/interface/DbMiscFunctions/DbActorValues.txt"` |

### `sGetBaseActorValueStrings(akActor, ActorValues, ActorValueStrings, Divider) → String`

**Flags:** Global

same as GetBaseActorValueStrings but puts all values in a single string divided by Divider.
if ActorValueStrings == none, uses ActorValues for strings.
can specify ActorValueStrings for translations. Indexes in ActorValues and ActorValuesStrings should match.
no requirements

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `ActorValues` | `String[]` | ✓ |  |
| `ActorValueStrings` | `String[]` |  |  |
| `Divider` | `String` |  | `"|"` |

### `sGetBaseActorValueStringsFromFile(akActor, Divider, filePath) → String`

**Flags:** Global

same as GetBaseActorValueStringsFromFile but puts all the values in a single string seperated by Divider
can specify your own file path. Look at the structure of DbActorValues.txt to make another file.
requires skse and papyrusUtil.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `Divider` | `String` |  | `"|"` |
| `filePath` | `String` |  | `"ata/interface/DbMiscFunctions/DbActorValues.txt"` |

### `SortActorArrayByName(akArray, Ascending)`

**Flags:** Global

requires skse. Sort Actors in akArray by their display name.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Actor[]` | ✓ |  |
| `Ascending` | `Bool` |  | `true` |

### `SortFormArrayByName(akArray, Ascending)`

**Flags:** Global

requires skse. Sort Forms in akArray by their name.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Form[]` | ✓ |  |
| `Ascending` | `Bool` |  | `true` |

### `SortObjectRefArrayByName(akArray, Ascending)`

**Flags:** Global

requires skse. Sort ObjectReferences in akArray by their display name.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `ObjectReference[]` | ✓ |  |
| `Ascending` | `Bool` |  | `true` |

### `SortStringArray(akArray, Ascending, Direct) → String[]`

**Flags:** Global

Sort========================================================================================
if Direct == true, sorts the passed in akArray directly
if Direct == false, Passed in array is unaffected and returns new array (that is akArray sorted).
if Direct == false requires skse to create the new array.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `String[]` | ✓ |  |
| `Ascending` | `Bool` |  | `true` |
| `Direct` | `Bool` |  | `true` |

### `SplitAsFloat(s, Max, Divider) → Float[]`

**Flags:** Global

requires SKSE. Splits string by divider and returns as float array.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `Max` | `Int` |  | `-1` |
| `Divider` | `String` |  | `"|"` |

### `SplitAsInt(s, Max, Divider) → Int[]`

**Flags:** Global

requires SKSE. Splits string by divider and returns as int array.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `Max` | `Int` |  | `-1` |
| `Divider` | `String` |  | `"|"` |

### `StringHasPrefix(s, Prefix) → Bool`

**Flags:** Global

Does the string have the Prefix?
Requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `Prefix` | `String` | ✓ |  |

### `StringHasSuffix(s, Suffix) → Bool`

**Flags:** Global

Does the string have the Suffix?
Requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `Suffix` | `String` | ✓ |  |

### `StringInsert(TargetStr, InsertStr, CharPosition) → String`

**Flags:** Global

insert the InsertStr to the TargetStr at the CharPosition and return new string.
if CharPosition == -1, appends the InsertStr to the end of TargetStr

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `TargetStr` | `String` | ✓ |  |
| `InsertStr` | `String` | ✓ |  |
| `CharPosition` | `Int` |  | `-1` |

### `StringRemoveCharAt(s, Index) → String`

**Flags:** Global

Remove a single character in String s at Index
Requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `Index` | `Int` | ✓ |  |

### `StringRemoveNonPrintableCharacters(s) → String`

**Flags:** Global

Remove Non printable characters from string
Requires skse.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |

### `StringRemovePrintableCharacters(s) → String`

**Flags:** Global

Remove Non printable characters from string
Requires skse.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |

### `StringReplace(TargetStr, SearchStr, ReplaceStr, Count, StartIndex) → String`

**Flags:** Global

Replace instances of the SearchStr with the ReplaceStr in the TargetStr
Default count = 0 which means replace all instances. Otherwise only replace the Count number.
Example:
String MyString = "A Yes, B Yes, C Yes"
String MyStringB = StringReplace(MyString, "Yes", "No")
String MyStringC = StringReplace(MyString, "Yes", "No", 2)
MyStringB == "A No, B No, C No"
MyStringC == "A No, B No, C Yes"
Requires SKSE

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `TargetStr` | `String` | ✓ |  |
| `SearchStr` | `String` | ✓ |  |
| `ReplaceStr` | `String` | ✓ |  |
| `Count` | `Int` |  | `0` |
| `StartIndex` | `Int` |  | `0` |

### `StringRFind(s, toFind) → Int`

**Flags:** Global

Find the last index of the string toFind in string s.
returns -1 if not found.
requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |
| `toFind` | `String` | ✓ |  |

### `SwapActors(akArray, IndexA, IndexB)`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Actor[]` | ✓ |  |
| `IndexA` | `Int` | ✓ |  |
| `IndexB` | `Int` | ✓ |  |

### `SwapActorsV(akArray, IndexA, IndexB)`

**Flags:** Global

validate indexes first.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Actor[]` | ✓ |  |
| `IndexA` | `Int` | ✓ |  |
| `IndexB` | `Int` | ✓ |  |

### `SwapBools(akArray, IndexA, IndexB)`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Bool[]` | ✓ |  |
| `IndexA` | `Int` | ✓ |  |
| `IndexB` | `Int` | ✓ |  |

### `SwapBoolsV(akArray, IndexA, IndexB)`

**Flags:** Global

validate indexes first.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Bool[]` | ✓ |  |
| `IndexA` | `Int` | ✓ |  |
| `IndexB` | `Int` | ✓ |  |

### `SwapEquipment(A, B)`

**Flags:** Global

requires SKSE and Papyrus Extender
swap all worn equipment between actor A and B.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `A` | `Actor` | ✓ |  |
| `B` | `Actor` | ✓ |  |

### `SwapFloats(akArray, IndexA, IndexB)`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Float[]` | ✓ |  |
| `IndexA` | `Int` | ✓ |  |
| `IndexB` | `Int` | ✓ |  |

### `SwapFloatsV(akArray, IndexA, IndexB)`

**Flags:** Global

validate indexes first.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Float[]` | ✓ |  |
| `IndexA` | `Int` | ✓ |  |
| `IndexB` | `Int` | ✓ |  |

### `SwapForms(akArray, IndexA, IndexB)`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Form[]` | ✓ |  |
| `IndexA` | `Int` | ✓ |  |
| `IndexB` | `Int` | ✓ |  |

### `SwapFormsV(akArray, IndexA, IndexB)`

**Flags:** Global

validate indexes first.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Form[]` | ✓ |  |
| `IndexA` | `Int` | ✓ |  |
| `IndexB` | `Int` | ✓ |  |

### `SwapInts(akArray, IndexA, IndexB)`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Int[]` | ✓ |  |
| `IndexA` | `Int` | ✓ |  |
| `IndexB` | `Int` | ✓ |  |

### `SwapIntsV(akArray, IndexA, IndexB)`

**Flags:** Global

validate indexes first.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Int[]` | ✓ |  |
| `IndexA` | `Int` | ✓ |  |
| `IndexB` | `Int` | ✓ |  |

### `SwapObjectReferences(akArray, IndexA, IndexB)`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `ObjectReference[]` | ✓ |  |
| `IndexA` | `Int` | ✓ |  |
| `IndexB` | `Int` | ✓ |  |

### `SwapObjectReferencesV(akArray, IndexA, IndexB)`

**Flags:** Global

validate indexes first.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `ObjectReference[]` | ✓ |  |
| `IndexA` | `Int` | ✓ |  |
| `IndexB` | `Int` | ✓ |  |

### `SwapStrings(akArray, IndexA, IndexB)`

**Flags:** Global

Swap==============================================================================
Swap the element at IndexA with the element at IndexB in the akArray
The V functions (V for validate) will first clamp the indexes between 0 and the last available index in the akArray first before swapping.
If you know for sure that the indexes are in bounds, use Swap as it's faster. If you don't know for sure, use SwapV or you might get none entries.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `String[]` | ✓ |  |
| `IndexA` | `Int` | ✓ |  |
| `IndexB` | `Int` | ✓ |  |

### `SwapStringsV(akArray, IndexA, IndexB)`

**Flags:** Global

validate indexes first.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `String[]` | ✓ |  |
| `IndexA` | `Int` | ✓ |  |
| `IndexB` | `Int` | ✓ |  |

### `ToggleCreationKitMarkers(ShowMarkers, MoveToRef) → Bool`

**Flags:** Global

show or hide creation kit markers in Game
If ShowMarkers == true, shows them, otherwise hides them.
If moveToRef == None (default), moves player to either whiterun or markarth fast travel markers.
Must move player to new area after changing bShowMarkers ini so cells reload to display markers.
no requirements

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ShowMarkers` | `Bool` |  | `true` |
| `MoveToRef` | `ObjectReference` |  |  |

### `UnequipAllItems(akActor, abPreventEquip, abSilent, delay)`

**Flags:** Global

unequip all items on actor with delay in between.
requires skse and po3's papyrus extender

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `abPreventEquip` | `Bool` |  | `false` |
| `abSilent` | `Bool` |  | `true` |
| `delay` | `Float` |  | `0.1` |

### `UnlockEquippedShout()`

**Flags:** Global

requires skse

### `UnlockShout(akShout)`

**Flags:** Global

add shout to player if necessary and unlock all words of power.
requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akShout` | `shout` | ✓ |  |

### `UpdateActor(akActor, akForm)`

**Flags:** Global

add and remove akForm from actor silently.
This is useful for instance after changing an actors speed via akActor.SetAv("SpeedMult", 110)
Or after changing armor model paths, or on the player after changing item names if in the inventory or container menu, will display visually the name change.
No requirements

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `akForm` | `Form` | ✓ |  |

### `WaitWhileKeyIsPressed(keyCode, secondsToWait) → Bool`

**Flags:** Global

wait while the keyCode is pressed.
If the key is released before the secondsToWait time has elapsed, returns false.
If the secondsToWait time elapses and the keyCode is still pressed, returns true.
Requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `keyCode` | `Int` | ✓ |  |
| `secondsToWait` | `Float` | ✓ |  |

### `WaitWhileKeyIsPressed_interval(keyCode, waitCount, waitInterval) → Bool`

**Flags:** Global

Like WaitWhileKeyIsPressed but this is an interval wait version.
Wait while the keyCode is pressed.
Returns true if the entire waitCount finishes.
Returns false if the key was released before the waitCount is finished.
The wait interval should be a small value (default is 0.01) for this function to be accurate.
Requires skse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `keyCode` | `Int` | ✓ |  |
| `waitCount` | `Int` | ✓ |  |
| `waitInterval` | `Float` |  | `0.01` |

### `WriteAllAnimationVariablesToFile(akRef, OutputFilePath, BoolVariablesSourceFilePath, IntVariablesSourceFilePath, FloatVariablesSourceFilePath)`

**Flags:** Global

Requires skse and papyrusutil

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `OutputFilePath` | `String` | ✓ |  |
| `BoolVariablesSourceFilePath` | `String` |  | `"ata/interface/DbMiscFunctions/DbAnimationVariableBools.txt"` |
| `IntVariablesSourceFilePath` | `String` |  | `"ata/interface/DbMiscFunctions/DbAnimationVariableInts.txt"` |
| `FloatVariablesSourceFilePath` | `String` |  | `"ata/interface/DbMiscFunctions/DbAnimationVariableFloats.txt"` |

### `WriteAllPscDataInFolderToFile(SearchFolderPath, TargetFilePath, Divider, DoneMessage)`

**Flags:** Global

Write all data info to TargetFilePath from all .psc files in SearchFolderPath. Writes ScriptNames, Function Names, Event Names, Function Definitions and Event Definitions.
requires skse and papyrus util.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `SearchFolderPath` | `String` | ✓ |  |
| `TargetFilePath` | `String` | ✓ |  |
| `Divider` | `String` |  | `"n"` |
| `DoneMessage` | `String` |  | `"one Writing"` |

### `WriteAnimationVariableBoolsToFile(akRef, OutputFilePath, VariablesSourceFilePath)`

**Flags:** Global

Write bool animation variables of akRef found in DbAnimationVariableBools.txt to OutputFilePath.
Can specify a different VariablesSourceFilePath if desired.
Default variables found in DbAnimationVariableBools.txt are from https://www.creationkit.com/index.php?title=List_of_Animation_Variables
Requires skse and papyrusutil

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `OutputFilePath` | `String` | ✓ |  |
| `VariablesSourceFilePath` | `String` |  | `"ata/interface/DbMiscFunctions/DbAnimationVariableBools.txt"` |

### `WriteAnimationVariableFloatsToFile(akRef, OutputFilePath, VariablesSourceFilePath)`

**Flags:** Global

Write Float animation variables of akRef found in DbAnimationVariableFloats.txt to OutputFilePath.
Can specify a different VariablesSourceFilePath if desired.
Default variables found in DbAnimationVariableFloats.txt are from https://www.creationkit.com/index.php?title=List_of_Animation_Variables
Requires skse and papyrusutil

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `OutputFilePath` | `String` | ✓ |  |
| `VariablesSourceFilePath` | `String` |  | `"ata/interface/DbMiscFunctions/DbAnimationVariableFloats.txt"` |

### `WriteAnimationVariableIntsToFile(akRef, OutputFilePath, VariablesSourceFilePath)`

**Flags:** Global

Write int animation variables of akRef found in DbAnimationVariableInts.txt to OutputFilePath.
Can specify a different VariablesSourceFilePath if desired.
Default variables found in DbAnimationVariableInts.txt are from https://www.creationkit.com/index.php?title=List_of_Animation_Variables
Requires skse and papyrusutil

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |
| `OutputFilePath` | `String` | ✓ |  |
| `VariablesSourceFilePath` | `String` |  | `"ata/interface/DbMiscFunctions/DbAnimationVariableInts.txt"` |

### `WriteIDsInFormArrayToFile(akList, FilePath, IncludeNames, ReplaceIdStartWith0x)`

**Flags:** Global

Write form ID's of forms in akList to file. If ReplaceIdStartWith0x == true (default), replaces first two characters of ID with 0x.
Requires skse and PapyrusUtil

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akList` | `Form[]` | ✓ |  |
| `FilePath` | `String` | ✓ |  |
| `IncludeNames` | `Bool` |  | `false` |
| `ReplaceIdStartWith0x` | `Bool` |  | `true` |

### `WriteIDsInFormListToFile(akList, FilePath, IncludeNames, ReplaceIdStartWith0x)`

**Flags:** Global

Write form ID's of forms in akList to file. If ReplaceIdStartWith0x == true (default), replaces first two characters of ID with 0x.
Requires skse and PapyrusUtil

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akList` | `Formlist` | ✓ |  |
| `FilePath` | `String` | ✓ |  |
| `IncludeNames` | `Bool` |  | `false` |
| `ReplaceIdStartWith0x` | `Bool` |  | `true` |

### `WriteIDsInJsonUtilListToFile(JsonFilePath, ListKeyName, FilePath, IncludeNames, ReplaceIdStartWith0x)`

**Flags:** Global

Write form ID's of forms in JsonUtil formlist to file. If ReplaceIdStartWith0x == true (default), replaces first two characters of ID with 0x.
Requires skse and PapyrusUtil

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `JsonFilePath` | `String` | ✓ |  |
| `ListKeyName` | `String` | ✓ |  |
| `FilePath` | `String` | ✓ |  |
| `IncludeNames` | `Bool` |  | `false` |
| `ReplaceIdStartWith0x` | `Bool` |  | `true` |

### `WriteIDsInStorageUtilListToFile(ObjKey, ListKeyName, FilePath, IncludeNames, ReplaceIdStartWith0x)`

**Flags:** Global

Write form ID's of forms in storageutil formlist to file. If ReplaceIdStartWith0x == true (default), replaces first two characters of ID with 0x.
Requires skse and PapyrusUtil

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `ListKeyName` | `String` | ✓ |  |
| `FilePath` | `String` | ✓ |  |
| `IncludeNames` | `Bool` |  | `false` |
| `ReplaceIdStartWith0x` | `Bool` |  | `true` |

### `WriteJsonSaveAndLoadFunctionsToFile(SourceFilePath, DestinationFilePath, GlobalVariablesToggle, FloatsToggle, StringsToggle, IntsToggle, BoolsToggle, GlobalVariableArraysToggle, FloatArraysToggle, StringArraysToggle, IntArraysToggle, BoolArraysToggle, Messages, ConfirmMessage, UsePropertiesAsDefaults)`

**Flags:** Global

Search the SournceFilePath for String / Int / Float / Global variables, if their toggles are enabled, (outside of any events or functions)
and write Json Save and Load functions to DestinationFilePath for said variables.
If DestinationFilePath == "" it will write the functions to the SourceFilePath.
Set Messages int to 0 to display ConfirmMessage notification when finished, set to 1 to display messagebox.
requires skse and PapyrusUtil

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `SourceFilePath` | `String` | ✓ |  |
| `DestinationFilePath` | `String` |  | `""` |
| `GlobalVariablesToggle` | `Bool` |  | `true` |
| `FloatsToggle` | `Bool` |  | `true` |
| `StringsToggle` | `Bool` |  | `true` |
| `IntsToggle` | `Bool` |  | `true` |
| `BoolsToggle` | `Bool` |  | `true` |
| `GlobalVariableArraysToggle` | `Bool` |  | `true` |
| `FloatArraysToggle` | `Bool` |  | `true` |
| `StringArraysToggle` | `Bool` |  | `true` |
| `IntArraysToggle` | `Bool` |  | `true` |
| `BoolArraysToggle` | `Bool` |  | `true` |
| `Messages` | `Int` |  | `0` |
| `ConfirmMessage` | `String` |  | `"one Writing Json Functions"` |
| `UsePropertiesAsDefaults` | `Bool` |  | `true` |

---

## Functions

### `GetFormTypeStrings(sFilePath, startIndex, endIndex) → String[]`

requires skse and papyrusUtil.
Get form type strings within a range.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sFilePath` | `String` |  | `"ata/interface/DbMiscFunctions/DbFormTypeStrings.txt"` |
| `startIndex` | `Int` |  | `0` |
| `endIndex` | `Int` |  | `134` |

### `MovePlayerTo(Ref)`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Ref` | `ObjectReference` | ✓ |  |

### `RFindInString() → Int`


---

## `DbMiscFunctionsSSE`

**Source:** `dylbills` (Dylbills Papyrus Functions) • **Flags:** Hidden

**Imports:** `DbMiscFunctions`

---

## Global Functions

### `GetAllFilesInFolder(directory, extension, FullPath) → String[]`

**Flags:** Global

Get all file paths in directory, including files in sub folders.
If Fullpath == true (default) get's full paths, e.g Data/Interface/MyFile.txt
Otherwise gets e.g MyFile.txt
Requires skse and papyrusUtil.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `directory` | `String` | ✓ |  |
| `extension` | `String` |  | `""` |
| `FullPath` | `Bool` |  | `true` |

### `GetAllFoldersInFolder(directory) → String[]`

**Flags:** Global

Get all folder paths in directory, including sub folders.
Requires skse and papyrusUtil.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `directory` | `String` | ✓ |  |

### `WriteAllFilePathsToFile(OutputFilePath, Directory, extension, FullPath, NullString, AllowDuplicates)`

**Flags:** Global

Write all of the file paths found in the Directory, including files in sub folders to the OutputFilePath.
If NullString != none, will only write file paths not found in the NullString
If AllowDuplicates == false (default), only writes file paths not already present in the OutputFilePath
Requires skse and PapyrusUtil

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `OutputFilePath` | `String` | ✓ |  |
| `Directory` | `String` | ✓ |  |
| `extension` | `String` |  | `""` |
| `FullPath` | `Bool` |  | `true` |
| `NullString` | `String` |  | `""` |
| `AllowDuplicates` | `Bool` |  | `false` |

### `WriteAllFolderPathsToFile(OutputFilePath, Directory, NullString, AllowDuplicates)`

**Flags:** Global

Write all of the Folder paths found in the Directory, including sub folders, to the OutputFolderPath.
If NullString != none, will only write Folder paths not found in the NullString
If AllowDuplicates == false (default), only writes Folder paths not already present in the OutputFolderPath
Requires skse and PapyrusUtil

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `OutputFilePath` | `String` | ✓ |  |
| `Directory` | `String` | ✓ |  |
| `NullString` | `String` |  | `""` |
| `AllowDuplicates` | `Bool` |  | `false` |


---

## `DbSkseEvents`

**Source:** `dylbills` (Dylbills Papyrus Functions) • **Flags:** Hidden

---

## Events

### `OnActivateGlobal(ActivatorRef, ActivatedRef)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `ActivatorRef` | `ObjectReference` |
| `ActivatedRef` | `ObjectReference` |

### `OnActorFootStepGlobal(akActor, type)`

**Kind:** Event

type can be "FootLeft", "FootRight", "FootSprintLeft", "FootSprintRight", "JumpUp", "JumpDown" ect.

**Parameters**

| Name | Type |
|---|---|
| `akActor` | `Actor` |
| `type` | `String` |

### `OnActorSpellCastGlobal(Caster, Source, slot)`

**Kind:** Event

Actor Action events. Source is the weapon / spell / shout.
Slots are 0 = left hand, 1 = right hand, 2 = voice / power.

**Parameters**

| Name | Type |
|---|---|
| `Caster` | `Actor` |
| `Source` | `Form` |
| `slot` | `Int` |

### `OnActorSpellFireGlobal(Caster, Source, slot)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `Caster` | `Actor` |
| `Source` | `Form` |
| `slot` | `Int` |

### `OnBeginDrawGlobal(akActor, Source, slot)`

**Kind:** Event

for draw / sheathe events, they are always sent for the right hand.
Left hand events are only sent if there's something in the left hand, i.e spell / weapon / shield ect.

**Parameters**

| Name | Type |
|---|---|
| `akActor` | `Actor` |
| `Source` | `Form` |
| `slot` | `Int` |

### `OnBeginSheatheGlobal(akActor, Source, slot)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akActor` | `Actor` |
| `Source` | `Form` |
| `slot` | `Int` |

### `OnBowDrawGlobal(akActor, Source, slot)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akActor` | `Actor` |
| `Source` | `Form` |
| `slot` | `Int` |

### `OnBowReleaseGlobal(akActor, Source, slot)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akActor` | `Actor` |
| `Source` | `Form` |
| `slot` | `Int` |

### `OnCloseGlobal(ActivatorRef, akActionRef)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `ActivatorRef` | `ObjectReference` |
| `akActionRef` | `ObjectReference` |

### `OnCombatStateChangedGlobal(akActor, akTarget, aeCombatState)`

**Kind:** Event

note that this will work on the player for most cases. Exception being if stopCombat is called on the player with a script.
internally this checks the player's combat status whenever another actor changes combat status
and sends the event if the player combat status has changed, if the player is registered for akActor.

**Parameters**

| Name | Type |
|---|---|
| `akActor` | `Actor` |
| `akTarget` | `Actor` |
| `aeCombatState` | `Int` |

### `OnContainerChangedGlobal(newContainer, oldContainer, itemReference, baseObj, itemCount)`

**Kind:** Event

Note that this can be used as ItemAdded or ItemRemoved event.

**Parameters**

| Name | Type |
|---|---|
| `newContainer` | `ObjectReference` |
| `oldContainer` | `ObjectReference` |
| `itemReference` | `ObjectReference` |
| `baseObj` | `Form` |
| `itemCount` | `Int` |

### `OnDeathGlobal(Victim, Killer)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `Victim` | `Actor` |
| `Killer` | `Actor` |

### `OnDestructionStageChangedGlobal(ref, oldStage, newStage)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `ref` | `ObjectReference` |
| `oldStage` | `Int` |
| `newStage` | `Int` |

### `OnDyingGlobal(Victim, Killer)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `Victim` | `Actor` |
| `Killer` | `Actor` |

### `OnEffectFinishGlobal(Caster, Target, akEffect, source, castingSource, elapsedSeconds, elapsedGameHours)`

**Kind:** Event

Like ActiveMagicEffect OnEffectFinish
Triggers when the akEffect becomes inactive or finishes on the Target.
Source could be a spell, enchantment, potion, ingredient ect. The magic item that applied the akEffect.
For castingSource:
;LeftHand = 0,
RightHand = 1,
Other = 2, (most likely shout)
Instant = 3
elapsedSeconds is that amount real time seconds since the effect was last started on the Target. Time spent in menus is counted.
elapsedGameHours is the amount of game hours elapsed since the effect was last started on the Target.

**Parameters**

| Name | Type |
|---|---|
| `Caster` | `Actor` |
| `Target` | `Actor` |
| `akEffect` | `MagicEffect` |
| `source` | `form` |
| `castingSource` | `Int` |
| `elapsedSeconds` | `Float` |
| `elapsedGameHours` | `Float` |

### `OnEffectStartGlobal(Caster, Target, akEffect, source, castingSource)`

**Kind:** Event

Like ActiveMagicEffect OnEffectStart
Triggers when the akEffect becomes active on the Target, meaning the conditions for the effect or spell evaluate to true.
Source could be a spell, enchantment, potion, ingredient ect. The magic item that applied the akEffect.
For castingSource:
;LeftHand = 0,
RightHand = 1,
Other = 2, (most likely shout)
Instant = 3

**Parameters**

| Name | Type |
|---|---|
| `Caster` | `Actor` |
| `Target` | `Actor` |
| `akEffect` | `MagicEffect` |
| `source` | `form` |
| `castingSource` | `Int` |

### `OnEndDrawGlobal(akActor, Source, slot)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akActor` | `Actor` |
| `Source` | `Form` |
| `slot` | `Int` |

### `OnEndSheatheGlobal(akActor, Source, slot)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akActor` | `Actor` |
| `Source` | `Form` |
| `slot` | `Int` |

### `OnEnterBleedoutGlobal(Victim)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `Victim` | `Actor` |

### `OnEnterRange(akTarget, akCenterRef, distance)`

**Kind:** Event

Triggered when the akTarget enters the previously registered distance range of akCenterRef.
Distance is the distance that was registered, not the current distance from akTarget to akCenterRef.
Use akTarget.GetDistance(akCenterRef) for current distance.
This is because this event uses polling and not triggered immediately when the target enters the range.
Polling interval determined by the fEventPollingInterval setting in Data/SKSE/Plugins/DbSkseFunctions.ini

**Parameters**

| Name | Type |
|---|---|
| `akTarget` | `ObjectReference` |
| `akCenterRef` | `ObjectReference` |
| `distance` | `Float` |

### `OnFurnitureEnterGlobal(akActor, furnitureRef)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akActor` | `Actor` |
| `furnitureRef` | `ObjectReference` |

### `OnFurnitureExitGlobal(akActor, furnitureRef)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akActor` | `Actor` |
| `furnitureRef` | `ObjectReference` |

### `OnHitGlobal(Attacker, Target, Source, akAmmo, akProjectile, abPowerAttack, abSneakAttack, abBashAttack, abHitBlocked)`

**Kind:** Event

note that OnHitGlobal sends Ammo as well as projectile.
This is because the projectile in this event is bugged, it doesn't detect reliably.
This sends the Ammo the attacker has equipped if the Source is a bow or crossbow.

**Parameters**

| Name | Type |
|---|---|
| `Attacker` | `ObjectReference` |
| `Target` | `ObjectReference` |
| `Source` | `Form` |
| `akAmmo` | `Ammo` |
| `akProjectile` | `Projectile` |
| `abPowerAttack` | `Bool` |
| `abSneakAttack` | `Bool` |
| `abBashAttack` | `Bool` |
| `abHitBlocked` | `Bool` |

### `OnItemCraftedGlobal(itemCrafted, benchRef, count, workBenchType, benchSkill)`

**Kind:** Event

workbench types are:
None = 0,
CreateObject = 1,
SmithingWeapon = 2,
Enchanting = 3,
EnchantingExperiment = 4,
Alchemy = 5,
AlchemyExperiment = 6,
SmithingArmor = 7
benchSkill will be an actor value such as "smithing", "enchanting" ect.

**Parameters**

| Name | Type |
|---|---|
| `itemCrafted` | `Form` |
| `benchRef` | `ObjectReference` |
| `count` | `Int` |
| `workBenchType` | `Int` |
| `benchSkill` | `String` |

### `OnItemsPickpocketedGlobal(akTarget, itemTaken, count)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akTarget` | `Actor` |
| `itemTaken` | `Form` |
| `count` | `Int` |

### `OnLeaveRange(akTarget, akCenterRef, distance)`

**Kind:** Event

Triggered when the akTarget leaves previously registered distance range of akCenterRef.
Distance is the distance that was registered, not the current distance from akTarget to akCenterRef.
Use akTarget.GetDistance(akCenterRef) for current distance.
This is because this event uses polling and not triggered immediatly when the target enters the range.
Polling interval determined by the fEventPollingInterval setting in Data/SKSE/Plugins/DbSkseFunctions.ini

**Parameters**

| Name | Type |
|---|---|
| `akTarget` | `ObjectReference` |
| `akCenterRef` | `ObjectReference` |
| `distance` | `Float` |

### `OnLoadGameGlobal()`

**Kind:** Event

events ===========================================================================================================================================================================
as of version 6.3 OnLoadGameGlobal doesn't have to be registered for, it is always sent.

### `OnLocationClearedGlobal(akLocation)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akLocation` | `Location` |

### `OnLockChangedGlobal(akReference, Locked)`

**Kind:** Event

event sent when a reference is locked or unlocked

**Parameters**

| Name | Type |
|---|---|
| `akReference` | `ObjectReference` |
| `Locked` | `Bool` |

### `OnMagicEffectAppliedGlobal(Caster, Target, akEffect)`

**Kind:** Event

Like OnMagicEffectApplied. Is triggered when the magicEffect is added to a target.
Is trigged regardless if the MagicEffect or spell conditions evaluate to true.

**Parameters**

| Name | Type |
|---|---|
| `Caster` | `ObjectReference` |
| `Target` | `ObjectReference` |
| `akEffect` | `MagicEffect` |

### `OnMusicTypeChangeGlobal(newMusicType, oldMusicType)`

**Kind:** Event

Uses frame update function to detect change. Interval determined by the iFrameUpdateInterval setting in Data/SKSE/Plugins/DbSkseFunctions.ini

**Parameters**

| Name | Type |
|---|---|
| `newMusicType` | `MusicType` |
| `oldMusicType` | `MusicType` |

### `OnObjectEquippedGlobal(akActor, akBaseObject, akReference)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akActor` | `Actor` |
| `akBaseObject` | `Form` |
| `akReference` | `ObjectReference` |

### `OnObjectUnequippedGlobal(akActor, akBaseObject, akReference)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akActor` | `Actor` |
| `akBaseObject` | `Form` |
| `akReference` | `ObjectReference` |

### `OnOpenGlobal(ActivatorRef, akActionRef)`

**Kind:** Event

Open and close events are for animated doors / gates, use OnActivateGlobal for more general purposes.

**Parameters**

| Name | Type |
|---|---|
| `ActivatorRef` | `ObjectReference` |
| `akActionRef` | `ObjectReference` |

### `OnPackageChangeGlobal(akActor, akPackage)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akActor` | `Actor` |
| `akPackage` | `Package` |

### `OnPackageEndGlobal(akActor, akPackage)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akActor` | `Actor` |
| `akPackage` | `Package` |

### `OnPackageStartGlobal(akActor, akPackage)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akActor` | `Actor` |
| `akPackage` | `Package` |

### `OnPerkEntryRunGlobal(akPerk, akTarget, owner, flag)`

**Kind:** Event

This triggers the same as adding a script fragment to a perk entry point in the creation kit.
Not sure what the flag is for, but it's in the TESPerkEntryRunEvent so I included it here.

**Parameters**

| Name | Type |
|---|---|
| `akPerk` | `perk` |
| `akTarget` | `ObjectReference` |
| `owner` | `ObjectReference` |
| `flag` | `Int` |

### `OnPlayerChangeCellGlobal(akNewCell, akPreviousCell)`

**Kind:** Event

Event triggered when the player moves from one cell to another
Note that if the akPreviousCell is unloaded it will be none.
This happens when the player fast travels to a new worldspace
or when the player moves outside of the current cell grid and the previous cell is unloaded.

**Parameters**

| Name | Type |
|---|---|
| `akNewCell` | `Cell` |
| `akPreviousCell` | `Cell` |

### `OnPositionPlayerFinishGlobal(fastTravelMarker, moveToRef, akWorldSpace, akInteriorCell)`

**Kind:** Event

OnPositionPlayerFinish is after the game is done loading when the player moves into a new space.
The parameters here will be the same from the last OnPositionPlayerStart event.

**Parameters**

| Name | Type |
|---|---|
| `fastTravelMarker` | `ObjectReference` |
| `moveToRef` | `ObjectReference` |
| `akWorldSpace` | `WorldSpace` |
| `akInteriorCell` | `Cell` |

### `OnPositionPlayerStartGlobal(fastTravelMarker, moveToRef, akWorldSpace, akInteriorCell)`

**Kind:** Event

Position Player events are triggered whenever the player enters a new space, i.e fast traveling, going through a load door or a script calling MoveTo on the player.
OnPositionPlayerStart is right before the Loading Menu opens.
If the player is moving to an exterior the akWorldSpace will exist but the akInteriorCell will not and vice versa for moving to an interior.
The fastTravelMarker will only exist if the player fast travels or Game.FastTravel(objectReference destination) is called
The moveToRef will only exist if moveTo is called on the player (can be from a papyrus script or console command), ie Game.GetPlayer().moveTo(ref)
Also note that the parameters for these PositionPlayer events only work on Skyrim SE and AE, not VR.
The events will be sent on VR but the parameters will all be none

**Parameters**

| Name | Type |
|---|---|
| `fastTravelMarker` | `ObjectReference` |
| `moveToRef` | `ObjectReference` |
| `akWorldSpace` | `WorldSpace` |
| `akInteriorCell` | `Cell` |

### `OnProjectileImpactGlobal(shooter, target, Source, ammoSource, akProjectile, abSneakAttack, abHitBlocked, impactResult, collidedLayer, distanceTraveled, damagedNodeName, projectileMarker, projectileHitTranslation)`

**Kind:** Event

impactResults are: 0 = none, 1 = destroy, 2 = bounce, 3 = impale, 4 = stick
for collided layer see DbSkseFunctions.GetCollisionLayerName()
projectileMarker is an xMarker that is placed at the projectile at the point of impact so you can use functions
GetPosition, GetAngle and GetHeadingAngle to compare with the target
damagedNodeName only works on actors. e.g "SHIELD", "NPC Head [Head]", "NPC R UpperArm [RUar]" ect.
projectileHitTranslation is only valid for actors. projectileHitTranslation.length will be 6 if the data is valid.
[0] = Xposition, [1] = Yposition, [2] = Zposition,
[3] = XhitDirection, [4] = YhitDirection, [5] = ZhitDirection
this event requires the iMaxArrowsSavedPerReference setting in DbSkseFunctions.ini to be greater than 0.

**Parameters**

| Name | Type |
|---|---|
| `shooter` | `ObjectReference` |
| `target` | `ObjectReference` |
| `Source` | `Form` |
| `ammoSource` | `Ammo` |
| `akProjectile` | `Projectile` |
| `abSneakAttack` | `Bool` |
| `abHitBlocked` | `Bool` |
| `impactResult` | `Int` |
| `collidedLayer` | `Int` |
| `distanceTraveled` | `Float` |
| `damagedNodeName` | `String` |
| `projectileMarker` | `ObjectReference` |
| `projectileHitTranslation` | `Float[]` |

### `OnQuestObjectiveStateChangedGlobal(akQuest, displayText, oldState, newState, objectiveIndex, ojbectiveAliases)`

**Kind:** Event

states for oldState and newState are:
Dormant = 0,
Displayed = 1,
Completed = 2,
CompletedDisplayed = 3,
Failed = 4,
FailedDisplayed = 5

**Parameters**

| Name | Type |
|---|---|
| `akQuest` | `Quest` |
| `displayText` | `String` |
| `oldState` | `Int` |
| `newState` | `Int` |
| `objectiveIndex` | `Int` |
| `ojbectiveAliases` | `alias[]` |

### `OnRaceSwitchCompleteGlobal(akActor, akOldRace, akNewRace)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akActor` | `Actor` |
| `akOldRace` | `Race` |
| `akNewRace` | `Race` |

### `OnSpellCastGlobal(Caster, Source)`

**Kind:** Event

Event sent when an ObjectReference casts a spell. Source could be a spell, enchantment, potion or ingredient.

**Parameters**

| Name | Type |
|---|---|
| `Caster` | `ObjectReference` |
| `Source` | `Form` |

### `OnTranslationAlmostCompleteGlobal(ref)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `ref` | `ObjectReference` |

### `OnTranslationCompleteGlobal(ref)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `ref` | `ObjectReference` |

### `OnTranslationFailedGlobal(ref)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `ref` | `ObjectReference` |

### `OnTriggerEnterGlobal(akTriggerBox, akTarget)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akTriggerBox` | `ObjectReference` |
| `akTarget` | `ObjectReference` |

### `OnTriggerLeaveGlobal(akTriggerBox, akTarget)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akTriggerBox` | `ObjectReference` |
| `akTarget` | `ObjectReference` |

### `OnUiItemMenuEvent(menuName, akSelectedForm, eventType, count, playerInventory, stolen)`

**Kind:** Event

menuName is the item menu that's currently open.
akSelectedForm can be none if nothing is currently selected / highlighted.
eventTypes are: -1 = all types, 0 = selection changed, 1 = item selected, 2 = r button (drop, take all ect), 3 = f button (favorite ect).
count is the count of the item selected or highlighted, this is before the ui event is processed.
So for example if the inventory menu is open and eventType is 2 (r button for dropped) to get the number of items dropped use: int dropped items = (count - Game.GetPlayer().GetItemCount(akSelectedForm))
playerInventory is if the item was selected from the player's inventory and not the open container in the case of container menu, barter menu, gift menu ect.

**Parameters**

| Name | Type |
|---|---|
| `menuName` | `String` |
| `akSelectedForm` | `Form` |
| `eventType` | `Int` |
| `count` | `Int` |
| `playerInventory` | `Bool` |
| `stolen` | `Bool` |

### `OnVoiceCastGlobal(Caster, Source, slot)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `Caster` | `Actor` |
| `Source` | `Form` |
| `slot` | `Int` |

### `OnVoiceFireGlobal(Caster, Source, slot)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `Caster` | `Actor` |
| `Source` | `Form` |
| `slot` | `Int` |

### `OnWaitStartGlobal()`

**Kind:** Event

### `OnWaitStopGlobal(interrupted)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `interrupted` | `Bool` |

### `OnWeaponSwingGlobal(akActor, Source, slot)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akActor` | `Actor` |
| `Source` | `Form` |
| `slot` | `Int` |

### `OnWeatherChangeGlobal(newWeather, oldWeather)`

**Kind:** Event

Be aware that weather changes when going in and out of the map menu.
Uses frame update function to detect change. Interval determined by the iFrameUpdateInterval setting in Data/SKSE/Plugins/DbSkseFunctions.ini

**Parameters**

| Name | Type |
|---|---|
| `newWeather` | `weather` |
| `oldWeather` | `weather` |

---

## Global Functions

### `GetNumRangeEventsRegisteredOnActiveMagicEffect(eventReceiver) → Int`

**Flags:** Native Global

ActiveMagicEffect ===================================================================================================================================================================================================================================================================================

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `ActiveMagicEffect` | ✓ |  |

### `GetNumRangeEventsRegisteredOnAlias(eventReceiver) → Int`

**Flags:** Native Global

Alias ===================================================================================================================================================================================================================================================================================

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Alias` | ✓ |  |

### `GetNumRangeEventsRegisteredOnForm(eventReceiver) → Int`

**Flags:** Native Global

form ===================================================================================================================================================================================================================================================================================

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Form` | ✓ |  |

### `IsActiveMagicEffectRegisteredForGlobalEvent(asEvent, eventReceiver, paramFilter, paramFilterIndex) → Bool`

**Flags:** Native Global

ActiveMagicEffect ===============================================================================================================================================================

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asEvent` | `String` | ✓ |  |
| `eventReceiver` | `ActiveMagicEffect` | ✓ |  |
| `paramFilter` | `Form` |  |  |
| `paramFilterIndex` | `Int` |  | `0` |

### `IsActiveMagicEffectRegisteredForRangeEvents(eventReceiver, akTarget, akCenterRef, distance) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `ActiveMagicEffect` | ✓ |  |
| `akTarget` | `ObjectReference` | ✓ |  |
| `akCenterRef` | `ObjectReference` | ✓ |  |
| `distance` | `Float` | ✓ |  |

### `IsActiveMagicEffectRegisteredForUiItemMenuEvent(menuName, eventReceiver, paramFilter, eventType)`

**Flags:** Native Global

sActiveMagicEffect

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `eventReceiver` | `ActiveMagicEffect` | ✓ |  |
| `paramFilter` | `Form` |  |  |
| `eventType` | `Int` |  | `-1` |

### `IsAliasRegisteredForGlobalEvent(asEvent, eventReceiver, paramFilter, paramFilterIndex) → Bool`

**Flags:** Native Global

alias ==========================================================================================================================================================================

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asEvent` | `String` | ✓ |  |
| `eventReceiver` | `Alias` | ✓ |  |
| `paramFilter` | `Form` |  |  |
| `paramFilterIndex` | `Int` |  | `0` |

### `IsAliasRegisteredForRangeEvents(eventReceiver, akTarget, akCenterRef, distance) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Alias` | ✓ |  |
| `akTarget` | `ObjectReference` | ✓ |  |
| `akCenterRef` | `ObjectReference` | ✓ |  |
| `distance` | `Float` | ✓ |  |

### `IsAliasRegisteredForUiItemMenuEvent(menuName, eventReceiver, paramFilter, eventType)`

**Flags:** Native Global

Alias

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `eventReceiver` | `Alias` | ✓ |  |
| `paramFilter` | `Form` |  |  |
| `eventType` | `Int` |  | `-1` |

### `IsFormRegisteredForGlobalEvent(asEvent, eventReceiver, paramFilter, paramFilterIndex) → Bool`

**Flags:** Native Global

form ==================================================================================================================================================

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asEvent` | `String` | ✓ |  |
| `eventReceiver` | `Form` | ✓ |  |
| `paramFilter` | `Form` |  |  |
| `paramFilterIndex` | `Int` |  | `0` |

### `IsFormRegisteredForRangeEvents(eventReceiver, akTarget, akCenterRef, distance) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Form` | ✓ |  |
| `akTarget` | `ObjectReference` | ✓ |  |
| `akCenterRef` | `ObjectReference` | ✓ |  |
| `distance` | `Float` | ✓ |  |

### `IsFormRegisteredForUiItemMenuEvent(menuName, eventReceiver, paramFilter, eventType)`

**Flags:** Native Global

form

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `eventReceiver` | `Form` | ✓ |  |
| `paramFilter` | `Form` |  |  |
| `eventType` | `Int` |  | `-1` |

### `RegisterActiveMagicEffectForGlobalEvent(asEvent, eventReceiver, paramFilter, paramFilterIndex)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asEvent` | `String` | ✓ |  |
| `eventReceiver` | `ActiveMagicEffect` | ✓ |  |
| `paramFilter` | `Form` |  |  |
| `paramFilterIndex` | `Int` |  | `0` |

### `RegisterActiveMagicEffectForRangeEvents(eventReceiver, akTarget, akCenterRef, distance) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `ActiveMagicEffect` | ✓ |  |
| `akTarget` | `ObjectReference` | ✓ |  |
| `akCenterRef` | `ObjectReference` | ✓ |  |
| `distance` | `Float` | ✓ |  |

### `RegisterActiveMagicEffectForUiItemMenuEvent(menuName, eventReceiver, paramFilter, eventType)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `eventReceiver` | `ActiveMagicEffect` | ✓ |  |
| `paramFilter` | `Form` |  |  |
| `eventType` | `Int` |  | `-1` |

### `RegisterAliasForGlobalEvent(asEvent, eventReceiver, paramFilter, paramFilterIndex)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asEvent` | `String` | ✓ |  |
| `eventReceiver` | `Alias` | ✓ |  |
| `paramFilter` | `Form` |  |  |
| `paramFilterIndex` | `Int` |  | `0` |

### `RegisterAliasForRangeEvents(eventReceiver, akTarget, akCenterRef, distance) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Alias` | ✓ |  |
| `akTarget` | `ObjectReference` | ✓ |  |
| `akCenterRef` | `ObjectReference` | ✓ |  |
| `distance` | `Float` | ✓ |  |

### `RegisterAliasForUiItemMenuEvent(menuName, eventReceiver, paramFilter, eventType)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `eventReceiver` | `Alias` | ✓ |  |
| `paramFilter` | `Form` |  |  |
| `eventType` | `Int` |  | `-1` |

### `RegisterFormForGlobalEvent(asEvent, eventReceiver, paramFilter, paramFilterIndex)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asEvent` | `String` | ✓ |  |
| `eventReceiver` | `Form` | ✓ |  |
| `paramFilter` | `Form` |  |  |
| `paramFilterIndex` | `Int` |  | `0` |

### `RegisterFormForRangeEvents(eventReceiver, akTarget, akCenterRef, distance) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Form` | ✓ |  |
| `akTarget` | `ObjectReference` | ✓ |  |
| `akCenterRef` | `ObjectReference` | ✓ |  |
| `distance` | `Float` | ✓ |  |

### `RegisterFormForUiItemMenuEvent(menuName, eventReceiver, paramFilter, eventType)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `eventReceiver` | `Form` | ✓ |  |
| `paramFilter` | `Form` |  |  |
| `eventType` | `Int` |  | `-1` |

### `ToggleGlobalEventOnActiveMagicEffect(sEvent, eventReceiver, paramFilter, paramFilterIndex) → Bool`

**Flags:** Global

returns true if registering, or false if unregistering.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sEvent` | `String` | ✓ |  |
| `eventReceiver` | `ActiveMagicEffect` | ✓ |  |
| `paramFilter` | `form` |  |  |
| `paramFilterIndex` | `Int` |  | `0` |

### `ToggleGlobalEventOnAlias(sEvent, eventReceiver, paramFilter, paramFilterIndex) → Bool`

**Flags:** Global

returns true if registering, or false if unregistering.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sEvent` | `String` | ✓ |  |
| `eventReceiver` | `Alias` | ✓ |  |
| `paramFilter` | `form` |  |  |
| `paramFilterIndex` | `Int` |  | `0` |

### `ToggleGlobalEventOnForm(sEvent, eventReceiver, paramFilter, paramFilterIndex) → Bool`

**Flags:** Global

returns true if registering, or false if unregistering.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sEvent` | `String` | ✓ |  |
| `eventReceiver` | `form` | ✓ |  |
| `paramFilter` | `form` |  |  |
| `paramFilterIndex` | `Int` |  | `0` |

### `UnregisterActiveMagicEffectForGlobalEvent(asEvent, eventReceiver, paramFilter, paramFilterIndex)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asEvent` | `String` | ✓ |  |
| `eventReceiver` | `ActiveMagicEffect` | ✓ |  |
| `paramFilter` | `Form` |  |  |
| `paramFilterIndex` | `Int` |  | `0` |

### `UnregisterActiveMagicEffectForGlobalEvent_All(asEvent, eventReceiver)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asEvent` | `String` | ✓ |  |
| `eventReceiver` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterActiveMagicEffectForRangeEvents(eventReceiver, akTarget, akCenterRef, distance) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `ActiveMagicEffect` | ✓ |  |
| `akTarget` | `ObjectReference` | ✓ |  |
| `akCenterRef` | `ObjectReference` | ✓ |  |
| `distance` | `Float` | ✓ |  |

### `UnregisterActiveMagicEffectForRangeEvents_All(eventReceiver) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterActiveMagicEffectForUiItemMenuEvent(menuName, eventReceiver, paramFilter, eventType)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `eventReceiver` | `ActiveMagicEffect` | ✓ |  |
| `paramFilter` | `Form` |  |  |
| `eventType` | `Int` |  | `-1` |

### `UnregisterActiveMagicEffectForUiItemMenuEvent_All(eventReceiver)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `ActiveMagicEffect` | ✓ |  |

### `UnregisterAliasForGlobalEvent(asEvent, eventReceiver, paramFilter, paramFilterIndex)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asEvent` | `String` | ✓ |  |
| `eventReceiver` | `Alias` | ✓ |  |
| `paramFilter` | `Form` |  |  |
| `paramFilterIndex` | `Int` |  | `0` |

### `UnregisterAliasForGlobalEvent_All(asEvent, eventReceiver)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asEvent` | `String` | ✓ |  |
| `eventReceiver` | `Alias` | ✓ |  |

### `UnregisterAliasForRangeEvents(eventReceiver, akTarget, akCenterRef, distance) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Alias` | ✓ |  |
| `akTarget` | `ObjectReference` | ✓ |  |
| `akCenterRef` | `ObjectReference` | ✓ |  |
| `distance` | `Float` | ✓ |  |

### `UnregisterAliasForRangeEvents_All(eventReceiver) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Alias` | ✓ |  |

### `UnregisterAliasForUiItemMenuEvent(menuName, eventReceiver, paramFilter, eventType)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `eventReceiver` | `Alias` | ✓ |  |
| `paramFilter` | `Form` |  |  |
| `eventType` | `Int` |  | `-1` |

### `UnregisterAliasForUiItemMenuEvent_All(eventReceiver)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Alias` | ✓ |  |

### `UnregisterFormForGlobalEvent(asEvent, eventReceiver, paramFilter, paramFilterIndex)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asEvent` | `String` | ✓ |  |
| `eventReceiver` | `Form` | ✓ |  |
| `paramFilter` | `Form` |  |  |
| `paramFilterIndex` | `Int` |  | `0` |

### `UnregisterFormForGlobalEvent_All(asEvent, eventReceiver)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asEvent` | `String` | ✓ |  |
| `eventReceiver` | `Form` | ✓ |  |

### `UnregisterFormForRangeEvents(eventReceiver, akTarget, akCenterRef, distance) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Form` | ✓ |  |
| `akTarget` | `ObjectReference` | ✓ |  |
| `akCenterRef` | `ObjectReference` | ✓ |  |
| `distance` | `Float` | ✓ |  |

### `UnregisterFormForRangeEvents_All(eventReceiver) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Form` | ✓ |  |

### `UnregisterFormForUiItemMenuEvent(menuName, eventReceiver, paramFilter, eventType)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `eventReceiver` | `Form` | ✓ |  |
| `paramFilter` | `Form` |  |  |
| `eventType` | `Int` |  | `-1` |

### `UnregisterFormForUiItemMenuEvent_All(eventReceiver)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `eventReceiver` | `Form` | ✓ |  |


---

## `DbSkseFunctions`

**Source:** `dylbills` (Dylbills Papyrus Functions) • **Flags:** Hidden

---

## Events

### `OnSoundFinish(SoundOrDescriptor, instanceID)`

**Kind:** Event

sends the sound or soundDescriptor played and the instanceID
only works for sounds played from PlaySound or PlaySoundDescriptor from this script

**Parameters**

| Name | Type |
|---|---|
| `SoundOrDescriptor` | `Form` |
| `instanceID` | `Int` |

---

## Global Functions

### `AddAdditionalRaceToArmorAddon(akArmorAddon, akRace)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArmorAddon` | `armorAddon` | ✓ |  |
| `akRace` | `race` | ✓ |  |

### `AddAndUnlockAllShouts(minNumberOfWordsWithTranslations, onlyShoutsWithNames, onlyShoutsWithDescriptions)`

**Flags:** Native Global

add and unlock all shouts to the player that match the param filters.
default is adding and unlocking ALL shouts found in game to player

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `minNumberOfWordsWithTranslations` | `Int` |  | `0` |
| `onlyShoutsWithNames` | `Bool` |  | `false` |
| `onlyShoutsWithDescriptions` | `Bool` |  | `false` |

### `AddFormsToList(akForms, akList)`

**Flags:** Native Global

Add forms in akForms array to akList

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForms` | `Form[]` | ✓ |  |
| `akList` | `Formlist` | ✓ |  |

### `AddKnownEnchantmentsToFormList(akList)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akList` | `Formlist` | ✓ |  |

### `AddRaceSlotToMask(akRace, slotMask)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRace` | `Race` | ✓ |  |
| `slotMask` | `Int` | ✓ |  |

### `AddSkillBookForSkillToList(skill, akList)`

**Flags:** Native Global

add all books that teach skill to akList

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `skill` | `String` | ✓ |  |
| `akList` | `formlist` | ✓ |  |

### `AddSpellTomesForSpellToList(akSpell, akList)`

**Flags:** Native Global

add all spell tomes that teach akSpell to akList

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSpell` | `spell` | ✓ |  |
| `akList` | `FormList` | ✓ |  |

### `ArmorAddonHasRace(akArmorAddon, akRace) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArmorAddon` | `armorAddon` | ✓ |  |
| `akRace` | `race` | ✓ |  |

### `ArmorAddonSlotMaskHasPartOf(akArmorAddon, slotMask) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArmorAddon` | `Armor` | ✓ |  |
| `slotMask` | `Int` | ✓ |  |

### `ArmorSlotMaskHasPartOf(akArmor, slotMask) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArmor` | `Armor` | ✓ |  |
| `slotMask` | `Int` | ✓ |  |

### `CanSoulGemHoldNPCSoul(akSoulGem) → Bool`

**Flags:** Native Global

can the soulGem hold an NPC soul? I.E is it a black soul gem?

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSoulGem` | `SoulGem` | ✓ |  |

### `CountWhiteSpaces(s) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |

### `CreateColorForm(color) → ColorForm`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `color` | `Int` |  | `0xF` |

### `CreateConstructibleObject() → ConstructibleObject`

**Flags:** Native Global

### `CreateFormList(fillerList) → Formlist`

**Flags:** Native Global

create new forms of these types at runtime.
carefull with these. Using these functions are like using PlaceAtMe to create permanent references.
Making too many of these may cause save game bloat, so use sparingly.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fillerList` | `formlist` |  |  |

### `CreateKeyword() → Keyword`

**Flags:** Native Global

### `CreateSoundMarker() → Sound`

**Flags:** Native Global

Create new sound. to set the sound, use Papyrus Extender, 'Po3_SkseFunctions.SetSoundDescriptor(newSoundMarker, akSoundDescriptor)'

### `CreateTextureSet() → TextureSet`

**Flags:** Native Global

### `DispelMagicEffectOnRef(ref, akMagicEffect, magicSource)`

**Flags:** Native Global

if magicSource is not none, only dispels effects that come from the magicSource (spell, shout, potion ect)
Otherwise, dispels all activeMagicEffects that match the akMagicEffect

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `akMagicEffect` | `MagicEffect` | ✓ |  |
| `magicSource` | `Form` |  |  |

### `ExecuteConsoleCommand(command, ref)`

**Flags:** Native Global

differs from consoleUtil.ExecuteCommand in that you can execute a targeted command on a passed in ref.
if ref == none and command is targeted command, runs command on console selected ref like normal.
If no console selected ref, or is not a targeted command, executes command like normal.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `command` | `String` | ✓ |  |
| `ref` | `ObjectReference` |  |  |

### `FormListToArray(akList, sortOption) → Form[]`

**Flags:** Native Global

returns new form array that contains the forms in akList
Sort options are as follows. Note, to sort by editor Id reliably, po3 tweaks must be installed.
1 = by form name ascending,
2 = by form name descending,
3 = by form editor Id name ascending,
4 = by form editor Id name descending,
5 = by form Id ascending,
6 = by form Id descending

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akList` | `Formlist` | ✓ |  |
| `sortOption` | `Int` |  | `0` |

### `GameHoursToRealTimeSeconds(gameHours) → Float`

**Flags:** Native Global

Calculate how many real time seconds it will take for gameHours to pass based on current time scale.
Example - with default time scale (20), GameHoursToRealTimeSeconds(1) returns 180.0

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `gameHours` | `Float` | ✓ |  |

### `GetActiveEffectCastingSource(akEffect) → Int`

**Flags:** Native Global

Get casting source that the ActiveMagicEffect came from
LeftHand = 0,
RightHand = 1,
Other = 2, (most likely shout)
Instant = 3

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEffect` | `ActiveMagicEffect` | ✓ |  |

### `GetActiveEffectSource(akEffect) → Form`

**Flags:** Native Global

get source that the ActiveMagicEffect came from
Could be a spell, enchantment, potion, or ingredient. Use GetType() to find out which.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEffect` | `ActiveMagicEffect` | ✓ |  |

### `GetActiveMagicEffectConditionStatus(akEffect) → Int`

**Flags:** Native Global

-1 = not applicable or not found
0 = conditions not met, the active effect is not affecting the reference it's on.
1 = conditions met, the active effect is affecting the reference it's on

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEffect` | `ActiveMagicEffect` | ✓ |  |

### `GetActorWardState(akActor) → Int`

**Flags:** Native Global

ward states are 0 = none, 1 = absorbing, 2 = break

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `actor` | ✓ |  |

### `GetAll3DNodeNamesForRef(ref, firstPerson) → String[]`

**Flags:** Native Global

This will also log the names to C:/Users/YourUserName/Documents/My Games/Skyrim Special Edition/SKSE/DbSkseFunctions.log

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `firstPerson` | `Bool` |  | `false` |

### `GetAllActiveQuests() → Quest[]`

**Flags:** Native Global

Get all quests in game currently being tracked by the player.

### `GetAllAliasesWithScriptAttached(sScriptName) → Alias[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sScriptName` | `String` | ✓ |  |

### `GetAllArmorsForSlotMask(slotMask) → Armor[]`

**Flags:** Native Global

Get all armors in game that use the slotMask

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `slotMask` | `Int` | ✓ |  |

### `GetAllConstructibleObjects(createdObject) → ConstructibleObject[]`

**Flags:** Native Global

get all constructible objects that create the createdObject.
if none is passed in, get's all constructible objects in game.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `createdObject` | `Form` | ✓ |  |

### `GetAllContainerRefsThatContainForm(akForm) → ObjectReference[]`

**Flags:** Native Global

get all container refs, including actors, that have at least 1 of the akForm in their inventory.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `form` | ✓ |  |

### `GetAllExteriorCells(akLocation, akWorldSpace, matchMode) → cell[]`

**Flags:** Native Global

Get all exterior cells in game that match the akLocation and or akWorldSpace
if matchMode == 0, get all cells in game where either the passed in akLocation or akWorldSpace match.
if matchMode == 1, get all cells in game where both the passed in akLocation and akWorldSpace match.
if matchMode == anything else, get all interior cells in game

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akLocation` | `Location` | ✓ |  |
| `akWorldSpace` | `WorldSpace` | ✓ |  |
| `matchMode` | `Int` |  | `0` |

### `GetAllFormsThatUseTextureSet(akTextureSet, modName) → form[]`

**Flags:** Native Global

get all forms that use the akTextureSet.
If modName != "", only gets forms from that mod, otherwise gets all forms in game that use the textureset

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akTextureSet` | `TextureSet` | ✓ |  |
| `modName` | `String` |  | `""` |

### `GetAllFormsWithName(sFormName, nameMatchMode, formTypes, formTypeMatchMode) → Form[]`

**Flags:** Native Global

Get all forms who's name (with GetName()) match the sFormName.
nameMatchMode 0 = exact match, 1 = name contains sFormName.
formTypeMatchMode 1 = forms who match a type in formTypes.
formTypeMatchMode 0 = forms that match none of the types in formTypes.
formTypeMatchMode -1 (or if formTypes == none) = formType filter is ignored completely, get all forms regardless of type that match (or contain) sFormName.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sFormName` | `String` | ✓ |  |
| `nameMatchMode` | `Int` |  | `0` |
| `formTypes` | `Int[]` |  |  |
| `formTypeMatchMode` | `Int` |  | `1` |

### `GetAllFormsWithScriptAttached(sScriptName, formTypes, formTypeMatchMode) → Form[]`

**Flags:** Native Global

formTypeMatchMode 1 = forms that have a type in formTypes.
formTypeMatchMode 0 = forms that do not have a type in formTypes.
formTypeMatchMode -1 (or if formTypes == none) = formType filter is ignored completely, get all forms regardless of type that have the script with sScriptName attached

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sScriptName` | `String` | ✓ |  |
| `formTypes` | `Int[]` |  |  |
| `formTypeMatchMode` | `Int` |  | `0` |

### `GetAllHitProjectileRefsOfType(ref, only3dLoaded, onlyEnabled, projectileType) → ObjectReference[]`

**Flags:** Native Global

get all projectile object references that hit the ref that match the conditions.
projectileTypes are: 1 = Missile, 2 = Grenade, 3 = Beam, 4 = Flamethrower, 5 = Cone, 6 = Barrier, 7 = Arrow.
if the projectileType param is none of those types, returns all projectiles that have hit the ref regardless of type.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `only3dLoaded` | `Bool` |  | `true` |
| `onlyEnabled` | `Bool` |  | `true` |
| `projectileType` | `Int` |  | `7` |

### `GetAllInteriorCells(akLocation, akOwner, matchMode) → cell[]`

**Flags:** Native Global

Get all interior cells in game that match the akLocation and or akOwner
if matchMode == 0, get all cells in game where either the passed in akLocation or akOwner match.
if matchMode == 1, get all cells in game where both the passed in akLocation and akOwner match.
if matchMode == anything else, get all interior cells in game

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akLocation` | `Location` | ✓ |  |
| `akOwner` | `Actorbase` | ✓ |  |
| `matchMode` | `Int` |  | `0` |

### `GetAllLoadedModDescriptions(sortOption, maxCharacters, overMaxCharacterSuffix, newLineReplacer) → String[]`

**Flags:** Native Global

get all loaded mod descriptions, (regular mods and light mods)
if maxCharacters is greater than 0, limits the number of characters for descriptions.
If a description exceeds maxCharacters, adds the overMaxCharacterSuffix to the end of the description.
Sort options are as follows. 0 = not sorted, 1 = sorted by Description ascending, 2 = sorted by Description descending, 3 sorted by mod name ascending, 4 = sorted by mod name descending.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sortOption` | `Int` |  | `0` |
| `maxCharacters` | `Int` |  | `0` |
| `overMaxCharacterSuffix` | `String` |  | `".."` |
| `newLineReplacer` | `String` |  | `""` |

### `GetAllLoadedModNames(sortOption) → String[]`

**Flags:** Native Global

Get all loaded mod names, (regular mods and light mods)
Sort options are as follows. 0 = not sorted, 1 = sorted by name ascending, 2 = sorted by name descending.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sortOption` | `Int` |  | `0` |

### `GetAllMapMarkerRefs(visibleFilter, canTravelToFilter) → ObjectReference[]`

**Flags:** Native Global

get all map marker refs in game
for the filter params:
-1 = filter is ignored
 0 = (false) only get markers that are not visible or can't be fast traveled to
 1 = (true)  only get markers that are visible or that can be fast traveled to

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `visibleFilter` | `Int` |  | `-1` |
| `canTravelToFilter` | `Int` |  | `-1` |

### `GetAllObjectRefsInContainer(containerRef) → ObjectReference[]`

**Flags:** Native Global

Get all persistent object references in the containerRef, regardless if they're quest objects or not.
Object refs must be persistent to be in a container.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `containerRef` | `ObjectReference` | ✓ |  |

### `GetAllQuestObjectRefs() → ObjectReference[]`

**Flags:** Native Global

Get all quest object references in game

### `GetAllRefaliases(onlyQuestObjects, onlyFilled) → ReferenceAlias[]`

**Flags:** Native Global

Get all ReferenceAlias's in game.
if onlyQuestObjects is true, only gets ref alias's that have the Quest Object box checked
if onlyFilled is true, only gets ref alias's that are filled with a valid object reference.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `onlyQuestObjects` | `Bool` |  | `false` |
| `onlyFilled` | `Bool` |  | `false` |

### `GetAllRefAliasesForRef(ref) → ReferenceAlias[]`

**Flags:** Native Global

Get all references aliases that are currently filled with the ref.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |

### `GetAllRefAliasesWithScriptAttached(sScriptName, onlyQuestObjects, onlyFilled) → ReferenceAlias[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sScriptName` | `String` | ✓ |  |
| `onlyQuestObjects` | `Bool` |  | `false` |
| `onlyFilled` | `Bool` |  | `false` |

### `GetAllShotProjectileRefsOfType(ref, only3dLoaded, onlyEnabled, projectileType) → ObjectReference[]`

**Flags:** Native Global

get all projectile object references that were shot by the ref that match the conditions.
projectileTypes are: 1 = Missile, 2 = Grenade, 3 = Beam, 4 = Flamethrower, 5 = Cone, 6 = Barrier, 7 = Arrow.
if the projectileType param is none of those types, returns all projectiles that the ref has shot regardless of type.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `only3dLoaded` | `Bool` |  | `true` |
| `onlyEnabled` | `Bool` |  | `true` |
| `projectileType` | `Int` |  | `7` |

### `GetArmorAddonRaces(akArmorAddon) → Race[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArmorAddon` | `armorAddon` | ✓ |  |

### `GetArtObjectModelNth3dName(akArtObject, n) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArtObject` | `art` | ✓ |  |
| `n` | `Int` | ✓ |  |

### `GetArtObjectNthTextureSet(akArtObject, n) → TextureSet`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArtObject` | `art` | ✓ |  |
| `n` | `Int` | ✓ |  |

### `GetArtObjectNumOfTextureSets(akArtObject) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArtObject` | `art` | ✓ |  |

### `GetAshPileLinkedRef(ref) → ObjectReference`

**Flags:** Native Global

If the ref is an ashpile, gets the actor linked to it, if any. If the ref is an actor, gets the ashpile linked to it, if any.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |

### `GetAttachedCells() → cell[]`

**Flags:** Native Global

Get all cells currently attached

### `GetAttachedProjectileRefs(ref) → Projectile[]`

**Flags:** Native Global

get projectile object references attached to the ref
only works if the ref is not an actor

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |

### `GetAttachedProjectiles(ref) → Projectile[]`

**Flags:** Native Global

get projectiles currently attached to the ref
only works if the ref is an actor

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |

### `GetBase3DNodeNameForRef(ref, firstPerson) → String`

**Flags:** Native Global

get the top level base 3d node name for ref

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `firstPerson` | `Bool` |  | `false` |

### `GetBookSkill(akBook) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akBook` | `book` | ✓ |  |

### `GetCellLocation(akCell) → Location`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akCell` | `cell` | ✓ |  |

### `GetCellOrWorldSpaceOriginForRef(ref) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |

### `GetCellWorldSpace(akCell) → WorldSpace`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akCell` | `cell` | ✓ |  |

### `GetClipBoardText() → String`

**Flags:** Native Global

get and set text from system clipboard, for copy / paste functionality

### `GetClosestObjectFromRef(ref, refs) → ObjectReference`

**Flags:** Native Global

Get the closest object reference in the refs array to the ref

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `refs` | `ObjectReference[]` | ✓ |  |

### `GetClosestObjectIndexFromRef(ref, refs) → Int`

**Flags:** Native Global

Get the index of the closest object reference in the refs array to the ref

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `refs` | `ObjectReference[]` | ✓ |  |

### `GetCollisionLayerName(layer) → String`

**Flags:** Native Global

get the string of the collision layer. i.e "Biped", "static", "trees" ect
names are as follows:
Unidentified = 0,
Static = 1,
AnimStatic = 2,
Transparent = 3,
Clutter = 4,
Weapon = 5,
Projectile = 6,
Spell = 7,
Biped = 8,
Trees = 9,
Props = 10,
Water = 11,
Trigger = 12,
Terrain = 13,
Trap = 14,
NonCollidable = 15,
CloudTrap = 16,
Ground = 17,
Portal = 18,
DebrisSmall = 19,
DebrisLarge = 20,
AcousticSpace = 21,
ActorZone = 22,
ProjectileZone = 23,
GasTrap = 24,
ShellCasting = 25,
TransparentWall = 26,
InvisibleWall = 27,
TransparentSmallAnim = 28,
ClutterLarge = 29,
CharController = 30,
StairHelper = 31,
DeadBip = 32,
BipedNoCC = 33,
AvoidBox = 34,
CollisionBox = 35,
CameraSphere = 36,
DoorDetection = 37,
ConeProjectile = 38,
Camera = 39,
ItemPicker = 40,
LOS = 41,
PathingPick = 42,
Unused0 = 43,
Unused1 = 44,
SpellExplosion = 45,
DroppingPick = 46

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `layer` | `Int` | ✓ |  |

### `GetCurrentMapMarkerRefs(visibleFilter, canTravelToFilter) → ObjectReference[]`

**Flags:** Native Global

get all map marker refs valid for the current world space or interior cell grid, (can potentially be viewed on the current map)
for the filter params:
-1 = filter is ignored
 0 = (false) only get markers that are not visible or can't be fast traveled to
 1 = (true)  only get markers that are visible or that can be fast traveled to

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `visibleFilter` | `Int` |  | `-1` |
| `canTravelToFilter` | `Int` |  | `-1` |

### `GetCurrentMusicType() → MusicType`

**Flags:** Native Global

get MusicType that's currently playing

### `GetDetectionLevel(akActor, akTarget) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `actor` | ✓ |  |
| `akTarget` | `actor` | ✓ |  |

### `GetEnableChildrenRefs(ref) → ObjectReference[]`

**Flags:** Native Global

get all enable children for the ref. See also objectReference.GetEnableParentRef()

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |

### `GetEnableParentRef(ref) → ObjectReference`

**Flags:** Native Global

Get the enable parent of the ref, if there is one.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |

### `GetFavorites(formTypes, formTypeMatchMode) → Form[]`

**Flags:** Native Global

Get forms currently favorited by the player
formTypeMatchMode 1 = forms who match a type in formTypes.
formTypeMatchMode 0 = forms that match none of the types in formTypes.
formTypeMatchMode -1 (or if formTypes == none) = formType filter is ignored completely, get all favorited forms regardless of type.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `formTypes` | `Int[]` |  |  |
| `formTypeMatchMode` | `Int` |  | `1` |

### `GetFormDescription(akForm, maxCharacters, overMaxCharacterSuffix, newLineReplacer, noneStringType, nullFormString) → String`

**Flags:** Native Global

if maxCharacters is greater than 0, limits the number of characters for descriptions.
If a description exceeds maxCharacters, adds the overMaxCharacterSuffix to the end of the description.
if newLineReplacer is not empty "", replaces new lines in description with newLineReplacer
if noneStringType is 1 and a description is empty, "", gets editorID instead of the description
if noneStringType is 2 and a description is empty, "", gets form ID instead of the description
if akForm is none, returns nullFormString

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `form` | ✓ |  |
| `maxCharacters` | `Int` |  | `0` |
| `overMaxCharacterSuffix` | `String` |  | `".."` |
| `newLineReplacer` | `String` |  | `""` |
| `noneStringType` | `Int` |  | `0` |
| `nullFormString` | `String` |  | `"ull"` |

### `GetFormDescriptions(akForms, sortOption, maxCharacters, overMaxCharacterSuffix, newLineReplacer, noneStringType, nullFormString) → String[]`

**Flags:** Native Global

get form descriptions for akForms.
if maxCharacters is greater than 0, limits the number of characters for descriptions.
If a description exceeds maxCharacters, adds the overMaxCharacterSuffix to the end of the description.
if noneStringType is 1 and a description is empty, "", gets editorID instead of the description
if noneStringType is 2 and a description is empty, "", gets form ID instead of the description
if akForm is none, sets nullFormString for that form.
Sort options are as follows. Note, to sort by editor Id reliably, po3 tweaks must be installed.
0 = not sorted,
1 = sorted by description ascending,
2 = sorted by description descending,
3 = by form name ascending,
4 = by form name descending,
5 = by form editor Id name ascending,
6 = by form editor Id name descending,
7 = by form Id ascending,
8 = by form Id descending

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForms` | `Form[]` | ✓ |  |
| `sortOption` | `Int` |  | `0` |
| `maxCharacters` | `Int` |  | `0` |
| `overMaxCharacterSuffix` | `String` |  | `".."` |
| `newLineReplacer` | `String` |  | `""` |
| `noneStringType` | `Int` |  | `0` |
| `nullFormString` | `String` |  | `"ull"` |

### `GetFormDescriptionsFromList(akFormList, sortOption, maxCharacters, overMaxCharacterSuffix, newLineReplacer, noneStringType, nullFormString) → String[]`

**Flags:** Native Global

get form descriptions for forms in akFormList.
if maxCharacters is greater than 0, limits the number of characters for descriptions.
If a description exceeds maxCharacters, adds the overMaxCharacterSuffix to the end of the description.
if noneStringType is 1 and a description is empty, "", gets editorID instead of the description
if noneStringType is 2 and a description is empty, "", gets form ID instead of the description
if akForm is none, sets nullFormString for that form.
Sort options are as follows. Note, to sort by editor Id reliably, po3 tweaks must be installed.
0 = not sorted,
1 = sorted by description ascending,
2 = sorted by description descending,
3 = by form name ascending,
4 = by form name descending,
5 = by form editor Id name ascending,
6 = by form editor Id name descending,
7 = by form Id ascending,
8 = by form Id descending

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFormList` | `Formlist` | ✓ |  |
| `sortOption` | `Int` |  | `0` |
| `maxCharacters` | `Int` |  | `0` |
| `overMaxCharacterSuffix` | `String` |  | `".."` |
| `newLineReplacer` | `String` |  | `""` |
| `noneStringType` | `Int` |  | `0` |
| `nullFormString` | `String` |  | `"ull"` |

### `GetFormEditorId(akForm, nullFormString) → String`

**Flags:** Native Global

Get form editor Id name.
If akForm is none, returns nullFormString

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `nullFormString` | `String` |  | `"ull"` |

### `GetFormEditorIds(akForms, sortOption, nullFormString) → String[]`

**Flags:** Native Global

get form editor id names for akForms
if a form is none, sets nullFormString for that form
Sort options are as follows. 0 = not sorted, 1 = sorted by name ascending, 2 = sorted by name descending.
Note, to get editor Ids reliably, po3 tweaks must be installed.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForms` | `Form[]` | ✓ |  |
| `sortOption` | `Int` |  | `0` |
| `nullFormString` | `String` |  | `"ull"` |

### `GetFormEditorIdsFromList(akFormList, sortOption, nullFormString) → String[]`

**Flags:** Native Global

get form editor id names for forms in akFormList
if a form is none, sets nullFormString for that form
Sort options are as follows. 0 = not sorted, 1 = sorted by name ascending, 2 = sorted by name descending.
Note, to get editor Ids reliably, po3 tweaks must be installed.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFormList` | `Formlist` | ✓ |  |
| `sortOption` | `Int` |  | `0` |
| `nullFormString` | `String` |  | `"ull"` |

### `GetFormNames(akForms, sortOption, noneStringType, nullFormString) → String[]`

**Flags:** Native Global

get form names for akForms
if noneStringType is 1 and a name is empty, "", gets editorID instead of name
if noneStringType is 2 and a name is empty, "", gets form ID instead of name
if a form is none, sets nullFormString for that form
Sort options are as follows. 0 = not sorted, 1 = sorted by name ascending, 2 = sorted by name descending.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForms` | `Form[]` | ✓ |  |
| `sortOption` | `Int` |  | `0` |
| `noneStringType` | `Int` |  | `0` |
| `nullFormString` | `String` |  | `"ull"` |

### `GetFormNamesFromList(akFormList, sortOption, noneStringType, nullFormString) → String[]`

**Flags:** Native Global

get form names for forms in akFormList
if noneStringType is 1 and a name is empty, "", gets editorID instead of name
if noneStringType is 2 and a name is empty, "", gets form ID instead of name
if a form is none, sets nullFormString for that form
Sort options are as follows. 0 = not sorted, 1 = sorted by name ascending, 2 = sorted by name descending.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFormList` | `Formlist` | ✓ |  |
| `sortOption` | `Int` |  | `0` |
| `noneStringType` | `Int` |  | `0` |
| `nullFormString` | `String` |  | `"ull"` |

### `GetFormWorldModelNth3dName(akForm, n) → String`

**Flags:** Native Global

this only works on models that have a new texture sets applied to them. The int n is the index of the model with the override texture set.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `form` | ✓ |  |
| `n` | `Int` | ✓ |  |

### `GetFurnitureWorkbenchSkillString(akFurniture) → String`

**Flags:** Native Global

return string will be an actor value skill such as "smithing", "enchanting" ect.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFurniture` | `furniture` | ✓ |  |

### `GetFurnitureWorkbenchType(akFurniture) → Int`

**Flags:** Native Global

furniture workbench types are:
None = 0,
CreateObject = 1,
SmithingWeapon = 2,
Enchanting = 3,
EnchantingExperiment = 4,
Alchemy = 5,
AlchemyExperiment = 6,
SmithingArmor = 7

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFurniture` | `furniture` | ✓ |  |

### `GetGameHoursPassed() → Float`

**Flags:** Native Global

### `GetKeywordString(akKeyword) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akKeyword` | `keyword` | ✓ |  |

### `GetKnownEnchantments() → Enchantment[]`

**Flags:** Native Global

### `GetLastMenuOpened() → String`

**Flags:** Native Global

### `GetLastPlayerActivatedRef() → ObjectReference`

**Flags:** Native Global

Get the last object reference that the player activated
Requires the bActivateEventSinkEnabledByDefault setting in the DbSkseFunctions.ini file to be enabled.

### `GetLastPlayerMenuActivatedRef() → ObjectReference`

**Flags:** Native Global

Get the last object reference that the player activated after a menu was opened
Requires the bMenuOpenCloseEventSinkEnabled and bActivateEventSinkEnabledByDefault settings in the DbSkseFunctions.ini file to be enabled.

### `GetLastProjectileHitRef(ref, only3dLoaded, onlyEnabled, projectileType) → ObjectReference`

**Flags:** Native Global

get the last projectile object reference that hit the ref that match the conditions.
projectileTypes are: 1 = Missile, 2 = Grenade, 3 = Beam, 4 = Flamethrower, 5 = Cone, 6 = Barrier, 7 = Arrow.
if the projectileType param is none of those types, returns the last projectile that hit the ref regardless of type
requires iMaxArrowsSavedPerReference to be set to greater than 0 in DbSkseFunctions.ini

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `only3dLoaded` | `Bool` |  | `true` |
| `onlyEnabled` | `Bool` |  | `true` |
| `projectileType` | `Int` |  | `7` |

### `GetLastProjectileShotRef(ref, only3dLoaded, onlyEnabled, projectileType) → ObjectReference`

**Flags:** Native Global

get the last projectile object reference that was shot by the ref that match the conditions.
projectileTypes are: 1 = Missile, 2 = Grenade, 3 = Beam, 4 = Flamethrower, 5 = Cone, 6 = Barrier, 7 = Arrow.
if the projectileType param is none of those types, returns the last projectile was shot by the ref regardless of type
requires iMaxArrowsSavedPerReference to be set to greater than 0 in DbSkseFunctions.ini

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `only3dLoaded` | `Bool` |  | `true` |
| `onlyEnabled` | `Bool` |  | `true` |
| `projectileType` | `Int` |  | `7` |

### `GetLoadedLightModDescriptions(sortOption, maxCharacters, overMaxCharacterSuffix, newLineReplacer) → String[]`

**Flags:** Native Global

get loaded light mod descriptions.
if maxCharacters is greater than 0, limits the number of characters for descriptions.
If a description exceeds maxCharacters, adds the overMaxCharacterSuffix to the end of the description.
Sort options are as follows. 0 = not sorted, 1 = sorted by Description ascending, 2 = sorted by Description descending, 3 sorted by mod name ascending, 4 = sorted by mod name descending.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sortOption` | `Int` |  | `0` |
| `maxCharacters` | `Int` |  | `0` |
| `overMaxCharacterSuffix` | `String` |  | `".."` |
| `newLineReplacer` | `String` |  | `""` |

### `GetLoadedLightModNames(sortOption) → String[]`

**Flags:** Native Global

Sort options are as follows. 0 = not sorted, 1 = sorted by name ascending, 2 = sorted by name descending.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sortOption` | `Int` |  | `0` |

### `GetLoadedModDescriptions(sortOption, maxCharacters, overMaxCharacterSuffix, newLineReplacer) → String[]`

**Flags:** Native Global

get loaded mod descriptions.
if maxCharacters is greater than 0, limits the number of characters for descriptions.
If a description exceeds maxCharacters, adds the overMaxCharacterSuffix to the end of the description.
Sort options are as follows. 0 = not sorted, 1 = sorted by Description ascending, 2 = sorted by Description descending, 3 sorted by mod name ascending, 4 = sorted by mod name descending.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sortOption` | `Int` |  | `0` |
| `maxCharacters` | `Int` |  | `0` |
| `overMaxCharacterSuffix` | `String` |  | `".."` |
| `newLineReplacer` | `String` |  | `""` |

### `GetLoadedModNames(sortOption) → String[]`

**Flags:** Native Global

Sort options are as follows. 0 = not sorted, 1 = sorted by name ascending, 2 = sorted by name descending.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sortOption` | `Int` |  | `0` |

### `GetMagicEffectsForForm(akForm) → MagicEffect[]`

**Flags:** Native Global

get magic effects for akForm, assuming akForm is a magic item such as a spell, potion, shout, enchantment, scroll ect...

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `GetMapMarkerIconType(MapMarker) → Int`

**Flags:** Native Global

For these functions the MapMarker ObjectReference must be a map marker with map marker data.
In other words, IsMapMarker must return true;

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `MapMarker` | `ObjectReference` | ✓ |  |

### `GetMapMarkerName(MapMarker) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `MapMarker` | `ObjectReference` | ✓ |  |

### `GetMusicTypePriority(akMusicType) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akMusicType` | `MusicType` | ✓ |  |

### `GetMusicTypeStatus(akMusicType) → Int`

**Flags:** Native Global

MusicTypeStatus is as follows
kInactive = 0
kPlaying = 1
kPaused = 2
kFinishing = 3
kFinished = 4

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akMusicType` | `MusicType` | ✓ |  |

### `GetMusicTypeTrackIndex(akMusicType) → Int`

**Flags:** Native Global

get the current track index queued in akMusicType

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akMusicType` | `MusicType` | ✓ |  |

### `GetNumberOfTracksInMusicType(akMusicType) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akMusicType` | `MusicType` | ✓ |  |

### `GetParentSoundCategory(akSoundCategory) → SoundCategory`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSoundCategory` | `SoundCategory` | ✓ |  |

### `GetProjectileAmmoSource(projectileRef) → Ammo`

**Flags:** Native Global

Get the ammo that the projectileRef was shot with if any

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `projectileRef` | `ObjectReference` | ✓ |  |

### `GetProjectileBaseCollisionConeSpread(akProjectile) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akProjectile` | `projectile` | ✓ |  |

### `GetProjectileBaseCollisionRadius(akProjectile) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akProjectile` | `projectile` | ✓ |  |

### `GetProjectileBaseDecal(akProjectile) → TextureSet`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akProjectile` | `projectile` | ✓ |  |

### `GetProjectileBaseExplosion(akProjectile) → Explosion`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akProjectile` | `projectile` | ✓ |  |

### `GetProjectileCollidedLayerNames(projectileRef) → String[]`

**Flags:** Native Global

get names of the collision layers the projectileRef has collided with

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `projectileRef` | `ObjectReference` | ✓ |  |

### `GetProjectileCollidedLayers(projectileRef) → Int[]`

**Flags:** Native Global

get the collision layers the projectileRef has collided with

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `projectileRef` | `ObjectReference` | ✓ |  |

### `GetProjectileDistanceTraveled(projectileRef) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `projectileRef` | `ObjectReference` | ✓ |  |

### `GetProjectileEnchantment(projectileRef) → Enchantment`

**Flags:** Native Global

Get the enchantment the projectileRef was shot with if any

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `projectileRef` | `ObjectReference` | ✓ |  |

### `GetProjectileExplosion(projectileRef) → Explosion`

**Flags:** Native Global

get the explosion for the projectileRef, if any.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `projectileRef` | `ObjectReference` | ✓ |  |

### `GetProjectileHitRefs(projectileRef) → ObjectReference[]`

**Flags:** Native Global

get the object reference(s) that the projectileRef hit (collided with). Most of the time this is 1 object, sometimes it's more.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `projectileRef` | `ObjectReference` | ✓ |  |

### `GetProjectileImpactResult(projectileRef) → Int`

**Flags:** Native Global

impactResults are: 0 = none, 1 = destroy, 2 = bounce, 3 = impale, 4 = stick

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `projectileRef` | `ObjectReference` | ✓ |  |

### `GetProjectileMagicSource(projectileRef) → Form`

**Flags:** Native Global

Get the magic item that shot the projectileRef if any
Most likely a spell or shout

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `projectileRef` | `ObjectReference` | ✓ |  |

### `GetProjectileNodeHitNames(projectileRef) → String[]`

**Flags:** Native Global

get the node names that the projectileRef has hit.
these only seem to be valid if the projectileRef hit an actor.
i.e "NPC Head [Head]", "NPC Spine1 [Spn1]" ect.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `projectileRef` | `ObjectReference` | ✓ |  |

### `GetProjectilePoison(projectileRef) → Potion`

**Flags:** Native Global

Get the poison the projectileRef was shot with if any

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `projectileRef` | `ObjectReference` | ✓ |  |

### `GetProjectilePower(projectileRef) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `projectileRef` | `ObjectReference` | ✓ |  |

### `GetProjectileRefType(projectileRef) → Int`

**Flags:** Native Global

get the type of the projectileRef
projectile types are: 1 = Missile, 2 = Grenade, 3 = Beam, 4 = Flamethrower, 5 = Cone, 6 = Barrier, 7 = Arrow, 0 = unrecognized.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `projectileRef` | `ObjectReference` | ✓ |  |

### `GetProjectileShooter(projectileRef) → ObjectReference`

**Flags:** Native Global

get the object reference that shot the projectileRef

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `projectileRef` | `ObjectReference` | ✓ |  |

### `GetProjectileWeaponDamage(projectileRef) → Float`

**Flags:** Native Global

Get the damage of the weapon that shot the projectileRef if any

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `projectileRef` | `ObjectReference` | ✓ |  |

### `GetProjectileWeaponSource(projectileRef) → Weapon`

**Flags:** Native Global

Get the weapon that the projectileRef was shot from if any
Most likely a bow or crossbow.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `projectileRef` | `ObjectReference` | ✓ |  |

### `GetQuestObjectRefsInContainer(containerRef) → ObjectReference[]`

**Flags:** Native Global

Get all quest object references in the containerRef

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `containerRef` | `ObjectReference` | ✓ |  |

### `GetRaceSlotMask(akRace) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRace` | `Race` | ✓ |  |

### `GetRecentProjectileHitRefs(ref, only3dLoaded, onlyEnabled, projectileType) → ObjectReference[]`

**Flags:** Native Global

get recent projectile object references that hit the ref that match the conditions.
projectileTypes are: 1 = Missile, 2 = Grenade, 3 = Beam, 4 = Flamethrower, 5 = Cone, 6 = Barrier, 7 = Arrow.
if the projectileType param is none of those types, returns all recent projectiles that have hit the ref regardless of type.
requires iMaxArrowsSavedPerReference to be set to greater than 0 in DbSkseFunctions.ini

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `only3dLoaded` | `Bool` |  | `true` |
| `onlyEnabled` | `Bool` |  | `true` |
| `projectileType` | `Int` |  | `7` |

### `GetRecentProjectileShotRefs(ref, only3dLoaded, onlyEnabled, projectileType) → ObjectReference[]`

**Flags:** Native Global

get recent projectile object references that were shot by the ref that match the conditions.
projectileTypes are: 1 = Missile, 2 = Grenade, 3 = Beam, 4 = Flamethrower, 5 = Cone, 6 = Barrier, 7 = Arrow.
if the projectileType param is none of those types, returns all recent projectiles that the ref has shot regardless of type.
requires iMaxArrowsSavedPerReference to be set to greater than 0 in DbSkseFunctions.ini

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `only3dLoaded` | `Bool` |  | `true` |
| `onlyEnabled` | `Bool` |  | `true` |
| `projectileType` | `Int` |  | `7` |

### `GetRefLinearVelocity(ref) → Float[]`

**Flags:** Native Global

[0] = x, [1] = y, [2] = z

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |

### `GetSkillBooksForSkill(skill) → book[]`

**Flags:** Native Global

get all books that teach the skill

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `skill` | `String` | ✓ |  |

### `GetSoundCategoryForSoundDescriptor(akSoundDescriptor) → SoundCategory`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSoundDescriptor` | `SoundDescriptor` | ✓ |  |

### `GetSoundCategoryFrequency(akSoundCategory) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSoundCategory` | `SoundCategory` | ✓ |  |

### `GetSoundCategoryVolume(akSoundCategory) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSoundCategory` | `SoundCategory` | ✓ |  |

### `GetSpellTomeForSpell(akSpell) → Book`

**Flags:** Native Global

get the first spell tome found that teaches akSpell, or none if not found.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSpell` | `spell` | ✓ |  |

### `GetSpellTomesForSpell(akSpell) → Book[]`

**Flags:** Native Global

get all spell tomes that teach akSpell, or empty array if none found.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSpell` | `spell` | ✓ |  |

### `GetUiTargetMembers(menuName, target) → String[]`

**Flags:** Native Global

Get all target members that the UI target has.
Example, start with "GetUiTargetMembers("InventoryMenu", "_root)"
This will get all target members that _root has. One will be Menu_mc.
Then you can do "GetUiTargetMembers("InventoryMenu", "_root.Menu_mc)"
And so on and so forth. Another good starting place is for example "GetUiTargetMembers("InventoryMenu", "_global)"

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `target` | `String` | ✓ |  |

### `GetUiTargetMembersData(menuName, target) → String[]`

**Flags:** Native Global

This is the same as GetUiTargetMembers except it gets the type, current value and full target path of members.
Example, start with "UI.GetUiTargetMembers("InventoryMenu", "_root.Menu_mc)"
One string in the array will be "type[bool] value[true] member[_root.Menu_mc._visible]"

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `target` | `String` | ✓ |  |

### `GetUITargetType(menuName, target) → Int`

**Flags:** Native Global

types are:
Undefined = 0
Null = 1
Boolean = 2
Number = 3
String = 4
StringW = 5
Object = 6
Array = 7
DisplayObject = 8

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `target` | `String` | ✓ |  |

### `GetUITargetTypeAsString(menuName, target) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `target` | `String` | ✓ |  |

### `GetUITargetValueAsString(menuName, target) → String`

**Flags:** Native Global

Instead of UI.GetBool, UI.GetString ect, gets the current value of the target as string. Bools will be "true" or "false".

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menuName` | `String` | ✓ |  |
| `target` | `String` | ✓ |  |

### `GetVersion() → Float`

**Flags:** Native Global

### `GetWordOfPowerTranslation(akWord) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akWord` | `WordOfPower` | ✓ |  |

### `HasCollision(ref) → Bool`

**Flags:** Native Global

does the ref have collision?

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |

### `IsActorAMount(akActor) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `actor` | ✓ |  |

### `IsActorAttacking(akActor) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `actor` | ✓ |  |

### `IsActorBlocking(akActor) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `actor` | ✓ |  |

### `IsActorCasting(akActor) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `actor` | ✓ |  |

### `IsActorDualCasting(akActor) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `actor` | ✓ |  |

### `IsActorFleeing(akActor) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `actor` | ✓ |  |

### `IsActorIgnoringCombat(akActor) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `actor` | ✓ |  |

### `IsActorInMidAir(akActor) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `actor` | ✓ |  |

### `IsActorInRagdollState(akActor) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `actor` | ✓ |  |

### `IsActorOnFlyingMount(akActor) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `actor` | ✓ |  |

### `IsActorPowerAttacking(akActor) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `actor` | ✓ |  |

### `IsActorRecoiling(akActor) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `actor` | ✓ |  |

### `IsActorSpeaking(akActor) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `actor` | ✓ |  |

### `IsActorStaggered(akActor) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `actor` | ✓ |  |

### `IsActorUndead(akActor) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `actor` | ✓ |  |

### `IsAliasQuestObjectFlagSet(akAlias) → Bool`

**Flags:** Native Global

Does the akAlias have the quest obect flag checked?

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `alias` | ✓ |  |

### `IsCellOffLimits(akCell) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akCell` | `cell` | ✓ |  |

### `IsCellPublic(akCell) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akCell` | `cell` | ✓ |  |

### `IsFormMagicItem(akForm) → Bool`

**Flags:** Native Global

is the form a magic item such as spell, potion, shout, enchantment, scroll ect...?

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `IsGamePaused() → Bool`

**Flags:** Native Global

### `IsInMenu() → Bool`

**Flags:** Native Global

returns true if a menu is open, (other than the hud menu which is always open), regardless if the game is paused or not.

### `IsItemMenuOpen() → Bool`

**Flags:** Native Global

### `IsMagicEffectActiveOnRef(ref, akMagicEffect, magicSource) → Bool`

**Flags:** Native Global

is the akMagicEffect currently affecting the ref?
if magicSource is not none, only returns true if the activeMagicEffect matches the akMagicEffect and it's condition status is true and comes from the magicSource (spell, shout, potion ect)
Otherwise, returns true if activeMagicEffect matches the akMagicEffect and it's condition status is true regardless of source.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `akMagicEffect` | `MagicEffect` | ✓ |  |
| `magicSource` | `Form` |  |  |

### `IsMapMarker(Ref) → Bool`

**Flags:** Native Global

returns true if Ref has map data.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Ref` | `ObjectReference` | ✓ |  |

### `IsPCSleeping() → Bool`

**Flags:** Native Global

Same as the IsPCSleeping condition. Returns true if the player is sleeping

### `IsWhiteSpace(c) → Bool`

**Flags:** Native Global

is the string c char whitespace? Uses c++ isspace function

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `c` | `String` | ✓ |  |

### `LoadMostRecentSaveGame() → Bool`

**Flags:** Native Global

### `LogAllAnimations(ref)`

**Flags:** Native Global

Log all animations for the ref, to use with:
objectReference.PlayAnimation, objectReference.playAnimationAndWait, RegisterForAnimationEvent, OnAnimationEvent ect.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |

### `LogAllAnimationsAttributes(ref)`

**Flags:** Native Global

Log all animation attributes for the ref, to use with:

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |

### `LogAllAnimationsCharacterProperties(ref)`

**Flags:** Native Global

Log all animation character properties for the ref, to use with:

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |

### `LogAllAnimationVariables(ref)`

**Flags:** Native Global

Log all animation variables and their values for the ref

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |

### `LogAnimationVariables(ref, variables, type)`

**Flags:** Native Global

Log the animation variables in the variables array for the ref.
Valid types are: 0 = bool, 1 = int, 2 = float, 3 (default) = log all types.
If variables == none (default) logs default variables from the CK wiki page for the type, or all variables from the wiki if type is 3.
CK wiki page: (https://www.creationkit.com/index.php?title=List_of_Animation_Variables). To see which default variables are logged see the
DbAnimationVariableBools.txt, DbAnimationVariableInts.txt and DbAnimationVariableFloats.txt files in Data/Interface/DbMiscFunctions.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `variables` | `String[]` |  |  |
| `type` | `Int` |  | `3` |

### `ModHasFormType(modName, formType) → Bool`

**Flags:** Native Global

does the mod have at least 1 form of formType?

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `modName` | `String` | ✓ |  |
| `formType` | `Int` | ✓ |  |

### `PlaySound(akSound, akSource, volume, eventReceiverForm, eventReceiverAlias, eventReceiverActiveEffect) → Int`

**Flags:** Native Global

PlaySound / PlaySoundDescriptor returns instanceID like Sound.play(), but you can pass in a form, alias or activeMagicEffect to receive the OnSoundFinish Event.
Example, if your script extends form:
DbSkseFunctions.PlaySound(akSound, Game.GetPlayer(), 1.0, self) ;play sound and receive the OnSoundFinish event when sound finishes playing.
You can also set a start volume.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSound` | `Sound` | ✓ |  |
| `akSource` | `ObjectReference` | ✓ |  |
| `volume` | `Float` |  | `1` |
| `eventReceiverForm` | `Form` |  |  |
| `eventReceiverAlias` | `Alias` |  |  |
| `eventReceiverActiveEffect` | `activeMagicEffect` |  |  |

### `PlaySoundDescriptor(akSoundDescriptor, akSource, volume, eventReceiverForm, eventReceiverAlias, eventReceiverActiveEffect) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSoundDescriptor` | `SoundDescriptor` | ✓ |  |
| `akSource` | `ObjectReference` | ✓ |  |
| `volume` | `Float` |  | `1` |
| `eventReceiverForm` | `Form` |  |  |
| `eventReceiverAlias` | `Alias` |  |  |
| `eventReceiverActiveEffect` | `activeMagicEffect` |  |  |

### `RaceSlotMaskHasPartOf(akRace, slotMask) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRace` | `Race` | ✓ |  |
| `slotMask` | `Int` | ✓ |  |

### `RefreshItemMenu()`

**Flags:** Native Global

forces item menus to update if they are open (inventory, container, barter ect...)
To display any changes made to items while an item menu is open, such as changing an item's name.

### `RemoveAdditionalRaceFromArmorAddon(akArmorAddon, akRace)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArmorAddon` | `armorAddon` | ✓ |  |
| `akRace` | `race` | ✓ |  |

### `RemoveFormListAddedForm(akList, akForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akList` | `formlist` | ✓ |  |
| `akForm` | `form` | ✓ |  |

### `RemoveRaceSlotFromMask(akRace, slotMask)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRace` | `Race` | ✓ |  |
| `slotMask` | `Int` | ✓ |  |

### `SetAliasQuestObjectFlag(akAlias, set) → Bool`

**Flags:** Native Global

Sets or clears the Quest Object flag for the akAlias. Returns true if successful

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `alias` | ✓ |  |
| `set` | `Bool` | ✓ |  |

### `SetAllBooksRead(read)`

**Flags:** Native Global

if read is true, set all books in game as 'read', otherwise set all books in game as 'unread'

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `read` | `Bool` | ✓ |  |

### `SetArtObjectNthTextureSet(artObject, textureSet, n)`

**Flags:** Native Global

This function isn't working yet. Technically it's being set successfully internally but can't get it to display in game.
At least with VisualEffect.play(). I'm working on a fix for this.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `artObject` | `art` | ✓ |  |
| `textureSet` | `TextureSet` | ✓ |  |
| `n` | `Int` | ✓ |  |

### `SetBookRead(akBook, read)`

**Flags:** Native Global

if read is true, set akBook  as 'read', otherwise set akBook as 'unread'
if read is false and akBook is a skill book, the skill from the book can be increased again when reading.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akBook` | `book` | ✓ |  |
| `read` | `Bool` | ✓ |  |

### `SetBookSkill(akBook, skill)`

**Flags:** Native Global

sets the skill book teaches. If skill is "", removes TeachesSkill flag from book. (Book will no longer teach a skill.)
not save persistent, use a load game event for maintenance

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akBook` | `book` | ✓ |  |
| `skill` | `String` | ✓ |  |

### `SetBookSpell(akBook, akSpell)`

**Flags:** Native Global

Sets spell book tome teaches. If akSpell is none, removes TeachesSpell flag from book. (Book will no longer teach a spell.)
not save persistent, use a load game event for maintenance

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akBook` | `book` | ✓ |  |
| `akSpell` | `spell` | ✓ |  |

### `SetCanFastTravelToMarker(MapMarker, canTravelTo) → Bool`

**Flags:** Native Global

Get is the vanilla ObjectReference function mapMarker.CanFastTravelToMarker()

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `MapMarker` | `ObjectReference` | ✓ |  |
| `canTravelTo` | `Bool` | ✓ |  |

### `SetCellOffLimits(akCell, bOffLimits)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akCell` | `cell` | ✓ |  |
| `bOffLimits` | `Bool` | ✓ |  |

### `SetCellOrWorldSpaceOriginForRef(ref, cellOrWorldSpace) → Bool`

**Flags:** Native Global

This function is usefull if you have to move a map marker from one worldspace to another using MoveTo and have it display on the world map.
This function will only be successfull if the passed in ref has been moved from its original worldspace or interior cell so...
use moveto on the ref first before this function is used.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `cellOrWorldSpace` | `Form` | ✓ |  |

### `SetCellPublic(akCell, bPublic)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akCell` | `cell` | ✓ |  |
| `bPublic` | `Bool` | ✓ |  |

### `SetClipBoardText(s) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `s` | `String` | ✓ |  |

### `SetCollision(ref, enabled)`

**Flags:** Global

enable or disable collision on ref using tcl console command
requires powerofthree's Tweaks to work.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `enabled` | `Bool` | ✓ |  |

### `SetKeywordString(akKeyword, keywordString)`

**Flags:** Native Global

doesn't carry over between saves. Use load game event for maintenace

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akKeyword` | `keyword` | ✓ |  |
| `keywordString` | `String` | ✓ |  |

### `SetMapMarkerIconType(MapMarker, iconType) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `MapMarker` | `ObjectReference` | ✓ |  |
| `iconType` | `Int` | ✓ |  |

### `SetMapMarkerName(MapMarker, Name) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `MapMarker` | `ObjectReference` | ✓ |  |
| `Name` | `String` | ✓ |  |

### `SetMapMarkerVisible(MapMarker, visible) → Bool`

**Flags:** Native Global

Get is the vanilla ObjectReference function mapMarker.IsMapMarkerVisible()

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `MapMarker` | `ObjectReference` | ✓ |  |
| `visible` | `Bool` | ✓ |  |

### `SetMusicTypePriority(akMusicType, priority)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akMusicType` | `MusicType` | ✓ |  |
| `priority` | `Int` | ✓ |  |

### `SetMusicTypeTrackIndex(akMusicType, index)`

**Flags:** Native Global

if the akMusicType is currently playing, it will jump to the track index passed in.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akMusicType` | `MusicType` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `SetProjectileBaseCollisionConeSpread(akProjectile, coneSpread) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akProjectile` | `projectile` | ✓ |  |
| `coneSpread` | `Float` | ✓ |  |

### `SetProjectileBaseCollisionRadius(akProjectile, radius) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akProjectile` | `projectile` | ✓ |  |
| `radius` | `Float` | ✓ |  |

### `SetProjectileBaseDecal(akProjectile, decalTextureSet) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akProjectile` | `projectile` | ✓ |  |
| `decalTextureSet` | `TextureSet` | ✓ |  |

### `SetProjectileBaseExplosion(akProjectile, akExplosion) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akProjectile` | `projectile` | ✓ |  |
| `akExplosion` | `Explosion` | ✓ |  |

### `SetRaceSlotMask(akRace, slotMask)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRace` | `Race` | ✓ |  |
| `slotMask` | `Int` | ✓ |  |

### `SetSoulGemCanHoldNPCSoul(akSoulGem, canHold)`

**Flags:** Native Global

set soul gem can hold npc soul, I.E make it a black soul gem or not.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSoulGem` | `SoulGem` | ✓ |  |
| `canHold` | `Bool` | ✓ |  |

### `SetSoulGemSize(akSoulGem, level)`

**Flags:** Native Global

set the max Soul size the soulGem (base form) can hold. 0 = no soul up to 5 = grand.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSoulGem` | `SoulGem` | ✓ |  |
| `level` | `Int` | ✓ |  |

### `SetSoundCategoryForSoundDescriptor(akSoundDescriptor, akSoundCategory)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSoundDescriptor` | `SoundDescriptor` | ✓ |  |
| `akSoundCategory` | `SoundCategory` | ✓ |  |

### `SetSoundInstanceSource(instanceID, ref) → Bool`

**Flags:** Native Global

set the sound source for the currently playing soundId to the passed in ref.
this function will only work for sounds playing from PlaySound or PlaySoundDescriptor from this script
I also found a strange bug. If the sound's source is the player and the player is in first person, this function will fail to set the ref as the new source.
If however the player is in third person, this function will succeed in setting the ref as the new source for the sound instanceID

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `instanceID` | `Int` | ✓ |  |
| `ref` | `ObjectReference` | ✓ |  |

### `SortFormArray(akForms, sortOption) → Form[]`

**Flags:** Native Global

returns new form array that contains the forms of the passed in akForms array, but sorted.
Sort options are as follows. Note, to sort by editor Id reliably, po3 tweaks must be installed.
1 = by form name ascending,
2 = by form name descending,
3 = by form editor Id name ascending,
4 = by form editor Id name descending,
5 = by form Id ascending,
6 = by form Id descending

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForms` | `Form[]` | ✓ |  |
| `sortOption` | `Int` |  | `1` |

### `TestCv() → Int`

**Flags:** Native Global

### `ToggleCollision(ref)`

**Flags:** Global

toggle collision on ref using tcl console command
requires powerofthree's Tweaks to work.
https://www.nexusmods.com/skyrimspecialedition/mods/51073?tab=description

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |

### `TraceUiMenuTargetMembersData(menu, target, asUserLog)`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `menu` | `String` | ✓ |  |
| `target` | `String` | ✓ |  |
| `asUserLog` | `String` |  | `""` |

### `UnlockShout(akShout)`

**Flags:** Native Global

add shout to player if necessary and unlock all of its Words

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akShout` | `shout` | ✓ |  |

### `UpdateActor3DModel(akActor)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `UpdateActor3DPosition(akActor, warp)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `warp` | `Bool` |  | `false` |

### `UpdateRefLight(ref)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |

### `WouldActorBeStealing(akActor, akTarget) → Bool`

**Flags:** Native Global

would the akActor be stealing the akTarget if they took it?

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `actor` | ✓ |  |
| `akTarget` | `ObjectReference` | ✓ |  |


---

## `DbSksePersistentVariables`

**Source:** `dylbills` (Dylbills Papyrus Functions) • **Extends:** `Actor`

---

## Properties

### `LastPlayerActivatedRef: ObjectReference`

**Flags:** Auto Hidden

**Accessors:** Get / Set

### `LastPlayerMenuActivatedRef: ObjectReference`

**Flags:** Auto Hidden

**Accessors:** Get / Set

---

## Events

### `OnDbSksePlayerActivatedMenuRef(ref)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `ref` | `ObjectReference` |

### `OnDbSksePlayerActivatedRef(ref)`

**Kind:** Event

these events are no longer sent do to performance issues. The above properties are now filled directly from c++

**Parameters**

| Name | Type |
|---|---|
| `ref` | `ObjectReference` |


---

## `DynamicActorArrays`

**Source:** `dylbills` (Dylbills Papyrus Functions) • **Extends:** `Form`

---

## Functions

### `Clear(akArray, ToClear) → Actor[]`

Remove all of the ToClear elements from the akArray and return new array.
The length of the new array must be 128 or less, otherwise returns the akArray unedited.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Actor[]` | ✓ |  |
| `ToClear` | `Actor` | ✓ |  |

### `Count(akArray, ToCount) → Int`

count how many of the ToCount elements are in the array.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Actor[]` | ✓ |  |
| `ToCount` | `Actor` | ✓ |  |

### `CreateArray(size) → Actor[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `size` | `Int` | ✓ |  |

### `Duplicate(akArray) → Actor[]`

Copy all the elements from akArray to NewArray and return NewArray.
Only copy's up to 128 elements.
different than doing ArrayA = ArrayB.
When doing that, altering ArrayB will also alter ArrayA. Not so with these copy functions.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Actor[]` | ✓ |  |

### `GetArray() → Actor[]`

For the create array functions.

### `InsertArrayAt(akArray, ToInsert, Index) → Actor[]`

Insert the ToInsert array to the akArray at Index and return new array.
Passed in akArray must be less than 128 elements in length.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Actor[]` | ✓ |  |
| `ToInsert` | `Actor[]` | ✓ |  |
| `Index` | `Int` | ✓ |  |

### `InsertAt(akArray, ToInsert, Index) → Actor[]`

insert the ToInsert Actor into the array, increasing the size by one and
moving each Actor after index back by one, returning the new array.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Actor[]` | ✓ |  |
| `ToInsert` | `Actor` | ✓ |  |
| `Index` | `Int` | ✓ |  |

### `Join(a_Array, b_Array) → Actor[]`

Join a_Array with b_Array and return new array.
The added lengths of the arrays must be less than or equil to 128 elements.
If greater than, the tail end of b_array is clipped off where it exceeds 128.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_Array` | `Actor[]` | ✓ |  |
| `b_Array` | `Actor[]` | ✓ |  |

### `Push(akArray, ToPush) → Actor[]`

Add an element to the end of the array and return new array.
The passed in akArray must be less than 128 elements in length.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Actor[]` | ✓ |  |
| `ToPush` | `Actor` | ✓ |  |

### `Remove(akArray, ToRemove, First) → Actor[]`

Find the ToRemove element in the akArray and remove it, returning the shortened array.
If First == true (default) finds first instance of ToRemove, otherwise finds last instance of ToRemove (rFind)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Actor[]` | ✓ |  |
| `ToRemove` | `Actor` | ✓ |  |
| `First` | `Bool` |  | `true` |

### `RemoveAt(akArray, Index) → Actor[]`

Remove the element at the Index of the akArray and return new array.
Passed in array must be less than or equal to 129 elements in length.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Actor[]` | ✓ |  |
| `Index` | `Int` | ✓ |  |

### `Resize(akArray, NewSize, Fill) → Actor[]`

Resize akArray to NewSize and return New Array.
If NewSize is less than current size, removes elements after NewSize in akArray.
If NewSize is greater than current size, the Fill element to the end of the akArray.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Actor[]` | ✓ |  |
| `NewSize` | `Int` | ✓ |  |
| `Fill` | `Actor` |  |  |

### `Shift(akArray, First) → Actor[]`

Remove either the first or last element from the array and return new shortened array
Passed in array must be less than or equal to 129 elements in length.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Actor[]` | ✓ |  |
| `First` | `Bool` |  | `true` |

### `SubArray(akArray, StartIndex, EndIndex) → Actor[]`

Put the elements between StartIndex and EndIndex of akArray into a new array and return said array.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Actor[]` | ✓ |  |
| `StartIndex` | `Int` | ✓ |  |
| `EndIndex` | `Int` | ✓ |  |


---

## `DynamicArrays`

**Source:** `dylbills` (Dylbills Papyrus Functions) • **Extends:** `Form`

---

## Functions

### `ClearActors(akArray, ToClear) → Actor[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Actor[]` | ✓ |  |
| `ToClear` | `Actor` | ✓ |  |

### `ClearBools(akArray, ToClear) → Bool[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Bool[]` | ✓ |  |
| `ToClear` | `Bool` | ✓ |  |

### `ClearFloats(akArray, ToClear) → Float[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Float[]` | ✓ |  |
| `ToClear` | `Float` | ✓ |  |

### `ClearForms(akArray, ToClear) → Form[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Form[]` | ✓ |  |
| `ToClear` | `Form` | ✓ |  |

### `ClearInts(akArray, ToClear) → Int[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Int[]` | ✓ |  |
| `ToClear` | `Int` | ✓ |  |

### `ClearObjectReferences(akArray, ToClear) → ObjectReference[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `ObjectReference[]` | ✓ |  |
| `ToClear` | `ObjectReference` | ✓ |  |

### `ClearStrings(akArray, ToClear) → String[]`

Clear==================================================================================

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `String[]` | ✓ |  |
| `ToClear` | `String` | ✓ |  |

### `CopyActorArray(akArray) → Actor[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Actor[]` | ✓ |  |

### `CopyBoolArray(akArray) → Bool[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Bool[]` | ✓ |  |

### `CopyFloatArray(akArray) → Float[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Float[]` | ✓ |  |

### `CopyFormArray(akArray) → Form[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Form[]` | ✓ |  |

### `CopyIntArray(akArray) → Int[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Int[]` | ✓ |  |

### `CopyObjectReferenceArray(akArray) → ObjectReference[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `ObjectReference[]` | ✓ |  |

### `CopyStringArray(akArray) → String[]`

Copy==========================================================

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `String[]` | ✓ |  |

### `CountActors(akArray, ToCount) → Int`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Actor[]` | ✓ |  |
| `ToCount` | `Actor` | ✓ |  |

### `CountBools(akArray, ToCount) → Int`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Bool[]` | ✓ |  |
| `ToCount` | `Bool` | ✓ |  |

### `CountFloats(akArray, ToCount) → Int`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Float[]` | ✓ |  |
| `ToCount` | `Float` | ✓ |  |

### `CountForms(akArray, ToCount) → Int`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Form[]` | ✓ |  |
| `ToCount` | `Form` | ✓ |  |

### `CountInts(akArray, ToCount) → Int`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Int[]` | ✓ |  |
| `ToCount` | `Int` | ✓ |  |

### `CountObjectReferences(akArray, ToCount) → Int`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `ObjectReference[]` | ✓ |  |
| `ToCount` | `ObjectReference` | ✓ |  |

### `CountStrings(akArray, ToCount) → Int`

count=========================================================
count how many of the ToCount elements are in the array.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `String[]` | ✓ |  |
| `ToCount` | `String` | ✓ |  |

### `CreateActorArray(L) → Actor[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `L` | `Int` | ✓ |  |

### `CreateBoolArray(L) → Bool[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `L` | `Int` | ✓ |  |

### `CreateFloatArray(L) → Float[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `L` | `Int` | ✓ |  |

### `CreateFormArray(L) → Form[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `L` | `Int` | ✓ |  |

### `CreateIntArray(L) → Int[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `L` | `Int` | ✓ |  |

### `CreateObjectReferenceArray(L) → ObjectReference[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `L` | `Int` | ✓ |  |

### `CreateStringArray(L) → String[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `L` | `Int` | ✓ |  |

### `GetActorArray() → Actor[]`

### `GetBoolArray() → Bool[]`

### `GetFloatArray() → Float[]`

### `GetFormArray() → Form[]`

### `GetIntArray() → Int[]`

### `GetObjectReferenceArray() → ObjectReference[]`

### `GetStringArray() → String[]`

For the create array functions.
functions must also be defined in the empty state.

### `InsertActorArrayAt(akArray, ToInsert, Index) → Actor[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Actor[]` | ✓ |  |
| `ToInsert` | `Actor[]` | ✓ |  |
| `Index` | `Int` | ✓ |  |

### `InsertActorAt(akArray, ToInsert, Index) → Actor[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Actor[]` | ✓ |  |
| `ToInsert` | `Actor` | ✓ |  |
| `Index` | `Int` | ✓ |  |

### `InsertBoolArrayAt(akArray, ToInsert, Index) → Bool[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Bool[]` | ✓ |  |
| `ToInsert` | `Bool[]` | ✓ |  |
| `Index` | `Int` | ✓ |  |

### `InsertBoolAt(akArray, ToInsert, Index) → Bool[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Bool[]` | ✓ |  |
| `ToInsert` | `Bool` | ✓ |  |
| `Index` | `Int` | ✓ |  |

### `InsertFloatArrayAt(akArray, ToInsert, Index) → Float[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Float[]` | ✓ |  |
| `ToInsert` | `Float[]` | ✓ |  |
| `Index` | `Int` | ✓ |  |

### `InsertFloatAt(akArray, ToInsert, Index) → Float[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Float[]` | ✓ |  |
| `ToInsert` | `Float` | ✓ |  |
| `Index` | `Int` | ✓ |  |

### `InsertFormArrayAt(akArray, ToInsert, Index) → Form[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Form[]` | ✓ |  |
| `ToInsert` | `Form[]` | ✓ |  |
| `Index` | `Int` | ✓ |  |

### `InsertFormAt(akArray, ToInsert, Index) → Form[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Form[]` | ✓ |  |
| `ToInsert` | `Form` | ✓ |  |
| `Index` | `Int` | ✓ |  |

### `InsertIntArrayAt(akArray, ToInsert, Index) → Int[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Int[]` | ✓ |  |
| `ToInsert` | `Int[]` | ✓ |  |
| `Index` | `Int` | ✓ |  |

### `InsertIntAt(akArray, ToInsert, Index) → Int[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Int[]` | ✓ |  |
| `ToInsert` | `Int` | ✓ |  |
| `Index` | `Int` | ✓ |  |

### `InsertObjectReferenceArrayAt(akArray, ToInsert, Index) → ObjectReference[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `ObjectReference[]` | ✓ |  |
| `ToInsert` | `ObjectReference[]` | ✓ |  |
| `Index` | `Int` | ✓ |  |

### `InsertObjectReferenceAt(akArray, ToInsert, Index) → ObjectReference[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `ObjectReference[]` | ✓ |  |
| `ToInsert` | `ObjectReference` | ✓ |  |
| `Index` | `Int` | ✓ |  |

### `InsertStringArrayAt(akArray, ToInsert, Index) → String[]`

InsertArrayAt====================================================================

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `String[]` | ✓ |  |
| `ToInsert` | `String[]` | ✓ |  |
| `Index` | `Int` | ✓ |  |

### `InsertStringAt(akArray, ToInsert, Index) → String[]`

InsertAt====================================================================

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `String[]` | ✓ |  |
| `ToInsert` | `String` | ✓ |  |
| `Index` | `Int` | ✓ |  |

### `JoinActorArrays(a_Array, b_Array) → Actor[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_Array` | `Actor[]` | ✓ |  |
| `b_Array` | `Actor[]` | ✓ |  |

### `JoinBoolArrays(a_Array, b_Array) → Bool[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_Array` | `Bool[]` | ✓ |  |
| `b_Array` | `Bool[]` | ✓ |  |

### `JoinFloatArrays(a_Array, b_Array) → Float[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_Array` | `Float[]` | ✓ |  |
| `b_Array` | `Float[]` | ✓ |  |

### `JoinFormArrays(a_Array, b_Array) → Form[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_Array` | `Form[]` | ✓ |  |
| `b_Array` | `Form[]` | ✓ |  |

### `JoinIntArrays(a_Array, b_Array) → Int[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_Array` | `Int[]` | ✓ |  |
| `b_Array` | `Int[]` | ✓ |  |

### `JoinObjectReferenceArrays(a_Array, b_Array) → ObjectReference[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_Array` | `ObjectReference[]` | ✓ |  |
| `b_Array` | `ObjectReference[]` | ✓ |  |

### `JoinStringArrays(a_Array, b_Array) → String[]`

Join =============================================================================

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_Array` | `String[]` | ✓ |  |
| `b_Array` | `String[]` | ✓ |  |

### `PushActor(akArray, ToPush) → Actor[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Actor[]` | ✓ |  |
| `ToPush` | `Actor` | ✓ |  |

### `PushBool(akArray, ToPush) → Bool[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Bool[]` | ✓ |  |
| `ToPush` | `Bool` | ✓ |  |

### `PushFloat(akArray, ToPush) → Float[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Float[]` | ✓ |  |
| `ToPush` | `Float` | ✓ |  |

### `PushForm(akArray, ToPush) → Form[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Form[]` | ✓ |  |
| `ToPush` | `Form` | ✓ |  |

### `PushInt(akArray, ToPush) → Int[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Int[]` | ✓ |  |
| `ToPush` | `Int` | ✓ |  |

### `PushObjectReference(akArray, ToPush) → ObjectReference[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `ObjectReference[]` | ✓ |  |
| `ToPush` | `ObjectReference` | ✓ |  |

### `PushString(akArray, ToPush) → String[]`

Push=============================================================

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `String[]` | ✓ |  |
| `ToPush` | `String` | ✓ |  |

### `RemoveActor(akArray, ToRemove, First) → Actor[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Actor[]` | ✓ |  |
| `ToRemove` | `Actor` | ✓ |  |
| `First` | `Bool` |  | `true` |

### `RemoveActorAt(akArray, Index) → Actor[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Actor[]` | ✓ |  |
| `Index` | `Int` | ✓ |  |

### `RemoveBool(akArray, ToRemove, First) → Bool[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Bool[]` | ✓ |  |
| `ToRemove` | `Bool` | ✓ |  |
| `First` | `Bool` |  | `true` |

### `RemoveBoolAt(akArray, Index) → Bool[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Bool[]` | ✓ |  |
| `Index` | `Int` | ✓ |  |

### `RemoveFloat(akArray, ToRemove, First) → Float[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Float[]` | ✓ |  |
| `ToRemove` | `Float` | ✓ |  |
| `First` | `Bool` |  | `true` |

### `RemoveFloatAt(akArray, Index) → Float[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Float[]` | ✓ |  |
| `Index` | `Int` | ✓ |  |

### `RemoveForm(akArray, ToRemove, First) → Form[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Form[]` | ✓ |  |
| `ToRemove` | `Form` | ✓ |  |
| `First` | `Bool` |  | `true` |

### `RemoveFormAt(akArray, Index) → Form[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Form[]` | ✓ |  |
| `Index` | `Int` | ✓ |  |

### `RemoveInt(akArray, ToRemove, First) → Int[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Int[]` | ✓ |  |
| `ToRemove` | `Int` | ✓ |  |
| `First` | `Bool` |  | `true` |

### `RemoveIntAt(akArray, Index) → Int[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Int[]` | ✓ |  |
| `Index` | `Int` | ✓ |  |

### `RemoveObjectReference(akArray, ToRemove, First) → ObjectReference[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `ObjectReference[]` | ✓ |  |
| `ToRemove` | `ObjectReference` | ✓ |  |
| `First` | `Bool` |  | `true` |

### `RemoveObjectReferenceAt(akArray, Index) → ObjectReference[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `ObjectReference[]` | ✓ |  |
| `Index` | `Int` | ✓ |  |

### `RemoveString(akArray, ToRemove, First) → String[]`

Remove =============================================================================

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `String[]` | ✓ |  |
| `ToRemove` | `String` | ✓ |  |
| `First` | `Bool` |  | `true` |

### `RemoveStringAt(akArray, Index) → String[]`

RemoveAt============================================================

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `String[]` | ✓ |  |
| `Index` | `Int` | ✓ |  |

### `ResizeActorArray(akArray, NewSize, Fill) → Actor[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Actor[]` | ✓ |  |
| `NewSize` | `Int` | ✓ |  |
| `Fill` | `Actor` |  |  |

### `ResizeBoolArray(akArray, NewSize, Fill) → Bool[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Bool[]` | ✓ |  |
| `NewSize` | `Int` | ✓ |  |
| `Fill` | `Bool` |  | `false` |

### `ResizeFloatArray(akArray, NewSize, Fill) → Float[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Float[]` | ✓ |  |
| `NewSize` | `Int` | ✓ |  |
| `Fill` | `Float` |  | `0` |

### `ResizeFormArray(akArray, NewSize, Fill) → Form[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Form[]` | ✓ |  |
| `NewSize` | `Int` | ✓ |  |
| `Fill` | `Form` |  |  |

### `ResizeIntArray(akArray, NewSize, Fill) → Int[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Int[]` | ✓ |  |
| `NewSize` | `Int` | ✓ |  |
| `Fill` | `Int` |  | `0` |

### `ResizeObjectReferenceArray(akArray, NewSize, Fill) → ObjectReference[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `ObjectReference[]` | ✓ |  |
| `NewSize` | `Int` | ✓ |  |
| `Fill` | `ObjectReference` |  |  |

### `ResizeStringArray(akArray, NewSize, Fill) → String[]`

Resize ===========================================================================

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `String[]` | ✓ |  |
| `NewSize` | `Int` | ✓ |  |
| `Fill` | `String` |  | `""` |

### `ShiftActor(akArray, First) → Actor[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Actor[]` | ✓ |  |
| `First` | `Bool` |  | `true` |

### `ShiftBool(akArray, First) → Bool[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Bool[]` | ✓ |  |
| `First` | `Bool` |  | `true` |

### `ShiftFloat(akArray, First) → Float[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Float[]` | ✓ |  |
| `First` | `Bool` |  | `true` |

### `ShiftForm(akArray, First) → Form[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Form[]` | ✓ |  |
| `First` | `Bool` |  | `true` |

### `ShiftInt(akArray, First) → Int[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Int[]` | ✓ |  |
| `First` | `Bool` |  | `true` |

### `ShiftObjectReference(akArray, First) → ObjectReference[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `ObjectReference[]` | ✓ |  |
| `First` | `Bool` |  | `true` |

### `ShiftString(akArray, First) → String[]`

Shift============================================================

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `String[]` | ✓ |  |
| `First` | `Bool` |  | `true` |

### `SortFloatArray(akArray, Ascending, Direct) → Float[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Float[]` | ✓ |  |
| `Ascending` | `Bool` |  | `true` |
| `Direct` | `Bool` |  | `true` |

### `SortIntArray(akArray, Ascending, Direct) → Int[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Int[]` | ✓ |  |
| `Ascending` | `Bool` |  | `true` |
| `Direct` | `Bool` |  | `true` |

### `SortStringArray(akArray, Ascending, Direct) → String[]`

Sort========================================================================================

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `String[]` | ✓ |  |
| `Ascending` | `Bool` |  | `true` |
| `Direct` | `Bool` |  | `true` |

### `SubActorArray(akArray, StartIndex, EndIndex) → Actor[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Actor[]` | ✓ |  |
| `StartIndex` | `Int` | ✓ |  |
| `EndIndex` | `Int` | ✓ |  |

### `SubBoolArray(akArray, StartIndex, EndIndex) → Bool[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Bool[]` | ✓ |  |
| `StartIndex` | `Int` | ✓ |  |
| `EndIndex` | `Int` | ✓ |  |

### `SubFloatArray(akArray, StartIndex, EndIndex) → Float[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Float[]` | ✓ |  |
| `StartIndex` | `Int` | ✓ |  |
| `EndIndex` | `Int` | ✓ |  |

### `SubFormArray(akArray, StartIndex, EndIndex) → Form[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Form[]` | ✓ |  |
| `StartIndex` | `Int` | ✓ |  |
| `EndIndex` | `Int` | ✓ |  |

### `SubIntArray(akArray, StartIndex, EndIndex) → Int[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `Int[]` | ✓ |  |
| `StartIndex` | `Int` | ✓ |  |
| `EndIndex` | `Int` | ✓ |  |

### `SubObjectReferenceArray(akArray, StartIndex, EndIndex) → ObjectReference[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `ObjectReference[]` | ✓ |  |
| `StartIndex` | `Int` | ✓ |  |
| `EndIndex` | `Int` | ✓ |  |

### `SubStringArray(akArray, StartIndex, EndIndex) → String[]`

Sub arrays =============================================================================

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `String[]` | ✓ |  |
| `StartIndex` | `Int` | ✓ |  |
| `EndIndex` | `Int` | ✓ |  |


---

## `DynamicArrays_B`

**Source:** `dylbills` (Dylbills Papyrus Functions) • **Extends:** `Form`

---

## Functions

### `CreateActorArray(L) → Actor[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `L` | `Int` | ✓ |  |

### `CreateBoolArray(L) → Bool[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `L` | `Int` | ✓ |  |

### `CreateFloatArray(L) → Float[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `L` | `Int` | ✓ |  |

### `CreateFormArray(L) → Form[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `L` | `Int` | ✓ |  |

### `CreateIntArray(L) → Int[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `L` | `Int` | ✓ |  |

### `CreateObjectReferenceArray(L) → ObjectReference[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `L` | `Int` | ✓ |  |

### `CreateStringArray(L) → String[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `L` | `Int` | ✓ |  |

### `GetActorArray() → Actor[]`

### `GetBoolArray() → Bool[]`

### `GetFloatArray() → Float[]`

### `GetFormArray() → Form[]`

### `GetIntArray() → Int[]`

### `GetObjectReferenceArray() → ObjectReference[]`

### `GetStringArray() → String[]`

functions must also be defined in the empty state


---

## `DynamicStringArrays`

**Source:** `dylbills` (Dylbills Papyrus Functions) • **Extends:** `Form`

---

## Functions

### `Clear(akArray, ToClear) → String[]`

Remove all of the ToClear elements from the akArray and return new array.
The length of the new array must be 128 or less, otherwise returns the akArray unedited.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `String[]` | ✓ |  |
| `ToClear` | `String` | ✓ |  |

### `Count(akArray, ToCount) → Int`

count how many of the ToCount elements are in the array.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `String[]` | ✓ |  |
| `ToCount` | `String` | ✓ |  |

### `CreateArray(size) → String[]`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `size` | `Int` | ✓ |  |

### `Duplicate(akArray) → String[]`

Copy all the elements from akArray to NewArray and return NewArray.
Only copy's up to 128 elements.
different than doing ArrayA = ArrayB.
When doing that, altering ArrayB will also alter ArrayA. Not so with these copy functions.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `String[]` | ✓ |  |

### `GetArray() → String[]`

For the create array functions.

### `InsertArrayAt(akArray, ToInsert, Index) → String[]`

Insert the ToInsert array to the akArray at Index and return new array.
Passed in akArray must be less than 128 elements in length.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `String[]` | ✓ |  |
| `ToInsert` | `String[]` | ✓ |  |
| `Index` | `Int` | ✓ |  |

### `InsertAt(akArray, ToInsert, Index) → String[]`

insert the ToInsert string into the array, increasing the size by one and
moving each string after index back by one, returning the new array.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `String[]` | ✓ |  |
| `ToInsert` | `String` | ✓ |  |
| `Index` | `Int` | ✓ |  |

### `Join(a_Array, b_Array) → String[]`

Join a_Array with b_Array and return new array.
The added lengths of the arrays must be less than or equil to 128 elements.
If greater than, the tail end of b_array is clipped off where it exceeds 128.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_Array` | `String[]` | ✓ |  |
| `b_Array` | `String[]` | ✓ |  |

### `Push(akArray, ToPush) → String[]`

Add an element to the end of the array and return new array.
The passed in akArray must be less than 128 elements in length.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `String[]` | ✓ |  |
| `ToPush` | `String` | ✓ |  |

### `Remove(akArray, ToRemove, First) → String[]`

Find the ToRemove element in the akArray and remove it, returning the shortened array.
If First == true (default) finds first instance of ToRemove, otherwise finds last instance of ToRemove (rFind)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `String[]` | ✓ |  |
| `ToRemove` | `String` | ✓ |  |
| `First` | `Bool` |  | `true` |

### `RemoveAt(akArray, Index) → String[]`

Remove the element at the Index of the akArray and return new array.
Passed in array must be less than or equal to 129 elements in length.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `String[]` | ✓ |  |
| `Index` | `Int` | ✓ |  |

### `Resize(akArray, NewSize, Fill) → String[]`

Resize akArray to NewSize and return New Array.
If NewSize is less than current size, removes elements after NewSize in akArray.
If NewSize is greater than current size, the Fill element to the end of the akArray.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `String[]` | ✓ |  |
| `NewSize` | `Int` | ✓ |  |
| `Fill` | `String` |  | `""` |

### `Shift(akArray, First) → String[]`

Remove either the first or last element from the array and return new shortened array
Passed in array must be less than or equal to 129 elements in length.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `String[]` | ✓ |  |
| `First` | `Bool` |  | `true` |

### `Sort(akArray, Ascending, Direct) → String[]`

Sort the array from smallest to largest or vice versa.
Note that if Direct == true (default) this affects the passed in akArray directly.
So no need to do MyIntArray[] = SortIntArray(MyIntArray)
Can just do: SortIntArray(MyIntArray)
If Direct == false, it first copy's the array, sorts the copied array and returns the copied array so the passed in akArray is unaffected.
If Direct == false, passed in akArray must be less than or equal to 128 elements in length.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `String[]` | ✓ |  |
| `Ascending` | `Bool` |  | `true` |
| `Direct` | `Bool` |  | `true` |

### `SubArray(akArray, StartIndex, EndIndex) → String[]`

Put the elements between StartIndex and EndIndex of akArray into a new array and return said array.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akArray` | `String[]` | ✓ |  |
| `StartIndex` | `Int` | ✓ |  |
| `EndIndex` | `Int` | ✓ |  |


---

## `PapyrusUtilEx`

**Source:** `dylbills` (Dylbills Papyrus Functions) • **Flags:** Hidden

---

## Global Functions

### `CopyArray(akHandle_A, sScriptName_A, sArrayPropertyName_A, akHandle_B, sScriptName_B, sArrayPropertyName_B) → Bool`

**Flags:** Native Global

Replace the _B array with a copy of the _A array. The _A array is unaltered.
Array _A and array _B must be the same type. Remember both arrays must be already initialized.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akHandle_A` | `String` | ✓ |  |
| `sScriptName_A` | `String` | ✓ |  |
| `sArrayPropertyName_A` | `String` | ✓ |  |
| `akHandle_B` | `String` | ✓ |  |
| `sScriptName_B` | `String` | ✓ |  |
| `sArrayPropertyName_B` | `String` | ✓ |  |

### `CountInArray(akHandle, sScriptName, sArrayPropertyName, index) → Int`

**Flags:** Native Global

Returns the number of instances of the element at the index that the array contains.
A value of -1 for the index (default) means the last element in the array.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akHandle` | `String` | ✓ |  |
| `sScriptName` | `String` | ✓ |  |
| `sArrayPropertyName` | `String` | ✓ |  |
| `index` | `Int` |  | `-1` |

### `GetActiveEffectHandle(akActiveEffect) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActiveEffect` | `ActiveMagicEffect` | ✓ |  |

### `GetAliasHandle(akAlias) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `Alias` | ✓ |  |

### `GetFormHandle(akForm) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `MergeArrays(akHandle_A, sScriptName_A, sArrayPropertyName_A, akHandle_B, sScriptName_B, sArrayPropertyName_B) → Bool`

**Flags:** Native Global

Merge the _A array to the end of the _B array, increasing _B array's size. The _A array is unaltered.
Array _A and array _B must be the same type. Remember both arrays must be already initialized.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akHandle_A` | `String` | ✓ |  |
| `sScriptName_A` | `String` | ✓ |  |
| `sArrayPropertyName_A` | `String` | ✓ |  |
| `akHandle_B` | `String` | ✓ |  |
| `sScriptName_B` | `String` | ✓ |  |
| `sArrayPropertyName_B` | `String` | ✓ |  |

### `RemoveFromArray(akHandle, sScriptName, sArrayPropertyName, index, removeAll) → Int`

**Flags:** Native Global

Remove the element at the index from the array.
If the removeAll parameter is true, removes all elements from the array that match the element at the index
A value of -1 for the index (default) means the last element in the array.
Returns the amount of elements removed, if it returns 0 it means the index wasn't valid (>= array.length)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akHandle` | `String` | ✓ |  |
| `sScriptName` | `String` | ✓ |  |
| `sArrayPropertyName` | `String` | ✓ |  |
| `index` | `Int` |  | `-1` |
| `removeAll` | `Bool` |  | `false` |

### `ResizeArray(akHandle, sScriptName, sArrayPropertyName, size, fillIndex) → Bool`

**Flags:** Native Global

Resize a papyrus array.
if the array is sized to larger than before, the rest of the array is filled with the element at the fillIndex in the array.
A value of -1 (default) for the fillIndex means the last element in the array.
If the array is sized smaller the elements past the new size are removed from the array.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akHandle` | `String` | ✓ |  |
| `sScriptName` | `String` | ✓ |  |
| `sArrayPropertyName` | `String` | ✓ |  |
| `size` | `Int` | ✓ |  |
| `fillIndex` | `Int` |  | `-1` |

### `SliceArray(akHandle, sScriptName, sArrayPropertyName, startIndex, endIndex, keep) → Bool`

**Flags:** Native Global

Remove a portion of the array.
If keep is true (default) it keeps the portion between the startIndex and endIndex and removes the rest. If keep is false, it removes the portion between the startIndex and endIndex.
A value of -1 for the endIndex (default) means the last element in the array.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akHandle` | `String` | ✓ |  |
| `sScriptName` | `String` | ✓ |  |
| `sArrayPropertyName` | `String` | ✓ |  |
| `startIndex` | `Int` | ✓ |  |
| `endIndex` | `Int` |  | `-1` |
| `keep` | `Bool` |  | `true` |

### `SliceArrayOnto(akHandle_A, sScriptName_A, sArrayPropertyName_A, akHandle_B, sScriptName_B, sArrayPropertyName_B, startIndex, endIndex, replace, keep) → Bool`

**Flags:** Native Global

Take a portion of the _A array and remove it, merging it with or replacing the _B array depending on the replace parameter.
If keep is true (default) it keeps the portion between the startIndex and endIndex and removes the rest. If keep is false, it removes the portion between the startIndex and endIndex.
Array _A and array _B must be the same type. Remember both arrays must be already initialized.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akHandle_A` | `String` | ✓ |  |
| `sScriptName_A` | `String` | ✓ |  |
| `sArrayPropertyName_A` | `String` | ✓ |  |
| `akHandle_B` | `String` | ✓ |  |
| `sScriptName_B` | `String` | ✓ |  |
| `sArrayPropertyName_B` | `String` | ✓ |  |
| `startIndex` | `Int` | ✓ |  |
| `endIndex` | `Int` |  | `-1` |
| `replace` | `Bool` |  | `false` |
| `keep` | `Bool` |  | `true` |
