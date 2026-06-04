# SkyPatcher reference corpus — status & provenance

Build-time record for the `skypatcher-authoring` reference corpus. Reading order for a session
picking this up: this file → `grammar-core.md` → the relevant `records/<type>.md`.

## What this is

The bundled grammar reference for the `skypatcher-authoring` skill. It documents SkyPatcher's
INI patch-string grammar — every filter, operation, value enumeration, and worked example —
reconstructed from SkyPatcher's official Nexus documentation into a consistent, lookup-friendly
form. The eventual `SKILL.md` (not yet authored — see "Layering") drives lookups against it.

## Source & version

- **Mod:** SkyPatcher by Zzyxz — Nexus SE #106659. **Version: 6.4.1** (`SkyPatcher - AE-106659-6-4-1`).
- **Primary source:** the mod's **Articles tab** (`nexusmods.com/skyrimspecialedition/mods/106659?tab=articles`).
  Nexus 403s automated fetches (Cloudflare), so Aaron manually downloaded all 30 articles as
  HTML on **2026-06-02**.
- **Raw capture lives at** `dev/references/Skypatcher/`:
  - `Articles/*.htm` — the 30 saved article pages (source of truth).
  - `_extracted/*.md` — clean text extracted by `_extract_articles.py` (article `<article>`
    body only, chrome/comments stripped, nbsp normalized). This corpus is reconstructed from
    those.
  - `SkyPatcher - AE-106659-6-4-1/.../SkyPatcher.ini` — the shipped global config, used as the
    ground-truth list of patchable record types and global settings.
  - `SkyPatcher - Starter Setup-…/` — the shipped subfolder scaffold, used to confirm the
    canonical record-type → directory names and casing.

## Coverage

| | |
|---|---|
| Record-type patchers in `SkyPatcher.ini` | **28** |
| Record types with an official article (documented here) | **27** |
| Foundational cross-cutting articles (→ `grammar-core.md` + `value-tables.md`) | **3** |
| **Gap** | **Object Modification (OMOD)** — see below |

The 3 foundational articles ("Read first – General Information", "How to use Filters",
"FormID – EditorID") were synthesized — together with the universal patterns observed across
all 27 per-record articles — into `grammar-core.md` and `value-tables.md`.

## The OMOD gap (decision: documented, not filled)

`iEnableObjectModificationPatching=1` ships enabled in 6.4.1, but **OMOD has no documentation
article** and **no source file in SkyPatcher's public repo** — the public source is behind the
released DLL. The author's Fallout 4 sibling tool, `Zzyxz/RobCo-Patcher`, *does* carry the same
patcher in its source, so OMOD grammar is derivable from there — but FO4-flavored and not
verified against SkyPatcher's Skyrim port.

**Aaron's call (2026-06-02): document as a clean gap.** The corpus stays 100% SkyPatcher-verified;
`records/object-modification.md` records the gap and the RobCo-Patcher lead for a possible future
fill. Do not invent OMOD grammar.

## Source-repo cross-check

`Zzyxz/SkyPatcher` (GitHub, branch `main`, **no license**) was read for grammar facts only — no
code vendored. It confirmed: the record-type set (27 `.cpp` files matching the articles), that
**Race Hook** is real (handled by `RACE::readConfigHook`, with its own article + `raceHook/`
folder), and that **OMOD is absent** from the public source. Treat the repo as behind the DLL.

## Structure

```
references/
├── _CORPUS_STATUS.md          ← this file
├── grammar-core.md            ← filesystem, addressing, filter system, operation conventions, global INI
├── value-tables.md            ← shared enums (cast type, actor values, biped slots, archetypes, …)
├── index.jsonl                ← lookup routing: record type → reference file (generated last)
└── records/
    ├── npc.md  weapon.md  armor.md  …  (27 documented types)
    └── object-modification.md  ← gap note
```

## Confidence

- **High** — content is reconstructed from the author's own official docs; every worked example
  is preserved verbatim from the source article (examples are the highest-value, copy-paste-ready
  content). Signatures (xEdit sigs) and subfolder names are cross-checked against the shipped INI,
  the starter-setup scaffold, and the source repo.
- **Known soft spots** — the source articles contain occasional author typos and copy-paste
  artifacts (e.g. an example mislabeled `filterByWeapons` inside the Soul Gem article). Where a
  record file's examples look inconsistent, the *primary filter from §2 of grammar-core* and the
  operation name are authoritative; a stray example plugin/path is illustrative only.
- **Gap** — OMOD, as above.

## Layering (build plan)

- **Layer 1 (this corpus) — DONE pending review.** The reference files above.
- **Layer 2 — not started.** `SKILL.md` (the lookup + bundled-or-warn playbook, modeled on
  `papyrus-reference`), `evals/` (trigger + author-output eval sets), and a final §8 reviewer
  walk per the `skill-authoring` standard. Author Layer 2 only after Aaron reviews Layer 1.
