# `ShazdehUtils`

**Source:** `shazdeh` (Shazdeh Papyrus Utils) • **Flags:** Hidden

---

## Global Functions

### `FormHasMagicEffect(akForm, akEffect) → Bool`

**Flags:** Native Global

akForm can be either: Spell, Potion, Ingredient, Scroll, Enchantment

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `akEffect` | `MagicEffect` | ✓ |  |

### `FormHasMagicEffectKeyword(akForm, akKeyword) → Bool`

**Flags:** Native Global

akForm can be either: Spell, Potion, Ingredient, Scroll, Enchantment

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `akKeyword` | `Keyword` | ✓ |  |

### `FormlistHasFormDeep(akList, akForm) → Bool`

**Flags:** Native Global

Recursive HasForm that checks FormList(s) within akList as well

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akList` | `FormList` | ✓ |  |
| `akForm` | `Form` | ✓ |  |

### `GetClipboard() → String`

**Flags:** Native Global

### `GetFormTypeString(akForm) → String`

**Flags:** Native Global

returns the 4 character Form signature

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `GetFormWithStrongestEffect(akSource, akEffect, aiFormType, akKeywordFilter) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSource` | `ObjectReference` | ✓ |  |
| `akEffect` | `MagicEffect` | ✓ |  |
| `aiFormType` | `Int` | ✓ |  |
| `akKeywordFilter` | `Keyword` |  |  |

### `GetFormWithWeakestEffect(akSource, akEffect, aiFormType, akKeywordFilter) → Form`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSource` | `ObjectReference` | ✓ |  |
| `akEffect` | `MagicEffect` | ✓ |  |
| `aiFormType` | `Int` | ✓ |  |
| `akKeywordFilter` | `Keyword` |  |  |

### `GetItemCountInContainersArray(aReferences, akForm) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aReferences` | `ObjectReference[]` | ✓ |  |
| `akForm` | `Form` | ✓ |  |

### `GetItemCountInContainersList(akList, akForm) → Int`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akList` | `FormList` | ✓ |  |
| `akForm` | `Form` | ✓ |  |

### `GetLinkedDoor(akRef) → ObjectReference`

**Flags:** Native Global

used for teleport doors, gives you the other door connected to this Reference

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akRef` | `ObjectReference` | ✓ |  |

### `GetStrongestPotion(akSource, akEffect, akKeywordFilter) → Potion`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSource` | `ObjectReference` | ✓ |  |
| `akEffect` | `MagicEffect` | ✓ |  |
| `akKeywordFilter` | `Keyword` |  |  |

### `GetStrongestSpell(akSource, akEffect, akKeywordFilter) → Spell`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSource` | `ObjectReference` | ✓ |  |
| `akEffect` | `MagicEffect` | ✓ |  |
| `akKeywordFilter` | `Keyword` |  |  |

### `GetWeakestPotion(akSource, akEffect, akKeywordFilter) → Potion`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSource` | `ObjectReference` | ✓ |  |
| `akEffect` | `MagicEffect` | ✓ |  |
| `akKeywordFilter` | `Keyword` |  |  |

### `GetWeakestSpell(akSource, akEffect, akKeywordFilter) → Spell`

**Flags:** Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSource` | `ObjectReference` | ✓ |  |
| `akEffect` | `MagicEffect` | ✓ |  |
| `akKeywordFilter` | `Keyword` |  |  |

### `HasAllKeywordsInList(akForm, akList) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `akList` | `FormList` | ✓ |  |

### `HasAllPerksInList(akActor, akList) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `akList` | `FormList` | ✓ |  |

### `HasAnyKeywordInList(akForm, akList) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |
| `akList` | `FormList` | ✓ |  |

### `HasAnyPerkInList(akActor, akList) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akActor` | `Actor` | ✓ |  |
| `akList` | `FormList` | ✓ |  |

### `IsGamepadConnected() → Bool`

**Flags:** Native Global

Game.IsUsingGamepad() return false if the last pressed key is from keyboard,
whereas this strictly checks whether gamepad is connected.

### `LookupFormSmart(asID) → Form`

**Flags:** Native Global

Finds form by either its Editor ID, or by Form ID

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asID` | `String` | ✓ |  |

### `SetClipboard(asText) → Bool`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `asText` | `String` | ✓ |  |

### `SimulateLeftStickInput(afXValue, afYValue)`

**Flags:** Native Global

Simulate pressing the left thumb stick on gamepad

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `afXValue` | `Float` |  | `0` |
| `afYValue` | `Float` |  | `0` |

### `TransferItemFromContainersArray(akList, akRef, akItem, aiCount)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akList` | `FormList` | ✓ |  |
| `akRef` | `ObjectReference[]` | ✓ |  |
| `akItem` | `Form` | ✓ |  |
| `aiCount` | `Int` | ✓ |  |

### `TransferItemFromContainersList(akList, akRef, akItem, aiCount)`

**Flags:** Native Global

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akList` | `FormList` | ✓ |  |
| `akRef` | `ObjectReference` | ✓ |  |
| `akItem` | `Form` | ✓ |  |
| `aiCount` | `Int` | ✓ |  |

### `TransferItems(akSource, akTarget, aiCount, aiFormType, akFormFilter, abExcludeWorn, abExcludeFavorites, abExcludeQuestItems)`

**Flags:** Native Global

akFormFilter can be:
  None;
  a Keyword (only trasnfer items with that keyword);
  or a FormList (only transfer items within that FormList),
  or Form

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSource` | `ObjectReference` | ✓ |  |
| `akTarget` | `ObjectReference` | ✓ |  |
| `aiCount` | `Int` |  | `-1` |
| `aiFormType` | `Int` |  | `0` |
| `akFormFilter` | `Form` |  |  |
| `abExcludeWorn` | `Bool` |  | `true` |
| `abExcludeFavorites` | `Bool` |  | `true` |
| `abExcludeQuestItems` | `Bool` |  | `true` |
