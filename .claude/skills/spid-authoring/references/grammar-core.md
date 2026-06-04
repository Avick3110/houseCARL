# SPID Grammar — Core

The shared spine of the `spid-authoring` corpus: what SPID does, how it loads and orders configs,
the shape of a distribution line, how inputs are normalized, how filters combine, and the two
per-line tail fields (Count/Index and Chance). The two domain vocabularies live beside this file:

- **`form-types.md`** — the 10 things you can distribute (the left side / the `FormType`).
- **`filters.md`** — the 4 filter sections that choose *which NPCs* receive the form.
- **`value-tables.md`** — the flat lookup tables (skill indices, trait letters, package-list types,
  form signatures, distribution order) that the prose files reference.

> **Source of truth.** Everything here traces to the verbatim "SPID: The Complete Reference"
> article (Nexus article 6617, SPID 7.3.0) or to the MIT-source cross-check
> (`powerof3/Spell-Perk-Item-Distributor`). Where a fact comes from source rather than the
> article, it's marked **[source]**.

---

## 1. What SPID is

SPID (**Spell Perk Item Distributor**, by powerofthree) is an SKSE plugin that distributes forms —
spells, perks, items, shouts, packages, keywords, outfits, factions, skins — **to NPCs at runtime**,
driven by plain-text `_DISTR.ini` config files.

It writes nothing to plugins and nothing to the save. SPID redistributes everything from scratch on
every game launch — think of it as a *virtual plugin that applies last in the load order*. That makes
any SPID-based mod safe to install or uninstall at any time, leaving no trace.

Because distribution targets NPCs (not records on disk), SPID is the tool for "give X to this group of
NPCs," not for editing a record's own fields — that's SkyPatcher's job. See the routing skill for the
cross-tool decision.

---

## 2. File discovery & load order

- SPID scans Skyrim's `Data/` folder for every INI whose name carries the **`_DISTR`** suffix
  (e.g. `MyMod_DISTR.ini`).
- Files are loaded in **alphabetical order, A → Z**.
- Each file is read **top to bottom**. The relative order of entries *of the same form type* is
  preserved, and that order is honored later at distribution time (see §3).

There are no per-type subfolders (unlike SkyPatcher) — all `*_DISTR.ini` live flat in `Data/`
(commonly shipped inside a mod managed by a mod manager, but resolved from `Data/` at runtime).

Comments: standard INI line comments with **`;`** are supported — SPID reads configs through the
**CSimpleIniA** library, whose default line-comment character is `;`. **[source]** (The `=` split and
comment stripping are CSimpleIniA's, not SPID's own code.)

---

## 3. When distribution happens, and in what order

**When:** distribution is **lazy** — it runs when NPCs are loaded into the world (typically on cell
load), not all at once at game start.

**Order (per-type passes):** SPID processes each form type separately, in this fixed order:

```
Keywords → Factions → Perks → Spells → Shouts → Packages → Items → Skins → Outfits
```

Within a single type, forms distribute in the **order they were loaded** (file A→Z, then top→bottom
within each file) — with one exception:

- **Keywords are additionally topo-sorted** so that a keyword which depends on another keyword is
  distributed *after* its dependency. You can freely use keywords as requirements for other keywords
  without hand-ordering them.

(This full order list is also in `value-tables.md` for quick lookup.)

---

## 4. The distribution line

Every entry is one line of the form:

```
FormType = FormOrEditorID | StringFilters | FormFilters | LevelFilters | TraitFilters | CountOrPackageIndex | Chance
```

The **right-hand side is 7 fields separated by 6 pipes** (`|`). Only the first RHS field is required;
the other six are optional. `FormType` (left of `=`) is also required.

| Pos | Field | Required? | Covered in |
|----:|---|---|---|
| — | `FormType` (left of `=`) | **yes** | `form-types.md` |
| 0 | `FormOrEditorID` | **yes** | §6 below |
| 1 | `StringFilters` | optional | `filters.md` → String |
| 2 | `FormFilters` | optional | `filters.md` → Form |
| 3 | `LevelFilters` | optional | `filters.md` → Level |
| 4 | `TraitFilters` | optional | `filters.md` → Trait |
| 5 | `CountOrPackageIndex` | optional (default: Count=1 / Index=0 / ListType=0) | §9 below |
| 6 | `Chance` | optional (default: 100) | §10 below |

**Optional sections may be left blank or written as `NONE`.** Both forms below are valid and identical:

```ini
Item = MyItem|||||5
Item = MyItem|NONE|NONE|NONE|NONE|5
```

Reading pipe positions: count the pipes to know which field you're in. `Item = MyItem|||||5` has five
pipes, so `5` lands in position 5 (`CountOrPackageIndex`); Chance is omitted. `Form = 0x12345||||||60`
has six pipes, so `60` lands in position 6 (`Chance`).

> A trailing field that you don't set can simply be omitted (drop its pipe) — you only need pipes up
> to the last field you actually use.

---

## 5. Input normalization (how SPID cleans your line before parsing) **[source]**

Each value is run through `sanitize()` before parsing, which defines several conveniences the article
doesn't mention. Knowing these prevents "why does my line still work / not work" confusion:

- **Whitespace around `|` and `,` is stripped.** `Form = 0x123 | A, B | NordRace` is normalized to
  `Form = 0x123|A,B|NordRace`. Spaces around separators are purely cosmetic.
  *(But spaces inside a name term are significant — `Whiterun Guard` is one term.)*
- **`" - "` (space-hyphen-space) is converted to `~`** when the value contains no `~` yet — so the
  exact string xEdit shows, `0001396B - Skyrim.esm`, is **directly pasteable** as a FormID. Only the
  first `" - "` is converted.
- **Bare hex with leading zeros gets a `0x` prefix added** (`00012345` → `0x12345`), and **leading
  zeros after `0x` are stripped** (`0x00012345` → `0x12345`). FormIDs are forgiving about zero-padding.

---

## 6. Referencing a form — `FormOrEditorID`

The required first field identifies the form to distribute. Two ways to write it:

- **EditorID** — the text identifier from the Creation Kit / xEdit (e.g. `ElvenMace`, `ImperialBow`,
  `NordRace`). **Preferred** — it's stable across mod merging, ESL conversion, and FormID compaction.
- **FormID** — hex with the source plugin as a **tilde suffix**: `0x12345~MyPlugin.esp`.
  *(Divergence to remember: SPID/KID/CID use suffix-tilde `0x123~Plugin.esp`; SkyPatcher uses
  prefix-pipe `Plugin.esp|0x123`. See the routing skill.)*

The form must be one of the supported types in `form-types.md`. The same EditorID/FormID syntax is used
not just here but anywhere a form is named — including inside Form Filters (§ `filters.md`).

---

## 7. How filters combine

The 4 filter sections (String, Form, Level, Trait — all in `filters.md`) share one combination model:

- **Within a section: OR (additive).** `A,B` matches an NPC who has A *or* B.
- **Between sections: AND (multiplicative).** Every section present on the line must pass.
- **Exclusions (`-X`) are always AND**, regardless of positive matches. So `A,B,-X` reads
  `(A OR B) AND NOT X`.

Worked: `Form = 0x12345|A,B|0x12,0x34` means "give `0x12345` when the NPC has (A OR B) AND (0x12 OR
0x34)."

To target *unions* of groups, use **multiple lines** for the same form (a TIP from the article):

```ini
Keyword = FemaleBandits||BanditFaction||F     ; Female AND in BanditFaction
; vs. — the union "Females OR Bandits":
Keyword = FemaleOrBandits||BanditFaction       ; everyone in BanditFaction
Keyword = FemaleOrBandits||||F                 ; plus every Female in the game
```

A line with **no filters at all** matches *every* NPC of the game.

---

## 8. Type inferring — the generic `Form` keyword

Instead of naming the exact form type, you may write **`Form`** and let SPID infer the type from the
distributable form you give it:

```ini
Form = SteelSword      ; inferred as Item
Form = DefaultOutfit   ; inferred as Outfit
```

**Caveat:** `SleepOutfit` and `Skin` can **never** be inferred — they share the same underlying
records as `Outfit` (OTFT) and `Item` (ARMO) respectively, so the latter is always detected first.
To distribute a SleepOutfit or Skin you must name the type explicitly. (Full type/signature detail in
`form-types.md`.)

---

## 9. `CountOrPackageIndex` (position 5) — meaning depends on the form type

This single numeric field is interpreted three different ways:

- **Item → Count.** Number of items to add. Default `1` if blank/absent. A **range** `min-max` lets
  SPID pick a random count:
  ```ini
  Form = ImperialBow          ; 1 bow (default)
  Form = IronSword|||||3      ; 3 swords
  Item = SteelArrow|||||10-20 ; between 10 and 20 arrows
  ```
- **Package → Index.** Zero-based insertion index in the NPC's package list. Default `0` (first):
  ```ini
  Package = Patrol|||||1   ; insert Patrol as the 2nd package (index 1)
  Package = Travel         ; index 0 (default) — first package
  ```
- **FormList → Package List Type.** Which package list to overwrite, `0`–`4`. Default `0`. The five
  types (DPLT/SPOR/OCOR/GWOR/ECOR) are tabulated in `value-tables.md`:
  ```ini
  Package = DefaultPackageListLinkedPatrol|||||0   ; Default Package List
  Package = DefaultPackageListLinkedPatrol         ; same — type 0 is default
  ```

---

## 10. `Chance` (position 6)

A percentage, **0–100 decimal**, default **100** (guaranteed) when absent. No lower limit on how small
it can be (`0.01` is fine).

```ini
Form = 0x12345||||||60     ; 60% chance
Form = 0x12345||||||0.01   ; 0.01% chance
```

**Deterministic chance — append `!`.** By default the roll is random every time the NPC is processed,
so the same NPC can get different results across game restarts. Appending `!` (e.g. `50!`) makes the
result **consistent for the same NPC + same savegame (player character) across all sessions**:

```ini
Form = 0x12345||||||50!     ; deterministic 50% — each NPC always or never gets it, consistently
Form = 0x12345||||||33.3!   ; decimals work too
```

What changes a deterministic outcome (re-rolls it): the target NPC, the player character (new
game / different character), and **any edit to the source entry** — changing a filter, the chance, or
even formatting (`Form = 0x12345|NONE|||||50!` rolls differently from `Form = 0x12345||||||50!`).
**[source]** confirms `!` sets the parser's `deterministic` flag.

---

## 11. Note on templated NPCs

Templated NPCs inherit attributes from a *base template* NPC. **Filters always evaluate the final,
resolved NPC** (after merging template attributes).

String Filters and Form Filters can additionally target specific NPCs by EditorID/FormID, *including*
the templates they derive from — but when a template hierarchy is several levels deep, only certain
levels are reachable:

- **Not-leveled NPCs:** the **Final NPC** and its **immediate Base template** are reachable; anything
  further up the chain is **unreachable**.
  ```
  DLC2SV01DragonPriestBoss [NPC_:0401CAD5] => EncDragonPriestFire [NPC_:0002025A] => EncDragonPriest [NPC_:00023A93]
  Final NPC (reachable)                        Base template (reachable)             Further template (UNREACHABLE)
  ```
- **Leveled NPCs:** the **Original NPC**, the **Base leveled NPC** (closest LVLN), and the **Base
  template** picked from it are reachable; the dynamically-created final NPC and any *nested* leveled
  template are unreachable.
  ```
  [NPC_:FF000D45] => LvlDraugrAmbushMelee2HMale [NPC_:0004A04E] => LCharDraugrMelee2HMale [LVLN:0001E772]
  Dynamic final (UNREACHABLE)   Original NPC (reachable)           Base leveled NPC (reachable)
  ```

Practical takeaway: target the **EditorID of the base template** to hit "all descendants of this
template" — that's the common, robust pattern for families of NPCs.
