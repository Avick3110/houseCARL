---
name: kid-authoring
description: Author or interpret KID (Keyword Item Distributor) `_KID.ini` files — runtime, no-ESP distribution of keywords onto items (weapons, armor, ammo, magic effects, potions, scrolls, books, soul gems, spells, enchantments, and more) — using the bundled KID grammar reference rather than invented syntax. Use when the user wants to write or fix a `_KID` ini, add or distribute a keyword to items, tag all iron weapons / heavy armor / food / soul gems / a whole mod's gear with a keyword, filter items by name, archetype, equip slot, enchanted or templated state, or damage/armor/weight range, set up an `ExclusiveGroup` of mutually-exclusive keywords — or asks what an existing `_KID.ini` does, whether a given item gets a keyword from it, why a line isn't applying, or to audit or review a mod's KID lines. This is keywords on ITEMS — keywords on NPCs are SPID, items in containers are CID, editing a record's own fields is SkyPatcher. Load this before writing or reading any KID line — interpreting a `_KID.ini` needs the same non-obvious grammar as writing one (type strings, the `0x123~Plugin.esp` FormID form, per-type traits, `+`/`-`/`*` modifiers), and a misread token silently changes what you think gets tagged.
---

# KID Authoring

## Overview

KID (Keyword Item Distributor, by powerofthree) is an SKSE plugin that **adds keywords to items** —
weapons, armor, ammo, magic effects, potions, scrolls, books, soul gems, spells, enchantments, and
more (19 item types) — **at game startup**, driven by plain-text `_KID.ini` files. It writes nothing
to plugins or saves; it re-applies its keywords from scratch on each launch, so a KID mod is trace-free
to add or remove. This skill composes correct KID lines and places the file, drawing every type,
filter, trait, value, and example from the bundled grammar reference in `references/`.

It is a **procedural** skill with a **reference-lookup** core — the look-it-up-never-invent discipline
that `papyrus-reference`, `mutagen-reference`, and the sibling `spid-authoring` / `skypatcher-authoring`
use, because a fabricated KID token fails silently (see "Bundled-or-warn").

**Scope boundary — the deciding question is what receives the keyword:**
- a keyword onto an **item record** (weapon/armor/potion/…) → **KID** (this skill).
- a spell/perk/item/keyword onto an **NPC** → `spid-authoring`. (SPID also distributes *keywords*, but
  to NPCs — if the target is an NPC, it's SPID, not KID.)
- an item into a **container** → CID.
- changing a record's **own fields** (a weapon's damage, an NPC's stats) → `skypatcher-authoring`.
  SkyPatcher can also add keywords *as part of* editing a record; reach for KID when adding keywords by
  item-filter is the whole job.

## First step — open the grammar reference

KID has one uniform line grammar, so there's no per-type resolution to do up front — open the reference
and read the parts your task touches:

1. **`references/grammar-core.md`** — read first. The 5-section line, file discovery (`_KID.ini` in
   `Data\`), startup timing, the keyword id (incl. dynamic creation), chance, and the `ExclusiveGroup`
   feature. Almost every task needs it.
2. **`references/types.md`** — the 19 item types, their xEdit signatures, and which carry traits. Pick
   the one type your line targets.
3. **`references/filters.md`** — section 2: String filters (name / archetype / actor value / nif path)
   and Form/EditorID filters, plus the `+` / `-` / `*` / match modifiers and evaluation order. This is
   where "which items" lives.
4. **`references/traits.md`** — section 3: the per-type trait grammar (Armor `AR()`, Weapon animation
   types, Magic Effect `D()`/`CT()`/school, Soul Gem `SOUL()`/`GEM()`, …). Only the 12 trait-bearing
   types have these.
5. **`references/value-tables.md`** — flat lookups: effect archetypes, spell types, schools,
   delivery/casting numbers, soul/furniture/bench sizes, body slots, resistances, actor values.
6. **`references/index.jsonl`** — a grep-friendly router (one JSON entry per line) mapping a topic,
   type, filter, trait, or value to its file. Grep it to jump straight to the right section.

Read grammar-core plus the one or two topic files your task needs — don't bulk-load everything.

## Workflow — compose a KID line

1. **Identify the keyword** — what tag is being added. Reference it by **EditorID** (e.g.
   `WeapMaterialDwarven`) or **FormID** `0x12345~Plugin.esp`. If the EditorID doesn't resolve, KID
   *creates the keyword at runtime* — so a custom tag like `MyCursedGear` is valid and KID makes it
   (`grammar-core.md` §5).

2. **Identify the item type** — *what* gets the keyword. One of the 19 in `types.md` (`Weapon`,
   `Armor`, `Magic Effect`, `Potion`, `Book`, `Soul Gem`, …). Write the type string **exactly** as
   listed. One line targets one type — repeat the line per type to cover several.

3. **Choose the filters** (`filters.md`) — *which* items of that type:
   - by **name / archetype / actor value / nif path** → a **String** filter.
   - by **specific record / plugin / associated form** (e.g. a weapon's enchantment) → a **Form**
     filter (`0x123~Mod.esp`, an EditorID, or a plugin name for all of a mod's items).
   - apply modifiers: `+` requires all, `-` excludes, `*` wildcard-substring (strings only), bare =
     match-any. Evaluate Requirements → Exclusions → Matches → Wildcards.

4. **Add traits if needed** (`traits.md`) — narrow by type-specific properties (`E`/`-E` enchanted,
   `AR(10/50)` armor rating, `OneHandSword`, `20(0/25)` novice destruction, `BLACK` soul gem, …). Only
   the 12 trait-bearing types accept these.

5. **Compose the line** (`grammar-core.md` §4):
   ```
   Keyword = KeywordOrFormID | Type | filters | traits | chance
   ```
   - **Count the pipes** — sections are positional. `Keyword = MyKwd|Book|NONE|S,20` puts `S,20` in
     *traits*; `Keyword = MyKwd|Armor|||50` puts `50` in *chance* with empty filters and traits.
     Leave an unused middle section blank or `NONE`; drop a trailing unused section.
   - Set **chance** (0–100, default 100) only if you want less than guaranteed.

6. **Place the file** at `Data\<name>_KID.ini` — the **`_KID` substring in the filename is mandatory**
   (a file without it is never read), and KID INIs have **no `[Section]` headers** — every line sits at
   the top level. Comment with `;`.

7. **Confirm**: the type matches the record kind; the filter reads the way the user intended; the pipe
   positions are right; trait tokens are valid for that type; and the filename contains `_KID`.

## Bundled-or-warn — never invent KID grammar

The reference documents KID **v3.5.0**, the full published grammar. If a type, filter, trait, value, or
behavior isn't in it, **say so — don't fabricate a plausible token.** KID failures are silent: an
unparseable line or unknown token is logged to `po3_KeywordItemDistributor.log` and skipped, so a
guessed token yields a `_KID.ini` that quietly adds nothing — and nothing in-game points at the cause.
A clear "that isn't in the KID reference" beats a confident wrong line that costs a debugging session.
If the user is on a KID newer than v3.5.0 and asks about a feature not in the reference, surface that
the reference may be behind and offer to re-derive from the current KID source/description.

## Common mistakes

- **Targeting NPCs.** KID adds keywords to **items**. "Add a keyword to all bandits / female Nords" is
  *NPCs* → that's SPID. The keyword going onto an NPC, not an item, is the tell.
- **Miscounting pipe positions.** Sections are positional; a chance written one pipe early lands in
  *traits* and is silently misread. Count the pipes; keep blank middles (`||`) when a later section is
  used.
- **Wrong type string.** Use the exact name from `types.md` (`Magic Effect`, not `MagicEffect`; `Soul
  Gem`, not `Soulgem`). A wrong type string fails to match.
- **Traits on a trait-less type.** Location, Misc Item, Key, Activator, Flora, Race, Talking Activator
  take **no** traits — filter them by name/form only (`types.md`).
- **Confusing the `E` trait with the `Enchantment` type.** `E` filters *items that carry* an
  enchantment; `Enchantment` is the ENCH record type itself.
- **Forgetting `_KID` in the filename**, or adding `[Section]` headers. The `_KID` substring is
  required; KID reads only the unnamed root section.
- **Inventing a trait or value.** Look up the per-type trait list and the value tables — a wrong trait
  token (e.g. a casting-type number that doesn't exist) silently no-ops.

## Notes

- **Provenance.** The `references/` corpus is reconstructed from the MIT KID source
  (`powerof3/Keyword-Item-Distributor`, v3.5.0) and the Nexus #55728 description, with forwarded enum
  values confirmed against `powerof3/CommonLibSSE`. See `references/_CORPUS_STATUS.md`. On a KID version
  bump, re-derive before trusting it for new features.
- **Lookup without authoring.** The same reference answers "what's the spell-type number for Ability",
  "which body slot is 33", or "what archetypes can I filter magic effects by" — open `value-tables.md`;
  no line needs writing.
- **`ExclusiveGroup`.** A second key (`ExclusiveGroup = Name|kwd1,kwd2`) defines mutually-exclusive
  keywords so an item won't receive two from the same group (`grammar-core.md` §7) — source-documented,
  not on the Nexus page.
- **Cross-tool routing.** KID is one of the Skyrim distributor frameworks (KID / SPID / CID) plus the
  record-editor SkyPatcher. When the target is ambiguous, ask what receives the change: an item (KID),
  an NPC (SPID), a container (CID), or a record's own fields (SkyPatcher).
- **Comment syntax** is standard INI `;` (KID reads configs via the CSimpleIniA library).
