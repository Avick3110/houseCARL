# `PyramidUtils`

**Source:** `scrab` (Scrab's Papyrus Extender) • **Flags:** Hidden

---

## Global Functions

### `ConsoleGetAbsPos(akRef) → String`

**Flags:** Global

custom console proxy functions - ignore these

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `Form` | ✓ |  |

### `ConsoleGetPlayerAbsDist(akRef) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `Form` | ✓ |  |

### `Dismount(akTarget)`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akTarget` | `Actor` | ✓ |  |

### `FilterByEnchanted(akContainer, akForms, abEnchanted) → Form[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akContainer` | `ObjectReference` | ✓ |  |
| `akForms` | `Form[]` | ✓ |  |
| `abEnchanted` | `Bool` |  | `true` |

### `FilterByEquippedSlot(akForms, aiSlots, abAll) → Form[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForms` | `Form[]` | ✓ |  |
| `aiSlots` | `Int[]` | ✓ |  |
| `abAll` | `Bool` |  | `false` |

### `FilterFormsByGoldValue(akForms, aiValue, abGreaterThan, abEqual) → Form[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForms` | `Form[]` | ✓ |  |
| `aiValue` | `Int` | ✓ |  |
| `abGreaterThan` | `Bool` |  | `true` |
| `abEqual` | `Bool` |  | `true` |

### `FilterFormsByKeyword(akForms, akKeywords, abMatchAll, abInvert) → Form[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForms` | `Form[]` | ✓ |  |
| `akKeywords` | `Keyword[]` | ✓ |  |
| `abMatchAll` | `Bool` |  | `false` |
| `abInvert` | `Bool` |  | `false` |

### `FormHasKeyword(akItem, akKwds, abAll) → Bool`

**Flags:** Global

Form Processing

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akItem` | `Form` | ✓ |  |
| `akKwds` | `Keyword[]` | ✓ |  |
| `abAll` | `Bool` |  | `false` |

### `FormHasKeywordStrings(akItem, akKwds, abAll) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akItem` | `Form` | ✓ |  |
| `akKwds` | `String[]` | ✓ |  |
| `abAll` | `Bool` |  | `false` |

### `GetAbsPosX(akRef) → Float`

**Flags:** Global

uses worldspace offsets to get absolute position on external refs

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |

### `GetAbsPosY(akRef) → Float`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |

### `GetAbsPosZ(akRef) → Float`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |

### `GetButtonForDXScanCode(aiCode) → String`

**Flags:** Global

Input

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiCode` | `Int` | ✓ |  |

### `GetDetectedBy(akActor) → Actor[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetExteriorLocations(akCell) → Location[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akCell` | `Cell` | ✓ |  |

### `GetExteriorWorldSpaces(akCell) → WorldSpace[]`

**Flags:** Global

if cell is exterior gets worldspace like normal, if interior looks for external doors and their worldspace

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akCell` | `Cell` | ✓ |  |

### `GetGlobal(asEditorID) → GlobalVariable`

**Flags:** Global

misc

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asEditorID` | `String` | ✓ |  |

### `GetInventoryNamedObjects(akContainer, asNames) → Form[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akContainer` | `ObjectReference` | ✓ |  |
| `asNames` | `String[]` | ✓ |  |

### `GetItemsByKeyword(akContainer, akKeywords, abMatchAll) → Form[]`

**Flags:** Global

Inventory Processing

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akContainer` | `ObjectReference` | ✓ |  |
| `akKeywords` | `Keyword[]` | ✓ |  |
| `abMatchAll` | `Bool` |  | `false` |

### `GetPlayerSpeechTarget() → Actor`

**Flags:** Global

Player

### `GetQuestMarker(akQuest) → ObjectReference`

**Flags:** Global

geography

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akQuest` | `Quest` | ✓ |  |

### `GetTemperFactor(akContainer, akItem) → Float`

**Flags:** Global

unlike ObjecReference.GetItemHealthPercent, this will work on items in a container (range: 0.0-1.6)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akContainer` | `ObjectReference` | ✓ |  |
| `akItem` | `Form` | ✓ |  |

### `GetTravelDistance(akRef1, akRef2) → Float`

**Flags:** Global

unlike GetDistance this works even when one or both refs are in an interior or another cell

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef1` | `ObjectReference` | ✓ |  |
| `akRef2` | `ObjectReference` | ✓ |  |

### `GetVersion() → Float`

**Flags:** Global

Script Version Number
This will no longer change and is only meant for backwards compatibility with mods made while this script was a standalone mod

### `RegisterForAllAlphaNumericKeys(akForm)`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `RemoveForms(akFromCont, akForms, akToCont) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFromCont` | `ObjectReference` | ✓ |  |
| `akForms` | `Form[]` | ✓ |  |
| `akToCont` | `ObjectReference` |  |  |

### `ReplaceAt(asStr, aiIndex, asReplace) → String`

**Flags:** Global

String Processing

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asStr` | `String` | ✓ |  |
| `aiIndex` | `Int` | ✓ |  |
| `asReplace` | `String` | ✓ |  |

### `SetActorCalmed(akActor, abCalmed)`

**Flags:** Global

Actor

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `abCalmed` | `Bool` | ✓ |  |

### `SetActorFrozen(akTarget, abFrozen)`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akTarget` | `Actor` | ✓ |  |
| `abFrozen` | `Bool` | ✓ |  |

### `WornHasKeywords(akActor, akKwds) → Keyword[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `akKwds` | `Keyword[]` | ✓ |  |

### `WornHasKeywordStrings(akActor, akKwds) → Keyword[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `akKwds` | `String[]` | ✓ |  |


---

## `SPE_Actor`

**Source:** `scrab` (Scrab's Papyrus Extender) • **Flags:** Hidden

---

## Global Functions

### `Dismount(akActor)`

**Flags:** Native Global

Unmount this actor

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetDetectedActors(akActor) → Actor[]`

**Flags:** Native Global

Get all actors this actor is currently detecting

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetDetectedBy(akActor) → Actor[]`

**Flags:** Native Global

Get all actors that are currently detecting this actor

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetPlayerSpeechTarget() → Actor`

**Flags:** Native Global

Get the players current dialogue target

### `GetRaceType(akActor) → String`

**Flags:** Native Global

Maps this actor to their corresponding havok behavior class, possible values are:
Human
Wolf
Dog
Chicken
Hare
FlameAtronach
FrostAtronach
StormAtronach
Bear
Chaurus
Cow
Deer
ChaurusHunter
Gargoyle
Lurker
Boar
DwarvenBallista
Seeker
Netch
Riekling
AshHopper
Dragon
DragonPriest
Draugr
DwarvenSphere
DwarvenSpider
DwarvenCenturion
Falmer
Spider
Giant
Goat
Hagraven
Horker
Horse
IceWraith
Mammoth
Mudcrab
Sabrecat
Skeever
Slaughterfish
Spriggan
Troll
VampireLord
Werewolf
Wispmother
Wisp

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetWornArmor(akActor, aiSlotMask) → Armor[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `aiSlotMask` | `Int` |  | `0` |

### `GetWornForms(akActor) → Form[]`

**Flags:** Native Global

Get all currently worn forms, optionally filtered by slot mask
aiSlotMask: Sum of slot masks to check. An armor is returned if it occupies any of the specified slots
See Armor.psc or https://ck.uesp.net/wiki/Slot_Masks_-_Armor for a list of masks

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `IsActorCalmed(akActor) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `IsActorFrozen(akActor) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `SetActorCalmed(akActpr, abDoCalm)`

**Flags:** Native Global

Calm this actor. Calmed actors will ignore ongoing combat and not be attacked by anyone.
(This behaves similar to Acheron's paficiation, beware however that both systems are indepnendent from another)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActpr` | `Actor` | ✓ |  |
| `abDoCalm` | `Bool` | ✓ |  |

### `SetActorFrozen(akActor, abDoFreeze)`

**Flags:** Native Global

Disable an actors collision

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `abDoFreeze` | `Bool` | ✓ |  |

### `WornHasKeywords(akActor, akKeywords) → Keyword[]`

**Flags:** Native Global

Same as WornHasKeyword(), but allows to specify multiple keywords
The string variant allows to search for substrings in keywords if abMatchPartial is true
Returns all keywords that are present on any worn form, in case of abMatchPartial = true, returns the first match

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `akKeywords` | `Keyword[]` | ✓ |  |

### `WornHasKeywordStrings(akActor, asKeywords, abMatchPartial) → Keyword[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `asKeywords` | `String[]` | ✓ |  |
| `abMatchPartial` | `Bool` | ✓ |  |


---

## `SPE_Cell`

**Source:** `scrab` (Scrab's Papyrus Extender) • **Flags:** Hidden

---

## Global Functions

### `GetExteriorLocations(akCell) → Location[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akCell` | `Cell` | ✓ |  |

### `GetExteriorWorldSpaces(akCell) → worldspace[]`

**Flags:** Native Global

Returns all connected worldspaces/locations of the given cell

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akCell` | `Cell` | ✓ |  |


---

## `SPE_Events`

**Source:** `scrab` (Scrab's Papyrus Extender) • **Flags:** Hidden

---

## Events

### `OnAnimationEventEx(akReference, asEventName, asPayload)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akReference` | `Actor` |
| `asEventName` | `String` |
| `asPayload` | `String` |

---

## Global Functions

### `RegisterForAnimationEventEx(akReference, asEventName)`

**Flags:** Native Global

Similar to the Vanilla RegisterForAnimationEvent(). Supports payload data (Sound.NPCHumanCartRidePlayerEnter, etc).

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akReference` | `Actor` | ✓ |  |
| `asEventName` | `String` | ✓ |  |

### `RegisterForAnimationEventEx_Alias(akAlias, asEventName)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `ReferenceAlias` | ✓ |  |
| `asEventName` | `String` | ✓ |  |

### `RegisterForAnimationEventEx_MgEff(akMagicEffect, asEventName)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akMagicEffect` | `ActiveMagicEffect` | ✓ |  |
| `asEventName` | `String` | ✓ |  |

### `UnregisterForAnimationEventEx(akReference, asEventName)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akReference` | `Actor` | ✓ |  |
| `asEventName` | `String` | ✓ |  |

### `UnregisterForAnimationEventEx_Alias(akAlias, asEventName)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAlias` | `ReferenceAlias` | ✓ |  |
| `asEventName` | `String` | ✓ |  |

### `UnregisterForAnimationEventEx_MgEff(akMagicEffect, asEventName)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akMagicEffect` | `ActiveMagicEffect` | ✓ |  |
| `asEventName` | `String` | ✓ |  |


---

## `SPE_Form`

**Source:** `scrab` (Scrab's Papyrus Extender) • **Flags:** Hidden

---

## Global Functions

### `FlattenLeveledList(akList) → Form[]`

**Flags:** Native Global

Given a leveled list, return a single array containing all possible drops from this list
The function recursively finds all items, including those contained in other leveled lists of any depth
That is, it is guaranteed that the returned Form[] array will no longer contain any LeveledItem objects

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akList` | `LeveledItem` | ✓ |  |

### `FormHasKeywords(akForm, akKeywords, abContainAll) → Bool`

**Flags:** Native Global

Check if the given form has any (or all) of the given keywords
If abPartialMatch, will return true if any of the strings in asKeywords is a substring of the form's keywords (e.g. ["ActorType"] would match "ActorTypeNPC")

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `akKeywords` | `Keyword[]` | ✓ |  |
| `abContainAll` | `Bool` | ✓ |  |

### `FormHasKeywordStrings(akForm, asKeywords, abContainAll, abPartialMatch) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `asKeywords` | `String[]` | ✓ |  |
| `abContainAll` | `Bool` | ✓ |  |
| `abPartialMatch` | `Bool` | ✓ |  |

### `GetContainer(akForm) → objectreference[]`

**Flags:** Native Global

Get all references that are currently in possession of the given form

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |


---

## `SPE_GlobalVariable`

**Source:** `scrab` (Scrab's Papyrus Extender) • **Flags:** Hidden

---

## Global Functions

### `GetGlobal(asEditorID) → globalvariable`

**Flags:** Native Global

Get a global value by its editor ID

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asEditorID` | `String` | ✓ |  |


---

## `SPE_Interface`

**Source:** `scrab` (Scrab's Papyrus Extender) • **Flags:** Hidden

---

## Global Functions

### `CloseCustomMenu()`

**Flags:** Native Global

### `GetButtonForDXScanCode(aiKeyCode) → String`

**Flags:** Native Global

Return the string representation of the given key code

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiKeyCode` | `Int` | ✓ |  |

### `GetMenuName() → String`

**Flags:** Native Global

Return the name of the custom menu

### `OpenCustomMenu(asFilePath) → Bool`

**Flags:** Native Global

Open/close a custom menu under the given filepath
The difference between this menu and the one provided by SKSE is that this one will *not* pause the game while open

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asFilePath` | `String` | ✓ |  |

### `PrintConsole(asText)`

**Flags:** Native Global

Print the given String into the consosle

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asText` | `String` | ✓ |  |


---

## `SPE_Location`

**Source:** `scrab` (Scrab's Papyrus Extender) • **Flags:** Hidden

---

## Global Functions

### `GetParentLocation(akLocation) → location`

**Flags:** Native Global

Returns the parent location of the given location

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akLocation` | `Location` | ✓ |  |


---

## `SPE_ObjectRef`

**Source:** `scrab` (Scrab's Papyrus Extender) • **Flags:** Hidden

---

## Global Functions

### `GetAbsPosX(akReference) → Float`

**Flags:** Native Global

Return absolute positions of akReference. That is, the position of the reference with respect to the adjacent exterior worldspace (if any)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akReference` | `ObjectReference` | ✓ |  |

### `GetAbsPosY(akReference) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akReference` | `ObjectReference` | ✓ |  |

### `GetAbsPosZ(akReference) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akReference` | `ObjectReference` | ✓ |  |

### `GetEnchantedItems(akReference, abWeapons, abArmor, abWornOnly) → form[]`

**Flags:** Native Global

Return all enchanted items on the given object

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akReference` | `ObjectReference` | ✓ |  |
| `abWeapons` | `Bool` | ✓ |  |
| `abArmor` | `Bool` | ✓ |  |
| `abWornOnly` | `Bool` | ✓ |  |

### `GetInventoryNamedObjects(akReference, asNames) → Form[]`

**Flags:** Native Global

Return all items on the given object whichs item name matches one of the given names

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akReference` | `ObjectReference` | ✓ |  |
| `asNames` | `String[]` | ✓ |  |

### `GetItemsByKeyword(akReference, akKeywords, abMatchAll) → Form[]`

**Flags:** Native Global

Return all items on the given object which includes any (or all) of the given keywords

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akReference` | `ObjectReference` | ✓ |  |
| `akKeywords` | `Keyword[]` | ✓ |  |
| `abMatchAll` | `Bool` | ✓ |  |

### `GetTemperFactor(akReference, akForm) → Float`

**Flags:** Native Global

Return IteamHealthPercent of akForm. Alternative to ObjecReference.GetItemHealthPercent for items stored in a container (range: 0.0 - 1.6)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akReference` | `ObjectReference` | ✓ |  |
| `akForm` | `Form` | ✓ |  |

### `GetTravelDistance(akReference, akTarget) → Float`

**Flags:** Native Global

GetDistance between akReference and akTarget using absolute positions, s.t. interior cells are considered accordingly

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akReference` | `ObjectReference` | ✓ |  |
| `akTarget` | `ObjectReference` | ✓ |  |

### `RemoveItems(akReference, akForms, akTarget) → Int`

**Flags:** Native Global

Remove all items in akForms from akReference, optionally moving them to akTarget. Returns the number of items removed

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akReference` | `ObjectReference` | ✓ |  |
| `akForms` | `Form[]` | ✓ |  |
| `akTarget` | `ObjectReference` | ✓ |  |


---

## `SPE_Quest`

**Source:** `scrab` (Scrab's Papyrus Extender) • **Flags:** Hidden

---

## Global Functions

### `GetQuestMarker(akQuest) → objectreference`

**Flags:** Native Global

Get the currently active quest marker for the given quest

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akQuest` | `Quest` | ✓ |  |


---

## `SPE_Utility`

**Source:** `scrab` (Scrab's Papyrus Extender) • **Flags:** Hidden

---

## Global Functions

### `FilterArray_Float(arr, filter) → Float[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arr` | `Float[]` | ✓ |  |
| `filter` | `Float[]` | ✓ |  |

### `FilterArray_Form(arr, filter) → Form[]`

**Flags:** Native Global

Remove any objects from arr that are present in filter, return the filtered array

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arr` | `Form[]` | ✓ |  |
| `filter` | `Form[]` | ✓ |  |

### `FilterArray_Int(arr, filter) → Int[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arr` | `Int[]` | ✓ |  |
| `filter` | `Int[]` | ✓ |  |

### `FilterArray_String(arr, filter) → String[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arr` | `String[]` | ✓ |  |
| `filter` | `String[]` | ✓ |  |

### `FilterBySlot(akForm, aiSlots, abMatchAll) → Armor[]`

**Flags:** Native Global

Remove any which do not use any of the given slots. If abMatchAll is true, only forms that use all slots are returned

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form[]` | ✓ |  |
| `aiSlots` | `Int[]` | ✓ |  |
| `abMatchAll` | `Bool` | ✓ |  |

### `FilterBySlotmask(akForm, aiSlotMask, abMatchAll) → Armor[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form[]` | ✓ |  |
| `aiSlotMask` | `Int` | ✓ |  |
| `abMatchAll` | `Bool` | ✓ |  |

### `FilterFormsByGoldValue(akForm, aiGoldThreshold, abGreater, abEqual) → Form[]`

**Flags:** Native Global

Remove any form whichs gold value is less than the given threshold (xor greater if abGreater, or equal if abEqual)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form[]` | ✓ |  |
| `aiGoldThreshold` | `Int` | ✓ |  |
| `abGreater` | `Bool` | ✓ |  |
| `abEqual` | `Bool` | ✓ |  |

### `FilterFormsByKeyword(akForm, akKeywords, abMatchAll, abInvert) → form[]`

**Flags:** Native Global

Remove any form that does not contain any of the given keywords
if abMatchAll is true, only forms that contain all keywords are returned
if abInvert is true, only forms that do not contain any of the keywords are returned

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form[]` | ✓ |  |
| `akKeywords` | `Keyword[]` | ✓ |  |
| `abMatchAll` | `Bool` | ✓ |  |
| `abInvert` | `Bool` | ✓ |  |

### `FindIf_Float(arr, lua) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arr` | `Float[]` | ✓ |  |
| `lua` | `String` | ✓ |  |

### `FindIf_Int(arr, lua) → Int`

**Flags:** Native Global

Find the first element satisfying the function "predicate(a)"
Returns the index of the element, -1 if no such element exists, -2 if an error occursed
Example: FindIf_Int(arr, "function predicate(a) return a > 5 end") returns the index of the first element > 5
"lua" can also be a filepath to a .lua file containing the function, relative to Skyrim.exe

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arr` | `Int[]` | ✓ |  |
| `lua` | `String` | ✓ |  |

### `FindIf_String(arr, lua) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arr` | `String[]` | ✓ |  |
| `lua` | `String` | ✓ |  |

### `IntersectArray_Float(arr, arr2) → Float[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arr` | `Float[]` | ✓ |  |
| `arr2` | `Float[]` | ✓ |  |

### `IntersectArray_Form(arr, arr2) → Form[]`

**Flags:** Native Global

Return the intersection of arr and arr2

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arr` | `Form[]` | ✓ |  |
| `arr2` | `Form[]` | ✓ |  |

### `IntersectArray_Int(arr, arr2) → Int[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arr` | `Int[]` | ✓ |  |
| `arr2` | `Int[]` | ✓ |  |

### `IntersectArray_String(arr, arr2) → String[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arr` | `String[]` | ✓ |  |
| `arr2` | `String[]` | ✓ |  |

### `PushFront_Float(arr, push) → Float[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arr` | `Float[]` | ✓ |  |
| `push` | `Float` | ✓ |  |

### `PushFront_Int(arr, push) → Int[]`

**Flags:** Native Global

Create a new array [push, arr[0], arr[1], ..., arr[N-1]]

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arr` | `Int[]` | ✓ |  |
| `push` | `Int` | ✓ |  |

### `PushFront_String(arr, push) → String[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arr` | `String[]` | ✓ |  |
| `push` | `String` | ✓ |  |

### `RemoveIf_Float(arr, lua) → Float[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arr` | `Float[]` | ✓ |  |
| `lua` | `String` | ✓ |  |

### `RemoveIf_Int(arr, lua) → Int[]`

**Flags:** Native Global

Remove all occurences satisfying the predicate function, return the filtered array
Example: RemoveIf_Int(arr, "function predicate(a) return a > 5 end") removes all elements that are > 5
"lua" can also be a filepath to a .lua file containing the function, relative to Skyrim.exe

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arr` | `Int[]` | ✓ |  |
| `lua` | `String` | ✓ |  |

### `RemoveIf_String(arr, lua) → String[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arr` | `String[]` | ✓ |  |
| `lua` | `String` | ✓ |  |

### `ReplaceAt(asStr, aiIndex, asReplace) → String`

**Flags:** Native Global

Replace the character at index aiIndex in asStr with asReplace

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asStr` | `String` | ✓ |  |
| `aiIndex` | `Int` | ✓ |  |
| `asReplace` | `String` | ✓ |  |

### `Shuffle_Float(arr)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arr` | `Float[]` | ✓ |  |

### `Shuffle_Int(arr)`

**Flags:** Native Global

Randomly reorder every element in the array, the array is modified in-place

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arr` | `Int[]` | ✓ |  |

### `Shuffle_String(arr)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arr` | `String[]` | ✓ |  |

### `Sort_Float(arr, lua) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arr` | `Float[]` | ✓ |  |
| `lua` | `String` | ✓ |  |

### `Sort_Int(arr, lua) → Bool`

**Flags:** Native Global

Sort arr using a comparison function: "compare(a, b)" in place, the array is modified in-place, returns true on success
Example: Sort_Int(arr, "function compare(a, b) return a <= b end") would sort arr in descending order
"lua" can also be a filepath to a .lua file containing the function, relative to Skyrim.exe

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arr` | `Int[]` | ✓ |  |
| `lua` | `String` | ✓ |  |

### `Sort_String(arr, lua) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arr` | `String[]` | ✓ |  |
| `lua` | `String` | ✓ |  |
