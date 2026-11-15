# KID Grammar — Core

The shared spine of the `kid-authoring` corpus: what KID does, how it discovers and parses configs,
the shape of a distribution line, how the keyword is named, and the two tail fields. The domain
vocabularies live beside this file:

- **`types.md`** — the 19 item types you can add keywords to (the `type` section).
- **`filters.md`** — the String / Form filters that choose *which items* get the keyword (section 2).
- **`traits.md`** — the per-type trait filters that narrow further (section 3).
- **`value-tables.md`** — the flat enums (archetypes, spell types, schools, soul/furniture/bench
  sizes, body slots, delivery/casting values) the prose files reference.

> **Source of truth.** KID has no single "complete reference" article. This corpus is **dual-source**:
> the **MIT GitHub source** (`powerof3/Keyword-Item-Distributor`, v4.1.0; v4.0.6 reads its files the
> same) is authoritative for grammar; the **Nexus #55728 description** supplies the user-facing value tables and worked examples.
> Provenance markers: **[source]** = from the C++ parser/headers only · **[desc]** = stated in the
> Nexus description only · **[lib]** = confirmed from CommonLibSSE (`powerof3/CommonLibSSE@dev`), the
> library KID compiles against · unmarked = confirmed by both.

**Sections in this file:** 1. What KID is · 2. File discovery & parsing · 3. When distribution
happens · 4. The distribution line · 5. The keyword (section 0) · 6. Chance (section 4) ·
7. `ExclusiveGroup` · 8. Input forms & normalization · 9. Cross-tool routing.

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
- spell/perk/item/keyword to **NPCs** → **SPID**, best by group (faction, race, level, trait); for one
  named NPC **SkyPatcher** is usually the better fit, though a single-NPC SPID line is valid
- item into a **container** → **SkyPatcher** (its container patcher — `filterByContainers` with
  `addToContainers`)
- editing a record's **own fields** (damage, value, an NPC's stats) → **SkyPatcher**

KID and SPID share an author and many idioms (the `~` FormID form, the `+`/`-`/`*` filter modifiers),
but **KID adds keywords to items; SPID distributes forms to NPCs** — including SPID's *own* keyword
distribution, which targets NPCs, not items. The deciding question is always **what receives the
keyword**.

---

## 2. File discovery & parsing

<!-- generated:kid.discovery -->
KID reads every file directly in `Data/` whose name holds `_KID` anywhere (case-sensitive: `_kid` is
not read, nor `AllGearKID.ini`, which has no `_KID`) and whose extension is exactly `.ini`
(case-sensitive: `.INI` is not read). A file in a subfolder is never read, nor one that is only
inside a BSA. Files are read in the byte order of their names, not alphabetically: upper-case
letters sort before lower-case ones and `_` sits between them, so `ZZZ_KID.ini` comes before
`aaa_KID.ini`. These paragraphs model KID 4.1.0; 4.0.6 reads its files the same.
<!-- end generated:kid.discovery -->

Files live **flat in `Data\`** — *not* under `SKSE\Plugins\` (SkyPatcher puts theirs in
`SKSE\Plugins\SkyPatcher\<type folder>\`). Shipped inside a mod managed by a mod manager, but
resolved from `Data\` at runtime. The same key (`Keyword`) may appear on many lines — each line is
its own entry. **[source]**

<!-- generated:kid.lines -->
Only the unnamed section is read: the lines before the first named `[section]` header, and any after
an empty `[]` header, which returns to it. A line whose first non-space character is `;` or `#` is a
comment. There is no comment after a value: everything after `=` to the end of the line is the
value, so a `;` note after the last section becomes part of it. The key `ExclusiveGroup`, spelt in
exactly that case, makes a group line, `GroupName|Members`; any other key (`Keyword`, `keyword`, any
word) makes a keyword line, `Keyword|Type|Filters|Traits|Chance`. The sections are split on `|` and
not trimmed, so ` Armor` with a space is not a type. The type is one of `Armor`, `Weapon`, `Ammo`,
`Magic Effect`, `Potion`, `Scroll`, `Location`, `Ingredient`, `Book`, `Misc Item`, `Key`, `Soul
Gem`, `Spell`, `Activator`, `Flora`, `Furniture`, `Race`, `Talking Activator`, `Enchantment`, spelt
exactly so; a line with any other type, or none, is dropped without a word in the log. A section
after the fifth is ignored. An empty section, or `NONE` in any case, is unset. In the filters `,`
separates the entries, each trimmed, and an entry `NONE` is skipped. An entry holding `+` is a group
whose parts must all match (AND), the groups of a line are alternatives (OR), and the entries
without `+` form one more list. A leading `-` on an entry or on a group's part excludes, and after
it a leading `*` makes the term a partial string. Traits are read on `Armor`, `Weapon`, `Ammo`,
`Magic Effect`, `Potion`, `Scroll`, `Ingredient`, `Book`, `Soul Gem`, `Spell`, `Furniture` and
`Enchantment` lines; on `Location`, `Misc Item`, `Key`, `Activator`, `Flora`, `Race` and `Talking
Activator` lines they are ignored. Each type reads its traits its own way (`traits.md`): a trait no
rule of the type takes is ignored, which widens the line, but some types match a trait by what it
holds (`R(`, `D(`, `W(`, digits only) or read any other trait as a soul gem's capacity, so a
misspelt trait can read as a different one and narrow the line (`XR(40)` on a magic effect line is a
resistance filter). Chance is a number from 0 to 100, 100 when unset; a chance that does not start
with a number fails the line, and KID logs `Failed to parse entry`. In a group line the members are
split on `,` and not trimmed, and a leading `-` takes a keyword out of the group.
<!-- end generated:kid.lines -->

---

## 3. When distribution happens

**At startup / data load** — KID applies keywords once, early, to the in-memory item records. This is
**not** lazy-per-cell like SPID; by the time you are in-game the keywords are already on the records.

<!-- generated:kid.order -->
Within one file KID takes the lines grouped by key, the keys in alphabetical order whatever their
case, and each key's lines in file order; the files are taken in their read order. KID then hands
keywords out type by type, in this order: Magic Effect → Armor → Weapon → Ammo → Potion → Scroll →
Location → Ingredient → Book → Misc Item → Key → Soul Gem → Spell → Activator → Flora → Furniture →
Race → Talking Activator → Enchantment. Within a type KID re-sorts the keywords so that a keyword
another line's filter names is handed out first. KID only adds keywords: no line takes one off, and
two lines never conflict, except that an item already holding one keyword of an `ExclusiveGroup`
receives no other keyword of that group.
<!-- end generated:kid.order -->

Diagnostics: KID writes **`po3_KeywordItemDistributor.log`** to
`Documents\My Games\Skyrim Special Edition\SKSE\` — check it to see what matched and what failed.
Nothing is errored to the user. A line with a wrong or missing type is dropped with **no** log line
at all; only an entry KID throws on (a FormID it cannot read, a chance that is not a number) is
logged (`Failed to parse entry [Keyword = …]`), and a keyword or filter that finds nothing is logged
as a `FAIL` or `SKIP` line. So a wrong token silently distributes nothing. This is why the skill
never invents syntax.

---

## 4. The distribution line

Every entry is one line of the form:

```
Keyword = formID~esp(OR)keywordEditorID|type|strings,formIDs(OR)editorIDs|traits|chance
```

Write no space beside a `|`: KID does not trim the sections, so `kwd | Armor` names the keyword
`kwd ` and the type ` Armor`, which is no type, and the line is dropped.

The value (right of `=`) is **5 sections separated by 4 pipes** (`|`). The keyword and the type are
required; the rest are optional and positional. **[source]** (`split("|")`, indices in `LookupConfigs.h`)

| Pos | Section | Required? | Covered in |
|----:|---|---|---|
| 0 | **Keyword** — the keyword to add (`formID~esp` or EditorID) | **yes** | §5 below |
| 1 | **Type** — which item type to add it to | **yes** | `types.md` |
| 2 | **Filters** — strings / FormIDs / EditorIDs that pick items | optional | `filters.md` |
| 3 | **Traits** — type-specific narrowing | optional | `traits.md` |
| 4 | **Chance** — distribution probability | optional (default 100) | §6 below |

A line without one of the 19 type strings, spelt exactly, is dropped with no log line (§2). An unused middle section is written blank or `NONE`
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

<!-- generated:kid.missing -->
In the keyword section a name no keyword holds is created as a new keyword, also when a record of
another type holds the name; a FormID that names nothing, or names a record that is not a keyword,
drops the line (`FAIL - keyword doesn't exist`). Every line's keyword is created before any filter
is read, so a line its filters drop still creates it, and another line's filter can name it. In the
filters a FormID, a runtime FormID or a plugin that names nothing, or a record of a type KID does
not take as a filter, is skipped and the line reaches more items (or, among the `-` entries,
excludes fewer), unless every plain entry without a leading `*` fails (a `*` entry does not keep the
line), or every `-` entry fails: then the line is dropped. A `+` group one part of which fails is
dropped, and when every group is dropped, so is the line. A filter name no record holds and no KID
line creates is kept as text: it matches an item whose EditorID or name is that text, or, for an
actor-value or archetype name, whose own actor value or archetype it is. A filter never creates a
keyword.
<!-- end generated:kid.missing -->

---

## 6. Chance — section 4

A percentage, **0.0 – 100.0**, default **100** when blank, absent, or `NONE`. **[source]** (`chance{100}`)

```ini
; 50% of enchanted iron weapons
Keyword = MyKwd|Weapon|*Iron|E|50
; 100% (chance omitted)
Keyword = MyKwd|Weapon|*Iron|E
```

A comment goes on its own line: KID reads everything after `=` as the value, so a `; note` after
the last section becomes part of it (§2).

KID's chance is **fixed per item, not re-rolled across sessions** [desc] — an item either gets the
keyword or doesn't, consistently. (KID distributes to item *records*, of which there is one each, so
there is no per-instance variation to re-roll — unlike SPID's per-NPC chance.)

---

## 7. `ExclusiveGroup` — the second key **[source]**

Besides `Keyword =`, a KID INI may define **mutually-exclusive keyword groups**. The Nexus description
does **not** document this; it is source-confirmed in v4.0.6 and v4.1.0 (`src/ExclusiveGroups.cpp`).

```
ExclusiveGroup = GroupName|keyword1,keyword2,-excludedKeyword
```

- **Section 0** — the group's name (any label).
- **Section 1** — a comma-separated list of **keyword** Form filters (FormIDs / EditorIDs), with no
  space beside a comma: the members are not trimmed, so ` keyword2` names nothing. A `-`
  prefix **removes** a keyword from the group; everything else **adds**. Only MATCH and exclusion (`-`)
  are meaningful here — **no `+` (ALL) and no `*` (wildcard).** Each entry must resolve to a **keyword**
  (KYWD); non-keywords are logged and ignored.
- Must have a name **and at least one form filter**, or the line is ignored with a warning.

**Effect:** keywords in the same group are treated as mutually exclusive during distribution — an item
that already carries one keyword from the group won't also receive another from it. Use it to keep,
e.g., material or rarity tags from doubling up on the same item.

---

## 8. Input forms & normalization

<!-- generated:kid.names -->
A token holding exactly one `~` is a FormID with its plugin (`0x800~Plugin.esp`): the number is
added to the plugin's own load index as the game numbers it, so a load index written into the number
moves it to another plugin's record, and a part before `~` that is no hex number fails the line
(`Failed to parse entry`). A token with two or more `~` is a name. Else `0x` followed by hex digits
only is a runtime FormID (`0x1CE14`, as the console prints it, so it depends on the load order).
Else a filter ending in `.esm`, `.esp` or `.esl` (case-sensitive) is a plugin name and selects every
item that plugin defines. Anything else is a name. The keyword section's name is looked up among
keywords only. A filter's name is looked up among every record type, then among the keywords KID
lines create, and only then kept as text. A filter ending in `.nif`, in any case, is a model path.
<!-- end generated:kid.names -->

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
| give a spell/perk/item/keyword to **NPCs** | SPID (best by group; for one named NPC, usually SkyPatcher) |
| put an item into a **container** | SkyPatcher (its container patcher) |
| change a record's **own fields** (damage, value, NPC stats) | SkyPatcher |

When unsure, ask **what receives the keyword**: an item record → KID; NPCs → SPID, and one named NPC
usually SkyPatcher.
