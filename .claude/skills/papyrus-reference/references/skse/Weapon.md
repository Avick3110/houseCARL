# `Weapon`

**Source:** `skse` (Skyrim Script Extender x64) • **Extends:** `Form` • **Flags:** Hidden

---

## Functions

### `Fire(akSource, akAmmo)`

**Flags:** Native

Fire this weapon base object from the specified source

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSource` | `ObjectReference` | ✓ |  |
| `akAmmo` | `Ammo` |  |  |

### `GetBaseDamage() → Int`

**Flags:** Native

### `GetCritDamage() → Int`

**Flags:** Native

### `GetCritEffect() → Spell`

**Flags:** Native

works on the spell that applies when critting

### `GetCritEffectOnDeath() → Bool`

**Flags:** Native

Gets, sets or unsets whether the the crit effect should only occur on death

### `GetCritMultiplier() → Float`

**Flags:** Native

Gets/sets the weapons crit multiplier

### `GetEnchantment() → Enchantment`

**Flags:** Native

works on the enchantment associated with the weapon

### `GetEnchantmentValue() → Int`

**Flags:** Native

works on the enchantment value of the associated weapon

### `GetEquippedModel() → Static`

**Flags:** Native

works on the weapon model when equipped of the associated weapon

### `GetEquipType() → EquipSlot`

**Flags:** Native

Returns the particular equipslot type

### `GetIconPath() → String`

**Flags:** Native

works on the path to the nif file representing the icon for the weapon in the inventory

### `GetMaxRange() → Float`

**Flags:** Native

### `GetMessageIconPath() → String`

**Flags:** Native

works on the path to the file representing the message icon for the weapon

### `GetMinRange() → Float`

**Flags:** Native

### `GetModelPath() → String`

**Flags:** Native

works on the path to the nif file representing the in-game model of the weapon

### `GetReach() → Float`

**Flags:** Native

### `GetResist() → String`

**Flags:** Native

DamageResist
ElectricResist
FireResist
FrostResist
MagicResist
PoisonResist

### `GetSkill() → String`

**Flags:** Native

### `GetSpeed() → Float`

**Flags:** Native

### `GetStagger() → Float`

**Flags:** Native

### `GetTemplate() → Weapon`

**Flags:** Native

returns the weapon template of this weapon

### `GetWeaponType() → Int`

**Flags:** Native

### `IsBattleaxe() → Bool`

### `IsBow() → Bool`

### `IsDagger() → Bool`

### `IsGreatsword() → Bool`

### `IsMace() → Bool`

### `IsStaff() → Bool`

### `IsSword() → Bool`

### `IsWarAxe() → Bool`

### `IsWarhammer() → Bool`

### `SetBaseDamage(damage)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `damage` | `Int` | ✓ |  |

### `SetCritDamage(damage)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `damage` | `Int` | ✓ |  |

### `SetCritEffect(ce)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ce` | `Spell` | ✓ |  |

### `SetCritEffectOnDeath(ceod)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `ceod` | `Bool` | ✓ |  |

### `SetCritMultiplier(crit)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `crit` | `Float` | ✓ |  |

### `SetEnchantment(e)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `e` | `Enchantment` | ✓ |  |

### `SetEnchantmentValue(value)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `value` | `Int` | ✓ |  |

### `SetEquippedModel(model)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `model` | `Static` | ✓ |  |

### `SetEquipType(type)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `type` | `EquipSlot` | ✓ |  |

### `SetIconPath(path)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `path` | `String` | ✓ |  |

### `SetMaxRange(maxRange)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `maxRange` | `Float` | ✓ |  |

### `SetMessageIconPath(path)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `path` | `String` | ✓ |  |

### `SetMinRange(minRange)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `minRange` | `Float` | ✓ |  |

### `SetModelPath(path)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `path` | `String` | ✓ |  |

### `SetReach(reach)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `reach` | `Float` | ✓ |  |

### `SetResist(resist)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `resist` | `String` | ✓ |  |

### `SetSkill(skill)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `skill` | `String` | ✓ |  |

### `SetSpeed(speed)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `speed` | `Float` | ✓ |  |

### `SetStagger(stagger)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `stagger` | `Float` | ✓ |  |

### `SetWeaponType(type)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `type` | `Int` | ✓ |  |
