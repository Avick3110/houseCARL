# `Game`

**Source:** `vanilla` • **Flags:** Hidden

---

## Global Functions

### `AddAchievement(aiAchievementID)`

**Flags:** Native Global

Adds the specified achievement to the player's profile

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiAchievementID` | `Int` | ✓ |  |

### `AddHavokBallAndSocketConstraint(arRefA, arRefANode, arRefB, arRefBNode, afRefALocalOffsetX, afRefALocalOffsetY, afRefALocalOffsetZ, afRefBLocalOffsetX, afRefBLocalOffsetY, afRefBLocalOffsetZ) → Bool`

**Flags:** Native Global

Adds a ball-and-socket constraint between two rigid bodies, identified by their ref and node names

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arRefA` | `ObjectReference` | ✓ |  |
| `arRefANode` | `String` | ✓ |  |
| `arRefB` | `ObjectReference` | ✓ |  |
| `arRefBNode` | `String` | ✓ |  |
| `afRefALocalOffsetX` | `Float` |  | `0` |
| `afRefALocalOffsetY` | `Float` |  | `0` |
| `afRefALocalOffsetZ` | `Float` |  | `0` |
| `afRefBLocalOffsetX` | `Float` |  | `0` |
| `afRefBLocalOffsetY` | `Float` |  | `0` |
| `afRefBLocalOffsetZ` | `Float` |  | `0` |

### `AddPerkPoints(aiPerkPoints)`

**Flags:** Native Global

Add the specified number of perk points to the player

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiPerkPoints` | `Int` | ✓ |  |

### `AdvanceSkill(asSkillName, afMagnitude)`

**Flags:** Native Global

Advance the given skill on the player by the provided amount of skill usage

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asSkillName` | `String` | ✓ |  |
| `afMagnitude` | `Float` | ✓ |  |

### `CalculateFavorCost(aiFavorPrice) → Int`

**Flags:** Native Global

Calculates how much a x point favor would cost the player

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiFavorPrice` | `Int` | ✓ |  |

### `ClearPrison()`

**Flags:** Native Global

Clears the prison variables on the player

### `ClearTempEffects()`

**Flags:** Native Global

Clears temp effects from game

### `DisablePlayerControls(abMovement, abFighting, abCamSwitch, abLooking, abSneaking, abMenu, abActivate, abJournalTabs, aiDisablePOVType)`

**Flags:** Native Global

Disables the user's controls

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abMovement` | `Bool` |  | `true` |
| `abFighting` | `Bool` |  | `true` |
| `abCamSwitch` | `Bool` |  | `false` |
| `abLooking` | `Bool` |  | `false` |
| `abSneaking` | `Bool` |  | `false` |
| `abMenu` | `Bool` |  | `true` |
| `abActivate` | `Bool` |  | `true` |
| `abJournalTabs` | `Bool` |  | `false` |
| `aiDisablePOVType` | `Int` |  | `0` |

### `EnableFastTravel(abEnable)`

**Flags:** Native Global

Enables or disables the ability to fast travel

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abEnable` | `Bool` |  | `true` |

### `EnablePlayerControls(abMovement, abFighting, abCamSwitch, abLooking, abSneaking, abMenu, abActivate, abJournalTabs, aiDisablePOVType)`

**Flags:** Native Global

Enables the user's controls

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abMovement` | `Bool` |  | `true` |
| `abFighting` | `Bool` |  | `true` |
| `abCamSwitch` | `Bool` |  | `true` |
| `abLooking` | `Bool` |  | `true` |
| `abSneaking` | `Bool` |  | `true` |
| `abMenu` | `Bool` |  | `true` |
| `abActivate` | `Bool` |  | `true` |
| `abJournalTabs` | `Bool` |  | `true` |
| `aiDisablePOVType` | `Int` |  | `0` |

### `FadeOutGame(abFadingOut, abBlackFade, afSecsBeforeFade, afFadeDuration)`

**Flags:** Native Global

Fades out the game to black, or vice versa

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abFadingOut` | `Bool` | ✓ |  |
| `abBlackFade` | `Bool` | ✓ |  |
| `afSecsBeforeFade` | `Float` | ✓ |  |
| `afFadeDuration` | `Float` | ✓ |  |

### `FastTravel(akDestination)`

**Flags:** Native Global

Fast-travels the player to the specified object's location

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akDestination` | `ObjectReference` | ✓ |  |

### `FindClosestActor(afX, afY, afZ, afRadius) → Actor`

**Flags:** Native Global

Finds the closest actor within a given radius of a location

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afX` | `Float` | ✓ |  |
| `afY` | `Float` | ✓ |  |
| `afZ` | `Float` | ✓ |  |
| `afRadius` | `Float` | ✓ |  |

### `FindClosestActorFromRef(arCenter, afRadius) → Actor`

**Flags:** Global

Finds the closest actor within a given radius of a reference

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arCenter` | `ObjectReference` | ✓ |  |
| `afRadius` | `Float` | ✓ |  |

### `FindClosestReferenceOfAnyTypeInList(arBaseObjects, afX, afY, afZ, afRadius) → ObjectReference`

**Flags:** Native Global

Finds the closest reference of any base object in the list within a given radius of a location

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arBaseObjects` | `FormList` | ✓ |  |
| `afX` | `Float` | ✓ |  |
| `afY` | `Float` | ✓ |  |
| `afZ` | `Float` | ✓ |  |
| `afRadius` | `Float` | ✓ |  |

### `FindClosestReferenceOfAnyTypeInListFromRef(arBaseObjects, arCenter, afRadius) → ObjectReference`

**Flags:** Global

Finds the closest reference of a given base object within a given radius of a reference

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arBaseObjects` | `FormList` | ✓ |  |
| `arCenter` | `ObjectReference` | ✓ |  |
| `afRadius` | `Float` | ✓ |  |

### `FindClosestReferenceOfType(arBaseObject, afX, afY, afZ, afRadius) → ObjectReference`

**Flags:** Native Global

Finds the closest reference of a given base object within a given radius of a location

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arBaseObject` | `Form` | ✓ |  |
| `afX` | `Float` | ✓ |  |
| `afY` | `Float` | ✓ |  |
| `afZ` | `Float` | ✓ |  |
| `afRadius` | `Float` | ✓ |  |

### `FindClosestReferenceOfTypeFromRef(arBaseObject, arCenter, afRadius) → ObjectReference`

**Flags:** Global

Finds the closest reference of a given base object within a given radius of a reference

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arBaseObject` | `Form` | ✓ |  |
| `arCenter` | `ObjectReference` | ✓ |  |
| `afRadius` | `Float` | ✓ |  |

### `FindRandomActor(afX, afY, afZ, afRadius) → Actor`

**Flags:** Native Global

Finds a random actor within a given radius of a location

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afX` | `Float` | ✓ |  |
| `afY` | `Float` | ✓ |  |
| `afZ` | `Float` | ✓ |  |
| `afRadius` | `Float` | ✓ |  |

### `FindRandomActorFromRef(arCenter, afRadius) → Actor`

**Flags:** Global

Finds a random actor within a given radius of a reference

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arCenter` | `ObjectReference` | ✓ |  |
| `afRadius` | `Float` | ✓ |  |

### `FindRandomReferenceOfAnyTypeInList(arBaseObjects, afX, afY, afZ, afRadius) → ObjectReference`

**Flags:** Native Global

Finds a random reference of a any base object in the list within a given radius of a location

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arBaseObjects` | `FormList` | ✓ |  |
| `afX` | `Float` | ✓ |  |
| `afY` | `Float` | ✓ |  |
| `afZ` | `Float` | ✓ |  |
| `afRadius` | `Float` | ✓ |  |

### `FindRandomReferenceOfAnyTypeInListFromRef(arBaseObjects, arCenter, afRadius) → ObjectReference`

**Flags:** Global

Finds a random reference of a given base object within a given radius of a reference

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arBaseObjects` | `FormList` | ✓ |  |
| `arCenter` | `ObjectReference` | ✓ |  |
| `afRadius` | `Float` | ✓ |  |

### `FindRandomReferenceOfType(arBaseObject, afX, afY, afZ, afRadius) → ObjectReference`

**Flags:** Native Global

Finds a random reference of a given base object within a given radius of a location

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arBaseObject` | `Form` | ✓ |  |
| `afX` | `Float` | ✓ |  |
| `afY` | `Float` | ✓ |  |
| `afZ` | `Float` | ✓ |  |
| `afRadius` | `Float` | ✓ |  |

### `FindRandomReferenceOfTypeFromRef(arBaseObject, arCenter, afRadius) → ObjectReference`

**Flags:** Global

Finds a random reference of a given base object within a given radius of a reference

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arBaseObject` | `Form` | ✓ |  |
| `arCenter` | `ObjectReference` | ✓ |  |
| `afRadius` | `Float` | ✓ |  |

### `ForceFirstPerson()`

**Flags:** Native Global

Make the player got to 1st person camera mode

### `ForceThirdPerson()`

**Flags:** Native Global

Make the player got to 3rd person camera mode

### `GetForm(aiFormID) → Form`

**Flags:** Native Global

Returns the form specified by the ID

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiFormID` | `Int` | ✓ |  |

### `GetFormFromFile(aiFormID, asFilename) → Form`

**Flags:** Native Global

Returns the form specified by the ID originating in the given file

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiFormID` | `Int` | ✓ |  |
| `asFilename` | `String` | ✓ |  |

### `GetGameSettingFloat(asGameSetting) → Float`

**Flags:** Native Global

Obtains the value of a game setting - one for each type of game setting

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asGameSetting` | `String` | ✓ |  |

### `GetGameSettingInt(asGameSetting) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asGameSetting` | `String` | ✓ |  |

### `GetGameSettingString(asGameSetting) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asGameSetting` | `String` | ✓ |  |

### `GetPlayer() → Actor`

**Flags:** Native Global

Returns the player actor

### `GetPlayerGrabbedRef() → ObjectReference`

**Flags:** Native Global

Returns the reference the player is currently grabbing

### `GetPlayersLastRiddenHorse() → Actor`

**Flags:** Native Global

Returns the horse last ridden by the player

### `GetRealHoursPassed() → Float`

**Flags:** Native Global

Returns the number of days spent in play

### `GetSunPositionX() → Float`

**Flags:** Native Global

Returns the X position of the Sun.

### `GetSunPositionY() → Float`

**Flags:** Native Global

Returns the Y position of the Sun.

### `GetSunPositionZ() → Float`

**Flags:** Native Global

Returns the Z position of the Sun.

### `HideTitleSequenceMenu()`

**Flags:** Native Global

### `IncrementSkill(asSkillName)`

**Flags:** Native Global

Increment the given skill on the player by the one point

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asSkillName` | `String` | ✓ |  |

### `IncrementSkillBy(asSkillName, aiCount)`

**Flags:** Native Global

Increment the given skill on the player by the given number of points

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asSkillName` | `String` | ✓ |  |
| `aiCount` | `Int` | ✓ |  |

### `IncrementStat(asStatName, aiModAmount)`

**Flags:** Native Global

Modifies the specified MiscStat by the given amount.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asStatName` | `String` | ✓ |  |
| `aiModAmount` | `Int` |  | `1` |

### `IsActivateControlsEnabled() → Bool`

**Flags:** Native Global

Are the activation controls enabled?

### `IsCamSwitchControlsEnabled() → Bool`

**Flags:** Native Global

Are the camera switch controls enabled?

### `IsFastTravelControlsEnabled() → Bool`

**Flags:** Native Global

Is fast travel controls enabled? Returns false if EnableFastTravel(false) has been called

### `IsFastTravelEnabled() → Bool`

**Flags:** Native Global

Is fast travel enabled?

### `IsFightingControlsEnabled() → Bool`

**Flags:** Native Global

Are the fighting controls enabled?

### `IsJournalControlsEnabled() → Bool`

**Flags:** Native Global

Are the journal menu controls enabled?

### `IsLookingControlsEnabled() → Bool`

**Flags:** Native Global

Are the looking controls enabled?

### `IsMenuControlsEnabled() → Bool`

**Flags:** Native Global

Are the menu controls enabled?

### `IsMovementControlsEnabled() → Bool`

**Flags:** Native Global

Are the movement controls enabled?

### `IsPlayerSungazing() → Bool`

**Flags:** Native Global

Is the player looking at the sun?

### `IsSneakingControlsEnabled() → Bool`

**Flags:** Native Global

Are the sneaking controls enabled?

### `IsWordUnlocked(akWord) → Bool`

**Flags:** Native Global

Is the specified Word of Power Unlocked?

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akWord` | `WordOfPower` | ✓ |  |

### `PlayBink(asFileName, abInterruptible, abMuteAudio, abMuteMusic, abLetterbox)`

**Flags:** Native Global

Plays a bink video - does not return until bink has finished, use with care!

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asFileName` | `String` | ✓ |  |
| `abInterruptible` | `Bool` |  | `false` |
| `abMuteAudio` | `Bool` |  | `true` |
| `abMuteMusic` | `Bool` |  | `true` |
| `abLetterbox` | `Bool` |  | `true` |

### `PrecacheCharGen()`

**Flags:** Native Global

Precaches character gen data.

### `PrecacheCharGenClear()`

**Flags:** Native Global

Clears Precached character gen data.

### `QueryStat(asStat) → Int`

**Flags:** Native Global

Queries the given stat and returns its value

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asStat` | `String` | ✓ |  |

### `QuitToMainMenu()`

**Flags:** Native Global

Forces the game back to the main menu

### `RemoveHavokConstraints(arFirstRef, arFirstRefNodeName, arSecondRef, arSecondRefNodeName) → Bool`

**Flags:** Native Global

Removes any constraint between two rigid bodies

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arFirstRef` | `ObjectReference` | ✓ |  |
| `arFirstRefNodeName` | `String` | ✓ |  |
| `arSecondRef` | `ObjectReference` | ✓ |  |
| `arSecondRefNodeName` | `String` | ✓ |  |

### `RequestAutoSave()`

**Flags:** Native Global

Request that an auto-save be made

### `RequestModel(asModelName)`

**Flags:** Native Global

Requests the specified model

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asModelName` | `String` | ✓ |  |

### `RequestSave()`

**Flags:** Native Global

Request that a normal save be made

### `SendWereWolfTransformation()`

**Flags:** Native Global

Finds an actor in high who can detect the player to call werewolf crime on the player

### `ServeTime()`

**Flags:** Native Global

Has the player serve their prison time

### `SetAllowFlyingMountLandingRequests(abAllow)`

**Flags:** Native Global

Allow or disallow player requests to have a flying mount land.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abAllow` | `Bool` | ✓ |  |

### `SetBeastForm(abEntering)`

**Flags:** Native Global

Called as we enter/exit beast form

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abEntering` | `Bool` | ✓ |  |

### `SetCameraTarget(arTarget)`

**Flags:** Native Global

Sets the camera target actor

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arTarget` | `Actor` | ✓ |  |

### `SetHudCartMode(abSetCartMode)`

**Flags:** Native Global

Sets or clears "cart mode" for the HUD

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abSetCartMode` | `Bool` |  | `true` |

### `SetInChargen(abDisableSaving, abDisableWaiting, abShowControlsDisabledMessage)`

**Flags:** Native Global

Informs the game whether we are in CharGen or not

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abDisableSaving` | `Bool` | ✓ |  |
| `abDisableWaiting` | `Bool` | ✓ |  |
| `abShowControlsDisabledMessage` | `Bool` | ✓ |  |

### `SetPlayerAIDriven(abAIDriven)`

**Flags:** Native Global

Enables or disables the AI driven flag on Player

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abAIDriven` | `Bool` |  | `true` |

### `SetPlayerReportCrime(abReportCrime)`

**Flags:** Native Global

Enables or disables  crime reporting on Player

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abReportCrime` | `Bool` |  | `true` |

### `SetSittingRotation(afValue)`

**Flags:** Native Global

Set the players sitting camera rotation - in degrees, offset from the standard angle.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afValue` | `Float` | ✓ |  |

### `SetSunGazeImageSpaceModifier(apImod)`

**Flags:** Native Global

Sets the Image Space Modifier that is triggered when the player gazes at the sun.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `apImod` | `ImageSpaceModifier` |  |  |

### `ShakeCamera(akSource, afStrength, afDuration)`

**Flags:** Native Global

Shakes the object from the location of the passed-in object. If none, it will shake the camera from the player's location.
Strength is clamped from 0 to 1
Duration in seconds. By default (0.0) use the game setting.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSource` | `ObjectReference` |  |  |
| `afStrength` | `Float` |  | `0.5` |
| `afDuration` | `Float` |  | `0` |

### `ShakeController(afSmallMotorStrength, afBigMotorStreangth, afDuration)`

**Flags:** Native Global

Shakes the controller for the specified length of time (in seconds). The strength values are clamped from 0 to 1

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afSmallMotorStrength` | `Float` | ✓ |  |
| `afBigMotorStreangth` | `Float` | ✓ |  |
| `afDuration` | `Float` | ✓ |  |

### `ShowFirstPersonGeometry(abShow)`

**Flags:** Native Global

Show the players first person geometry.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abShow` | `Bool` |  | `true` |

### `ShowLimitedRaceMenu()`

**Flags:** Native Global

### `ShowRaceMenu()`

**Flags:** Native Global

Displays the race/sex menu

### `ShowTitleSequenceMenu()`

**Flags:** Native Global

Title Sequence menu functions

### `ShowTrainingMenu(aTrainer)`

**Flags:** Native Global

Displays the training menu based on passed in trainer actor

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aTrainer` | `Actor` | ✓ |  |

### `StartTitleSequence(asSequenceName)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asSequenceName` | `String` | ✓ |  |

### `TeachWord(akWord)`

**Flags:** Native Global

Teaches the specified word of power to the player

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akWord` | `WordOfPower` | ✓ |  |

### `TriggerScreenBlood(aiValue)`

**Flags:** Native Global

Trigger screen blood with the given count

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiValue` | `Int` | ✓ |  |

### `UnlockWord(akWord)`

**Flags:** Native Global

Unlocks the specified word of power so the player can use it

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akWord` | `WordOfPower` | ✓ |  |

### `UsingGamepad() → Bool`

**Flags:** Native Global

Returns true if we're using a gamepad
