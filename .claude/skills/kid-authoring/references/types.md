# KID Item Types — section 1

The `type` section (position 1) names **which kind of item** the keyword is added to. KID supports
**19 types**. Write the type string **exactly as listed** below — these are the canonical names KID
matches (`Cache.h` `itemTypes`). **[source]**

A single `Keyword =` line targets **one** type; repeat the line per type to cover several
(`grammar-core.md` §4).

## The 19 types

| Type string | Record (xEdit) | Has traits? | Trait class (`traits.md`) |
|---|---|---|---|
| `Armor` | ARMO | ✅ | Armor |
| `Weapon` | WEAP | ✅ | Weapon |
| `Ammo` | AMMO | ✅ | Ammo |
| `Magic Effect` | MGEF | ✅ | Magic Effect |
| `Potion` | ALCH | ✅ | Potion |
| `Scroll` | SCRL | ✅ | *(shared)* Spell |
| `Location` | LCTN | — | — |
| `Ingredient` | INGR | ✅ | Ingredient |
| `Book` | BOOK | ✅ | Book |
| `Misc Item` | MISC | — | — |
| `Key` | KEYM | — | — |
| `Soul Gem` | SLGM | ✅ | Soul Gem |
| `Spell` | SPEL | ✅ | Spell |
| `Activator` | ACTI | — | — |
| `Flora` | FLOR | — | — |
| `Furniture` | FURN | ✅ | Furniture |
| `Race` | RACE | — | — |
| `Talking Activator` | TACT | — | — |
| `Enchantment` | ENCH | ✅ | *(shared)* Spell |

> **Scout-memory correction:** earlier notes claimed only ~12 types with the rest "historical." All 19
> above are current in v3.5.0 — confirmed in the parser's type enum and the live Nexus description.

## Notes per type

- **`Potion` = ALCH = potions *and* food *and* poisons.** They're all `AlchemyItem` records; the
  Potion traits (`P` poison / `F` food, `traits.md`) split them apart.
- **`Magic Effect` (MGEF)** is the *effect*, not the spell. Adding a keyword to a magic effect affects
  every spell/potion/scroll/enchantment that uses it. To target the castable form itself, use `Spell`,
  `Scroll`, or `Enchantment`.
- **`Spell`, `Enchantment`, `Scroll` share one trait class** (Spell traits — `H`, `ST`, `D`, `CT`,
  skill AV). They're all `MagicItem`-derived, so the same trait grammar applies to all three.
- **`Enchantment` = ENCH** is the enchantment record (object/weapon enchantments), distinct from the
  `E` *enchanted* trait on Armor/Weapon (which filters items that *carry* an enchantment).

## The 7 trait-less types

`Location`, `Misc Item`, `Key`, `Activator`, `Flora`, `Race`, `Talking Activator` have **no trait
class** — KID parses no traits for them (`traits.md` doesn't list them). Narrow these using **section 2
filters only** (names, EditorIDs, FormIDs, plugin name, and their type-specific Form filters such as
`Location` → music type / crime faction / parent location — see `filters.md`). A traits section on one
of these types is silently ignored, so leave it blank:

```ini
Keyword = LocTypeDungeon|Location|SomeDungeonLocationID      ; no traits — filter by form
Keyword = VendorGold|Misc Item|Gold001                       ; misc items take no traits
```
