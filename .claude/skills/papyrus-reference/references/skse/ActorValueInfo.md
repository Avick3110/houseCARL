# `ActorValueInfo`

**Source:** `skse` (Skyrim Script Extender x64) • **Extends:** `Form` • **Flags:** Hidden

---

## Global Functions

### `GetActorValueInfoByID(id) → ActorValueInfo`

**Flags:** Native Global

Returns the AVI by id (0-164)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `id` | `Int` | ✓ |  |

### `GetActorValueInfoByName(avName) → actorvalueinfo`

**Flags:** Native Global

Returns the AVI by name

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `avName` | `String` | ✓ |  |

### `GetAVIByID(id) → ActorValueInfo`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `id` | `Int` | ✓ |  |

### `GetAVIByName(avName) → ActorValueInfo`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `avName` | `String` | ✓ |  |

---

## Functions

### `AddSkillExperience(exp)`

**Flags:** Native

Adds experience to this skill (Same as console AdvanceSkill, triggers skill-up)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `exp` | `Float` | ✓ |  |

### `GetBaseValue(akActor) → Float`

**Flags:** Native

Same as Actor.GetBaseActorValue (convenience function)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetCurrentValue(akActor) → Float`

**Flags:** Native

Same as Actor.GetActorValue (convenience function)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetExperienceForLevel(currentLevel) → Float`

**Flags:** Native

Returns the experience required for skill-up
(ImproveMult * currentLevel ^ fSkillUseCurve + ImproveOffset)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `currentLevel` | `Int` | ✓ |  |

### `GetMaximumValue(akActor) → Float`

**Flags:** Native

Acquires the Maximum value for the current ActorValue

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetPerks(akActor, unowned, allRanks) → Perk[]`

**Flags:** Native

Same as GetPerkTree except returns into a new array

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` |  |  |
| `unowned` | `Bool` |  | `true` |
| `allRanks` | `Bool` |  | `false` |

### `GetPerkTree(list, akActor, unowned, allRanks)`

**Flags:** Native

Returns perks from the skill into the FormList
Actor filter applies to unowned and allRanks
unowned will add perks that the actor does not own, or only perks the actor owns
allRanks will add all ranks of each perk to the list, unowned/owned filter also applies

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `list` | `FormList` | ✓ |  |
| `akActor` | `Actor` |  |  |
| `unowned` | `Bool` |  | `true` |
| `allRanks` | `Bool` |  | `false` |

### `GetSkillExperience() → Float`

**Flags:** Native

Returns the amount of experienced gained in this skill

### `GetSkillImproveMult() → Float`

**Flags:** Native

### `GetSkillImproveOffset() → Float`

**Flags:** Native

### `GetSkillLegendaryLevel() → Int`

**Flags:** Native

Returns the legendary level of this skill

### `GetSkillOffsetMult() → Float`

**Flags:** Native

### `GetSkillUseMult() → Float`

**Flags:** Native

Skill Multiplier manipulation

### `IsSkill() → Bool`

**Flags:** Native

Returns whether this AVI is a skill

### `SetSkillExperience(exp)`

**Flags:** Native

Does not trigger skill-up

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `exp` | `Float` | ✓ |  |

### `SetSkillImproveMult(value)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `value` | `Float` | ✓ |  |

### `SetSkillImproveOffset(value)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `value` | `Float` | ✓ |  |

### `SetSkillLegendaryLevel(level)`

**Flags:** Native

Sets the legendary level of this skill

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `level` | `Int` | ✓ |  |

### `SetSkillOffsetMult(value)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `value` | `Float` | ✓ |  |

### `SetSkillUseMult(value)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `value` | `Float` | ✓ |  |
