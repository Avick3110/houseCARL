# `SEA_BarterFunctions`

**Source:** `currency-swapper` (Currency Swapper)

---

## Events

### `OnCurrencyRevert(a_kOldCurrency)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `a_kOldCurrency` | `Form` |

### `OnCurrencySwap(a_kOldCurrency, a_kNewCurrency)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `a_kOldCurrency` | `Form` |
| `a_kNewCurrency` | `Form` |

### `OnCustomBarterMenu(a_kSeller)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `a_kSeller` | `Actor` |

### `OnCustomCurrencyFail(a_kCurrency)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `a_kCurrency` | `Form` |

### `OnCustomPurchase(a_kCurrency)`

**Kind:** Event

Events

**Parameters**

| Name | Type |
|---|---|
| `a_kCurrency` | `Form` |

### `OnCustomSale(a_kCurrency)`

**Kind:** Event

**Parameters**

| Name | Type |
|---|---|
| `a_kCurrency` | `Form` |

---

## Global Functions

### `GetCurrency() → Form`

**Flags:** Native Global

### `RegisterFormForAllEvents(a_kForm)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_kForm` | `Form` | ✓ |  |

### `ResetCurrency()`

**Flags:** Native Global

### `SetCurrency(a_kNewCurrency) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_kNewCurrency` | `Form` | ✓ |  |

### `SetCurrencyConsole(a_sNewCurrencyEditorID) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_sNewCurrencyEditorID` | `String` | ✓ |  |

### `SetTrainingOverrides(a_bOverrideMultiplier, a_bMultOverride, a_bOverrideBase, a_bBaseOverride)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_bOverrideMultiplier` | `Bool` | ✓ |  |
| `a_bMultOverride` | `Float` | ✓ |  |
| `a_bOverrideBase` | `Bool` | ✓ |  |
| `a_bBaseOverride` | `Float` | ✓ |  |

### `SetTrainingOverridesConsole(a_sOverrideMultiplier, a_sMultOverride, a_sOverrideBase, a_sBaseOverride)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_sOverrideMultiplier` | `String` | ✓ |  |
| `a_sMultOverride` | `String` | ✓ |  |
| `a_sOverrideBase` | `String` | ✓ |  |
| `a_sBaseOverride` | `String` | ✓ |  |
