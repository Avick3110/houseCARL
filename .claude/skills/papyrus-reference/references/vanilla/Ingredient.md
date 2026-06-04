# `Ingredient`

**Source:** `vanilla` • **Extends:** `Form`

---

## Functions

### `IsHostile() → Bool`

**Flags:** Native

Is this ingredient classified as hostile?

### `LearnAllEffects()`

**Flags:** Native

Flags the all effects as known by the player

### `LearnEffect(aiIndex)`

**Flags:** Native

Flags the effect with the given 0 based index as known by the player

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiIndex` | `Int` | ✓ |  |

### `LearnNextEffect() → Int`

**Flags:** Native

Flags the next unknown effect as known by the player, returning index of effect learned
