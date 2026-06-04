# `NetImmerse`

**Source:** `skse` (Skyrim Script Extender x64) • **Flags:** Hidden

---

## Global Functions

### `GetNodeLocalPosition(ref, node, in, firstPerson) → Bool`

**Flags:** Native Global

returns the node's local position into the specify array, must be size of 3

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `node` | `String` | ✓ |  |
| `in` | `Float[]` | ✓ |  |
| `firstPerson` | `Bool` | ✓ |  |

### `GetNodeLocalPositionX(ref, node, firstPerson) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `node` | `String` | ✓ |  |
| `firstPerson` | `Bool` | ✓ |  |

### `GetNodeLocalPositionY(ref, node, firstPerson) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `node` | `String` | ✓ |  |
| `firstPerson` | `Bool` | ✓ |  |

### `GetNodeLocalPositionZ(ref, node, firstPerson) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `node` | `String` | ✓ |  |
| `firstPerson` | `Bool` | ✓ |  |

### `GetNodeLocalRotationEuler(ref, node, in, firstPerson) → Bool`

**Flags:** Native Global

returns the euler rotation of the node into the specified array, must be size of 3

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `node` | `String` | ✓ |  |
| `in` | `Float[]` | ✓ |  |
| `firstPerson` | `Bool` | ✓ |  |

### `GetNodeLocalRotationMatrix(ref, node, in, firstPerson) → Bool`

**Flags:** Native Global

returns the matrix rotation of the node into the specified array, must be size of 9

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `node` | `String` | ✓ |  |
| `in` | `Float[]` | ✓ |  |
| `firstPerson` | `Bool` | ✓ |  |

### `GetNodePositionX(ref, node, firstPerson) → Float`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `node` | `String` | ✓ |  |
| `firstPerson` | `Bool` | ✓ |  |

### `GetNodePositionY(ref, node, firstPerson) → Float`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `node` | `String` | ✓ |  |
| `firstPerson` | `Bool` | ✓ |  |

### `GetNodePositionZ(ref, node, firstPerson) → Float`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `node` | `String` | ✓ |  |
| `firstPerson` | `Bool` | ✓ |  |

### `GetNodeScale(ref, node, firstPerson) → Float`

**Flags:** Native Global

Sets the scale of a particular Nif node

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `node` | `String` | ✓ |  |
| `firstPerson` | `Bool` | ✓ |  |

### `GetNodeWorldPosition(ref, node, in, firstPerson) → Bool`

**Flags:** Native Global

returns the node's world position into the specify array, must be size of 3

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `node` | `String` | ✓ |  |
| `in` | `Float[]` | ✓ |  |
| `firstPerson` | `Bool` | ✓ |  |

### `GetNodeWorldPositionX(ref, node, firstPerson) → Float`

**Flags:** Native Global

NiNode Manipulation

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `node` | `String` | ✓ |  |
| `firstPerson` | `Bool` | ✓ |  |

### `GetNodeWorldPositionY(ref, node, firstPerson) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `node` | `String` | ✓ |  |
| `firstPerson` | `Bool` | ✓ |  |

### `GetNodeWorldPositionZ(ref, node, firstPerson) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `node` | `String` | ✓ |  |
| `firstPerson` | `Bool` | ✓ |  |

### `GetNodeWorldRotationEuler(ref, node, in, firstPerson) → Bool`

**Flags:** Native Global

Euler Rotation in DEGREES (heading, attitude, bank)
returns the euler rotation of the node into the specified array, must be size of 3

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `node` | `String` | ✓ |  |
| `in` | `Float[]` | ✓ |  |
| `firstPerson` | `Bool` | ✓ |  |

### `GetNodeWorldRotationMatrix(ref, node, in, firstPerson) → Bool`

**Flags:** Native Global

Matrix Rotation in RADIANS
returns the matrix rotation of the node into the specified array, must be size of 9

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `node` | `String` | ✓ |  |
| `in` | `Float[]` | ✓ |  |
| `firstPerson` | `Bool` | ✓ |  |

### `GetRelativeNodePosition(ref, nodeA, nodeB, in, firstPerson) → Bool`

**Flags:** Native Global

returns the node's relative world position of nodeB minus nodeA into the specify array, must be size of 3

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `nodeA` | `String` | ✓ |  |
| `nodeB` | `String` | ✓ |  |
| `in` | `Float[]` | ✓ |  |
| `firstPerson` | `Bool` | ✓ |  |

### `GetRelativeNodePositionX(ref, nodeA, nodeB, firstPerson) → Float`

**Flags:** Native Global

Returns nodeB - nodeA

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `nodeA` | `String` | ✓ |  |
| `nodeB` | `String` | ✓ |  |
| `firstPerson` | `Bool` | ✓ |  |

### `GetRelativeNodePositionY(ref, nodeA, nodeB, firstPerson) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `nodeA` | `String` | ✓ |  |
| `nodeB` | `String` | ✓ |  |
| `firstPerson` | `Bool` | ✓ |  |

### `GetRelativeNodePositionZ(ref, nodeA, nodeB, firstPerson) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `nodeA` | `String` | ✓ |  |
| `nodeB` | `String` | ✓ |  |
| `firstPerson` | `Bool` | ✓ |  |

### `HasNode(ref, node, firstPerson) → Bool`

**Flags:** Native Global

Return whether the object has the particular node

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `node` | `String` | ✓ |  |
| `firstPerson` | `Bool` | ✓ |  |

### `SetNodeLocalPosition(ref, node, in, firstPerson) → Bool`

**Flags:** Native Global

sets the node's local position of the specified array, must be size of 3

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `node` | `String` | ✓ |  |
| `in` | `Float[]` | ✓ |  |
| `firstPerson` | `Bool` | ✓ |  |

### `SetNodeLocalPositionX(ref, node, x, firstPerson)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `node` | `String` | ✓ |  |
| `x` | `Float` | ✓ |  |
| `firstPerson` | `Bool` | ✓ |  |

### `SetNodeLocalPositionY(ref, node, y, firstPerson)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `node` | `String` | ✓ |  |
| `y` | `Float` | ✓ |  |
| `firstPerson` | `Bool` | ✓ |  |

### `SetNodeLocalPositionZ(ref, node, z, firstPerson)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `node` | `String` | ✓ |  |
| `z` | `Float` | ✓ |  |
| `firstPerson` | `Bool` | ✓ |  |

### `SetNodeLocalRotationEuler(ref, node, in, firstPerson) → Bool`

**Flags:** Native Global

sets the euler rotation for the node of the specified array, must be size of 3

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `node` | `String` | ✓ |  |
| `in` | `Float[]` | ✓ |  |
| `firstPerson` | `Bool` | ✓ |  |

### `SetNodeLocalRotationMatrix(ref, node, in, firstPerson) → Bool`

**Flags:** Native Global

sets the matrix rotation for the node of the specified array, must be size of 9

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `node` | `String` | ✓ |  |
| `in` | `Float[]` | ✓ |  |
| `firstPerson` | `Bool` | ✓ |  |

### `SetNodePositionX(ref, node, x, firstPerson)`

**Flags:** Global

DEPRECATED FUNCTIONS

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `node` | `String` | ✓ |  |
| `x` | `Float` | ✓ |  |
| `firstPerson` | `Bool` | ✓ |  |

### `SetNodePositionY(ref, node, y, firstPerson)`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `node` | `String` | ✓ |  |
| `y` | `Float` | ✓ |  |
| `firstPerson` | `Bool` | ✓ |  |

### `SetNodePositionZ(ref, node, z, firstPerson)`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `node` | `String` | ✓ |  |
| `z` | `Float` | ✓ |  |
| `firstPerson` | `Bool` | ✓ |  |

### `SetNodeScale(ref, node, scale, firstPerson)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `node` | `String` | ✓ |  |
| `scale` | `Float` | ✓ |  |
| `firstPerson` | `Bool` | ✓ |  |

### `SetNodeTextureSet(ref, node, tSet, firstPerson)`

**Flags:** Native Global

Sets a NiTriShape's textures by name of the Nif node

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ref` | `ObjectReference` | ✓ |  |
| `node` | `String` | ✓ |  |
| `tSet` | `TextureSet` | ✓ |  |
| `firstPerson` | `Bool` | ✓ |  |
