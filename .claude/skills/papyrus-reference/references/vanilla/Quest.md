# `Quest`

**Source:** `vanilla` • **Extends:** `Form` • **Flags:** Hidden

---

## Events

### `OnStoryActivateActor(akLocation, akActor)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akLocation` | `Location` |
| `akActor` | `ObjectReference` |

### `OnStoryAddToPlayer(akOwner, akContainer, akLocation, akItemBase, aiAcquireType)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akOwner` | `ObjectReference` |
| `akContainer` | `ObjectReference` |
| `akLocation` | `Location` |
| `akItemBase` | `Form` |
| `aiAcquireType` | `Int` |

### `OnStoryArrest(akArrestingGuard, akCriminal, akLocation, aiCrime)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akArrestingGuard` | `ObjectReference` |
| `akCriminal` | `ObjectReference` |
| `akLocation` | `Location` |
| `aiCrime` | `Int` |

### `OnStoryAssaultActor(akVictim, akAttacker, akLocation, aiCrime)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akVictim` | `ObjectReference` |
| `akAttacker` | `ObjectReference` |
| `akLocation` | `Location` |
| `aiCrime` | `Int` |

### `OnStoryBribeNPC(akActor)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akActor` | `ObjectReference` |

### `OnStoryCastMagic(akCastingActor, akSpellTarget, akLocation, akSpell)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akCastingActor` | `ObjectReference` |
| `akSpellTarget` | `ObjectReference` |
| `akLocation` | `Location` |
| `akSpell` | `Form` |

### `OnStoryChangeLocation(akActor, akOldLocation, akNewLocation)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akActor` | `ObjectReference` |
| `akOldLocation` | `Location` |
| `akNewLocation` | `Location` |

### `OnStoryCraftItem(akBench, akLocation, akCreatedItem)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akBench` | `ObjectReference` |
| `akLocation` | `Location` |
| `akCreatedItem` | `Form` |

### `OnStoryCrimeGold(akVictim, akCriminal, akFaction, aiGoldAmount, aiCrime)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akVictim` | `ObjectReference` |
| `akCriminal` | `ObjectReference` |
| `akFaction` | `Form` |
| `aiGoldAmount` | `Int` |
| `aiCrime` | `Int` |

### `OnStoryCure(akInfection)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akInfection` | `Form` |

### `OnStoryDialogue(akLocation, akActor1, akActor2)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akLocation` | `Location` |
| `akActor1` | `ObjectReference` |
| `akActor2` | `ObjectReference` |

### `OnStoryDiscoverDeadBody(akActor, akDeadActor, akLocation)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akActor` | `ObjectReference` |
| `akDeadActor` | `ObjectReference` |
| `akLocation` | `Location` |

### `OnStoryEscapeJail(akLocation, akCrimeGroup)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akLocation` | `Location` |
| `akCrimeGroup` | `Form` |

### `OnStoryFlatterNPC(akActor)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akActor` | `ObjectReference` |

### `OnStoryHello(akLocation, akActor1, akActor2)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akLocation` | `Location` |
| `akActor1` | `ObjectReference` |
| `akActor2` | `ObjectReference` |

### `OnStoryIncreaseLevel(aiNewLevel)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `aiNewLevel` | `Int` |

### `OnStoryIncreaseSkill(asSkill)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `asSkill` | `String` |

### `OnStoryInfection(akTransmittingActor, akInfection)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akTransmittingActor` | `ObjectReference` |
| `akInfection` | `Form` |

### `OnStoryIntimidateNPC(akActor)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akActor` | `ObjectReference` |

### `OnStoryJail(akGuard, akCrimeGroup, akLocation, aiCrimeGold)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akGuard` | `ObjectReference` |
| `akCrimeGroup` | `Form` |
| `akLocation` | `Location` |
| `aiCrimeGold` | `Int` |

### `OnStoryKillActor(akVictim, akKiller, akLocation, aiCrimeStatus, aiRelationshipRank)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akVictim` | `ObjectReference` |
| `akKiller` | `ObjectReference` |
| `akLocation` | `Location` |
| `aiCrimeStatus` | `Int` |
| `aiRelationshipRank` | `Int` |

### `OnStoryNewVoicePower(akActor, akVoicePower)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akActor` | `ObjectReference` |
| `akVoicePower` | `Form` |

### `OnStoryPayFine(akCriminal, akGuard, akCrimeGroup, aiCrimeGold)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akCriminal` | `ObjectReference` |
| `akGuard` | `ObjectReference` |
| `akCrimeGroup` | `Form` |
| `aiCrimeGold` | `Int` |

### `OnStoryPickLock(akActor, akLock)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akActor` | `ObjectReference` |
| `akLock` | `ObjectReference` |

### `OnStoryPlayerGetsFavor(akActor)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akActor` | `ObjectReference` |

### `OnStoryRelationshipChange(akActor1, akActor2, aiOldRelationship, aiNewRelationship)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akActor1` | `ObjectReference` |
| `akActor2` | `ObjectReference` |
| `aiOldRelationship` | `Int` |
| `aiNewRelationship` | `Int` |

### `OnStoryRemoveFromPlayer(akOwner, akItem, akLocation, akItemBase, aiRemoveType)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akOwner` | `ObjectReference` |
| `akItem` | `ObjectReference` |
| `akLocation` | `Location` |
| `akItemBase` | `Form` |
| `aiRemoveType` | `Int` |

### `OnStoryScript(akKeyword, akLocation, akRef1, akRef2, aiValue1, aiValue2)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akKeyword` | `Keyword` |
| `akLocation` | `Location` |
| `akRef1` | `ObjectReference` |
| `akRef2` | `ObjectReference` |
| `aiValue1` | `Int` |
| `aiValue2` | `Int` |

### `OnStoryServedTime(akLocation, akCrimeGroup, aiCrimeGold, aiDaysJail)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akLocation` | `Location` |
| `akCrimeGroup` | `Form` |
| `aiCrimeGold` | `Int` |
| `aiDaysJail` | `Int` |

### `OnStoryTrespass(akVictim, akTrespasser, akLocation, aiCrime)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `akVictim` | `ObjectReference` |
| `akTrespasser` | `ObjectReference` |
| `akLocation` | `Location` |
| `aiCrime` | `Int` |

---

## Functions

### `CompleteAllObjectives()`

**Flags:** Native

Flags all objectives as complete

### `CompleteQuest()`

**Flags:** Native

Flags this quest as completed

### `FailAllObjectives()`

**Flags:** Native

Flags all objectives as failed

### `GetAlias(aiAliasID) → Alias`

**Flags:** Native

Obtains the specified alias on the quest

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiAliasID` | `Int` | ✓ |  |

### `GetCurrentStageID() → Int`

**Flags:** Native

Obtains the id of the highest completed stage on this quest

### `GetStage() → Int`

Alias for GetCurrentStage - obtains the highest completed stage on this quest

### `GetStageDone(aiStage) → Bool`

Alias for IsStageDone - checks to see whether the given stage is done or not

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiStage` | `Int` | ✓ |  |

### `IsActive() → Bool`

**Flags:** Native

Is this quest "active" (tracked by the player)?

### `IsCompleted() → Bool`

**Flags:** Native

Checks to see if the quest is completed

### `IsObjectiveCompleted(aiObjective) → Bool`

**Flags:** Native

Checks to see if the specified objective is completed

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiObjective` | `Int` | ✓ |  |

### `IsObjectiveDisplayed(aiObjective) → Bool`

**Flags:** Native

Checks to see if the specified objective is displayed

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiObjective` | `Int` | ✓ |  |

### `IsObjectiveFailed(aiObjective) → Bool`

**Flags:** Native

Checks to see if the specified objective is failed

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiObjective` | `Int` | ✓ |  |

### `IsRunning() → Bool`

**Flags:** Native

Checks to see if the quest is running

### `IsStageDone(aiStage) → Bool`

**Flags:** Native

Obtains whether the specified stage is done or not

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiStage` | `Int` | ✓ |  |

### `IsStarting() → Bool`

**Flags:** Native

Checks to see if the quest is enabled but not running yet

### `IsStopped() → Bool`

**Flags:** Native

Checks to see if the quest is no longer enabled or running

### `IsStopping() → Bool`

**Flags:** Native

Checks to see if the quest is not enabled anymore but still shutting down

### `ModObjectiveGlobal(afModValue, aModGlobal, aiObjectiveID, afTargetValue, abCountingUp, abCompleteObjective, abRedisplayObjective) → Bool`

thread-safe way to modify a global value
optional parameters:
aiObjectiveID = objective ID to redisplay
afTargetValue = value you're counting up (or down) towards -- if included, function will return TRUE when the global reaches the target value
abCountingUp = by default, function assumes you're counting up towards the target value; make this false to count DOWN towards target value
abCompleteObjective = by default, function assumes you're completing the objective once you reach the target value; make this false to FAIL the objective
abRedisplayObjective = by default, function asssume you want to redisplay the objective every time the global is incremeneted; make this FALSE to only display the objectives on complete or failure

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afModValue` | `Float` | ✓ |  |
| `aModGlobal` | `GlobalVariable` | ✓ |  |
| `aiObjectiveID` | `Int` |  | `-1` |
| `afTargetValue` | `Float` |  | `-1` |
| `abCountingUp` | `Bool` |  | `true` |
| `abCompleteObjective` | `Bool` |  | `true` |
| `abRedisplayObjective` | `Bool` |  | `true` |

### `Reset()`

**Flags:** Native

Resets the quest

### `SetActive(abActive)`

**Flags:** Native

Flags this quest as "active" (tracked by the player)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abActive` | `Bool` |  | `true` |

### `SetCurrentStageID(aiStageID) → Bool`

**Flags:** Native

Set the quest to the requested stage ID - returns true if stage exists and was set.
This function is latent and will wait for the quest to start up before returning (if it needed to be started)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiStageID` | `Int` | ✓ |  |

### `SetObjectiveCompleted(aiObjective, abCompleted)`

**Flags:** Native

Sets the specified objective to completed or not

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiObjective` | `Int` | ✓ |  |
| `abCompleted` | `Bool` |  | `true` |

### `SetObjectiveDisplayed(aiObjective, abDisplayed, abForce)`

**Flags:** Native

Sets the specified objective to displayed or hidden - if abForce is true, will display the objective even if it has already been displayed

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiObjective` | `Int` | ✓ |  |
| `abDisplayed` | `Bool` |  | `true` |
| `abForce` | `Bool` |  | `false` |

### `SetObjectiveFailed(aiObjective, abFailed)`

**Flags:** Native

Sets the specified objective to failed or not

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiObjective` | `Int` | ✓ |  |
| `abFailed` | `Bool` |  | `true` |

### `SetStage(aiStage) → Bool`

Alias of SetCurrentStage - Set the quest to the requested stage
This function is latent and will wait for the quest to start up before returning (if it needed to be started)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiStage` | `Int` | ✓ |  |

### `Start() → Bool`

**Flags:** Native

Starts the quest - returns whether the quest was able to be started or not
This function is latent and will wait for the quest to start up before returning

### `Stop()`

**Flags:** Native

Stops the quest

### `UpdateCurrentInstanceGlobal(aUpdateGlobal) → Bool`

**Flags:** Native

Updates current instance's value for the given global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aUpdateGlobal` | `GlobalVariable` | ✓ |  |
