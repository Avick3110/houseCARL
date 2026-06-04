# `ObjectReference`

**Source:** `skse` (Skyrim Script Extender x64) • **Extends:** `Form` • **Flags:** Hidden

---

## Properties

### `Motion_BoxIntertia: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `3`

### `Motion_Character: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `7`

### `Motion_Dynamic: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `1`

### `Motion_Fixed: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `5`

### `Motion_Keyframed: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `4`

### `Motion_SphereIntertia: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `2`

### `Motion_ThinBoxIntertia: Int`

**Flags:** Auto

**Accessors:** Get

**Default:** `6`

### `X: Float`

**Flags:** Hidden

### `Y: Float`

**Flags:** Hidden

### `Z: Float`

**Flags:** Hidden

---

## Events

### `OnActivate(akActionRef)`

**Kind:** Event

Event received when this reference is activated

**Parameters**

| Name | Type |
|---|---|
| `akActionRef` | `ObjectReference` |

### `OnAttachedToCell()`

**Kind:** Event

Event received when this object has moved to an attached cell from a detached one

### `OnCellAttach()`

**Kind:** Event

Event received when this object's parent cell is attached

### `OnCellDetach()`

**Kind:** Event

Event received when this object's parent cell is detached

### `OnCellLoad()`

**Kind:** Event

Event received when every object in this object's parent cell is loaded (TODO: Find restrictions)

### `OnClose(akActionRef)`

**Kind:** Event

Event received when this object is closed

**Parameters**

| Name | Type |
|---|---|
| `akActionRef` | `ObjectReference` |

### `OnContainerChanged(akNewContainer, akOldContainer)`

**Kind:** Event

Event received when this object enters, exits, or changes containers

**Parameters**

| Name | Type |
|---|---|
| `akNewContainer` | `ObjectReference` |
| `akOldContainer` | `ObjectReference` |

### `OnDestructionStageChanged(aiOldStage, aiCurrentStage)`

**Kind:** Event

Event received when this reference's destruction stage has changed

**Parameters**

| Name | Type |
|---|---|
| `aiOldStage` | `Int` |
| `aiCurrentStage` | `Int` |

### `OnDetachedFromCell()`

**Kind:** Event

Event recieved when this object moves to a detached cell from an attached one

### `OnEquipped(akActor)`

**Kind:** Event

Event received when this object is equipped by an actor

**Parameters**

| Name | Type |
|---|---|
| `akActor` | `Actor` |

### `OnGrab()`

**Kind:** Event

Event received when this object is grabbed by the player

### `OnHit(akAggressor, akSource, akProjectile, abPowerAttack, abSneakAttack, abBashAttack, abHitBlocked)`

**Kind:** Event

Event received when this object is hit by a source (weapon, spell, explosion) or projectile attack

**Parameters**

| Name | Type |
|---|---|
| `akAggressor` | `ObjectReference` |
| `akSource` | `Form` |
| `akProjectile` | `Projectile` |
| `abPowerAttack` | `Bool` |
| `abSneakAttack` | `Bool` |
| `abBashAttack` | `Bool` |
| `abHitBlocked` | `Bool` |

### `OnItemAdded(akBaseItem, aiItemCount, akItemReference, akSourceContainer)`

**Kind:** Event

Event received when an item is added to this object's inventory. If the item is a persistant reference, akItemReference will
point at it - otherwise the parameter will be None

**Parameters**

| Name | Type |
|---|---|
| `akBaseItem` | `Form` |
| `aiItemCount` | `Int` |
| `akItemReference` | `ObjectReference` |
| `akSourceContainer` | `ObjectReference` |

### `OnItemRemoved(akBaseItem, aiItemCount, akItemReference, akDestContainer)`

**Kind:** Event

Event received when an item is removed from this object's inventory. If the item is a persistant reference, akItemReference
will point at it - otherwise the parameter will be None

**Parameters**

| Name | Type |
|---|---|
| `akBaseItem` | `Form` |
| `aiItemCount` | `Int` |
| `akItemReference` | `ObjectReference` |
| `akDestContainer` | `ObjectReference` |

### `OnLoad()`

**Kind:** Event

Event recieved when this object is completely loaded - will be fired every time this object is loaded

### `OnLockStateChanged()`

**Kind:** Event

Event received when the lock on this object changes

### `OnMagicEffectApply(akCaster, akEffect)`

**Kind:** Event

Event received when a magic affect is being applied to this object

**Parameters**

| Name | Type |
|---|---|
| `akCaster` | `ObjectReference` |
| `akEffect` | `MagicEffect` |

### `OnOpen(akActionRef)`

**Kind:** Event

Event received when this object is opened

**Parameters**

| Name | Type |
|---|---|
| `akActionRef` | `ObjectReference` |

### `OnRead()`

**Kind:** Event

Event received when this object, if a book, is read

### `OnRelease()`

**Kind:** Event

Event received when this object is released by the player

### `OnReset()`

**Kind:** Event

Event received when this reference is reset

### `OnSell(akSeller)`

**Kind:** Event

Event received when this reference is sold by an actor

**Parameters**

| Name | Type |
|---|---|
| `akSeller` | `Actor` |

### `OnSpellCast(akSpell)`

**Kind:** Event

Event received when a spell is cast by this object

**Parameters**

| Name | Type |
|---|---|
| `akSpell` | `Form` |

### `OnTranslationAlmostComplete()`

**Kind:** Event

Event received when translation is almost complete (from a call to TranslateTo, "almost" is determined by a gamesetting, default is 90% of the way)

### `OnTranslationComplete()`

**Kind:** Event

Event received when translation is complete (from a call to TranslateTo)

### `OnTranslationFailed()`

**Kind:** Event

Event received when translation is aborted (from a call to StopTranslateTo)

### `OnTrapHit(akTarget, afXVel, afYVel, afZVel, afXPos, afYPos, afZPos, aeMaterial, abInitialHit, aeMotionType)`

**Kind:** Event

Event recieved when this reference hits a target

**Parameters**

| Name | Type |
|---|---|
| `akTarget` | `ObjectReference` |
| `afXVel` | `Float` |
| `afYVel` | `Float` |
| `afZVel` | `Float` |
| `afXPos` | `Float` |
| `afYPos` | `Float` |
| `afZPos` | `Float` |
| `aeMaterial` | `Int` |
| `abInitialHit` | `Bool` |
| `aeMotionType` | `Int` |

### `OnTrapHitStart(akTarget, afXVel, afYVel, afZVel, afXPos, afYPos, afZPos, aeMaterial, abInitialHit, aeMotionType)`

**Kind:** Event

Event recieved when this starts hitting a target

**Parameters**

| Name | Type |
|---|---|
| `akTarget` | `ObjectReference` |
| `afXVel` | `Float` |
| `afYVel` | `Float` |
| `afZVel` | `Float` |
| `afXPos` | `Float` |
| `afYPos` | `Float` |
| `afZPos` | `Float` |
| `aeMaterial` | `Int` |
| `abInitialHit` | `Bool` |
| `aeMotionType` | `Int` |

### `OnTrapHitStop(akTarget)`

**Kind:** Event

Event recieved when this stops hitting a target

**Parameters**

| Name | Type |
|---|---|
| `akTarget` | `ObjectReference` |

### `OnTrigger(akActionRef)`

**Kind:** Event

Event received when a this trigger is tripped

**Parameters**

| Name | Type |
|---|---|
| `akActionRef` | `ObjectReference` |

### `OnTriggerEnter(akActionRef)`

**Kind:** Event

Event received when this trigger volume is entered

**Parameters**

| Name | Type |
|---|---|
| `akActionRef` | `ObjectReference` |

### `OnTriggerLeave(akActionRef)`

**Kind:** Event

Event received when this trigger volume is left

**Parameters**

| Name | Type |
|---|---|
| `akActionRef` | `ObjectReference` |

### `OnUnequipped(akActor)`

**Kind:** Event

Event received when this object is unequipped by an actor

**Parameters**

| Name | Type |
|---|---|
| `akActor` | `Actor` |

### `OnUnload()`

**Kind:** Event

Event recieved when this object is being unloaded - will be fired every time this object is unloaded

### `OnWardHit(akCaster, akSpell, aiStatus)`

**Kind:** Event

Event received when this object's Ward is hit by a spell

**Parameters**

| Name | Type |
|---|---|
| `akCaster` | `ObjectReference` |
| `akSpell` | `Spell` |
| `aiStatus` | `Int` |

---

## Functions

### `Activate(akActivator, abDefaultProcessingOnly) → Bool`

**Flags:** Native

Have akActivator activate this reference. If abDefaultProcessingOnly is true then any block will be bypassed
and no OnActivate event will be sent. The function returns true if default processing ran, and succeeded. If
default processing has been blocked, will always return false.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActivator` | `ObjectReference` | ✓ |  |
| `abDefaultProcessingOnly` | `Bool` |  | `false` |

### `AddDependentAnimatedObjectReference(akDependent) → Bool`

**Flags:** Native

Sets up a dependent animated object
This function should be used only with a coder supervision.  It is left undocumented because it can cause dangling pointers as well as very broken functionality
for the dependent object if used improperly.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akDependent` | `ObjectReference` | ✓ |  |

### `AddInventoryEventFilter(akFilter)`

**Flags:** Native

Add an inventory event filter to this reference. Item added/removed events matching the
specified form (or in the specified form list) will now be let through.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFilter` | `Form` | ✓ |  |

### `AddItem(akItemToAdd, aiCount, abSilent)`

**Flags:** Native

Adds the specified base object or object reference to this object reference's container/inventory
Note that you cannot add more then one copy of a reference to a container (a warning will be printed if you try)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akItemToAdd` | `Form` | ✓ |  |
| `aiCount` | `Int` |  | `1` |
| `abSilent` | `Bool` |  | `false` |

### `AddKeyIfNeeded(ObjectWithNeededKey)`

Should only be called by ObjectReferences that have/are containers (ie Containers and Actors). Checks to see if self has the key to ObjectWithNeededKey, and if not, creates a copy of the key and puts it in self.

jduvall

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjectWithNeededKey` | `ObjectReference` | ✓ |  |

### `AddToMap(abAllowFastTravel)`

**Flags:** Native

Adds this reference (which is a map marker) to the map, optionally making it available for fast travel

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abAllowFastTravel` | `Bool` |  | `false` |

### `ApplyHavokImpulse(afX, afY, afZ, afMagnitude)`

**Flags:** Native

Apply an impulse to this reference

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afX` | `Float` | ✓ |  |
| `afY` | `Float` | ✓ |  |
| `afZ` | `Float` | ✓ |  |
| `afMagnitude` | `Float` | ✓ |  |

### `BlockActivation(abBlocked)`

**Flags:** Native

Turns on and off blocking of normal activation - OnActivate events will still be sent

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abBlocked` | `Bool` |  | `true` |

### `CalculateEncounterLevel(aiDifficulty) → Int`

**Flags:** Native

Calculate's this references encounter level based on the requested difficulty level
0 - Easy
1 - Medium
2 - Hard
3 - Very Hard
4 - None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiDifficulty` | `Int` |  | `4` |

### `CanFastTravelToMarker() → Bool`

**Flags:** Native

Can the map marker be fast traveled to?

### `ClearDestruction()`

**Flags:** Native

Clears all effects of destruction from this object

### `countLinkedRefChain(apKeyword, maxExpectedLinkedRefs) → Int`

jtucker, jduvall
This function  counts the number of linked refs that are in a linked Ref chain (ie object is linked to A, A is linked to B, etc. this then counts all the linked refs.)
Often used in conjunction with GetNthLinkedRef()
*** WARNING: Having a link ref chain that at any point loops back on itself and calling this function will result in very bad things. Don't do that!***

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `apKeyword` | `keyword` |  |  |
| `maxExpectedLinkedRefs` | `Int` |  | `100` |

### `CreateDetectionEvent(akOwner, aiSoundLevel)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akOwner` | `Actor` | ✓ |  |
| `aiSoundLevel` | `Int` |  | `0` |

### `CreateEnchantment(maxCharge, effects, magnitudes, areas, durations)`

**Flags:** Native

Creates a new enchantment on the item given the specified parameters
all arrays must be the same size
created enchantments are not purged from the save when removed or overwritten
exact same enchantments are re-used by the game

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `maxCharge` | `Float` | ✓ |  |
| `effects` | `MagicEffect[]` | ✓ |  |
| `magnitudes` | `Float[]` | ✓ |  |
| `areas` | `Int[]` | ✓ |  |
| `durations` | `Int[]` | ✓ |  |

### `DamageObject(afDamage)`

**Flags:** Native

Damages this object and advances the destruction stage - does not return until the object is damaged

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afDamage` | `Float` | ✓ |  |

### `Delete()`

**Flags:** Native

Delets this object

### `DeleteWhenAble()`

This will become a native function... it will wait until the object is not persisting, then delete itself.

jduvall

### `Disable(abFadeOut)`

**Flags:** Native

Disables this object - fading out if requested

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abFadeOut` | `Bool` |  | `false` |

### `DisableLinkChain(apKeyword, abFadeOut)`

Disables all of the references that are linked, in a chain, to this one.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `apKeyword` | `Keyword` |  |  |
| `abFadeOut` | `Bool` |  | `false` |

### `DisableNoWait(abFadeOut)`

**Flags:** Native

Disables this object - fading out if requested. Does NOT wait for the fade or disable to finish

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abFadeOut` | `Bool` |  | `false` |

### `DropObject(akObject, aiCount) → ObjectReference`

**Flags:** Native

Drops the specified object from this object's inventory

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akObject` | `Form` | ✓ |  |
| `aiCount` | `Int` |  | `1` |

### `Enable(abFadeIn)`

**Flags:** Native

Enables this object - fading in if requested

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abFadeIn` | `Bool` |  | `false` |

### `EnableFastTravel(abEnable)`

**Flags:** Native

Enables the ability to fast travel to this marker - or disables it. Note that if you disable
fast travel the player will see "You haven't discovered this location" as an error message

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abEnable` | `Bool` |  | `true` |

### `EnableLinkChain(apKeyword)`

Enables all of the references that are linked, in a chain, to this one.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `apKeyword` | `Keyword` |  |  |

### `EnableNoWait(abFadeIn)`

**Flags:** Native

Enables this object - fading in if requested. Does NOT wait for the fade or enable to finish

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abFadeIn` | `Bool` |  | `false` |

### `ForceAddRagdollToWorld()`

**Flags:** Native

Forcibly adds / removes the ragdoll for a reference to the world

### `ForceRemoveRagdollFromWorld()`

**Flags:** Native

### `GetActorOwner() → ActorBase`

**Flags:** Native

Gets the actor that owns this object (or None if not owned by an Actor)

### `GetAllForms(toFill)`

**Flags:** Native

Returns all base forms in the inventory/container into the specified FormList

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `toFill` | `FormList` | ✓ |  |

### `GetAllItemsCount() → Int`

**Flags:** Native

### `GetAngleX() → Float`

**Flags:** Native

Get the current X angle of this object

### `GetAngleY() → Float`

**Flags:** Native

Get the current Y angle of this object

### `GetAngleZ() → Float`

**Flags:** Native

Get the current Z angle of this object

### `GetAnimationVariableBool(arVariableName) → Bool`

**Flags:** Native

Get a variable from the reference's animation graph (if applicable). Bool version.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arVariableName` | `String` | ✓ |  |

### `GetAnimationVariableFloat(arVariableName) → Float`

**Flags:** Native

Get a variable from the reference's animation graph (if applicable). Float version.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arVariableName` | `String` | ✓ |  |

### `GetAnimationVariableInt(arVariableName) → Int`

**Flags:** Native

Get a variable from the reference's animation graph (if applicable). Int version.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arVariableName` | `String` | ✓ |  |

### `GetBaseObject() → Form`

**Flags:** Native

Returns the base object this reference represents

### `GetContainerForms() → Form[]`

**Flags:** Native

Returns all base forms from the container into a new array

### `GetCurrentDestructionStage() → Int`

**Flags:** Native

Returns the object's current destruction stage

### `GetCurrentLocation() → Location`

**Flags:** Native

Returns this reference's current location

### `GetCurrentScene() → Scene`

**Flags:** Native

Returns the scene this reference is currently in - if any

### `GetDisplayName() → String`

**Flags:** Native

Returns the name of this reference
this is the name that is displayed

### `GetDistance(akOther) → Float`

**Flags:** Native

Calculates the distance between this reference and another - both must either be in the same interior, or same worldspace

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akOther` | `ObjectReference` | ✓ |  |

### `GetEditorLocation() → Location`

**Flags:** Native

Returns this reference's editor location

### `GetEnableParent() → ObjectReference`

**Flags:** Native

Returns the enable parent object

### `GetEnchantment() → Enchantment`

**Flags:** Native

Returns the player-made enchantment if there is one

### `GetFactionOwner() → Faction`

**Flags:** Native

Gets the faction that owns this object (or None if not owned by a Faction)

### `GetHeadingAngle(akOther) → Float`

**Flags:** Native

Gets the angle between this object's heading and the other object in degrees - in the range from -180 to 180

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akOther` | `ObjectReference` | ✓ |  |

### `GetHeight() → Float`

**Flags:** Native

Get the current height of the object

### `GetItemCharge() → Float`

**Flags:** Native

### `GetItemCount(akItem) → Int`

**Flags:** Native

Returns how many of the specified item is in this object reference's inventory

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akItem` | `Form` | ✓ |  |

### `GetItemHealthPercent() → Float`

**Flags:** Native

Returns the smithed health of this object reference (1.0 == 100%)

### `GetItemMaxCharge() → Float`

**Flags:** Native

Works on any enchanted item

### `GetKey() → Key`

**Flags:** Native

Returns the key base object that will unlock this object

### `GetLength() → Float`

**Flags:** Native

Get the current length of the object

### `GetLinkedRef(apKeyword) → ObjectReference`

**Flags:** Native

Get our linked reference

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `apKeyword` | `Keyword` |  |  |

### `GetLockLevel() → Int`

**Flags:** Native

Get the level of the lock on this object

### `GetMass() → Float`

**Flags:** Native

Get this object's mass

### `GetNthForm(index) → Form`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |

### `GetNthLinkedRef(aiLinkedRef) → ObjectReference`

**Flags:** Native

Returns the Nth linked ref from this reference (0 = self, 1 = GetLinkedRef, 2 = GetLinkedRef.GetLinkedRef, etc)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiLinkedRef` | `Int` | ✓ |  |

### `GetNthReferenceAlias(n) → ReferenceAlias`

**Flags:** Native

Returns the nth ReferenceAlias holding this reference

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `n` | `Int` | ✓ |  |

### `GetNumItems() → Int`

**Flags:** Native

Container-only functions

### `GetNumReferenceAliases() → Int`

**Flags:** Native

Returns the number of ref aliases holding this reference

### `GetOpenState() → Int`

**Flags:** Native

Gets the open state of this object. Which can be one of the following:
0 - None
1 - Open
2 - Opening
3 - Closed
4 - Closing

### `GetParentCell() → Cell`

**Flags:** Native

Gets the cell this object is in

### `GetPoison() → Potion`

**Flags:** Native

Returns the poison applied to the weapon

### `GetPositionX() → Float`

**Flags:** Native

Get the current X position of the object

### `GetPositionY() → Float`

**Flags:** Native

Get the current Y position of the object

### `GetPositionZ() → Float`

**Flags:** Native

Get the current Z position of the object

### `GetReferenceAliases() → ReferenceAlias[]`

**Flags:** Native

Returns all of the aliases holding this reference

### `GetScale() → Float`

**Flags:** Native

Get the current scale of the object

### `GetSelfAsActor() → actor`

Returns self cast as an actor

### `GetTotalArmorWeight() → Float`

**Flags:** Native

### `GetTotalItemWeight() → Float`

**Flags:** Native

### `GetTriggerObjectCount() → Int`

**Flags:** Native

Get the number of objects inside this trigger (throws warning if not a triggger)

### `GetVoiceType() → VoiceType`

**Flags:** Native

Gets the voice type for this reference. Will return None if not an actor or a talking activator

### `GetWidth() → Float`

**Flags:** Native

Get the current width of the object

### `GetWorldSpace() → WorldSpace`

**Flags:** Native

Get this objects worldspace

### `HasEffectKeyword(akKeyword) → Bool`

**Flags:** Native

Returns if this reference has an active effect coming from a magic effect with the specified keyword attached

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akKeyword` | `Keyword` | ✓ |  |

### `HasNode(asNodeName) → Bool`

**Flags:** Native

Returns whether the reference has the given node

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asNodeName` | `String` | ✓ |  |

### `HasRefType(akRefType) → Bool`

**Flags:** Native

Returns if this reference has the specified location ref type

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRefType` | `LocationRefType` | ✓ |  |

### `IgnoreFriendlyHits(abIgnore)`

**Flags:** Native

Flags this reference as ignoring (or not ignoring) friendly hits

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abIgnore` | `Bool` |  | `true` |

### `InterruptCast()`

**Flags:** Native

Interrupts any spell-casting this object may be doing

### `Is3DLoaded() → Bool`

**Flags:** Native

Returns if the 3d for this object is loaded or not

### `IsActivateChild(akChild) → Bool`

**Flags:** Native

Checks to see if the passed in reference is the activate child of this one

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akChild` | `ObjectReference` | ✓ |  |

### `IsActivationBlocked() → Bool`

**Flags:** Native

Checks to see if activation is currently blocked on this object

### `IsContainerEmpty() → Bool`

**Flags:** Native

added in 1.6.1126

### `IsDeleted() → Bool`

**Flags:** Native

Is this object currently flagged for delete?

### `IsDisabled() → Bool`

**Flags:** Native

Is this object currently disabled?

### `IsEnabled() → Bool`

Because Shane got tired of remembering which way to call this

### `IsFurnitureInUse(abIgnoreReserved) → Bool`

**Flags:** Native

Is any marker on this furniture in use?

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abIgnoreReserved` | `Bool` |  | `false` |

### `IsFurnitureMarkerInUse(aiMarker, abIgnoreReserved) → Bool`

**Flags:** Native

Is a particular marker on this furniture in use?

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiMarker` | `Int` | ✓ |  |
| `abIgnoreReserved` | `Bool` |  | `false` |

### `IsHarvested() → Bool`

**Flags:** Native

Tree and Flora only functions

### `IsIgnoringFriendlyHits() → Bool`

**Flags:** Native

Is this object ignoring friendly hits?

### `IsInDialogueWithPlayer() → Bool`

**Flags:** Native

Is this actor or talking activator currently talking to the player?

### `IsInInterior() → Bool`

Returns !IsInExterior()

jduvall

### `IsInLocation(akLocation) → Bool`

Convenience function to check if I'm in a location or any of its children

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akLocation` | `Location` | ✓ |  |

### `IsLockBroken() → Bool`

**Flags:** Native

Is the lock on this object broken?

### `IsLocked() → Bool`

**Flags:** Native

Is the lock on this object locked?

### `IsMapMarkerVisible() → Bool`

**Flags:** Native

Is the map marker visible?

### `IsNearPlayer() → Bool`

Function to know if I'm near the player (whether I can be safely enabled or disabled)

### `IsOffLimits() → Bool`

**Flags:** Native

### `KnockAreaEffect(afMagnitude, afRadius)`

**Flags:** Native

Executes a knock effect to an area

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afMagnitude` | `Float` | ✓ |  |
| `afRadius` | `Float` | ✓ |  |

### `Lock(abLock, abAsOwner)`

**Flags:** Native

Lock/unlock this object. If told to lock it, it will add a lock if it doesn't have one. If locked/unlocked as the owner on a door,
the adjoining cell will be made public/private as appropriate

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abLock` | `Bool` |  | `true` |
| `abAsOwner` | `Bool` |  | `false` |

### `MoveTo(akTarget, afXOffset, afYOffset, afZOffset, abMatchRotation)`

**Flags:** Native

Moves this object to the position of the specified object, with an offset, and optionally matching its rotation

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akTarget` | `ObjectReference` | ✓ |  |
| `afXOffset` | `Float` |  | `0` |
| `afYOffset` | `Float` |  | `0` |
| `afZOffset` | `Float` |  | `0` |
| `abMatchRotation` | `Bool` |  | `true` |

### `MoveToIfUnloaded(akTarget, afXOffset, afYOffset, afZOffset) → Bool`

Calls MoveTo if the calling ObjectReference is currently unloaded. Doesn't do anything if it IS loaded. No waiting or while loops. Returns true if it does the moveto

kkuhlmann:

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akTarget` | `ObjectReference` | ✓ |  |
| `afXOffset` | `Float` |  | `0` |
| `afYOffset` | `Float` |  | `0` |
| `afZOffset` | `Float` |  | `0` |

### `MoveToInteractionLocation(akTarget)`

**Flags:** Native

Moves this object to the position (and rotation) of the specified object's interaction position

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akTarget` | `ObjectReference` | ✓ |  |

### `MoveToMyEditorLocation()`

**Flags:** Native

Moves this object to its editor location

### `MoveToNode(akTarget, asNodeName)`

**Flags:** Native

Moves this object to the position (and rotation) of the specified node on the specified object's 3D

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akTarget` | `ObjectReference` | ✓ |  |
| `asNodeName` | `String` | ✓ |  |

### `MoveToWhenUnloaded(akTarget, afXOffset, afYOffset, afZOffset)`

DEPRECATED: DO NOT USE. Calls MoveTo if both the calling ObjectReference and the akTarget ObjectReference have current locations that are not loaded.

jduvall:

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akTarget` | `ObjectReference` | ✓ |  |
| `afXOffset` | `Float` |  | `0` |
| `afYOffset` | `Float` |  | `0` |
| `afZOffset` | `Float` |  | `0` |

### `PlaceActorAtMe(akActorToPlace, aiLevelMod, akZone) → Actor`

**Flags:** Native

Create an actor at this object's location. Level mod is one of the following:
0 - Easy
1 - Medium
2 - Hard
3 - Boss
4 - None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActorToPlace` | `ActorBase` | ✓ |  |
| `aiLevelMod` | `Int` |  | `4` |
| `akZone` | `EncounterZone` |  |  |

### `PlaceAtMe(akFormToPlace, aiCount, abForcePersist, abInitiallyDisabled) → ObjectReference`

**Flags:** Native

Create x copies of the passed in form (forcing them to persist if desired) and place them at our location, returning the last object created

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFormToPlace` | `Form` | ✓ |  |
| `aiCount` | `Int` |  | `1` |
| `abForcePersist` | `Bool` |  | `false` |
| `abInitiallyDisabled` | `Bool` |  | `false` |

### `PlayAnimation(asAnimation) → Bool`

**Flags:** Native

Start the specified animation playing - returns true if it succeeds

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asAnimation` | `String` | ✓ |  |

### `PlayAnimationAndWait(asAnimation, asEventName) → Bool`

**Flags:** Native

Start the specified animation playing and wait for the specified event - returns true if succeeds

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asAnimation` | `String` | ✓ |  |
| `asEventName` | `String` | ✓ |  |

### `PlayGamebryoAnimation(asAnimation, abStartOver, afEaseInTime) → Bool`

**Flags:** Native

Start the specified Gamebryo animation playing - returns true if it succeeds

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asAnimation` | `String` | ✓ |  |
| `abStartOver` | `Bool` |  | `false` |
| `afEaseInTime` | `Float` |  | `0` |

### `PlayImpactEffect(akImpactEffect, asNodeName, afPickDirX, afPickDirY, afPickDirZ, afPickLength, abApplyNodeRotation, abUseNodeLocalRotation) → Bool`

**Flags:** Native

Play the specified impact effect - returns true if it succeeds

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akImpactEffect` | `ImpactDataSet` | ✓ |  |
| `asNodeName` | `String` |  | `""` |
| `afPickDirX` | `Float` |  | `0` |
| `afPickDirY` | `Float` |  | `0` |
| `afPickDirZ` | `Float` |  | `-1` |
| `afPickLength` | `Float` |  | `512` |
| `abApplyNodeRotation` | `Bool` |  | `false` |
| `abUseNodeLocalRotation` | `Bool` |  | `false` |

### `PlaySyncedAnimationAndWaitSS(asAnimation1, asEvent1, akObj2, asAnimation2, asEvent2) → Bool`

**Flags:** Native

Play two animations at once - one on this object, one on another object - and wait for both

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asAnimation1` | `String` | ✓ |  |
| `asEvent1` | `String` | ✓ |  |
| `akObj2` | `ObjectReference` | ✓ |  |
| `asAnimation2` | `String` | ✓ |  |
| `asEvent2` | `String` | ✓ |  |

### `PlaySyncedAnimationSS(asAnimation1, akObj2, asAnimation2) → Bool`

**Flags:** Native

Play two animations at once - one on this object, one on another object

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asAnimation1` | `String` | ✓ |  |
| `akObj2` | `ObjectReference` | ✓ |  |
| `asAnimation2` | `String` | ✓ |  |

### `PlayTerrainEffect(asEffectModelName, asAttachBoneName)`

**Flags:** Native

Play a terrain effect that is attached to the specified bone of this object.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asEffectModelName` | `String` | ✓ |  |
| `asAttachBoneName` | `String` | ✓ |  |

### `ProcessTrapHit(akTrap, afDamage, afPushback, afXVel, afYVel, afZVel, afXPos, afYPos, afZPos, aeMaterial, afStagger)`

**Flags:** Native

Tells this object to process a trap hitting it

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akTrap` | `ObjectReference` | ✓ |  |
| `afDamage` | `Float` | ✓ |  |
| `afPushback` | `Float` | ✓ |  |
| `afXVel` | `Float` | ✓ |  |
| `afYVel` | `Float` | ✓ |  |
| `afZVel` | `Float` | ✓ |  |
| `afXPos` | `Float` | ✓ |  |
| `afYPos` | `Float` | ✓ |  |
| `afZPos` | `Float` | ✓ |  |
| `aeMaterial` | `Int` | ✓ |  |
| `afStagger` | `Float` | ✓ |  |

### `PushActorAway(akActorToPush, aiKnockbackForce)`

**Flags:** Native

Pushes the passed-in actor away from this object, using the passed in knockback force to determine the speed

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActorToPush` | `Actor` | ✓ |  |
| `aiKnockbackForce` | `Float` | ✓ |  |

### `rampRumble(power, duration, falloff) → Bool`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `power` | `Float` |  | `0.5` |
| `duration` | `Float` |  | `0.25` |
| `falloff` | `Float` |  | `1600` |

### `RemoveAllInventoryEventFilters()`

**Flags:** Native

Remove all inventory event filters from this reference - all item added/removed events will now be received

### `RemoveAllItems(akTransferTo, abKeepOwnership, abRemoveQuestItems)`

**Flags:** Native

Removes all items from this container, transferring it to the other object if passed

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akTransferTo` | `ObjectReference` |  |  |
| `abKeepOwnership` | `Bool` |  | `false` |
| `abRemoveQuestItems` | `Bool` |  | `false` |

### `RemoveAllStolenItems(akTransferTo)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akTransferTo` | `ObjectReference` | ✓ |  |

### `RemoveDependentAnimatedObjectReference(akDependent) → Bool`

**Flags:** Native

Removes a previously added dependent object
This function should be used only with a coder supervision.  It is left undocumented because it can cause dangling pointers as well as very broken functionality
for the dependent object if used improperly.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akDependent` | `ObjectReference` | ✓ |  |

### `RemoveInventoryEventFilter(akFilter)`

**Flags:** Native

Remove an inventory event filter from this reference. Item added/removed events matching the
specified form (or in the specified form list) will no longer be let through.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFilter` | `Form` | ✓ |  |

### `RemoveItem(akItemToRemove, aiCount, abSilent, akOtherContainer)`

**Flags:** Native

Removes the specified item from this object reference's inventory

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akItemToRemove` | `Form` | ✓ |  |
| `aiCount` | `Int` |  | `1` |
| `abSilent` | `Bool` |  | `false` |
| `akOtherContainer` | `ObjectReference` |  |  |

### `Reset(akTarget)`

**Flags:** Native

Resets this object, optional place the object at the new target

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akTarget` | `ObjectReference` |  |  |

### `ResetInventory()`

**Flags:** Native

### `Say(akTopicToSay, akActorToSpeakAs, abSpeakInPlayersHead)`

**Flags:** Native

Has this object "say" the specified topic, as if spoken by the specified actor (if one is
provided, and potentially "speaking" in the player's head.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akTopicToSay` | `Topic` | ✓ |  |
| `akActorToSpeakAs` | `Actor` |  |  |
| `abSpeakInPlayersHead` | `Bool` |  | `false` |

### `SendStealAlarm(akThief)`

**Flags:** Native

Has this object behave as if the specified actor attempted to steal it

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akThief` | `Actor` | ✓ |  |

### `SetActorCause(akActor)`

**Flags:** Native

Sets this object's actor cause to the specified actor

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `SetActorOwner(akActorBase)`

**Flags:** Native

Sets this object's owner to the specified actor base - None means to remove ownership

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActorBase` | `ActorBase` | ✓ |  |

### `SetAngle(afXAngle, afYAngle, afZAngle)`

**Flags:** Native

Set the orientation of the object (angles are in degrees)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afXAngle` | `Float` | ✓ |  |
| `afYAngle` | `Float` | ✓ |  |
| `afZAngle` | `Float` | ✓ |  |

### `SetAnimationVariableBool(arVariableName, abNewValue)`

**Flags:** Native

Set a variable on the reference's animation graph (if applicable). Bool version.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arVariableName` | `String` | ✓ |  |
| `abNewValue` | `Bool` | ✓ |  |

### `SetAnimationVariableFloat(arVariableName, afNewValue)`

**Flags:** Native

Set a variable on the reference's animation graph (if applicable). Float version.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arVariableName` | `String` | ✓ |  |
| `afNewValue` | `Float` | ✓ |  |

### `SetAnimationVariableInt(arVariableName, aiNewValue)`

**Flags:** Native

Set a variable on the reference's animation graph (if applicable). Int version.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arVariableName` | `String` | ✓ |  |
| `aiNewValue` | `Int` | ✓ |  |

### `SetContainerAllowStolenItems(setAllowStolenItems)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `setAllowStolenItems` | `Bool` | ✓ |  |

### `SetDestroyed(abDestroyed)`

**Flags:** Native

Sets this object as destroyed or not

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abDestroyed` | `Bool` |  | `true` |

### `SetDisplayName(name, force) → Bool`

**Flags:** Native

Sets a reference's display name
returns false if force is false and the reference
is held by an alias using 'Stored Text' or 'Uses Stored Text'
Text Replacement does not use this name and may be lost if forced

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `name` | `String` | ✓ |  |
| `force` | `Bool` |  | `false` |

### `SetEnchantment(source, maxCharge)`

**Flags:** Native

Changes an item's player-made enchantment to something else
None enchantment will remove the existing enchantment
does not delete the custom enchantment, only removes it

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `source` | `Enchantment` | ✓ |  |
| `maxCharge` | `Float` | ✓ |  |

### `SetFactionOwner(akFaction)`

**Flags:** Native

Sets this object's owner to the specified faction

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akFaction` | `Faction` | ✓ |  |

### `SetHarvested(harvested)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `harvested` | `Bool` | ✓ |  |

### `SetItemCharge(charge)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `charge` | `Float` | ✓ |  |

### `SetItemHealthPercent(health)`

**Flags:** Native

Tempering

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `health` | `Float` | ✓ |  |

### `SetItemMaxCharge(maxCharge)`

**Flags:** Native

Only works on ObjectReferences that have user-enchants

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `maxCharge` | `Float` | ✓ |  |

### `SetLockLevel(aiLockLevel)`

**Flags:** Native

Sets the lock level on this object. Will add an unlocked lock to it if it doesn't have one

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiLockLevel` | `Int` | ✓ |  |

### `SetMotionType(aeMotionType, abAllowActivate)`

**Flags:** Native

Sets the motion type of the reference
aeMotionType: The type of motion (see properties at end of file)
abAllowActivate: When setting to a dynamic type, allows the simulation to be activated

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aeMotionType` | `Int` | ✓ |  |
| `abAllowActivate` | `Bool` |  | `true` |

### `SetNoFavorAllowed(abNoFavor)`

**Flags:** Native

Sets this object reference as one that teammates will refuse to do favors on

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abNoFavor` | `Bool` |  | `true` |

### `SetOpen(abOpen)`

**Flags:** Native

Opens/closes this object

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abOpen` | `Bool` |  | `true` |

### `SetPosition(afX, afY, afZ)`

**Flags:** Native

Set the position of the object

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afX` | `Float` | ✓ |  |
| `afY` | `Float` | ✓ |  |
| `afZ` | `Float` | ✓ |  |

### `SetScale(afScale)`

**Flags:** Native

Set the current scale of the object

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afScale` | `Float` | ✓ |  |

### `SplineTranslateTo(afX, afY, afZ, afXAngle, afYAngle, afZAngle, afTangentMagnitude, afSpeed, afMaxRotationSpeed)`

**Flags:** Native

Makes the reference translate to the given position/orientation on a spline

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afX` | `Float` | ✓ |  |
| `afY` | `Float` | ✓ |  |
| `afZ` | `Float` | ✓ |  |
| `afXAngle` | `Float` | ✓ |  |
| `afYAngle` | `Float` | ✓ |  |
| `afZAngle` | `Float` | ✓ |  |
| `afTangentMagnitude` | `Float` | ✓ |  |
| `afSpeed` | `Float` | ✓ |  |
| `afMaxRotationSpeed` | `Float` |  | `0` |

### `SplineTranslateToRef(arTarget, afTangentMagnitude, afSpeed, afMaxRotationSpeed)`

Makes the reference translate to the target ref position/orient on a spline at the given speed

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arTarget` | `ObjectReference` | ✓ |  |
| `afTangentMagnitude` | `Float` | ✓ |  |
| `afSpeed` | `Float` | ✓ |  |
| `afMaxRotationSpeed` | `Float` |  | `0` |

### `SplineTranslateToRefNode(arTarget, arNodeName, afTangentMagnitude, afSpeed, afMaxRotationSpeed)`

**Flags:** Native

Makes the reference translate to the target node's ref/orient on a spline at the given speed

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arTarget` | `ObjectReference` | ✓ |  |
| `arNodeName` | `String` | ✓ |  |
| `afTangentMagnitude` | `Float` | ✓ |  |
| `afSpeed` | `Float` | ✓ |  |
| `afMaxRotationSpeed` | `Float` |  | `0` |

### `StopTranslation()`

**Flags:** Native

Stops the reference from moving

### `TetherToHorse(akHorse)`

**Flags:** Native

Tether a prisoner cart to the given horse.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akHorse` | `ObjectReference` | ✓ |  |

### `TranslateTo(afX, afY, afZ, afXAngle, afYAngle, afZAngle, afSpeed, afMaxRotationSpeed)`

**Flags:** Native

Makes the reference translate to the given position/orientation
Note: Rotation speed is entirely dependent on the length of the path and the movement speed
that is, the rotation will happen such that the reference reaches the goal orientation at the end
of the translation.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afX` | `Float` | ✓ |  |
| `afY` | `Float` | ✓ |  |
| `afZ` | `Float` | ✓ |  |
| `afXAngle` | `Float` | ✓ |  |
| `afYAngle` | `Float` | ✓ |  |
| `afZAngle` | `Float` | ✓ |  |
| `afSpeed` | `Float` | ✓ |  |
| `afMaxRotationSpeed` | `Float` |  | `0` |

### `TranslateToRef(arTarget, afSpeed, afMaxRotationSpeed)`

Makes the reference translate to the target ref position/orient at the given speed

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arTarget` | `ObjectReference` | ✓ |  |
| `afSpeed` | `Float` | ✓ |  |
| `afMaxRotationSpeed` | `Float` |  | `0` |

### `WaitForAnimationEvent(asEventName) → Bool`

**Flags:** Native

Waits for the animation graph to send the specified event

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asEventName` | `String` | ✓ |  |
