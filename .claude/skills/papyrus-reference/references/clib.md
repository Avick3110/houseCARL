# `cArrayCreateBase`

**Source:** `clib` (cLib-Papyrus Function Library) • **Flags:** Hidden

---

## Global Functions

### `cArrayCreateActor(indices, filler, usePapUtil, outputTrace, useConsoleUtil) → Actor[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Actor` |  |  |
| `usePapUtil` | `Bool` |  | `true` |
| `outputTrace` | `Bool` |  | `true` |
| `useConsoleUtil` | `Bool` |  | `true` |

### `cArrayCreateAlias(indices, filler, useSKSE, outputTrace, useConsoleUtil) → Alias[]`

**Flags:** Global

Requirements: None, SKSE:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Alias` |  |  |
| `useSKSE` | `Bool` |  | `true` |
| `outputTrace` | `Bool` |  | `true` |
| `useConsoleUtil` | `Bool` |  | `true` |

### `cArrayCreateBool(indices, filler, useSKSE, outputTrace, useConsoleUtil) → Bool[]`

**Flags:** Global

Requirements: None, SKSE:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Bool` |  | `false` |
| `useSKSE` | `Bool` |  | `true` |
| `outputTrace` | `Bool` |  | `true` |
| `useConsoleUtil` | `Bool` |  | `true` |

### `cArrayCreateFloat(indices, filler, useSKSE, outputTrace, useConsoleUtil) → Float[]`

**Flags:** Global

Requirements: None, SKSE:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Float` |  | `0` |
| `useSKSE` | `Bool` |  | `true` |
| `outputTrace` | `Bool` |  | `true` |
| `useConsoleUtil` | `Bool` |  | `true` |

### `cArrayCreateForm(indices, filler, useSKSE, outputTrace, useConsoleUtil) → Form[]`

**Flags:** Global

Requirements: None, SKSE:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Form` |  |  |
| `useSKSE` | `Bool` |  | `true` |
| `outputTrace` | `Bool` |  | `true` |
| `useConsoleUtil` | `Bool` |  | `true` |

### `cArrayCreateInt(indices, filler, useSKSE, outputTrace, useConsoleUtil) → Int[]`

**Flags:** Global

Requirements: None, SKSE:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Int` |  | `0` |
| `useSKSE` | `Bool` |  | `true` |
| `outputTrace` | `Bool` |  | `true` |
| `useConsoleUtil` | `Bool` |  | `true` |

### `cArrayCreateObjectReference(indices, filler, usePapUtil, outputTrace, useConsoleUtil) → ObjectReference[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `ObjectReference` |  |  |
| `usePapUtil` | `Bool` |  | `true` |
| `outputTrace` | `Bool` |  | `true` |
| `useConsoleUtil` | `Bool` |  | `true` |

### `cArrayCreateObjRef(indices, filler) → ObjectReference[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `ObjectReference` |  |  |

### `cArrayCreateString(indices, filler, useSKSE, outputTrace, useConsoleUtil) → String[]`

**Flags:** Global

Requirements: None, SKSE:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `String` |  | `""` |
| `useSKSE` | `Bool` |  | `true` |
| `outputTrace` | `Bool` |  | `true` |
| `useConsoleUtil` | `Bool` |  | `true` |


---

## `cGetCell`

**Source:** `clib` (cLib-Papyrus Function Library) • **Flags:** Hidden

---

## Global Functions

### `cGetCellFormIDFromCoords(ckXVar, ckYVar, xVar, yVar) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ckXVar` | `Int` | ✓ |  |
| `ckYVar` | `Int` | ✓ |  |
| `xVar` | `Float` |  | `0` |
| `yVar` | `Float` |  | `0` |

### `cGetCellFromCoords(ckXVar, ckYVar, xVar, yVar) → Form`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ckXVar` | `Int` | ✓ |  |
| `ckYVar` | `Int` | ✓ |  |
| `xVar` | `Float` |  | `0` |
| `yVar` | `Float` |  | `0` |

### `cGetCKCoordsFromXY(xVar, yVar, aObjectRef) → Int[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `xVar` | `Float` | ✓ |  |
| `yVar` | `Float` | ✓ |  |
| `aObjectRef` | `ObjectReference` |  |  |

### `cGetScriptName() → String`

**Flags:** Global

### `cIsBetweenFloat(aValue, minV, maxV) → Bool`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aValue` | `Float` | ✓ |  |
| `minV` | `Float` | ✓ |  |
| `maxV` | `Float` | ✓ |  |

### `cIsBetweenInt(aValue, minV, maxV) → Bool`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aValue` | `Int` | ✓ |  |
| `minV` | `Int` | ✓ |  |
| `maxV` | `Int` | ✓ |  |

### `clibTrace(msg, errorLevel, condition)`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `msg` | `String` | ✓ |  |
| `errorLevel` | `Int` | ✓ |  |
| `condition` | `Bool` |  | `true` |


---

## `clib`

**Source:** `clib` (cLib-Papyrus Function Library) • **Flags:** Hidden

**Imports:** `cArrayCreateBase`

---

## Global Functions

### `cArePluginsInstalled(listOfPlugins) → Bool[]`

**Flags:** Global

Requirements: SKSE

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `listOfPlugins` | `String[]` | ✓ |  |

### `cArrayActorToActorBase(aArray) → Form[]`

**Flags:** Global

Requirements:None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Actor[]` | ✓ |  |

### `cArrayAddLVLI(aLeveledList, aArray, level, count) → Int`

**Flags:** Global

Requirements: None

====== Leveled Item Lists
 all items in the form must have the same level and count

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aLeveledList` | `LeveledItem` | ✓ |  |
| `aArray` | `Form[]` | ✓ |  |
| `level` | `Int` | ✓ |  |
| `count` | `Int` | ✓ |  |

### `cArrayAllAddLVLI(aLeveledList, aArray, levels, counts) → Int`

**Flags:** Global

Requirements: None

accepts arrays for all three arguments, forms, levelss, countss
Note: the levels and counts arrays use the cWrapInt function. This allows the following:
  A 21 index form array and levels and counts arrays of 7 forms each:
  Form[0] -> levels[0] -> counts[0]
  ...
  Form[6] -> levels[6] -> counts[6]
  Form[7] -> levels[0] -> counts[0]
  ...
  Form[13] -> levels[6] -> counts[6]
  Form[14] -> levels[0] -> counts[0]
If all arrays are equal in size it will of course proceed in normal fashion

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aLeveledList` | `LeveledItem` | ✓ |  |
| `aArray` | `Form[]` | ✓ |  |
| `levels` | `Int[]` | ✓ |  |
| `counts` | `Int[]` | ✓ |  |

### `cArrayAnalyzeFloat(aArray) → Float[]`

**Flags:** Global

Requirements: None

returns array [0] == smallest value, [1] == its index, [2] == largest value, [3] == its index,
  [4] == array length, [5] == array sum, [6] == array average

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Float[]` | ✓ |  |

### `cArrayAnalyzeInt(aArray) → Int[]`

**Flags:** Global

Requirements: None

returns array [0] == smallest value, [1] == its index, [2] == largest value, [3] == its index,
  [4] == array length, [5] == array sum, [6] == array average, [7] == average remainder (if any)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Int[]` | ✓ |  |

### `cArrayAnalyzeString(aArray) → String[]`

**Flags:** Global

Requirements: None

returns array [0] == smallest value (lex), [1] == its index, [2] == largest value (lex), [3] == its index,
  [4] == array length

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `String[]` | ✓ |  |

### `cArrayASCIIAsBinary() → String[]`

**Flags:** Global

Requirements: None

### `cArrayASCIIAsHex() → String[]`

**Flags:** Global

Requirements: None

### `cArrayASCIIChars() → String[]`

**Flags:** Global

Requirements: None

***CONFIRMED WORKING 21-11-02

### `cArrayAverageFloat(aArray) → Float`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Float[]` | ✓ |  |

### `cArrayAverageInt(aArray) → Int`

**Flags:** Global

Requirements: None

remainder is dropped!

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Int[]` | ✓ |  |

### `cArrayBaseEnchantment(aArray) → Enchantment[]`

**Flags:** Global

Requirements: SKSE

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Enchantment[]` | ✓ |  |

### `cArrayBoolToInt(aArray) → Int[]`

**Flags:** Global

Requirements:None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Int[]` | ✓ |  |

### `cArrayBubbleSortFloat(aArray, invertIt, usePapUtil) → Float[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

>>> Returns new array [cArraySortFloat/Int() faster but no return]

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Float[]` | ✓ |  |
| `invertIt` | `Bool` |  | `false` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayBubbleSortInt(aArray, invertIt, usePapUtil) → Int[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Int[]` | ✓ |  |
| `invertIt` | `Bool` |  | `false` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayBubbleSortString(aArray, invertIt, usePapUtil) → String[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `String[]` | ✓ |  |
| `invertIt` | `Bool` |  | `false` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayCompactActor(aArray, shiftValue) → Actor[]`

**Flags:** Global

Requirements: None

>>> Shift all values that cast to Bool as False to the end

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Actor[]` | ✓ |  |
| `shiftValue` | `Actor` |  |  |

### `cArrayCompactAlias(aArray, shiftValue) → Alias[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Alias[]` | ✓ |  |
| `shiftValue` | `Alias` |  |  |

### `cArrayCompactFloat(aArray, shiftValue) → Float[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Float[]` | ✓ |  |
| `shiftValue` | `Float` |  | `0` |

### `cArrayCompactForm(aArray, shiftValue) → Form[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Form[]` | ✓ |  |
| `shiftValue` | `Form` |  |  |

### `cArrayCompactInt(aArray, shiftValue) → Int[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Int[]` | ✓ |  |
| `shiftValue` | `Int` |  | `0` |

### `cArrayCompactObjRef(aArray, shiftValue) → ObjectReference[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `ObjectReference[]` | ✓ |  |
| `shiftValue` | `ObjectReference` |  |  |

### `cArrayCompactString(aArray, shiftValue) → String[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `String[]` | ✓ |  |
| `shiftValue` | `String` |  | `""` |

### `cArrayCopyActor(aArray) → Actor[]`

**Flags:** Global

Requirements: None

>>> Array copying
--> Returns new array copy

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Actor[]` | ✓ |  |

### `cArrayCopyAlias(aArray) → Alias[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Alias[]` | ✓ |  |

### `cArrayCopyBool(aArray) → Bool[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Bool[]` | ✓ |  |

### `cArrayCopyFloat(aArray) → Float[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Float[]` | ✓ |  |

### `cArrayCopyForm(aArray) → Form[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Form[]` | ✓ |  |

### `cArrayCopyInt(aArray) → Int[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Int[]` | ✓ |  |

### `cArrayCopyObjRef(aArray) → ObjectReference[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `ObjectReference[]` | ✓ |  |

### `cArrayCopyString(aArray) → String[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `String[]` | ✓ |  |

### `cArrayCopyToActor(aArray1, aArray2, filler) → Actor[]`

**Flags:** Global

Requirements: None

just copies one array to another, can be used for arrays of any size whether SKSE is installed or not

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray1` | `Actor[]` | ✓ |  |
| `aArray2` | `Actor[]` | ✓ |  |
| `filler` | `Actor` |  |  |

### `cArrayCopyToAlias(aArray1, aArray2, filler) → Alias[]`

**Flags:** Global

Requirements: None

just copies one array to another, can be used for arrays of any size whether SKSE is installed or not

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray1` | `Alias[]` | ✓ |  |
| `aArray2` | `Alias[]` | ✓ |  |
| `filler` | `Alias` |  |  |

### `cArrayCopyToBool(aArray1, aArray2, filler) → Bool[]`

**Flags:** Global

Requirements: None

just copies one array to another, can be used for arrays of any size whether SKSE is installed or not

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray1` | `Bool[]` | ✓ |  |
| `aArray2` | `Bool[]` | ✓ |  |
| `filler` | `Bool` |  | `false` |

### `cArrayCopyToFloat(aArray1, aArray2, filler) → Float[]`

**Flags:** Global

Requirements: None

just copies one array to another, can be used for arrays of any size whether SKSE is installed or not

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray1` | `Float[]` | ✓ |  |
| `aArray2` | `Float[]` | ✓ |  |
| `filler` | `Float` |  | `0` |

### `cArrayCopyToForm(aArray1, aArray2, filler) → Form[]`

**Flags:** Global

Requirements: None

just copies one array to another, can be used for arrays of any size whether SKSE is installed or not

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray1` | `Form[]` | ✓ |  |
| `aArray2` | `Form[]` | ✓ |  |
| `filler` | `Form` |  |  |

### `cArrayCopyToInt(aArray1, aArray2, filler) → Int[]`

**Flags:** Global

Requirements: None

just copies one array to another, can be used for arrays of any size whether SKSE is installed or not

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray1` | `Int[]` | ✓ |  |
| `aArray2` | `Int[]` | ✓ |  |
| `filler` | `Int` |  | `0` |

### `cArrayCopyToObjRef(aArray1, aArray2, filler) → ObjectReference[]`

**Flags:** Global

Requirements: None

just copies one array to another, can be used for arrays of any size whether SKSE is installed or not

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray1` | `ObjectReference[]` | ✓ |  |
| `aArray2` | `ObjectReference[]` | ✓ |  |
| `filler` | `ObjectReference` |  |  |

### `cArrayCopyToString(aArray1, aArray2, filler) → String[]`

**Flags:** Global

Requirements: None

just copies one array to another, can be used for arrays of any size whether SKSE is installed or not

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray1` | `String[]` | ✓ |  |
| `aArray2` | `String[]` | ✓ |  |
| `filler` | `String` |  | `""` |

### `cArrayCountValueActor(aArray, valueToCount, invertIt, usePapUtil) → Int`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

>>> Tally

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Actor[]` | ✓ |  |
| `valueToCount` | `Actor` |  |  |
| `invertIt` | `Bool` |  | `false` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayCountValueAlias(aArray, valueToCount, invertIt, usePapUtil) → Int`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Alias[]` | ✓ |  |
| `valueToCount` | `Alias` |  |  |
| `invertIt` | `Bool` |  | `false` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayCountValueBool(aArray, valueToCount, invertIt, usePapUtil) → Int`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Bool[]` | ✓ |  |
| `valueToCount` | `Bool` |  | `true` |
| `invertIt` | `Bool` |  | `false` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayCountValueFloat(aArray, valueToCount, invertIt, usePapUtil) → Int`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Float[]` | ✓ |  |
| `valueToCount` | `Float` |  | `0` |
| `invertIt` | `Bool` |  | `false` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayCountValueForm(aArray, valueToCount, invertIt, usePapUtil) → Int`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Form[]` | ✓ |  |
| `valueToCount` | `Form` |  |  |
| `invertIt` | `Bool` |  | `false` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayCountValueFormList(aFormList, valueToCount, invertIt) → Int`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aFormList` | `FormList` | ✓ |  |
| `valueToCount` | `Form` |  |  |
| `invertIt` | `Bool` |  | `false` |

### `cArrayCountValueInt(aArray, valueToCount, invertIt, usePapUtil) → Int`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Int[]` | ✓ |  |
| `valueToCount` | `Int` |  | `0` |
| `invertIt` | `Bool` |  | `false` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayCountValueObjRef(aArray, valueToCount, invertIt, usePapUtil) → Int`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `ObjectReference[]` | ✓ |  |
| `valueToCount` | `ObjectReference` |  |  |
| `invertIt` | `Bool` |  | `false` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayCountValueString(aArray, valueToCount, invertIt, usePapUtil) → Int`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `String[]` | ✓ |  |
| `valueToCount` | `String` |  | `""` |
| `invertIt` | `Bool` |  | `false` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayDecDigits() → String[]`

**Flags:** Global

Requirements: None

***CONFIRMED WORKING 21-11-02

### `cArrayDecimalsToHexStrings(aArray) → String[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Int[]` | ✓ |  |

### `cArrayDynamicComparisonBool(operators, this, thatArray) → Bool[]`

**Flags:** Global

Requirements: None

Valid operators: ==, !=, &&, ||, !&&, &&!, !&&!, !||, ||!, !||! (10 functions in one)
  e.g. !&& == !this && that ; &&! == this && !that ; !&&! == !this && !that

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `operators` | `String` | ✓ |  |
| `this` | `Bool` | ✓ |  |
| `thatArray` | `Bool[]` | ✓ |  |

### `cArrayDynamicComparisonFloat(operators, this, thatArray) → Bool[]`

**Flags:** Global

Requirements: None

Valid operators: ==, !=, &&, ||, !&&, &&!, !&&!, !||, ||!, !||! (10 functions in one)
  e.g. !&& == !this && that ; &&! == this && !that ; !&&! == !this && !that

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `operators` | `String` | ✓ |  |
| `this` | `Float` | ✓ |  |
| `thatArray` | `Float[]` | ✓ |  |

### `cArrayDynamicComparisonForm(operators, this, thatArray) → Bool[]`

**Flags:** Global

Requirements: None

Valid operators: ==, !=, &&, ||, !&&, &&!, !&&!, !||, ||!, !||!
  e.g. !&& == !this && that ; &&! == this && !that ; !&&! == !this && !that

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `operators` | `String` | ✓ |  |
| `this` | `Form` | ✓ |  |
| `thatArray` | `Form[]` | ✓ |  |

### `cArrayDynamicComparisonInt(operators, this, thatArray) → Bool[]`

**Flags:** Global

Requirements: None

Valid operators: ==, !=, &&, ||, !&&, &&!, !&&!, !||, ||!, !||!
  e.g. !&& == !this && that ; &&! == this && !that ; !&&! == !this && !that

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `operators` | `String` | ✓ |  |
| `this` | `Int` | ✓ |  |
| `thatArray` | `Int[]` | ✓ |  |

### `cArrayDynamicComparisonString(operators, this, thatArray) → Bool[]`

**Flags:** Global

Requirements: None

Valid operators: ==, !=, &&, ||, !&&, &&!, !&&!, !||, ||!, !||!
  e.g. !&& == !this && that ; &&! == this && !that ; !&&! == !this && !that

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `operators` | `String` | ✓ |  |
| `this` | `String` | ✓ |  |
| `thatArray` | `String[]` | ✓ |  |

### `cArrayDynamicOperationFloat(operation, this, thatArray) → Float[]`

**Flags:** Global

Requirements: None

Valid operators: +, -, /, *, **, ^, pow

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `operation` | `String` | ✓ |  |
| `this` | `Float` | ✓ |  |
| `thatArray` | `Float[]` | ✓ |  |

### `cArrayDynamicOperationInt(operation, this, thatArray) → Int[]`

**Flags:** Global

Requirements: None

Valid operators: +, -, /, *, <<, leftshift, lshift, >>, rightshift, rshift, AND, NOT, OR, XOR

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `operation` | `String` | ✓ |  |
| `this` | `Int` | ✓ |  |
| `thatArray` | `Int[]` | ✓ |  |

### `cArrayDynamicOperationString(operation, this, thatArray) → String[]`

**Flags:** Global

Requirements: None

Valid operators: +s, s+, +s+

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `operation` | `String` | ✓ |  |
| `this` | `String` | ✓ |  |
| `thatArray` | `String[]` | ✓ |  |

### `cArrayFindActor(aArray, aValue, startAt, invertIt) → Int`

**Flags:** Global

Requirements: None

>>> Yes...This is .Find() and rFind() **used for Bool invert** (first value that != aValue)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Actor[]` | ✓ |  |
| `aValue` | `Actor` |  |  |
| `startAt` | `Int` |  | `0` |
| `invertIt` | `Bool` |  | `true` |

### `cArrayFindAlias(aArray, aValue, startAt, invertIt) → Int`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Alias[]` | ✓ |  |
| `aValue` | `Alias` |  |  |
| `startAt` | `Int` |  | `0` |
| `invertIt` | `Bool` |  | `true` |

### `cArrayFindBool(aArray, aValue, startAt, invertIt) → Int`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Bool[]` | ✓ |  |
| `aValue` | `Bool` |  | `false` |
| `startAt` | `Int` |  | `0` |
| `invertIt` | `Bool` |  | `true` |

### `cArrayFindByNameActor(aArray, aName, bySubStr) → Int`

**Flags:** Global

Requirements: SKSE

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Actor[]` | ✓ |  |
| `aName` | `String` | ✓ |  |
| `bySubStr` | `Bool` |  | `true` |

### `cArrayFindByNameAlias(aArray, aName, bySubStr) → Int`

**Flags:** Global

Requirements: SKSE

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Alias[]` | ✓ |  |
| `aName` | `String` | ✓ |  |
| `bySubStr` | `Bool` |  | `true` |

### `cArrayFindByNameForm(aArray, aName, bySubStr) → Int`

**Flags:** Global

Requirements: SKSE

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Form[]` | ✓ |  |
| `aName` | `String` | ✓ |  |
| `bySubStr` | `Bool` |  | `true` |

### `cArrayFindByNameObjRef(aArray, aName, bySubStr) → Int`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `ObjectReference[]` | ✓ |  |
| `aName` | `String` | ✓ |  |
| `bySubStr` | `Bool` |  | `true` |

### `cArrayFindFloat(aArray, aValue, startAt, invertIt) → Int`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Float[]` | ✓ |  |
| `aValue` | `Float` |  | `0` |
| `startAt` | `Int` |  | `0` |
| `invertIt` | `Bool` |  | `true` |

### `cArrayFindForm(aArray, aValue, startAt, invertIt) → Int`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Form[]` | ✓ |  |
| `aValue` | `Form` |  |  |
| `startAt` | `Int` |  | `0` |
| `invertIt` | `Bool` |  | `true` |

### `cArrayFindInt(aArray, aValue, startAt, invertIt) → Int`

**Flags:** Global

Requirements: None

kept for invertIt

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Int[]` | ✓ |  |
| `aValue` | `Int` |  | `0` |
| `startAt` | `Int` |  | `0` |
| `invertIt` | `Bool` |  | `true` |

### `cArrayFindObjRef(aArray, aValue, startAt, invertIt) → Int`

**Flags:** Global

Requirements: None

kept for invert

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `ObjectReference[]` | ✓ |  |
| `aValue` | `ObjectReference` |  |  |
| `startAt` | `Int` |  | `0` |
| `invertIt` | `Bool` |  | `true` |

### `cArrayFindString(aArray, aValue, startAt, invertIt) → Int`

**Flags:** Global

Requirements: None

kept for invert

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `String[]` | ✓ |  |
| `aValue` | `String` |  | `""` |
| `startAt` | `Int` |  | `0` |
| `invertIt` | `Bool` |  | `true` |

### `cArrayFromActors(aActor0, aActor1, aActor2, aActor3, aActor4, aActor5, aActor6, aActor7, aActor8, aActor9, skipTrailingNone) → Actor[]`

**Flags:** Global

Requirements: None

====== CREATION
>>> See cStringToArray() in "String" section above
>>> Array from separated values (10 each)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aActor0` | `Actor` | ✓ |  |
| `aActor1` | `Actor` |  |  |
| `aActor2` | `Actor` |  |  |
| `aActor3` | `Actor` |  |  |
| `aActor4` | `Actor` |  |  |
| `aActor5` | `Actor` |  |  |
| `aActor6` | `Actor` |  |  |
| `aActor7` | `Actor` |  |  |
| `aActor8` | `Actor` |  |  |
| `aActor9` | `Actor` |  |  |
| `skipTrailingNone` | `Bool` |  | `true` |

### `cArrayFromAliases(aAlias0, aAlias1, aAlias2, aAlias3, aAlias4, aAlias5, aAlias6, aAlias7, aAlias8, aAlias9, skipTrailingNone) → Alias[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aAlias0` | `Alias` | ✓ |  |
| `aAlias1` | `Alias` |  |  |
| `aAlias2` | `Alias` |  |  |
| `aAlias3` | `Alias` |  |  |
| `aAlias4` | `Alias` |  |  |
| `aAlias5` | `Alias` |  |  |
| `aAlias6` | `Alias` |  |  |
| `aAlias7` | `Alias` |  |  |
| `aAlias8` | `Alias` |  |  |
| `aAlias9` | `Alias` |  |  |
| `skipTrailingNone` | `Bool` |  | `true` |

### `cArrayFromFloats(aFloat0, aFloat1, aFloat2, aFloat3, aFloat4, aFloat5, aFloat6, aFloat7, aFloat8, aFloat9, skipTrailingZero) → Float[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aFloat0` | `Float` | ✓ |  |
| `aFloat1` | `Float` |  | `0` |
| `aFloat2` | `Float` |  | `0` |
| `aFloat3` | `Float` |  | `0` |
| `aFloat4` | `Float` |  | `0` |
| `aFloat5` | `Float` |  | `0` |
| `aFloat6` | `Float` |  | `0` |
| `aFloat7` | `Float` |  | `0` |
| `aFloat8` | `Float` |  | `0` |
| `aFloat9` | `Float` |  | `0` |
| `skipTrailingZero` | `Bool` |  | `true` |

### `cArrayFromForms(aForm0, aForm1, aForm2, aForm3, aForm4, aForm5, aForm6, aForm7, aForm8, aForm9, skipTrailingNone) → Form[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aForm0` | `Form` | ✓ |  |
| `aForm1` | `Form` |  |  |
| `aForm2` | `Form` |  |  |
| `aForm3` | `Form` |  |  |
| `aForm4` | `Form` |  |  |
| `aForm5` | `Form` |  |  |
| `aForm6` | `Form` |  |  |
| `aForm7` | `Form` |  |  |
| `aForm8` | `Form` |  |  |
| `aForm9` | `Form` |  |  |
| `skipTrailingNone` | `Bool` |  | `true` |

### `cArrayFromInts(aInt0, aInt1, aInt2, aInt3, aInt4, aInt5, aInt6, aInt7, aInt8, aInt9, skipTrailingZero) → Int[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aInt0` | `Int` | ✓ |  |
| `aInt1` | `Int` |  | `0` |
| `aInt2` | `Int` |  | `0` |
| `aInt3` | `Int` |  | `0` |
| `aInt4` | `Int` |  | `0` |
| `aInt5` | `Int` |  | `0` |
| `aInt6` | `Int` |  | `0` |
| `aInt7` | `Int` |  | `0` |
| `aInt8` | `Int` |  | `0` |
| `aInt9` | `Int` |  | `0` |
| `skipTrailingZero` | `Bool` |  | `true` |

### `cArrayFromObjRefs(aObjRef0, aObjRef1, aObjRef2, aObjRef3, aObjRef4, aObjRef5, aObjRef6, aObjRef7, aObjRef8, aObjRef9, skipTrailingNone) → ObjectReference[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aObjRef0` | `ObjectReference` | ✓ |  |
| `aObjRef1` | `ObjectReference` |  |  |
| `aObjRef2` | `ObjectReference` |  |  |
| `aObjRef3` | `ObjectReference` |  |  |
| `aObjRef4` | `ObjectReference` |  |  |
| `aObjRef5` | `ObjectReference` |  |  |
| `aObjRef6` | `ObjectReference` |  |  |
| `aObjRef7` | `ObjectReference` |  |  |
| `aObjRef8` | `ObjectReference` |  |  |
| `aObjRef9` | `ObjectReference` |  |  |
| `skipTrailingNone` | `Bool` |  | `true` |

### `cArrayFromStrings(aString0, aString1, aString2, aString3, aString4, aString5, aString6, aString7, aString8, aString9, skipTrailingEmpty) → String[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aString0` | `String` | ✓ |  |
| `aString1` | `String` |  | `""` |
| `aString2` | `String` |  | `""` |
| `aString3` | `String` |  | `""` |
| `aString4` | `String` |  | `""` |
| `aString5` | `String` |  | `""` |
| `aString6` | `String` |  | `""` |
| `aString7` | `String` |  | `""` |
| `aString8` | `String` |  | `""` |
| `aString9` | `String` |  | `""` |
| `skipTrailingEmpty` | `Bool` |  | `true` |

### `cArrayGetAngle(aObj) → Float[]`

**Flags:** Global

Requirements: None

>>> .Get*() as an array && .Set*() accepts array

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aObj` | `ObjectReference` | ✓ |  |

### `cArrayGetDistancesObjRef(aObj, aArray) → Float[]`

**Flags:** Global

Requirements:None

>>> get the distances of array of objects from object aObj

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aObj` | `ObjectReference` | ✓ |  |
| `aArray` | `ObjectReference[]` | ✓ |  |

### `cArrayGetPlacement(aObj) → Float[]`

**Flags:** Global

Requirements: None

>>> Placement == PosX&&PosY&&PosZ&&AngX&&AngY&&AngZ

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aObj` | `ObjectReference` | ✓ |  |

### `cArrayGetPosition(aObj) → Float[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aObj` | `ObjectReference` | ✓ |  |

### `cArrayGetValueIndicesActor(aArray, valueToFind, invertIt) → Int[]`

**Flags:** Global

Requirements: None

>>> Returns array of indices == valueToFind, also can provide the inverse

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Actor[]` | ✓ |  |
| `valueToFind` | `Actor` |  |  |
| `invertIt` | `Bool` |  | `false` |

### `cArrayGetValueIndicesAlias(aArray, valueToFind, invertIt) → Int[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Alias[]` | ✓ |  |
| `valueToFind` | `Alias` |  |  |
| `invertIt` | `Bool` |  | `false` |

### `cArrayGetValueIndicesBool(aArray, valueToFind, invertIt) → Int[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Bool[]` | ✓ |  |
| `valueToFind` | `Bool` |  | `false` |
| `invertIt` | `Bool` |  | `false` |

### `cArrayGetValueIndicesFloat(aArray, valueToFind, invertIt) → Int[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Float[]` | ✓ |  |
| `valueToFind` | `Float` |  | `0` |
| `invertIt` | `Bool` |  | `false` |

### `cArrayGetValueIndicesForm(aArray, valueToFind, invertIt) → Int[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Form[]` | ✓ |  |
| `valueToFind` | `Form` |  |  |
| `invertIt` | `Bool` |  | `false` |

### `cArrayGetValueIndicesInt(aArray, valueToFind, invertIt) → Int[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Int[]` | ✓ |  |
| `valueToFind` | `Int` |  | `0` |
| `invertIt` | `Bool` |  | `false` |

### `cArrayGetValueIndicesObjRef(aArray, valueToFind, invertIt) → Int[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `ObjectReference[]` | ✓ |  |
| `valueToFind` | `ObjectReference` |  |  |
| `invertIt` | `Bool` |  | `false` |

### `cArrayGetValueIndicesString(aArray, valueToFind, invertIt) → Int[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `String[]` | ✓ |  |
| `valueToFind` | `String` |  | `""` |
| `invertIt` | `Bool` |  | `false` |

### `cArrayHexDigits() → String[]`

**Flags:** Global

Requirements: None

***CONFIRMED WORKING 21-11-02

### `cArrayHexIDModNamesToForms(aArray, esXNames, clearNones, useSKSE) → Form[]`

**Flags:** Global

Requirements: None, SKSE:Soft

==> Each can be different mod, String[] to supply name for each

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `String[]` | ✓ |  |
| `esXNames` | `String[]` | ✓ |  |
| `clearNones` | `Bool` |  | `true` |
| `useSKSE` | `Bool` |  | `true` |

### `cArrayHexIDToForms(aArray, esXName, skipNones, useSKSE) → Form[]`

**Flags:** Global

Requirements: None, SKSE:Soft

>>> Get forms from file
==> If all forms from same mod

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `String[]` | ✓ |  |
| `esXName` | `String` | ✓ |  |
| `skipNones` | `Bool` |  | `true` |
| `useSKSE` | `Bool` |  | `true` |

### `cArrayHexStringsToDecimal(aArray) → Int[]`

**Flags:** Global

Requirements: None

without SKSE array creation is limited to 128!

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `String[]` | ✓ |  |

### `cArrayIntIDModNamesToForms(aArray, esXName, skipNones, useSKSE) → Form[]`

**Flags:** Global

Requirements: None, SKSE:Soft

==> Each can be different mod, String[] to supply name for each

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Int[]` | ✓ |  |
| `esXName` | `String[]` | ✓ |  |
| `skipNones` | `Bool` |  | `true` |
| `useSKSE` | `Bool` |  | `true` |

### `cArrayIntIDToForms(aArray, esXName, skipNones, useSKSE) → Form[]`

**Flags:** Global

Requirements: None, SKSE:Soft

==> If all forms from same mod

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Int[]` | ✓ |  |
| `esXName` | `String` | ✓ |  |
| `skipNones` | `Bool` |  | `true` |
| `useSKSE` | `Bool` |  | `true` |

### `cArrayIntToBool(aArray) → Bool[]`

**Flags:** Global

Requirements:None

see also: ArraySumFloat()
          ArraySumInt()
          ArrayAverageFloat()
          ArrayAverageInt()
>>> Conversion

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Int[]` | ✓ |  |

### `cArrayJoinString(aArray, delimiterString, startIndex, numIndices) → String`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `String[]` | ✓ |  |
| `delimiterString` | `String` |  | `""` |
| `startIndex` | `Int` |  | `0` |
| `numIndices` | `Int` |  | `-1` |

### `cArrayLargestFloat(aArray) → Float`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Float[]` | ✓ |  |

### `cArrayLargestInt(aArray) → Int`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Int[]` | ✓ |  |

### `cArrayLetterChars() → String[]`

**Flags:** Global

Requirements: None

***CONFIRMED WORKING 21-11-02

### `cArrayListSkillNames() → String[]`

**Flags:** Global

Requirements: None

====== Retrieve temporary data for various functions
***CONFIRMED WORKING 21-11-02

### `cArrayMergeActor(aArray1, aArray2, useSKSE, usePapUtil) → Actor[]`

**Flags:** Global

Requirements: None, SKSE:Soft, PapyrusUtil:Soft

>>> Merging (returns new array)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray1` | `Actor[]` | ✓ |  |
| `aArray2` | `Actor[]` | ✓ |  |
| `useSKSE` | `Bool` |  | `true` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayMergeAlias(aArray1, aArray2, useSKSE, usePapUtil) → Alias[]`

**Flags:** Global

Requirements: None, SKSE:Soft, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray1` | `Alias[]` | ✓ |  |
| `aArray2` | `Alias[]` | ✓ |  |
| `useSKSE` | `Bool` |  | `true` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayMergeBool(aArray1, aArray2, useSKSE, usePapUtil) → Bool[]`

**Flags:** Global

Requirements: None, SKSE:Soft, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray1` | `Bool[]` | ✓ |  |
| `aArray2` | `Bool[]` | ✓ |  |
| `useSKSE` | `Bool` |  | `true` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayMergeFloat(aArray1, aArray2, useSKSE, usePapUtil) → Float[]`

**Flags:** Global

Requirements: None, SKSE:Soft, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray1` | `Float[]` | ✓ |  |
| `aArray2` | `Float[]` | ✓ |  |
| `useSKSE` | `Bool` |  | `true` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayMergeForm(aArray1, aArray2, useSKSE, usePapUtil) → Form[]`

**Flags:** Global

Requirements: None, SKSE:Soft, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray1` | `Form[]` | ✓ |  |
| `aArray2` | `Form[]` | ✓ |  |
| `useSKSE` | `Bool` |  | `true` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayMergeInt(aArray1, aArray2, useSKSE, usePapUtil) → Int[]`

**Flags:** Global

Requirements: None, SKSE:Soft, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray1` | `Int[]` | ✓ |  |
| `aArray2` | `Int[]` | ✓ |  |
| `useSKSE` | `Bool` |  | `true` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayMergeObjRef(aArray1, aArray2, useSKSE, usePapUtil) → ObjectReference[]`

**Flags:** Global

Requirements: None, SKSE:Soft, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray1` | `ObjectReference[]` | ✓ |  |
| `aArray2` | `ObjectReference[]` | ✓ |  |
| `useSKSE` | `Bool` |  | `true` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayMergeString(aArray1, aArray2, useSKSE, usePapUtil) → String[]`

**Flags:** Global

Requirements: None, SKSE:Soft, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray1` | `String[]` | ✓ |  |
| `aArray2` | `String[]` | ✓ |  |
| `useSKSE` | `Bool` |  | `true` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayNameFromFL(aFormList) → String[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aFormList` | `FormList` | ✓ |  |

### `cArrayNameFromForms(aArray) → String[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Form[]` | ✓ |  |

### `cArrayNoneActor() → Actor[]`

**Flags:** Global

Requirements: None

>>> None arrays (great for papyrus array spam handling, also 'resets' an array variable)

### `cArrayNoneAlias() → Alias[]`

**Flags:** Global

Requirements: None

### `cArrayNoneBool() → Bool[]`

**Flags:** Global

Requirements: None

### `cArrayNoneFloat() → Float[]`

**Flags:** Global

Requirements: None

### `cArrayNoneForm() → Form[]`

**Flags:** Global

Requirements: None

### `cArrayNoneInt() → Int[]`

**Flags:** Global

Requirements: None

### `cArrayNoneObjectReference() → ObjectReference[]`

**Flags:** Global

Requirements: None

### `cArrayNoneObjRef() → ObjectReference[]`

**Flags:** Global

Requirements: None

### `cArrayNoneString() → String[]`

**Flags:** Global

Requirements: None

### `cArrayObjRefToBaseObject(aArray) → Form[]`

**Flags:** Global

Requirements:None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `ObjectReference[]` | ✓ |  |

### `cArrayPartitionFloat(aArray, low, high) → Int`

**Flags:** Global

Requirements: None

Only for use as part of the cArraySortFloat function

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Float[]` | ✓ |  |
| `low` | `Int` | ✓ |  |
| `high` | `Int` | ✓ |  |

### `cArrayPartitionInt(aArray, low, high) → Int`

**Flags:** Global

Requirements: None

Only for use as part of the cArraySortInt function

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Int[]` | ✓ |  |
| `low` | `Int` | ✓ |  |
| `high` | `Int` | ✓ |  |

### `cArrayRandomFloats(arraySize, this, that) → Float[]`

**Flags:** Global

Requirements: None

array length capped at 128

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arraySize` | `Int` |  | `128` |
| `this` | `Float` |  | `0` |
| `that` | `Float` |  | `100` |

### `cArrayRandomInts(arraySize, this, that) → Int[]`

**Flags:** Global

Requirements: None

array length capped at 128

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arraySize` | `Int` |  | `128` |
| `this` | `Int` |  | `0` |
| `that` | `Int` |  | `100` |

### `cArrayRemoveDuplicatesActor(aArray, usePapUtil) → Actor[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

>>> HowTo: RemoveValue
cArrayReplace.*(replaceThis = (None,0,0.0,""), withThis = filler)
cArrayClearNone.*() == cArrayRemoveValue.*(aArray, None)
cArrayClearZero.*() == cArrayRemoveValue.*(aArray, 0) <- 0.0 (Int/Float)
cArrayClearBlank() == cArrayRemoveValueString(aArray, "") (String)
cArrayClearEmpty() == cArrayRemoveValueString(aArray, "") (String)
>>> Remove duplicate records no Bool version, only returns 1-2 index array

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Actor[]` | ✓ |  |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayRemoveDuplicatesAlias(aArray, usePapUtil) → Alias[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Alias[]` | ✓ |  |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayRemoveDuplicatesFloat(aArray, usePapUtil) → Float[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Float[]` | ✓ |  |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayRemoveDuplicatesForm(aArray, usePapUtil) → Form[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Form[]` | ✓ |  |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayRemoveDuplicatesInt(aArray, usePapUtil) → Int[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Int[]` | ✓ |  |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayRemoveDuplicatesObjRef(aArray, usePapUtil) → ObjectReference[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `ObjectReference[]` | ✓ |  |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayRemoveDuplicatesString(aArray, usePapUtil) → String[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `String[]` | ✓ |  |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayRemoveIndexActor(aArray, indexToRemove, usePapUtil) → Actor[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

>>> 'fill' empty values
--> cArrayReplace.*(replaceThis = (None,0,0.0,""), withThis = filler)
>>> Clear all values
--> 'clear' == cArrayReplace.*(replaceThis = (irrelevant), withThis = (None,0,0.0,""), forceAll = TRUE)
 OR
--> 'clear' == cArrayCreate.*()
>>> Add new value to end
--> 'push' == cArrayResize.*(newSize = aArray.length + 1, filler = new value)
>>> Remove Index [allows Pop & Shift behavior] (returns new array)
--> 'shift' mimic cArrayRemoveIndex.*(indexToRemove == 0)
--> 'pop'   mimic cArrayRemoveIndex.*(indexToRemove == aArray.length)
>>> Add new index 'unshift'
--> use cArrayMerge.*

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Actor[]` | ✓ |  |
| `indexToRemove` | `Int` |  | `0` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayRemoveIndexAlias(aArray, indexToRemove, usePapUtil) → Alias[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Alias[]` | ✓ |  |
| `indexToRemove` | `Int` |  | `0` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayRemoveIndexBool(aArray, indexToRemove, usePapUtil) → Bool[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Bool[]` | ✓ |  |
| `indexToRemove` | `Int` |  | `0` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayRemoveIndexFloat(aArray, indexToRemove, usePapUtil) → Float[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Float[]` | ✓ |  |
| `indexToRemove` | `Int` |  | `0` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayRemoveIndexForm(aArray, indexToRemove, usePapUtil) → Form[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Form[]` | ✓ |  |
| `indexToRemove` | `Int` |  | `0` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayRemoveIndexInt(aArray, indexToRemove, usePapUtil) → Int[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Int[]` | ✓ |  |
| `indexToRemove` | `Int` |  | `0` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayRemoveIndexObjRef(aArray, indexToRemove, usePapUtil) → ObjectReference[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `ObjectReference[]` | ✓ |  |
| `indexToRemove` | `Int` |  | `0` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayRemoveIndexString(aArray, indexToRemove, usePapUtil) → String[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `String[]` | ✓ |  |
| `indexToRemove` | `Int` |  | `0` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayRemoveIndicesActor(aArray, indicesToRemove, stopLength) → Actor[]`

**Flags:** Global

Requirements: None

>>> Supply with array of ints and this removes those IndICES then returns new array

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Actor[]` | ✓ |  |
| `indicesToRemove` | `Int[]` | ✓ |  |
| `stopLength` | `Int` |  | `0` |

### `cArrayRemoveIndicesAlias(aArray, indicesToRemove, stopLength) → Alias[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Alias[]` | ✓ |  |
| `indicesToRemove` | `Int[]` | ✓ |  |
| `stopLength` | `Int` |  | `0` |

### `cArrayRemoveIndicesBool(aArray, indicesToRemove, stopLength) → Bool[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Bool[]` | ✓ |  |
| `indicesToRemove` | `Int[]` | ✓ |  |
| `stopLength` | `Int` |  | `0` |

### `cArrayRemoveIndicesFloat(aArray, indicesToRemove, stopLength) → Float[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Float[]` | ✓ |  |
| `indicesToRemove` | `Int[]` | ✓ |  |
| `stopLength` | `Int` |  | `0` |

### `cArrayRemoveIndicesForm(aArray, indicesToRemove, stopLength) → Form[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Form[]` | ✓ |  |
| `indicesToRemove` | `Int[]` | ✓ |  |
| `stopLength` | `Int` |  | `0` |

### `cArrayRemoveIndicesInt(aArray, indicesToRemove, stopLength) → Int[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Int[]` | ✓ |  |
| `indicesToRemove` | `Int[]` | ✓ |  |
| `stopLength` | `Int` |  | `0` |

### `cArrayRemoveIndicesObjRef(aArray, indicesToRemove, stopLength) → ObjectReference[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `ObjectReference[]` | ✓ |  |
| `indicesToRemove` | `Int[]` | ✓ |  |
| `stopLength` | `Int` |  | `0` |

### `cArrayRemoveIndicesString(aArray, indicesToRemove, stopLength) → String[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `String[]` | ✓ |  |
| `indicesToRemove` | `Int[]` | ✓ |  |
| `stopLength` | `Int` |  | `0` |

### `cArrayRemoveTrailingActor(aArray, trailingValue) → Actor[]`

**Flags:** Global

Requirements: None

this assumes that the last indices are not *supposed* to be trailingValue

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Actor[]` | ✓ |  |
| `trailingValue` | `Actor` |  |  |

### `cArrayRemoveTrailingAlias(aArray, trailingValue) → Alias[]`

**Flags:** Global

Requirements: None

this assumes that the last indices are not *supposed* to be trailingValue

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Alias[]` | ✓ |  |
| `trailingValue` | `Alias` |  |  |

### `cArrayRemoveTrailingBool(aArray, trailingValue) → Bool[]`

**Flags:** Global

Requirements: None

this assumes that the last indices are not *supposed* to be trailingValue

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Bool[]` | ✓ |  |
| `trailingValue` | `Bool` |  | `false` |

### `cArrayRemoveTrailingFloat(aArray, trailingValue) → Float[]`

**Flags:** Global

Requirements: None

this assumes that the last indices are not *supposed* to be trailingValue

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Float[]` | ✓ |  |
| `trailingValue` | `Float` |  | `0` |

### `cArrayRemoveTrailingForm(aArray, trailingValue) → Form[]`

**Flags:** Global

Requirements: None

this assumes that the last indices are not *supposed* to be trailingValue

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Form[]` | ✓ |  |
| `trailingValue` | `Form` |  |  |

### `cArrayRemoveTrailingInt(aArray, trailingValue) → Int[]`

**Flags:** Global

Requirements: None

this assumes that the last indices are not *supposed* to be trailingValue

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Int[]` | ✓ |  |
| `trailingValue` | `Int` |  | `0` |

### `cArrayRemoveTrailingObjRef(aArray, trailingValue) → ObjectReference[]`

**Flags:** Global

Requirements: None

this assumes that the last indices are not *supposed* to be trailingValue

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `ObjectReference[]` | ✓ |  |
| `trailingValue` | `ObjectReference` |  |  |

### `cArrayRemoveTrailingString(aArray, trailingValue) → String[]`

**Flags:** Global

Requirements: None

this assumes that the last indices are not *supposed* to be trailingValue

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `String[]` | ✓ |  |
| `trailingValue` | `String` |  | `""` |

### `cArrayRemoveValueActor(aArray, toRemove, usePapUtil) → Actor[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

>>> *Removes* all indices of said value (returns new array)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Actor[]` | ✓ |  |
| `toRemove` | `Actor` |  |  |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayRemoveValueAlias(aArray, toRemove, usePapUtil) → Alias[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Alias[]` | ✓ |  |
| `toRemove` | `Alias` |  |  |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayRemoveValueBool(aArray, toRemove, usePapUtil) → Bool[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Bool[]` | ✓ |  |
| `toRemove` | `Bool` |  | `false` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayRemoveValueFloat(aArray, toRemove, usePapUtil) → Float[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Float[]` | ✓ |  |
| `toRemove` | `Float` |  | `0` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayRemoveValueForm(aArray, toRemove, usePapUtil) → Form[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Form[]` | ✓ |  |
| `toRemove` | `Form` |  |  |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayRemoveValueInt(aArray, toRemove, usePapUtil) → Int[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Int[]` | ✓ |  |
| `toRemove` | `Int` |  | `0` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayRemoveValueObjRef(aArray, toRemove, usePapUtil) → ObjectReference[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `ObjectReference[]` | ✓ |  |
| `toRemove` | `ObjectReference` |  |  |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayRemoveValueString(aArray, toRemove, usePapUtil) → String[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `String[]` | ✓ |  |
| `toRemove` | `String` |  | `""` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayReplaceActor(aArray, replaceThis, withThis, forceAll) → Actor[]`

**Flags:** Global

Requirements: None

forceAll == TRUE replaces EVERYTHING with aValue

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Actor[]` | ✓ |  |
| `replaceThis` | `Actor` | ✓ |  |
| `withThis` | `Actor` | ✓ |  |
| `forceAll` | `Bool` |  | `false` |

### `cArrayReplaceAlias(aArray, replaceThis, withThis, forceAll) → Alias[]`

**Flags:** Global

Requirements: None

forceAll == TRUE replaces EVERYTHING with aValue

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Alias[]` | ✓ |  |
| `replaceThis` | `Alias` | ✓ |  |
| `withThis` | `Alias` | ✓ |  |
| `forceAll` | `Bool` |  | `false` |

### `cArrayReplaceBool(aArray, replaceThis, withThis, forceAll) → Bool[]`

**Flags:** Global

Requirements: None

forceAll == TRUE replaces EVERYTHING with aValue

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Bool[]` | ✓ |  |
| `replaceThis` | `Bool` | ✓ |  |
| `withThis` | `Bool` | ✓ |  |
| `forceAll` | `Bool` |  | `false` |

### `cArrayReplaceFloat(aArray, replaceThis, withThis, forceAll) → Float[]`

**Flags:** Global

Requirements: None

forceAll == TRUE replaces EVERYTHING with aValue

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Float[]` | ✓ |  |
| `replaceThis` | `Float` | ✓ |  |
| `withThis` | `Float` | ✓ |  |
| `forceAll` | `Bool` |  | `false` |

### `cArrayReplaceForm(aArray, replaceThis, withThis, forceAll) → Form[]`

**Flags:** Global

Requirements: None

forceAll == TRUE replaces EVERYTHING with aValue

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Form[]` | ✓ |  |
| `replaceThis` | `Form` | ✓ |  |
| `withThis` | `Form` | ✓ |  |
| `forceAll` | `Bool` |  | `false` |

### `cArrayReplaceInt(aArray, replaceThis, withThis, forceAll) → Int[]`

**Flags:** Global

Requirements: None

forceAll == TRUE replaces EVERYTHING with aValue

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Int[]` | ✓ |  |
| `replaceThis` | `Int` | ✓ |  |
| `withThis` | `Int` | ✓ |  |
| `forceAll` | `Bool` |  | `false` |

### `cArrayReplaceObjRef(aArray, replaceThis, withThis, forceAll) → ObjectReference[]`

**Flags:** Global

Requirements: None

forceAll == TRUE replaces EVERYTHING with aValue

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `ObjectReference[]` | ✓ |  |
| `replaceThis` | `ObjectReference` | ✓ |  |
| `withThis` | `ObjectReference` | ✓ |  |
| `forceAll` | `Bool` |  | `false` |

### `cArrayReplaceString(aArray, replaceThis, withThis, forceAll) → String[]`

**Flags:** Global

Requirements: None

forceAll == TRUE replaces EVERYTHING with aValue

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `String[]` | ✓ |  |
| `replaceThis` | `String` | ✓ |  |
| `withThis` | `String` | ✓ |  |
| `forceAll` | `Bool` |  | `false` |

### `cArrayResizeActor(aArray, newSize, filler, clampMinLength, clampMaxLength, usePapUtil) → Actor[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

>>> Resizing (mixed return, SKSE = resize original, vanilla = new copy)
 clampMinLength, clampMaxLength allows 'automated' conditional length

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Actor[]` | ✓ |  |
| `newSize` | `Int` | ✓ |  |
| `filler` | `Actor` |  |  |
| `clampMinLength` | `Int` |  | `-1` |
| `clampMaxLength` | `Int` |  | `-1` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayResizeAlias(aArray, newSize, filler, clampMinLength, clampMaxLength, usePapUtil) → Alias[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Alias[]` | ✓ |  |
| `newSize` | `Int` | ✓ |  |
| `filler` | `Alias` |  |  |
| `clampMinLength` | `Int` |  | `-1` |
| `clampMaxLength` | `Int` |  | `-1` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayResizeBool(aArray, newSize, filler, clampMinLength, clampMaxLength, usePapUtil) → Bool[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Bool[]` | ✓ |  |
| `newSize` | `Int` | ✓ |  |
| `filler` | `Bool` |  | `false` |
| `clampMinLength` | `Int` |  | `-1` |
| `clampMaxLength` | `Int` |  | `-1` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayResizeFloat(aArray, newSize, filler, clampMinLength, clampMaxLength, usePapUtil) → Float[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Float[]` | ✓ |  |
| `newSize` | `Int` | ✓ |  |
| `filler` | `Float` |  | `0` |
| `clampMinLength` | `Int` |  | `-1` |
| `clampMaxLength` | `Int` |  | `-1` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayResizeForm(aArray, newSize, filler, clampMinLength, clampMaxLength, usePapUtil) → Form[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Form[]` | ✓ |  |
| `newSize` | `Int` | ✓ |  |
| `filler` | `Form` |  |  |
| `clampMinLength` | `Int` |  | `-1` |
| `clampMaxLength` | `Int` |  | `-1` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayResizeInt(aArray, newSize, filler, clampMinLength, clampMaxLength, usePapUtil) → Int[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Int[]` | ✓ |  |
| `newSize` | `Int` | ✓ |  |
| `filler` | `Int` |  | `0` |
| `clampMinLength` | `Int` |  | `-1` |
| `clampMaxLength` | `Int` |  | `-1` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayResizeObjRef(aArray, newSize, filler, clampMinLength, clampMaxLength, usePapUtil) → ObjectReference[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `ObjectReference[]` | ✓ |  |
| `newSize` | `Int` | ✓ |  |
| `filler` | `ObjectReference` |  |  |
| `clampMinLength` | `Int` |  | `-1` |
| `clampMaxLength` | `Int` |  | `-1` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayResizeString(aArray, newSize, filler, clampMinLength, clampMaxLength, usePapUtil) → String[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `String[]` | ✓ |  |
| `newSize` | `Int` | ✓ |  |
| `filler` | `String` |  | `""` |
| `clampMinLength` | `Int` |  | `-1` |
| `clampMaxLength` | `Int` |  | `-1` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArrayReturnActor(aArray, returnIndex) → Actor`

**Flags:** Global

Requirements: None

>>> 1) allows return via .Find(), returnIndex == -1 returns 1st value that casts as TRUE

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Actor[]` | ✓ |  |
| `returnIndex` | `Int` |  | `-1` |

### `cArrayReturnAlias(aArray, returnIndex) → Alias`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Alias[]` | ✓ |  |
| `returnIndex` | `Int` |  | `-1` |

### `cArrayReturnBool(aArray, returnIndex) → Bool`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Bool[]` | ✓ |  |
| `returnIndex` | `Int` |  | `-1` |

### `cArrayReturnFloat(aArray, returnIndex) → Float`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Float[]` | ✓ |  |
| `returnIndex` | `Int` |  | `-1` |

### `cArrayReturnForm(aArray, returnIndex) → Form`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Form[]` | ✓ |  |
| `returnIndex` | `Int` |  | `-1` |

### `cArrayReturnInt(aArray, returnIndex) → Int`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Int[]` | ✓ |  |
| `returnIndex` | `Int` |  | `-1` |

### `cArrayReturnObjRef(aArray, returnIndex) → ObjectReference`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `ObjectReference[]` | ✓ |  |
| `returnIndex` | `Int` |  | `-1` |

### `cArrayReturnString(aArray, returnIndex) → String`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `String[]` | ✓ |  |
| `returnIndex` | `Int` |  | `-1` |

### `cArrayReverseActor(aArray) → Actor[]`

**Flags:** Global

Requirements: None

>>> Reverse order (returns new array)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Actor[]` | ✓ |  |

### `cArrayReverseAlias(aArray) → Alias[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Alias[]` | ✓ |  |

### `cArrayReverseBool(aArray) → Bool[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Bool[]` | ✓ |  |

### `cArrayReverseFloat(aArray) → Float[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Float[]` | ✓ |  |

### `cArrayReverseForm(aArray) → Form[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Form[]` | ✓ |  |

### `cArrayReverseInt(aArray) → Int[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Int[]` | ✓ |  |

### `cArrayReverseObjRef(aArray) → ObjectReference[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `ObjectReference[]` | ✓ |  |

### `cArrayReverseString(aArray) → String[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `String[]` | ✓ |  |

### `cArrayRFindActor(aArray, aValue, startAt, invertIt) → Int`

**Flags:** Global

Requirements: None

use it for invertIt
startAt requires a positive int and counts backwards from the end

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Actor[]` | ✓ |  |
| `aValue` | `Actor` |  |  |
| `startAt` | `Int` |  | `0` |
| `invertIt` | `Bool` |  | `true` |

### `cArrayRFindAlias(aArray, aValue, startAt, invertIt) → Int`

**Flags:** Global

Requirements: None

use it for invertIt
startAt requires a positive int and counts backwards from the end

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Alias[]` | ✓ |  |
| `aValue` | `Alias` |  |  |
| `startAt` | `Int` |  | `0` |
| `invertIt` | `Bool` |  | `true` |

### `cArrayRFindBool(aArray, aValue, startAt, invertIt) → Int`

**Flags:** Global

Requirements: None

use it for invertIt
startAt requires a positive int and counts backwards from the end

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Bool[]` | ✓ |  |
| `aValue` | `Bool` |  | `false` |
| `startAt` | `Int` |  | `0` |
| `invertIt` | `Bool` |  | `true` |

### `cArrayRFindFloat(aArray, aValue, startAt, invertIt) → Int`

**Flags:** Global

Requirements: None

use it for invertIt
startAt requires a positive int and counts backwards from the end

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Float[]` | ✓ |  |
| `aValue` | `Float` |  | `0` |
| `startAt` | `Int` |  | `0` |
| `invertIt` | `Bool` |  | `true` |

### `cArrayRFindForm(aArray, aValue, startAt, invertIt) → Int`

**Flags:** Global

Requirements: None

use it for invertIt
startAt requires a positive int and counts backwards from the end

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Form[]` | ✓ |  |
| `aValue` | `Form` |  |  |
| `startAt` | `Int` |  | `0` |
| `invertIt` | `Bool` |  | `true` |

### `cArrayRFindInt(aArray, aValue, startAt, invertIt) → Int`

**Flags:** Global

Requirements: None

use it for invertIt
startAt requires a positive int and counts backwards from the end

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Int[]` | ✓ |  |
| `aValue` | `Int` |  | `0` |
| `startAt` | `Int` |  | `0` |
| `invertIt` | `Bool` |  | `true` |

### `cArrayRFindObjRef(aArray, aValue, startAt, invertIt) → Int`

**Flags:** Global

Requirements: None

use it for invertIt
startAt requires a positive int and counts backwards from the end

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `ObjectReference[]` | ✓ |  |
| `aValue` | `ObjectReference` |  |  |
| `startAt` | `Int` |  | `0` |
| `invertIt` | `Bool` |  | `true` |

### `cArrayRFindString(aArray, aValue, startAt, invertIt) → Int`

**Flags:** Global

Requirements: None

used for invertIt ; -1 == last element

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `String[]` | ✓ |  |
| `aValue` | `String` |  | `""` |
| `startAt` | `Int` |  | `-1` |
| `invertIt` | `Bool` |  | `true` |

### `cArraySetAngle(aObj, aArray)`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aObj` | `ObjectReference` | ✓ |  |
| `aArray` | `Float[]` | ✓ |  |

### `cArraySetPlacement(aObj, aArray)`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aObj` | `ObjectReference` | ✓ |  |
| `aArray` | `Float[]` | ✓ |  |

### `cArraySetPosition(aObj, aArray)`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aObj` | `ObjectReference` | ✓ |  |
| `aArray` | `Float[]` | ✓ |  |

### `cArraySliceActor(aArray, fromIndex, toIndex, usePapUtil) → Actor[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

>>> Slice copies a portion of aArray to new array and returns it

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Actor[]` | ✓ |  |
| `fromIndex` | `Int` | ✓ |  |
| `toIndex` | `Int` |  | `0` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArraySliceAlias(aArray, fromIndex, toIndex, usePapUtil) → Alias[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Alias[]` | ✓ |  |
| `fromIndex` | `Int` | ✓ |  |
| `toIndex` | `Int` |  | `0` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArraySliceBool(aArray, fromIndex, toIndex, usePapUtil) → Bool[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Bool[]` | ✓ |  |
| `fromIndex` | `Int` | ✓ |  |
| `toIndex` | `Int` |  | `0` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArraySliceFloat(aArray, fromIndex, toIndex, usePapUtil) → Float[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Float[]` | ✓ |  |
| `fromIndex` | `Int` | ✓ |  |
| `toIndex` | `Int` |  | `0` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArraySliceForm(aArray, fromIndex, toIndex, usePapUtil) → Form[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Form[]` | ✓ |  |
| `fromIndex` | `Int` | ✓ |  |
| `toIndex` | `Int` |  | `0` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArraySliceInt(aArray, fromIndex, toIndex, usePapUtil) → Int[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Int[]` | ✓ |  |
| `fromIndex` | `Int` | ✓ |  |
| `toIndex` | `Int` |  | `0` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArraySliceObjRef(aArray, fromIndex, toIndex, usePapUtil) → ObjectReference[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `ObjectReference[]` | ✓ |  |
| `fromIndex` | `Int` | ✓ |  |
| `toIndex` | `Int` |  | `0` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArraySliceString(aArray, fromIndex, toIndex, usePapUtil) → String[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `String[]` | ✓ |  |
| `fromIndex` | `Int` | ✓ |  |
| `toIndex` | `Int` |  | `0` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArraySmallestFloat(aArray) → Float`

**Flags:** Global

Requirements: None

>>> Analysis/Comparison/Query/Tally

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Float[]` | ✓ |  |

### `cArraySmallestInt(aArray) → Int`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Int[]` | ✓ |  |

### `cArraySortFloat(aArray, low, high, usePapUtil)`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Float[]` | ✓ |  |
| `low` | `Int` |  | `-1` |
| `high` | `Int` |  | `-1` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArraySortInt(aArray, low, high, usePapUtil)`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Int[]` | ✓ |  |
| `low` | `Int` |  | `-1` |
| `high` | `Int` |  | `-1` |
| `usePapUtil` | `Bool` |  | `true` |

### `cArraySpliceActor(aArray, toInsert, insertAtIndex) → Actor[]`

**Flags:** Global

Requirements: None

>>> Inserts an array into another (returns new array)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Actor[]` | ✓ |  |
| `toInsert` | `Actor[]` | ✓ |  |
| `insertAtIndex` | `Int` |  | `0` |

### `cArraySpliceAlias(aArray, toInsert, insertAtIndex) → Alias[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Alias[]` | ✓ |  |
| `toInsert` | `Alias[]` | ✓ |  |
| `insertAtIndex` | `Int` |  | `0` |

### `cArraySpliceBool(aArray, toInsert, insertAtIndex) → Bool[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Bool[]` | ✓ |  |
| `toInsert` | `Bool[]` | ✓ |  |
| `insertAtIndex` | `Int` |  | `0` |

### `cArraySpliceFloat(aArray, toInsert, insertAtIndex) → Float[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Float[]` | ✓ |  |
| `toInsert` | `Float[]` | ✓ |  |
| `insertAtIndex` | `Int` |  | `0` |

### `cArraySpliceForm(aArray, toInsert, insertAtIndex) → Form[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Form[]` | ✓ |  |
| `toInsert` | `Form[]` | ✓ |  |
| `insertAtIndex` | `Int` |  | `0` |

### `cArraySpliceInt(aArray, toInsert, insertAtIndex) → Int[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Int[]` | ✓ |  |
| `toInsert` | `Int[]` | ✓ |  |
| `insertAtIndex` | `Int` |  | `0` |

### `cArraySpliceObjRef(aArray, toInsert, insertAtIndex) → ObjectReference[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `ObjectReference[]` | ✓ |  |
| `toInsert` | `ObjectReference[]` | ✓ |  |
| `insertAtIndex` | `Int` |  | `0` |

### `cArraySpliceString(aArray, toInsert, insertAtIndex) → String[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `String[]` | ✓ |  |
| `toInsert` | `String[]` | ✓ |  |
| `insertAtIndex` | `Int` |  | `0` |

### `cArrayStringFromKeywords(aArray) → String[]`

**Flags:** Global

Requirements: SKSE

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Keyword[]` | ✓ |  |

### `cArraySumFloat(aArray, usePapUtil) → Float`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

>>> Entire array calculations

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Float[]` | ✓ |  |
| `usePapUtil` | `Bool` |  | `true` |

### `cArraySumInt(aArray, usePapUtil) → Int`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Int[]` | ✓ |  |
| `usePapUtil` | `Bool` |  | `true` |

### `cArraySwapIndexActor(aArray, index1, index2)`

**Flags:** Global

Requirements: None

>>> Swap Indices

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Actor[]` | ✓ |  |
| `index1` | `Int` | ✓ |  |
| `index2` | `Int` | ✓ |  |

### `cArraySwapIndexAlias(aArray, index1, index2)`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Alias[]` | ✓ |  |
| `index1` | `Int` | ✓ |  |
| `index2` | `Int` | ✓ |  |

### `cArraySwapIndexBool(aArray, index1, index2)`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Bool[]` | ✓ |  |
| `index1` | `Int` | ✓ |  |
| `index2` | `Int` | ✓ |  |

### `cArraySwapIndexFloat(aArray, index1, index2)`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Float[]` | ✓ |  |
| `index1` | `Int` | ✓ |  |
| `index2` | `Int` | ✓ |  |

### `cArraySwapIndexForm(aArray, index1, index2)`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Form[]` | ✓ |  |
| `index1` | `Int` | ✓ |  |
| `index2` | `Int` | ✓ |  |

### `cArraySwapIndexInt(aArray, index1, index2)`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Int[]` | ✓ |  |
| `index1` | `Int` | ✓ |  |
| `index2` | `Int` | ✓ |  |

### `cArraySwapIndexObjRef(aArray, index1, index2)`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `ObjectReference[]` | ✓ |  |
| `index1` | `Int` | ✓ |  |
| `index2` | `Int` | ✓ |  |

### `cArraySwapIndexString(aArray, index1, index2)`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `String[]` | ✓ |  |
| `index1` | `Int` | ✓ |  |
| `index2` | `Int` | ✓ |  |

### `cArrayTidyActor(aArray, clearNone, clearDupes) → Actor[]`

**Flags:** Global

Requirements: None

====== Manipulation
>>>  Housekeeping (returns original)
---@ Three(2 for objects) in one

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Actor[]` | ✓ |  |
| `clearNone` | `Bool` |  | `false` |
| `clearDupes` | `Bool` |  | `false` |

### `cArrayTidyAlias(aArray, clearNone, clearDupes) → Alias[]`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Alias[]` | ✓ |  |
| `clearNone` | `Bool` |  | `false` |
| `clearDupes` | `Bool` |  | `false` |

### `cArrayTidyFloat(aArray, clearZero, clearDupes, sortIt) → Float[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Float[]` | ✓ |  |
| `clearZero` | `Bool` |  | `false` |
| `clearDupes` | `Bool` |  | `false` |
| `sortIt` | `Bool` |  | `false` |

### `cArrayTidyForm(aArray, clearNone, clearDupes) → Form[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Form[]` | ✓ |  |
| `clearNone` | `Bool` |  | `false` |
| `clearDupes` | `Bool` |  | `false` |

### `cArrayTidyInt(aArray, clearZero, clearDupes, sortIt) → Int[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Int[]` | ✓ |  |
| `clearZero` | `Bool` |  | `false` |
| `clearDupes` | `Bool` |  | `false` |
| `sortIt` | `Bool` |  | `false` |

### `cArrayTidyObjRef(aArray, clearNone, clearDupes) → ObjectReference[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `ObjectReference[]` | ✓ |  |
| `clearNone` | `Bool` |  | `false` |
| `clearDupes` | `Bool` |  | `false` |

### `cArrayTidyString(aArray, clearEmpty, clearDupes, sortIt) → String[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `String[]` | ✓ |  |
| `clearEmpty` | `Bool` |  | `false` |
| `clearDupes` | `Bool` |  | `false` |
| `sortIt` | `Bool` |  | `false` |

### `cArrayToFL(aArray, aFormList, useSKSE) → FormList`

**Flags:** Global

Requirements: None, SKSE:Soft

>>> to/from Array

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `Form[]` | ✓ |  |
| `aFormList` | `FormList` | ✓ |  |
| `useSKSE` | `Bool` |  | `true` |

### `cArrayTranslateTo(aObj, pArray, speed, maxSpeed)`

**Flags:** Global

Requirements: None

>>> Accepts placement array as an argument

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aObj` | `ObjectReference` | ✓ |  |
| `pArray` | `Float[]` | ✓ |  |
| `speed` | `Float` |  | `50` |
| `maxSpeed` | `Float` |  | `0` |

### `cBitShiftL(aInt, numShifts) → Int`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aInt` | `Int` | ✓ |  |
| `numShifts` | `Int` | ✓ |  |

### `cBitShiftR(aInt, numShifts) → Int`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aInt` | `Int` | ✓ |  |
| `numShifts` | `Int` | ✓ |  |

### `cBitwiseOp(i1, i2, iBits, iOp, bWarn) → Int`

**Flags:** Global

Requirements: None

31 bitwise operations. Returns a negative number on errors
  Set iBits lower to limit the bitmask to the lower bits for efficiency - Def = 31bits
    Set bOp for the bitwise operation. 0 = NOT, 1 = AND(default), 2 = OR, 3 = XOR
      Set bWarn to True if you are too lazy to check the error return value, and want a notification
 Code from Milagros Osorio http://www.gamesas.com/bitwise-ops-t256983.html

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `i1` | `Int` | ✓ |  |
| `i2` | `Int` | ✓ |  |
| `iBits` | `Int` |  | `31` |
| `iOp` | `Int` |  | `1` |
| `bWarn` | `Bool` |  | `false` |

### `cClampFloat(aValue, minV, maxV, usePapUtil) → Float`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

>>> Conditional manipulation

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aValue` | `Float` | ✓ |  |
| `minV` | `Float` | ✓ |  |
| `maxV` | `Float` | ✓ |  |
| `usePapUtil` | `Bool` |  | `true` |

### `cClampInt(aValue, minV, maxV, usePapUtil) → Int`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aValue` | `Int` | ✓ |  |
| `minV` | `Int` | ✓ |  |
| `maxV` | `Int` | ✓ |  |
| `usePapUtil` | `Bool` |  | `true` |

### `cColoredText(aString, ddInstalled, textColorHex, trimWhere) → String`

**Flags:** Global

Requirements: SKSE:Hard, SkyUI:Soft unsure if hard

>>> Returns text with MCM menu color formatting

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aString` | `String` | ✓ |  |
| `ddInstalled` | `Bool` |  | `false` |
| `textColorHex` | `String` |  | `""` |
| `trimWhere` | `String` |  | `""` |

### `cD2H(aInt, useSKSE) → String`

**Flags:** Global

Requirements: None, SKSE:Soft

>>> Hex <-> Decimal conversion

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aInt` | `Int` | ✓ |  |
| `useSKSE` | `Bool` |  | `true` |

### `cDynamicComparisonBool(operators, this, that) → Bool`

**Flags:** Global

Requirements: None

Valid operators: ==, !=, &&, ||, !&&, &&!, !&&!, !||, ||!, !||!
e.g. !&& == !this && that ; &&! == this && !that ; !&&! == !this && !that

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `operators` | `String` | ✓ |  |
| `this` | `Bool` | ✓ |  |
| `that` | `Bool` | ✓ |  |

### `cDynamicComparisonFloat(operators, this, that) → Bool`

**Flags:** Global

Requirements: None

Valid operators: ==, !=, <, <=, >, >=, &&, ||, !&&, &&!, !&&!, !||, ||!, !||!
e.g. !&& == !this && that ; &&! == this && !that ; !&&! == !this && !that

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `operators` | `String` | ✓ |  |
| `this` | `Float` | ✓ |  |
| `that` | `Float` | ✓ |  |

### `cDynamicComparisonForm(operators, this, that) → Bool`

**Flags:** Global

Requirements: None

Valid operators: ==, !=, &&, ||, !&&, &&!, !&&!, !||, ||!, !||!
e.g. !&& == !this && that ; &&! == this && !that ; !&&! == !this && !that

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `operators` | `String` | ✓ |  |
| `this` | `Form` | ✓ |  |
| `that` | `Form` | ✓ |  |

### `cDynamicComparisonInt(operators, this, that) → Bool`

**Flags:** Global

Requirements: None

Valid operators: ==, !=, <, <=, >, >=, &&, ||, !&&, &&!, !&&!, !||, ||!, !||!
e.g. !&& == !this && that ; &&! == this && !that ; !&&! == !this && !that

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `operators` | `String` | ✓ |  |
| `this` | `Int` | ✓ |  |
| `that` | `Int` | ✓ |  |

### `cDynamicComparisonString(operators, this, that) → Bool`

**Flags:** Global

Requirements: None

Valid operators: ==, !=, <, <=, >, >=, &&, ||, !&&, &&!, !&&!, !||, ||!, !||!
e.g. !&& == !this && that ; &&! == this && !that ; !&&! == !this && !that

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `operators` | `String` | ✓ |  |
| `this` | `String` | ✓ |  |
| `that` | `String` | ✓ |  |

### `cDynamicOperationFloat(operation, this, that) → Float`

**Flags:** Global

Requirements: None

Valid operators: +, -, /, *, **, ^, pow

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `operation` | `String` | ✓ |  |
| `this` | `Float` | ✓ |  |
| `that` | `Float` | ✓ |  |

### `cDynamicOperationInt(operation, this, that) → Int`

**Flags:** Global

Requirements: None

Valid operators: +, -, /, *, <<, leftshift, lshift, >>, rightshift, rshift, AND, NOT, OR, XOR

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `operation` | `String` | ✓ |  |
| `this` | `Int` | ✓ |  |
| `that` | `Int` | ✓ |  |

### `cDynamicOperationString(operation, this, s) → String`

**Flags:** Global

Requirements: None

Valid operators: +s, s+, +s+

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `operation` | `String` | ✓ |  |
| `this` | `String` | ✓ |  |
| `s` | `String` | ✓ |  |

### `cErrArrInitFail(functionName, arrayName, returnValue, errorLevel, condition, useConsoleUtil)`

**Flags:** Global

Requirements: None, ConsoleUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `functionName` | `String` | ✓ |  |
| `arrayName` | `String` |  | `"ewArray"` |
| `returnValue` | `String` |  | `"rrayNone"` |
| `errorLevel` | `Int` |  | `2` |
| `condition` | `Bool` |  | `true` |
| `useConsoleUtil` | `Bool` |  | `true` |

### `cErrInvalidArg(functionName, argName, returnValue, errorLevel, condition, useSKSE, useConsoleUtil)`

**Flags:** Global

Requirements: None, ConsoleUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `functionName` | `String` | ✓ |  |
| `argName` | `String` |  | `""` |
| `returnValue` | `String` |  | `""` |
| `errorLevel` | `Int` |  | `2` |
| `condition` | `Bool` |  | `true` |
| `useSKSE` | `Bool` |  | `true` |
| `useConsoleUtil` | `Bool` |  | `true` |

### `cErrReqDisabled(functionName, modName, returnValue, errorLevel, condition, useConsoleUtil)`

**Flags:** Global

Requirements: None, ConsoleUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `functionName` | `String` | ✓ |  |
| `modName` | `String` |  | `"KSE"` |
| `returnValue` | `String` |  | `""` |
| `errorLevel` | `Int` |  | `2` |
| `condition` | `Bool` |  | `true` |
| `useConsoleUtil` | `Bool` |  | `true` |

### `cFLFindByName(aFormList, aName, bySubStr) → Int`

**Flags:** Global

Requirements: SKSE

Query

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aFormList` | `FormList` | ✓ |  |
| `aName` | `String` | ✓ |  |
| `bySubStr` | `Bool` |  | `true` |

### `cFLReplaceValue(aFormlist, replaceThis, withThis, forceAdd) → Bool`

**Flags:** Global

Requirements: None

Bool return is whether or not the replaced value is still there (can only remove ADDED forms)
forceAdd forces the value to be added even if replaceThis can't be removed
 a return of TRUE == success, False == failed

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aFormlist` | `FormList` | ✓ |  |
| `replaceThis` | `Form` | ✓ |  |
| `withThis` | `Form` | ✓ |  |
| `forceAdd` | `Bool` |  | `false` |

### `cFLToArray(aFormList, useSKSE) → Form[]`

**Flags:** Global

Requirements: None, SKSE:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aFormList` | `FormList` | ✓ |  |
| `useSKSE` | `Bool` |  | `true` |

### `cGetAllEquippedForms(aActor, slot) → Form[]`

**Flags:** Global

Requirement: None

>>> Inventory functions

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aActor` | `Actor` | ✓ |  |
| `slot` | `Int` |  | `-1` |

### `cGetCellCKCoordsArray(aObjectRef) → Int[]`

**Flags:** Global

Requirements: None

Grid Map for reference
https://docs.google.com/spreadsheets/d/1yhsNb12btLWpRNRIpZ2DfjVsWR946qEZTmVML_Wi9U8/edit?usp=sharing

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aObjectRef` | `ObjectReference` | ✓ |  |

### `cGetCellXYAsString(aObjectRef) → String`

**Flags:** Global

Requirements: None

Learned from Ashen thanks!!!
 Player Cell Position: https://www.nexusmods.com/skyrimspecialedition/mods/46173
 Convenience for display/messages
Grid Map for reference
https://docs.google.com/spreadsheets/d/1yhsNb12btLWpRNRIpZ2DfjVsWR946qEZTmVML_Wi9U8/edit?usp=sharing

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aObjectRef` | `ObjectReference` | ✓ |  |

### `cGetCKCoordsFromXY(xVar, yVar, aObjectRef) → Int[]`

**Flags:** Global

Requirements: None

If aObjectRef is provided then xVar and yVar are overwritten by its position
Grid Map for reference
https://docs.google.com/spreadsheets/d/1yhsNb12btLWpRNRIpZ2DfjVsWR946qEZTmVML_Wi9U8/edit?usp=sharing

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `xVar` | `Float` | ✓ |  |
| `yVar` | `Float` | ✓ |  |
| `aObjectRef` | `ObjectReference` |  |  |

### `cGetCoCXYFromCKCoords(ckX, ckY, aObjectRef) → Float[]`

**Flags:** Global

Requirements: None

If aObjectRef is provided then ckX and ckY are overwritten by its position
Grid Map for reference
https://docs.google.com/spreadsheets/d/1yhsNb12btLWpRNRIpZ2DfjVsWR946qEZTmVML_Wi9U8/edit?usp=sharing

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ckX` | `Int` | ✓ |  |
| `ckY` | `Int` | ✓ |  |
| `aObjectRef` | `ObjectReference` |  |  |

### `cGetForm(decForm, hexForm, modName) → Form`

**Flags:** Global

Requirements: None

====== Query/Analysis
>>> resolve form

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `decForm` | `Int` | ✓ |  |
| `hexForm` | `String` |  | `""` |
| `modName` | `String` |  | `""` |

### `cGetHexIDFromForm(aForm) → String`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aForm` | `Form` | ✓ |  |

### `cGetHexSubID(decForm, hexForm, aForm) → String`

**Flags:** Global

Requirements: None

Returns last 3 hex digits for light or 6 in regular. Input for this function assumes some prior
  validation. FormIDs must be 'fully loaded' (e.g. hexForm must be 8 digits). Using aForm argument
    requires that it be currently loaded but decForm || hexForm arguments does not

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `decForm` | `Int` | ✓ |  |
| `hexForm` | `String` |  | `""` |
| `aForm` | `Form` |  |  |

### `cGetInheritedOwner(aObjectRef) → Faction`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aObjectRef` | `ObjectReference` | ✓ |  |

### `cGetIntSubID(decForm, hexForm, aForm) → Int`

**Flags:** Global

Requirements: None

used in GetFormFromFile

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `decForm` | `Int` | ✓ |  |
| `hexForm` | `String` |  | `""` |
| `aForm` | `Form` |  |  |

### `cGetItemName(aForm, simple) → String`

**Flags:** Global

Requirements: None

!simple appends "No Name-" to hexFormID return

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aForm` | `Form` | ✓ |  |
| `simple` | `Bool` |  | `false` |

### `cGetModName(hexForm, decForm, formVar) → String`

**Flags:** Global

Requirements: SKSE

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `hexForm` | `String` |  | `""` |
| `decForm` | `Int` |  | `0` |
| `formVar` | `Form` |  |  |

### `cGetModNameForm(aForm) → String`

**Flags:** Global

Requirements: SKSE

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aForm` | `Form` | ✓ |  |

### `cGetOwner(aObjectRef) → Form`

**Flags:** Global

Requirements: None

>>> Determine ownership

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aObjectRef` | `ObjectReference` | ✓ |  |

### `cGetScriptName() → String`

**Flags:** Global

Requirements: None

### `cGetSKSEType(aForm, useSKSE) → Int`

**Flags:** Global

Requirements: None, SKSE:Soft

Non-SKSE version only works for inventory items that use 'vendor' type keywords which means that
  more obscure items could be missed but it covers quite a lot tbh (10,664 references)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aForm` | `Form` | ✓ |  |
| `useSKSE` | `Bool` |  | `true` |

### `cGetVersion() → Int`

**Flags:** Global

Requirements: None

### `cH2D(aString) → Int`

**Flags:** Global

Requirements: None, SKSE:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aString` | `String` | ✓ |  |

### `cIDInVanillaRange(decForm, hexForm, aForm) → Bool`

**Flags:** Global

Requirements: None

Requires full formID of a loaded plugin
Checks if the dec FormID value is between 0 and SSEEdit value for next form in Dragonborn.esm
NOTE: Injected records cannot be differentiated. This does not mean the form is valid, only that it's in range
  however, apart from this caveat it does confirm that it is *not* from a mod use cGetForm to test validity

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `decForm` | `Int` | ✓ |  |
| `hexForm` | `String` |  | `""` |
| `aForm` | `Form` |  |  |

### `cIfActor(this, that) → Actor`

**Flags:** Global

Requirements: None

>>> Simple version when returns ARE the conditions
>>> Single value returns

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `this` | `Actor` | ✓ |  |
| `that` | `Actor` | ✓ |  |

### `cIfAlias(this, that) → Alias`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `this` | `Alias` | ✓ |  |
| `that` | `Alias` | ✓ |  |

### `cIfArrayActor(this, that) → Actor[]`

**Flags:** Global

Requirements: None

>>> Array returns

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `this` | `Actor[]` | ✓ |  |
| `that` | `Actor[]` | ✓ |  |

### `cIfArrayAlias(this, that) → Alias[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `this` | `Alias[]` | ✓ |  |
| `that` | `Alias[]` | ✓ |  |

### `cIfArrayBool(this, that) → Bool[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `this` | `Bool[]` | ✓ |  |
| `that` | `Bool[]` | ✓ |  |

### `cIfArrayFloat(this, that) → Float[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `this` | `Float[]` | ✓ |  |
| `that` | `Float[]` | ✓ |  |

### `cIfArrayForm(this, that) → Form[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `this` | `Form[]` | ✓ |  |
| `that` | `Form[]` | ✓ |  |

### `cIfArrayInt(this, that) → Int[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `this` | `Int[]` | ✓ |  |
| `that` | `Int[]` | ✓ |  |

### `cIfArrayObjRef(this, that) → ObjectReference[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `this` | `ObjectReference[]` | ✓ |  |
| `that` | `ObjectReference[]` | ✓ |  |

### `cIfArrayString(this, that) → String[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `this` | `String[]` | ✓ |  |
| `that` | `String[]` | ✓ |  |

### `cIfFloat(this, that) → Float`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `this` | `Float` | ✓ |  |
| `that` | `Float` | ✓ |  |

### `cIfForm(this, that) → Form`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `this` | `Form` | ✓ |  |
| `that` | `Form` | ✓ |  |

### `cIfInt(this, that) → Int`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `this` | `Int` | ✓ |  |
| `that` | `Int` | ✓ |  |

### `cIfObjRef(this, that) → ObjectReference`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `this` | `ObjectReference` | ✓ |  |
| `that` | `ObjectReference` | ✓ |  |

### `cIfString(this, that) → String`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `this` | `String` | ✓ |  |
| `that` | `String` | ✓ |  |

### `cIsBetweenFloat(aValue, minV, maxV) → Bool`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aValue` | `Float` | ✓ |  |
| `minV` | `Float` | ✓ |  |
| `maxV` | `Float` | ✓ |  |

### `cIsBetweenInt(aValue, minV, maxV) → Bool`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aValue` | `Int` | ✓ |  |
| `minV` | `Int` | ✓ |  |
| `maxV` | `Int` | ✓ |  |

### `cIsContainer(aObjectRef, useSKSE) → Bool`

**Flags:** Global

Requirements: None, SKSE:Soft

>>> Object query

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aObjectRef` | `ObjectReference` | ✓ |  |
| `useSKSE` | `Bool` |  | `true` |

### `cIsEven(aInt) → Bool`

**Flags:** Global

Requirements: None

========================= Math / Logic
>>> Analysis

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aInt` | `Int` | ✓ |  |

### `cIsFloat(aString) → Bool`

**Flags:** Global

Requirements: None

may not work with very small/large numbers

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aString` | `String` | ✓ |  |

### `cIsInAnyMenu() → Bool`

**Flags:** Global

Requirements: SKSE

In my experience more accurate thatn .IsInMenuMode()

### `cIsInt(aString) → Bool`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aString` | `String` | ✓ |  |

### `cIsLight(hexForm, decForm, formVar, useSKSE) → Bool`

**Flags:** Global

Requirements: None, SKSE:Soft

====== Mod Functions

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `hexForm` | `String` |  | `""` |
| `decForm` | `Int` |  | `0` |
| `formVar` | `Form` |  |  |
| `useSKSE` | `Bool` |  | `true` |

### `cIsOdd(aInt) → Bool`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aInt` | `Int` | ✓ |  |

### `cIsPlayerOwner(aObjectRef, playerAct) → Bool`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aObjectRef` | `ObjectReference` | ✓ |  |
| `playerAct` | `Actor` |  |  |

### `clibTrace(functionName, msg, errorLevel, condition, useConsoleUtil)`

**Flags:** Global

Requirements: None, ConsoleUtil:Soft

Functions used to output error messages

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `functionName` | `String` | ✓ |  |
| `msg` | `String` | ✓ |  |
| `errorLevel` | `Int` | ✓ |  |
| `condition` | `Bool` |  | `true` |
| `useConsoleUtil` | `Bool` |  | `true` |

### `cLogicalAND(i1, i2, useSKSE) → Int`

**Flags:** Global

Requirements: None, SKSE:Soft

--> cBitwiseOp provides non-SKSE functionality for these

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `i1` | `Int` | ✓ |  |
| `i2` | `Int` | ✓ |  |
| `useSKSE` | `Bool` |  | `true` |

### `cLogicalNOT(i1, useSKSE) → Int`

**Flags:** Global

Requirements: None, SKSE:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `i1` | `Int` | ✓ |  |
| `useSKSE` | `Bool` |  | `true` |

### `cLogicalOR(i1, i2, useSKSE) → Int`

**Flags:** Global

Requirements: None, SKSE:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `i1` | `Int` | ✓ |  |
| `i2` | `Int` | ✓ |  |
| `useSKSE` | `Bool` |  | `true` |

### `cLogicalXOR(i1, i2, useSKSE) → Int`

**Flags:** Global

Requirements: None, SKSE:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `i1` | `Int` | ✓ |  |
| `i2` | `Int` | ✓ |  |
| `useSKSE` | `Bool` |  | `true` |

### `cMapAdd(keyName, aValue, aArray) → String[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `keyName` | `String` | ✓ |  |
| `aValue` | `String` | ✓ |  |
| `aArray` | `String[]` | ✓ |  |

### `cMapAddForm(keyName, aForm, aArray, useSKSE) → String[]`

**Flags:** Global

Requirements: SKSE

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `keyName` | `String` | ✓ |  |
| `aForm` | `Form` | ✓ |  |
| `aArray` | `String[]` | ✓ |  |
| `useSKSE` | `Bool` |  | `true` |

### `cMapCreate(keyName, aValue, numKeyPairs, useSKSE) → String[]`

**Flags:** Global

Requirements: None, SKSE:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `keyName` | `String` | ✓ |  |
| `aValue` | `String` |  | `""` |
| `numKeyPairs` | `Int` |  | `64` |
| `useSKSE` | `Bool` |  | `true` |

### `cMapFirstFree(aArray) → Int`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `String[]` | ✓ |  |

### `cMapGet(keyName, aArray) → String`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `keyName` | `String` | ✓ |  |
| `aArray` | `String[]` | ✓ |  |

### `cMapGetBool(keyName, aArray) → Bool`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `keyName` | `String` | ✓ |  |
| `aArray` | `String[]` | ✓ |  |

### `cMapGetFloat(keyName, aArray) → Float`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `keyName` | `String` | ✓ |  |
| `aArray` | `String[]` | ✓ |  |

### `cMapGetForm(keyName, aArray) → Form`

**Flags:** Global

Requirements: SKSE

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `keyName` | `String` | ✓ |  |
| `aArray` | `String[]` | ✓ |  |

### `cMapGetInt(keyName, aArray) → Int`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `keyName` | `String` | ✓ |  |
| `aArray` | `String[]` | ✓ |  |

### `cMapHasKey(keyName, aArray) → Bool`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `keyName` | `String` | ✓ |  |
| `aArray` | `String[]` | ✓ |  |

### `cMapRemove(keyName, aArray) → String[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `keyName` | `String` | ✓ |  |
| `aArray` | `String[]` | ✓ |  |

### `cMapSet(keyName, aValue, aArray) → String[]`

**Flags:** Global

Requirements: None

>>> MAP FUNCTIONS

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `keyName` | `String` | ✓ |  |
| `aValue` | `String` | ✓ |  |
| `aArray` | `String[]` | ✓ |  |

### `cModPerkPoints(number) → Bool`

**Flags:** Global

Requirements: SKSE

NOT compatible with Vokriinator Black!!

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `number` | `Int` |  | `1` |

### `cOrActor(this, that, orThat2, orThat3, orThat4, orThat5, orThat6, orThat7, orThat8, orThat9) → Actor`

**Flags:** Global

Requirements: None

>>> Longer chains of conditional values

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `this` | `Actor` | ✓ |  |
| `that` | `Actor` | ✓ |  |
| `orThat2` | `Actor` |  |  |
| `orThat3` | `Actor` |  |  |
| `orThat4` | `Actor` |  |  |
| `orThat5` | `Actor` |  |  |
| `orThat6` | `Actor` |  |  |
| `orThat7` | `Actor` |  |  |
| `orThat8` | `Actor` |  |  |
| `orThat9` | `Actor` |  |  |

### `cOrAlias(this, that, orThat2, orThat3, orThat4, orThat5, orThat6, orThat7, orThat8, orThat9) → Alias`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `this` | `Alias` | ✓ |  |
| `that` | `Alias` | ✓ |  |
| `orThat2` | `Alias` |  |  |
| `orThat3` | `Alias` |  |  |
| `orThat4` | `Alias` |  |  |
| `orThat5` | `Alias` |  |  |
| `orThat6` | `Alias` |  |  |
| `orThat7` | `Alias` |  |  |
| `orThat8` | `Alias` |  |  |
| `orThat9` | `Alias` |  |  |

### `cOrFloat(this, that, orThat2, orThat3, orThat4, orThat5, orThat6, orThat7, orThat8, orThat9) → Float`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `this` | `Float` | ✓ |  |
| `that` | `Float` | ✓ |  |
| `orThat2` | `Float` |  | `0` |
| `orThat3` | `Float` |  | `0` |
| `orThat4` | `Float` |  | `0` |
| `orThat5` | `Float` |  | `0` |
| `orThat6` | `Float` |  | `0` |
| `orThat7` | `Float` |  | `0` |
| `orThat8` | `Float` |  | `0` |
| `orThat9` | `Float` |  | `0` |

### `cOrForm(this, that, orThat2, orThat3, orThat4, orThat5) → Form`

**Flags:** Global

Requirements: None

Unnecessary really but I found it online and figured I'd include it

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `this` | `Form` | ✓ |  |
| `that` | `Form` | ✓ |  |
| `orThat2` | `Form` |  |  |
| `orThat3` | `Form` |  |  |
| `orThat4` | `Form` |  |  |
| `orThat5` | `Form` |  |  |

### `cOrInt(this, that, orThat2, orThat3, orThat4, orThat5) → Int`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `this` | `Int` | ✓ |  |
| `that` | `Int` | ✓ |  |
| `orThat2` | `Int` |  | `0` |
| `orThat3` | `Int` |  | `0` |
| `orThat4` | `Int` |  | `0` |
| `orThat5` | `Int` |  | `0` |

### `cOrObjRef(this, that, orThat2, orThat3, orThat4, orThat5, orThat6, orThat7, orThat8, orThat9) → ObjectReference`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `this` | `ObjectReference` | ✓ |  |
| `that` | `ObjectReference` | ✓ |  |
| `orThat2` | `ObjectReference` |  |  |
| `orThat3` | `ObjectReference` |  |  |
| `orThat4` | `ObjectReference` |  |  |
| `orThat5` | `ObjectReference` |  |  |
| `orThat6` | `ObjectReference` |  |  |
| `orThat7` | `ObjectReference` |  |  |
| `orThat8` | `ObjectReference` |  |  |
| `orThat9` | `ObjectReference` |  |  |

### `cOrString(this, that, orThat2, orThat3, orThat4, orThat5) → String`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `this` | `String` | ✓ |  |
| `that` | `String` | ✓ |  |
| `orThat2` | `String` |  | `""` |
| `orThat3` | `String` |  | `""` |
| `orThat4` | `String` |  | `""` |
| `orThat5` | `String` |  | `""` |

### `cPseudoSwitchAlias(case, elseDefault, case0, case1, case2, case3, case4, case5, case6, case7, case8, case9) → Alias`

**Flags:** Global

Requirements: None

>>> Pseudo-switch statements

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `case` | `Int` | ✓ |  |
| `elseDefault` | `Alias` | ✓ |  |
| `case0` | `Alias` | ✓ |  |
| `case1` | `Alias` |  |  |
| `case2` | `Alias` |  |  |
| `case3` | `Alias` |  |  |
| `case4` | `Alias` |  |  |
| `case5` | `Alias` |  |  |
| `case6` | `Alias` |  |  |
| `case7` | `Alias` |  |  |
| `case8` | `Alias` |  |  |
| `case9` | `Alias` |  |  |

### `cPseudoSwitchBool(case, elseDefault, case0, case1, case2, case3, case4, case5, case6, case7, case8, case9) → Bool`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `case` | `Int` | ✓ |  |
| `elseDefault` | `Bool` | ✓ |  |
| `case0` | `Bool` | ✓ |  |
| `case1` | `Bool` |  | `false` |
| `case2` | `Bool` |  | `false` |
| `case3` | `Bool` |  | `false` |
| `case4` | `Bool` |  | `false` |
| `case5` | `Bool` |  | `false` |
| `case6` | `Bool` |  | `false` |
| `case7` | `Bool` |  | `false` |
| `case8` | `Bool` |  | `false` |
| `case9` | `Bool` |  | `false` |

### `cPseudoSwitchFloat(case, elseDefault, case0, case1, case2, case3, case4, case5, case6, case7, case8, case9) → Float`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `case` | `Int` | ✓ |  |
| `elseDefault` | `Float` | ✓ |  |
| `case0` | `Float` | ✓ |  |
| `case1` | `Float` |  | `0` |
| `case2` | `Float` |  | `0` |
| `case3` | `Float` |  | `0` |
| `case4` | `Float` |  | `0` |
| `case5` | `Float` |  | `0` |
| `case6` | `Float` |  | `0` |
| `case7` | `Float` |  | `0` |
| `case8` | `Float` |  | `0` |
| `case9` | `Float` |  | `0` |

### `cPseudoSwitchForm(case, elseDefault, case0, case1, case2, case3, case4, case5, case6, case7, case8, case9) → Form`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `case` | `Int` | ✓ |  |
| `elseDefault` | `Form` | ✓ |  |
| `case0` | `Form` | ✓ |  |
| `case1` | `Form` |  |  |
| `case2` | `Form` |  |  |
| `case3` | `Form` |  |  |
| `case4` | `Form` |  |  |
| `case5` | `Form` |  |  |
| `case6` | `Form` |  |  |
| `case7` | `Form` |  |  |
| `case8` | `Form` |  |  |
| `case9` | `Form` |  |  |

### `cPseudoSwitchInt(case, elseDefault, case0, case1, case2, case3, case4, case5, case6, case7, case8, case9) → Int`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `case` | `Int` | ✓ |  |
| `elseDefault` | `Int` | ✓ |  |
| `case0` | `Int` | ✓ |  |
| `case1` | `Int` |  | `0` |
| `case2` | `Int` |  | `0` |
| `case3` | `Int` |  | `0` |
| `case4` | `Int` |  | `0` |
| `case5` | `Int` |  | `0` |
| `case6` | `Int` |  | `0` |
| `case7` | `Int` |  | `0` |
| `case8` | `Int` |  | `0` |
| `case9` | `Int` |  | `0` |

### `cPseudoSwitchString(case, elseDefault, case0, case1, case2, case3, case4, case5, case6, case7, case8, case9) → String`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `case` | `Int` | ✓ |  |
| `elseDefault` | `String` | ✓ |  |
| `case0` | `String` | ✓ |  |
| `case1` | `String` |  | `""` |
| `case2` | `String` |  | `""` |
| `case3` | `String` |  | `""` |
| `case4` | `String` |  | `""` |
| `case5` | `String` |  | `""` |
| `case6` | `String` |  | `""` |
| `case7` | `String` |  | `""` |
| `case8` | `String` |  | `""` |
| `case9` | `String` |  | `""` |

### `cRandomNumberGenFloat(this, that, usePO3) → Float`

**Flags:** Global

Requirements: None

>>> Random Number Generation (no limitation aside from VM capability at this point)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `this` | `Float` | ✓ |  |
| `that` | `Float` | ✓ |  |
| `usePO3` | `Bool` |  | `true` |

### `cRandomNumberGenInt(this, that, usePO3) → Int`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `this` | `Int` | ✓ |  |
| `that` | `Int` | ✓ |  |
| `usePO3` | `Bool` |  | `true` |

### `cRoundFloat(aFloat, places) → Float`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aFloat` | `Float` | ✓ |  |
| `places` | `Int` |  | `1` |

### `cRoundInt(aInt, places) → Int`

**Flags:** Global

Requirements: None

places == places to the *left*

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aInt` | `Int` | ✓ |  |
| `places` | `Int` |  | `1` |

### `cStringAdd_ed(aString) → String`

**Flags:** Global

Requirements: None

useful for dynamic confirmation, progress, completion, log or error reporting messages.
e.g. The function uses "save", "restore" or "remove" as an function argument.
User's choice: argument == "save"
1. cConcatenate("Do you want to ", argument, "?")              == Do you want to save?
2. cConcatenate(cStringAdd_ing(argument), "...")               == Saving...
3. cConcatenate("Successfully ", cStringAdd_ed(argument), "!") == Successfully saved!
now you can make a single function that adds, saves, restores, clears and/or removes something and use a single
  set of dynamically constructed messages (this is just one part of that)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aString` | `String` | ✓ |  |

### `cStringAdd_ing(aString) → String`

**Flags:** Global

Requirements: None

useful for dynamic confirmation, progress, completion, log or error reporting messages.
e.g. The function uses "save", "restore" or "remove" as an function argument.
User's choice: argument == "save"
1. cConcatenate("Do you want to ", argument, "?")              == Do you want to save?
2. cConcatenate(cStringAdd_ing(argument), "...")               == Saving...
3. cConcatenate("Successfully ", cStringAdd_ed(argument), "!") == Successfully saved!
now you can make a single function that adds, saves, restores, clears and/or removes something and use a single
  set of dynamically constructed messages (this is just one part of that)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aString` | `String` | ✓ |  |

### `cStringAddCommaList(aString0, aString1, aString2, aString3, aString4, aString5, aString6, aString7, aString8, aString9) → String`

**Flags:** Global

Requirements: None

>>> String join and split
 for convenience ensures no comma in front

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aString0` | `String` | ✓ |  |
| `aString1` | `String` | ✓ |  |
| `aString2` | `String` |  | `""` |
| `aString3` | `String` |  | `""` |
| `aString4` | `String` |  | `""` |
| `aString5` | `String` |  | `""` |
| `aString6` | `String` |  | `""` |
| `aString7` | `String` |  | `""` |
| `aString8` | `String` |  | `""` |
| `aString9` | `String` |  | `""` |

### `cStringASCIICheck(aString, builtString, asciiChars) → String`

**Flags:** Global

Requirements: None

Returns next ASCII character in string without SKSE

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aString` | `String` | ✓ |  |
| `builtString` | `String` | ✓ |  |
| `asciiChars` | `String[]` | ✓ |  |

### `cStringCountSubstring(countThis, inThis) → Int`

**Flags:** Global

Requirements: SKSE

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `countThis` | `String` | ✓ |  |
| `inThis` | `String` | ✓ |  |

### `cStringFind(inThis, findThis, startIndex, useSKSE) → Int`

**Flags:** Global

Requirements: None, SKSE:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `inThis` | `String` | ✓ |  |
| `findThis` | `String` | ✓ |  |
| `startIndex` | `Int` |  | `0` |
| `useSKSE` | `Bool` |  | `true` |

### `cStringGetNthChar(aString, n, useSKSE) → String`

**Flags:** Global

Requirements: None, SKSE:Soft

thank you cadpnq for the suggestion that made the non-SKSE version possible!

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aString` | `String` | ✓ |  |
| `n` | `Int` | ✓ |  |
| `useSKSE` | `Bool` |  | `true` |

### `cStringHexCheck(aString, builtString, hexDigits) → String`

**Flags:** Global

Requirements: None

Returns next hex digit in string without SKSE

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aString` | `String` | ✓ |  |
| `builtString` | `String` | ✓ |  |
| `hexDigits` | `String[]` | ✓ |  |

### `cStringHexToArray(aString, useSKSE) → String[]`

**Flags:** Global

Requirements: None, SKSE:Soft

Non-SKSE version only has to look through the *16* hex digits as opposed to all 69 ASCII chars

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aString` | `String` | ✓ |  |
| `useSKSE` | `Bool` |  | `true` |

### `cStringIsDigit(aDigit, useSKSE) → Bool`

**Flags:** Global

Requirements: None, SKSE:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aDigit` | `String` | ✓ |  |
| `useSKSE` | `Bool` |  | `true` |

### `cStringIsLetter(aLetter, useSKSE) → Bool`

**Flags:** Global

Requirements: None, SKSE:Soft

Like the SKSE version, the non-SKSE version only checks the first char
thank you cadpnq for the suggestion that made the non-SKSE version possible!

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aLetter` | `String` | ✓ |  |
| `useSKSE` | `Bool` |  | `true` |

### `cStringIsMiscChar(aChar) → Bool`

**Flags:** Global

Requirements: None

This is !cStringIsDigit(aChar) && !cStringIsLetter(aChar)
thank you cadpnq for the suggestion that made the non-SKSE version possible!

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aChar` | `String` | ✓ |  |

### `cStringJoin(aArray, delimiterString, usePapUtil) → String`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aArray` | `String[]` | ✓ |  |
| `delimiterString` | `String` |  | `""` |
| `usePapUtil` | `Bool` |  | `true` |

### `cStringLeft(aString, numChars, useSKSE) → String`

**Flags:** Global

Requirements: None, SKSE:Soft

thank you cadpnq for the suggestion that made the non-SKSE version possible!

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aString` | `String` | ✓ |  |
| `numChars` | `Int` | ✓ |  |
| `useSKSE` | `Bool` |  | `true` |

### `cStringLength(aString, useSKSE) → Int`

**Flags:** Global

Requirements: None, SKSE:Soft

Because the entire string must be parsed to calculate the size it is recommended to combine string handling
  functions if possible. Non-SKSE max length 128
thank you cadpnq for the suggestion that made the non-SKSE version possible!

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aString` | `String` | ✓ |  |
| `useSKSE` | `Bool` |  | `true` |

### `cStringRemove(aString, toRemove) → String`

**Flags:** Global

Requirements: None

>>> String truncation
 Convenience version of cStringReplace()

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aString` | `String` | ✓ |  |
| `toRemove` | `String` | ✓ |  |

### `cStringRepeat(repeatThis, thisManyTimes) → String`

**Flags:** Global

Requirements: None

>>> Generation

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `repeatThis` | `String` | ✓ |  |
| `thisManyTimes` | `Int` | ✓ |  |

### `cStringReplace(aString, toReplace, withWhat, useSKSE) → String`

**Flags:** Global

Requirements: None, SKSE:Soft

>>> Manipulation

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aString` | `String` | ✓ |  |
| `toReplace` | `String` | ✓ |  |
| `withWhat` | `String` |  | `""` |
| `useSKSE` | `Bool` |  | `true` |

### `cStringRight(aString, numChars, useSKSE) → String`

**Flags:** Global

Requirements: None, SKSE:Soft

thank you cadpnq for the suggestion that made the non-SKSE version possible!

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aString` | `String` | ✓ |  |
| `numChars` | `Int` | ✓ |  |
| `useSKSE` | `Bool` |  | `true` |

### `cStringSetLength(aString, stringLength, filler) → String`

**Flags:** Global

Requirements: None

Think of this as a combination of 'ArrayResize' a 'string length clamp' for a string
filler can be any length desired so long as the string doesn't exceed 128 chars!
thank you cadpnq for the suggestion that made the non-SKSE version possible!

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aString` | `String` | ✓ |  |
| `stringLength` | `Int` | ✓ |  |
| `filler` | `String` |  | `""` |

### `cStringSetNthChar(aString, a1stCharIndex, withThis1st, a2ndCharIndex, withThis2nd, a3rdCharIndex, withThis3rd) → String`

**Flags:** Global

Requirements: None

withThis2nd or 3rd == "*&^" allows mix/match of ""

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aString` | `String` | ✓ |  |
| `a1stCharIndex` | `Int` | ✓ |  |
| `withThis1st` | `String` |  | `""` |
| `a2ndCharIndex` | `Int` |  | `0` |
| `withThis2nd` | `String` |  | `"&^"` |
| `a3rdCharIndex` | `Int` |  | `0` |
| `withThis3rd` | `String` |  | `"&^"` |

### `cStringSubString(aString, startChar, numChars, useSKSE) → String`

**Flags:** Global

Requirements None, SKSE:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aString` | `String` | ✓ |  |
| `startChar` | `Int` | ✓ |  |
| `numChars` | `Int` |  | `0` |
| `useSKSE` | `Bool` |  | `true` |

### `cStringToArray(aString, numChars, useSKSE) → String[]`

**Flags:** Global

Requirements: None, SKSE:Soft

Splits a string into an array of its characters

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aString` | `String` | ✓ |  |
| `numChars` | `Int` |  | `-1` |
| `useSKSE` | `Bool` |  | `true` |

### `cStringTrim(aString, charToTrim, useSKSE) → String`

**Flags:** Global

Requirements: None, SKSE:Soft

rewritten to allow charToTrim to be longer than one char in ***SKSE version only!*** One char only in non-SKSE
thank you cadpnq for the suggestion that made the non-SKSE version possible!

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aString` | `String` | ✓ |  |
| `charToTrim` | `String` |  | `""` |
| `useSKSE` | `Bool` |  | `true` |

### `cStringTrimLeft(aString, charToTrim, useSKSE) → String`

**Flags:** Global

Requirements: None, SKSE:Soft

charToTrim cannot be longer than one char (it will trim more than one just the string length can't be > 1)
thank you cadpnq for the suggestion that made the non-SKSE version possible!

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aString` | `String` | ✓ |  |
| `charToTrim` | `String` |  | `""` |
| `useSKSE` | `Bool` |  | `true` |

### `cStringTrimRight(aString, charToTrim, useSKSE) → String`

**Flags:** Global

Requirements: None, SKSE:Soft

rewritten to allow charToTrim to be longer than one char in ***SKSE version only!*** One char only in non-SKSE
thank you cadpnq for the suggestion that made the non-SKSE version possible!

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aString` | `String` | ✓ |  |
| `charToTrim` | `String` |  | `""` |
| `useSKSE` | `Bool` |  | `true` |

### `cTernaryActor(ifThis, returnThis, elseThis) → Actor`

**Flags:** Global

Requirements: None

========================= Conditional statements
 NOTE: These functions can't short circuit like a traditional ternary. Thus, they're most efficient if only
   one argument is a function. If both are functions they *both* will be run before returning a value;
   Nesting these functions is perfectly fine with numbers or operator calculations but know this: nesting
   with multiple functions as arguments will results in *allllll* of the function being called. Use of
   traditional if/then is recommended in those cases. Nexting ternary functions *inside* if thens works
   great though (and will still shave lines off)
>>> Single value returns

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ifThis` | `Bool` | ✓ |  |
| `returnThis` | `Actor` | ✓ |  |
| `elseThis` | `Actor` |  |  |

### `cTernaryAlias(ifThis, returnThis, elseThis) → Alias`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ifThis` | `Bool` | ✓ |  |
| `returnThis` | `Alias` | ✓ |  |
| `elseThis` | `Alias` |  |  |

### `cTernaryArrayActor(ifThis, returnThis, elseThis) → Actor[]`

**Flags:** Global

Requirements: None

>>> Array returns

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ifThis` | `Bool` | ✓ |  |
| `returnThis` | `Actor[]` | ✓ |  |
| `elseThis` | `Actor[]` | ✓ |  |

### `cTernaryArrayAlias(ifThis, returnThis, elseThis) → Alias[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ifThis` | `Bool` | ✓ |  |
| `returnThis` | `Alias[]` | ✓ |  |
| `elseThis` | `Alias[]` | ✓ |  |

### `cTernaryArrayBool(ifThis, returnThis, elseThis) → Bool[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ifThis` | `Bool` | ✓ |  |
| `returnThis` | `Bool[]` | ✓ |  |
| `elseThis` | `Bool[]` | ✓ |  |

### `cTernaryArrayFloat(ifThis, returnThis, elseThis) → Float[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ifThis` | `Bool` | ✓ |  |
| `returnThis` | `Float[]` | ✓ |  |
| `elseThis` | `Float[]` | ✓ |  |

### `cTernaryArrayForm(ifThis, returnThis, elseThis) → Form[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ifThis` | `Bool` | ✓ |  |
| `returnThis` | `Form[]` | ✓ |  |
| `elseThis` | `Form[]` | ✓ |  |

### `cTernaryArrayInt(ifThis, returnThis, elseThis) → Int[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ifThis` | `Bool` | ✓ |  |
| `returnThis` | `Int[]` | ✓ |  |
| `elseThis` | `Int[]` | ✓ |  |

### `cTernaryArrayObjRef(ifThis, returnThis, elseThis) → ObjectReference[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ifThis` | `Bool` | ✓ |  |
| `returnThis` | `ObjectReference[]` | ✓ |  |
| `elseThis` | `ObjectReference[]` | ✓ |  |

### `cTernaryArrayString(ifThis, returnThis, elseThis) → String[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ifThis` | `Bool` | ✓ |  |
| `returnThis` | `String[]` | ✓ |  |
| `elseThis` | `String[]` | ✓ |  |

### `cTernaryFloat(ifThis, returnThis, elseThis) → Float`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ifThis` | `Bool` | ✓ |  |
| `returnThis` | `Float` | ✓ |  |
| `elseThis` | `Float` |  | `0` |

### `cTernaryForm(ifThis, returnThis, elseThis) → Form`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ifThis` | `Bool` | ✓ |  |
| `returnThis` | `Form` | ✓ |  |
| `elseThis` | `Form` |  |  |

### `cTernaryInt(ifThis, returnThis, elseThis) → Int`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ifThis` | `Bool` | ✓ |  |
| `returnThis` | `Int` | ✓ |  |
| `elseThis` | `Int` |  | `0` |

### `cTernaryObjRef(ifThis, returnThis, elseThis) → ObjectReference`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ifThis` | `Bool` | ✓ |  |
| `returnThis` | `ObjectReference` | ✓ |  |
| `elseThis` | `ObjectReference` |  |  |

### `cTernaryString(ifThis, returnThis, elseThis) → String`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ifThis` | `Bool` | ✓ |  |
| `returnThis` | `String` | ✓ |  |
| `elseThis` | `String` |  | `""` |

### `cTotalPerkPoints(aActor, singleSkill) → Int`

**Flags:** Global

Requirements: SKSE

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aActor` | `Actor` | ✓ |  |
| `singleSkill` | `String` |  | `""` |

### `cWrapFloat(aValue, maxValue, minValue, usePapUtil) → Float`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aValue` | `Float` | ✓ |  |
| `maxValue` | `Float` | ✓ |  |
| `minValue` | `Float` |  | `0` |
| `usePapUtil` | `Bool` |  | `true` |

### `cWrapIndex(aValue, endIndex, startIndex, usePapUtil) → Int`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

Adapted from PapyrusUtil function, awesome function!

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aValue` | `Int` | ✓ |  |
| `endIndex` | `Int` | ✓ |  |
| `startIndex` | `Int` |  | `0` |
| `usePapUtil` | `Bool` |  | `true` |

### `cWrapInt(aValue, highVal, lowVal, usePapUtil) → Int`

**Flags:** Global

Requirements: None, PapyrusUtil:Soft

Adapted from PapyrusUtil function, awesome function!

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aValue` | `Int` | ✓ |  |
| `highVal` | `Int` | ✓ |  |
| `lowVal` | `Int` |  | `0` |
| `usePapUtil` | `Bool` |  | `true` |

### `GetAllLightMods() → String[]`

**Flags:** Global

### `GetAllMods() → String[]`

**Flags:** Global

### `GetAllRegularMods() → String[]`

**Flags:** Global

### `p(msg)`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `msg` | `String` | ✓ |  |

---

## Functions

### `cActualSwitchTemplate(case)`

Requirements: None

>>> Actual switch used in message.Show() output

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `case` | `Int` | ✓ |  |

### `cASCII2Binary(char) → String`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `char` | `String` | ✓ |  |

### `cASCII2Hex(char) → String`

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `char` | `String` | ✓ |  |


---

## `clibArrays`

**Source:** `clib` (cLib-Papyrus Function Library) • **Flags:** Hidden

---

## Global Functions

### `cArrayArgumentValidation(indices, outputTrace, type) → Bool`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `outputTrace` | `Bool` | ✓ |  |
| `type` | `String` | ✓ |  |

### `cArrayCreateACHR(indices, filler, outputTrace) → Actor[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Actor` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateActiveMagicEffect(indices, filler, outputTrace) → ActiveMagicEffect[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `ActiveMagicEffect` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateActor(indices, filler, outputTrace) → Actor[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Actor` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateActorBase(indices, filler, outputTrace) → ActorBase[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `ActorBase` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateALCH(indices, filler, outputTrace) → Potion[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Potion` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateAlias(indices, filler, outputTrace) → Alias[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Alias` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateARMO(indices, filler, outputTrace) → Armor[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Armor` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateArmor(indices, filler, outputTrace) → Armor[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Armor` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateBook(indices, filler, outputTrace) → Book[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Book` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateBool(indices, filler, outputTrace) → Bool[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Bool` |  | `false` |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateCell(indices, filler, outputTrace) → Cell[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Cell` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateCOBJ(indices, filler, outputTrace) → ConstructibleObject[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `ConstructibleObject` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateConstructibleObject(indices, filler, outputTrace) → ConstructibleObject[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `ConstructibleObject` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateCONT(indices, filler, outputTrace) → Container[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Container` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateContainer(indices, filler, outputTrace) → Container[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Container` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateDIAL(indices, filler, outputTrace) → TopicInfo[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `TopicInfo` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateEffectShader(indices, filler, outputTrace) → EffectShader[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `EffectShader` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateEFSH(indices, filler, outputTrace) → EffectShader[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `EffectShader` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateENCH(indices, filler, outputTrace) → Enchantment[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Enchantment` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateEnchantment(indices, filler, outputTrace) → Enchantment[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Enchantment` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateFACT(indices, filler, outputTrace) → Faction[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Faction` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateFaction(indices, filler, outputTrace) → Faction[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Faction` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateFloat(indices, filler, outputTrace) → Float[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Float` |  | `0` |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateFLOR(indices, filler, outputTrace) → Flora[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Flora` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateFlora(indices, filler, outputTrace) → Flora[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Flora` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateFLST(indices, filler, outputTrace) → Formlist[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Formlist` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateFood(indices, filler, outputTrace) → Potion[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Potion` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateForm(indices, filler, outputTrace) → Form[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Form` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateFormlist(indices, filler, outputTrace) → Formlist[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Formlist` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateGLOB(indices, filler, outputTrace) → GlobalVariable[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `GlobalVariable` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateGlobalVariable(indices, filler, outputTrace) → GlobalVariable[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `GlobalVariable` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateIMAD(indices, filler, outputTrace)`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `ImageSpaceModifier` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateImageSpaceModifier(indices, filler, outputTrace)`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `ImageSpaceModifier` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateINFO(indices, filler, outputTrace) → Topic[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Topic` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateIngestible(indices, filler, outputTrace) → Potion[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Potion` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateINGR(indices, filler, outputTrace) → Ingredient[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Ingredient` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateIngredient(indices, filler, outputTrace) → Ingredient[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Ingredient` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateInt(indices, filler, outputTrace) → Int[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Int` |  | `0` |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateKeyword(indices, filler, outputTrace) → Keyword[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Keyword` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateKYWD(indices, filler, outputTrace) → Keyword[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Keyword` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateLCTN(indices, filler, outputTrace) → Location[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Location` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateLeveledActor(indices, filler, outputTrace) → LeveledActor[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `LeveledActor` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateLeveledItem(indices, filler, outputTrace) → LeveledItem[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `LeveledItem` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateLeveledSpell(indices, filler, outputTrace) → LeveledSpell[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `LeveledSpell` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateLocation(indices, filler, outputTrace) → Location[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Location` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateLocationAlias(indices, filler, outputTrace) → LocationAlias[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `LocationAlias` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateLVLI(indices, filler, outputTrace) → LeveledItem[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `LeveledItem` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateLVLN(indices, filler, outputTrace) → LeveledActor[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `LeveledActor` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateLVSP(indices, filler, outputTrace) → LeveledSpell[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `LeveledSpell` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateMagicEffect(indices, filler, outputTrace) → MagicEffect[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `MagicEffect` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateMESG(indices, filler, outputTrace) → Message[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Message` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateMessage(indices, filler, outputTrace) → Message[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Message` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateMGEF(indices, filler, outputTrace) → MagicEffect[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `MagicEffect` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateMUSC(indices, filler, outputTrace) → MusicType[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `MusicType` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateMusicType(indices, filler, outputTrace) → MusicType[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `MusicType` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateNPC_(indices, filler, outputTrace) → ActorBase[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `ActorBase` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateObjectReference(indices, filler, outputTrace) → ObjectReference[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `ObjectReference` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateObjRef(indices, filler) → ObjectReference[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `ObjectReference` |  |  |

### `cArrayCreateOTFT(indices, filler, outputTrace) → Outfit[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Outfit` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateOutfit(indices, filler, outputTrace) → Outfit[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Outfit` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreatePACK(indices, filler, outputTrace) → Package[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Package` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreatePackage(indices, filler, outputTrace) → Package[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Package` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreatePerk(indices, filler, outputTrace) → Perk[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Perk` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreatePotion(indices, filler, outputTrace) → Potion[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Potion` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateQuest(indices, filler, outputTrace) → Quest[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Quest` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateQUST(indices, filler, outputTrace) → Quest[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Quest` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateRace(indices, filler, outputTrace) → Race[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Race` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateRefAlias(indices, filler, outputTrace) → ReferenceAlias[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `ReferenceAlias` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateReferenceAlias(indices, filler, outputTrace) → ReferenceAlias[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `ReferenceAlias` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateREFR(indices, filler, outputTrace) → ObjectReference[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `ObjectReference` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateRFCT(indices, filler, outputTrace) → VisualEffect[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `VisualEffect` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateSCEN(indices, filler, outputTrace) → Scene[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Scene` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateScene(indices, filler, outputTrace) → Scene[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Scene` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateSCRL(indices, filler, outputTrace) → Scroll[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Scroll` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateScroll(indices, filler, outputTrace) → Scroll[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Scroll` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateSHOU(indices, filler, outputTrace) → Shout[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Shout` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateShout(indices, filler, outputTrace) → Shout[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Shout` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateSLGM(indices, filler, outputTrace) → SoulGem[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `SoulGem` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateSNCT(indices, filler, outputTrace) → SoundCategory[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `SoundCategory` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateSoulGem(indices, filler, outputTrace) → SoulGem[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `SoulGem` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateSOUN(indices, filler, outputTrace) → Sound[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Sound` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateSound(indices, filler, outputTrace) → Sound[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Sound` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateSoundCategory(indices, filler, outputTrace) → SoundCategory[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `SoundCategory` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateSPEL(indices, filler, outputTrace) → Spell[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Spell` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateSpell(indices, filler, outputTrace) → Spell[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Spell` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateStr(indices, filler, outputTrace) → String[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `String` |  | `""` |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateString(indices, filler, outputTrace) → String[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `String` |  | `""` |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateTextureSet(indices, filler, outputTrace) → TextureSet[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `TextureSet` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateTopic(indices, filler, outputTrace) → Topic[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Topic` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateTopicInfo(indices, filler, outputTrace) → TopicInfo[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `TopicInfo` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateTXST(indices, filler, outputTrace) → TextureSet[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `TextureSet` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateVisualEffect(indices, filler, outputTrace) → VisualEffect[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `VisualEffect` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateWEAP(indices, filler, outputTrace) → Weapon[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Weapon` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateWeapon(indices, filler, outputTrace) → Weapon[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Weapon` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateWeather(indices, filler, outputTrace) → Weather[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Weather` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateWOOP(indices, filler, outputTrace) → WordOfPower[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `WordOfPower` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateWordOfPower(indices, filler, outputTrace) → WordOfPower[]`

**Flags:** Global

Requirements: None

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `WordOfPower` |  |  |
| `outputTrace` | `Bool` |  | `true` |

### `cArrayCreateWTHR(indices, filler, outputTrace) → Weather[]`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `indices` | `Int` | ✓ |  |
| `filler` | `Weather` |  |  |
| `outputTrace` | `Bool` |  | `true` |
