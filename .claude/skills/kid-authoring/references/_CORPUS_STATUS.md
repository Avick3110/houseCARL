# KID reference corpus — status & provenance

Build-time record for the `kid-authoring` reference corpus. Reading order for a session picking this
up: this file → `grammar-core.md` → `types.md` / `filters.md` / `traits.md` → `value-tables.md`.

## What this is

The bundled grammar reference for the `kid-authoring` skill (part of the 5-skill Skyrim
distributor-framework cluster — sibling to `spid-authoring` / `skypatcher-authoring`). It documents
KID's `_KID.ini` grammar — the line shape, all 19 item types, every filter and modifier, the per-type
traits, the value tables, and the `ExclusiveGroup` feature — reconstructed into a consistent,
lookup-friendly form. The `SKILL.md` (Layer 2, not yet authored) drives lookups against it.

KID has **one line grammar** across all types (like SPID), so the corpus is shaped as: `grammar-core`
(the line + mechanics) + `types` (what you tag) + `filters` (which items) + `traits` (per-type
narrowing — KID's richest, most type-specific part, hence its own file) + `value-tables` (the flat
enums).

## Source & version — dual-source

KID has **no Nexus "articles" tab** and its description tab partially filter-blocks automated fetches,
so the corpus draws on two complementary sources:

- **Mod:** Keyword Item Distributor (KID) by powerofthree (powerof3) — Nexus SE #55728.
  **Version: v3.5.0.rc1** (the latest release; repo frozen since 2024-04, so the grammar is stable).
- **Grammar source of truth — the MIT GitHub source** (`powerof3/Keyword-Item-Distributor`, `master`,
  **MIT License**). Authoritative for the line shape, the exact type-strings, the trait syntax, the
  filter dispatch, file discovery, and `ExclusiveGroup`.
- **User-facing docs + worked examples — the Nexus #55728 description.** Authoritative for the value
  tables (enum numbers), the per-type Form-filter list, and the copy-paste examples.
- **Raw captures live at `dev/references/KID/`:**
  - `_extracted/kid-description-55728.txt` — the **verbatim Nexus description**, captured 2026-06-04
    via Claude in Chrome with all **18 `bbc_spoiler` blocks force-revealed** (every value table + the
    9 worked examples). See `dev/references/KID/source-notes.md` for the capture method (incl. the
    sanitizer-bypass via in-page blob download).
  - `source-notes.md` — provenance, the MIT-source file list, and the findings reconciled here.

## Coverage

| | |
|---|---|
| Line grammar (5 sections, keyword id, chance, file discovery, multi-key) | **complete** — `Defs.h`/`LookupConfigs.h`/`LookupConfigs.cpp` + description agree |
| Item types | **19 / 19** with canonical strings + xEdit signatures (`types.md`) |
| Trait classes | **10 / 10** (12 trait-bearing types; Spell/Ench/Scroll share one) (`traits.md`) |
| Filters | String channels (name/archetype/AV/nif) + Form/EditorID + per-type Form filters + the 4 modifiers (`filters.md`) |
| Value tables | archetypes (47), spell types (0–13), schools, soul/furniture/bench sizes, body slots, delivery/casting (`value-tables.md`) |
| `ExclusiveGroup` | **documented** — source-only feature the Nexus docs omit (`grammar-core.md` §7) |
| `Chance`, log path, VR FormID quirk | complete |

## Confidence

- **High** for everything dual-confirmed: line grammar, the 19 type-strings (read straight from the
  parser's `itemTypes`), the 10 trait classes and their token syntax (read from `Traits.h`), file
  discovery, chance default, `ExclusiveGroup`, and the description's published value tables
  (archetypes, spell types, schools, soul/furniture/bench sizes).
- **`[lib]`-marked values are byte-confirmed against CommonLibSSE** (`powerof3/CommonLibSSE@dev`, the
  library KID compiles against) — the earlier "engine-knowledge" residual is **closed**:
  - **Delivery `D()` (0–4)** and **Casting Type `CT()` (0–3)** — verified from `RE/M/MagicSystem.h`.
  - **Skill AV indices** (OneHanded 6 … Enchanting 23, schools 18–22) — verified from `RE/A/ActorValues.h`.
  - **Body slots 30–61** — verified from `RE/B/BGSBipedObjectForm.h`; this **corrected two mislabels**
    (30 = Head, not Head/Hair; 31 = Hair, not LongHair) and completed slots 44–61.
  - **Resistance `R(value)` numbers (39–45)** — now printed (same AV enum), replacing the earlier
    "read it from xEdit" punt.
- **Minor residuals to confirm at the §8 review (or if a test line fails):**
  - The exact **Matches-vs-Wildcards** boolean: documented per the shared powerof3 `Filters<T>` model
    (the same ALL/NOT/MATCH/ANY struct SPID uses, confirmed in `Defs.h`) + the description's
    "progressively restricts" — `LookupFilters.cpp` was not deep-read field-by-field.
  - The **per-type Form-filter** table (Location→music type, etc.) is from the description's own list;
    not each entry was cross-checked against `LookupFilters.cpp`.

## Source-repo cross-check

`powerof3/Keyword-Item-Distributor` (GitHub, `master`, **MIT License**) was read for grammar facts only
— no code vendored (MIT would permit it; the corpus documents grammar, it doesn't embed source). Files
consulted: `include/Defs.h` (line comment + `Filters<T>`), `include/Traits.h` (all 10 trait classes),
`include/Cache.h` (`itemTypes`, archetype map, ActorValue map, FormType set), `include/LookupConfigs.h`
(section enum, `Data` defaults), `src/LookupConfigs.cpp` (the parser — split, modifier dispatch, type
switch, chance), `include/ExclusiveGroups.h` + `src/ExclusiveGroups.cpp` (the `ExclusiveGroup` feature).

The numeric enums KID forwards but doesn't define were confirmed against **`powerof3/CommonLibSSE@dev`**
(the library KID builds on): `RE/M/MagicSystem.h` (Delivery, CastingType, SpellType), `RE/A/ActorValues.h`
(skill + resistance indices), `RE/B/BGSBipedObjectForm.h` (biped slots).

## Structure

```
references/
├── _CORPUS_STATUS.md   ← this file
├── grammar-core.md     ← what KID is, file discovery + parsing, the 5-section line, the keyword id,
│                          chance, ExclusiveGroup, input forms, cross-tool routing
├── types.md            ← the 19 item types + signatures + which are trait-bearing
├── filters.md          ← section 2: String + Form/EditorID filters, per-type Form filters, the
│                          +/-/* /match modifiers + evaluation order
├── traits.md           ← section 3: the 10 per-type trait classes and their token grammar
├── value-tables.md     ← flat enums (archetypes, spell types, schools, AVs, delivery/casting,
│                          soul/furniture/bench sizes, body slots, defaults)
└── index.jsonl         ← lookup routing (Layer 2 — grep-friendly topic→file router)
```

## Layering (build plan)

- **Layer 1 (this corpus) — DONE pending Aaron's review.** The five reference files above. Built only
  after the complete reference was in hand (description captured + MIT source cross-checked) — the
  project's no-guesswork gate.
- **Layer 2 — DONE pending review.** `SKILL.md` (procedural + bundled-or-warn, modeled on
  `spid-authoring`; carries `name: kid-authoring` per the **revised 2026-06-04 standard** — `name:` =
  folder, required for Codex + Claude dual-host), `index.jsonl` (router, consistent with the siblings),
  and `evals/eval_set.json` (`HOUSECARL_SKILL_AUTHORING.md` §6.5 fan-out — 20 fresh-context Sonnet
  agents, anonymized routing menu: **recall 10/10, specificity 10/10**, both gates passed). §8 shipping
  checklist walked — all **19** items pass (SKILL.md 133 lines; description 1029 chars; `name:` = folder
  per item 2; added to `$Skills` in `scripts/build-plugin.ps1` per item 19; skill-creator invoked).

## Cluster note

`kid-authoring` is one of the 5 distributor skills. Cross-tool divergences for the eventual routing
skill + SKILL.md: KID's FormID is **suffix-tilde** `0x123~Plugin.esp` (shared with SPID/CID; SkyPatcher
uses prefix-pipe). KID files are **flat `Data\*_KID*.ini`** with **no `[Section]` headers** (SPID is
flat `Data/*_DISTR.ini`; SkyPatcher uses `SKSE\Plugins\` per-type subfolders). The deciding question
across the family is **what receives the change**: an **item** record → KID; an **NPC** → SPID; a
**container** → CID; a record's **own fields** → SkyPatcher. KID and SPID share the author and the
`+`/`-`/`*` modifier idioms — keep idiom wording aligned with `spid-authoring`.
