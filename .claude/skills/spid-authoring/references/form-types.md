# SPID Form Types — what you can distribute

The `FormType` (left of `=`) names *what kind of thing* you're distributing. This file is the
authoritative list. Mechanics of the line, referencing, and inference live in `grammar-core.md`;
signatures are also tabulated in `value-tables.md`.

> Source: the article's "Form Type" section (SPID 7.3.0). Record signatures are xEdit signatures.

---

## The 10 distributable form types

| FormType | Accepts records (signature) | Notes |
|---|---|---|
| **Spell** | `[SPEL, LVSP]` | Regular spells and leveled spells. |
| **Perk** | `[PERK]` | |
| **Item** | `[ALCH, AMMO, ARMO, BOOK, INGR, KEYM, LVLI, MISC, SCRL, SLGM, WEAP]` | "Basically any carriable item." Uses the `CountOrPackageIndex` field as a **count** (see grammar-core §9). |
| **Shout** | `[SHOU]` | |
| **Package** | `[PACK, FLST]` | Distributes AI packages. Uses `CountOrPackageIndex` as a **package index** (PACK) or **package-list type** (FLST). See the FLST crash caveat below. |
| **Keyword** | `[KYWD]` | Can also be **created dynamically** — see below. |
| **Outfit** | `[OTFT]` | Sets the NPC's outfit. |
| **SleepOutfit** | `[OTFT]` | Sets the NPC's *sleep* outfit. Same record type as Outfit — **cannot be inferred** (see below). |
| **Faction** | `[FACT]` | Adds the NPC to a faction. |
| **Skin** | `[ARMO]` | Sets the NPC's worn skin (xEdit `WNAM`). Same record type as an armor Item — **cannot be inferred**. |

---

## Special cases

### `Item` — any carriable
The Item type covers the full carriable set (`ALCH, AMMO, ARMO, BOOK, INGR, KEYM, LVLI, MISC, SCRL,
SLGM, WEAP`). When you distribute an Item, the `CountOrPackageIndex` field is the quantity, with a
default of 1 and an optional `min-max` random range (grammar-core §9).

### `Package` from a `FormList` — must contain only Packages
A `Package` entry may point at a `FLST`. **If that FormList contains anything other than Packages, the
game will most likely crash.** Keep package FormLists pure.

When the distributable form is a FormList, `CountOrPackageIndex` selects which *package list* to
overwrite (type `0`–`4`; see `value-tables.md` → Package List Types).

### `Keyword` — dynamic creation
SPID can **create a keyword that doesn't exist** and distribute it. Just name a keyword that isn't in
any plugin:

```ini
; Creates a brand-new keyword and distributes it to NPCs
Keyword = MyVerySpecialKeyword
```

This is the idiom behind keyword-tagging NPCs for other frameworks/mods to react to. (Recall from
grammar-core §3 that Keywords distribute first and are topo-sorted, so a dynamically-created keyword
can be used as a filter by a later keyword entry.)

### `SleepOutfit` and `Skin` — never inferred
Both reuse records that another type claims first:
- `SleepOutfit` uses `OTFT` → `Outfit` wins inference.
- `Skin` uses `ARMO` → `Item` wins inference.

So the generic `Form = …` shortcut (grammar-core §8) will **never** resolve to SleepOutfit or Skin.
Always write the type explicitly:

```ini
SleepOutfit = SomeBedClothesOutfit   ; must name the type — "Form = …" would become Outfit
Skin        = SomeBodySkinArmor      ; must name the type — "Form = …" would become Item
```

### The generic `Form` type
For every *other* type, `Form` infers correctly from the named record:

```ini
Form = SteelSword      ; => Item
Form = DefaultOutfit   ; => Outfit
```

Equivalent to writing `Item = SteelSword` / `Outfit = DefaultOutfit`.
