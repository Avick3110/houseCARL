# `DynamicAnimationCasting`

**Source:** `dynanimcast` (Dynamic Animation Casting)

---

## Global Functions

### `NextFavouriteSpell(delta) → Int`

**Flags:** Native Global

Select the next or previous favourite spell to cast from @FAVOURITE spell in DynamicAnimationCasting.toml

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `delta` | `Int` | ✓ |  |

### `RegisterCustomSpell(name, spell) → Bool`

**Flags:** Native Global

Register a custom spell for the name, which can be casted by @NAME in DynamicAnimationCasting.toml

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `name` | `String` | ✓ |  |
| `spell` | `Spell` | ✓ |  |

### `SelectFavouriteSpell(index) → Bool`

**Flags:** Native Global

Select the favourite spell index to cast from @FAVOURITE spell in DynamicAnimationCasting.toml

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |
