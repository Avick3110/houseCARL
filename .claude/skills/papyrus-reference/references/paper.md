# `PAPER_SKSEFunctions`

**Source:** `paper` (PAPER) • **Flags:** Hidden

---

## Global Functions

### `ApplyInventoryEventFilterToForms(aiIndicesToKeep, akFormArray) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiIndicesToKeep` | `Int[]` | ✓ |  |
| `akFormArray` | `Form[]` | ✓ |  |

### `ApplyInventoryEventFilterToInts(aiIndicesToKeep, aiIntArray) → Int[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiIndicesToKeep` | `Int[]` | ✓ |  |
| `aiIntArray` | `Int[]` | ✓ |  |

### `ApplyInventoryEventFilterToObjs(aiIndicesToKeep, akObjArray) → ObjectReference[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiIndicesToKeep` | `Int[]` | ✓ |  |
| `akObjArray` | `ObjectReference[]` | ✓ |  |

### `GetInstalledResources(asStrings) → String[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asStrings` | `String[]` | ✓ |  |

### `GetInventoryEventFilterIndices(akEventItems, akFilter) → Int[]`

**Flags:** Native Global

Helper functions for filtering arguments of Inventory Events

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEventItems` | `Form[]` | ✓ |  |
| `akFilter` | `Form` | ✓ |  |

### `GetPaperVersion() → Int[]`

**Flags:** Native Global

Other

### `GetWarpaintColors(akActorBase) → ColorForm[]`

**Flags:** Native Global

ActorBase

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActorBase` | `ActorBase` | ✓ |  |

### `ResourceExists(asResourcePath) → Bool`

**Flags:** Native Global

Resources

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asResourcePath` | `String` | ✓ |  |

### `UpdateInventoryEventFilterIndices(akEventItems, akFilter, aiIndices) → Int[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akEventItems` | `Form[]` | ✓ |  |
| `akFilter` | `Form` | ✓ |  |
| `aiIndices` | `Int[]` | ✓ |  |
