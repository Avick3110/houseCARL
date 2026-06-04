# SPID Filters — choosing which NPCs receive the form

Four optional filter sections narrow a distribution to a group of NPCs. They occupy pipe positions
1–4 of the line (grammar-core §4):

```
FormOrEditorID | StringFilters | FormFilters | LevelFilters | TraitFilters | CountOrPackageIndex | Chance
position 0       position 1      position 2     position 3     position 4 …
```

**Combination model (all four share it — grammar-core §7):** OR within a section, AND between
sections, and exclusions (`-X`) are always AND. Letter codes and skill indices are tabulated in
`value-tables.md`.

> Source: the article's String / Form / Level / Trait Filter sections (SPID 7.3.0), plus **[source]**
> cross-checks where noted.

---

## 1. String Filters (position 1)

A comma-separated list of **textual** expressions.

```
Form = 0x12345|StringExpression1,StringExpression2,...
```

**Matches an NPC by any of:**
- NPC's **Name** (e.g. `Balgruuf`)
- NPC's **EditorID** (e.g. `BalgruufTheGreater`)
- NPC **Template's EditorID** (targets all descendants of a template — grammar-core §11)
- NPC's **Keywords** (e.g. `ActorTypeNPC`) — *including keywords distributed by SPID*
- NPC **Race's Keywords** (e.g. `ActorTypeAnimal`)

**Modifiers** (only **one** modifier per expression):

| Modifier | Place | Effect |
|---|---|---|
| `-` | front of a term | **Exclude** — match NPCs that do NOT have the exact term. Always AND. |
| `*` | front of a term | **Partial match** — substring. `*Guard` matches "Whiterun Guard", "Falkreath Guard"… and also "Guardian", "Bodyguard". |
| `+` | between terms | **Combine (AND)** — match NPCs that have ALL the exact terms. |

```ini
; OR: Whiterun Guards (one term, with a space — spaces inside a term are significant)
Form = 0x12345|Whiterun Guard
; OR across two keywords
Form = 0x12345|ActorTypeNPC,ActorTypeDragon
; exclude one NPC by name (note "Balgruuf Junior" would still match — exact term only)
Form = 0x12345|-Balgruuf
; (A OR B) AND NOT X  — exclusions are always AND
Form = 0x12345|ActorTypeNPC,ActorTypeDragon,-Nazeem
; partial match — every "...Guard..."
Form = 0x12345|*Guard
; combine — must be ALL of these at once
Form = 0x12345|ActorTypeNPC+Bandit+ActorTypeGhost
```

**Invalid** (more than one modifier in a single expression):
```ini
Form = 0x12345|-*Guard
Form = 0x12345|-Guard+ActorTypeNPC
Form = 0x12345|ActorTypeNPC-Guard
Form = 0x12345|*Guard+ActorTypeNPC
```

---

## 2. Form Filters (position 2)

A comma-separated list of **FormOrEditorIDs** that match an NPC by its form-valued properties.

```
Form = 0x12345||FormExpression1,FormExpression2,...
```

(Note the **two** leading pipes — position 1 String is empty, position 2 Form follows.)

**Filterable forms** (the NPC property each checks, with its xEdit field):

| Form type | Sig | NPC's record (xEdit) |
|---|---|---|
| Combat Style | `[CSTY]` | ZNAM – Combat Style |
| Class | `[CLAS]` | CNAM – Class |
| Faction | `[FACT]` | Factions |
| Race | `[RACE]` | RNAM – Race |
| Outfit | `[OTFT]` | DOFT – Default outfit |
| Perk | `[PERK]` | Perks |
| Specific NPC | `[NPC_]` | FormID / EDID |
| NPC's Template | `[NPC_]` | FormID / EDID (targets descendants — grammar-core §11) |
| Actor | `[ACHR]` | FormID / EDID *(added in 7.3)* |
| Voice Type | `[VTYP]` | VTCK – Voice |
| Known Spell | `[SPEL]` | Actor Effects |
| Skin | `[ARMO]` | WNAM – Worn Armor |
| Editor Location | `[LCTN]` | XLCN – Persistent Location *(where the NPC is placed in the **editor**, not where it currently is)* |
| FormList | `[FLST]` | Recursively matches any form in the list — may nest other FormLists |

Additionally, a **plain plugin name** matches all NPCs *defined in* that plugin:

```ini
Form = 0x12345||CoolNPCs.esp   ; every NPC from CoolNPCs.esp
```

**Modifiers** (only **one** per expression):

| Modifier | Place | Effect |
|---|---|---|
| `-` | front of a form | **Exclude** (always AND). |
| `+` | between forms | **Combine (AND)** — NPC must have ALL the forms. |

*(Form Filters have no `*` partial modifier — that's String-only.)*

```ini
; all Nords
Form = 0x12345||NordRace
; located in Whiterun OR reports crime in Whiterun Hold
Form = 0x12345||WhiterunLocation,CrimeFactionWhiterun
; everyone except Nords
Form = 0x12345||-NordRace
; (Nord OR Imperial) AND NOT in BanditFaction
Form = 0x12345||NordRace,ImperialRace,-BanditFaction
; simultaneously Nord AND Whiterun-crime AND knows Stoneflesh
Form = 0x12345||NordRace+CrimeFactionWhiterun+StonefleshLeftHand
```

**Invalid** (mixed/duplicate modifiers in one expression):
```ini
Form = 0x12345|-NordRace+WhiterunLocation
Form = 0x12345|CrimeFactionWhiterun-NordRace
```

---

## 3. Level Filters (position 3)

A comma-separated list of numeric range expressions for **level** and **skills**.

```
Form = 0x12345|||LevelExpression,SkillExpression1,SkillExpression2,...
```

(Three leading pipes — positions 1 and 2 empty.)

| Value | Expression | Notes |
|---|---|---|
| **Level** | `min/max` | **Only ONE** Level Expression allowed — if you give several, only the **last** is kept. |
| **Skill Level** | `skillIndex(min/max)` | `skillIndex` is `0`–`17` (table in `value-tables.md`). |
| **Skill Weight** | `wskillIndex(min/max)` | Same as Skill but prefixed with `w` — filters on how actively the NPC levels that skill. **[source]** |

**Ranges syntax** (applies to level and skill ranges):
- **Closed** `min/max` — between min and max, inclusive.
- **Half-open** `min` or `min/` — from min upward (to infinity).
- **Exact** `value/value` — exactly that value.

```ini
; at least level 5
Form = 0x12345|||5
; exactly 50 in Destruction (skill index 14)
Form = 0x12345|||14(50/50)
; levels Two-Handed (index 1) slightly more actively than other skills (weight filter)
Form = 0x12345|||w1(2/3)
; only the LAST level expression survives — here 5/10 is discarded, 7/12 kept
Form = 0x12345|||5/10,7/12
```

**Leveled Distribution:** the moment a line defines a Level Filter it joins the *Leveled Distribution*
pass — SPID checks the level/skills of loaded **auto-leveled** NPCs against the filter (unless another
filter discards the NPC or the chance roll fails). This is a distinct distribution path from regular
(non-level-filtered) entries.

---

## 4. Trait Filters (position 4)

A **single** expression (not a comma list) filtering by NPC traits.

```
Form = 0x12345||||TraitExpression
```

(Four leading pipes.)

**The 8 traits** (single-letter codes — full table incl. negations in `value-tables.md`): **[source]**

| Letter | Trait |
|---|---|
| `F` | Female |
| `M` | Male |
| `U` | Unique |
| `S` | Summonable |
| `C` | Child |
| `L` | Leveled (Is PC Level Mult) |
| `T` | **Player's Teammate** |
| `D` | Dead (died, or Start Dead) |

**Modifiers:**

| Modifier | Place | Effect |
|---|---|---|
| `-` | front of a trait | **Exclude** — NPC must NOT have the trait. |
| `/` | between traits | **Combine (AND)** — NPC must have ALL the traits. |

**Traits is the only filter that allows MIXING modifiers in one expression.** (String/Form forbid it.)

```ini
; females
Form = 0x12345||||F
; NOT unique
Form = 0x12345||||-U
; non-unique AND male AND not-a-child (adult)
Form = 0x12345||||-U/M/-C
; mixing freely: female AND not-unique AND leveled AND not-summonable
Form = 0x12345||||F/-U/L/-S
```

**Sex aliasing [source]:** because sex is binary in the parser, **`-F` is an alias for `M`** and
**`-M` is an alias for `F`** — "not female" is stored as Male, not as a generic exclusion. Usually you
just write `M`/`F` directly; this only matters if you're reasoning about `-F`/`-M` inside a mixed
expression.
