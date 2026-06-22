---
name: papyrus-reference
description: Look up Papyrus function signatures, parameter types, return types, flags, and documentation from bundled reference files when reading or writing .psc scripts. Bundled corpus covers vanilla Skyrim + SKSE + ~45 popular SKSE-plugin sources (PapyrusUtil, JContainers, MCMHelper, RaceMenu, PO3 Papyrus Extender, ConsoleUtil, and others). Use when the user is editing a Papyrus script, adding or modifying ANY function call in a .psc (even one copied from elsewhere in the same file), investigating a function call, looking up an SKSE plugin function, calling JContainers / PapyrusUtil / SkyUI / MCM scripting APIs, debugging a Papyrus compile error with unknown-identifier or type-mismatch errors, asking what a Papyrus function or event does, or checking a parameter type before authoring a script or event handler. Load this before ANY .psc read OR edit — including an edit that only reuses a call already in the file (a copied call is not a verified call, and "the compiler will catch it" covers signatures, not semantics — animation-event strings, ActorValue names, flags/latency). Also load it before answering any "what does this function do" or "what parameters does X take" question; if the function isn't in the bundle, this skill's bundled-or-warn flow surfaces an explicit "no reference for X" message so Claude never invents a signature.
---

# Papyrus Reference

## Overview

This skill provides offline lookup for Papyrus function signatures, parameter shapes, return types, flags, and doc-comments. The reference corpus is generated from BellCube's [papyrus-index](https://github.com/BellCubeDev/papyrus-index) and ships in this skill's `references/` tree. The body walks the lookup procedure, the surgical-read pattern that uses each index entry's `line_start`/`line_end` to read only the relevant entry block from its source file, the bundled-or-warn fallback when a function isn't in the corpus, and the tier strategy that gates SKSE-plugin sources by modlist content.

This skill covers the **API surface** — function signatures and docs. Reading actual `.psc` source from a modlist, and compiling `.psc` → `.pex`, are separate concerns handled with your normal file-reading and compiler tooling.

For the compiles-clean-but-misbehaves traps a correct signature does NOT reveal — `GetForm` returning `None` for the whole ESL range, `SendModEvent`'s 4-arg handler arity, `FormList.HasForm` missing base-`NPC_` entries, mixed storage backends, `Utility.Wait` in a paused menu, literal/docstring escaping, common-noun type collisions — see [references/silent-biters.md](references/silent-biters.md). It is a hand-curated companion, separate from the by-construction signature corpus.

## First step

When you encounter a Papyrus function call you need to verify, or when authoring a `.psc`, open the function index at `references/index.jsonl`. The index is JSONL — one entry per line — and resolves an unqualified function name to the per-script reference file that documents it AND the 1-indexed `line_start`/`line_end` range of the entry block within that file. The index is grep-friendly: the entries are compact JSON (no spaces after colons), so match the **full quoted token** `"name":"FunctionName"` — a spaced pattern like `"name": "…"` matches nothing.

Once you have the matching index entry, do **NOT** read the whole reference file — use the entry's `line_start`/`line_end` to read just the entry block via your file-reading tool's line-range (offset/limit) capability. The whole-file load is typically 10-300 KB; the targeted block read is 200-1500 bytes. Don't bulk-load the index either; it is ~1.5 MB and that defeats the per-session token economics. Use targeted reads throughout.

## Reasoning framework / Lookup procedure

1. **Identify the unqualified function name** from the call site — for `Self.GetActorValue("Health")` the name is `GetActorValue`; for `StringUtil.Substring(str, 0, 4)` the name is `Substring`.

2. **Look up the name in `references/index.jsonl`.** Match the **full quoted token** — `"name":"<FunctionName>"`, closing quote included — so the hit is exact and field-scoped. The index is **compact JSON** (no spaces after colons), so a spaced pattern like `"name": "…"`, or one missing the leading quote like `name:"…"`, matches **zero lines** even for a function that is present — a false "absent" indistinguishable from a true miss. If a lookup you expect to hit returns nothing, validate the pattern against a guaranteed-present token first (`"name":"OnInit"` resolves to several entries); only trust a zero-result as "not in the corpus" once the method itself is proven. There are three result shapes:

   - **Single match.** Pull the entry's `file`, `line_start`, and `line_end`. Confirm signature (return type, parameter types, default values), flags (`Native` / `Global` / `Hidden` / `BetaOnly` / `DebugOnly`), and doc-comment if present via the block read in step 3.
   - **Multiple matches across sources.** Use the source qualifier from the call site to disambiguate. For unqualified calls inside a script body, disambiguation hinges on the calling script's `extends` chain — `Self.GetActorValue(...)` inside a script extending `Actor` resolves to `Actor#GetActorValue`. For global calls, the `Script.Function` qualifier in the source is authoritative: `StringUtil.Substring` resolves to the entry whose `qualified` field is `StringUtil.Substring`.
   - **No match in the bundled index.** Proceed to the "Bundled-or-warn" section below.

3. **Block-read the resolved reference entry.** Read only lines `line_start`..`line_end` from the entry's `file` (use your file tool's offset/limit). This returns just the entry's lines — typically 8-30 lines for a function block. Do **NOT** read the whole reference file; a whole-file read on `Actor.md` (303 functions, ~80 KB) or `papyrusutil.md` (~140 KB multi-script source) costs 25-50× more tokens than the block read needs. Confirm parameters in order, types, default values, return type, flags. About 51% of functions carry doc-comments; the other 49% surface signature-only entries — that's normal, not a sign of missing data.

4. **For event lookups, property lookups, and struct member lookups,** the same index handles them — the `kind` field discriminates (`global` / `instance-method` / `event` / `property`). The per-script reference file groups them under `## Events`, `## Properties`, and `## Structs` sections.

## Bundled-or-warn — never invent a signature

When the bundled index has no match for the requested function:

1. **Emit an explicit warning to the user.** Use this shape:

   ```
   No Papyrus reference for `FunctionName` — not present in the bundled corpus.
   I will not invent a signature.

   Options to proceed:
   - Verify the function name spelling (typos are the #1 cause of bundled-miss)
   - Check whether the function lives in an SKSE plugin not in the bundled set
     (~45 popular plugins are covered; brand-new or niche plugins may not be)
   - Investigate via Creation Kit / Papyrus modding forums / the mod's shipped
     source if it includes .psc files
   - Author a custom reference file following this skill's index + block shape
   - Skip the function call if it's not load-bearing for the current task
   ```

2. **Never invent a signature.** A confidently-asserted but wrong signature can cause the user to ship code that compiles but misbehaves at runtime, or to file a compile failure as an upstream bug when it's actually an authoring error — both are recoverable only through painful debugging the user shouldn't have been put through. A clear non-answer beats a confident wrong one.

## Function index

The index lives at `references/index.jsonl`. Entries are **compact JSON** — one per line, no spaces after colons (match with a full quoted token like `"name":"GetActorValue"`, never a spaced `"name": "…"`). Per-entry shape:

```json
{"name":"GetActorValue","qualified":"Actor#GetActorValue","source":"vanilla","file":"references/vanilla/Actor.md","kind":"instance-method","line_start":721,"line_end":732}
{"name":"Substring","qualified":"StringUtil.Substring","source":"skse","file":"references/skse/StringUtil.md","kind":"global","line_start":131,"line_end":145}
{"name":"PushString","qualified":"PapyrusUtil.PushString","source":"papyrusutil","file":"references/papyrusutil.md","kind":"global","requires_plugin":"PapyrusUtilSE.dll","line_start":2610,"line_end":2622}
```

Fields:

- `name` — unqualified function/event/property name. This is the primary lookup key.
- `qualified` — `Script.Function` for global functions, `Script#Method` for instance methods, `Script.Event` for events, `Script.Property` for properties. Use this to disambiguate when `name` collides across sources.
- `source` — BellCube source directory (`vanilla`, `skse`, `papyrusutil`, `jcontainers`, etc.).
- `file` — relative path to the per-script reference markdown, rooted at the skill folder.
- `kind` — one of `global`, `instance-method`, `event`, `property`. Property entries appear in `## Properties` sections; events in `## Events`; functions split by `isGlobal` flag into `## Global Functions` vs `## Functions`.
- `line_start` / `line_end` — 1-indexed inclusive line range of the entry's block within `file`. Use these for the targeted block read. Typical block sizes: properties 6-10 lines, events 5-15 lines, functions 8-30 lines.
- `requires_plugin` (Tier 2 only) — the SKSE plugin DLL name that gates this entry's availability. Omitted for Tier 1 vanilla + skse entries (always available).

## Tier strategy

The corpus ships in three tiers. All bundled entries are present in `references/index.jsonl` regardless of tier; tier filtering happens at lookup time, not at install time.

- **Tier 1 — always available.** `vanilla/` (~25-30 scripts: `Actor`, `Form`, `ObjectReference`, `Quest`, `Game`, `Utility`, `Debug`, etc.) + `skse/` (~15-20 scripts: `StringUtil`, `Math`, `Input`, SKSE-additions to base types). These ship with any Skyrim install + SKSE, so there's no useful gating. Their index entries omit `requires_plugin`.

- **Tier 2 — modlist-gated.** ~45 popular SKSE plugin sources (`PapyrusUtil`, `JContainers`, `RaceMenu`, `MCMHelper`, etc.). Each Tier 2 entry's index row carries `requires_plugin` — the SKSE plugin DLL filename.

  When you look up a function and the matching index entries are all Tier 2, confirm the gating plugin is present in the user's **active load order** before recommending the function — read the load order / active-plugins list if you have filesystem access to it, otherwise ask the user. If the plugin isn't active, fall through to the "Bundled-or-warn" path: the function exists in the bundle, but the user's modlist doesn't include the plugin that provides it.

- **Tier 3 — omitted.** Fallout 4 and Starfield sources are not included in the Skyrim corpus.

## Common mistakes

- **Inventing a function signature when the bundled index has no match.** Surface the explicit warning instead. If the user pushes back ("just guess"), explain that a wrong signature can cause silent runtime misbehavior or false-positive bug reports, and ask them to confirm via CK or source before proceeding.

- **Writing the index grep with spaces after colons, or dropping the leading quote.** Entries are compact JSON — `"name":"Dispel"`, never `"name": "Dispel"`. A spaced pattern (`"name": "`) or a quote-dropped one (`name:"`) matches **zero lines** for functions that are present, and that format-induced zero-match is indistinguishable from a true "not in corpus" — so it silently routes a present function into the bundled-or-warn path, the exact failure this skill exists to prevent. Match the full compact token (`"name":"Dispel"`); when an expected hit returns empty, suspect the pattern before the corpus.

- **Looking up by qualified name when only unqualified is provided.** The index's `name` field is unqualified — `Substring`, not `StringUtil.Substring`. Use `name` for the primary lookup and `qualified` only for disambiguation when multiple entries share a `name`.

- **Treating a missing doc-comment as a missing function.** About 49% of the corpus carries signature-only entries (no doc-block in the source `.psc`). The function exists and the signature is authoritative — there just isn't a doc-comment. Don't fall through to "Bundled-or-warn" on missing docs alone.

- **Loading a per-source reference file when the index pointed somewhere else.** Always trust the index's `file` field. If the user says "look up `Foo` in `Actor.md`" but the index resolves `Foo` to `references/skse/Form.md`, the index is authoritative — investigate the discrepancy before guessing.

- **Reading the whole reference file instead of block-reading via `line_start`/`line_end`.** Every index entry carries an inclusive 1-indexed line range pointing at the entry's block. A whole-file load on `Actor.md` is ~80 KB and pulls in 300+ unrelated entries — pure token waste. Only fall back to a whole-file read if the entry block is malformed (extremely rare; would indicate a corpus-generation bug worth reporting) or if you genuinely need cross-entry context like the script's `Extends` header.

- **Skipping the Tier 2 plugin-active check for a script-context lookup.** A `PapyrusUtil.PushString` call only makes sense if `PapyrusUtilSE.dll` is active. If the user's modlist doesn't include it, the call won't resolve at runtime — the bundled-or-warn path is the correct response, not "here's the signature, use it."

- **Conflating events with functions.** Events have `kind: "event"` in the index and live in `## Events` sections. Their signatures (`Event OnInit()`, `Event OnEffectStart(Actor akTarget, Actor akCaster)`) look like function signatures but they're invoked by the engine, not called. If a user asks "how do I call `OnEffectStart`," that's a confused question — surface that events are engine-driven, not user-called.

## Notes

- **Corpus provenance** — the `references/` tree is generated from BellCube's [papyrus-index](https://github.com/BellCubeDev/papyrus-index). To update coverage (e.g. a newly-released plugin), regenerate from upstream and refresh `references/` + `index.jsonl`.

- **Hand-curated companion** — `references/silent-biters.md` is NOT part of the BellCube-generated corpus (it carries semantic gotchas, not signatures) and is never resolved by the index lookup. Preserve it across any regeneration of `references/` + `index.jsonl`; it carries a hand-maintained-staleness duty.

- **Authoring custom reference files** — for plugins not in the bundled corpus, you can hand-author a per-script reference file plus matching `index.jsonl` entries following the same `name` / `qualified` / `source` / `file` / `kind` / `line_start` / `line_end` shape this skill uses. The lookup procedure above then resolves them identically.
