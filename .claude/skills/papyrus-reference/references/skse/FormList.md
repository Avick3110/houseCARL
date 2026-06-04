# `FormList`

**Source:** `skse` (Skyrim Script Extender x64) • **Extends:** `Form`

---

## Functions

### `AddForm(apForm)`

**Flags:** Native

Adds the given form to this form list

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `apForm` | `Form` | ✓ |  |

### `AddForms(forms)`

**Flags:** Native

Adds an Array of Forms to this list

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `forms` | `Form[]` | ✓ |  |

### `Find(apForm) → Int`

**Flags:** Native

Finds the specified form in the form list and returns its index.
If not found, returns a negative number

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `apForm` | `Form` | ✓ |  |

### `GetAt(aiIndex) → Form`

**Flags:** Native

Returns the form at index 'aiIndex' in the list

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiIndex` | `Int` | ✓ |  |

### `GetSize() → Int`

**Flags:** Native

Returns the number of forms in the list

### `HasForm(akForm) → Bool`

**Flags:** Native

Queries the form list to see if it contains the passed in form

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akForm` | `Form` | ✓ |  |

### `RemoveAddedForm(apForm)`

**Flags:** Native

Removes the given added form from this form list

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `apForm` | `Form` | ✓ |  |

### `Revert()`

**Flags:** Native

Removes all script added forms from this form list

### `ToArray() → Form[]`

**Flags:** Native

SKSE64 additions built 2024-01-17 20:01:40.731000 UTC
Returns a Form array of this list (Invalid entries will be None)
