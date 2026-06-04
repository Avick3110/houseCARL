# `ANDR_PapyrusFunctions`

**Source:** `andrealphus` (Andrealphus' Papyrus Functions) • **Flags:** Hidden

**Imports:** `Math`

---

## Global Functions

### `CastEnchantment(akSource, akEnchantment, akTarget)`

**Flags:** Native Global

- akSource: The Actor from which to cast the Enchantment.
- akEnchantment: Enchantment to cast.
- akTarget: Actor at which to aim the Enchantment.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSource` | `Actor` | ✓ |  |
| `akEnchantment` | `Enchantment` | ✓ |  |
| `akTarget` | `Actor` | ✓ |  |

### `CastIngredient(akSource, akIngedient, akTarget)`

**Flags:** Native Global

- akSource: The Actor from which to cast the Ingredient.
- akIngedient: Ingredient to cast.
- akTarget: Actor at which to aim the Ingredient.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSource` | `Actor` | ✓ |  |
| `akIngedient` | `Ingredient` | ✓ |  |
| `akTarget` | `Actor` | ✓ |  |

### `CastPotion(akSource, akPotion, akTarget)`

**Flags:** Native Global

- akSource: The Actor from which to cast the Potion.
- akPotion: Potion to cast.
- akTarget: Actor at which to aim the Potion.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSource` | `Actor` | ✓ |  |
| `akPotion` | `Potion` | ✓ |  |
| `akTarget` | `Actor` | ✓ |  |

### `CastSpellFromHand(akSource, akSpell, IsLeftHand, DistanceVar, HeightVar, Offset_NoSneak_Left_X, Offset_NoSneak_Left_Y, Offset_NoSneak_Left_Z, Offset_NoSneak_Right_X, Offset_NoSneak_Right_Y, Offset_NoSneak_Right_Z, Offset_Sneak_Left_X, Offset_Sneak_Left_Y, Offset_Sneak_Left_Z, Offset_Sneak_Right_X, Offset_Sneak_Right_Y, Offset_Sneak_Right_Z)`

**Flags:** Global

; Dislaimer: This can be replaced with LaunchSpellProjectile(), but I'm keeping this function for reference and dependencies.
- akSource: The caster.
- akSpell: The spell to cast.
- IsLeftHand: True if cast from the left hand, false if cast from the right hand.
- DistanceVar: Optional, the distance from the caster, where the destination marker is spawned. Default is 2000 units.
- HeightVar: Optional, the height difference at which destination marker is spawned. Default is 100 units.
- Offset_NoSneak_Left_X: Optional, X Offset for left hand when the actor is not sneaking. Default value is 30.0.
- Offset_NoSneak_Left_Y: Optional, Y Offset for left hand when the actor is not sneaking. Default value is 30.0.
- Offset_NoSneak_Left_Z: Optional, Z Offset for left hand when the actor is not sneaking. Default value is 110.0.
- Offset_NoSneak_Right_X: Optional, X Offset for right hand when the actor is not sneaking. Default value is 30.0.
- Offset_NoSneak_Right_Y: Optional, Y Offset for right hand when the actor is not sneaking. Default value is -30.0.
- Offset_NoSneak_Right_Z: Optional, Z Offset for right hand when the actor is not sneaking. Default value is 110.0.
- Offset_Sneak_Left_X: Optional, X Offset for left hand when the actor is sneaking. Default value is 30.0.
- Offset_Sneak_Left_Y: Optional, Y Offset for left hand when the actor is sneaking. Default value is 30.0.
- Offset_Sneak_Left_Z: Optional, Z Offset for left hand when the actor is sneaking. Default value is 70.0.
- Offset_Sneak_Right_X: Optional, X Offset for right hand when the actor is sneaking. Default value is 30.0.
- Offset_Sneak_Right_Y: Optional, Y Offset for right hand when the actor is sneaking. Default value is -30.0.
- Offset_Sneak_Right_Z: Optional, Z Offset for right hand when the actor is sneaking. Default value is 70.0.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSource` | `Actor` | ✓ |  |
| `akSpell` | `Spell` | ✓ |  |
| `IsLeftHand` | `Bool` | ✓ |  |
| `DistanceVar` | `Float` |  | `2000` |
| `HeightVar` | `Float` |  | `100` |
| `Offset_NoSneak_Left_X` | `Float` |  | `30` |
| `Offset_NoSneak_Left_Y` | `Float` |  | `30` |
| `Offset_NoSneak_Left_Z` | `Float` |  | `110` |
| `Offset_NoSneak_Right_X` | `Float` |  | `30` |
| `Offset_NoSneak_Right_Y` | `Float` |  | `-30` |
| `Offset_NoSneak_Right_Z` | `Float` |  | `110` |
| `Offset_Sneak_Left_X` | `Float` |  | `30` |
| `Offset_Sneak_Left_Y` | `Float` |  | `30` |
| `Offset_Sneak_Left_Z` | `Float` |  | `70` |
| `Offset_Sneak_Right_X` | `Float` |  | `30` |
| `Offset_Sneak_Right_Y` | `Float` |  | `-30` |
| `Offset_Sneak_Right_Z` | `Float` |  | `70` |

### `CastSpellFromPointToPoint(akSource, akSpell, StartPoint_X, StartPoint_Y, StartPoint_Z, EndPoint_X, EndPoint_Y, EndPoint_Z)`

**Flags:** Native Global

- akSource: The caster.
- akSpell: The spell to cast.
- StartPoint_X: The X position of the starting point.
- StartPoint_Y: The Y position of the starting point.
- StartPoint_Z: The Z position of the starting point.
- EndPoint_X: The X position of the ending point.
- EndPoint_Y: The Y position of the ending point.
- EndPoint_Z: The Z position of the ending point.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSource` | `Actor` | ✓ |  |
| `akSpell` | `Spell` | ✓ |  |
| `StartPoint_X` | `Float` | ✓ |  |
| `StartPoint_Y` | `Float` | ✓ |  |
| `StartPoint_Z` | `Float` | ✓ |  |
| `EndPoint_X` | `Float` | ✓ |  |
| `EndPoint_Y` | `Float` | ✓ |  |
| `EndPoint_Z` | `Float` | ✓ |  |

### `CastSpellFromRef(akSource, akSpell, akTarget, akOriginRef)`

**Flags:** Native Global

- akSource: The caster of the spell.
- akSpell: Spell to cast.
- akTarget: An ObjectReference at which to aim the spell.
- akOriginRef: The ObjectReference where to cast the spell from.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSource` | `Actor` | ✓ |  |
| `akSpell` | `Spell` | ✓ |  |
| `akTarget` | `ObjectReference` | ✓ |  |
| `akOriginRef` | `ObjectReference` | ✓ |  |

### `CastSpellFromRefAimed(akSource, akSpell, akOriginRef)`

**Flags:** Global

- akSource: The caster of the spell.
- akSpell: Spell to cast.
- akOriginRef: The ObjectReference where to cast the spell from.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSource` | `Actor` | ✓ |  |
| `akSpell` | `Spell` | ✓ |  |
| `akOriginRef` | `ObjectReference` | ✓ |  |

### `GetActiveMagicEffectFromActor(akActor, akMagicEffect) → ActiveMagicEffect`

**Flags:** Native Global

- akActor: the actor to check.
- akMagicEffect: the base magic to look for.
- Returns: the instance (ActiveMagicEffect) of akMagicEffect on the akActor.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `akMagicEffect` | `MagicEffect` | ✓ |  |

### `GetAndrealphusExtenderVersion() → String`

**Flags:** Native Global

- Return value: a string that's the version number of this mod.

### `GetEffectiveEnchantmentCost(akSource, akEnchantment) → Float`

**Flags:** Native Global

- Return value: the total effective cost of all the enchantment's effects.
- akSource: the user of the enchantment.
- akEnchantment: the enchantment.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSource` | `Actor` | ✓ |  |
| `akEnchantment` | `Enchantment` | ✓ |  |

### `GetEffectiveIngredientCost(akSource, akIngredient) → Float`

**Flags:** Native Global

- Return value: the total effective cost of all the ingredient's effects.
- akSource: the user of the ingredient.
- akIngredient: the ingredient.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSource` | `Actor` | ✓ |  |
| `akIngredient` | `Ingredient` | ✓ |  |

### `GetEffectivePotionCost(akSource, akPotion) → Float`

**Flags:** Native Global

- Return value: the total effective cost of all the potion's effects.
- akSource: the user of the potion.
- akPotion: the potion.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSource` | `Actor` | ✓ |  |
| `akPotion` | `Potion` | ✓ |  |

### `GetEffectiveScrollCost(akSource, akScroll) → Float`

**Flags:** Native Global

- Return value: the total effective cost of all the scroll's effects.
- akSource: the user of the scroll.
- akScroll: the scroll.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSource` | `Actor` | ✓ |  |
| `akScroll` | `Scroll` | ✓ |  |

### `LaunchAmmo(akCaster, akAmmo, akWeapon, sNodeName, akTarget, akProjectile, OriginSecondRef)`

**Flags:** Native Global

; based off of fenix31415's and po3's launcharrow function
- akCaster: the actor "casting" the ammo.
- akAmmo: the ammo being used
- akWeapon: the weapon that's being used.
- NodeSource: the name of the skeleton bone node of the akCaster, the ammo is launched from. If empty, it will be cast from the origin point of akCaster or OriginSecondRef (only if filled.)
- akTarget: the target of the ammo. (might cause issues if none)
- akProjectile: the base projectile.
- OriginSecondRef: an optional second ref to launch the ammo from (as proxy). If not None, NodeSource will be taken from this ref, instead of akCaster.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akCaster` | `Actor` | ✓ |  |
| `akAmmo` | `Ammo` | ✓ |  |
| `akWeapon` | `Weapon` | ✓ |  |
| `sNodeName` | `String` |  | `""` |
| `akTarget` | `ObjectReference` |  |  |
| `akProjectile` | `Projectile` | ✓ |  |
| `OriginSecondRef` | `ObjectReference` |  |  |

### `LaunchMagicSpell(akCaster, akSpell, sNodeName, akTarget, akProjectile, OriginSecondRef)`

**Flags:** Native Global

- akCaster: the caster.
- akSpell: the spell to cast.
- sNodeName: the name of the skeleton bone node of the akCaster, the spell is launched from. If empty, it will be cast from the origin point of akCaster or OriginSecondRef (only if filled.)
- akTarget: the target the spell is aimed at, for the player.  (might cause issues if none)
- akProjectile: the projectile that's being used.
- OriginSecondRef: an optional second ref to launch the spell from (as proxy). If not None, NodeSource will be taken from this ref, instead of akCaster.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akCaster` | `Actor` | ✓ |  |
| `akSpell` | `Spell` | ✓ |  |
| `sNodeName` | `String` |  | `""` |
| `akTarget` | `ObjectReference` |  |  |
| `akProjectile` | `Projectile` | ✓ |  |
| `OriginSecondRef` | `ObjectReference` |  |  |

### `MoveRefToCrosshairLoc(akActor, markerRef, fDistance, fHeight, UseLeftRightOffsets, isLeft)`

**Flags:** Native Global

- akActor: The actor. This should be the player in almost all cases. (Any other actor might crash, so be careful!)
- markerRef: the ref to move.
- fDistance: the distance to place the marker at in units. (Values between 2000.0-4000.0 are recommended.)
- fHeight: the height offset, from the base of the actor, in units. (Values between 90.0-100.0 are recommended.)
- UseLeftRightOffsets: whether you want to use the offsets for left and right hands. (leave false if you're unsure.)
- isLeft: whether you want to apply the offset value for left hand or not. Will need UseLeftRightOffsets to be true. (ignore if you're unsure.)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `markerRef` | `ObjectReference` | ✓ |  |
| `fDistance` | `Float` | ✓ |  |
| `fHeight` | `Float` | ✓ |  |
| `UseLeftRightOffsets` | `Bool` |  | `false` |
| `isLeft` | `Bool` |  | `false` |

### `SetRefAsNoAIAcquire(akObject, SetNoAIAquire)`

**Flags:** Native Global

- akObject: the objectreference
- SetNoAIAquire: to disable or enable SetNoAIAquire field.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akObject` | `ObjectReference` | ✓ |  |
| `SetNoAIAquire` | `Bool` | ✓ |  |
