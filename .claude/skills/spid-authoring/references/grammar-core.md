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

**Sections.** 1 What SPID is · 2 File discovery & load order · 3 When distribution happens, and in
what order · 4 The distribution line · 5 Input normalization · 6 Referencing a form
(`FormOrEditorID`) · 7 How filters combine · 8 Type inferring (`Form`) · 9 `CountOrPackageIndex` ·
10 `Chance` · 11 Note on templated NPCs.

---

## 1. What SPID is

SPID (**Spell Perk Item Distributor**, by powerofthree) is an SKSE plugin that distributes forms —
spells, perks, items, shouts, packages, keywords, outfits, factions, skins — **to NPCs at runtime**,
driven by plain-text `_DISTR.ini` config files.

It writes nothing to plugins and nothing to the save. SPID redistributes everything from scratch on
every game launch — think of it as a *virtual plugin that applies last in the load order*. That makes
any SPID-based mod safe to install or uninstall at any time, leaving no trace.

Because distribution targets NPCs (not records on disk), SPID is the tool for "give X to this group of
NPCs," not for editing a record's own fields — that's SkyPatcher's job, and its own skill
(`skypatcher-authoring`); a keyword onto item records is `kid-authoring`.

---

## 2. File discovery & load order

<!-- generated:spid.discovery -->
SPID reads every file directly in `Data/` whose name holds `_DISTR` anywhere (case-sensitive:
`_distr` is not read, and `Foo_DISTR_old.ini` is) and whose extension is exactly `.ini`
(case-sensitive: `.INI` is not read). A file in a subfolder is never read, nor one that is only
inside a BSA. Files are read in the byte order of their names, not alphabetically: upper-case
letters sort before lower-case ones and `_` sits between them, so `NPC_Trap_DISTR.ini` comes before
`Naked_DISTR.ini`. These paragraphs model SPID 7.3.3; 7.3.0 reads its files the same.
<!-- end generated:spid.discovery -->

The order of the lines inside a file, and across files within one type, is in §3.

There are no per-type subfolders (unlike SkyPatcher) — all `*_DISTR.ini` live flat in `Data/`
(commonly shipped inside a mod managed by a mod manager, but resolved from `Data/` at runtime).

**The scan does not descend into subfolders. [source]** SPID reads its configs with
`distribution::get_configs(R"(Data\)", "_DISTR"sv)` (`Distribution::INI::GetConfigs`,
`SPID/src/LookupConfigs.cpp`, SPID 7.3.0, commit `31e76d3`). That helper is CLibUtil's
`clib_util::distribution::get_configs` (`include/CLIBUtil/distribution.hpp`), and it walks a plain
`std::filesystem::directory_iterator` over the one folder it is given — not
`recursive_directory_iterator` — keeping entries whose extension is `.ini` and whose path contains
`_DISTR`, then sorting them. So only `Data/` itself is read: a `_DISTR.ini` one folder
down is never found, and never logged as skipped either, because the scan never sees it. **Place the
file flat in `Data/`**, and when auditing someone else's mod, report a `_DISTR.ini` in a subfolder as
not read at all.

Comments, sections and the shape of a key: §4. SPID reads configs through the **CSimpleIniA**
library; the `=` split and the comment rule are CSimpleIniA's, not SPID's own code. **[source]**

---

## 3. When distribution happens, and in what order

**When:** distribution is **lazy** — it runs when NPCs are loaded into the world (typically on cell
load), not all at once at game start. The configs themselves are read **once per game launch**, so an
INI edited while the game is running takes effect only after a restart. **[source]**
(`SPID/src/main.cpp` — configs are read at `kPostLoad` and forms looked up at `kDataLoaded`.)

**Every launch, from scratch.** SPID "distributes everything from scratch each time you launch the
game" and "doesn't have effect on either loaded plugins nor on save" — article 6617, § General
Distribution Info, quoted verbatim. Nothing it distributes is written into the save, so a rule added
to a `_DISTR.ini` after a save was made applies to that save's actors on the next launch, when they
next load in. The "already handled this NPC" marker is a runtime `SPID_Processed` keyword created
fresh at each launch, so it carries nothing across sessions. **[source]**
(`SPID/src/DistributeManager.cpp` — `Setup()` creates the keyword via `IFormFactory`;
`distribute_on_load` skips an NPC that already has it; declared in `DistributeManager.h`.) The
practical limit is the once-per-launch config read above: no restart, no new rule.

**Who — not the player.** The ordinary on-load path is gated on `detail::should_process_NPC`, which
is `!a_npc->IsPlayer() && !a_npc->IsDeleted()`, so a normal distribution never reaches the player
character. The other paths into `Distribute()` exclude the player their own way rather than through
that function: the two PC-level-mult redistribution hooks run only for an NPC that already carries
`SPID_Processed`, and only the on-load path ever adds it; on-death distribution guards its death-event
handler with `!IsPlayerRef() && !IsSummoned()`. (Its other entry, the `ShouldBackgroundClone` hook,
carries no explicit player check — the player is not background-cloned, but that is a game fact, not a
SPID guard.) **[source]** (`SPID/src/DistributeManager.cpp`, `DistributePCLevelMult.cpp`,
`DeathDistribution.cpp` — all `Distribute()` call sites read.)

Practically: don't assert a SPID distribution on `Game.GetPlayer()`; sample NPCs.

**Order (per-type passes):**

<!-- generated:spid.order -->
Within one file SPID takes the lines grouped by key, the keys in alphabetical order whatever their
case, and each key's lines in file order, so a file's `FinalOutfit` lines come before its `Outfit`
lines wherever they sit. Each line joins its type's list: the files in their read order, each with
its lines in that order. `Form` lines join after every typed line of every file. Types are handed
out in this order: Keywords → Factions → Perks → Spells → Leveled spells → Shouts → Packages → Items
→ Skins → Sleep outfits, then Outfits in a pass of their own. Keyword lines are re-sorted so a
keyword another keyword line's string filter names is handed out first. For `Skin`, `SleepOutfit`
and `Outfit` the first line whose filters pass an NPC wins it and the rest are not tried; for every
other type each passing line gives, and `Item` counts add up.
<!-- end generated:spid.order -->

You can freely use keywords as requirements for other keywords without hand-ordering them.

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

**How SPID reads a line, and the other line kinds:**

<!-- generated:spid.lines -->
Only the lines before the first `[section]` header are read. A line whose first non-space character
is `;` or `#` is a comment. There is no comment after a value: everything after `=` to the end of
the line is the value, so `Spell = X ; note` names `X ; note`. A key is `<Type>`, `Final<Type>`
(`Outfit` only; on another type SPID drops `Final`), `Death<Type>` (handed out when the NPC dies),
`GlobalLinked<Type>` or `Linked<Type>` (optionally with `Final` and `Death` after `Linked`), or
`ExclusiveGroup`. `<Type>` is one of `Keyword`, `Faction`, `Perk`, `Spell`, `LevSpell`, `Shout`,
`Package`, `Item`, `Skin`, `SleepOutfit`, `Outfit`, `Form`, spelt in exactly that case; any other
key is skipped. A regular or `Death` line is `Form | StringFilters | FormFilters | LevelFilters |
TraitFilters | CountOrIndex | Chance`; a `Linked` line is `Form | ParentForms | CountOrIndex |
Chance`; an `ExclusiveGroup` line is `GroupName | Forms`. A line with more `|` sections than that is
skipped. An empty section, or `NONE` in any case, is unset, but only as the whole section: `NONE` as
one item of a list is looked up as a name. In a filter section `,` separates the items (OR), `+`
joins the parts of one item (AND), a leading `-` excludes, and in a string filter a leading `*`
matches part of a name. An empty item is skipped with its whole line.
<!-- end generated:spid.lines -->

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
  **It is not stable against a winning override that renames the record**, which is ordinary on a
  heavily overhauled order (an overhaul renaming a vanilla perk is the common case). SPID resolves
  the EditorID it is given against the record the order actually carries, so a line naming the
  pre-overhaul EditorID resolves to nothing and distributes nothing, silently. Read the winner's
  EditorID before writing the name; where the winner has renamed the record, use the FormID.
- **FormID** — hex with the source plugin as a **tilde suffix**: `0x12345~MyPlugin.esp`.
  *(Divergence to remember: SPID/KID/CID use suffix-tilde `0x123~Plugin.esp`; SkyPatcher uses
  prefix-pipe `Plugin.esp|0x123` — `skypatcher-authoring`.)*

The form must be one of the supported types in `form-types.md`. The same EditorID/FormID syntax is used
not just here but anywhere a form is named — including inside Form Filters (§ `filters.md`).

**Every spelling SPID reads, in the order it tries them:**

<!-- generated:spid.names -->
In any form position a token holding `~` is a FormID with its plugin (`0x800~Plugin.esp`): the
number is added to the plugin's own load index as the game numbers it, so a load index written into
the number moves it to another plugin's record. Else a token holding `.es` is a plugin name; in the
form filters it selects every NPC that plugin defines. Else `0x` followed by hex digits is a runtime
FormID (`0x1CE14`, as the console prints it, so it depends on the load order). Anything else is an
EditorID, looked up among every record type; `12345` without `0x` is an EditorID too.
<!-- end generated:spid.names -->

**When a name or FormID finds nothing:**

<!-- generated:spid.missing -->
A value name no record holds: on a `Keyword` or `Form` line SPID creates a keyword of that name and
hands it out; on a `Spell` or `Package` line it creates the keyword and hands nothing out; on every
other type SPID drops the line, as it does for a FormID or a plugin that names nothing. In the form
filters a token that names nothing, or a record type SPID does not take as a filter, is skipped and
the line reaches more NPCs (or, in the `-` list, excludes fewer), unless every token of that list
fails: then the line is dropped. One `+` part that names nothing drops the line. A filter never
creates a keyword.
<!-- end generated:spid.missing -->

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
; Female AND in BanditFaction
Keyword = FemaleBandits||BanditFaction||F
; vs. — the union "Females OR Bandits":
; everyone in BanditFaction
Keyword = FemaleOrBandits||BanditFaction
; plus every Female in the game
Keyword = FemaleOrBandits||||F
```

A line with **no filters at all** matches *every* NPC of the game.

---

## 8. Type inferring — the generic `Form` keyword

Instead of naming the exact form type, you may write **`Form`** and let SPID infer the type from the
distributable form you give it:

```ini
; inferred as Item
Form = SteelSword
; inferred as Outfit
Form = DefaultOutfit
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
  ; 1 bow (default)
  Form = ImperialBow
  ; 3 swords
  Form = IronSword|||||3
  ; between 10 and 20 arrows
  Item = SteelArrow|||||10-20
  ```
- **Package → Index.** Zero-based insertion index in the NPC's package list. Default `0` (first):
  ```ini
  ; insert Patrol as the 2nd package (index 1)
  Package = Patrol|||||1
  ; index 0 (default) — first package
  Package = Travel
  ```
- **FormList → Package List Type.** Which package list to overwrite, `0`–`4`. Default `0`. The five
  types (DPLT/SPOR/OCOR/GWOR/ECOR) are tabulated in `value-tables.md`:
  ```ini
  ; Default Package List
  Package = DefaultPackageListLinkedPatrol|||||0
  ; same — type 0 is default
  Package = DefaultPackageListLinkedPatrol
  ```

---

## 10. `Chance` (position 6)

A percentage, **0–100 decimal**, default **100** (guaranteed) when absent. No lower limit on how small
it can be (`0.01` is fine).

```ini
; 60% chance
Form = 0x12345||||||60
; 0.01% chance
Form = 0x12345||||||0.01
```

**Deterministic chance — append `!`.** By default the roll is random every time the NPC is processed,
so the same NPC can get different results across game restarts. Appending `!` (e.g. `50!`) makes the
result **consistent for the same NPC + same savegame (player character) across all sessions**:

```ini
; deterministic 50% — each NPC always or never gets it, consistently
Form = 0x12345||||||50!
; decimals work too
Form = 0x12345||||||33.3!
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
