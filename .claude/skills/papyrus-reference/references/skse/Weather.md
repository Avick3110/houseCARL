# `Weather`

**Source:** `skse` (Skyrim Script Extender x64) • **Extends:** `Form` • **Flags:** Hidden

---

## Global Functions

### `FindWeather(auiType) → Weather`

**Flags:** Native Global

Finds a weather from the current region/climate whose classification matches the given one.
0 - Pleasant
1 - Cloudy
2 - Rainy
3 - Snow

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `auiType` | `Int` | ✓ |  |

### `GetCurrentWeather() → Weather`

**Flags:** Native Global

Gets the sky's current weather

### `GetCurrentWeatherTransition() → Float`

**Flags:** Native Global

Gets the transition percentage of the current weather

### `GetOutgoingWeather() → Weather`

**Flags:** Native Global

Gets the sky's outgoing weather

### `GetSkyMode() → Int`

**Flags:** Native Global

Gets the sky's current mode
0 - No sky (SM_NONE)
1 - Interior (SM_INTERIOR)
2 - Skydome only (SM_SKYDOME_ONLY)
3 - Full sky (SM_FULL)

### `ReleaseOverride()`

**Flags:** Native Global

Tells the sky to release its overriding weather.

---

## Functions

### `ForceActive(abOverride)`

**Flags:** Native

Forces the active weather on the sky to be this weather.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abOverride` | `Bool` |  | `false` |

### `GetClassification() → Int`

**Flags:** Native

Gets this weather's classification
-1 - No classification
 0 - Pleasant
 1 - Cloudy
 2 - Rainy
 3 - Snow

### `GetFogDistance(day, type) → Float`

**Flags:** Native

0 - Near
1 - Far
2 - Power
3 - Max

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `day` | `Bool` | ✓ |  |
| `type` | `Int` | ✓ |  |

### `GetSunDamage() → Float`

**Flags:** Native

Returns the sun damage percentage

### `GetSunGlare() → Float`

**Flags:** Native

Returns the sun glare percentage

### `GetWindDirection() → Float`

**Flags:** Native

Returns the wind direction in degrees (0-360)

### `GetWindDirectionRange() → Float`

**Flags:** Native

Returns the wind direction range in degrees (0-180)

### `SetActive(abOverride, abAccelerate)`

**Flags:** Native

Sets the active weather on the sky to be this weather.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abOverride` | `Bool` |  | `false` |
| `abAccelerate` | `Bool` |  | `false` |
