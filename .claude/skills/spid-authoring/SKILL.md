---
name: spid-authoring
description: Author or interpret SPID (Spell Perk Item Distributor) `_DISTR.ini` files — runtime, no-ESP distribution of spells, perks, items, shouts, keywords, outfits, factions, and AI packages to NPCs — using the bundled SPID grammar reference rather than invented syntax. Use when the user wants to write or fix a SPID `_DISTR` ini, distribute a spell/perk/item/keyword/outfit to NPCs, give all bandits or guards or a whole race or faction some form, hand out gear or an ability filtered by gender/level/skill/chance, set an AI package on NPCs, make a distribution deterministic — or asks what an existing `_DISTR.ini` does, whether a given NPC is targeted by it, why a line isn't distributing, to audit or review a mod's SPID lines, or which filters and traits target which NPCs (string/form/level/trait filters, the `F`/`M`/`U`/`T` trait codes, the `0x123~Plugin.esp` FormID form, the deterministic `!` chance). Load this before writing or reading any SPID line — interpreting a `_DISTR.ini` needs the same non-obvious grammar as writing one, and a misread filter silently changes who you think is targeted.
---

# SPID Authoring

## Overview

SPID (Spell Perk Item Distributor, by powerofthree) is an SKSE plugin that distributes forms —
spells, perks, items, shouts, keywords, outfits, factions, AI packages — **to NPCs at runtime**,
driven by plain-text `_DISTR.ini` files. It writes nothing to plugins or saves; it redistributes
from scratch on each launch, so SPID mods are trace-free to add or remove. This skill composes
correct SPID distribution lines and places the INI, drawing every form type, filter, modifier,
value, and example from the bundled grammar reference in `references/`.

It is a **procedural** skill with a **reference-lookup** core — the lookup-and-never-invent
discipline that `papyrus-reference`, `mutagen-reference`, and the sibling `skypatcher-authoring`
use, because a fabricated SPID token fails silently (see "Bundled-or-warn").

**Scope boundary — what this is *not*:**
- **Editing a record's own fields** (a weapon's damage, an NPC's health, keywords *on a record*) →
  that's `skypatcher-authoring`. SPID gives forms *to NPCs*; it does not edit record properties.
- **Distributing keywords to ITEMS** (weapons, armor, ammo) → that's KID, a separate framework.
  SPID's keyword distribution targets **NPCs**.
- **Adding/removing items to/from CONTAINERS** → that's CID. SPID does not touch containers.
- The deciding question is **the target**: NPCs → SPID; items → KID; containers → CID; a record's
  fields → SkyPatcher.

## First step — open the grammar reference

The SPID grammar is uniform (one line shape across all 10 form types), so there's no per-type
resolution to do — open the reference and read the parts your task touches:

1. **`references/grammar-core.md`** — read first. The line syntax (the seven pipe sections), file
   discovery, distribution order, FormID/EditorID forms, input normalization, filter-combination
   logic, count/index, chance. Almost every task needs it.
2. **`references/form-types.md`** — the 10 things you can distribute (Spell, Item, Keyword, Outfit,
   Package, Faction, …), their signatures, and special cases (FormList-must-be-Packages,
   dynamic keywords, the un-inferable SleepOutfit/Skin).
3. **`references/filters.md`** — the 4 filter sections (String / Form / Level / Trait) in depth:
   what each matches, its modifiers, and worked examples. This is where targeting lives.
4. **`references/value-tables.md`** — flat lookups: skill indices (0–17), trait letter codes,
   package-list types, signatures, distribution order, defaults.
5. **`references/index.jsonl`** — a grep-friendly router (one JSON entry per line) mapping a topic,
   form type, filter, or value-table to its file. Grep it to jump straight to the right section.

Read grammar-core plus the one or two topic files your task needs — don't bulk-load everything.

## Workflow — compose a distribution line

1. **Identify the FormType** — *what* is being distributed. A spell → `Spell`; a potion or weapon or
   gold → `Item`; an AI package → `Package`; a custom tag → `Keyword` (SPID can create it on the
   fly). The generic `Form =` infers the type from the named record — except SleepOutfit and Skin,
   which must be named explicitly (`form-types.md`).

2. **Identify the target NPCs** — *to whom* — and pick the filter section(s) in `filters.md`:
   - by **name / EditorID / keyword / race-keyword / template** → **String** filter (position 1)
   - by **race / faction / class / combat style / voice / known spell / editor location / plugin /
     specific NPC** → **Form** filter (position 2)
   - by **level or skill** → **Level** filter (position 3) — note this moves the entry into Leveled
     Distribution
   - by **gender / unique / summonable / child / leveled / teammate / dead** → **Trait** filter
     (position 4); trait letters are in `value-tables.md`

3. **Compose the line:**
   ```
   FormType = FormOrEditorID | StringFilters | FormFilters | LevelFilters | TraitFilters | CountOrPackageIndex | Chance
   ```
   - Reference the form by **EditorID** (stable, preferred) or **`0x123~Plugin.esp`** (suffix-tilde).
   - **Count the pipes** — each section is positional. A value meant for Chance sits after six pipes;
     a value meant for Count sits after five. Leave an unused middle section blank (`||`) or `NONE`;
     a trailing unused section can just be dropped.
   - Combine filters per `grammar-core.md`: **OR within a section, AND between sections**, and
     **exclusions (`-X`) are always AND**. To target a *union* of groups, write multiple lines for
     the same form.

4. **Set Count / Index / Chance** if needed (`grammar-core.md`): item count or `min-max` range;
   zero-based package index; package-list type `0`–`4`; a `0`–`100` chance (default 100). Append
   `!` to the chance to make the result deterministic (consistent per NPC + save across sessions).

5. **Place the file** at `Data/<name>_DISTR.ini` — the **`_DISTR` suffix is mandatory** (a file
   without it is never read). Files load alphabetically A→Z, each top-to-bottom. Comment lines with
   `;`. (Shipped inside a mod managed by a mod manager, but resolved from `Data/` at runtime.)

6. **Confirm**: the FormType matches the record; the filter logic reads the way the user intended
   (re-read it as "give X when (…) AND (…)"); the pipe positions are right; and the filename ends in
   `_DISTR.ini`.

## Bundled-or-warn — never invent SPID grammar

The reference documents SPID **7.3.0**, the full published grammar. If a form type, filter, trait,
or behavior isn't in it, **say so — don't fabricate a plausible token.** SPID failures are silent:
an unparseable line, an unknown filter term, or an unresolvable form is skipped with no error, so a
guessed token yields a `_DISTR.ini` that quietly distributes nothing — and nothing in the log points
at the cause. A clear "that isn't in the SPID reference" beats a confident wrong line that costs a
debugging session. If the user is on a SPID version newer than 7.3.0 and asks about a feature not in
the reference, surface that the reference may be behind and offer to re-derive from the current
SPID documentation.

## Common mistakes

- **Miscounting pipe positions.** The sections are positional; a chance written one pipe early lands
  in CountOrPackageIndex and silently changes meaning. Count the pipes, and keep blank middle
  sections (`||`) when a later section is used.
- **SkyPatcher's FormID form.** SPID uses **suffix-tilde `0x123~Plugin.esp`**; SkyPatcher uses
  prefix-pipe `Plugin.esp|0x123`. They are not interchangeable — a SkyPatcher-style ID won't resolve.
- **Expecting `Form =` to infer SleepOutfit or Skin.** Both reuse records another type claims first
  (OTFT→Outfit, ARMO→Item), so they're never inferred — name the type explicitly.
- **A FormList under `Package` that contains non-Packages.** SPID will most likely crash the game —
  keep package FormLists pure (`form-types.md`).
- **Mixing two modifiers in one String or Form expression.** Only one modifier per expression there
  (`-Guard+ActorTypeNPC` is invalid). Traits is the *only* section that allows mixing (`F/-U/L`).
- **Wrong tool for the target.** Distributing a keyword to *items* is KID, not SPID; adding loot to a
  *container* is CID; editing a record's *fields* is SkyPatcher. SPID's targets are NPCs.
- **Forgetting the `_DISTR` suffix**, or assuming a per-type subfolder. SPID files are flat in
  `Data/` and must carry `_DISTR` in the name, or they're ignored entirely.
- **Inventing a filter by analogy.** There are exactly four filter sections; "match by X" must map to
  one of them (text → String, form → Form, level/skill → Level, trait → Trait). Look it up in
  `filters.md` rather than assuming a filter exists.

## Notes

- **Provenance.** The `references/` corpus is reconstructed from "SPID: The Complete Reference"
  (Nexus article 6617, SPID 7.3.0) and cross-checked against the MIT source
  (`powerof3/Spell-Perk-Item-Distributor`). On a SPID version bump, re-derive before trusting it
  for new features.
- **Lookup without authoring.** The same reference answers "what's the skill index for Destruction",
  "what trait letter is Player's Teammate", or "what are the package-list types" — open
  `value-tables.md`; no line needs to be written.
- **Cross-tool routing.** SPID is one of the Skyrim distributor frameworks (SPID / KID / CID) plus
  the record-editor SkyPatcher. When the user's target is ambiguous, the deciding question is what
  receives the change: NPCs (SPID), items (KID), containers (CID), or a record's own fields
  (SkyPatcher).
- **Comment syntax** is standard INI `;` (SPID reads configs via the CSimpleIniA library); the
  `references/` corpus flags this as a library-default rather than a SPID-specific guarantee.
