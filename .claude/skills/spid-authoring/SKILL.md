---
name: spid-authoring
description: >-
  Authors and interprets SPID (Spell Perk Item Distributor) `_DISTR.ini` files — runtime, no-ESP
  distribution of spells, perks, items, keywords, outfits and factions to NPCs, on the bundled SPID
  7.3.0 grammar. Load it before composing or judging any SPID line: a misread filter silently
  changes who is targeted, and an unparseable one is skipped with no error. Use when writing or
  auditing a `_DISTR.ini`, targeting NPCs by faction, race, level or trait, or asking why a line
  isn't distributing, including a line that names one NPC. Not this skill: a keyword onto item
  records is KID, and a record's own fields are SkyPatcher, which is also usually the better fit for
  one named NPC — a preference, not a boundary.
compatibility: Requires the houseCARL MCP server and a configured Mod Organizer 2 instance.
---

# SPID Authoring

## Overview

SPID (Spell Perk Item Distributor, by powerofthree) is an SKSE plugin that distributes forms —
spells, perks, items, shouts, keywords, outfits, factions, AI packages — **to NPCs at runtime**,
driven by plain-text `_DISTR.ini` files. It writes nothing to plugins or saves and redistributes
from scratch each launch, so a SPID mod is trace-free to add or remove. This skill composes and
reads SPID lines, taking every form type, filter, modifier and value from `references/` — look a
token up, never invent one, because a wrong token fails silently.

**Scope.** SPID's target is NPCs. It is *best* for groups — faction, race, level, trait — and it can
also name one NPC by EditorID or FormID (`references/filters.md` §1 and §2), so a single-NPC
distribution is a real SPID line, not a refusal. For one NPC, `housecarl:skypatcher-authoring` is
usually the *better* fit, and a record's *own fields* are its job outright; a keyword onto *item
records* is `housecarl:kid-authoring`. Siblings carry the `housecarl:` prefix on a Claude Code
plugin install; a Codex flat install sees the bare folder name (`kid-authoring`).

## First step — open the grammar reference

The grammar is uniform — one line shape across all 10 form types — so open only what your task
touches:

- **`references/grammar-core.md`** — read first: the seven pipe sections, file discovery and
  distribution order, the FormID and EditorID forms, and how filters combine. Almost every task
  needs it.
- **`references/form-types.md`** — read it when you are deciding *what* is being distributed, or
  when `Form =` will not infer the type.
- **`references/filters.md`** — read it whenever targeting is in question; §3 owns what a Level
  Filter does and does not reach.
- **`references/value-tables.md`** — a lookup, not a read: open it for a skill index, a trait letter
  or a package-list type and close it again.
- **`references/index.jsonl`** — grep it to jump straight to the right section instead of reading a
  whole file.

Read grammar-core plus the one or two files you need — don't bulk-load everything.

## Workflow — compose a distribution line

1. **Identify the FormType** — *what* is being distributed. A spell → `Spell`; a potion, weapon or
   gold → `Item`; an AI package → `Package`; a custom tag → `Keyword` (SPID can create it on the
   fly). The generic `Form =` infers the type from the named record — except SleepOutfit and Skin,
   which must be named explicitly (`references/form-types.md`).

2. **Identify the target NPCs** — *to whom* — and pick the filter section(s) in
   `references/filters.md`. The inputs to have from the user before composing: the form, the group
   receiving it, and any level, chance or count bound on it.
   - by **name / EditorID / keyword / race-keyword / template** → **String** filter (position 1)
   - by **race / faction / class / combat style / voice / known spell / editor location / plugin /
     specific NPC** → **Form** filter (position 2)
   - by **level or skill** → **Level** filter (position 3)
   - by **gender / unique / summonable / child / leveled / teammate / dead** → **Trait** filter
     (position 4); trait letters are in `references/value-tables.md`

3. **Compose the line:**
   ```
   FormType = FormOrEditorID | StringFilters | FormFilters | LevelFilters | TraitFilters | CountOrPackageIndex | Chance
   ```
   - Reference the form by **EditorID** or by **`0x123~Plugin.esp`** (suffix-tilde). EditorID is
     stable across merging, ESL conversion and FormID compaction — but not against a winning
     override that **renames** the record, which is ordinary on a heavily overhauled order. Read the
     winner's EditorID before trusting the name; use the FormID when it has changed
     (`references/grammar-core.md` §6).
   - **Count the pipes** — each section is positional. Positions are 0-based everywhere in this skill
     and in `references/`: `FormOrEditorID` is position 0, String 1, Form 2, Level 3, Trait 4, Count 5,
     Chance 6 (`references/grammar-core.md` §4). So a value meant for Chance sits after six pipes, one
     meant for Count after five. Leave an unused middle section blank (`||`) or `NONE`; a trailing
     unused section can be dropped.
   - Combine filters per `references/grammar-core.md`: **OR within a section, AND between
     sections**, **exclusions (`-X`) always AND**. A union of groups **in the same section is a comma
     list on one line** — `Form = 0x123||FactionA,FactionB` is "in A **or** in B", one line, because
     within a section commas already mean OR. Write multiple lines only when the union spans
     *different* sections, which a single line cannot express: those AND together.

4. **Set Count / Index / Chance** if needed (`references/grammar-core.md`): item count or `min-max`
   range; zero-based package index; package-list type `0`–`4`; a `0`–`100` chance (default 100).
   Append `!` to the chance to make it deterministic (consistent per NPC + save across sessions).

5. **Place the file** at `Data/<name>_DISTR.ini` — the **`_DISTR` suffix is mandatory** and the file
   is flat in `Data/`, never in a per-type subfolder; without either it is never read. Files load
   alphabetically A→Z, each top-to-bottom; comment with `;`. Ship it inside a mod the mod manager
   handles, then confirm the placement with `housecarl_asset_status` (*Check the reach before you
   write it*, below).

## Two worked lines

> *"Give bandit-faction NPCs at level 20 and above the Juggernaut perk, but never a unique NPC."*

```ini
Perk = 0xBCD2A~Skyrim.esm||BanditFaction|20|-U
```

Read back: perk `0xBCD2A` from `Skyrim.esm` to any NPC **in** `BanditFaction` (Form filter, position
2 — as a String filter it would match nothing and log nothing) **and** level 20+ **and** not Unique
(`-U`). The empty String section still gets its pipe.

> *"Half of all female Nords, and only them, get my custom keyword."*

```ini
Keyword = HC_FrostTouched||NordRace||F||50
```

Read back: create keyword `HC_FrostTouched` at runtime, give it to `NordRace` NPCs (Form, position
2) who are Female (`F`, Trait, position 4), 50% of the time (Chance, position 6). Level and Count
are empty; every pipe before Chance is still written.

## Check the reach before you write it

A SPID line is written blind unless you measure the population first. Three checks, all read-only:

1. **Size the group the filter names**, before composing: `housecarl_records` with `types`, a
   `where` predicate for the faction or keyword, and `counts_only=true` — the cheap census. A
   faction or keyword test is one quantified step, and it takes either spelling: the link step
   (`Factions[*any].Faction->editorid = BanditFaction`), which resolves the target record's EditorID,
   or the **FormLink wire form** (`Factions[*any].Faction = 01BCC0:Skyrim.esm`), which compares the
   link itself. Prefer the wire form when you already hold the FormID — it needs no second
   resolution — and the link step when you only know the EditorID. Either way, resolve the field path
   with `housecarl:mutagen-reference` rather than guessing it. For a **union** of groups, one call
   does it: a comma list inside the predicate (`Factions[*any].Faction in [01BCC0:Skyrim.esm,
   0267BE:Skyrim.esm]`) censuses both at once, where one predicate per group needs a call each.
2. **Count how many of those are on a PC level multiplier**, when a Level Filter is in play:
   `housecarl_records` with the same `types`, check 1's predicate **and**
   `"Configuration.Level.LevelMult >= 0"` together in `where`, and `counts_only=true`. `where`
   predicates are ANDed, so dropping check 1's term counts every scaling NPC in the order instead
   of the group the line names. That is the population the re-distribution pass covers, not the
   population the line reaches — see *Verifying a distribution actually happened*.
3. **Prove the file is the one the game reads**, after placing it: `housecarl_asset_status` with
   `asset_paths=["<name>_DISTR.ini"]`, a Data-relative path like any other. It names the winning
   source, every source that provides it, and whether more than one contends.

**A census is a floor.** Both counts read each record's *own* local data. An NPC that inherits its
factions or stats from a template has that data masked on its own record, while SPID matches the
resolved NPC (`references/grammar-core.md` §11), so a templated actor SPID reaches can go uncounted.
Say that alongside the number rather than passing it off as the resolved population.

**Stop condition.** Quote the counts you measured and no others; never state a reach you did not
measure. **What the surface cannot do:** nothing reports the set a *composed line* reaches — SPID's
own parse of the seven sections, trait letters and chance (issue #614) — and nothing checks that a
drafted `_DISTR.ini` parses offline: `housecarl_skse` says outright that distributor INIs in `Data\`
root are covered by no family, and no issue is filed for a SPID parse check.

## Bundled-or-warn, and the version window

The `references/` corpus documents SPID **7.3.0**. If a form type, filter, trait or behaviour is not
in it, **say so — do not fabricate a plausible token.** There is no backstop on this path: no
houseCARL tool reads a `_DISTR.ini`, and SPID skips an unparseable line, an unknown filter term or
an unresolvable form with no error, so a guessed token yields a file that quietly distributes
nothing. The only checks that exist are the three under *Check the reach before you write it*.

The window runs **both** ways. Above 7.3.0 the corpus may be behind — surface that and offer to
re-derive. **Below 7.3.0 a documented feature may not exist yet**: `references/filters.md` marks the
`Actor [ACHR]` Form filter *(added in 7.3)*, and on an older install that line is skipped silently.
Read the installed version before relying on a dated feature: `housecarl_skse` with
`findings='inventory'` and `filter=` the SPID DLL name reports what the DLL's static version
manifest declares — name, author, version — without loading it. That is what the file declares, not
what it does; treat it as the version, not as proof of behaviour.

**When the SKSE manifest and MO2's `meta.ini` disagree about SPID, `meta.ini` is the running
version. [source]** SPID's manifest carries only the major digit: `v.PluginVersion(Version::MAJOR)`
in the `SKSEPlugin_Version` export (`SPID/src/main.cpp`, SPID 7.3.0, commit `31e76d3`; unchanged at
7.3.3), where `Version::MAJOR` is `PROJECT_VERSION_MAJOR` off `set(VERSION 7.3.0)` in
`SPID/CMakeLists.txt`. Minor and patch are never written, so **every** SPID 7.x release declares
7.0.0 to SKSE. The full number does exist in the binary, in its Win32 version resource —
`FILEVERSION @PROJECT_VERSION_MAJOR@, @PROJECT_VERSION_MINOR@, @PROJECT_VERSION_PATCH@, 0`
(`SPID/cmake/version.rc.in`) — which is where `7.3.1.0` comes from and why it matches what MO2
recorded. So `housecarl_skse` reporting 7.0.0 is a truthful read of a lossy export, not a misread
(houseCARL issue #667): treat its version as a **major-version floor only**, and take the minor and
patch from `meta.ini`. A 7.3-only token like the `Actor [ACHR]` Form filter is gated on the
`meta.ini` number; the manifest cannot answer that question at all. Quote both sources with their
names when they differ, and say which one you gated on.

## Common mistakes, and the rule that replaces each

- **Count the pipes before you save.** The sections are positional; a chance written one pipe early
  lands in CountOrPackageIndex and silently changes meaning. Keep blank middle sections (`||`).
- **Write the FormID suffix-tilde: `0x123~Plugin.esp`.** SkyPatcher's prefix-pipe
  `Plugin.esp|0x123` is one keystroke apart and will not resolve here — the failure is silent.
- **Name SleepOutfit and Skin explicitly.** Both reuse records another type claims first
  (OTFT→Outfit, ARMO→Item), so `Form =` never infers them.
- **Keep a package FormList pure.** A FormList under `Package` containing non-Packages will most
  likely crash the game (`references/form-types.md`).
- **One modifier per String or Form expression.** `-Guard+ActorTypeNPC` is invalid; Traits is the
  only section that allows mixing (`F/-U/L`).
- **End the filename in `_DISTR` and keep it flat in `Data/`.** A file without the suffix, or filed
  into a per-type subfolder, is ignored entirely — no line in it ever runs.
- **Map "match by X" onto one of the four filter sections** (text → String, form → Form, level/skill
  → Level, trait → Trait) and look it up in `references/filters.md`. There are exactly four; a filter invented
  by analogy does not exist and takes the whole line down with it.

## Verifying a distribution actually happened

A parsed `_DISTR.ini` and a clean SPID log are **not** proof that an NPC got the thing. The log shows
what SPID looked up; only a live actor shows what SPID applied.

- **SPID does not distribute to the player.** The on-load path is gated on an explicit `!IsPlayer()`
  check and the other paths into `Distribute()` exclude the player by their own guards, so asserting
  on `Game.GetPlayer()` reports failure for a rule that is working perfectly. No filter is needed and
  none exists — sample NPCs instead (`references/grammar-core.md` §3). A written-out player
  exclusion, `-0x7~Skyrim.esm`, is **legal but inert**: it parses as an ordinary Form exclusion and
  changes nothing, because the player was never in the set. Leave it out rather than carrying a term
  that reads like it is doing work; if it is already there, say it is harmless, not wrong.
- **A newly added rule reaches old saves, but not mid-session.** SPID distributes from scratch each
  launch and writes nothing into the save, so actors in a pre-existing save do pick up a rule added
  afterwards. The INIs are read **once per game launch**, though: a rule added while the game runs
  needs a restart, and the actor has to load in that new session.
- **A dynamic keyword IS reachable.** A keyword SPID creates at runtime has no plugin FormID —
  nothing in xEdit to point at — but it is in the game's keyword array, so `Keyword.GetKeyword()`
  finds it by name, which is what makes a no-ESP keyword distribution verifiable from script at all.
  For the signature use `housecarl:papyrus-reference`; the SPID side is in `references/form-types.md`.
- **Chance skips actors by design.** A `Chance` below 100 is *supposed* to miss some NPCs, so two
  sampled actors without the form is an ordinary outcome for `||||||50` — and the roll is re-made
  each session unless `!` pins it per NPC. Set Chance to 100 before calling a rule failed.
- **A Level Filter narrows by the NPC's own level; it does not narrow to auto-levelled NPCs.** It is
  checked against every NPC the line otherwise matches. The separate re-distribution pass covers
  only NPCs on a PC level multiplier and exists to re-evaluate them as the player levels — it does
  not shrink the target set. `references/filters.md` §3 owns this with its source citation; check 2
  under *Check the reach before you write it* counts the difference.
- **Sample somewhere NPCs must be** — testing practice, not a SPID rule. An interior cell with known
  occupants beats an exterior spawn marker: finding no NPC in scan range there is inconclusive, not
  a failing distribution.

## Notes

- **Provenance.** The `references/` corpus is reconstructed from "SPID: The Complete Reference"
  (Nexus article 6617, SPID 7.3.0) and cross-checked against the MIT source
  (`powerof3/Spell-Perk-Item-Distributor`). On a SPID version bump, re-derive before trusting it for
  new features.
- **Lookup without authoring.** The same reference answers "what's the skill index for Destruction",
  "what trait letter is Player's Teammate", or "what are the package-list types" — open
  `references/value-tables.md`; no line needs to be written.
- **Comment syntax** is standard INI `;` (SPID reads configs via the CSimpleIniA library); the
  corpus flags this as a library default rather than a SPID-specific guarantee.
