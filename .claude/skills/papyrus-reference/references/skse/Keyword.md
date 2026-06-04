# `Keyword`

**Source:** `skse` (Skyrim Script Extender x64) • **Extends:** `Form` • **Flags:** Hidden

---

## Global Functions

### `GetKeyword(key) → Keyword`

**Flags:** Native Global

SKSE64 additions built 2024-01-17 20:01:40.731000 UTC
return the keyword with the specified key

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `key` | `String` | ✓ |  |

---

## Functions

### `GetString() → String`

**Flags:** Native

return the string value of the keyword

### `SendStoryEvent(akLoc, akRef1, akRef2, aiValue1, aiValue2)`

**Flags:** Native

Sends this keyword as a story event to the story manager

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akLoc` | `Location` |  |  |
| `akRef1` | `ObjectReference` |  |  |
| `akRef2` | `ObjectReference` |  |  |
| `aiValue1` | `Int` |  | `0` |
| `aiValue2` | `Int` |  | `0` |

### `SendStoryEventAndWait(akLoc, akRef1, akRef2, aiValue1, aiValue2) → Bool`

**Flags:** Native

Sends this keyword as a story event to the story manager and waits for it to be processed. Returns true if a quest was started.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akLoc` | `Location` |  |  |
| `akRef1` | `ObjectReference` |  |  |
| `akRef2` | `ObjectReference` |  |  |
| `aiValue1` | `Int` |  | `0` |
| `aiValue2` | `Int` |  | `0` |
