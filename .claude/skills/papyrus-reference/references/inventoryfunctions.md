# `_Q2C_Functions`

**Source:** `inventoryfunctions` (Inventory Functions SE) • **Flags:** Hidden

---

## Global Functions

### `ActorBaseGetShouts(akActorBase, akKeyword) → Shout[]`

**Flags:** Native Global

Returns an array of Shouts that match the specified Keyword.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActorBase` | `ActorBase` | ✓ |  |
| `akKeyword` | `Keyword` |  |  |

### `ActorBaseGetSpells(akActorBase, akKeyword, asSchool, aiLevel, aiLevelComparison) → Spell[]`

**Flags:** Native Global

Scans the MagicEffects of the Actor's spells, adding the Spell to the returned array if the MagicEffect matches the criteria.
Use the default values to ignore that condition (eg sending asSchool as "" means 'of any School')
NOTE: sending just the akActorBase (or sending all additional arguments as their default values) will now return ALL spells set on the ActorBase - rather than an empty list as before

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActorBase` | `ActorBase` | ✓ |  |
| `akKeyword` | `Keyword` |  |  |
| `asSchool` | `String` |  | `""` |
| `aiLevel` | `Int` |  | `-1` |
| `aiLevelComparison` | `Int` |  | `1` |

### `ActorBaseHasShout(akActorBase, akKeyword) → Bool`

**Flags:** Global

Scans the ActorBase's shouts, returning true at the first one that matches the specified Keyword.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActorBase` | `ActorBase` | ✓ |  |
| `akKeyword` | `Keyword` | ✓ |  |

### `ActorBaseHasSpell(akActorBase, akKeyword, asSchool, aiLevel, aiLevelComparison) → Bool`

**Flags:** Global

Scans the MagicEffects of the ActorBase's spells, returning true at the first one that matches any of the supplied criteria.
NOTE: sending just the akActorBase (or sending all additional arguments as their default values) will return TRUE if the ActorBase has any spell at all

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActorBase` | `ActorBase` | ✓ |  |
| `akKeyword` | `Keyword` |  |  |
| `asSchool` | `String` |  | `""` |
| `aiLevel` | `Int` |  | `-1` |
| `aiLevelComparison` | `Int` |  | `1` |

### `ActorGetSpells(akActor, akKeyword, asSchool, aiLevel, aiLevelComparison, abSearchBase) → Spell[]`

**Flags:** Native Global

Scans the MagicEffects of the Actor's spells, adding the Spell to the returned array if the MagicEffect matches the criteria.
Use the default values to ignore that condition (eg sending asSchool as "" means 'of any School')
NOTE: sending just the akActor (or sending all additional arguments as their default values) will now return ALL spells the Actor knows - rather than an empty list as before
Optionally will also check through the relevant ActorBase (which is more likely to be the one with the spells)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `akKeyword` | `Keyword` |  |  |
| `asSchool` | `String` |  | `""` |
| `aiLevel` | `Int` |  | `-1` |
| `aiLevelComparison` | `Int` |  | `1` |
| `abSearchBase` | `Bool` |  | `true` |

### `ActorHasSpell(akActor, akKeyword, asSchool, aiLevel, aiLevelComparison, abSearchBase) → Bool`

**Flags:** Global

Scans the MagicEffects of the Actor's spells, returning true at the first one that matches any of the supplied criteria.
NOTE: sending just the akActor (or sending all additional arguments as their default values) will return TRUE if the actor has any spell at all
Optionally will also check through the relevant ActorBase (which is more likely to be the one with the spells)

----------------------------------------
     Additional spell-checking Functions
          also added by Kalivore
----------------------------------------

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `akKeyword` | `Keyword` |  |  |
| `asSchool` | `String` |  | `""` |
| `aiLevel` | `Int` |  | `-1` |
| `aiLevelComparison` | `Int` |  | `1` |
| `abSearchBase` | `Bool` |  | `true` |

### `GetNthFormOfType(akObjRef, aiType, aiItemIndex) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akObjRef` | `ObjectReference` | ✓ |  |
| `aiType` | `Int` | ✓ |  |
| `aiItemIndex` | `Int` | ✓ |  |

### `GetNthFormWithKeyword(akObjRef, akKeyword, aiItemIndex) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akObjRef` | `ObjectReference` | ✓ |  |
| `akKeyword` | `Keyword` | ✓ |  |
| `aiItemIndex` | `Int` | ✓ |  |

### `GetNumItemsOfType(akObjRef, aiType) → Int`

**Flags:** Native Global

added by Kalivore - type is the SKSE itemType (eg 26 for armour, or 46 for potion)
full list at http://www.creationkit.com/index.php?title=GetType_-_Form

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akObjRef` | `ObjectReference` | ✓ |  |
| `aiType` | `Int` | ✓ |  |

### `GetNumItemsWithKeyword(akObjRef, akKeyword) → Int`

**Flags:** Native Global

-------------------------------------------------
              Inventory Functions
-------------------------------------------------
 Q2C's original functions, updated for SKSE64
 Note that GetNumItemsWithKeyword groups by item type
 ie, a stack of ten daggers counts as ONE toward the total, NOT ten

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akObjRef` | `ObjectReference` | ✓ |  |
| `akKeyword` | `Keyword` | ✓ |  |

### `GetPoison(akObjRef) → Potion`

**Flags:** Global

this is also already done by SKSE, so just forward on to that version

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akObjRef` | `ObjectReference` | ✓ |  |

### `GetPoisonCharges(akObjRef) → Int`

**Flags:** Native Global

Get the number of poison charges on akObjRef
Returns: The number of charges, or -1 if unsuccessful for any reason
		(eg the item is not a weapon, or no poison applied)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akObjRef` | `ObjectReference` | ✓ |  |

### `GetTotalBaseGoldValue(akObjRef) → Int`

**Flags:** Native Global

get total of the base gold values of items in container
'base' means the extra value of enchantments/improvements on items is ignored

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akObjRef` | `ObjectReference` | ✓ |  |

### `RemovePoison(akObjRef)`

**Flags:** Native Global

Remove the poison from the weapon akObjRef

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akObjRef` | `ObjectReference` | ✓ |  |

### `SetDamage(akAmmo, afDamage)`

**Flags:** Native Global

Sets the base damage of this ammo

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAmmo` | `Ammo` | ✓ |  |
| `afDamage` | `Float` | ✓ |  |

### `SetIsBolt(akAmmo, abIsBolt)`

**Flags:** Native Global

Sets whether this ammo is a bolt

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAmmo` | `Ammo` | ✓ |  |
| `abIsBolt` | `Bool` | ✓ |  |

### `SetPoison(akObjRef, akPoison, aiCharges) → Int`

**Flags:** Native Global

Applies akPoison to akObjRef.  Note the item MUST be a weapon, or the function will fail and return -1
Returns: The number of poison charges the weapon now has (which should be the same as aiCharges)
		or -1 if unsuccessful

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akObjRef` | `ObjectReference` | ✓ |  |
| `akPoison` | `Potion` | ✓ |  |
| `aiCharges` | `Int` |  | `1` |

### `SetPoisonCharges(akObjRef, aiCharges) → Int`

**Flags:** Native Global

Set the number of poison charges on akObjRef
Returns: The number of poison charges now on the weapon in aiHandSlot (which should be the same as aiCharges)
		or -1 if unsuccessful for any reason (eg the item is not a weapon, or no poison applied)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akObjRef` | `ObjectReference` | ✓ |  |
| `aiCharges` | `Int` |  | `1` |

### `SetProjectile(akAmmo, akProjectile)`

**Flags:** Native Global

Sets the projectile associated with this ammo
--WARNING!!-- Highly untested! >:$

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akAmmo` | `Ammo` | ✓ |  |
| `akProjectile` | `Projectile` | ✓ |  |

### `SpellLevelAdept() → Int`

**Flags:** Global

returns 50

### `SpellLevelAny() → Int`

**Flags:** Global

returns -1

### `SpellLevelApprentice() → Int`

**Flags:** Global

returns 25

### `SpellLevelComparisonEq() → Int`

**Flags:** Global

returns 0. Means spell level must be equal to specified aiLevel to be counted

### `SpellLevelComparisonGt() → Int`

**Flags:** Global

returns 2. Means spell level must be greater than specified aiLevel to be counted

### `SpellLevelComparisonGtEq() → Int`

**Flags:** Global

returns 1. Means spell level must be equal to or greater than specified aiLevel to be counted

### `SpellLevelComparisonLt() → Int`

**Flags:** Global

returns -2. Means spell level must be lower than specified aiLevel to be counted

Valid aiLevelComparison values (only applied if aiLevel >= 0):

### `SpellLevelComparisonLtEq() → Int`

**Flags:** Global

returns -1. Means spell level must be less than or equal to specified aiLevel to be counted

### `SpellLevelExpert() → Int`

**Flags:** Global

returns 75

### `SpellLevelMaster() → Int`

**Flags:** Global

returns 100

### `SpellLevelNovice() → Int`

**Flags:** Global

returns 0 (all Novice spells I found were level 0)

### `SpellSchoolAlteration() → String`

**Flags:** Global

returns "Alteration"

### `SpellSchoolAny() → String`

**Flags:** Global

returns an empty string ""

----------------------------------------
     Additional spell-listing Functions
          also added by Kalivore
----------------------------------------

### `SpellSchoolConjuration() → String`

**Flags:** Global

returns "Conjuration"

### `SpellSchoolDestruction() → String`

**Flags:** Global

returns "Destruction"

### `SpellSchoolIllusion() → String`

**Flags:** Global

returns "Illusion"

### `SpellSchoolRestoration() → String`

**Flags:** Global

returns "Restoration"

### `WornGetPoison(akActor, aiHandSlot) → Potion`

**Flags:** Global

These are all just shortcuts to the full WornObject versions above, sending aiSlotMask as 0

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `aiHandSlot` | `Int` | ✓ |  |

### `WornGetPoisonCharges(akActor, aiHandSlot) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `aiHandSlot` | `Int` | ✓ |  |

### `WornObjectGetPoison(akActor, aiHandSlot, aiSlotMask) → potion`

**Flags:** Global

this is already done by SKSE, so just forward on to that version

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `aiHandSlot` | `Int` | ✓ |  |
| `aiSlotMask` | `Int` | ✓ |  |

### `WornObjectGetPoisonCharges(akActor, aiHandSlot, aiSlotMask) → Int`

**Flags:** Native Global

Get the number of poison charges on the weapon in aiHandSlot
Returns: The number of charges, or -1 if unsuccessful for any reason
		(eg the item is not a weapon, or no poison applied)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `aiHandSlot` | `Int` | ✓ |  |
| `aiSlotMask` | `Int` | ✓ |  |

### `WornObjectRemovePoison(akActor, aiHandSlot, aiSlotMask)`

**Flags:** Native Global

Remove the poison from the weapon in aiHandSlot

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `aiHandSlot` | `Int` | ✓ |  |
| `aiSlotMask` | `Int` | ✓ |  |

### `WornObjectSetPoison(akActor, aiHandSlot, aiSlotMask, akPoison, aiCharges) → Int`

**Flags:** Native Global

Applies akPoison to weapon in aiHandSlot.  Note the item MUST be a weapon, or the function will fail and return -1
Returns: The number of poison charges the weapon now has (which should be the same as aiCharges)
		or -1 if unsuccessful

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `aiHandSlot` | `Int` | ✓ |  |
| `aiSlotMask` | `Int` | ✓ |  |
| `akPoison` | `Potion` | ✓ |  |
| `aiCharges` | `Int` |  | `1` |

### `WornObjectSetPoisonCharges(akActor, aiHandSlot, aiSlotMask, aiCharges) → Int`

**Flags:** Native Global

Set the number of poison charges on the weapon in aiHandSlot
Returns: The number of poison charges now on the weapon in aiHandSlot (which should be the same as aiCharges)
		or -1 if unsuccessful for any reason (eg the item is not a weapon, or no poison applied)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `aiHandSlot` | `Int` | ✓ |  |
| `aiSlotMask` | `Int` | ✓ |  |
| `aiCharges` | `Int` |  | `1` |

### `WornRemovePoison(akActor, aiHandSlot)`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `aiHandSlot` | `Int` | ✓ |  |

### `WornSetPoison(akActor, aiHandSlot, poison, aiCharges) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `aiHandSlot` | `Int` | ✓ |  |
| `poison` | `Potion` | ✓ |  |
| `aiCharges` | `Int` |  | `1` |

### `WornSetPoisonCharges(akActor, aiHandSlot, aiCharges) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `aiHandSlot` | `Int` | ✓ |  |
| `aiCharges` | `Int` |  | `1` |
