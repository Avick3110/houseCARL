# KID Traits — section 3

The traits section (position 3) narrows distribution by **type-specific item properties** — the
enchanted flag, an armor-rating range, a weapon's animation type, a spell's school, and so on. It's
optional; leave it blank or `NONE` to skip it.

Only the **12 trait-bearing types** have traits (the ✅ rows in `types.md`). The other 7 types
(Location, Misc Item, Key, Activator, Flora, Race, Talking Activator) parse **no** traits — a traits
section on them is ignored. **[source]**

**Notations used below [source]:**
- **Single-letter booleans** (`E`, `T`, `H`, `B`, `P`, `F`, `S`, `AV`, `BLACK`, `DISPEL`) negate with a
  `-` prefix: `E` = enchanted, `-E` = not enchanted. Range/value traits do **not** negate.
- **`X(min/max)`** = a numeric range (e.g. `AR(10/50)`). One number — `X(10)` — sets only the minimum.
  The separator is any non-digit, but `/` is canonical (the Nexus examples use it).
- **`KEY(value)`** = a single enum number; the value lists live in `value-tables.md`.
- Traits are **comma-separated**; combine several to AND them (`P,F` = poison **and** food).

---

## Armor (ARMO)

| Trait | Meaning |
|---|---|
| `E` / `-E` | enchanted / not enchanted |
| `T` / `-T` | templated / not templated |
| `AR(min/max)` | armor-rating range (float) |
| `W(min/max)` | weight range (float) |
| `30`–`61` | a single number = **biped body slot** (slot N → bit `1<<(N-30)`) |
| `HEAVY` / `LIGHT` / `CLOTHING` | armor class |

## Weapon (WEAP)

| Trait | Meaning |
|---|---|
| `E` / `-E` | enchanted / not enchanted |
| `T` / `-T` | templated / not templated |
| `W(min/max)` | weight range (float) |
| `D(min/max)` | damage range (float) |
| animation type | one of: `HandToHandMelee` `OneHandSword` `OneHandDagger` `OneHandAxe` `OneHandMace` `TwoHandSword` `TwoHandAxe` `Bow` `Staff` `Crossbow` |

## Ammo (AMMO)

| Trait | Meaning |
|---|---|
| `B` / `-B` | is a bolt / is not a bolt (i.e. arrow) |
| `D(min/max)` | damage range (float) |

## Magic Effect (MGEF)

| Trait | Meaning |
|---|---|
| `H` / `-H` | hostile / not hostile |
| `DISPEL` / `-DISPEL` | has / lacks the *Dispel With Keywords* flag |
| `D(value)` | delivery type — `value-tables.md` → Delivery |
| `CT(value)` | casting type — `value-tables.md` → Casting Type |
| `R(value)` | resistance actor value (numeric) — `value-tables.md` → Resistances |
| `<av>(min/max)` | **school + skill range** — `<av>` is the school's actor-value number, `min/max` the skill level. `20(0/25)` = all novice (0–25) Destruction effects. Schools in `value-tables.md`. |

## Potion (ALCH)

| Trait | Meaning |
|---|---|
| `P` / `-P` | poison / not poison |
| `F` / `-F` | food / not food |

*(Potions, food, and poisons are all ALCH — these two traits separate them.)*

## Ingredient (INGR)

| Trait | Meaning |
|---|---|
| `F` / `-F` | food / not food |

## Book (BOOK)

| Trait | Meaning |
|---|---|
| `S` / `-S` | teaches a spell / doesn't |
| `AV` / `-AV` | teaches a skill (actor value) / doesn't |
| `<av>` | a numeric actor value = the specific skill/spell-type the book is associated with (e.g. `20` = Destruction). Names→numbers in `value-tables.md`. |

## Soul Gem (SLGM)

| Trait | Meaning |
|---|---|
| `BLACK` / `-BLACK` | can / cannot hold an NPC (black) soul |
| `SOUL(size)` | size of the soul **currently contained** — `value-tables.md` → Soul Sizes (1–5) |
| `GEM(size)` *or bare* `<size>` | the gem's **maximum capacity** — sizes 1–5 |

## Spell · Enchantment · Scroll (SPEL / ENCH / SCRL) — shared

| Trait | Meaning |
|---|---|
| `H` / `-H` | hostile / not hostile |
| `ST(value)` | spell type — `value-tables.md` → Spell Types (0–13) |
| `D(value)` | delivery type — `value-tables.md` → Delivery |
| `CT(value)` | casting type — `value-tables.md` → Casting Type |
| `<av>` | a single numeric actor value = associated skill/school (e.g. `20` = all Destruction spells). *No `(min/max)` here — that range form is Magic-Effect-only.* |

## Furniture (FURN)

| Trait | Meaning |
|---|---|
| `T(value)` | furniture type — `value-tables.md` → Furniture Types (0–3) |
| `BT(value)` | workbench type — `value-tables.md` → Bench Types (1–7) |
| `US(value)` | workbench use-skill actor value — `value-tables.md` → Actor Values |

---

## Worked examples [desc]

```ini
;all novice destruction magic effects (school 20, skill 0-25)
Keyword = NoviceDestruction|Magic Effect|NONE|20(0/25)

;poisonous foods (poison AND food)
Keyword = PoisonousFood|Potion|NONE|P,F

;non-enchanted heavy gauntlets (slot 33) — trait -E plus a slot number
Keyword = PlainGauntlets|Armor|NONE|-E,33

;all magic effects with the Absorb archetype (archetype is a STRING filter, not a trait)
Keyword = MagicAbsorb|Magic Effect|Absorb
```
