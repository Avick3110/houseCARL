# SPID reference corpus — status & provenance

Build-time record for the `spid-authoring` reference corpus. Reading order for a session picking this
up: this file → `grammar-core.md` → `form-types.md` / `filters.md` → `value-tables.md`.

## What this is

The bundled grammar reference for the `spid-authoring` skill (part of the 5-skill Skyrim
distributor-framework cluster — sibling to `skypatcher-authoring`). It documents SPID's `_DISTR.ini`
distribution grammar — the line syntax, every form type, every filter section and modifier, the
count/index/chance fields, and the runtime ordering — reconstructed into a consistent, lookup-friendly
form. The skill's `SKILL.md` drives lookups against it, routed through `index.jsonl`.

Unlike SkyPatcher (one grammar *per record type*, hence a `records/` dir), SPID has **one grammar**
that applies across 10 form types. So the corpus is shaped as: `grammar-core` (the line + mechanics) +
`form-types` (what you distribute) + `filters` (whom you target) + `value-tables` (the flat enums) —
mirroring the sibling's `grammar-core` + `value-tables` spine, with form-types/filters standing in for
the per-record files.

## Source & version

- **Mod:** Spell Perk Item Distributor (SPID) by powerofthree (powerof3) — Nexus SE #36869.
  **Version: 7.3.0** (release 2026-05-09).
- **Primary source:** the single canonical article **"SPID: The Complete Reference"**
  (`nexusmods.com/skyrimspecialedition/articles/6617`), which covers the entire grammar end-to-end.
- **Raw capture lives at** `dev/references/SPID/`:
  - `_extracted/spid-complete-reference-6617.md` — the **verbatim article text** (source of truth).
    Captured 2026-06-02 via Claude in Chrome (Nexus 403s automated fetches; the browser path beats
    Cloudflare). All 32 collapsed `bbc_spoiler` example blocks were force-revealed before extraction,
    so every worked example is included.
  - `source-notes.md` — the **MIT-source cross-check** (`powerof3/Spell-Perk-Item-Distributor`,
    branch `master`), used to resolve what the article shows only by example. Grammar facts only; no
    code vendored.

## Coverage

| | |
|---|---|
| Grammar sections in the article | **fully documented** (load/order, distribution timing+order, line syntax, form types, type inferring, all 4 filter sections + modifiers, count/index, chance + deterministic, templated NPCs) |
| Distributable form types | **10 / 10** (`form-types.md`) |
| Filter sections | **4 / 4** (`filters.md`) |
| Flat enums | skill indices, trait letters, package-list types, form signatures, distribution order, defaults (`value-tables.md`) |
| **Gaps** | **The article does not cover verification behaviour.** Five details a user hits when checking whether a rule landed — player exclusion, the once-per-launch config read, the runtime `SPID_Processed` marker, the dynamic keyword's missing plugin FormID, and its reachability by name — are absent from article 6617 and were resolved from source (marked **[source]**; see Confidence). Two other article-*silent* details (comment syntax, whitespace tolerance) came from source the same way. Expect further gaps outside the article's grammar scope. |

## Confidence

- **High.** Content reconstructed from the author's own "Complete Reference" article; every worked
  example is preserved verbatim from the source (examples are the highest-value, copy-paste-ready
  content). Form/filter signatures match the article's xEdit-signature tables.
- **Source-resolved beyond the article** (the no-guesswork wins, all in `source-notes.md`):
  - **Trait letter for Player's Teammate = `T`** — the article shows trait letters only by example and
    never states this one. Confirmed from `TraitsFilterComponentParser`.
  - **`-F` ≡ `M`, `-M` ≡ `F`** (binary-sex aliasing) — not in the article.
  - **Whitespace around `|` and `,` is stripped**, **`" - "` ⇒ `~`** (xEdit paste form), and FormID
    zero-padding is forgiven — from `sanitize()`.
  - **Chance `!`** deterministic flag and **Level `w`** weight prefix — confirmed in the parsers.
  - **The player is not distributed to** — `should_process_NPC` in `DistributeManager.cpp` gates the
    on-load path on `!IsPlayer() && !IsDeleted()`; the PC-level-mult hooks require an already-processed
    NPC and the death path carries its own `!IsPlayerRef()` guard. All `Distribute()` call sites were
    read (`DistributeManager.cpp`, `DistributePCLevelMult.cpp`, `DeathDistribution.cpp`). Not in the
    article.
  - **Configs are read once per game launch** (`main.cpp`, `kPostLoad`/`kDataLoaded`), so a rule added
    mid-session needs a restart. Not in the article.
  - **The "already handled this NPC" marker is a runtime keyword, not a persisted one** — `Setup()` in
    `DistributeManager.cpp` creates `SPID_Processed` via `IFormFactory` each launch (declared in
    `DistributeManager.h`), and `distribute_on_load` skips an NPC that already carries it. The
    article states the from-scratch-per-launch behaviour; the mechanism behind it is source-only.
  - **A dynamically created keyword has no plugin FormID but is pushed into the game's keyword array**
    (`FormData.h`, `kCreateIfMissing`). The other half — that SKSE's `Keyword.GetKeyword` resolves a
    name against that same array — is read from SKSE's own source (`ianpatt/skse64`,
    `skse64/PapyrusKeyword.cpp`), not inferred; its one-shot, never-invalidated cache is recorded as a
    caveat in `form-types.md`. The article documents dynamic creation but neither consequence.
- **One residual confidence caveat:** comment syntax. SPID parses configs via **CSimpleIniA**
  (source-confirmed); the **`;`** line-comment character is CSimpleIni's documented *library default*
  rather than a SPID line of code we read. Stated in the corpus as "standard INI `;` comments (via
  CSimpleIniA)" — grounded, not invented, but a notch below the byte-confirmed facts.
- **Known article quirk** (preserved + flagged, not corrected): the skill-index section says
  "17 skills" but lists indices 0–17 (18 rows). Valid indices are 0–17; the "17" is an author slip.

## Source-repo cross-check

`powerof3/Spell-Perk-Item-Distributor` (GitHub, `master`, **MIT License**) was read for grammar facts
only — no code vendored. Files consulted (all `SPID/src/`): `LookupConfigs.cpp` (`sanitize()`, parser
chain), `LookupConfigs.h` (`TraitsFilterComponentParser`, `ChanceComponentParser`,
`LevelFiltersComponentParser`), `Defs.h` (`Traits` struct), `DistributeManager.cpp`
(`detail::should_process_NPC`, `Setup()`, the load hooks), `DistributeManager.h` (the `processed`
keyword declaration), `DistributePCLevelMult.cpp` and `DeathDistribution.cpp` (the remaining
`Distribute()` call sites and their guards), `Distribute.cpp` (the call-site inventory),
`FormData.h` (`kCreateIfMissing`), `main.cpp` (config read and lookup timing). The
verification-behaviour files were read at commit `6e66908` (`master`, 2026-09-02); the earlier parsing
files at `master` as of 2026-06-02. MIT (unlike SkyPatcher's unlicensed repo) would permit vendoring,
but the corpus documents grammar, it doesn't embed source.

One fact reaches outside SPID's repo: **SKSE** (`ianpatt/skse64`, `master` `4cd2e34`, read 2026-09-05)
— `skse64/PapyrusKeyword.cpp`, for how `Keyword.GetKeyword` resolves a name. Cited in `form-types.md`
so the by-name-reachability claim does not span an unread seam.

## Structure

```
references/
├── _CORPUS_STATUS.md   ← this file
├── grammar-core.md     ← what SPID is, file discovery + load/distribution order, the line syntax,
│                          input normalization, filter-combination logic, type inferring,
│                          CountOrPackageIndex, Chance + deterministic, templated-NPC reachability
├── form-types.md       ← the 10 distributable form types + signatures + special cases
├── filters.md          ← the 4 filter sections (String / Form / Level / Trait) in depth
├── value-tables.md     ← flat enums (skill indices, trait letters, package-list types, signatures, …)
└── index.jsonl         ← lookup routing: one line per topic, mapping aliases to the file that answers
```

**When you add a fact to this corpus, add or update its `index.jsonl` line too** — a fact the router
cannot reach is a fact the skill will not find.

## Layering (build plan)

- **Layer 1 (this corpus) — DONE pending review.** The five reference files above. Built only after
  the complete reference was in hand (article captured + source cross-checked) — the project's
  no-guesswork gate.
- **Layer 2 — done.** `SKILL.md` (the lookup + bundled-or-warn playbook, modeled on
  `papyrus-reference`), `index.jsonl` (routing kept consistent with the `skypatcher-authoring`
  sibling), and `evals/` (trigger + author-output eval sets per HOUSECARL_SKILL_AUTHORING.md
  §6.4/§6.5) all ship. The skill is live as `/housecarl:spid-authoring`.

## Cluster note

`spid-authoring` is one of the 5 distributor skills. Cross-tool divergences to carry into the routing
skill + SKILL.md: SPID's FormID is **suffix-tilde** `0x123~Plugin.esp` (SkyPatcher is prefix-pipe);
SPID files are **flat `Data/*_DISTR.ini`** (SkyPatcher uses per-type subfolders); SPID **distributes
forms to NPCs** (SkyPatcher modifies records in place). SPID and KID share an author (powerofthree) and
grammar idioms — coordinate idiom wording with `kid-authoring` when that skill is built.
