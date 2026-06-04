# KID Grammar — Core

The shared spine of the `kid-authoring` corpus: what KID does, how it discovers and parses configs,
the shape of a distribution line, how the keyword is named, and the two tail fields. The domain
vocabularies live beside this file:

- **`types.md`** — the 19 item types you can add keywords to (the `type` section).
- **`filters.md`** — the String / Form filters that choose *which items* get the keyword (section 3).
- **`traits.md`** — the per-type trait filters that narrow further (section 4).
- **`value-tables.md`** — the flat enums (archetypes, spell types, schools, soul/furniture/bench
  sizes, body slots, delivery/casting values) the prose files reference.

> **Source of truth.** KID has no single "complete reference" article. This corpus is **dual-source**:
> the **MIT GitHub source** (`powerof3/Keyword-Item-Distributor`, v3.5.0.rc1) is authoritative for
> grammar; the **Nexus #55728 description** supplies the user-facing value tables and worked examples.
> Provenance markers: **[source]** = from the C++ parser/headers only · **[desc]** = stated in the
> Nexus description only · **[lib]** = confirmed from CommonLibSSE (`powerof3/CommonLibSSE@dev`), the
> library KID compiles against · unmarked = confirmed by both.

---

## 1. What KID is

KID (**Keyword Item Distributor**, by powerofthree) is an SKSE utility plugin that **adds keywords to
items** — weapons, armor, ammo, magic effects, potions, scrolls, books, soul gems, spells,
enchantments, and more (19 types, `types.md`) — **at game startup**, driven by plain-text `_KID.ini`
config files.

It writes nothing to plugins and nothing to the save. KID re-applies its keywords from scratch on
every launch, directly onto the in-memory record, so a KID mod is trace-free to add or remove.

KID's target is **items** (object/base records). That's its lane in the distributor family:

- keyword/form to an **item record** → **KID** (this skill)
- spell/perk/item/keyword to an **NPC** → **SPID**
- item into a **container** → **CID**
- editing a record's **own fields** (damage, value, an NPC's stats) → **SkyPatcher**

KID and SPID share an author and many idioms (the `~` FormID form, the `+`/`-`/`*` filter modifiers),
but **KID adds keywords to items; SPID distributes forms to NPCs** — including SPID's *own* keyword
distribution, which targets NPCs, not items. The deciding question is always **what receives the
keyword**.

---

## 2. File discovery & parsing

- KID scans Skyrim's `Data\` folder for **every `.ini` whose name contains the substring `_KID`**
  (e.g. `MyMod_KID.ini`, `AllGearKID.ini`). **[source]** (`get_configs(R"(Data\)", "_KID")`)
- Files are loaded in **alphabetical order, A → Z**, each read **top to bottom**. **[source]**
- Files live **flat in `Data\`** — *not* under `SKSE\Plugins\` (where SPID and SkyPatcher put theirs).
  Shipped inside a mod managed by a mod manager, but resolved from `Data\` at runtime.
- Parsed through **CSimpleIniA** (`SetUnicode`, `SetMultiKey`). **[source]** Consequences:
  - **Comments:** standard INI line comments with **`;`** (CSimpleIniA's default).
  - **Multi-key:** the same key (`Keyword`) may appear on many lines — each line is its own entry.
  - Entries are read from the **unnamed root section** — KID INIs have **no `[Section]` headers**;
    every line sits at the top level.

---

## 3. When distribution happens

**At startup / data load** — KID applies keywords once, early, to the in-memory item records. This is
**not** lazy-per-cell like SPID; by the time you are in-game the keywords are already on the records.

Diagnostics: KID writes **`po3_KeywordItemDistributor.log`** to
`Documents\My Games\Skyrim Special Edition\SKSE\` — check it to see what matched and what failed.
A malformed entry is logged (`Failed to parse entry [Keyword = …]`) and skipped, **not** errored to
the user — so a wrong token silently distributes nothing. This is why the skill never invents syntax.

---

## 4. The distribution line

Every entry is one line of the form:

```
Keyword = formID~esp(OR)keywordEditorID | type | strings,formIDs(OR)editorIDs | traits | chance
```

The value (right of `=`) is **5 sections separated by 4 pipes** (`|`). Only section 0 (the keyword) is
required; the rest are optional and positional. **[source]** (`split("|")`, indices in `LookupConfigs.h`)

| Pos | Section | Required? | Covered in |
|----:|---|---|---|
| 0 | **Keyword** — the keyword to add (`formID~esp` or EditorID) | **yes** | §5 below |
| 1 | **Type** — which item type to add it to | optional* | `types.md` |
| 2 | **Filters** — strings / FormIDs / EditorIDs that pick items | optional | `filters.md` |
| 3 | **Traits** — type-specific narrowing | optional | `traits.md` |
| 4 | **Chance** — distribution probability | optional (default 100) | §6 below |

\* *Type is technically optional to the parser, but with no type KID has nothing to match against —
in practice every useful line names a type.* An unused middle section is written blank or `NONE`
(e.g. `…|Potion|NONE|P` leaves filters empty). A trailing unused section can simply be dropped — you
only need pipes up to the last section you use.

**Count the pipes** to know which section a value lands in. `Keyword = MyKwd|Book|NONE|S,20` has four
pipes → `S,20` is the *traits* section (3). `Keyword = MyKwd|Armor|||50` also has four pipes → `50` is
*chance* (4), with empty filters and traits.

> **One keyword, many item types:** a single line targets **one** type. To add the same keyword to
> several types, write **one line per type** (`[desc]` "Distribute the same keyword multiple times to
> add it to different types of items").

---

## 5. The keyword — section 0 (required)

Names the keyword to add. Two forms, same as anywhere KID/SPID name a form:

- **EditorID** — the keyword's editor name (e.g. `WeapMaterialDwarven`, `MagicDamageFire`).
- **FormID** — hex with the source plugin as a **tilde suffix**: `0x12345~MyMod.esp`. The `esp` is
  omitted for vanilla Skyrim/DLC records. Leading zeros are stripped (`0x12345`, not `0x00012345`).

**Dynamic keyword creation [desc]:** if the EditorID you name **doesn't resolve** to an existing
keyword, KID **creates the keyword at runtime** for you. So you can invent a tag (`MyCustomTag`) and
KID makes it — verify in-game with SKSE's `GetKeywordString`. (This is why a typo'd keyword name never
errors: KID just creates a new, empty keyword instead.)

---

## 6. Chance — section 4

A percentage, **0.0 – 100.0**, default **100** when blank, absent, or `NONE`. **[source]** (`chance{100}`)

```ini
Keyword = MyKwd|Weapon|*Iron|E|50     ; 50% of enchanted iron weapons
Keyword = MyKwd|Weapon|*Iron|E        ; 100% (chance omitted)
```

KID's chance is **fixed per item, not re-rolled across sessions** [desc] — an item either gets the
keyword or doesn't, consistently. (KID distributes to item *records*, of which there is one each, so
there is no per-instance variation to re-roll — unlike SPID's per-NPC chance.)

---

## 7. `ExclusiveGroup` — the second key **[source]**

Besides `Keyword =`, a KID INI may define **mutually-exclusive keyword groups**. The Nexus description
does **not** document this; it is source-confirmed in v3.5.0 (`ExclusiveGroups.cpp`).

```
ExclusiveGroup = GroupName | keyword1, keyword2, -excludedKeyword
```

- **Section 0** — the group's name (any label).
- **Section 1** — a comma-separated list of **keyword** Form filters (FormIDs / EditorIDs). A `-`
  prefix **removes** a keyword from the group; everything else **adds**. Only MATCH and exclusion (`-`)
  are meaningful here — **no `+` (ALL) and no `*` (wildcard).** Each entry must resolve to a **keyword**
  (KYWD); non-keywords are logged and ignored.
- Must have a name **and at least one form filter**, or the line is ignored with a warning.

**Effect:** keywords in the same group are treated as mutually exclusive during distribution — an item
that already carries one keyword from the group won't also receive another from it. Use it to keep,
e.g., material or rarity tags from doubling up on the same item.

---

## 8. Input forms & normalization

- **FormID = tilde-suffix** `0x12345~Plugin.esp` (KID/SPID/CID style). *SkyPatcher diverges —
  `Plugin.esp|0x12345` prefix-pipe; the two are not interchangeable.*
- **Comma** separates entries within the filters section (and within `ExclusiveGroup`'s list).
- **Leading zeros** in a FormID are forgiven (`0x00012345` ≡ `0x12345`).
- **VR quirk [source]:** under Skyrim VR, KID rewrites Dawnguard/Dragonborn FormIDs
  (`0x02…`→`~Dawnguard.esm`, `0x04…`→`~Dragonborn.esm`) because VR doesn't load masters in order. SE/AE
  users don't need to think about it; it only matters if you hand-write `0x02…`/`0x04…` IDs for VR.

---

## 9. Cross-tool routing (quick)

| You want to… | Tool |
|---|---|
| add a keyword to **items** (weapon/armor/potion/…) | **KID** ← this skill |
| give a spell/perk/item/keyword to **NPCs** | SPID |
| put an item into a **container** | CID |
| change a record's **own fields** (damage, value, NPC stats) | SkyPatcher |

When unsure, ask **what receives the keyword**: an item record → KID; an NPC → SPID.
