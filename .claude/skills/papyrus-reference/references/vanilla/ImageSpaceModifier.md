# `ImageSpaceModifier`

**Source:** `vanilla` • **Extends:** `Form` • **Flags:** Hidden

---

## Global Functions

### `RemoveCrossFade(afFadeDuration)`

**Flags:** Native Global

Removes whatever modifier is on the cross-fade chain, fading it out

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afFadeDuration` | `Float` |  | `1` |

---

## Functions

### `Apply(afStrength)`

**Flags:** Native

Applies this modifier with the specified strength (which is not clamped)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afStrength` | `Float` |  | `1` |

### `ApplyCrossFade(afFadeDuration)`

**Flags:** Native

Adds this modifier to the cross-fade chain, removing the previous modifier, and fading over the specified duration (in seconds)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afFadeDuration` | `Float` |  | `1` |

### `PopTo(akNewModifier, afStrength)`

**Flags:** Native

Disables this modifier and enables the new one, "popping" between the two. Will not interrupt any running cross-fade (if the modifiers aren't the ones fading)

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akNewModifier` | `ImageSpaceModifier` | ✓ |  |
| `afStrength` | `Float` |  | `1` |

### `Remove()`

**Flags:** Native

Removes this modifier
