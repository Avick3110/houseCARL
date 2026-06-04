# SPID Value Tables — flat lookups

Quick-reference enumerations the prose files (`grammar-core.md`, `form-types.md`, `filters.md`) point
at. Each table is reproduced from the verbatim article unless marked **[source]** (from the MIT-source
cross-check, `powerof3/Spell-Perk-Item-Distributor`).

---

## Skill indices (Level Filters → Skill / Skill Weight)

Used as `skillIndex(min/max)` and `wskillIndex(min/max)`.

| # | Skill | | # | Skill |
|---:|---|---|---:|---|
| 0 | One-Handed | | 9 | Sneak |
| 1 | Two-Handed | | 10 | Alchemy |
| 2 | Archery | | 11 | Speech |
| 3 | Block | | 12 | Alteration |
| 4 | Smithing | | 13 | Conjuration |
| 5 | Heavy Armor | | 14 | Destruction |
| 6 | Light Armor | | 15 | Illusion |
| 7 | Pickpocket | | 16 | Restoration |
| 8 | Lockpicking | | 17 | Enchanting |

> **Article quirk:** the article's prose says "one of the 17 skills" but lists indices **0–17**
> (18 rows). The list is authoritative — valid indices are **0 through 17**, Enchanting being 17.
> (Skyrim genuinely has 18 AV skills; "17" is the author's off-by-one.)

---

## Trait letter codes (Trait Filters) **[source]**

From `TraitsFilterComponentParser` (`LookupConfigs.h`). Each trait has a positive and a negated form.

| Code | Trait | Negated | Notes |
|---|---|---|---|
| `F` | Female | `-F` ≡ **`M`** | sex is binary — "not female" is stored as Male |
| `M` | Male | `-M` ≡ **`F`** | "not male" is stored as Female |
| `U` | Unique | `-U` | |
| `S` | Summonable | `-S` | |
| `C` | Child | `-C` | |
| `L` | Leveled (Is PC Level Mult) | `-L` | |
| `T` | Player's Teammate | `-T` | |
| `D` | Dead / Start Dead | `-D` | |

Combine with `/` (AND). Traits is the only filter that may mix modifiers in one expression.

---

## Package List Types (CountOrPackageIndex when the form is a FormList)

For `Package = <FLST>|||||<type>` — which package list the FormList overwrites. Default `0`.

| # | Type | xEdit |
|---:|---|---|
| 0 | Default Package List | DPLT – Default Package List |
| 1 | Spectator Override | SPOR – Spectator override package list |
| 2 | Observe Corpse Override | OCOR – Observe dead body override package list |
| 3 | Guard Warn Override | GWOR – Guard warn override package list |
| 4 | Enter Combat Override | ECOR – Combat override package list |

---

## Distributable form types → record signatures

The `FormType` (left of `=`). Full detail + special cases in `form-types.md`.

| FormType | Signature(s) |
|---|---|
| Spell | SPEL, LVSP |
| Perk | PERK |
| Item | ALCH, AMMO, ARMO, BOOK, INGR, KEYM, LVLI, MISC, SCRL, SLGM, WEAP |
| Shout | SHOU |
| Package | PACK, FLST *(FLST must contain only Packages or the game may crash)* |
| Keyword | KYWD *(can be created dynamically)* |
| Outfit | OTFT |
| SleepOutfit | OTFT *(cannot be inferred)* |
| Faction | FACT |
| Skin | ARMO *(cannot be inferred)* |

---

## Filterable forms (Form Filters → NPC properties)

The form types you may put in a **Form Filter** (position 2) and the NPC field each checks. Detail in
`filters.md` §2.

| Form type | Sig | NPC's record (xEdit) |
|---|---|---|
| Combat Style | CSTY | ZNAM – Combat Style |
| Class | CLAS | CNAM – Class |
| Faction | FACT | Factions |
| Race | RACE | RNAM – Race |
| Outfit | OTFT | DOFT – Default outfit |
| Perk | PERK | Perks |
| Specific NPC | NPC_ | FormID / EDID |
| NPC's Template | NPC_ | FormID / EDID |
| Actor | ACHR | FormID / EDID *(7.3+)* |
| Voice Type | VTYP | VTCK – Voice |
| Known Spell | SPEL | Actor Effects |
| Skin | ARMO | WNAM – Worn Armor |
| Editor Location | LCTN | XLCN – Persistent Location *(editor placement)* |
| FormList | FLST | recursive (may nest FormLists) |

Plus: a **plain plugin name** in a Form Filter matches all NPCs defined in that plugin.

---

## Distribution order (per-type passes, at runtime)

```
Keywords → Factions → Perks → Spells → Shouts → Packages → Items → Skins → Outfits
```

Within a type: load order (file A→Z, then top→bottom). Exception: Keywords are topo-sorted so a
keyword that depends on another distributes after its dependency.

---

## Default values (when a section is blank / absent)

| Field | Default |
|---|---|
| StringFilters / FormFilters / LevelFilters / TraitFilters | none (no restriction) |
| CountOrPackageIndex — as Item Count | 1 |
| CountOrPackageIndex — as Package Index | 0 (first) |
| CountOrPackageIndex — as Package List Type | 0 (Default Package List) |
| Chance | 100 (guaranteed) |

---

## FormID / EditorID syntax (quick)

| Form | Example | Notes |
|---|---|---|
| EditorID | `ElvenMace` | preferred — stable across merge/ESL/compaction |
| FormID (tilde-suffix) | `0x12345~MyPlugin.esp` | SPID/KID/CID style (SkyPatcher uses `Plugin.esp\|0x123`) |
| xEdit paste form | `0001396B - Skyrim.esm` | `" - "` auto-converts to `~` **[source]** |
| zero-padding | `00012345` → `0x12345`; `0x00012345` → `0x12345` | leading zeros forgiven **[source]** |
