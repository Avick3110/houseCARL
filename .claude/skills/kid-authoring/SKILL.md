---
name: kid-authoring
description: >-
  Authors and interprets Keyword Item Distributor (KID) `_KID.ini` files — runtime, no-ESP
  distribution of keywords onto item records, on the KID v3.5.0 grammar. Use when writing or
  auditing a `_KID.ini`, tagging items by name, archetype, equip-slot or stat filters, or asking
  why a KID line isn't applying; load it before composing any KID line, because a misread token
  silently changes what gets tagged. Keywords onto NPCs are SPID, not KID.
compatibility: Requires the houseCARL MCP server and a configured Mod Organizer 2 instance.
---

# KID Authoring

## Overview

KID (Keyword Item Distributor, by powerofthree) is an SKSE plugin that **adds keywords to item
records** — weapons, armor, ammo, magic effects, potions, scrolls, books, soul gems, spells,
enchantments and more (19 item types) — **at game startup**, driven by plain-text `_KID.ini` files.
It writes nothing to plugins and nothing to saves, and re-applies its keywords from scratch on each
launch, so a KID mod is trace-free to add or remove.

This skill **composes** a correct line from the bundled v3.5.0 grammar, **grounds** it against the
live load order so its reach is measured before the file ships, and **places** the file where KID
will read it. Every type, filter, trait and value comes from `references/`, never from memory — a
fabricated token fails silently.

## This skill's lane

KID's lane is **a keyword onto an item record**. A keyword onto an NPC is SPID: load
`housecarl:spid-authoring` for that job instead. Sibling skills are written `housecarl:<name>`, the
Claude Code plugin form; a Codex install sees the same skill under its bare folder name
(`spid-authoring`). MCP tool names are bare — `housecarl_records` — on every host.

## Which file answers which question

Read `references/grammar-core.md` first — it holds the five-section line, how KID finds and parses
the file, and what happens to a line it cannot read. When you need the exact type string, or whether
that type takes traits at all, read `references/types.md`. When the question is *which* items, read
`references/filters.md`. When the narrowing is a property of the item rather than its name — armor
rating, animation type, soul size, effect school — read `references/traits.md`. When you need a
number for a named thing — a spell type, a school, a body slot — read `references/value-tables.md`.
To jump straight to a section rather than read a file, grep `references/index.jsonl`.

Read what your task needs, not everything.

## Workflow — compose, ground, place

1. **Identify the keyword.** Reference it by EditorID (`WeapMaterialDwarven`) or FormID
   (`0x12345~Plugin.esp`). An EditorID resolving to nothing is not an error: KID **creates the
   keyword at runtime**, so a custom tag like `MyCursedGear` is valid. A runtime keyword has no
   persistent FormID, so when other tooling must point at the tag, author the record instead —
   `housecarl_create` with `records=[{"record_type": "Keyword", "editorid": "MyCursedGear"}]` and
   `patch` naming the new plugin. Convert the FormID it reports, `XXXXXX:Plugin.esp`, to KID's tilde
   form `0xXXXXX~Plugin.esp` (leading zeros stripped) before it goes in the line: pasted as reported
   it reads as an EditorID and quietly creates an empty runtime keyword.

2. **Identify the item type.** One of the 19 listed in the types file, written **exactly** as given
   (`Magic Effect`, not `MagicEffect`). One line targets one type; repeat the line per type.

3. **Choose the filters.** By name / archetype / actor value / nif path → a String filter; by record,
   plugin or associated form → a Form filter. Modifiers: `+` requires all, `-` excludes, `*`
   wildcard-substring (strings only), bare = match-any; KID evaluates requirements → exclusions →
   matches → wildcards.

4. **Add traits if the narrowing is a property of the item.** Only the 12 trait-bearing types accept
   them. `references/traits.md` names the record field behind each trait in its own column — that is
   the path step 6's census puts in `where=`. Where the column says the schema holds no single field
   for a trait, resolve it against the record with `housecarl:mutagen-reference` before composing.
   Mind the spelling split the column flags: the INI takes KID's token, a `where=` takes the
   schema's (`HandToHandMelee` against `HandToHand`).

5. **Compose the line, and count the pipes.**
   ```
   Keyword = KeywordOrFormID | Type | filters | traits | chance
   ```
   Sections are positional. `Keyword = MyKwd|Book|NONE|S,20` puts `S,20` in *traits*;
   `Keyword = MyKwd|Armor|||50` puts `50` in *chance*. Leave an unused middle section blank or
   `NONE`; a trailing unused section can be dropped. Set `chance` (0–100, default 100) only when you
   want less than guaranteed.

6. **Ground the line.** Measure the reach before the file ships, and report the number with the call
   that produced it rather than as a claim. The census is `housecarl_records` with `types=["WEAP"]`,
   `where=["Data.AnimationType = OneHandDagger"]` and `counts_only=true`. To let the user eyeball
   the set, drop `counts_only` and pass a `limit` — the default `summary` rows carry each match's
   identity, and `project={"form": "fields", "fields": ["Name"]}` puts the name beside it — field
   paths live inside the form, never flat (form `identity` labels a `formids=` list, and a scan
   refuses it). For a set too large to render inline, pass `to_file` an absolute `.jsonl` path and
   re-enter it later as
   `formids=["@<that absolute path>"]`. Leave `source` omitted, or pass `"winner"` — the load-order
   winner is the record KID acts on. To show what a String filter would have caught instead, scan
   the channels that filter actually tests (`references/filters.md` §1 and §3), which is more than
   the display name: a bare term is an exact test on the EditorID or the name, and a `*wildcard` is
   a substring test on the EditorID, the name **and** the item's own keyword EditorIDs. So a
   wildcard census is three scans — `where=["Name contains Dagger"]`,
   `where=["editorid contains Dagger"]` and
   `where=["Keywords[*any]->editorid contains Dagger"]` — and the reach is their **union**, which
   `where=` cannot express in one call because its terms are ANDed. Get the union by
   inclusion-exclusion over the three counts and their four intersections, or by writing each scan
   to a `to_file` artifact and merging the identity columns. A single `Name contains` count
   under-reports a wildcard line, usually by a lot: the keyword channel alone catches everything
   carrying `WeapTypeDagger`.
   When the line carries a **plugin-name** filter, count by the **defining** plugin, not the winner:
   KID tests `TESFile::IsFormInMod` on the item's own FormID (`references/filters.md` §2), so the
   filter catches only records that plugin defines, and a patch that overrides them changes nothing
   about the set. Count the records that plugin defines — on a patched order that is a different
   number from "records this plugin wins", and it is the first one the filter means.
   **This grounds one type-plus-trait predicate, not the whole line:** nothing on the 2.0
   surface replays KID's evaluation order or `chance` — that is issue **#614**, not in 2.0, so say
   so rather than implying the whole line was proved.

7. **Place the file** at `Data\<name>_KID.ini`. The `_KID` substring is **mandatory** — a file
   without it is never read — and the file sits flat in `Data\`, not under
   `Data\SKSE\Plugins\SkyPatcher\<type folder>\` where SkyPatcher's INIs live. KID INIs have **no
   `[Section]` headers**; every line sits at the top level, and `;` starts a comment. (Backslashed
   paths here are the game's own literals; every path into this skill's own files is forward-slashed.)

8. **Confirm, then stop.** Check that the type matches the record kind, the pipe positions are right,
   the trait tokens are legal for that type, and the filename contains `_KID`. **The stop condition
   is step 6's count:** if the census is zero, or is a number the user did not expect, do not ship
   the line — say what it reached and re-cut the filter. After the next launch the deterministic
   backstop is `po3_KeywordItemDistributor.log` in `Documents\My Games\Skyrim Special Edition\SKSE\`
   — read it and check each line matched. No houseCARL tool reads that log; open it as a file.

## Worked lines, end to end

**Tag every dagger in the load order.** The trait, not the name, is the filter:

```ini
Keyword = HC_AuditDaggerTag|Weapon|NONE|OneHandDagger
```

Grounded on a 3,801-plugin order this reaches 786 WEAP records, defined across 54 plugins and won by
39 (`project={"form": "aggregate", "group_by": "defined_in"}`, then `"winner"`). A `*Dagger`
wildcard is wrong in **both** directions on the same order. Its three channels reach 676 records by
name, 702 by EditorID and 773 by keyword EditorID — 778 in union, and 772 of those are also
`OneHandDagger`, so it **misses 14** of the trait's 786 and **over-catches 6**. The 14 it misses are
the ones carrying no dagger keyword and no "dagger" text: `ccBGSSSE019_ForkofHorripilation` ("Fork of
Horripilation") and its replica/spoon variants, `zzzCOWitchKnife01` ("Witch Knife"),
`WTKitchenOverlordKnife` ("Dragonslayer"), `_Camp_DummyWeapon` and `MRTBlankWeapon` (no name at all).
The 6 it over-catches are `DBMTWR_RiftenDaggerDummy` ("The Dagger of Riften (2)", `HandToHand`),
`zzzGHCrSkavenDaggers` ("Skaven Daggers", `TwoHandSword`), `zzzRevSerpentSword` ("Atakota Sword",
`OneHandSword`, caught on its `WeapTypeDagger` keyword) and the three nameless `DummyDagger` records
(`OneHandSword`) — nameless is no protection, because the EditorID is a live channel. The keyword
channel is what makes the wildcard nearly as good as the trait here, and what makes a name-only
census (676) a bad proxy for it.

**Tag one mod's armor above rating 20**, filters and traits both used:

```ini
Keyword = MyLightSetTag|Armor|MyArmorMod.esp|AR(20)
```

**Tag every soul gem that is black, and nothing else** — empty filters held with `NONE`:

```ini
Keyword = MyBlackGemTag|Soul Gem|NONE|BLACK
```

## Never invent KID grammar

The bundled reference documents **KID v3.5.0**. If a type, filter, trait, value or behaviour is not
in it, say so — do not fabricate a plausible token. The pin runs **both** ways: an install older than
v3.5.0 may lack a feature the reference describes, and a newer one may add features it lacks; either
way, offer to re-derive from the current KID source rather than guessing.

KID's failures are silent. A malformed entry is logged (`Failed to parse entry [Keyword = …]`) and
**skipped**, never surfaced to the user, so a guessed token yields a `_KID.ini` that quietly adds
nothing with nothing in-game pointing at the cause. `po3_KeywordItemDistributor.log` is the only
deterministic backstop, which is why every token is looked up rather than written from memory.

The user's own instructions outrank anything in this skill.

## Common mistakes, and the rule that replaces each

- **Writing a KID line in SPID's shape.** The two ship together and their lines look alike, but a
  KID line is **five** sections, four pipes (`Keyword = keyword | Type | filters | traits | chance`),
  and a SPID line is **seven**, six pipes
  (`FormType = form | strings | forms | level | traits | count | chance`). SPID's split of
  string filters and form filters into two separate sections is the trap: KID mixes both kinds in
  its **one** filters section, so a line written SPID-style puts the form filters where KID reads
  traits and is skipped or silently misread. KID's type token is also its own — `Potion`, never
  `ALCH`.
- **Miscounting pipe positions.** Sections are positional; a chance written one pipe early lands in
  *traits* and is silently misread. Count the pipes and keep blank middles (`||`) when a later
  section is used.
- **A wrong type string.** Use the exact name the types file lists (`Soul Gem`, not
  `Soulgem`). A wrong type string matches nothing.
- **Traits on a trait-less type.** Location, Misc Item, Key, Activator, Flora, Race and Talking
  Activator parse no traits; filter these by name or form only, and see the types file for what
  happens to a traits section written on one.
- **Confusing the `E` trait with the `Enchantment` type.** `E` filters items that *carry* an
  enchantment; `Enchantment` is the ENCH record type itself.
- **Forgetting `_KID` in the filename**, or adding `[Section]` headers. The substring is required,
  and KID reads only the unnamed root section.
- **Putting the file under `Data\SKSE\Plugins\`.** SkyPatcher reads from
  `Data\SKSE\Plugins\SkyPatcher\<type folder>\`; KID reads `Data\` flat, so a correctly-named file
  in the wrong folder is never found.
- **Inventing a trait or value.** Look the per-type trait list and the value tables up — a casting
  type or body slot that does not exist silently no-ops.

## Notes

- **Provenance.** The `references/` corpus is reconstructed from the MIT KID source
  (`powerof3/Keyword-Item-Distributor`, v3.5.0) and the Nexus #55728 description, with forwarded
  enum values confirmed against `powerof3/CommonLibSSE`. Re-derive on a version bump.
- **Lookup without authoring.** The same reference answers "what is the spell-type number for
  Ability" or "which body slot is 33" — open the value tables; no line needs writing.
- **`ExclusiveGroup`.** A second key (`ExclusiveGroup = Name|kwd1,kwd2`) defines mutually-exclusive
  keywords so an item never receives two from the same group — source-documented, not on the Nexus
  page.
