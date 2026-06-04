# `SWE`

**Source:** `dynamicwetness` (Dynamic Wetness) • **Flags:** Hidden

---

## Properties

### `CAT_ARMOR_CLOTH: Int`

**Flags:** Auto

**Accessors:** Get / Set

**Default:** `4`

1 << 2 - Armor & Clothing

### `CAT_HAIR: Int`

**Flags:** Auto

**Accessors:** Get / Set

**Default:** `2`

1 << 1 - Hair

### `CAT_MASK_4BIT: Int`

**Flags:** Auto

**Accessors:** Get / Set

**Default:** `15`

0x0F - mask for all category bits

### `CAT_SKIN_FACE: Int`

**Flags:** Auto

**Accessors:** Get / Set

**Default:** `1`

1 << 0 - Skin & Face

### `CAT_WEAPON: Int`

**Flags:** Auto

**Accessors:** Get / Set

**Default:** `8`

1 << 3 - Weapons

### `ENV_EXTERIOR_OPEN: Int`

**Flags:** Auto

**Accessors:** Get / Set

**Default:** `16`

exterior & not under cover

### `ENV_NEAR_HEAT: Int`

**Flags:** Auto

**Accessors:** Get / Set

**Default:** `4`

near heat source

### `ENV_UNDER_ROOF: Int`

**Flags:** Auto

**Accessors:** Get / Set

**Default:** `8`

under roof/cover (heuristic)

### `ENV_WATER: Int`

**Flags:** Auto

**Accessors:** Get / Set

**Default:** `1`

in water / submerged

### `ENV_WET_WEATHER: Int`

**Flags:** Auto

**Accessors:** Get / Set

**Default:** `2`

rain/snow affecting the actor

### `FLAG_NO_AUTODRY: Int`

**Flags:** Auto

**Accessors:** Get / Set

**Default:** `131072`

suppress auto-dry for affected categories

### `FLAG_PASSTHROUGH: Int`

**Flags:** Auto

**Accessors:** Get / Set

**Default:** `65536`

add after SWE blending/drying

### `FLAG_ZERO_BASE: Int`

**Flags:** Auto

**Accessors:** Get / Set

**Default:** `262144`

force base to 0 while active

### `MASK_SKIN_PASSTHROUGH: Int`

**Flags:** Auto

**Accessors:** Get / Set

**Default:** `458753`

---

## Global Functions

### `ClearExternalWetness(akActor, key)`

**Flags:** Native Global

Remove this external source (key is trimmed & lowercased internally).

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `key` | `String` | ✓ |  |

### `GetBaseWetness(akActor) → Float`

**Flags:** Native Global

Raw: base wetness computed from water/rain etc. (0..1), before external sources.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetEnvMask(akActor) → Int`

**Flags:** Native Global

Bitmask of environment flags (see ENV_* above).

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetExternalWetness(akActor, key) → Float`

**Flags:** Native Global

Value of a specific external source key (0..1), 0 if not present.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `key` | `String` | ✓ |  |

### `GetFinalWetness(akActor) → Float`

**Flags:** Native Global

Final: wetness after categories/blending/external sources (0..1).

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `GetSubmergedLevel(akActor) → Float`

**Flags:** Native Global

Submerged fraction (0 = dry, 1 = fully submerged).

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `IsActorInExteriorWet(akActor) → Bool`

**Flags:** Native Global

True if actor is in exterior and not covered (i.e., exposed).

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `IsActorWetByWater(akActor) → Bool`

**Flags:** Native Global

True if water contact is strong enough (alias of "in water" check).

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `IsNearHeatSource(akActor, radius) → Bool`

**Flags:** Native Global

True if actor is near a heat source; radius in world units.
Pass 0.0 to use the mod's configured radius.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `radius` | `Float` |  | `0` |

### `IsUnderRoof(akActor) → Bool`

**Flags:** Native Global

True if actor is under roof/cover (heuristic).

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `IsWetWeatherAround(akActor) → Bool`

**Flags:** Native Global

True if precipitation (rain/snow) is active and relevant for actor.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |

### `SetExternalWetness(akActor, key, value, durationSec)`

**Flags:** Native Global

Set/update an external source (default category = Skin).
durationSec <= 0 => infinite until cleared.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `key` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |
| `durationSec` | `Float` |  | `-1` |

### `SetExternalWetnessEx(akActor, key, value, durationSec, catMask, maxGloss, maxSpec, minGloss, minSpec, glossBoost, specBoost, skinHairMul)`

**Flags:** Native Global

Extended variant with optional per-material overrides.
Any negative override value => ignore (do not force/override).

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `key` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |
| `durationSec` | `Float` | ✓ |  |
| `catMask` | `Int` | ✓ |  |
| `maxGloss` | `Float` |  | `-1` |
| `maxSpec` | `Float` |  | `-1` |
| `minGloss` | `Float` |  | `-1` |
| `minSpec` | `Float` |  | `-1` |
| `glossBoost` | `Float` |  | `-1` |
| `specBoost` | `Float` |  | `-1` |
| `skinHairMul` | `Float` |  | `-1` |

### `SetExternalWetnessMask(akActor, key, value, durationSec, catMask)`

**Flags:** Native Global

Set/replace value AND categories/flags for this key.
IMPORTANT: catMask contains both categories (low 4 bits) AND flags (high bits).
Example: Int m = CAT_SKIN_FACE + CAT_HAIR + FLAG_PASSTHROUGH

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `key` | `String` | ✓ |  |
| `value` | `Float` | ✓ |  |
| `durationSec` | `Float` |  | `-1` |
| `catMask` | `Int` |  | `1` |
