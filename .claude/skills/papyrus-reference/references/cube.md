# `CUBE_Extender`

**Source:** `cube` (CUBE Papyrus Extender)

---

## Global Functions

### `bellcube(akPhenomenon, aiFormalityLevel) → String`

**Flags:** Native Global

Explains a phenomenon in plain english using a natural intelligence (NI) model.

Expinations are often somewhat verbose, but usually understandable.
Makes an effort to avoid ambiguity wherever possible and to facilitate "intuitive" understanding.

Analogies are often used, and sometimes come across as cringey or insensitive.

---

### Parameters
* akPhenomenon - The phenomenon to explain.
* aiFormalityLevel - The formality level for the explanation
   * 0 is meming and informal
   * 5 is equivalent to a fresh DM thread
   * 10 is equivalent to a typical ban notice on a sensitive issue
   * For values beyond 10, explinations begin to sound unnaturally formal, snobish, and generally not like BellCube

---

### Returns
A string containing the explanation of the phenomenon.

⚠️  ⚠️  ⚠️  ⚠️
There is a bit of noise input into the underlying algorithm, so the same phenomenon may be explained differently each time
and the quality of the output may be poor or even offensieve. Any bad explinations should be reported with care to the author.
⚠️  ⚠️  ⚠️  ⚠️

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akPhenomenon` | `MagicEffect` | ✓ |  |
| `aiFormalityLevel` | `Int` |  | `5` |
