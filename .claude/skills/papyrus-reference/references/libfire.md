# `LibFire`

**Source:** `libfire` (LibFire) • **Flags:** Hidden

---

## Global Functions

### `ActorFindAnyKeyword(akActor, argKeywords) → Int`

**Flags:** Native Global

Returns the index of the first keyword in `argKeywords` assigned to `akActor` - if not found, -1 is returned

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `argKeywords` | `Keyword[]` | ✓ |  |

### `ActorFindAnyPerk(akActor, argPerks) → Int`

**Flags:** Native Global

Returns the index of the first perk in `argPerks` assigned to `akActor` - if not found, -1 is returned

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `argPerks` | `Perk[]` | ✓ |  |

### `ActorFindCrimeFactions(akActor) → Faction[]`

**Flags:** Native Global

Returns an array of factions that track crime and of which `akActor` is a current member

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `ActorHasAnyKeyword(akActor, akKeywords) → Bool`

**Flags:** Native Global

Returns whether `akActor` has any keyword in `akKeywords`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `akKeywords` | `FormList` | ✓ |  |

### `ActorHasPerkRank(akActor, akPerk, aiRank) → Bool`

**Flags:** Native Global

Returns whether `akActor` has `akPerk` and its rank is `aiRank` - if match not found, `False` is returned

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `akPerk` | `Perk` | ✓ |  |
| `aiRank` | `Int` | ✓ |  |

### `ActorIsCommandedBy(akActor, akOtherActor) → Bool`

**Flags:** Native Global

Returns whether `akActor` is commanded by `akOtherActor`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `akOtherActor` | `Actor` | ✓ |  |

### `ActorIsCommandedByPlayer(akActor) → Bool`

**Flags:** Native Global

Returns whether `akActor` is commanded by the player

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `ActorIsFollower(akActor) → Bool`

**Flags:** Native Global

Returns whether `akActor` is a teammate or player-controlled commanded/summoned actor

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `ActorIsInAnyFaction(akActor, akFactions) → Bool`

**Flags:** Native Global

Returns whether `akActor` is a member of any faction in `akFactions` with a rank greater than -1

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `akFactions` | `FormList` | ✓ |  |

### `ActorIsInFaction(akActor, akFaction) → Bool`

**Flags:** Native Global

Returns whether `akActor` is a member of `akFaction` with a rank greater than -1

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `akFaction` | `Faction` | ✓ |  |

### `ActorIsSummoned(akActor) → Bool`

**Flags:** Native Global

Returns whether `akActor` is a summoned actor

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `ArrayFindClosestActor(argActors, akOrigin) → Int`

**Flags:** Native Global

Searches `argActors` for closest actor to `akOrigin` and returns index of member - if not found, -1 is returned

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `argActors` | `Actor[]` | ✓ |  |
| `akOrigin` | `ObjectReference` | ✓ |  |

### `ArrayFindGlobalValue(argGlobals, afValue) → Int`

**Flags:** Native Global

Faction

Searches `argHaystack` for `afValue` and returns index of member - if not found, -1 is returned

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `argGlobals` | `GlobalVariable[]` | ✓ |  |
| `afValue` | `Float` | ✓ |  |

### `ClearFactionReactionsCache() → Bool`

**Flags:** Native Global

FormList

Clears cached faction fight reactions (sometimes required to update faction actors)
  Note: SetAllies and SetEnemies already clears the faction reactions cache.

### `ContainsText(asText, asSubText) → Bool`

**Flags:** Native Global

Returns whether `asText` contains `asSubText` (all Papyrus string comparisons are case-insensitive)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asText` | `String` | ✓ |  |
| `asSubText` | `String` | ✓ |  |

### `CopyFactionCrimeGold(akFaction, akOtherFaction, abModify)`

**Flags:** Native Global

Copies violent and nonviolent crime gold from `akFaction` to `akOtherFaction`. If `abModify` is True,
  adds crime gold values to existing values instead.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFaction` | `Faction` | ✓ |  |
| `akOtherFaction` | `Faction` | ✓ |  |
| `abModify` | `Bool` | ✓ |  |

### `FindClosestActorByLOS(akOrigin, afRadius) → Actor`

**Flags:** Native Global

Returns closest actor within `afRadius` of and line of sight to `akOrigin`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akOrigin` | `ObjectReference` | ✓ |  |
| `afRadius` | `Float` | ✓ |  |

### `FindClosestActorInFaction(akOrigin, akFaction, afRadius) → Actor`

**Flags:** Native Global

Returns closest actor who is a member of `akFaction` within `afRadius` of `akOrigin`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akOrigin` | `ObjectReference` | ✓ |  |
| `akFaction` | `Faction` | ✓ |  |
| `afRadius` | `Float` | ✓ |  |

### `FindClosestActorInFactionByLOS(akOrigin, akFaction, afRadius) → Actor`

**Flags:** Native Global

Returns closest actor who is a member of `akFaction` within `afRadius` of and line of sight to `akOrigin`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akOrigin` | `ObjectReference` | ✓ |  |
| `akFaction` | `Faction` | ✓ |  |
| `afRadius` | `Float` | ✓ |  |

### `FindNearbyActors(akOrigin, afRadius) → Actor[]`

**Flags:** Native Global

Returns actors in loaded cells within `afRadius` of `akOrigin`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akOrigin` | `ObjectReference` | ✓ |  |
| `afRadius` | `Float` | ✓ |  |

### `FindNearbyActorsInFaction(akOrigin, akFaction, afRadius) → Actor[]`

**Flags:** Native Global

Returns actors who are members of `akFaction` in loaded cells within `afRadius` of `akOrigin`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akOrigin` | `ObjectReference` | ✓ |  |
| `akFaction` | `Faction` | ✓ |  |
| `afRadius` | `Float` | ✓ |  |

### `FindNearbyActorsInFactionByLOS(akOrigin, akFaction, afRadius) → Actor[]`

**Flags:** Native Global

Returns actors who are members of `akFaction` in loaded cells within `afRadius` of and line of sight to `akOrigin`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akOrigin` | `ObjectReference` | ✓ |  |
| `akFaction` | `Faction` | ✓ |  |
| `afRadius` | `Float` | ✓ |  |

### `FindNearbyBooks(akOrigin, afRadius) → ObjectReference[]`

**Flags:** Native Global

Returns books in loaded cells within `afRadius` of `akOrigin`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akOrigin` | `ObjectReference` | ✓ |  |
| `afRadius` | `Float` | ✓ |  |

### `FindNearbyCommandedActors(akOrigin, afRadius) → Actor[]`

**Flags:** Native Global

Returns commanded actors in loaded cells within `afRadius` of `akOrigin` who are controlled by `akOrigin`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akOrigin` | `ObjectReference` | ✓ |  |
| `afRadius` | `Float` | ✓ |  |

### `FindNearbyFollowers(afRadius) → Actor[]`

**Flags:** Native Global

Returns teammates and player-controlled commanded/summoned actors in loaded cells within `afRadius` of player

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afRadius` | `Float` | ✓ |  |

### `FindNearbySummons(akOrigin, afRadius) → Actor[]`

**Flags:** Native Global

Returns summoned actors in loaded cells within `afRadius` of `akOrigin`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akOrigin` | `ObjectReference` | ✓ |  |
| `afRadius` | `Float` | ✓ |  |

### `FindNearbyTeammates(afRadius) → Actor[]`

**Flags:** Native Global

Returns teammates in loaded cells within `afRadius` of player

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afRadius` | `Float` | ✓ |  |

### `FindPlayerInfamousWithFactions() → Faction[]`

**Flags:** Native Global

Returns an array of factions with which the player is infamous

### `FindPlayerWantedByFactions() → Faction[]`

**Flags:** Native Global

Returns an array of factions to which the player owes crime gold

### `FormatFloat(asFormat, argValues) → String`

**Flags:** Native Global

Replaces `{}` tokens in `asFormat` with `argValues` (supports up to 9 values)
  Note: Arrays exceeding the maximum number of values will be truncated.
  Syntax: https://fmt.dev/latest/syntax.html

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asFormat` | `String` | ✓ |  |
| `argValues` | `Float[]` | ✓ |  |

### `FormatInt(asFormat, argValues) → String`

**Flags:** Native Global

Replaces `{}` tokens in `asFormat` with `argValues` (supports up to 9 values)
  Note: Arrays exceeding the maximum number of values will be truncated.
  Syntax: https://fmt.dev/latest/syntax.html

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asFormat` | `String` | ✓ |  |
| `argValues` | `Int[]` | ✓ |  |

### `FormatString(asFormat, argValues) → String`

**Flags:** Native Global

Replaces `{}` tokens in `asFormat` with `argValues` (supports up to 9 values)
  Note: Arrays exceeding the maximum number of values will be truncated.
  Syntax: https://fmt.dev/latest/syntax.html

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asFormat` | `String` | ✓ |  |
| `argValues` | `String[]` | ✓ |  |

### `GetActorPerkRank(akActor, akPerk) → Int`

**Flags:** Native Global

Returns the current rank of `akPerk` assigned to `akActor` - if perk not assigned, -1 is returned

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `akPerk` | `Perk` | ✓ |  |

### `GetActorPerks(akActor) → Perk[]`

**Flags:** Native Global

Returns an array of perks assigned to `akActor`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetCommandedActors(akActor) → Actor[]`

**Flags:** Native Global

Returns an array of commanded actors for `akActor` or `None`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetCommandingActor(akActor) → Actor`

**Flags:** Native Global

Returns the commanding actor for `akActor` when actor is commanded

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetCurrentHourOfDay() → Float`

**Flags:** Native Global

Returns hours passed since current day began

### `GetEquippedAmmo(akActor) → Ammo`

**Flags:** Native Global

Array

Returns the ammo currently used by `akActor`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetFactionCrimeValue(akFaction, aiMember) → Float`

**Flags:** Native Global

Returns crime value (murder, assault, etc.) for `akFaction` at `aiMember` offset
Valid offsets:
  - 0x0  Arrest (cast to Boolean)
  - 0x01 Attack On Sight (cast to Boolean)
  - 0x02 Murder (cast to Int)
  - 0x04 Assault (cast to Int)
  - 0x06 Trespass (cast to Int)
  - 0x08 Pickpocket (cast to Int)
  - 0x0C Steal Multiplier (Float)
  - 0x10 Escape (cast to Int)
  - 0x12 Werewolf (cast to Int)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFaction` | `Faction` | ✓ |  |
| `aiMember` | `Int` | ✓ |  |

### `GetFactionIgnoresAssault(akFaction) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFaction` | `Faction` | ✓ |  |

### `GetFactionIgnoresMurder(akFaction) → Bool`

**Flags:** Native Global

Returns flag values for `akFaction`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFaction` | `Faction` | ✓ |  |

### `GetFactionIgnoresPickpocket(akFaction) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFaction` | `Faction` | ✓ |  |

### `GetFactionIgnoresStealing(akFaction) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFaction` | `Faction` | ✓ |  |

### `GetFactionIgnoresTrespass(akFaction) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFaction` | `Faction` | ✓ |  |

### `GetFactionIgnoresWerewolf(akFaction) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFaction` | `Faction` | ✓ |  |

### `GetFactionReportsCrimesAgainstMembers(akFaction) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFaction` | `Faction` | ✓ |  |

### `GetFactionTracksCrime(akFaction) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFaction` | `Faction` | ✓ |  |

### `GetFactionUsesCrimeGoldDefaults(akFaction) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFaction` | `Faction` | ✓ |  |

### `GetHighestMinSkillLevelForSpell(akSpell) → Int`

**Flags:** Native Global

Time

Returns highest minimum skill level for `akSpell` (does not account for conditions)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSpell` | `Spell` | ✓ |  |

### `GetPermanentActorValue(akActor, asActorValue) → Float`

**Flags:** Native Global

Player Character

Returns the permanent value of `asActorValue` for `akActor`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `ObjectReference` | ✓ |  |
| `asActorValue` | `String` | ✓ |  |

### `GetRaceCarryWeight(akRace) → Float`

**Flags:** Native Global

Returns the base carry weight for `akRace`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRace` | `Race` | ✓ |  |

### `GetRaceFemaleHeight(akRace) → Float`

**Flags:** Native Global

Returns the base female height for `akRace`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRace` | `Race` | ✓ |  |

### `GetRaceFemaleWeight(akRace) → Float`

**Flags:** Native Global

Returns the base female weight for `akRace`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRace` | `Race` | ✓ |  |

### `GetRaceHealthRegen(akRace) → Float`

**Flags:** Native Global

Returns the base health regen for `akRace`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRace` | `Race` | ✓ |  |

### `GetRaceMagickaRegen(akRace) → Float`

**Flags:** Native Global

Returns the base magicka regen for `akRace`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRace` | `Race` | ✓ |  |

### `GetRaceMaleHeight(akRace) → Float`

**Flags:** Native Global

Returns the base male height for `akRace`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRace` | `Race` | ✓ |  |

### `GetRaceMaleWeight(akRace) → Float`

**Flags:** Native Global

Returns the base male weight for `akRace`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRace` | `Race` | ✓ |  |

### `GetRaceMass(akRace) → Float`

**Flags:** Native Global

Returns the base mass for `akRace`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRace` | `Race` | ✓ |  |

### `GetRaceSkillBonus(akRace, asActorValue) → Int`

**Flags:** Native Global

Returns the skill boost value of `asActorValue` for `akRace`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRace` | `Race` | ✓ |  |
| `asActorValue` | `String` | ✓ |  |

### `GetRaceSkills(akRace) → String[]`

**Flags:** Native Global

Returns names of boosted actor values for `akRace`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRace` | `Race` | ✓ |  |

### `GetRaceStaminaRegen(akRace) → Float`

**Flags:** Native Global

Returns the base stamina regen for `akRace`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRace` | `Race` | ✓ |  |

### `GetRaceStartingHealth(akRace) → Float`

**Flags:** Native Global

Returns the starting health for `akRace`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRace` | `Race` | ✓ |  |

### `GetRaceStartingMagicka(akRace) → Float`

**Flags:** Native Global

Returns the starting magicka for `akRace`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRace` | `Race` | ✓ |  |

### `GetRaceStartingStamina(akRace) → Float`

**Flags:** Native Global

Returns the starting stamina for `akRace`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRace` | `Race` | ✓ |  |

### `GetRaceUnarmedDamage(akRace) → Float`

**Flags:** Native Global

Returns the base unarmed damage for `akRace`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRace` | `Race` | ✓ |  |

### `GetRaceUnarmedReach(akRace) → Float`

**Flags:** Native Global

String

Returns the base unarmed reach for `akRace`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRace` | `Race` | ✓ |  |

### `IntToHex(aiSource) → String`

**Flags:** Native Global

Returns the hexadecimal string representation of `aiSource`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiSource` | `Int` | ✓ |  |

### `IsPlayerInfamous() → Bool`

**Flags:** Native Global

Returns whether the player is infamous with any faction

### `IsPlayerWanted() → Bool`

**Flags:** Native Global

Race

Returns whether the player is wanted by any faction

### `ResetFactionCrimeGold(akFaction)`

**Flags:** Native Global

Zeroes out violent and nonviolent crime gold on `akFaction`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFaction` | `Faction` | ✓ |  |

### `SearchListForForms(akHaystack, argNeedles) → Bool[]`

**Flags:** Native Global

Returns whether `akHaystack` contains each form in `argNeedles`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akHaystack` | `FormList` | ✓ |  |
| `argNeedles` | `Form[]` | ✓ |  |

### `SearchListsForForm(akHaystack, akNeedle) → Bool[]`

**Flags:** Native Global

ObjectReference

Returns whether each formlist in `akHaystack` contains `akNeedle`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akHaystack` | `FormList` | ✓ |  |
| `akNeedle` | `Form` | ✓ |  |

### `SetAllies(akFaction, akFactions, abSelfIsFriendToOther, abOtherIsFriendToSelf)`

**Flags:** Native Global

Sets `akFaction` as ally or friend to each faction in `akFactions`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFaction` | `Faction` | ✓ |  |
| `akFactions` | `FormList` | ✓ |  |
| `abSelfIsFriendToOther` | `Bool` |  | `false` |
| `abOtherIsFriendToSelf` | `Bool` |  | `false` |

### `SetEnemies(akFaction, akFactions, abSelfIsNeutralToOther, abOtherIsNeutralToSelf)`

**Flags:** Native Global

Sets `akFaction` as enemy or neutral to each faction in `akFactions`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFaction` | `Faction` | ✓ |  |
| `akFactions` | `FormList` | ✓ |  |
| `abSelfIsNeutralToOther` | `Bool` |  | `false` |
| `abOtherIsNeutralToSelf` | `Bool` |  | `false` |

### `SplitString(asSource, asDelimiter) → String[]`

**Flags:** Native Global

Returns `asSource` as array of String split by `asDelimiter`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asSource` | `String` | ✓ |  |
| `asDelimiter` | `String` | ✓ |  |

### `StrToFloatArray(asSource, asDelimiter) → Float[]`

**Flags:** Native Global

Returns `asSource` as array of Float split by `asDelimiter`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asSource` | `String` | ✓ |  |
| `asDelimiter` | `String` | ✓ |  |

### `StrToIntArray(asSource, asDelimiter) → Int[]`

**Flags:** Native Global

Returns `asSource` as array of Int split by `asDelimiter`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asSource` | `String` | ✓ |  |
| `asDelimiter` | `String` | ✓ |  |

### `WrapString(asSource, aiMaxLength) → String`

**Flags:** Native Global

Spell

Returns `asSource` wrapped to column `aiMaxLength` with lines delimited by newline character

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asSource` | `String` | ✓ |  |
| `aiMaxLength` | `Int` | ✓ |  |
