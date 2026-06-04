# `ActorUtil`

**Source:** `papyrusutil` (PapyrusUtil SE - Modders Scripting Utility Functions) • **Flags:** Hidden

---

## Global Functions

### `AddPackageOverride(targetActor, targetPackage, priority, flags)`

**Flags:** Native Global

This will add a package to actor that will override its normal behavior. Using this function overrides all packages added from any other location.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `targetActor` | `Actor` | ✓ |  |
| `targetPackage` | `Package` | ✓ |  |
| `priority` | `Int` |  | `30` |
| `flags` | `Int` |  | `0` |

### `ClearPackageOverride(targetActor) → Int`

**Flags:** Native Global

Remove all package overrides on this actor, including ones that were added by other mods.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `targetActor` | `Actor` | ✓ |  |

### `CountPackageOverride(targetActor) → Int`

**Flags:** Native Global

Count how many package overrides are currently on this actor. It will also count ones that's condition isn't met.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `targetActor` | `Actor` | ✓ |  |

### `RemoveAllPackageOverride(targetPackage) → Int`

**Flags:** Native Global

Remove this package from all actor overrides.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `targetPackage` | `Package` | ✓ |  |

### `RemovePackageOverride(targetActor, targetPackage) → Bool`

**Flags:** Native Global

Remove a previously added package override.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `targetActor` | `Actor` | ✓ |  |
| `targetPackage` | `Package` | ✓ |  |


---

## `JsonUtil`

**Source:** `papyrusutil` (PapyrusUtil SE - Modders Scripting Utility Functions) • **Flags:** Hidden

---

## Global Functions

### `AdjustFloatValue(FileName, KeyName, amount) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `amount` | `Float` | ✓ |  |

### `AdjustIntValue(FileName, KeyName, amount) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `amount` | `Int` | ✓ |  |

### `CanResolvePath(FileName, Path) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `Path` | `String` | ✓ |  |

### `ClearAll(FileName)`

**Flags:** Native Global

Debug use

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |

### `ClearPath(FileName, Path)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `Path` | `String` | ✓ |  |

### `ClearPathIndex(FileName, Path, Index)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `Path` | `String` | ✓ |  |
| `Index` | `Int` | ✓ |  |

### `CountAllPrefix(FileName, PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `PrefixKey` | `String` | ✓ |  |

### `CountFloatListPrefix(FileName, PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `PrefixKey` | `String` | ✓ |  |

### `CountFloatValuePrefix(FileName, PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `PrefixKey` | `String` | ✓ |  |

### `CountFormListPrefix(FileName, PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `PrefixKey` | `String` | ✓ |  |

### `CountFormValuePrefix(FileName, PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `PrefixKey` | `String` | ✓ |  |

### `CountIntListPrefix(FileName, PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `PrefixKey` | `String` | ✓ |  |

### `CountIntValuePrefix(FileName, PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `PrefixKey` | `String` | ✓ |  |

### `CountStringListPrefix(FileName, PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `PrefixKey` | `String` | ✓ |  |

### `CountStringValuePrefix(FileName, PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `PrefixKey` | `String` | ✓ |  |

### `FindPathFloatElement(FileName, Path, toFind) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `Path` | `String` | ✓ |  |
| `toFind` | `Float` | ✓ |  |

### `FindPathFormElement(FileName, Path, toFind) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `Path` | `String` | ✓ |  |
| `toFind` | `form` | ✓ |  |

### `FindPathIntElement(FileName, Path, toFind) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `Path` | `String` | ✓ |  |
| `toFind` | `Int` | ✓ |  |

### `FindPathStringElement(FileName, Path, toFind) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `Path` | `String` | ✓ |  |
| `toFind` | `String` | ✓ |  |

### `FloatListAdd(FileName, KeyName, value, allowDuplicate) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |
| `allowDuplicate` | `Bool` |  | `true` |

### `FloatListAdjust(FileName, KeyName, index, amount) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `amount` | `Float` | ✓ |  |

### `FloatListClear(FileName, KeyName) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `FloatListCopy(FileName, KeyName, copy) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `copy` | `Float[]` | ✓ |  |

### `FloatListCount(FileName, KeyName) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `FloatListCountValue(FileName, KeyName, value, exclude) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |
| `exclude` | `Bool` |  | `false` |

### `FloatListFind(FileName, KeyName, value) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |

### `FloatListGet(FileName, KeyName, index) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `FloatListHas(FileName, KeyName, value) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |

### `FloatListInsertAt(FileName, KeyName, index, value) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `value` | `Float` | ✓ |  |

### `FloatListRandom(FileName, KeyName) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `FloatListRemove(FileName, KeyName, value, allInstances) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |
| `allInstances` | `Bool` |  | `true` |

### `FloatListRemoveAt(FileName, KeyName, index) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `FloatListResize(FileName, KeyName, toLength, filler) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `toLength` | `Int` | ✓ |  |
| `filler` | `Float` |  | `0` |

### `FloatListSet(FileName, KeyName, index, value) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `value` | `Float` | ✓ |  |

### `FloatListSlice(FileName, KeyName, slice, startIndex)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `slice` | `Float[]` | ✓ |  |
| `startIndex` | `Int` |  | `0` |

### `FloatListToArray(FileName, KeyName) → Float[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `FormListAdd(FileName, KeyName, value, allowDuplicate) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `Form` | ✓ |  |
| `allowDuplicate` | `Bool` |  | `true` |

### `FormListClear(FileName, KeyName) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `FormListCopy(FileName, KeyName, copy) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `copy` | `Form[]` | ✓ |  |

### `FormListCount(FileName, KeyName) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `FormListCountValue(FileName, KeyName, value, exclude) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `Form` | ✓ |  |
| `exclude` | `Bool` |  | `false` |

### `FormListFind(FileName, KeyName, value) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `Form` | ✓ |  |

### `FormListGet(FileName, KeyName, index) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `FormListHas(FileName, KeyName, value) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `Form` | ✓ |  |

### `FormListInsertAt(FileName, KeyName, index, value) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `value` | `Form` | ✓ |  |

### `FormListRandom(FileName, KeyName) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `FormListRemove(FileName, KeyName, value, allInstances) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `Form` | ✓ |  |
| `allInstances` | `Bool` |  | `true` |

### `FormListRemoveAt(FileName, KeyName, index) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `FormListResize(FileName, KeyName, toLength, filler) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `toLength` | `Int` | ✓ |  |
| `filler` | `Form` |  |  |

### `FormListSet(FileName, KeyName, index, value) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `value` | `Form` | ✓ |  |

### `FormListSlice(FileName, KeyName, slice, startIndex)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `slice` | `Form[]` | ✓ |  |
| `startIndex` | `Int` |  | `0` |

### `FormListToArray(FileName, KeyName) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `GetErrors(FileName) → String`

**Flags:** Native Global

Get a formatted error string of any json parser errors on a file, returns as empty string if no errors.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |

### `GetFloatValue(FileName, KeyName, missing) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `missing` | `Float` |  | `0` |

### `GetFormValue(FileName, KeyName, missing) → form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `missing` | `form` |  |  |

### `GetIntValue(FileName, KeyName, missing) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `missing` | `Int` |  | `0` |

### `GetPathBoolValue(FileName, Path, missing) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `Path` | `String` | ✓ |  |
| `missing` | `Bool` |  | `false` |

### `GetPathFloatValue(FileName, Path, missing) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `Path` | `String` | ✓ |  |
| `missing` | `Float` |  | `0` |

### `GetPathFormValue(FileName, Path, missing) → form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `Path` | `String` | ✓ |  |
| `missing` | `form` |  |  |

### `GetPathIntValue(FileName, Path, missing) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `Path` | `String` | ✓ |  |
| `missing` | `Int` |  | `0` |

### `GetPathStringValue(FileName, Path, missing) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `Path` | `String` | ✓ |  |
| `missing` | `String` |  | `""` |

### `GetStringValue(FileName, KeyName, missing) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `missing` | `String` |  | `""` |

### `HasFloatValue(FileName, KeyName) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `HasFormValue(FileName, KeyName) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `HasIntValue(FileName, KeyName) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `HasStringValue(FileName, KeyName) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `IntListAdd(FileName, KeyName, value, allowDuplicate) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |
| `allowDuplicate` | `Bool` |  | `true` |

### `IntListAdjust(FileName, KeyName, index, amount) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `amount` | `Int` | ✓ |  |

### `IntListClear(FileName, KeyName) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `IntListCopy(FileName, KeyName, copy) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `copy` | `Int[]` | ✓ |  |

### `IntListCount(FileName, KeyName) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `IntListCountValue(FileName, KeyName, value, exclude) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |
| `exclude` | `Bool` |  | `false` |

### `IntListFind(FileName, KeyName, value) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |

### `IntListGet(FileName, KeyName, index) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `IntListHas(FileName, KeyName, value) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |

### `IntListInsertAt(FileName, KeyName, index, value) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `value` | `Int` | ✓ |  |

### `IntListRandom(FileName, KeyName) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `IntListRemove(FileName, KeyName, value, allInstances) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |
| `allInstances` | `Bool` |  | `true` |

### `IntListRemoveAt(FileName, KeyName, index) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `IntListResize(FileName, KeyName, toLength, filler) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `toLength` | `Int` | ✓ |  |
| `filler` | `Int` |  | `0` |

### `IntListSet(FileName, KeyName, index, value) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `value` | `Int` | ✓ |  |

### `IntListSlice(FileName, KeyName, slice, startIndex)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `slice` | `Int[]` | ✓ |  |
| `startIndex` | `Int` |  | `0` |

### `IntListToArray(FileName, KeyName) → Int[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `IsGood(FileName) → Bool`

**Flags:** Native Global

Check if the given file was succesfully loaded and has no json parser errors

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |

### `IsPathArray(FileName, Path) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `Path` | `String` | ✓ |  |

### `IsPathBool(FileName, Path) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `Path` | `String` | ✓ |  |

### `IsPathForm(FileName, Path) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `Path` | `String` | ✓ |  |

### `IsPathNumber(FileName, Path) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `Path` | `String` | ✓ |  |

### `IsPathObject(FileName, Path) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `Path` | `String` | ✓ |  |

### `IsPathString(FileName, Path) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `Path` | `String` | ✓ |  |

### `IsPendingSave(FileName) → Bool`

**Flags:** Native Global

Check if given file has had any changes to it they haven't yet been saved

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |

### `JsonExists(FileName) → Bool`

**Flags:** Global

Check if a json file exists or not

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |

### `JsonInFolder(folderPath) → String[]`

**Flags:** Native Global

Returns a list of all filenames in a given folder that end in .json

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `folderPath` | `String` | ✓ |  |

### `Load(FileName) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |

### `PathCount(FileName, Path) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `Path` | `String` | ✓ |  |

### `PathFloatElements(FileName, Path, invalidType) → Float[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `Path` | `String` | ✓ |  |
| `invalidType` | `Float` |  | `0` |

### `PathFormElements(FileName, Path, invalidType) → form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `Path` | `String` | ✓ |  |
| `invalidType` | `form` |  |  |

### `PathIntElements(FileName, Path, invalidType) → Int[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `Path` | `String` | ✓ |  |
| `invalidType` | `Int` |  | `0` |

### `PathMembers(FileName, Path) → String[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `Path` | `String` | ✓ |  |

### `PathStringElements(FileName, Path, invalidType) → String[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `Path` | `String` | ✓ |  |
| `invalidType` | `String` |  | `""` |

### `Save(FileName, minify) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `minify` | `Bool` |  | `false` |

### `SetFloatValue(FileName, KeyName, value) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |

### `SetFormValue(FileName, KeyName, value) → form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `form` | ✓ |  |

### `SetIntValue(FileName, KeyName, value) → Int`

**Flags:** Native Global

See StorageUtil.psc for equivalent function usage instructions

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |

### `SetPathFloatArray(FileName, Path, arr, append)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `Path` | `String` | ✓ |  |
| `arr` | `Float[]` | ✓ |  |
| `append` | `Bool` |  | `false` |

### `SetPathFloatValue(FileName, Path, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `Path` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |

### `SetPathFormArray(FileName, Path, arr, append)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `Path` | `String` | ✓ |  |
| `arr` | `form[]` | ✓ |  |
| `append` | `Bool` |  | `false` |

### `SetPathFormValue(FileName, Path, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `Path` | `String` | ✓ |  |
| `value` | `form` | ✓ |  |

### `SetPathIntArray(FileName, Path, arr, append)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `Path` | `String` | ✓ |  |
| `arr` | `Int[]` | ✓ |  |
| `append` | `Bool` |  | `false` |

### `SetPathIntValue(FileName, Path, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `Path` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |

### `SetPathStringArray(FileName, Path, arr, append)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `Path` | `String` | ✓ |  |
| `arr` | `String[]` | ✓ |  |
| `append` | `Bool` |  | `false` |

### `SetPathStringValue(FileName, Path, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `Path` | `String` | ✓ |  |
| `value` | `String` | ✓ |  |

### `SetRawPathValue(FileName, Path, RawJSON) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `Path` | `String` | ✓ |  |
| `RawJSON` | `String` | ✓ |  |

### `SetStringValue(FileName, KeyName, value) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `String` | ✓ |  |

### `StringListAdd(FileName, KeyName, value, allowDuplicate) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `String` | ✓ |  |
| `allowDuplicate` | `Bool` |  | `true` |

### `StringListClear(FileName, KeyName) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `StringListCopy(FileName, KeyName, copy) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `copy` | `String[]` | ✓ |  |

### `StringListCount(FileName, KeyName) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `StringListCountValue(FileName, KeyName, value, exclude) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `String` | ✓ |  |
| `exclude` | `Bool` |  | `false` |

### `StringListFind(FileName, KeyName, value) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `String` | ✓ |  |

### `StringListGet(FileName, KeyName, index) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `StringListHas(FileName, KeyName, value) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `String` | ✓ |  |

### `StringListInsertAt(FileName, KeyName, index, value) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `value` | `String` | ✓ |  |

### `StringListRandom(FileName, KeyName) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `StringListRemove(FileName, KeyName, value, allInstances) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `String` | ✓ |  |
| `allInstances` | `Bool` |  | `true` |

### `StringListRemoveAt(FileName, KeyName, index) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `StringListResize(FileName, KeyName, toLength, filler) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `toLength` | `Int` | ✓ |  |
| `filler` | `String` |  | `""` |

### `StringListSet(FileName, KeyName, index, value) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `value` | `String` | ✓ |  |

### `StringListSlice(FileName, KeyName, slice, startIndex)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `slice` | `String[]` | ✓ |  |
| `startIndex` | `Int` |  | `0` |

### `StringListToArray(FileName, KeyName) → String[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `Unload(FileName, saveChanges, minify) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `saveChanges` | `Bool` |  | `true` |
| `minify` | `Bool` |  | `false` |

### `UnsetFloatValue(FileName, KeyName) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `UnsetFormValue(FileName, KeyName) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `UnsetIntValue(FileName, KeyName) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `UnsetStringValue(FileName, KeyName) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FileName` | `String` | ✓ |  |
| `KeyName` | `String` | ✓ |  |


---

## `MiscUtil`

**Source:** `papyrusutil` (PapyrusUtil SE - Modders Scripting Utility Functions) • **Flags:** Hidden

---

## Global Functions

### `ExecuteBat(fileName)`

**Flags:** Global

Bat console command.
REMOVED v2.9: Unused.
function ExecuteBat(string fileName) global native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fileName` | `String` | ✓ |  |

### `FileExists(fileName) → Bool`

**Flags:** Native Global

Check if a given file exists relative to root Skyrim directory. Example: FileExists("data/meshes/example.nif")

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fileName` | `String` | ✓ |  |

### `FilesInFolder(directory, extension) → String[]`

**Flags:** Native Global

Get an array of files in a given parent directory that have the given extension.
directory is relative to the root Skyrim folder (where skyrim.exe is) and is non-recursive.
directory = "." to get all files in root Skyrim folder
directory = "data/meshes" to get all files in the <root>/data/meshes folder
extension = ".nif" to get all .nif mesh files.
(default) extension="*" to get all files

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `directory` | `String` | ✓ |  |
| `extension` | `String` |  | `""` |

### `FoldersInFolder(directory) → String[]`

**Flags:** Native Global

Get an array of folders in a given parent directory
Same rules and examples as above FilesInFolder apply to the directory rule here.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `directory` | `String` | ✓ |  |

### `GetActorRaceEditorID(actorRef) → String`

**Flags:** Native Global

Get race's editor ID.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `actorRef` | `Actor` | ✓ |  |

### `GetNodeRotation(obj, nodeName, firstPerson, rotationIndex) → Float`

**Flags:** Global

Get node rotation
REMOVED v2.9: Useless, only does a part of the job.
float function GetNodeRotation(ObjectReference obj, string nodeName, bool firstPerson, int rotationIndex) global native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `obj` | `ObjectReference` | ✓ |  |
| `nodeName` | `String` | ✓ |  |
| `firstPerson` | `Bool` | ✓ |  |
| `rotationIndex` | `Int` | ✓ |  |

### `GetRaceEditorID(raceForm) → String`

**Flags:** Native Global

Get race's editor ID.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `raceForm` | `Race` | ✓ |  |

### `PrintConsole(text)`

**Flags:** Native Global

Print text to console.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `text` | `String` | ✓ |  |

### `ReadFromFile(fileName) → String`

**Flags:** Native Global

Read string from file. Do not read large files!

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fileName` | `String` | ✓ |  |

### `ScanCellActors(CenterOn, radius, HasKeyword) → Actor[]`

**Flags:** Global

LEGACY v3.3 - Added Ignoredead parameter to function, aliased for backwards compatability with v3.2.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `CenterOn` | `ObjectReference` | ✓ |  |
| `radius` | `Float` |  | `5000` |
| `HasKeyword` | `Keyword` |  |  |

### `ScanCellNPCs(CenterOn, radius, HasKeyword, IgnoreDead) → Actor[]`

**Flags:** Native Global

Scans the current cell of the given CenterOn for an actor within the given radius and returns an array for all actors that are
currently alive and (optionally) has the given keyword if changed from default none. Setting radius higher than 0.0 will restrict the
search distance from around CenterOn, 0.0 will search entire cell the object is in.
NOTE: Keyword searches seem a little unpredictable so be sure to test if your usage of it works before using the results.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `CenterOn` | `ObjectReference` | ✓ |  |
| `radius` | `Float` |  | `0` |
| `HasKeyword` | `Keyword` |  |  |
| `IgnoreDead` | `Bool` |  | `true` |

### `ScanCellNPCsByFaction(FindFaction, CenterOn, radius, minRank, maxRank, IgnoreDead) → Actor[]`

**Flags:** Native Global

Same as ScanCellNPCs(), however it filters the return by a given faction and (optionally) their rank in that faction.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `FindFaction` | `Faction` | ✓ |  |
| `CenterOn` | `ObjectReference` | ✓ |  |
| `radius` | `Float` |  | `0` |
| `minRank` | `Int` |  | `0` |
| `maxRank` | `Int` |  | `127` |
| `IgnoreDead` | `Bool` |  | `true` |

### `ScanCellObjects(formType, CenterOn, radius, HasKeyword) → objectreference[]`

**Flags:** Native Global

Scans the current cell of the given CenterOn for an object of the given form type ID within radius and returns an array for all that
and (optionally) also has the given keyword if changed from default none. Setting radius higher than 0.0 will restrict the
search distance from around CenterOn, 0.0 will search entire cell the object is in.
NOTE: Keyword searches seem a little unpredictable so be sure to test if your usage of it works before using the results.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `formType` | `Int` | ✓ |  |
| `CenterOn` | `ObjectReference` | ✓ |  |
| `radius` | `Float` |  | `0` |
| `HasKeyword` | `Keyword` |  |  |

### `SetFreeCameraSpeed(speed)`

**Flags:** Native Global

Set freefly cam speed.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `speed` | `Float` | ✓ |  |

### `SetFreeCameraState(enable, speed)`

**Flags:** Native Global

Set current freefly cam state & set the speed if enabling

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `enable` | `Bool` | ✓ |  |
| `speed` | `Float` |  | `10` |

### `SetMenus(enabled)`

**Flags:** Native Global

Set HUD on / off - NOT CURRENT WORKING IN SKYRIM SPECIAL EDITION

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `enabled` | `Bool` | ✓ |  |

### `ToggleFreeCamera(stopTime)`

**Flags:** Native Global

Toggle freefly camera.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `stopTime` | `Bool` |  | `false` |

### `WriteToFile(fileName, text, append, timestamp) → Bool`

**Flags:** Native Global

Write string to file.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fileName` | `String` | ✓ |  |
| `text` | `String` | ✓ |  |
| `append` | `Bool` |  | `true` |
| `timestamp` | `Bool` |  | `false` |


---

## `ObjectUtil`

**Source:** `papyrusutil` (PapyrusUtil SE - Modders Scripting Utility Functions) • **Flags:** Hidden


---

## `PapyrusUtil`

**Source:** `papyrusutil` (PapyrusUtil SE - Modders Scripting Utility Functions) • **Flags:** Hidden

---

## Global Functions

### `ActorArray(size, filler) → Actor[]`

**Flags:** Native Global

Few extra array types not provided by SKSE normally to help avoid having to use and cast Form arrays

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `size` | `Int` | ✓ |  |
| `filler` | `Actor` |  |  |

### `AddFloatValues(Values) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Values` | `Float[]` | ✓ |  |

### `AddIntValues(Values) → Int`

**Flags:** Native Global

## Return the total sum of all values stored in the given array

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Values` | `Int[]` | ✓ |  |

### `AliasArray(size, filler) → Alias[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `size` | `Int` | ✓ |  |
| `filler` | `Alias` |  |  |

### `BoolArray(size, filler) → Bool[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `size` | `Int` | ✓ |  |
| `filler` | `Bool` |  | `false` |

### `ClampFloat(value, min, max) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `value` | `Float` | ✓ |  |
| `min` | `Float` | ✓ |  |
| `max` | `Float` | ✓ |  |

### `ClampInt(value, min, max) → Int`

**Flags:** Native Global

## Returns the value clamped to the min or max when out of range

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `value` | `Int` | ✓ |  |
| `min` | `Int` | ✓ |  |
| `max` | `Int` | ✓ |  |

### `ClearEmpty(ArrayValues) → String[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `String[]` | ✓ |  |

### `ClearNone(ArrayValues) → Form[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Form[]` | ✓ |  |

### `CountActor(ArrayValues, EqualTo) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Actor[]` | ✓ |  |
| `EqualTo` | `Actor` | ✓ |  |

### `CountAlias(ArrayValues, EqualTo) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Alias[]` | ✓ |  |
| `EqualTo` | `Alias` | ✓ |  |

### `CountBool(ArrayValues, EqualTo) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Bool[]` | ✓ |  |
| `EqualTo` | `Bool` | ✓ |  |

### `CountFalse(ArrayValues) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Bool[]` | ✓ |  |

### `CountFloat(ArrayValues, EqualTo) → Int`

**Flags:** Native Global

## Returns the number of instances an array has an element equal to the given value

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Float[]` | ✓ |  |
| `EqualTo` | `Float` | ✓ |  |

### `CountForm(ArrayValues, EqualTo) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Form[]` | ✓ |  |
| `EqualTo` | `Form` | ✓ |  |

### `CountInt(ArrayValues, EqualTo) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Int[]` | ✓ |  |
| `EqualTo` | `Int` | ✓ |  |

### `CountNone(ArrayValues) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Form[]` | ✓ |  |

### `CountObjRef(ArrayValues, EqualTo) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `ObjectReference[]` | ✓ |  |
| `EqualTo` | `ObjectReference` | ✓ |  |

### `CountString(ArrayValues, EqualTo) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `String[]` | ✓ |  |
| `EqualTo` | `String` | ✓ |  |

### `CountTrue(ArrayValues) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Bool[]` | ✓ |  |

### `FloatArray(size, filler) → Float[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `size` | `Int` | ✓ |  |
| `filler` | `Float` |  | `0` |

### `FormArray(size, filler) → Form[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `size` | `Int` | ✓ |  |
| `filler` | `Form` |  |  |

### `GetDiffActor(ArrayValues1, ArrayValues2, CompareBoth, IncludeDupes) → Actor[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues1` | `Actor[]` | ✓ |  |
| `ArrayValues2` | `Actor[]` | ✓ |  |
| `CompareBoth` | `Bool` |  | `false` |
| `IncludeDupes` | `Bool` |  | `false` |

### `GetDiffAlias(ArrayValues1, ArrayValues2, CompareBoth, IncludeDupes) → Alias[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues1` | `Alias[]` | ✓ |  |
| `ArrayValues2` | `Alias[]` | ✓ |  |
| `CompareBoth` | `Bool` |  | `false` |
| `IncludeDupes` | `Bool` |  | `false` |

### `GetDiffFloat(ArrayValues1, ArrayValues2, CompareBoth, IncludeDupes) → Float[]`

**Flags:** Native Global

## Get an array of values from ArrayValues1 that ARE NOT among the values of ArrayValues2. Duplicates are removed by default.
## Setting CompareBoth = true will change the behavior to also include the reverse comparison of ArrayValues2 values that are not present in ArrayValues1.
## Setting IncludeDupes = true will allow the resulting array to include duplicate entries of the same value if they were also duplicated in the input arrays.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues1` | `Float[]` | ✓ |  |
| `ArrayValues2` | `Float[]` | ✓ |  |
| `CompareBoth` | `Bool` |  | `false` |
| `IncludeDupes` | `Bool` |  | `false` |

### `GetDiffForm(ArrayValues1, ArrayValues2, CompareBoth, IncludeDupes) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues1` | `Form[]` | ✓ |  |
| `ArrayValues2` | `Form[]` | ✓ |  |
| `CompareBoth` | `Bool` |  | `false` |
| `IncludeDupes` | `Bool` |  | `false` |

### `GetDiffInt(ArrayValues1, ArrayValues2, CompareBoth, IncludeDupes) → Int[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues1` | `Int[]` | ✓ |  |
| `ArrayValues2` | `Int[]` | ✓ |  |
| `CompareBoth` | `Bool` |  | `false` |
| `IncludeDupes` | `Bool` |  | `false` |

### `GetDiffObjRef(ArrayValues1, ArrayValues2, CompareBoth, IncludeDupes) → ObjectReference[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues1` | `ObjectReference[]` | ✓ |  |
| `ArrayValues2` | `ObjectReference[]` | ✓ |  |
| `CompareBoth` | `Bool` |  | `false` |
| `IncludeDupes` | `Bool` |  | `false` |

### `GetDiffString(ArrayValues1, ArrayValues2, CompareBoth, IncludeDupes) → String[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues1` | `String[]` | ✓ |  |
| `ArrayValues2` | `String[]` | ✓ |  |
| `CompareBoth` | `Bool` |  | `false` |
| `IncludeDupes` | `Bool` |  | `false` |

### `GetMatchingActor(ArrayValues1, ArrayValues2) → Actor[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues1` | `Actor[]` | ✓ |  |
| `ArrayValues2` | `Actor[]` | ✓ |  |

### `GetMatchingAlias(ArrayValues1, ArrayValues2) → Alias[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues1` | `Alias[]` | ✓ |  |
| `ArrayValues2` | `Alias[]` | ✓ |  |

### `GetMatchingFloat(ArrayValues1, ArrayValues2) → Float[]`

**Flags:** Native Global

## Get an array of values that are present in both ArrayValues1 and ArrayValues2.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues1` | `Float[]` | ✓ |  |
| `ArrayValues2` | `Float[]` | ✓ |  |

### `GetMatchingForm(ArrayValues1, ArrayValues2) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues1` | `Form[]` | ✓ |  |
| `ArrayValues2` | `Form[]` | ✓ |  |

### `GetMatchingInt(ArrayValues1, ArrayValues2) → Int[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues1` | `Int[]` | ✓ |  |
| `ArrayValues2` | `Int[]` | ✓ |  |

### `GetMatchingObjRef(ArrayValues1, ArrayValues2) → ObjectReference[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues1` | `ObjectReference[]` | ✓ |  |
| `ArrayValues2` | `ObjectReference[]` | ✓ |  |

### `GetMatchingString(ArrayValues1, ArrayValues2) → String[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues1` | `String[]` | ✓ |  |
| `ArrayValues2` | `String[]` | ✓ |  |

### `GetScriptVersion() → Int`

**Flags:** Global

Get version of compiled papyrus scripts which should match return from GetVersion()

### `GetVersion() → Int`

**Flags:** Native Global

Get version of papyrus DLL library. Version 4.6 will return 46.

### `IntArray(size, filler) → Int[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `size` | `Int` | ✓ |  |
| `filler` | `Int` |  | `0` |

### `MergeActorArray(ArrayValues1, ArrayValues2, RemoveDupes) → Actor[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues1` | `Actor[]` | ✓ |  |
| `ArrayValues2` | `Actor[]` | ✓ |  |
| `RemoveDupes` | `Bool` |  | `false` |

### `MergeAliasArray(ArrayValues1, ArrayValues2, RemoveDupes) → Alias[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues1` | `Alias[]` | ✓ |  |
| `ArrayValues2` | `Alias[]` | ✓ |  |
| `RemoveDupes` | `Bool` |  | `false` |

### `MergeBoolArray(ArrayValues1, ArrayValues2, RemoveDupes) → Bool[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues1` | `Bool[]` | ✓ |  |
| `ArrayValues2` | `Bool[]` | ✓ |  |
| `RemoveDupes` | `Bool` |  | `false` |

### `MergeFloatArray(ArrayValues1, ArrayValues2, RemoveDupes) → Float[]`

**Flags:** Native Global

## Returns two arrays combined into one, optionally also removing any duplicate occurrences of a value.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues1` | `Float[]` | ✓ |  |
| `ArrayValues2` | `Float[]` | ✓ |  |
| `RemoveDupes` | `Bool` |  | `false` |

### `MergeFormArray(ArrayValues1, ArrayValues2, RemoveDupes) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues1` | `Form[]` | ✓ |  |
| `ArrayValues2` | `Form[]` | ✓ |  |
| `RemoveDupes` | `Bool` |  | `false` |

### `MergeIntArray(ArrayValues1, ArrayValues2, RemoveDupes) → Int[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues1` | `Int[]` | ✓ |  |
| `ArrayValues2` | `Int[]` | ✓ |  |
| `RemoveDupes` | `Bool` |  | `false` |

### `MergeObjRefArray(ArrayValues1, ArrayValues2, RemoveDupes) → ObjectReference[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues1` | `ObjectReference[]` | ✓ |  |
| `ArrayValues2` | `ObjectReference[]` | ✓ |  |
| `RemoveDupes` | `Bool` |  | `false` |

### `MergeStringArray(ArrayValues1, ArrayValues2, RemoveDupes) → String[]`

**Flags:** Native Global

bool[] function MergeBoolArray(bool[] ArrayValues1, bool[] ArrayValues2, bool RemoveDupes = false) global native ; // Bugged - Non-native version available below

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues1` | `String[]` | ✓ |  |
| `ArrayValues2` | `String[]` | ✓ |  |
| `RemoveDupes` | `Bool` |  | `false` |

### `ObjRefArray(size, filler) → ObjectReference[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `size` | `Int` | ✓ |  |
| `filler` | `ObjectReference` |  |  |

### `PushActor(ArrayValues, push) → Actor[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Actor[]` | ✓ |  |
| `push` | `Actor` | ✓ |  |

### `PushAlias(ArrayValues, push) → Alias[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Alias[]` | ✓ |  |
| `push` | `Alias` | ✓ |  |

### `PushBool(ArrayValues, push) → Bool[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Bool[]` | ✓ |  |
| `push` | `Bool` | ✓ |  |

### `PushFloat(ArrayValues, push) → Float[]`

**Flags:** Native Global

## Append a value to the end of the given array and return the new array.
## NOTE: The array has to be recreated each time you call this. For the sake of memory usage and performance, DO NOT use these to build up an array through a loop,
##       in such a situation it is significantly faster to create the full length array first and then fill it. Best to limit to only the occasional need.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Float[]` | ✓ |  |
| `push` | `Float` | ✓ |  |

### `PushForm(ArrayValues, push) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Form[]` | ✓ |  |
| `push` | `Form` | ✓ |  |

### `PushInt(ArrayValues, push) → Int[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Int[]` | ✓ |  |
| `push` | `Int` | ✓ |  |

### `PushObjRef(ArrayValues, push) → ObjectReference[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `ObjectReference[]` | ✓ |  |
| `push` | `ObjectReference` | ✓ |  |

### `PushString(ArrayValues, push) → String[]`

**Flags:** Native Global

bool[] function PushBool(bool[] ArrayValues, bool push) global native ; // Bugged - Non-native version available below

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `String[]` | ✓ |  |
| `push` | `String` | ✓ |  |

### `RemoveActor(ArrayValues, ToRemove) → Actor[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Actor[]` | ✓ |  |
| `ToRemove` | `Actor` | ✓ |  |

### `RemoveAlias(ArrayValues, ToRemove) → Alias[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Alias[]` | ✓ |  |
| `ToRemove` | `Alias` | ✓ |  |

### `RemoveBool(ArrayValues, ToRemove) → Bool[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Bool[]` | ✓ |  |
| `ToRemove` | `Bool` | ✓ |  |

### `RemoveDupeActor(ArrayValues) → Actor[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Actor[]` | ✓ |  |

### `RemoveDupeAlias(ArrayValues) → Alias[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Alias[]` | ✓ |  |

### `RemoveDupeFloat(ArrayValues) → Float[]`

**Flags:** Native Global

## Removes all duplicate elements from the given array and returns the shortened array with only a single instance of all element values.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Float[]` | ✓ |  |

### `RemoveDupeForm(ArrayValues) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Form[]` | ✓ |  |

### `RemoveDupeInt(ArrayValues) → Int[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Int[]` | ✓ |  |

### `RemoveDupeObjRef(ArrayValues) → ObjectReference[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `ObjectReference[]` | ✓ |  |

### `RemoveDupeString(ArrayValues) → String[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `String[]` | ✓ |  |

### `RemoveFloat(ArrayValues, ToRemove) → Float[]`

**Flags:** Native Global

## Removes all elements from the given array matching the provided value and returns the shortened array.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Float[]` | ✓ |  |
| `ToRemove` | `Float` | ✓ |  |

### `RemoveForm(ArrayValues, ToRemove) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Form[]` | ✓ |  |
| `ToRemove` | `Form` | ✓ |  |

### `RemoveInt(ArrayValues, ToRemove) → Int[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Int[]` | ✓ |  |
| `ToRemove` | `Int` | ✓ |  |

### `RemoveObjRef(ArrayValues, ToRemove) → ObjectReference[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `ObjectReference[]` | ✓ |  |
| `ToRemove` | `ObjectReference` | ✓ |  |

### `RemoveString(ArrayValues, ToRemove) → String[]`

**Flags:** Native Global

bool[] function RemoveBool(bool[] ArrayValues, bool ToRemove) global native ; // Bugged - Non-native version available below

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `String[]` | ✓ |  |
| `ToRemove` | `String` | ✓ |  |

### `ResizeActorArray(ArrayValues, toSize, filler) → Actor[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Actor[]` | ✓ |  |
| `toSize` | `Int` | ✓ |  |
| `filler` | `Actor` |  |  |

### `ResizeAliasArray(ArrayValues, toSize, filler) → Alias[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Alias[]` | ✓ |  |
| `toSize` | `Int` | ✓ |  |
| `filler` | `Alias` |  |  |

### `ResizeBoolArray(ArrayValues, toSize, filler) → Bool[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Bool[]` | ✓ |  |
| `toSize` | `Int` | ✓ |  |
| `filler` | `Bool` |  | `false` |

### `ResizeFloatArray(ArrayValues, toSize, filler) → Float[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Float[]` | ✓ |  |
| `toSize` | `Int` | ✓ |  |
| `filler` | `Float` |  | `0` |

### `ResizeFormArray(ArrayValues, toSize, filler) → Form[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Form[]` | ✓ |  |
| `toSize` | `Int` | ✓ |  |
| `filler` | `Form` |  |  |

### `ResizeIntArray(ArrayValues, toSize, filler) → Int[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Int[]` | ✓ |  |
| `toSize` | `Int` | ✓ |  |
| `filler` | `Int` |  | `0` |

### `ResizeObjRefArray(ArrayValues, toSize, filler) → ObjectReference[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `ObjectReference[]` | ✓ |  |
| `toSize` | `Int` | ✓ |  |
| `filler` | `ObjectReference` |  |  |

### `ResizeStringArray(ArrayValues, toSize, filler) → String[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `String[]` | ✓ |  |
| `toSize` | `Int` | ✓ |  |
| `filler` | `String` |  | `""` |

### `SignFloat(doSign, value) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `doSign` | `Bool` | ✓ |  |
| `value` | `Float` | ✓ |  |

### `SignInt(doSign, value) → Int`

**Flags:** Native Global

## Returns the given value signed if bool is true, unsigned if false, regardless if value started out signed or not.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `doSign` | `Bool` | ✓ |  |
| `value` | `Int` | ✓ |  |

### `SliceActorArray(ArrayValues, StartIndex, EndIndex) → Actor[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Actor[]` | ✓ |  |
| `StartIndex` | `Int` | ✓ |  |
| `EndIndex` | `Int` |  | `-1` |

### `SliceAliasArray(ArrayValues, StartIndex, EndIndex) → Alias[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Alias[]` | ✓ |  |
| `StartIndex` | `Int` | ✓ |  |
| `EndIndex` | `Int` |  | `-1` |

### `SliceBoolArray(ArrayValues, StartIndex, EndIndex) → Bool[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Bool[]` | ✓ |  |
| `StartIndex` | `Int` | ✓ |  |
| `EndIndex` | `Int` |  | `-1` |

### `SliceFloatArray(ArrayValues, StartIndex, EndIndex) → Float[]`

**Flags:** Native Global

## Returns a sub section of an array indicated by a starting and ending index.
## The default argument "int EndIndex = -1" clamps the to the end of the array. Equivalent of setting EndIndex = (ArrayValues.Length - 1)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Float[]` | ✓ |  |
| `StartIndex` | `Int` | ✓ |  |
| `EndIndex` | `Int` |  | `-1` |

### `SliceFormArray(ArrayValues, StartIndex, EndIndex) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Form[]` | ✓ |  |
| `StartIndex` | `Int` | ✓ |  |
| `EndIndex` | `Int` |  | `-1` |

### `SliceIntArray(ArrayValues, StartIndex, EndIndex) → Int[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Int[]` | ✓ |  |
| `StartIndex` | `Int` | ✓ |  |
| `EndIndex` | `Int` |  | `-1` |

### `SliceObjRefArray(ArrayValues, StartIndex, EndIndex) → ObjectReference[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `ObjectReference[]` | ✓ |  |
| `StartIndex` | `Int` | ✓ |  |
| `EndIndex` | `Int` |  | `-1` |

### `SliceStringArray(ArrayValues, StartIndex, EndIndex) → String[]`

**Flags:** Native Global

bool[] function SliceBoolArray(bool[] ArrayValues, int StartIndex, int EndIndex = -1) global native ; // Bugged - Non-native version available below

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `String[]` | ✓ |  |
| `StartIndex` | `Int` | ✓ |  |
| `EndIndex` | `Int` |  | `-1` |

### `SortFloatArray(ArrayValues, descending)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Float[]` | ✓ |  |
| `descending` | `Bool` |  | `false` |

### `SortIntArray(ArrayValues, descending)`

**Flags:** Native Global

## Sorts a given array's elements alphanumerically. Sorted in ascending order by default.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `Int[]` | ✓ |  |
| `descending` | `Bool` |  | `false` |

### `SortStringArray(ArrayValues, descending)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArrayValues` | `String[]` | ✓ |  |
| `descending` | `Bool` |  | `false` |

### `StringArray(size, filler) → String[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `size` | `Int` | ✓ |  |
| `filler` | `String` |  | `""` |

### `StringJoin(Values, Delimiter) → String`

**Flags:** Native Global

## Opposite of StringSplit()

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Values` | `String[]` | ✓ |  |
| `Delimiter` | `String` |  | `""` |

### `StringSplit(ArgString, Delimiter) → String[]`

**Flags:** Native Global

## Similar to SKSE's native StringUtil.Split() except results are whitespace trimmed. So comma, separated,list,can, be, spaced,or,not.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ArgString` | `String` | ✓ |  |
| `Delimiter` | `String` |  | `""` |

### `WrapFloat(value, end, start) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `value` | `Float` | ✓ |  |
| `end` | `Float` | ✓ |  |
| `start` | `Float` |  | `0` |

### `WrapInt(value, end, start) → Int`

**Flags:** Native Global

## Similar to the clamp functions, only values wrap around to the other side of range instead.
## Mostly useful for traversing around array values by wrapping the index from end to end without having to check for it being out of range first.
##     i.e.: Form var = myFormArray[WrapInt(i, (myFormArray.Length - 1))]

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `value` | `Int` | ✓ |  |
| `end` | `Int` | ✓ |  |
| `start` | `Int` |  | `0` |


---

## `StorageUtil`

**Source:** `papyrusutil` (PapyrusUtil SE - Modders Scripting Utility Functions) • **Flags:** Hidden

---

## Global Functions

### `AdjustFloatValue(ObjKey, KeyName, amount) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `amount` | `Float` | ✓ |  |

### `AdjustIntValue(ObjKey, KeyName, amount) → Int`

**Flags:** Native Global

Get previously saved int/float/string/Form value on form or globally.

  ObjKey: form to get from. Set none to get global value.
  KeyName: name of value.
  amount: +/- the amount to adjust the current value by

  given keys will be initialized to given amount if it does not exist

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `amount` | `Int` | ✓ |  |

### `ClearAllObjPrefix(ObjKey, PrefixKey) → Int`

**Flags:** Native Global

Performs all of the above prefix clears in one go.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `PrefixKey` | `String` | ✓ |  |

### `ClearAllPrefix(PrefixKey) → Int`

**Flags:** Native Global

Performs all of the above prefix clears in one go.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `PrefixKey` | `String` | ✓ |  |

### `ClearFloatListPrefix(PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `PrefixKey` | `String` | ✓ |  |

### `ClearFloatValuePrefix(PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `PrefixKey` | `String` | ✓ |  |

### `ClearFormListPrefix(PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `PrefixKey` | `String` | ✓ |  |

### `ClearFormValuePrefix(PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `PrefixKey` | `String` | ✓ |  |

### `ClearIntListPrefix(PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `PrefixKey` | `String` | ✓ |  |

### `ClearIntValuePrefix(PrefixKey) → Int`

**Flags:** Native Global

Clears each type of of any KeyName that starts with a given string prefix on all objects.
  Returns the number of values/lists that were unset.

  PrefixKey: The string a KeyName must start with to be cleared. Cannot be empty.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `PrefixKey` | `String` | ✓ |  |

### `ClearObjFloatListPrefix(ObjKey, PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `PrefixKey` | `String` | ✓ |  |

### `ClearObjFloatValuePrefix(ObjKey, PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `PrefixKey` | `String` | ✓ |  |

### `ClearObjFormListPrefix(ObjKey, PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `PrefixKey` | `String` | ✓ |  |

### `ClearObjFormValuePrefix(ObjKey, PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `PrefixKey` | `String` | ✓ |  |

### `ClearObjIntListPrefix(ObjKey, PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `PrefixKey` | `String` | ✓ |  |

### `ClearObjIntValuePrefix(ObjKey, PrefixKey) → Int`

**Flags:** Native Global

Clears each type of of any KeyName that starts with a given string prefix on specific objects.
  Returns the number of values/lists that were unset.

  ObjKey: form to perform the prefix clear on.
  PrefixKey: The string a KeyName must start with to be cleared. Cannot be empty.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `PrefixKey` | `String` | ✓ |  |

### `ClearObjStringListPrefix(ObjKey, PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `PrefixKey` | `String` | ✓ |  |

### `ClearObjStringValuePrefix(ObjKey, PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `PrefixKey` | `String` | ✓ |  |

### `ClearStringListPrefix(PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `PrefixKey` | `String` | ✓ |  |

### `ClearStringValuePrefix(PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `PrefixKey` | `String` | ✓ |  |

### `CountAllObjPrefix(ObjKey, PrefixKey) → Int`

**Flags:** Native Global

Performs all of the above prefix counts in one go.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `PrefixKey` | `String` | ✓ |  |

### `CountAllPrefix(PrefixKey) → Int`

**Flags:** Native Global

Performs all of the above prefix counts in one go.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `PrefixKey` | `String` | ✓ |  |

### `CountFloatListPrefix(PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `PrefixKey` | `String` | ✓ |  |

### `CountFloatValuePrefix(PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `PrefixKey` | `String` | ✓ |  |

### `CountFormListPrefix(PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `PrefixKey` | `String` | ✓ |  |

### `CountFormValuePrefix(PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `PrefixKey` | `String` | ✓ |  |

### `CountIntListPrefix(PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `PrefixKey` | `String` | ✓ |  |

### `CountIntValuePrefix(PrefixKey) → Int`

**Flags:** Native Global

Counts each type of of any KeyName that starts with a given string prefix on all objects.

  PrefixKey: The string a KeyName must start with to be counted. Cannot be empty.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `PrefixKey` | `String` | ✓ |  |

### `CountObjFloatListPrefix(ObjKey, PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `PrefixKey` | `String` | ✓ |  |

### `CountObjFloatValuePrefix(ObjKey, PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `PrefixKey` | `String` | ✓ |  |

### `CountObjFormListPrefix(ObjKey, PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `PrefixKey` | `String` | ✓ |  |

### `CountObjFormValuePrefix(ObjKey, PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `PrefixKey` | `String` | ✓ |  |

### `CountObjIntListPrefix(ObjKey, PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `PrefixKey` | `String` | ✓ |  |

### `CountObjIntValuePrefix(ObjKey, PrefixKey) → Int`

**Flags:** Native Global

Counts each type of of any KeyName that starts with a given string prefix on all objects.

  ObjKey: form to perform the prefix count on.
  PrefixKey: The string a KeyName must start with to be counted. Cannot be empty.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `PrefixKey` | `String` | ✓ |  |

### `CountObjStringListPrefix(ObjKey, PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `PrefixKey` | `String` | ✓ |  |

### `CountObjStringValuePrefix(ObjKey, PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `PrefixKey` | `String` | ✓ |  |

### `CountStringListPrefix(PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `PrefixKey` | `String` | ✓ |  |

### `CountStringValuePrefix(PrefixKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `PrefixKey` | `String` | ✓ |  |

### `debug_AllFloatListObjs() → Form[]`

**Flags:** Native Global

### `debug_AllFloatObjs() → Form[]`

**Flags:** Native Global

### `debug_AllFormListObjs() → Form[]`

**Flags:** Native Global

### `debug_AllFormObjs() → Form[]`

**Flags:** Native Global

### `debug_AllIntListObjs() → Form[]`

**Flags:** Native Global

### `debug_AllIntObjs() → Form[]`

**Flags:** Native Global

### `debug_AllObjFloatKeys(ObjKey) → String[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |

### `debug_AllObjFloatListKeys(ObjKey) → String[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |

### `debug_AllObjFormKeys(ObjKey) → String[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |

### `debug_AllObjFormListKeys(ObjKey) → String[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |

### `debug_AllObjIntKeys(ObjKey) → String[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |

### `debug_AllObjIntListKeys(ObjKey) → String[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |

### `debug_AllObjStringKeys(ObjKey) → String[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |

### `debug_AllObjStringListKeys(ObjKey) → String[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |

### `debug_AllStringListObjs() → Form[]`

**Flags:** Native Global

### `debug_AllStringObjs() → Form[]`

**Flags:** Native Global

### `debug_Cleanup() → Int`

**Flags:** Native Global

### `debug_DeleteAllValues()`

**Flags:** Native Global

### `debug_DeleteValues(ObjKey)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |

### `debug_FileDeleteAllValues()`

**Flags:** Global

### `debug_FileGetFloatKey(index) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |

### `debug_FileGetFloatKeysCount() → Int`

**Flags:** Global

### `debug_FileGetFloatListKey(index) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |

### `debug_FileGetFloatListKeysCount() → Int`

**Flags:** Global

### `debug_FileGetIntKey(index) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |

### `debug_FileGetIntKeysCount() → Int`

**Flags:** Global

Currently no longer implemented, unknown if/when they will return.

### `debug_FileGetIntListKey(index) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |

### `debug_FileGetIntListKeysCount() → Int`

**Flags:** Global

### `debug_FileGetStringKey(index) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |

### `debug_FileGetStringKeysCount() → Int`

**Flags:** Global

### `debug_FileGetStringListKey(index) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |

### `debug_FileGetStringListKeysCount() → Int`

**Flags:** Global

### `debug_GetFloatKey(ObjKey, index) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `debug_GetFloatKeysCount(ObjKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |

### `debug_GetFloatListKey(ObjKey, index) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `debug_GetFloatListKeysCount(ObjKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |

### `debug_GetFloatListObject(index) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |

### `debug_GetFloatListObjectCount() → Int`

**Flags:** Native Global

### `debug_GetFloatObject(index) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |

### `debug_GetFloatObjectCount() → Int`

**Flags:** Native Global

### `debug_GetFormKey(ObjKey, index) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `debug_GetFormKeysCount(ObjKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |

### `debug_GetFormListKey(ObjKey, index) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `debug_GetFormListKeysCount(ObjKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |

### `debug_GetFormListObject(index) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |

### `debug_GetFormListObjectCount() → Int`

**Flags:** Native Global

### `debug_GetFormObject(index) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |

### `debug_GetFormObjectCount() → Int`

**Flags:** Native Global

### `debug_GetIntKey(ObjKey, index) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `debug_GetIntKeysCount(ObjKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |

### `debug_GetIntListKey(ObjKey, index) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `debug_GetIntListKeysCount(ObjKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |

### `debug_GetIntListObject(index) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |

### `debug_GetIntListObjectCount() → Int`

**Flags:** Native Global

### `debug_GetIntObject(index) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |

### `debug_GetIntObjectCount() → Int`

**Flags:** Native Global

### `debug_GetStringKey(ObjKey, index) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `debug_GetStringKeysCount(ObjKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |

### `debug_GetStringListKey(ObjKey, index) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `debug_GetStringListKeysCount(ObjKey) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |

### `debug_GetStringListObject(index) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |

### `debug_GetStringListObjectCount() → Int`

**Flags:** Native Global

### `debug_GetStringObject(index) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |

### `debug_GetStringObjectCount() → Int`

**Flags:** Native Global

### `debug_SaveFile()`

**Flags:** Global

### `debug_SetDebugMode(enabled)`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `enabled` | `Bool` | ✓ |  |

### `ExportFile(fileName, restrictKey, restrictType, restrictForm, restrictGlobal, keyContains, append) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fileName` | `String` | ✓ |  |
| `restrictKey` | `String` |  | `""` |
| `restrictType` | `Int` |  | `-1` |
| `restrictForm` | `Form` |  |  |
| `restrictGlobal` | `Bool` |  | `false` |
| `keyContains` | `Bool` |  | `false` |
| `append` | `Bool` |  | `true` |

### `FileAdjustFloatValue(KeyName, amount) → Float`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `amount` | `Float` | ✓ |  |

### `FileAdjustIntValue(KeyName, amount) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `amount` | `Int` | ✓ |  |

### `FileFloatListAdd(KeyName, value, allowDuplicate) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |
| `allowDuplicate` | `Bool` |  | `true` |

### `FileFloatListAdjust(KeyName, index, amount) → Float`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `amount` | `Float` | ✓ |  |

### `FileFloatListClear(KeyName) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |

### `FileFloatListCopy(KeyName, copy) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `copy` | `Float[]` | ✓ |  |

### `FileFloatListCount(KeyName) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |

### `FileFloatListFind(KeyName, value) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |

### `FileFloatListGet(KeyName, index) → Float`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `FileFloatListHas(KeyName, value) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |

### `FileFloatListInsert(KeyName, index, value) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `value` | `Float` | ✓ |  |

### `FileFloatListRemove(KeyName, value, allInstances) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |
| `allInstances` | `Bool` |  | `false` |

### `FileFloatListRemoveAt(KeyName, index) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `FileFloatListResize(KeyName, toLength, filler) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `toLength` | `Int` | ✓ |  |
| `filler` | `Float` |  | `0` |

### `FileFloatListSet(KeyName, index, value) → Float`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `value` | `Float` | ✓ |  |

### `FileFloatListSlice(KeyName, slice, startIndex)`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `slice` | `Float[]` | ✓ |  |
| `startIndex` | `Int` |  | `0` |

### `FileFormListAdd(KeyName, value, allowDuplicate) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `value` | `Form` | ✓ |  |
| `allowDuplicate` | `Bool` |  | `true` |

### `FileFormListClear(KeyName) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |

### `FileFormListCopy(KeyName, copy) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `copy` | `Form[]` | ✓ |  |

### `FileFormListCount(KeyName) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |

### `FileFormListFind(KeyName, value) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `value` | `Form` | ✓ |  |

### `FileFormListGet(KeyName, index) → Form`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `FileFormListHas(KeyName, value) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `value` | `Form` | ✓ |  |

### `FileFormListInsert(KeyName, index, value) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `value` | `Form` | ✓ |  |

### `FileFormListRemove(KeyName, value, allInstances) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `value` | `Form` | ✓ |  |
| `allInstances` | `Bool` |  | `false` |

### `FileFormListRemoveAt(KeyName, index) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `FileFormListResize(KeyName, toLength, filler) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `toLength` | `Int` | ✓ |  |
| `filler` | `Form` |  |  |

### `FileFormListSet(KeyName, index, value) → Form`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `value` | `Form` | ✓ |  |

### `FileFormListSlice(KeyName, slice, startIndex)`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `slice` | `Form[]` | ✓ |  |
| `startIndex` | `Int` |  | `0` |

### `FileGetFloatValue(KeyName, missing) → Float`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `missing` | `Float` |  | `0` |

### `FileGetFormValue(KeyName, missing) → Form`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `missing` | `Form` |  |  |

### `FileGetIntValue(KeyName, missing) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `missing` | `Int` |  | `0` |

### `FileGetStringValue(KeyName, missing) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `missing` | `String` |  | `""` |

### `FileHasFloatValue(KeyName) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |

### `FileHasFormValue(KeyName) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |

### `FileHasIntValue(KeyName) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |

### `FileHasStringValue(KeyName) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |

### `FileIntListAdd(KeyName, value, allowDuplicate) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |
| `allowDuplicate` | `Bool` |  | `true` |

### `FileIntListAdjust(KeyName, index, amount) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `amount` | `Int` | ✓ |  |

### `FileIntListClear(KeyName) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |

### `FileIntListCopy(KeyName, copy) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `copy` | `Int[]` | ✓ |  |

### `FileIntListCount(KeyName) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |

### `FileIntListFind(KeyName, value) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |

### `FileIntListGet(KeyName, index) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `FileIntListHas(KeyName, value) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |

### `FileIntListInsert(KeyName, index, value) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `value` | `Int` | ✓ |  |

### `FileIntListRemove(KeyName, value, allInstances) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |
| `allInstances` | `Bool` |  | `false` |

### `FileIntListRemoveAt(KeyName, index) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `FileIntListResize(KeyName, toLength, filler) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `toLength` | `Int` | ✓ |  |
| `filler` | `Int` |  | `0` |

### `FileIntListSet(KeyName, index, value) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `value` | `Int` | ✓ |  |

### `FileIntListSlice(KeyName, slice, startIndex)`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `slice` | `Int[]` | ✓ |  |
| `startIndex` | `Int` |  | `0` |

### `FileSetFloatValue(KeyName, value) → Float`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |

### `FileSetFormValue(KeyName, value) → form`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `value` | `Form` | ✓ |  |

### `FileSetIntValue(KeyName, value) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |

### `FileSetStringValue(KeyName, value) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `value` | `String` | ✓ |  |

### `FileStringListAdd(KeyName, value, allowDuplicate) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `value` | `String` | ✓ |  |
| `allowDuplicate` | `Bool` |  | `true` |

### `FileStringListClear(KeyName) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |

### `FileStringListCopy(KeyName, copy) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `copy` | `String[]` | ✓ |  |

### `FileStringListCount(KeyName) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |

### `FileStringListFind(KeyName, value) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `value` | `String` | ✓ |  |

### `FileStringListGet(KeyName, index) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `FileStringListHas(KeyName, value) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `value` | `String` | ✓ |  |

### `FileStringListInsert(KeyName, index, value) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `value` | `String` | ✓ |  |

### `FileStringListRemove(KeyName, value, allInstances) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `value` | `String` | ✓ |  |
| `allInstances` | `Bool` |  | `false` |

### `FileStringListRemoveAt(KeyName, index) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `FileStringListResize(KeyName, toLength, filler) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `toLength` | `Int` | ✓ |  |
| `filler` | `String` |  | `""` |

### `FileStringListSet(KeyName, index, value) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `value` | `String` | ✓ |  |

### `FileStringListSlice(KeyName, slice, startIndex)`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |
| `slice` | `String[]` | ✓ |  |
| `startIndex` | `Int` |  | `0` |

### `FileUnsetFloatValue(KeyName) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |

### `FileUnsetFormValue(KeyName) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |

### `FileUnsetIntValue(KeyName) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |

### `FileUnsetStringValue(KeyName) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `KeyName` | `String` | ✓ |  |

### `FloatListAdd(ObjKey, KeyName, value, allowDuplicate) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |
| `allowDuplicate` | `Bool` |  | `true` |

### `FloatListAdjust(ObjKey, KeyName, index, amount) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `amount` | `Float` | ✓ |  |

### `FloatListClear(ObjKey, KeyName) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `FloatListCopy(ObjKey, KeyName, copy) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `copy` | `Float[]` | ✓ |  |

### `FloatListCount(ObjKey, KeyName) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `FloatListCountValue(ObjKey, KeyName, value, exclude) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |
| `exclude` | `Bool` |  | `false` |

### `FloatListFind(ObjKey, KeyName, value) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |

### `FloatListGet(ObjKey, KeyName, index) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `FloatListHas(ObjKey, KeyName, value) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |

### `FloatListInsert(ObjKey, KeyName, index, value) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `value` | `Float` | ✓ |  |

### `FloatListPluck(ObjKey, KeyName, index, missing) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `missing` | `Float` | ✓ |  |

### `FloatListPop(ObjKey, KeyName) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `FloatListRandom(ObjKey, KeyName) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `FloatListRemove(ObjKey, KeyName, value, allInstances) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |
| `allInstances` | `Bool` |  | `false` |

### `FloatListRemoveAt(ObjKey, KeyName, index) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `FloatListResize(ObjKey, KeyName, toLength, filler) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `toLength` | `Int` | ✓ |  |
| `filler` | `Float` |  | `0` |

### `FloatListSet(ObjKey, KeyName, index, value) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `value` | `Float` | ✓ |  |

### `FloatListShift(ObjKey, KeyName) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `FloatListSlice(ObjKey, KeyName, slice, startIndex)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `slice` | `Float[]` | ✓ |  |
| `startIndex` | `Int` |  | `0` |

### `FloatListSort(ObjKey, KeyName)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `FloatListToArray(ObjKey, KeyName) → Float[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `FormListAdd(ObjKey, KeyName, value, allowDuplicate) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `Form` | ✓ |  |
| `allowDuplicate` | `Bool` |  | `true` |

### `FormListClear(ObjKey, KeyName) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `FormListCopy(ObjKey, KeyName, copy) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `copy` | `Form[]` | ✓ |  |

### `FormListCount(ObjKey, KeyName) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `FormListCountValue(ObjKey, KeyName, value, exclude) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `Form` | ✓ |  |
| `exclude` | `Bool` |  | `false` |

### `FormListFilterByType(ObjKey, KeyName, FormTypeID, ReturnMatching) → Form[]`

**Flags:** Global

Convenience version of FormListFilterByTypes() for when only getting a single type.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `FormTypeID` | `Int` | ✓ |  |
| `ReturnMatching` | `Bool` |  | `true` |

### `FormListFilterByTypes(ObjKey, KeyName, FormTypeIDs, ReturnMatching) → Form[]`

**Flags:** Native Global

Returns array of forms from list that have (or optionally don't have) the specified form types.
  For valid list of form types, see FormType.psc or http://www.creationkit.com/GetType_-_Form

  ObjKey: form to find value on. Set none to find global list value.
  KeyName: name of list.
  FormTypeIDs[]: The int papyrus array with all the form types you wish to filter for
  [optional] ReturnMatching: By default, TRUE, the output Form[] array will contain forms from list that match the form types
                             If set to FALSE, inverts the resulting array with forms that have a type that DO NOT match.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `FormTypeIDs` | `Int[]` | ✓ |  |
| `ReturnMatching` | `Bool` |  | `true` |

### `FormListFind(ObjKey, KeyName, value) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `Form` | ✓ |  |

### `FormListGet(ObjKey, KeyName, index) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `FormListHas(ObjKey, KeyName, value) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `Form` | ✓ |  |

### `FormListInsert(ObjKey, KeyName, index, value) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `value` | `Form` | ✓ |  |

### `FormListPluck(ObjKey, KeyName, index, missing) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `missing` | `Form` | ✓ |  |

### `FormListPop(ObjKey, KeyName) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `FormListRandom(ObjKey, KeyName) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `FormListRemove(ObjKey, KeyName, value, allInstances) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `Form` | ✓ |  |
| `allInstances` | `Bool` |  | `false` |

### `FormListRemoveAt(ObjKey, KeyName, index) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `FormListResize(ObjKey, KeyName, toLength, filler) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `toLength` | `Int` | ✓ |  |
| `filler` | `Form` |  |  |

### `FormListSet(ObjKey, KeyName, index, value) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `value` | `Form` | ✓ |  |

### `FormListShift(ObjKey, KeyName) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `FormListSlice(ObjKey, KeyName, slice, startIndex)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `slice` | `Form[]` | ✓ |  |
| `startIndex` | `Int` |  | `0` |

### `FormListSort(ObjKey, KeyName)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `FormListToArray(ObjKey, KeyName) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `GetFloatValue(ObjKey, KeyName, missing) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `missing` | `Float` |  | `0` |

### `GetFormValue(ObjKey, KeyName, missing) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `missing` | `Form` |  |  |

### `GetIntValue(ObjKey, KeyName, missing) → Int`

**Flags:** Native Global

Get previously saved int/float/string/Form value on form or globally.

  ObjKey: form to get from. Set none to get global value.
  KeyName: name of value.
  [optional] missing: if value has not been set, return this value instead.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `missing` | `Int` |  | `0` |

### `GetStringValue(ObjKey, KeyName, missing) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `missing` | `String` |  | `""` |

### `HasFloatValue(ObjKey, KeyName) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `HasFormValue(ObjKey, KeyName) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `HasIntValue(ObjKey, KeyName) → Bool`

**Flags:** Native Global

Check if int/float/string/Form value has been set on form or globally.

  ObjKey: form to check on. Set none to check global value.
  KeyName: name of value.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `HasStringValue(ObjKey, KeyName) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `ImportFile(fileName, restrictKey, restrictType, restrictForm, restrictGlobal, keyContains) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fileName` | `String` | ✓ |  |
| `restrictKey` | `String` |  | `""` |
| `restrictType` | `Int` |  | `-1` |
| `restrictForm` | `Form` |  |  |
| `restrictGlobal` | `Bool` |  | `false` |
| `keyContains` | `Bool` |  | `false` |

### `IntListAdd(ObjKey, KeyName, value, allowDuplicate) → Int`

**Flags:** Native Global

Add an int/float/string/Form to a list on form or globally and return
  the value's new index. Index can be -1 if we were unable to add
  the value.

  ObjKey: form to add to. Set none to add global value.
  KeyName: name of value.
  value: value to add.
  [optional] allowDuplicate: allow adding value to list if this value already exists in the list.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |
| `allowDuplicate` | `Bool` |  | `true` |

### `IntListAdjust(ObjKey, KeyName, index, amount) → Int`

**Flags:** Native Global

Adjust the existing value of a list by the given amount.

  ObjKey: form to set value on. Set none to set global list value.
  KeyName: name of list.
  index: index of value in the list.
  amount: +/- the amount to adjust the lists current index value by.

  returns 0 if index does not exists

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `amount` | `Int` | ✓ |  |

### `IntListClear(ObjKey, KeyName) → Int`

**Flags:** Native Global

Clear a list of values (unset) on an form or globally and
  return the previous size of list.

  ObjKey: form to clear on. Set none to clear global list.
  KeyName: name of list.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `IntListCopy(ObjKey, KeyName, copy) → Bool`

**Flags:** Native Global

Creates a copy of array on the given storage list at the given object+key,
  overwriting any list that might already exists.

  Returns true on success.

  ObjKey: form to find value on. Set none to find global list value.
  KeyName: name of list.
  copy[]: The papyrus array with the content you wish to copy over into StorageUtil
  [optional] filler: When adding empty elements to the list this will be used as the default value

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `copy` | `Int[]` | ✓ |  |

### `IntListCount(ObjKey, KeyName) → Int`

**Flags:** Native Global

Get size of a list on form or globally.

  ObjKey: form to check on. Set none to check global list.
  KeyName: name of list.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `IntListCountValue(ObjKey, KeyName, value, exclude) → Int`

**Flags:** Native Global

Get the number of occurrences of a specific value within a list.

  ObjKey: form to check on. Set none to check global list.
  KeyName: name of list.
  value: value to look for.
  [optional] exclude: if true, function will return number of elements NOT equal to value.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |
| `exclude` | `Bool` |  | `false` |

### `IntListFind(ObjKey, KeyName, value) → Int`

**Flags:** Native Global

Find a value in list on form or globally and return its
  index or -1 if value was not found.

  ObjKey: form to find value on. Set none to find global list value.
  KeyName: name of list.
  value: value to search.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |

### `IntListGet(ObjKey, KeyName, index) → Int`

**Flags:** Native Global

Get a value from list by index on form or globally.
  This will return 0 as value if there was a problem.

  ObjKey: form to get value on. Set none to get global list value.
  KeyName: name of list.
  index: index of value in the list.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `IntListHas(ObjKey, KeyName, value) → Bool`

**Flags:** Native Global

Find if a value in list on form or globally exists, true if it exists,
  false if it doesn't.

  ObjKey: form to find value on. Set none to find global list value.
  KeyName: name of list.
  value: value to search.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |

### `IntListInsert(ObjKey, KeyName, index, value) → Bool`

**Flags:** Native Global

Insert an int/float/string/Form to a list on form or globally and return
  if successful.

  ObjKey: form to add to. Set none to add global value.
  KeyName: name of value.
  index: position in list to put the value. 0 is first entry in list.
  value: value to add.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `value` | `Int` | ✓ |  |

### `IntListPluck(ObjKey, KeyName, index, missing) → Int`

**Flags:** Native Global

Plucks a value from list by index on form or globally.
  The index is removed from the list's storage after returning it's value.

  ObjKey: form to pluck value from. Set none to get global list value.
  KeyName: name of list.
  index: index of value in the list.
  [optional] missing: if index has not been set, return this value instead.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `missing` | `Int` | ✓ |  |

### `IntListPop(ObjKey, KeyName) → Int`

**Flags:** Native Global

Gets the value of the very last element in a list, and subsequently removes the index afterward.

  ObjKey: form to pop value from. Set none to get global list value.
  KeyName: name of list to pop off it's last value.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `IntListRandom(ObjKey, KeyName) → Int`

**Flags:** Native Global

Outputs a randomly selected value from the given list's elements using mt19937.

  Returns the random elements value. If list is empty or doesn't exist, returns default null value.

  ObjKey: form to find value on. Set none to find global list value.
  KeyName: name of list.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `IntListRemove(ObjKey, KeyName, value, allInstances) → Int`

**Flags:** Native Global

Remove a previously added int/float/string/Form value from a list on form
  or globally and return how many instances of this value were removed.

  ObjKey: form to remove from. Set none to remove global value.
  KeyName: name of value.
  value: value to remove.
  [optional] allowInstances: remove all instances of this value in a list.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |
| `allInstances` | `Bool` |  | `false` |

### `IntListRemoveAt(ObjKey, KeyName, index) → Bool`

**Flags:** Native Global

Remove a value from list by index on form or globally and
  return if we were successful in doing so.

  ObjKey: form to remove from. Set none to remove global value.
  KeyName: name of list.
  index: index of value in the list.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `IntListResize(ObjKey, KeyName, toLength, filler) → Int`

**Flags:** Native Global

Sizes the given list to a set number of elements. If the list exists already it will be truncated
  when given fewer elements, or resized to the appropriate length with the filler argument being used as
  the default values

  Returns the number of elements truncated (signed) or added (unsigned) onto the list.

  ObjKey: form to find value on. Set none to find global list value.
  KeyName: name of list.
  toLength: The size you want to change the list to. Max length when using this function is 500.
  [optional] filler: When adding empty elements to the list this will be used as the default value

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `toLength` | `Int` | ✓ |  |
| `filler` | `Int` |  | `0` |

### `IntListSet(ObjKey, KeyName, index, value) → Int`

**Flags:** Native Global

Set a value in list by index on form or globally.
  This will return the previous value or 0 if there was a problem.

  ObjKey: form to set value on. Set none to set global list value.
  KeyName: name of list.
  index: index of value in the list.
  value: value to set to.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `value` | `Int` | ✓ |  |

### `IntListShift(ObjKey, KeyName) → Int`

**Flags:** Native Global

Gets the value of the very first element in a list, and subsequently removes the index afterward.

  ObjKey: form to shift value from. Set none to get global list value.
  KeyName: name of list to shift it's first value from.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `IntListSlice(ObjKey, KeyName, slice, startIndex)`

**Flags:** Native Global

Fills the given input array with the values of the list on form or globally,
  will fill the array until either the array or list runs out of valid indexes

  ObjKey: form to find value on. Set none to find global list value.
  KeyName: name of list.
  slice[]: an initialized array set to the slice size you want, i.e. int[] slice = new int[10]
  [optional] startIndex: the starting list index you want to start filling your slice array with

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `slice` | `Int[]` | ✓ |  |
| `startIndex` | `Int` |  | `0` |

### `IntListSort(ObjKey, KeyName)`

**Flags:** Native Global

Sort an int/float/string/Form list by values in ascending order.

  ObjKey: form to sort on. Set none for global value.
  KeyName: name of value.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `IntListToArray(ObjKey, KeyName) → Int[]`

**Flags:** Native Global

Outputs the values currently stored by the given object+key.

  Returns a new array containing the values.

  ObjKey: form to find value on. Set none to find global list value.
  KeyName: name of list.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `PluckFloatValue(ObjKey, KeyName, missing) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `missing` | `Float` |  | `0` |

### `PluckFormValue(ObjKey, KeyName, missing) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `missing` | `Form` |  |  |

### `PluckIntValue(ObjKey, KeyName, missing) → Int`

**Flags:** Native Global

Plucks a previously saved int/float/string/Form value on form or globally.
  Returning the value stored, then removing it from storage.

  ObjKey: form to pluck from. Set none to get global value.
  KeyName: name of value.
  [optional] missing: if value has not been set, return this value instead.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `missing` | `Int` |  | `0` |

### `PluckStringValue(ObjKey, KeyName, missing) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `missing` | `String` |  | `""` |

### `SetFloatValue(ObjKey, KeyName, value) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |

### `SetFormValue(ObjKey, KeyName, value) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `Form` | ✓ |  |

### `SetIntValue(ObjKey, KeyName, value) → Int`

**Flags:** Native Global

Set int/float/string/Form value globally or on any form by name and return
  the value passed, or as uninitialized variable if invalid keys given.

  ObjKey: form to save on. Set none to save globally.
  KeyName: name of value.
  value: value to set to the given keys. If zero, empty, or none are given, the key will be unset.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |

### `SetStringValue(ObjKey, KeyName, value) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `String` | ✓ |  |

### `StringListAdd(ObjKey, KeyName, value, allowDuplicate) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `String` | ✓ |  |
| `allowDuplicate` | `Bool` |  | `true` |

### `StringListClear(ObjKey, KeyName) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `StringListCopy(ObjKey, KeyName, copy) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `copy` | `String[]` | ✓ |  |

### `StringListCount(ObjKey, KeyName) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `StringListCountValue(ObjKey, KeyName, value, exclude) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `String` | ✓ |  |
| `exclude` | `Bool` |  | `false` |

### `StringListFind(ObjKey, KeyName, value) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `String` | ✓ |  |

### `StringListGet(ObjKey, KeyName, index) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `StringListHas(ObjKey, KeyName, value) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `String` | ✓ |  |

### `StringListInsert(ObjKey, KeyName, index, value) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `value` | `String` | ✓ |  |

### `StringListPluck(ObjKey, KeyName, index, missing) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `missing` | `String` | ✓ |  |

### `StringListPop(ObjKey, KeyName) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `StringListRandom(ObjKey, KeyName) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `StringListRemove(ObjKey, KeyName, value, allInstances) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `value` | `String` | ✓ |  |
| `allInstances` | `Bool` |  | `false` |

### `StringListRemoveAt(ObjKey, KeyName, index) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `StringListResize(ObjKey, KeyName, toLength, filler) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `toLength` | `Int` | ✓ |  |
| `filler` | `String` |  | `""` |

### `StringListSet(ObjKey, KeyName, index, value) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `value` | `String` | ✓ |  |

### `StringListShift(ObjKey, KeyName) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `StringListSlice(ObjKey, KeyName, slice, startIndex)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
| `slice` | `String[]` | ✓ |  |
| `startIndex` | `Int` |  | `0` |

### `StringListSort(ObjKey, KeyName)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `StringListToArray(ObjKey, KeyName) → String[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `UnsetFloatValue(ObjKey, KeyName) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `UnsetFormValue(ObjKey, KeyName) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `UnsetIntValue(ObjKey, KeyName) → Bool`

**Flags:** Native Global

Remove a previously set int/float/string/Form value on an form or globally and
  return if successful. This will return false if value didn't exist.

  ObjKey: form to remove from. Set none to remove global value.
  KeyName: name of value.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |

### `UnsetStringValue(ObjKey, KeyName) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ObjKey` | `Form` | ✓ |  |
| `KeyName` | `String` | ✓ |  |
