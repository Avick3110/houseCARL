# `Trash_Collection`

**Source:** `trashutil` (TrashUtility) • **Flags:** Hidden

---

## Global Functions

### `AuxArrayEmpty(Holder, CollectionName) → Bool`

**Flags:** Native Global

======================= Aux Array =======================

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Holder` | `Form` | ✓ |  |
| `CollectionName` | `String` | ✓ |  |

### `AuxArrayErase(Holder, CollectionName, _index) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Holder` | `Form` | ✓ |  |
| `CollectionName` | `String` | ✓ |  |
| `_index` | `Int` |  | `-1` |

### `AuxArrayGetFloat(Holder, CollectionName, _index) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Holder` | `Form` | ✓ |  |
| `CollectionName` | `String` | ✓ |  |
| `_index` | `Int` |  | `0` |

### `AuxArrayGetFloatArray(Holder, CollectionName) → Float[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Holder` | `Form` | ✓ |  |
| `CollectionName` | `String` | ✓ |  |

### `AuxArrayGetRef(Holder, CollectionName, _index) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Holder` | `Form` | ✓ |  |
| `CollectionName` | `String` | ✓ |  |
| `_index` | `Int` |  | `0` |

### `AuxArrayGetRefArray(Holder, CollectionName) → form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Holder` | `Form` | ✓ |  |
| `CollectionName` | `String` | ✓ |  |

### `AuxArrayGetSize(Holder, CollectionName) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Holder` | `Form` | ✓ |  |
| `CollectionName` | `String` | ✓ |  |

### `AuxArrayGetType(Holder, CollectionName, _index) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Holder` | `Form` | ✓ |  |
| `CollectionName` | `String` | ✓ |  |
| `_index` | `Int` |  | `0` |

### `AuxArraySetFloat(Holder, CollectionName, _flt, _index) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Holder` | `Form` | ✓ |  |
| `CollectionName` | `String` | ✓ |  |
| `_flt` | `Float` | ✓ |  |
| `_index` | `Int` |  | `0` |

### `AuxArraySetFromFloatArray(Holder, CollectionName, floatArr) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Holder` | `Form` | ✓ |  |
| `CollectionName` | `String` | ✓ |  |
| `floatArr` | `Float[]` | ✓ |  |

### `AuxArraySetFromFormList(Holder, CollectionName, _formlist) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Holder` | `Form` | ✓ |  |
| `CollectionName` | `String` | ✓ |  |
| `_formlist` | `FormList` | ✓ |  |

### `AuxArraySetFromRefArray(Holder, CollectionName, FormArr) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Holder` | `Form` | ✓ |  |
| `CollectionName` | `String` | ✓ |  |
| `FormArr` | `form[]` | ✓ |  |

### `AuxArraySetRef(Holder, CollectionName, _form, _index) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Holder` | `Form` | ✓ |  |
| `CollectionName` | `String` | ✓ |  |
| `_form` | `Form` | ✓ |  |
| `_index` | `Int` |  | `0` |

### `DestroyAuxArr(Holder, CollectionName) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Holder` | `Form` | ✓ |  |
| `CollectionName` | `String` | ✓ |  |

### `DestroyRefMap(Holder, CollectionName) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Holder` | `Form` | ✓ |  |
| `CollectionName` | `String` | ✓ |  |

### `DumpAllAuxArr()`

**Flags:** Native Global

### `DumpAllCollection()`

**Flags:** Native Global

### `DumpAllRefMap()`

**Flags:** Native Global

### `DumpCollection(Holder, CollectionName, CollectionType)`

**Flags:** Native Global

======================= Misc =======================
CollectionType, 0 = AuxArr, 1 = RefMap

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Holder` | `Form` | ✓ |  |
| `CollectionName` | `String` | ✓ |  |
| `CollectionType` | `Int` | ✓ |  |

### `MapGetValueType(Holder, CollectionName, _key) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Holder` | `Form` | ✓ |  |
| `CollectionName` | `String` | ✓ |  |
| `_key` | `Form` | ✓ |  |

### `RefMapClear(Holder, CollectionName) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Holder` | `Form` | ✓ |  |
| `CollectionName` | `String` | ✓ |  |

### `RefMapErase(Holder, CollectionName, _key) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Holder` | `Form` | ✓ |  |
| `CollectionName` | `String` | ✓ |  |
| `_key` | `Form` | ✓ |  |

### `RefMapEraseAll(Holder, CollectionName) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Holder` | `Form` | ✓ |  |
| `CollectionName` | `String` | ✓ |  |

### `RefMapEraseInvalidKey(Holder, CollectionName) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Holder` | `Form` | ✓ |  |
| `CollectionName` | `String` | ✓ |  |

### `RefMapGetFloat(Holder, CollectionName, _key) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Holder` | `Form` | ✓ |  |
| `CollectionName` | `String` | ✓ |  |
| `_key` | `Form` | ✓ |  |

### `RefMapGetFloatValues(Holder, CollectionName) → Float[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Holder` | `Form` | ✓ |  |
| `CollectionName` | `String` | ✓ |  |

### `RefMapGetKeys(Holder, CollectionName) → form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Holder` | `Form` | ✓ |  |
| `CollectionName` | `String` | ✓ |  |

### `RefMapGetRef(Holder, CollectionName, _key) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Holder` | `Form` | ✓ |  |
| `CollectionName` | `String` | ✓ |  |
| `_key` | `Form` | ✓ |  |

### `RefMapGetRefValues(Holder, CollectionName) → form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Holder` | `Form` | ✓ |  |
| `CollectionName` | `String` | ✓ |  |

### `RefMapGetSize(Holder, CollectionName) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Holder` | `Form` | ✓ |  |
| `CollectionName` | `String` | ✓ |  |

### `RefMapSetFloat(Holder, CollectionName, _key, _elem) → Bool`

**Flags:** Native Global

======================= Ref Map =======================

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Holder` | `Form` | ✓ |  |
| `CollectionName` | `String` | ✓ |  |
| `_key` | `Form` | ✓ |  |
| `_elem` | `Float` | ✓ |  |

### `RefMapSetRef(Holder, CollectionName, _key, _elem) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `Holder` | `Form` | ✓ |  |
| `CollectionName` | `String` | ✓ |  |
| `_key` | `Form` | ✓ |  |
| `_elem` | `Form` | ✓ |  |


---

## `Trash_Function`

**Source:** `trashutil` (TrashUtility) • **Flags:** Hidden

---

## Global Functions

### `ApplyHit(_attacker, _victim, _weapon, _applyench)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `_attacker` | `Actor` | ✓ |  |
| `_victim` | `Actor` | ✓ |  |
| `_weapon` | `Weapon` | ✓ |  |
| `_applyench` | `Bool` |  | `true` |

### `ApplyMeleeHit(_attacker, _victim, lefthand)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `_attacker` | `Actor` | ✓ |  |
| `_victim` | `Actor` | ✓ |  |
| `lefthand` | `Bool` |  | `false` |

### `ConsoleInfo(_str)`

**Flags:** Native Global

=================== Debug ===================

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `_str` | `String` | ✓ |  |

### `DumpFloatArray(_form)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `_form` | `Float[]` | ✓ |  |

### `DumpFormArray(_FormArr)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `_FormArr` | `Form[]` | ✓ |  |

### `DumpIntArray(_IntArr)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `_IntArr` | `Int[]` | ✓ |  |

### `DumpRefArray(_RefArr)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `_RefArr` | `ObjectReference[]` | ✓ |  |

### `GetDistance2D(A_ref, B_ref) → Float`

**Flags:** Native Global

=================== Distance ===================

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `A_ref` | `ObjectReference` | ✓ |  |
| `B_ref` | `ObjectReference` | ✓ |  |

### `GetDistance3D(A_ref, B_ref) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `A_ref` | `ObjectReference` | ✓ |  |
| `B_ref` | `ObjectReference` | ✓ |  |

### `GetDistanceBetweenPoints2D(A_x, A_y, B_x, B_y) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `A_x` | `Float` | ✓ |  |
| `A_y` | `Float` | ✓ |  |
| `B_x` | `Float` | ✓ |  |
| `B_y` | `Float` | ✓ |  |

### `GetDistanceBetweenPoints3D(A_x, A_y, A_z, B_x, B_y, B_z) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `A_x` | `Float` | ✓ |  |
| `A_y` | `Float` | ✓ |  |
| `A_z` | `Float` | ✓ |  |
| `B_x` | `Float` | ✓ |  |
| `B_y` | `Float` | ✓ |  |
| `B_z` | `Float` | ✓ |  |

### `GetDistanceFromPoint2D(_ref, x, y) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `_ref` | `ObjectReference` | ✓ |  |
| `x` | `Float` | ✓ |  |
| `y` | `Float` | ✓ |  |

### `GetDistanceFromPoint3D(_ref, x, y, z) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `_ref` | `ObjectReference` | ✓ |  |
| `x` | `Float` | ✓ |  |
| `y` | `Float` | ✓ |  |
| `z` | `Float` | ✓ |  |

### `GetFloatMax() → Float`

**Flags:** Native Global

### `GetFloatMin() → Float`

**Flags:** Native Global

### `GetFormIDString(_form) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `_form` | `form` | ✓ |  |

### `GetHeadingAngleBetweenPoints(A_x, A_y, A_z, ang_z, B_x, B_y, B_z, a_abs) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `A_x` | `Float` | ✓ |  |
| `A_y` | `Float` | ✓ |  |
| `A_z` | `Float` | ✓ |  |
| `ang_z` | `Float` | ✓ |  |
| `B_x` | `Float` | ✓ |  |
| `B_y` | `Float` | ✓ |  |
| `B_z` | `Float` | ✓ |  |
| `a_abs` | `Bool` |  | `false` |

### `GetHeadingAngleBetweenPointsX(A_x, A_y, A_z, ang_x, B_x, B_y, B_z, a_abs) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `A_x` | `Float` | ✓ |  |
| `A_y` | `Float` | ✓ |  |
| `A_z` | `Float` | ✓ |  |
| `ang_x` | `Float` | ✓ |  |
| `B_x` | `Float` | ✓ |  |
| `B_y` | `Float` | ✓ |  |
| `B_z` | `Float` | ✓ |  |
| `a_abs` | `Bool` |  | `false` |

### `GetHeadingAngleX(fst_ref, sec_ref, a_abs) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `fst_ref` | `ObjectReference` | ✓ |  |
| `sec_ref` | `ObjectReference` | ✓ |  |
| `a_abs` | `Bool` |  | `false` |

### `GetHeadingPointAngle(_obj, x, y, z, a_abs) → Float`

**Flags:** Native Global

=================== HeadingAngle ===================

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `_obj` | `ObjectReference` | ✓ |  |
| `x` | `Float` | ✓ |  |
| `y` | `Float` | ✓ |  |
| `z` | `Float` | ✓ |  |
| `a_abs` | `Bool` |  | `false` |

### `GetHeadingPointAngleX(_ref, _x, _y, _z, a_abs) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `_ref` | `ObjectReference` | ✓ |  |
| `_x` | `Float` | ✓ |  |
| `_y` | `Float` | ✓ |  |
| `_z` | `Float` | ✓ |  |
| `a_abs` | `Bool` |  | `false` |

### `GetInSameCellOrWorldSpace(A_ref, B_ref) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `A_ref` | `ObjectReference` | ✓ |  |
| `B_ref` | `ObjectReference` | ✓ |  |

### `GetIntMax() → Int`

**Flags:** Native Global

### `GetIntMin() → Int`

**Flags:** Native Global

### `GetPosAsArray(_obj) → Float[]`

**Flags:** Native Global

=================== Pos ===================

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `_obj` | `ObjectReference` | ✓ |  |

### `GetSlowTimeMult(_GetWorldTimeMult) → Float`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `_GetWorldTimeMult` | `Bool` |  | `true` |

### `InstantCastToActors(_Caster, _Spell, _Radius, _BlameCaster, _CastToCaster, _Center) → Form[]`

**Flags:** Native Global

=================== SpellMatter ===================

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `_Caster` | `ObjectReference` | ✓ |  |
| `_Spell` | `Form` | ✓ |  |
| `_Radius` | `Float` |  | `0` |
| `_BlameCaster` | `Bool` |  | `false` |
| `_CastToCaster` | `Bool` |  | `false` |
| `_Center` | `ObjectReference` |  |  |

### `InstantCastToActorsWithFilter(_Caster, _Spell, _Radius, _BlameCaster, _CastToCaster, _Center, Keyword_or_FormList, _MatchAllKeywords) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `_Caster` | `ObjectReference` | ✓ |  |
| `_Spell` | `Form` | ✓ |  |
| `_Radius` | `Float` |  | `0` |
| `_BlameCaster` | `Bool` |  | `false` |
| `_CastToCaster` | `Bool` |  | `false` |
| `_Center` | `ObjectReference` |  |  |
| `Keyword_or_FormList` | `Form` |  |  |
| `_MatchAllKeywords` | `Bool` |  | `false` |

### `InstantCastToActorsWithFilterAlt(_Caster, _Spell, _Radius, _BlameCaster, _CastToCaster, _Center, Keyword_Arr, _MatchAllKeywords) → Form[]`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `_Caster` | `ObjectReference` | ✓ |  |
| `_Spell` | `Form` | ✓ |  |
| `_Radius` | `Float` |  | `0` |
| `_BlameCaster` | `Bool` |  | `false` |
| `_CastToCaster` | `Bool` |  | `false` |
| `_Center` | `ObjectReference` |  |  |
| `Keyword_Arr` | `Keyword[]` |  |  |
| `_MatchAllKeywords` | `Bool` |  | `false` |

### `PlaySoundAtPoint(_Sound, x, y, z) → Bool`

**Flags:** Native Global

=================== Sound ===================
_Sound must be a Sound Descriptor(SNDR)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `_Sound` | `Form` | ✓ |  |
| `x` | `Float` | ✓ |  |
| `y` | `Float` | ✓ |  |
| `z` | `Float` | ✓ |  |

### `PrintFlt(_flt)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `_flt` | `Float` | ✓ |  |

### `PrintForm(_form)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `_form` | `Form` | ✓ |  |

### `SetPosAlt(_obj, x, y, z, transform)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `_obj` | `ObjectReference` | ✓ |  |
| `x` | `Float` | ✓ |  |
| `y` | `Float` | ✓ |  |
| `z` | `Float` | ✓ |  |
| `transform` | `Bool` |  | `false` |

### `SetPosFromArray(_obj, PosArr, transform)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `_obj` | `ObjectReference` | ✓ |  |
| `PosArr` | `Float[]` | ✓ |  |
| `transform` | `Bool` |  | `false` |

### `SetSlowTimeMult(_WorldTimeMult, _PlayerOnlyTimeMult, _Setter)`

**Flags:** Native Global

=================== TimeMultSetter ===================

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `_WorldTimeMult` | `Float` |  | `0` |
| `_PlayerOnlyTimeMult` | `Float` |  | `1` |
| `_Setter` | `Bool` |  | `true` |

### `ToHexString(_num) → String`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `_num` | `Int` | ✓ |  |


---

## `Trash_PlayerControl`

**Source:** `trashutil` (TrashUtility) • **Flags:** Hidden

---

## Global Functions

### `GetLookMoveDirection(flag) → Int`

**Flags:** Native Global

|flag = 0 -X | flag =1 -Y | flag =2 -X&Y | other -X&Y

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `flag` | `Int` |  | `-1` |

### `GetLookMoveOverwrite(flag) → Float`

**Flags:** Native Global

|flag = 0 -X | flag =1 -Y | other -X||Y

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `flag` | `Int` |  | `-1` |

### `GetLookMoveScale(flag) → Float`

**Flags:** Native Global

|flag = 0 -X | flag =1 -Y | other =2 -X&Y

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `flag` | `Int` |  | `-1` |

### `GetMoveDirection(flag) → Int`

**Flags:** Native Global

|flag = 0 -X | flag =1 -Y | other -X&Y

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `flag` | `Int` |  | `-1` |

### `GetMoveOverwrite(flag) → Int`

**Flags:** Native Global

|flag = 0 -X | flag =1 -Y | other -X||Y

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `flag` | `Int` |  | `-1` |

### `IsLookMoveScale(flag) → Bool`

**Flags:** Native Global

|flag = 0 -X | flag =1 -Y | flag =2 -X&&Y | flag =3 -X||Y | other -X||Y

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `flag` | `Int` |  | `-1` |

### `IsMoveDisabled(flag) → Bool`

**Flags:** Native Global

|flag = 0 -X | flag =1 -Y | other = -X||Y

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `flag` | `Int` |  | `-1` |

### `IsReverseLook(flag) → Bool`

**Flags:** Native Global

|flag = 0 -X | flag =1 -Y | flag =2 -X&&Y | flag =3 -X||Y | other -X||Y

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `flag` | `Int` |  | `-1` |

### `IsReverseMove(flag) → Bool`

**Flags:** Native Global

|flag = 0 -X | flag =1 -Y | flag =2 -X&&Y | flag =3 -X||Y | other -X||Y

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `flag` | `Int` |  | `-1` |

### `LookMoveScale(a_scale, flag)`

**Flags:** Native Global

|flag = 0 -X | flag =1 -Y | flag =2 -X&Y | other -X&Y

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_scale` | `Float` |  | `1` |
| `flag` | `Int` |  | `-1` |

### `ReverseLook(_Reverse, flag)`

**Flags:** Native Global

|flag = 0 -X | flag =1 -Y | flag =2 -X&Y | other -X&Y

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `_Reverse` | `Bool` |  | `false` |
| `flag` | `Int` |  | `-1` |

### `ReverseMove(_Reverse, flag)`

**Flags:** Native Global

|flag = 0 -X | flag =1 -Y | flag =2 -X&Y | other -X&Y

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `_Reverse` | `Bool` |  | `false` |
| `flag` | `Int` |  | `-1` |

### `SetAllReverse(_ReverseAll)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `_ReverseAll` | `Bool` |  | `false` |

### `SetLookMoveOverwrite(_movement, flag)`

**Flags:** Native Global

|flag = 0 -X | flag =1 -Y | flag =2 -X&Y | other -X||Y
movement == 0 it will not apply any overwrite

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `_movement` | `Float` |  | `0` |
| `flag` | `Int` |  | `-1` |

### `SetMoveDisabled(_Disabled, flag)`

**Flags:** Native Global

|flag = 0 -X | flag =1 -Y | other = -X&&Y

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `_Disabled` | `Bool` |  | `false` |
| `flag` | `Int` |  | `-1` |

### `SetMoveOverwrite(_direction, flag)`

**Flags:** Native Global

|flag = 0 -X | flag =1 -Y | flag =2 -X&Y | other -X&Y
_direction == 0 it will not apply any overwrite

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `_direction` | `Int` |  | `0` |
| `flag` | `Int` |  | `-1` |
