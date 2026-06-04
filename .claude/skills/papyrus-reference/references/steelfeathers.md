# `SteelfeathersPapyrusExtensions`

**Source:** `steelfeathers` (Steelfeathers' Papyrus Extensions) • **Flags:** Hidden

---

## Global Functions

### `GetFormActorOwner(inventoryObjRef, item) → actorBase`

**Flags:** Native Global

Get the actorBase owner of the passed-in form, if it has one.
InventoryObjRef is the actor or container currently holding the item you want to evaluate.
Best used within OnItemAdded() events; you don't need this for objects you pick up from the world, they already have an ObjectReference you can call GetActorOwner() on normally

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `inventoryObjRef` | `ObjectReference` | ✓ |  |
| `item` | `Form` | ✓ |  |

### `GetFormFactionOwner(inventoryObjRef, item) → faction`

**Flags:** Native Global

Same as GetFormActorOwner(), but returns the faction owner of this item, if it has one.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `inventoryObjRef` | `ObjectReference` | ✓ |  |
| `item` | `Form` | ✓ |  |

### `GetTotalGoldValue(item) → Int`

**Flags:** Native Global

Gets the total gold value you see for an item in your inventory, not just the base value. Allows you to fetch the actual value of enchanted items.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `item` | `Form` | ✓ |  |

### `GetVersion() → Int[]`

**Flags:** Native Global

Returns the SKSE plugin's version as an array of 3 ints. Use to verify the plugin is installed and working.
Version 1.0.0 becomes [1,0,0]

### `IsFormStolen(inventoryObjRef, item) → Bool`

**Flags:** Native Global

Returns whether this form has been stolen.
InventoryObjRef is the actor or container currently holding the item you want to evaluate.
Best used within OnItemAdded() events; you don't need this for objects you pick up from the world, they already have an ObjectReference you can call GetActorOwner() on normally

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `inventoryObjRef` | `ObjectReference` | ✓ |  |
| `item` | `Form` | ✓ |  |

### `OpenInventoryEx(target, type) → Bool`

**Flags:** Native Global

Open the target actor's inventory in one of the following states:
0 = regular item transfer, equivalent to OpenInventory(false), except that it actually works to open the inventory of a non-teammate
1 = stealing from a container or coprse
2 = pickpocketing
3 = item transfer between teammates, equivalent to OpenInventory(true)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `target` | `Actor` | ✓ |  |
| `type` | `Int` | ✓ |  |

### `SetFormActorOwner(inventoryObjRef, item, owner) → Bool`

**Flags:** Native Global

Change the actorBase that owns this form item; useful for laundering stolen items as marking them as belonging to you.
InventoryObjRef is the actor or container currently holding the item you want to evaluate.
Best used within OnItemAdded() events; you don't need this for objects you pick up from the world, they already have an ObjectReference you can call GetActorOwner() on normally

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `inventoryObjRef` | `ObjectReference` | ✓ |  |
| `item` | `Form` | ✓ |  |
| `owner` | `ActorBase` | ✓ |  |

### `SetFormFactionOwner(inventoryObjRef, item, owner) → Bool`

**Flags:** Native Global

Same as SetFormActorOwner(), but sets the owner to the passed-in faction instead.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `inventoryObjRef` | `ObjectReference` | ✓ |  |
| `item` | `Form` | ✓ |  |
| `owner` | `Faction` | ✓ |  |
