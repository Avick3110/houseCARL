# `LibMathf`

**Source:** `libmathf` (LibMathf) • **Flags:** Hidden

---

## Global Functions

### `Abs(f) → Float`

**Flags:** Native Global

Returns the absolute value of f

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `f` | `Float` | ✓ |  |

### `Acos(f) → Float`

**Flags:** Native Global

Returns the arc-cosine of f - the angle in radians whose cosine is f

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `f` | `Float` | ✓ |  |

### `Approximately(a, b) → Bool`

**Flags:** Native Global

Compares two floating point values and returns true if they are similar

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a` | `Float` | ✓ |  |
| `b` | `Float` | ✓ |  |

### `Asin(f) → Float`

**Flags:** Native Global

Returns the arc-sine of f - the angle in radians whose sine is f

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `f` | `Float` | ✓ |  |

### `Atan(f) → Float`

**Flags:** Native Global

Returns the arc-tangent of f - the angle in radians whose tangent is f

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `f` | `Float` | ✓ |  |

### `Atan2(y, x) → Float`

**Flags:** Native Global

Returns the angle in radians whose tan is y/x

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `y` | `Float` | ✓ |  |
| `x` | `Float` | ✓ |  |

### `Ceil(f) → Float`

**Flags:** Native Global

Returns the smallest number (as Float) greater than or equal to f

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `f` | `Float` | ✓ |  |

### `CeilToInt(f) → Int`

**Flags:** Native Global

Returns the smallest number (as Int) greater than or equal to f

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `f` | `Float` | ✓ |  |

### `Clamp(value, min, max) → Float`

**Flags:** Native Global

Returns value clamped between min and max

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `value` | `Float` | ✓ |  |
| `min` | `Float` | ✓ |  |
| `max` | `Float` | ✓ |  |

### `Clamp01(value) → Float`

**Flags:** Native Global

Returns value clamped between 0 and 1

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `value` | `Float` | ✓ |  |

### `ClosestPowerOfTwo(value) → Int`

**Flags:** Native Global

Returns the closest power of two number

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `value` | `Int` | ✓ |  |

### `Cos(f) → Float`

**Flags:** Native Global

Returns the cosine of angle f

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `f` | `Float` | ✓ |  |

### `DeltaAngle(current, target) → Float`

**Flags:** Native Global

Calculates the shortest difference between two angles in degrees

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `current` | `Float` | ✓ |  |
| `target` | `Float` | ✓ |  |

### `Exp(p) → Float`

**Flags:** Native Global

Returns e raised to the specified power

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `p` | `Float` | ✓ |  |

### `Floor(f) → Float`

**Flags:** Native Global

Returns the largest number (as Float) smaller than or equal to f

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `f` | `Float` | ✓ |  |

### `FloorToInt(f) → Int`

**Flags:** Native Global

Returns the largest number (as Int) smaller than or equal to f

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `f` | `Float` | ✓ |  |

### `IfThen(value, t, f) → Float`

**Flags:** Native Global

Returns t if value is true or f if value is false

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `value` | `Bool` | ✓ |  |
| `t` | `Float` | ✓ |  |
| `f` | `Float` | ✓ |  |

### `InRange(value, min, max) → Bool`

**Flags:** Native Global

Returns true if value is between min and max

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `value` | `Float` | ✓ |  |
| `min` | `Float` | ✓ |  |
| `max` | `Float` | ✓ |  |

### `InverseLerp(a, b, value) → Float`

**Flags:** Native Global

Calculates the linear parameter t that produces the interpolant value within the range [a, b]

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a` | `Float` | ✓ |  |
| `b` | `Float` | ✓ |  |
| `value` | `Float` | ✓ |  |

### `IsPowerOfTwo(n) → Bool`

**Flags:** Native Global

Returns true if the number is power of two

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `n` | `Int` | ✓ |  |

### `Lerp(a, b, t) → Float`

**Flags:** Native Global

Linearly interpolates between a and b by t

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a` | `Float` | ✓ |  |
| `b` | `Float` | ✓ |  |
| `t` | `Float` | ✓ |  |

### `LerpAngle(a, b, t) → Float`

**Flags:** Native Global

Same as Lerp but makes sure the values interpolate correctly when they wrap around 360 degrees

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a` | `Float` | ✓ |  |
| `b` | `Float` | ✓ |  |
| `t` | `Float` | ✓ |  |

### `LerpUnclamped(a, b, t) → Float`

**Flags:** Native Global

Linearly interpolates between a and b by t with no limit to t

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a` | `Float` | ✓ |  |
| `b` | `Float` | ✓ |  |
| `t` | `Float` | ✓ |  |

### `Log(f) → Float`

**Flags:** Native Global

Returns the logarithm of a number

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `f` | `Float` | ✓ |  |

### `Log10(f) → Float`

**Flags:** Native Global

Returns the base 10 logarithm of a number

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `f` | `Float` | ✓ |  |

### `Max(x, y) → Float`

**Flags:** Native Global

Returns the largest of two numbers

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `x` | `Float` | ✓ |  |
| `y` | `Float` | ✓ |  |

### `Min(x, y) → Float`

**Flags:** Native Global

Returns the smallest of two numbers

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `x` | `Float` | ✓ |  |
| `y` | `Float` | ✓ |  |

### `MoveTowards(current, target, maxDelta) → Float`

**Flags:** Native Global

Moves current value towards target

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `current` | `Float` | ✓ |  |
| `target` | `Float` | ✓ |  |
| `maxDelta` | `Float` | ✓ |  |

### `MoveTowardsAngle(current, target, maxDelta) → Float`

**Flags:** Native Global

Same as MoveTowards but makes sure the values interpolate correctly when they wrap around 360 degrees

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `current` | `Float` | ✓ |  |
| `target` | `Float` | ✓ |  |
| `maxDelta` | `Float` | ✓ |  |

### `NextPowerOfTwo(n) → Int`

**Flags:** Native Global

Returns the next power of two greater than or equal to n

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `n` | `Int` | ✓ |  |

### `PingPong(t, len) → Float`

**Flags:** Native Global

Returns number that will increment and decrement between 0 and length

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `t` | `Float` | ✓ |  |
| `len` | `Float` | ✓ |  |

### `Pow(f, p) → Float`

**Flags:** Native Global

Returns f raised to power p

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `f` | `Float` | ✓ |  |
| `p` | `Float` | ✓ |  |

### `Repeat(t, len) → Float`

**Flags:** Native Global

Loops t so t is never larger than length and never smaller than 0

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `t` | `Float` | ✓ |  |
| `len` | `Float` | ✓ |  |

### `Round(f) → Float`

**Flags:** Native Global

Returns f (as Float) rounded to the nearest integer

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `f` | `Float` | ✓ |  |

### `RoundToInt(f) → Int`

**Flags:** Native Global

Returns f (as Int) rounded to the nearest integer

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `f` | `Float` | ✓ |  |

### `Sign(f) → Float`

**Flags:** Native Global

Returns the sign of f

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `f` | `Float` | ✓ |  |

### `Sin(f) → Float`

**Flags:** Native Global

Returns the sine of angle f

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `f` | `Float` | ✓ |  |

### `SmoothStep(current, target, t) → Float`

**Flags:** Native Global

Interpolates between min and max with smoothing at the limits

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `current` | `Float` | ✓ |  |
| `target` | `Float` | ✓ |  |
| `t` | `Float` | ✓ |  |

### `Sqrt(f) → Float`

**Flags:** Native Global

Returns square root of f

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `f` | `Float` | ✓ |  |

### `Tan(f) → Float`

**Flags:** Native Global

Returns the tangent of angle f in radians

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `f` | `Float` | ✓ |  |
