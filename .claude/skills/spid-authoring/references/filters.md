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

**Sections.** 1 String Filters (position 1) · 2 Form Filters (position 2) · 3 Level Filters
(position 3), which owns what a Level Filter reaches · 4 Trait Filters (position 4).

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
| Actor | `[ACHR]` | FormID / EDID *(added in 7.3 — **absent below 7.3**: on an older install the whole line is skipped silently)* |
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

### What a Level Filter reaches — and what it does not **[source]**

This section owns the question; the skill body points here rather than restating it.

**A Level Filter narrows by the NPC's own level. It does not narrow the line to auto-levelled NPCs.**
On the ordinary on-load path SPID runs the **whole** entry list — level-filtered entries included —
against every non-player, non-deleted NPC that loads, and compares the range to that NPC's own
level. So a line reading `|||20` reaches every matching NPC at level 20 or above, fixed-level actors
included.

**The separate pass is a re-distribution, not the distribution.** SPID keeps a second, smaller list
holding only the level-filtered entries, and replays it for NPCs whose level is a **PC level
multiplier** — on player level-up, and on game load — so a scaling actor gains an entry when it grows
into the range and loses it when it falls out. That pass is restricted to PC-level-mult NPCs; it
adds re-evaluation to an already-distributed population, it does not shrink the population the line
reached in the first place.

**Why it matters in numbers.** On a heavily overhauled order the two populations are nowhere near
each other — a census of one live load order (`housecarl_records`, `counts_only=true`) found 17 of
24,782 bandit-faction NPCs on a PC level multiplier, against 4,040 matching the line's own level
bound; that same order carries 963 PC-level-mult NPC records in total, across every faction.
Reading the filter as "auto-levelled NPCs only" understates the reach by a factor of hundreds there.
Those counts read each record's own local data, so a templated NPC whose stats are inherited is not
counted although SPID matches the resolved actor (1,073 of the 24,782 inherit stats) — a census is a
floor, and the source below, not the ratio, is what settles the rule. Count both with
`housecarl_records` (`counts_only=true`) before quoting a reach.

> **Source.** `powerof3/Spell-Perk-Item-Distributor`: `DistributeManager.cpp`
> `detail::distribute_on_load` calls `Distribute(npcData, false)` for every NPC passing
> `should_process_NPC` (`!IsPlayer() && !IsDeleted()`); `FormData.h`
> `Distributables<Form>::GetForms(bool a_onlyLevelEntries)` returns the full `forms` list on
> `false` and the `formsWithLevels` subset only on `true`; `LookupFilters.cpp`
> `Data::passed_level_filters` tests the range against `NPC::Data::GetLevel()`;
> `DistributePCLevelMult.cpp` gates its hooks on `npc->HasPCLevelMult()`. Read on the `master`
> branch and re-checked at tag `v6.8.5.rc9` — the logic is identical at both, and the repository
> publishes no 7.0.0 or 7.3.0 tag to read, so this holds across the 7.x window the corpus documents.

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
