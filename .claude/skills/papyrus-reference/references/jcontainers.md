# `JArray`

**Source:** `jcontainers` (JContainers)

Ordered collection of values (value is float, integer, string, form or another container).
  Inherits JValue functionality

---

## Global Functions

### `addFlt(object, value, addToIndex)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `value` | `Float` | ✓ |  |
| `addToIndex` | `Int` |  | `-1` |

### `addForm(object, value, addToIndex)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `value` | `Form` | ✓ |  |
| `addToIndex` | `Int` |  | `-1` |

### `addFromArray(object, source, insertAtIndex)`

**Flags:** Native Global

Inserts the values from the source array into this array. If insertAtIndex is -1 (default behaviour) it appends to the end.
  negative index accesses items from the end of container counting backwards.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `source` | `Int` | ✓ |  |
| `insertAtIndex` | `Int` |  | `-1` |

### `addFromFormList(object, source, insertAtIndex)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `source` | `FormList` | ✓ |  |
| `insertAtIndex` | `Int` |  | `-1` |

### `addInt(object, value, addToIndex)`

**Flags:** Native Global

Appends the @value/@container to the end of the array.
  If @addToIndex >= 0 it inserts value at given index. negative index accesses items from the end of container counting backwards.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `value` | `Int` | ✓ |  |
| `addToIndex` | `Int` |  | `-1` |

### `addObj(object, container, addToIndex)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `container` | `Int` | ✓ |  |
| `addToIndex` | `Int` |  | `-1` |

### `addStr(object, value, addToIndex)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `value` | `String` | ✓ |  |
| `addToIndex` | `Int` |  | `-1` |

### `asFloatArray(object) → Float[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |

### `asFormArray(object) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |

### `asIntArray(object) → Int[]`

**Flags:** Native Global

Copy all items to new native Papyrus array of dynamic size.
  Items not matching the requested type will have default
  values as the ones from the getInt/Flt/Str/Form functions.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |

### `asStringArray(object) → String[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |

### `clear(object)`

**Flags:** Native Global

Removes all the items from the array

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |

### `count(object) → Int`

**Flags:** Native Global

Returns count of the items in the array

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |

### `countFloat(object, value) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `value` | `Float` | ✓ |  |

### `countForm(object, value) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `value` | `Form` | ✓ |  |

### `countInteger(object, value) → Int`

**Flags:** Native Global

Returns the number of times given value was found in a JArray.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `value` | `Int` | ✓ |  |

### `countObject(object, container) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `container` | `Int` | ✓ |  |

### `countString(object, value) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `value` | `String` | ✓ |  |

### `eraseFloat(object, value) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `value` | `Float` | ✓ |  |

### `eraseForm(object, value) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `value` | `Form` | ✓ |  |

### `eraseIndex(object, index)`

**Flags:** Native Global

Erases the item at the index. negative index accesses items from the end of container counting backwards.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `eraseInteger(object, value) → Int`

**Flags:** Native Global

Erase all elements of given value. Returns the number of erased elements.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `value` | `Int` | ✓ |  |

### `eraseObject(object, container) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `container` | `Int` | ✓ |  |

### `eraseRange(object, first, last)`

**Flags:** Native Global

Erases [first, last] index range of the items. negative index accesses items from the end of container counting backwards.
  For ex. with [1,-1] range it will erase everything except the first item

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `first` | `Int` | ✓ |  |
| `last` | `Int` | ✓ |  |

### `eraseString(object, value) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `value` | `String` | ✓ |  |

### `findFlt(object, value, searchStartIndex) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `value` | `Float` | ✓ |  |
| `searchStartIndex` | `Int` |  | `0` |

### `findForm(object, value, searchStartIndex) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `value` | `Form` | ✓ |  |
| `searchStartIndex` | `Int` |  | `0` |

### `findInt(object, value, searchStartIndex) → Int`

**Flags:** Native Global

Returns the index of the first found value/container that equals to given the value/container (default behaviour if searchStartIndex is 0).
  If nothing was found it returns -1.
  @searchStartIndex - index of the array where to start search
  negative index accesses items from the end of container counting backwards.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `value` | `Int` | ✓ |  |
| `searchStartIndex` | `Int` |  | `0` |

### `findObj(object, container, searchStartIndex) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `container` | `Int` | ✓ |  |
| `searchStartIndex` | `Int` |  | `0` |

### `findStr(object, value, searchStartIndex) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `value` | `String` | ✓ |  |
| `searchStartIndex` | `Int` |  | `0` |

### `getFlt(object, index, default) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `default` | `Float` |  | `0` |

### `getForm(object, index, default) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `default` | `Form` |  |  |

### `getInt(object, index, default) → Int`

**Flags:** Native Global

Returns the item at the index of the array.
  negative index accesses items from the end of container counting backwards.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `default` | `Int` |  | `0` |

### `getObj(object, index, default) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `default` | `Int` |  | `0` |

### `getStr(object, index, default) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `default` | `String` |  | `""` |

### `object() → Int`

**Flags:** Native Global

creates new container object. returns container's identifier (unique integer number).

### `objectWithBooleans(values) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `values` | `Bool[]` | ✓ |  |

### `objectWithFloats(values) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `values` | `Float[]` | ✓ |  |

### `objectWithForms(values) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `values` | `Form[]` | ✓ |  |

### `objectWithInts(values) → Int`

**Flags:** Native Global

Creates a new array that contains given values
  objectWithBooleans converts booleans into integers

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `values` | `Int[]` | ✓ |  |

### `objectWithSize(size) → Int`

**Flags:** Native Global

Creates a new array of given size, filled with empty (None) items

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `size` | `Int` | ✓ |  |

### `objectWithStrings(values) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `values` | `String[]` | ✓ |  |

### `reverse(object) → Int`

**Flags:** Native Global

Reverse the order of elements. Returns the array itself.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |

### `setFlt(object, index, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `value` | `Float` | ✓ |  |

### `setForm(object, index, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `value` | `Form` | ✓ |  |

### `setInt(object, index, value)`

**Flags:** Native Global

Replaces existing value at the @index of the array with the new @value.
  negative index accesses items from the end of container counting backwards.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `value` | `Int` | ✓ |  |

### `setObj(object, index, container)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `container` | `Int` | ✓ |  |

### `setStr(object, index, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `index` | `Int` | ✓ |  |
| `value` | `String` | ✓ |  |

### `sort(object) → Int`

**Flags:** Native Global

Sorts the items into ascending order (none < int < float < form < object < string). Returns the array itself

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |

### `subArray(object, startIndex, endIndex) → Int`

**Flags:** Native Global

Creates a new array containing all the values from the source array in range [startIndex, endIndex)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `startIndex` | `Int` | ✓ |  |
| `endIndex` | `Int` | ✓ |  |

### `swapItems(object, index1, index2)`

**Flags:** Native Global

Exchanges the items at @index1 and @index2. negative index accesses items from the end of container counting backwards.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `index1` | `Int` | ✓ |  |
| `index2` | `Int` | ✓ |  |

### `unique(object) → Int`

**Flags:** Native Global

Sorts the items, removes duplicates. Returns array itself. You can treat it as JSet now

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |

### `valueType(object, index) → Int`

**Flags:** Native Global

Returns type of the value at the @index. negative index accesses items from the end of container counting backwards.
  0 - no value, 1 - none, 2 - int, 3 - float, 4 - form, 5 - object, 6 - string

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `writeToFloatPArray(object, targetArray, writeAtIdx, stopWriteAtIdx, readIdx, defaultRead) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `targetArray` | `Float[]` | ✓ |  |
| `writeAtIdx` | `Int` |  | `0` |
| `stopWriteAtIdx` | `Int` |  | `-1` |
| `readIdx` | `Int` |  | `0` |
| `defaultRead` | `Float` |  | `0` |

### `writeToFormPArray(object, targetArray, writeAtIdx, stopWriteAtIdx, readIdx, defaultRead) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `targetArray` | `Form[]` | ✓ |  |
| `writeAtIdx` | `Int` |  | `0` |
| `stopWriteAtIdx` | `Int` |  | `-1` |
| `readIdx` | `Int` |  | `0` |
| `defaultRead` | `Form` |  |  |

### `writeToIntegerPArray(object, targetArray, writeAtIdx, stopWriteAtIdx, readIdx, defaultRead) → Bool`

**Flags:** Native Global

Writes the array's items into the @targetArray array starting at @destIndex
   @writeAtIdx -    [-1, 0] - writes all the items in reverse order
     [0, -1] - writes all the items in straight order
     [1, 3] - writes 3 items in straight order

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `targetArray` | `Int[]` | ✓ |  |
| `writeAtIdx` | `Int` |  | `0` |
| `stopWriteAtIdx` | `Int` |  | `-1` |
| `readIdx` | `Int` |  | `0` |
| `defaultRead` | `Int` |  | `0` |

### `writeToStringPArray(object, targetArray, writeAtIdx, stopWriteAtIdx, readIdx, defaultRead) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `targetArray` | `String[]` | ✓ |  |
| `writeAtIdx` | `Int` |  | `0` |
| `stopWriteAtIdx` | `Int` |  | `-1` |
| `readIdx` | `Int` |  | `0` |
| `defaultRead` | `String` |  | `""` |


---

## `JAtomic`

**Source:** `jcontainers` (JContainers)

This way you can even, probably, implement true locks and etc

---

## Global Functions

### `compareExchangeFlt(object, path, desired, expected, createMissingKeys, onErrorReturn) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `path` | `String` | ✓ |  |
| `desired` | `Float` | ✓ |  |
| `expected` | `Float` | ✓ |  |
| `createMissingKeys` | `Bool` |  | `false` |
| `onErrorReturn` | `Float` |  | `0` |

### `compareExchangeForm(object, path, desired, expected, createMissingKeys, onErrorReturn) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `path` | `String` | ✓ |  |
| `desired` | `Form` | ✓ |  |
| `expected` | `Form` | ✓ |  |
| `createMissingKeys` | `Bool` |  | `false` |
| `onErrorReturn` | `Form` |  |  |

### `compareExchangeInt(object, path, desired, expected, createMissingKeys, onErrorReturn) → Int`

**Flags:** Native Global

Compares the value at the @path with the @expected and, if they are equal, exchanges the value at the @path with the @desired values.
  Returns previous value.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `path` | `String` | ✓ |  |
| `desired` | `Int` | ✓ |  |
| `expected` | `Int` | ✓ |  |
| `createMissingKeys` | `Bool` |  | `false` |
| `onErrorReturn` | `Int` |  | `0` |

### `compareExchangeObj(object, path, desired, expected, createMissingKeys, onErrorReturn) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `path` | `String` | ✓ |  |
| `desired` | `Int` | ✓ |  |
| `expected` | `Int` | ✓ |  |
| `createMissingKeys` | `Bool` |  | `false` |
| `onErrorReturn` | `Int` |  | `0` |

### `compareExchangeStr(object, path, desired, expected, createMissingKeys, onErrorReturn) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `path` | `String` | ✓ |  |
| `desired` | `String` | ✓ |  |
| `expected` | `String` | ✓ |  |
| `createMissingKeys` | `Bool` |  | `false` |
| `onErrorReturn` | `String` |  | `""` |

### `exchangeFlt(object, path, value, createMissingKeys, onErrorReturn) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `path` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |
| `createMissingKeys` | `Bool` |  | `false` |
| `onErrorReturn` | `Float` |  | `0` |

### `exchangeForm(object, path, value, createMissingKeys, onErrorReturn) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `path` | `String` | ✓ |  |
| `value` | `Form` | ✓ |  |
| `createMissingKeys` | `Bool` |  | `false` |
| `onErrorReturn` | `Form` |  |  |

### `exchangeInt(object, path, value, createMissingKeys, onErrorReturn) → Int`

**Flags:** Native Global

Exchanges the value at the @path with the @value. Returns previous value.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `path` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |
| `createMissingKeys` | `Bool` |  | `false` |
| `onErrorReturn` | `Int` |  | `0` |

### `exchangeObj(object, path, value, createMissingKeys, onErrorReturn) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `path` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |
| `createMissingKeys` | `Bool` |  | `false` |
| `onErrorReturn` | `Int` |  | `0` |

### `exchangeStr(object, path, value, createMissingKeys, onErrorReturn) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `path` | `String` | ✓ |  |
| `value` | `String` | ✓ |  |
| `createMissingKeys` | `Bool` |  | `false` |
| `onErrorReturn` | `String` |  | `""` |

### `fetchAddFlt(object, path, value, initialValue, createMissingKeys, onErrorReturn) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `path` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |
| `initialValue` | `Float` |  | `0` |
| `createMissingKeys` | `Bool` |  | `false` |
| `onErrorReturn` | `Float` |  | `0` |

### `fetchAddInt(object, path, value, initialValue, createMissingKeys, onErrorReturn) → Int`

**Flags:** Native Global

A group of the functions that perform various math on the value at the @path of the container. Returns previos value:

      T previousValue = container.path
      container.path = someMathFunction(container.path, value)
      return previousValue

  If the value at the @path is None, then the @initialValue being read and passed into math function instead of None.
  If @createMissingKeys is True, the function attemps to create missing @path elements.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `path` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |
| `initialValue` | `Int` |  | `0` |
| `createMissingKeys` | `Bool` |  | `false` |
| `onErrorReturn` | `Int` |  | `0` |

### `fetchAndInt(object, path, value, initialValue, createMissingKeys, onErrorReturn) → Int`

**Flags:** Native Global

x &= v

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `path` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |
| `initialValue` | `Int` |  | `0` |
| `createMissingKeys` | `Bool` |  | `false` |
| `onErrorReturn` | `Int` |  | `0` |

### `fetchDivFlt(object, path, value, initialValue, createMissingKeys, onErrorReturn) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `path` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |
| `initialValue` | `Float` |  | `0` |
| `createMissingKeys` | `Bool` |  | `false` |
| `onErrorReturn` | `Float` |  | `0` |

### `fetchDivInt(object, path, value, initialValue, createMissingKeys, onErrorReturn) → Int`

**Flags:** Native Global

x /= v

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `path` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |
| `initialValue` | `Int` |  | `0` |
| `createMissingKeys` | `Bool` |  | `false` |
| `onErrorReturn` | `Int` |  | `0` |

### `fetchModInt(object, path, value, initialValue, createMissingKeys, onErrorReturn) → Int`

**Flags:** Native Global

x %= v

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `path` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |
| `initialValue` | `Int` |  | `0` |
| `createMissingKeys` | `Bool` |  | `false` |
| `onErrorReturn` | `Int` |  | `0` |

### `fetchMultFlt(object, path, value, initialValue, createMissingKeys, onErrorReturn) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `path` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |
| `initialValue` | `Float` |  | `0` |
| `createMissingKeys` | `Bool` |  | `false` |
| `onErrorReturn` | `Float` |  | `0` |

### `fetchMultInt(object, path, value, initialValue, createMissingKeys, onErrorReturn) → Int`

**Flags:** Native Global

x *= v

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `path` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |
| `initialValue` | `Int` |  | `0` |
| `createMissingKeys` | `Bool` |  | `false` |
| `onErrorReturn` | `Int` |  | `0` |

### `fetchOrInt(object, path, value, initialValue, createMissingKeys, onErrorReturn) → Int`

**Flags:** Native Global

x |= v

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `path` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |
| `initialValue` | `Int` |  | `0` |
| `createMissingKeys` | `Bool` |  | `false` |
| `onErrorReturn` | `Int` |  | `0` |

### `fetchXorInt(object, path, value, initialValue, createMissingKeys, onErrorReturn) → Int`

**Flags:** Native Global

x ^= v

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `path` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |
| `initialValue` | `Int` |  | `0` |
| `createMissingKeys` | `Bool` |  | `false` |
| `onErrorReturn` | `Int` |  | `0` |


---

## `JContainers`

**Source:** `jcontainers` (JContainers)

Utility functionality

---

## Global Functions

### `__isInstalled() → Bool`

**Flags:** Native Global

It's NOT part of public API

### `APIVersion() → Int`

**Flags:** Native Global

Version information.
  It's a good practice to validate installed JContainers version with the following code:
      bool isJCValid = JContainers.APIVersion() == AV && JContainers.featureVersion() >= FV
  where AV and FV are hardcoded API and feature version numbers.
  Current API version is 4
  Current feature version is 2

### `contentsOfDirectoryAtPath(directoryPath, extension) → String[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `directoryPath` | `String` | ✓ |  |
| `extension` | `String` |  | `""` |

### `featureVersion() → Int`

**Flags:** Native Global

### `fileExistsAtPath(path) → Bool`

**Flags:** Native Global

Returns true if the file at a specified @path exists

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `path` | `String` | ✓ |  |

### `isInstalled() → Bool`

**Flags:** Global

Returns true if JContainers plugin installed properly

### `removeFileAtPath(path)`

**Flags:** Native Global

Deletes the file or directory identified by the @path

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `path` | `String` | ✓ |  |

### `userDirectory() → String`

**Flags:** Native Global

A path to user-specific directory - My Games/Skyrim Special Edition/JCUser/


---

## `JDB`

**Source:** `jcontainers` (JContainers)

Global entry point to store mod information. Main intent - replace global variables
  Manages keys and values associations (like JMap)

---

## Global Functions

### `allKeys() → Int`

**Flags:** Native Global

returns new array containing all JDB keys

### `allValues() → Int`

**Flags:** Native Global

returns new array containing all containers associated with JDB

### `hasPath(path) → Bool`

**Flags:** Native Global

Returns true, if JDB capable resolve given @path, i.e. if it able to execute solve* or solver*Setter functions successfully

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `path` | `String` | ✓ |  |

### `root() → Int`

**Flags:** Native Global

Returns underlying JDB's container - an instance of JMap.
  The object being owned (retained) internally, so you don't have to (but can) retain or release it.

### `setObj(key, object)`

**Flags:** Native Global

Associates(and replaces previous association) container object with a string key.
  destroys association if object is zero
  for ex. JDB.setObj("frostfall", frostFallInformation) will associate 'frostall' key and frostFallInformation so you can access it later

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `key` | `String` | ✓ |  |
| `object` | `Int` | ✓ |  |

### `solveFlt(path, default) → Float`

**Flags:** Native Global

Attempts to retrieve the value associated with the @path.
  For ex. the following information associated with 'frosfall' key:

  "frostfall" : {
      "exposureRate" : 0.5,
      "arrayC" : ["stringValue", 1.5, 10, 1.14]
  }

  then JDB.solveFlt(".frostfall.exposureRate") will return 0.5 and
  JDB.solveObj(".frostfall.arrayC") will return the array containing ["stringValue", 1.5, 10, 1.14] values

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `path` | `String` | ✓ |  |
| `default` | `Float` |  | `0` |

### `solveFltSetter(path, value, createMissingKeys) → Bool`

**Flags:** Native Global

Attempts to assign the @value. Returns false if no such path.
  If 'createMissingKeys=true' it creates any missing path elements: JDB.solveIntSetter(".frostfall.keyB", 10, true) creates {frostfall: {keyB: 10}} structure

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `path` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |
| `createMissingKeys` | `Bool` |  | `false` |

### `solveForm(path, default) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `path` | `String` | ✓ |  |
| `default` | `Form` |  |  |

### `solveFormSetter(path, value, createMissingKeys) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `path` | `String` | ✓ |  |
| `value` | `Form` | ✓ |  |
| `createMissingKeys` | `Bool` |  | `false` |

### `solveInt(path, default) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `path` | `String` | ✓ |  |
| `default` | `Int` |  | `0` |

### `solveIntSetter(path, value, createMissingKeys) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `path` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |
| `createMissingKeys` | `Bool` |  | `false` |

### `solveObj(path, default) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `path` | `String` | ✓ |  |
| `default` | `Int` |  | `0` |

### `solveObjSetter(path, value, createMissingKeys) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `path` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |
| `createMissingKeys` | `Bool` |  | `false` |

### `solveStr(path, default) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `path` | `String` | ✓ |  |
| `default` | `String` |  | `""` |

### `solveStrSetter(path, value, createMissingKeys) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `path` | `String` | ✓ |  |
| `value` | `String` | ✓ |  |
| `createMissingKeys` | `Bool` |  | `false` |

### `writeToFile(path)`

**Flags:** Native Global

writes storage data into JSON file at given path

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `path` | `String` | ✓ |  |


---

## `JFormDB`

**Source:** `jcontainers` (JContainers)

Manages form related information (entry).

---

## Global Functions

### `allKeys(fKey, key) → Int`

**Flags:** Native Global

JMap-like interface functions:

  returns new array containing all keys

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fKey` | `Form` | ✓ |  |
| `key` | `String` | ✓ |  |

### `allValues(fKey, key) → Int`

**Flags:** Native Global

returns new array containing all values

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fKey` | `Form` | ✓ |  |
| `key` | `String` | ✓ |  |

### `findEntry(storageName, fKey) → Int`

**Flags:** Native Global

search for entry for given storage and form

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `storageName` | `String` | ✓ |  |
| `fKey` | `Form` | ✓ |  |

### `getFlt(fKey, key) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fKey` | `Form` | ✓ |  |
| `key` | `String` | ✓ |  |

### `getForm(fKey, key) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fKey` | `Form` | ✓ |  |
| `key` | `String` | ✓ |  |

### `getInt(fKey, key) → Int`

**Flags:** Native Global

returns value associated with key

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fKey` | `Form` | ✓ |  |
| `key` | `String` | ✓ |  |

### `getObj(fKey, key) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fKey` | `Form` | ✓ |  |
| `key` | `String` | ✓ |  |

### `getStr(fKey, key) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fKey` | `Form` | ✓ |  |
| `key` | `String` | ✓ |  |

### `hasPath(fKey, path) → Bool`

**Flags:** Native Global

returns true, if capable resolve given path, e.g. it able to execute solve* or solver*Setter functions successfully

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fKey` | `Form` | ✓ |  |
| `path` | `String` | ✓ |  |

### `makeEntry(storageName, fKey) → Int`

**Flags:** Native Global

returns (or creates new if not found) JMap entry for given storage and form

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `storageName` | `String` | ✓ |  |
| `fKey` | `Form` | ✓ |  |

### `setEntry(storageName, fKey, entry)`

**Flags:** Native Global

associates given form key and entry (container). set entry to zero to destroy association

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `storageName` | `String` | ✓ |  |
| `fKey` | `Form` | ✓ |  |
| `entry` | `Int` | ✓ |  |

### `setFlt(fKey, key, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fKey` | `Form` | ✓ |  |
| `key` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |

### `setForm(fKey, key, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fKey` | `Form` | ✓ |  |
| `key` | `String` | ✓ |  |
| `value` | `Form` | ✓ |  |

### `setInt(fKey, key, value)`

**Flags:** Native Global

creates key-value association. replaces existing value if any

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fKey` | `Form` | ✓ |  |
| `key` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |

### `setObj(fKey, key, container)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fKey` | `Form` | ✓ |  |
| `key` | `String` | ✓ |  |
| `container` | `Int` | ✓ |  |

### `setStr(fKey, key, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fKey` | `Form` | ✓ |  |
| `key` | `String` | ✓ |  |
| `value` | `String` | ✓ |  |

### `solveFlt(fKey, path, default) → Float`

**Flags:** Native Global

attempts to get value associated with path.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fKey` | `Form` | ✓ |  |
| `path` | `String` | ✓ |  |
| `default` | `Float` |  | `0` |

### `solveFltSetter(fKey, path, value, createMissingKeys) → Bool`

**Flags:** Native Global

Attempts to assign value. Returns false if no such path
  With 'createMissingKeys=true' it creates any missing path elements: JFormDB.solveIntSetter(formKey, ".frostfall.keyB", 10, true) creates {frostfall: {keyB: 10}} structure

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fKey` | `Form` | ✓ |  |
| `path` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |
| `createMissingKeys` | `Bool` |  | `false` |

### `solveForm(fKey, path, default) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fKey` | `Form` | ✓ |  |
| `path` | `String` | ✓ |  |
| `default` | `Form` |  |  |

### `solveFormSetter(fKey, path, value, createMissingKeys) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fKey` | `Form` | ✓ |  |
| `path` | `String` | ✓ |  |
| `value` | `Form` | ✓ |  |
| `createMissingKeys` | `Bool` |  | `false` |

### `solveInt(fKey, path, default) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fKey` | `Form` | ✓ |  |
| `path` | `String` | ✓ |  |
| `default` | `Int` |  | `0` |

### `solveIntSetter(fKey, path, value, createMissingKeys) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fKey` | `Form` | ✓ |  |
| `path` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |
| `createMissingKeys` | `Bool` |  | `false` |

### `solveObj(fKey, path, default) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fKey` | `Form` | ✓ |  |
| `path` | `String` | ✓ |  |
| `default` | `Int` |  | `0` |

### `solveObjSetter(fKey, path, value, createMissingKeys) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fKey` | `Form` | ✓ |  |
| `path` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |
| `createMissingKeys` | `Bool` |  | `false` |

### `solveStr(fKey, path, default) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fKey` | `Form` | ✓ |  |
| `path` | `String` | ✓ |  |
| `default` | `String` |  | `""` |

### `solveStrSetter(fKey, path, value, createMissingKeys) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fKey` | `Form` | ✓ |  |
| `path` | `String` | ✓ |  |
| `value` | `String` | ✓ |  |
| `createMissingKeys` | `Bool` |  | `false` |


---

## `JFormMap`

**Source:** `jcontainers` (JContainers)

Associative key-value container.
  Inherits JValue functionality

---

## Global Functions

### `addPairs(object, source, overrideDuplicates)`

**Flags:** Native Global

Inserts key-value pairs from the source container

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `source` | `Int` | ✓ |  |
| `overrideDuplicates` | `Bool` | ✓ |  |

### `allKeys(object) → Int`

**Flags:** Native Global

Returns a new array containing all keys

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |

### `allKeysPArray(object) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |

### `allValues(object) → Int`

**Flags:** Native Global

Returns a new array containing all values

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |

### `clear(object)`

**Flags:** Native Global

Removes all pairs from the container

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |

### `count(object) → Int`

**Flags:** Native Global

Returns count of pairs in the conainer

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |

### `getFlt(object, key, default) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `Form` | ✓ |  |
| `default` | `Float` |  | `0` |

### `getForm(object, key, default) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `Form` | ✓ |  |
| `default` | `Form` |  |  |

### `getInt(object, key, default) → Int`

**Flags:** Native Global

Returns the value associated with the @key. If not, returns @default value

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `Form` | ✓ |  |
| `default` | `Int` |  | `0` |

### `getNthKey(object, keyIndex) → Form`

**Flags:** Native Global

Retrieves N-th key. negative index accesses items from the end of container counting backwards.
  Worst complexity is O(n/2)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `keyIndex` | `Int` | ✓ |  |

### `getObj(object, key, default) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `Form` | ✓ |  |
| `default` | `Int` |  | `0` |

### `getStr(object, key, default) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `Form` | ✓ |  |
| `default` | `String` |  | `""` |

### `hasKey(object, key) → Bool`

**Flags:** Native Global

Returns true, if the container has @key: value pair

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `Form` | ✓ |  |

### `nextKey(object, previousKey, endKey) → Form`

**Flags:** Native Global

Simplifies iteration over container's contents.
  Accepts the @previousKey, returns the next key.
  If @previousKey == @endKey the function returns the first key.
  The function always returns so-called 'valid' keys (the ones != @endKey).
  The function returns @endKey ('invalid' key) only once to signal that iteration has reached its end.
  In most cases, if the map doesn't contain an invalid key ("" for JMap, None form-key for JFormMap)
  it's ok to omit the @endKey.

  Usage:

      string key = JMap.nextKey(map, previousKey="", endKey="")
      while key != ""
        <retrieve values here>
        key = JMap.nextKey(map, key, endKey="")
      endwhile

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `previousKey` | `Form` |  |  |
| `endKey` | `Form` |  |  |

### `object() → Int`

**Flags:** Native Global

creates new container object. returns container's identifier (unique integer number).

### `removeKey(object, key) → Bool`

**Flags:** Native Global

Removes the pair from the container where the key equals to the @key

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `Form` | ✓ |  |

### `setFlt(object, key, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `Form` | ✓ |  |
| `value` | `Float` | ✓ |  |

### `setForm(object, key, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `Form` | ✓ |  |
| `value` | `Form` | ✓ |  |

### `setInt(object, key, value)`

**Flags:** Native Global

Inserts @key: @value pair. Replaces existing pair with the same @key

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `Form` | ✓ |  |
| `value` | `Int` | ✓ |  |

### `setObj(object, key, container)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `Form` | ✓ |  |
| `container` | `Int` | ✓ |  |

### `setStr(object, key, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `Form` | ✓ |  |
| `value` | `String` | ✓ |  |

### `valueType(object, key) → Int`

**Flags:** Native Global

Returns type of the value associated with the @key.
  0 - no value, 1 - none, 2 - int, 3 - float, 4 - form, 5 - object, 6 - string

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `Form` | ✓ |  |


---

## `JIntMap`

**Source:** `jcontainers` (JContainers)

Associative key-value container.
  Inherits JValue functionality

---

## Global Functions

### `addPairs(object, source, overrideDuplicates)`

**Flags:** Native Global

Inserts key-value pairs from the source container

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `source` | `Int` | ✓ |  |
| `overrideDuplicates` | `Bool` | ✓ |  |

### `allKeys(object) → Int`

**Flags:** Native Global

Returns a new array containing all keys

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |

### `allKeysPArray(object) → Int[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |

### `allValues(object) → Int`

**Flags:** Native Global

Returns a new array containing all values

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |

### `clear(object)`

**Flags:** Native Global

Removes all pairs from the container

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |

### `count(object) → Int`

**Flags:** Native Global

Returns count of pairs in the conainer

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |

### `getFlt(object, key, default) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `Int` | ✓ |  |
| `default` | `Float` |  | `0` |

### `getForm(object, key, default) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `Int` | ✓ |  |
| `default` | `Form` |  |  |

### `getInt(object, key, default) → Int`

**Flags:** Native Global

Returns the value associated with the @key. If not, returns @default value

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `Int` | ✓ |  |
| `default` | `Int` |  | `0` |

### `getNthKey(object, keyIndex) → Int`

**Flags:** Native Global

Retrieves N-th key. negative index accesses items from the end of container counting backwards.
  Worst complexity is O(n/2)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `keyIndex` | `Int` | ✓ |  |

### `getObj(object, key, default) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `Int` | ✓ |  |
| `default` | `Int` |  | `0` |

### `getStr(object, key, default) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `Int` | ✓ |  |
| `default` | `String` |  | `""` |

### `hasKey(object, key) → Bool`

**Flags:** Native Global

Returns true, if the container has @key: value pair

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `Int` | ✓ |  |

### `nextKey(object, previousKey, endKey) → Int`

**Flags:** Native Global

Simplifies iteration over container's contents.
  Accepts the @previousKey, returns the next key.
  If @previousKey == @endKey the function returns the first key.
  The function always returns so-called 'valid' keys (the ones != @endKey).
  The function returns @endKey ('invalid' key) only once to signal that iteration has reached its end.
  In most cases, if the map doesn't contain an invalid key ("" for JMap, None form-key for JFormMap)
  it's ok to omit the @endKey.

  Usage:

      string key = JMap.nextKey(map, previousKey="", endKey="")
      while key != ""
        <retrieve values here>
        key = JMap.nextKey(map, key, endKey="")
      endwhile

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `previousKey` | `Int` |  | `0` |
| `endKey` | `Int` |  | `0` |

### `object() → Int`

**Flags:** Native Global

creates new container object. returns container's identifier (unique integer number).

### `removeKey(object, key) → Bool`

**Flags:** Native Global

Removes the pair from the container where the key equals to the @key

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `Int` | ✓ |  |

### `setFlt(object, key, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `Int` | ✓ |  |
| `value` | `Float` | ✓ |  |

### `setForm(object, key, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `Int` | ✓ |  |
| `value` | `Form` | ✓ |  |

### `setInt(object, key, value)`

**Flags:** Native Global

Inserts @key: @value pair. Replaces existing pair with the same @key

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `Int` | ✓ |  |
| `value` | `Int` | ✓ |  |

### `setObj(object, key, container)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `Int` | ✓ |  |
| `container` | `Int` | ✓ |  |

### `setStr(object, key, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `Int` | ✓ |  |
| `value` | `String` | ✓ |  |

### `valueType(object, key) → Int`

**Flags:** Native Global

Returns type of the value associated with the @key.
  0 - no value, 1 - none, 2 - int, 3 - float, 4 - form, 5 - object, 6 - string

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `Int` | ✓ |  |


---

## `JLua`

**Source:** `jcontainers` (JContainers)

Evaluates Lua code. Unstable API - I'm free to change or remove it anytime

---

## Global Functions

### `evalLuaFlt(luaCode, transport, default, minimizeLifetime) → Float`

**Flags:** Native Global

Evaluates piece of Lua code. The arguments are carried by @transport object.
  The @transport is any kind of object, not just JMap.
  If @minimizeLifetime is True the function will invoke JValue.zeroLifetime on the @transport object.
  It is more than wise to re-use @transport when evaluating lot of lua code at once.
  Returns @default value if evaluation fails.

  WARNING: You can transfer in/out from Lua only 24-bit integers with exact precision (+/- 16 777 216)
  Anything bigger or smaller than that will have "holes" due to how the floating point rounding works.

  Usage example:

      ; 7 from the end until 9 from the end. Returns "Lua" string
      string input = "Hello Lua user"
      string s = JLua.evaLuaStr("return string.sub(args.string, args.low, args.high)",\
          JLua.setStr("string",input, JLua.setInt("low",7, JLua.setInt("high",9 )))\
      )

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `luaCode` | `String` | ✓ |  |
| `transport` | `Int` | ✓ |  |
| `default` | `Float` |  | `0` |
| `minimizeLifetime` | `Bool` |  | `true` |

### `evalLuaForm(luaCode, transport, default, minimizeLifetime) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `luaCode` | `String` | ✓ |  |
| `transport` | `Int` | ✓ |  |
| `default` | `Form` |  |  |
| `minimizeLifetime` | `Bool` |  | `true` |

### `evalLuaInt(luaCode, transport, default, minimizeLifetime) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `luaCode` | `String` | ✓ |  |
| `transport` | `Int` | ✓ |  |
| `default` | `Int` |  | `0` |
| `minimizeLifetime` | `Bool` |  | `true` |

### `evalLuaObj(luaCode, transport, default, minimizeLifetime) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `luaCode` | `String` | ✓ |  |
| `transport` | `Int` | ✓ |  |
| `default` | `Int` |  | `0` |
| `minimizeLifetime` | `Bool` |  | `true` |

### `evalLuaStr(luaCode, transport, default, minimizeLifetime) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `luaCode` | `String` | ✓ |  |
| `transport` | `Int` | ✓ |  |
| `default` | `String` |  | `""` |
| `minimizeLifetime` | `Bool` |  | `true` |

### `setFlt(key, value, transport) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `key` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |
| `transport` | `Int` |  | `0` |

### `setForm(key, value, transport) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `key` | `String` | ✓ |  |
| `value` | `Form` | ✓ |  |
| `transport` | `Int` |  | `0` |

### `setInt(key, value, transport) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `key` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |
| `transport` | `Int` |  | `0` |

### `setObj(key, value, transport) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `key` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |
| `transport` | `Int` |  | `0` |

### `setStr(key, value, transport) → Int`

**Flags:** Native Global

Inserts new (or replaces existing) {key -> value} pair. Expects that @transport is JMap object, if @transport is 0 it creates new JMap object.
  Returns @transport

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `key` | `String` | ✓ |  |
| `value` | `String` | ✓ |  |
| `transport` | `Int` |  | `0` |


---

## `JMap`

**Source:** `jcontainers` (JContainers)

Associative key-value container.
  Inherits JValue functionality

---

## Global Functions

### `addPairs(object, source, overrideDuplicates)`

**Flags:** Native Global

Inserts key-value pairs from the source container

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `source` | `Int` | ✓ |  |
| `overrideDuplicates` | `Bool` | ✓ |  |

### `allKeys(object) → Int`

**Flags:** Native Global

Returns a new array containing all keys

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |

### `allKeysPArray(object) → String[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |

### `allValues(object) → Int`

**Flags:** Native Global

Returns a new array containing all values

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |

### `clear(object)`

**Flags:** Native Global

Removes all pairs from the container

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |

### `count(object) → Int`

**Flags:** Native Global

Returns count of pairs in the conainer

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |

### `getFlt(object, key, default) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `String` | ✓ |  |
| `default` | `Float` |  | `0` |

### `getForm(object, key, default) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `String` | ✓ |  |
| `default` | `Form` |  |  |

### `getInt(object, key, default) → Int`

**Flags:** Native Global

Returns the value associated with the @key. If not, returns @default value

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `String` | ✓ |  |
| `default` | `Int` |  | `0` |

### `getNthKey(object, keyIndex) → String`

**Flags:** Native Global

Retrieves N-th key. negative index accesses items from the end of container counting backwards.
  Worst complexity is O(n/2)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `keyIndex` | `Int` | ✓ |  |

### `getObj(object, key, default) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `String` | ✓ |  |
| `default` | `Int` |  | `0` |

### `getStr(object, key, default) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `String` | ✓ |  |
| `default` | `String` |  | `""` |

### `hasKey(object, key) → Bool`

**Flags:** Native Global

Returns true, if the container has @key: value pair

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `String` | ✓ |  |

### `nextKey(object, previousKey, endKey) → String`

**Flags:** Native Global

Simplifies iteration over container's contents.
  Accepts the @previousKey, returns the next key.
  If @previousKey == @endKey the function returns the first key.
  The function always returns so-called 'valid' keys (the ones != @endKey).
  The function returns @endKey ('invalid' key) only once to signal that iteration has reached its end.
  In most cases, if the map doesn't contain an invalid key ("" for JMap, None form-key for JFormMap)
  it's ok to omit the @endKey.

  Usage:

      string key = JMap.nextKey(map, previousKey="", endKey="")
      while key != ""
        <retrieve values here>
        key = JMap.nextKey(map, key, endKey="")
      endwhile

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `previousKey` | `String` |  | `""` |
| `endKey` | `String` |  | `""` |

### `object() → Int`

**Flags:** Native Global

creates new container object. returns container's identifier (unique integer number).

### `removeKey(object, key) → Bool`

**Flags:** Native Global

Removes the pair from the container where the key equals to the @key

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `String` | ✓ |  |

### `setFlt(object, key, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |

### `setForm(object, key, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `String` | ✓ |  |
| `value` | `Form` | ✓ |  |

### `setInt(object, key, value)`

**Flags:** Native Global

Inserts @key: @value pair. Replaces existing pair with the same @key

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |

### `setObj(object, key, container)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `String` | ✓ |  |
| `container` | `Int` | ✓ |  |

### `setStr(object, key, value)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `String` | ✓ |  |
| `value` | `String` | ✓ |  |

### `valueType(object, key) → Int`

**Flags:** Native Global

Returns type of the value associated with the @key.
  0 - no value, 1 - none, 2 - int, 3 - float, 4 - form, 5 - object, 6 - string

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `key` | `String` | ✓ |  |


---

## `JString`

**Source:** `jcontainers` (JContainers)

various string utility methods

---

## Global Functions

### `decodeFormStringToForm(formString) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `formString` | `String` | ✓ |  |

### `decodeFormStringToFormId(formString) → Int`

**Flags:** Native Global

FormId|Form <-> "__formData|<pluginName>|<lowFormId>"-string converisons

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `formString` | `String` | ✓ |  |

### `encodeFormIdToString(formId) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `formId` | `Int` | ✓ |  |

### `encodeFormToString(value) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `value` | `Form` | ✓ |  |

### `generateUUID() → String`

**Flags:** Native Global

Generates random uuid-string like 2e80251a-ab22-4ad8-928c-2d1c9561270e

### `wrap(sourceText, charactersPerLine) → Int`

**Flags:** Native Global

Breaks source text onto set of lines of almost equal size.
  Returns JArray object containing lines.
  Accepts ASCII and UTF-8 encoded strings only

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `sourceText` | `String` | ✓ |  |
| `charactersPerLine` | `Int` |  | `60` |


---

## `JValue`

**Source:** `jcontainers` (JContainers)

Common functionality, shared by JArray, JMap, JFormMap, JIntMap

---

## Global Functions

### `addToPool(object, poolName) → Int`

**Flags:** Native Global

Handly for temporary objects (objects with no owners) - the pool 'locationName' owns any amount of objects, preventing their destuction, extends lifetime.
  Do not forget to clean the pool later! Typical use:
  int jTempMap = JValue.addToPool(JMap.object(), "uniquePoolName")
  int jKeys = JValue.addToPool(JMap.allKeys(someJMap), "uniquePoolName")
  and anywhere later:
  JValue.cleanPool("uniquePoolName")

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `poolName` | `String` | ✓ |  |

### `cleanPool(poolName)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `poolName` | `String` | ✓ |  |

### `clear(object)`

**Flags:** Native Global

Removes all items from the container

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |

### `count(object) → Int`

**Flags:** Native Global

Returns amount of items in the container

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |

### `deepCopy(object) → Int`

**Flags:** Native Global

Returns deep copy

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |

### `empty(object) → Bool`

**Flags:** Native Global

Returns true, if the container is empty

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |

### `enableAPILog(arg0)`

**Flags:** Native Global

Most call entries made to JC will be logged. Heavy traffic, by default is disabled.
  Not thread safe for multiple users (though harmless).

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `arg0` | `Bool` | ✓ |  |

### `evalLuaFlt(object, luaCode, default) → Float`

**Flags:** Native Global

Evaluates piece of lua code. Lua support is experimental

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `luaCode` | `String` | ✓ |  |
| `default` | `Float` |  | `0` |

### `evalLuaForm(object, luaCode, default) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `luaCode` | `String` | ✓ |  |
| `default` | `Form` |  |  |

### `evalLuaInt(object, luaCode, default) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `luaCode` | `String` | ✓ |  |
| `default` | `Int` |  | `0` |

### `evalLuaObj(object, luaCode, default) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `luaCode` | `String` | ✓ |  |
| `default` | `Int` |  | `0` |

### `evalLuaStr(object, luaCode, default) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `luaCode` | `String` | ✓ |  |
| `default` | `String` |  | `""` |

### `hasPath(object, path) → Bool`

**Flags:** Native Global

Path resolving:

  Returns true, if it's possible to resolve given path, i.e. if it's possible to retrieve the value at the path.
  For ex. JValue.hasPath(container, ".player.health") will test whether @container structure close to this one - {'player': {'health': health_value}}

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `path` | `String` | ✓ |  |

### `isArray(object) → Bool`

**Flags:** Native Global

Returns true if the object is map, array or formmap container

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |

### `isExists(object) → Bool`

**Flags:** Native Global

Tests whether given object identifier is not the null object.
  Note that many other API functions already check that too.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |

### `isFormMap(object) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |

### `isIntegerMap(object) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |

### `isMap(object) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |

### `objectFromPrototype(prototype) → Int`

**Flags:** Native Global

Creates a new container object using given JSON string-prototype

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `prototype` | `String` | ✓ |  |

### `readFromDirectory(directoryPath, extension) → Int`

**Flags:** Native Global

Parses JSON files in a directory (non recursive) and returns JMap containing {filename, container-object} pairs.
  Note: by default it does not filter files by extension and will try to parse everything

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `directoryPath` | `String` | ✓ |  |
| `extension` | `String` |  | `""` |

### `readFromFile(filePath) → Int`

**Flags:** Native Global

JSON serialization/deserialization:

  Creates and returns a new container object containing contents of JSON file

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `filePath` | `String` | ✓ |  |

### `release(object) → Int`

**Flags:** Native Global

Releases the object and returns zero, so you can release and nullify with one line of code: object = JValue.release(object)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |

### `releaseAndRetain(previousObject, newObject, tag) → Int`

**Flags:** Native Global

Just a union of retain-release calls. Releases @previousObject, retains and returns @newObject.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `previousObject` | `Int` | ✓ |  |
| `newObject` | `Int` | ✓ |  |
| `tag` | `String` |  | `""` |

### `releaseObjectsWithTag(tag)`

**Flags:** Native Global

Releases all objects tagged with @tag.
  Internally invokes JValue.release on each object same amount of times it has been retained.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `tag` | `String` | ✓ |  |

### `retain(object, tag) → Int`

**Flags:** Native Global

--- Lifetime management functionality.
  Read this https://github.com/ryobg/JContainers/wiki/Lifetime-Management before using any of lifetime management functions

  Retains and returns the object.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `tag` | `String` |  | `""` |

### `shallowCopy(object) → Int`

**Flags:** Native Global

--- Mics. functionality

  Returns shallow copy (won't copy child objects)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |

### `solvedValueType(object, path) → Int`

**Flags:** Native Global

Returns type of resolved value. 0 - no value, 1 - none, 2 - int, 3 - float, 4 - form, 5 - object, 6 - string

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `path` | `String` | ✓ |  |

### `solveFlt(object, path, default) → Float`

**Flags:** Native Global

Attempts to retrieve value at given path. If fails, returns @default value

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `path` | `String` | ✓ |  |
| `default` | `Float` |  | `0` |

### `solveFltSetter(object, path, value, createMissingKeys) → Bool`

**Flags:** Native Global

Attempts to assign the value. If @createMissingKeys is False it may fail to assign - if no such path exist.
  With 'createMissingKeys=true' it creates any missing path element: solveIntSetter(map, ".keyA.keyB", 10, true) on empty JMap creates {keyA: {keyB: 10}} structure

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `path` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |
| `createMissingKeys` | `Bool` |  | `false` |

### `solveForm(object, path, default) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `path` | `String` | ✓ |  |
| `default` | `Form` |  |  |

### `solveFormSetter(object, path, value, createMissingKeys) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `path` | `String` | ✓ |  |
| `value` | `Form` | ✓ |  |
| `createMissingKeys` | `Bool` |  | `false` |

### `solveInt(object, path, default) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `path` | `String` | ✓ |  |
| `default` | `Int` |  | `0` |

### `solveIntSetter(object, path, value, createMissingKeys) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `path` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |
| `createMissingKeys` | `Bool` |  | `false` |

### `solveObj(object, path, default) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `path` | `String` | ✓ |  |
| `default` | `Int` |  | `0` |

### `solveObjSetter(object, path, value, createMissingKeys) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `path` | `String` | ✓ |  |
| `value` | `Int` | ✓ |  |
| `createMissingKeys` | `Bool` |  | `false` |

### `solveStr(object, path, default) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `path` | `String` | ✓ |  |
| `default` | `String` |  | `""` |

### `solveStrSetter(object, path, value, createMissingKeys) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `path` | `String` | ✓ |  |
| `value` | `String` | ✓ |  |
| `createMissingKeys` | `Bool` |  | `false` |

### `writeToFile(object, filePath)`

**Flags:** Native Global

Writes the object into JSON file

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
| `filePath` | `String` | ✓ |  |

### `zeroLifetime(object) → Int`

**Flags:** Native Global

Minimizes the time JC temporarily owns the object, returns the object.
  By using this function you help JC to delete unused objects as soon as possible.
  Has zero effect if the object is being retained or if another object contains/references it.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `object` | `Int` | ✓ |  |
