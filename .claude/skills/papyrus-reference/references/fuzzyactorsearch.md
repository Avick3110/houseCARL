# `bk56_SearchActorFuzzy`

**Source:** `fuzzyactorsearch` (Fuzzy Actor Search) • **Flags:** Hidden

---

## Global Functions

### `bk56_SearchActorFuzzy(actorName, nearbyOnly, handledOnly, WeightMin) → Int`

**Flags:** Native Global

Useful for locating NPCs whose display names may be altered or obscured by other modifications or for other mods that may not be able to get the formID directly.

Returns the FormID of an Actor whose name closely matches the input string. Supports fuzzy matching and optional filters for proximity and engine-handled status.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `actorName` | `String` | ✓ |  |
| `nearbyOnly` | `Bool` | ✓ |  |
| `handledOnly` | `Bool` | ✓ |  |
| `WeightMin` | `Float` | ✓ |  |
