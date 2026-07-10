---
name: skypatcher-authoring
description: Author or interpret SkyPatcher INI patches — runtime, no-ESP record edits that filter Bethesda records and set/add/remove their properties — using the bundled SkyPatcher grammar reference rather than invented syntax. Use when the user wants to write or fix a SkyPatcher patch or `.ini`, buff/nerf/rebalance weapons or armor, change NPC stats/factions/inventory, add or remove keywords, edit a leveled list or container contents, retune spells/enchantments/potions, unlevel encounter zones, tweak cell lighting or fog, disable a placed reference — or asks what an existing SkyPatcher INI does, which records or NPCs it affects, why a patch line isn't applying, to audit or review a mod's INI patches, which filters or operations a record type supports (filterByWeapons, filterByNpcs, attackDamage, keywordsToAdd…), or which subfolder a `.ini` belongs in. Load this before writing or reading any SkyPatcher line — interpreting a patch needs the same non-obvious grammar as writing one (the `Plugin.esp|FormID` and EditorID addressing, per-record filter and operation names), and a misread line silently changes what you think it edits.
---

# SkyPatcher Authoring

## Overview

SkyPatcher is an SKSE plugin that edits Bethesda records **at runtime** from plain-text INI
files — no ESP/ESL is produced for the edit itself. This skill composes correct SkyPatcher
patch strings and places the INI file, drawing every filter, operation, value, and example
from the bundled grammar reference in `references/`.

It is a **procedural** skill (a patch-authoring playbook) with a **reference-lookup** step at
its core — the same lookup-and-never-invent discipline `mutagen-reference` and `papyrus-reference`
use, because a fabricated SkyPatcher token fails silently (see "Bundled-or-warn").

**Scope boundary — what this is *not*:**
- **Record field schema** (what fields a WEAP record has at the Mutagen/xEdit level, their types,
  writability) → that's the `mutagen-reference` skill. This skill covers SkyPatcher's *own* filter
  and operation names, which differ from the underlying field names.
- **Distribution frameworks** — SPID (forms→NPCs), KID (keywords→items), CID (items→containers)
  are *separate INI frameworks* with different syntax and file locations. SkyPatcher *edits
  records*; it does not distribute. If the user names SPID/KID/CID or asks to "distribute" rather
  than "edit/patch," this is the wrong skill.

## First step — open the grammar reference

When composing or fixing any SkyPatcher patch, open the reference before writing a line:

1. **Resolve the record type** via `references/index.jsonl` — one JSON entry per line, grep-friendly
   on `name`, `sig` (xEdit signature), `primaryFilter`, `subfolder`, or `aliases`. It maps the type
   to its reference file.
2. **Read `references/grammar-core.md`** — the shared syntax (patch-string structure, `Plugin|FormID`
   vs EditorID, the filter system, operation conventions, file placement, conflict resolution).
3. **Read the matched `references/records/<type>.md`** — that type's specific filters, operations,
   flags, and worked examples.
4. Shared enums (cast types, actor values, biped-slot index, archetypes, soul types) live in
   `references/value-tables.md`; record files point there.

Read grammar-core once plus the one record file you need — don't bulk-load every record file.

## Workflow — compose a patch

1. **Identify the record type** from the user's goal: buff a sword → Weapon (`weapon/`, WEAP);
   retune a potion → Alchemy/Ingestible (`ingestible/`, ALCH); add loot to a chest → Container.
   Grep `index.jsonl` for the type, signature, or an alias. A type that isn't in the index → go to
   "Bundled-or-warn."

2. **Look up the grammar** — `grammar-core.md` for the mechanics, `records/<type>.md` for the
   filters and operations that type actually supports. Availability varies per record, so the
   record file is authoritative; don't assume a filter/op exists by analogy with another type.

3. **Compose the patch string** as `filter(s) : operation(s)`:
   - Pick a filter that selects the records — the primary filter by form (`filterByWeapons=…`), or
     a cross-cutting one (`filterByKeywords`, `filterByEditorIdContains`, `filterByModNames`).
   - Address forms as `Plugin.esp|FormID` (copy the full FormID from xEdit/CK) or by EditorID —
     except the FormID-only operations listed in `grammar-core.md` §4.
   - Chain segments with `:`, list multiple values with `,`, pack compound values with `~`
     (e.g. `mgefsToAdd=Form|id~Magnitude~Duration~Area`).
   - Rename with `fullName=~New Name~`; clear a form field with `null`; multiply with `…Mult`.

4. **Place the INI** at `Data/SKSE/Plugins/SkyPatcher/<subfolder>/<file>.ini` — the `<subfolder>`
   comes from the index. Name it `SomePlugin.esp.ini` to auto-gate on that plugin being active in
   the load order, or a plain name to always load. Nest plugin-named INIs in a mod-specific
   subfolder (`SkyPatcher/npc/MyMod/Skyrim.esm.ini`) so a mod manager can't overwrite a same-named
   file from another mod (`grammar-core.md` §2).

5. **Write the file and confirm** the subfolder, the filename's load behavior (always vs
   plugin-gated), and — if relevant — the conflict-ordering note (same-field edits resolve by
   filename order `0`→`z`; different-field edits don't conflict).

6. **Verify through the reader** — this is what makes the skill-authored write path safe.
   After placing the INI (and enabling its mod in MO2 if it's new):
   - `housecarl_skypatcher_read` on a record the patch targets: the computed post-state must show
     your ops APPLIED with the intended before → after values. A typo'd filter or operation
     classifies **Unknown with a loud warning** here — the same line SkyPatcher itself would skip
     *silently* in game — and a subtly-valid-but-wrong op shows up as the wrong field changing.
   - `housecarl_skypatcher_layer` for the file-level checks: your INI listed as APPLIED (not
     BSA-only, not filename-gated off, not shadowed by a same-path file from another mod), in the
     apply-order position you expect, no new same-field set conflict against another INI, and
     none of the three ITM classes pointing at your file: an intra-file dead write (a later line
     of your own file unconditionally overwrites every target of an earlier set — only the last
     write applies), a cross-INI duplicate (your line sets the same field/target to the same
     value another INI already sets), or a no-op write (the replay shows your SET writes the
     value the record already has). All three are authoring slips to fix at the source. (Dead
     writes list only FULLY dead ones — partial or conditional-only overwrites are not flagged,
     because the earlier write may still fire.)
   A patch that passes both is verified against the actual grammar and the actual load order —
   no game launch needed. (Resolving a reported conflict is the same loop: author the
   later-sorted INI that pins the intended value, then re-run the reader to confirm it wins.)

## Bundled-or-warn — never invent SkyPatcher grammar

The reference covers the 27 documented record types. If a filter, operation, or record type isn't
in it, **say so — don't fabricate a plausible token.** A wrong filter/operation name doesn't
error: SkyPatcher silently skips lines it can't parse, and a mistyped or unresolvable FormID is
skipped too — so the user gets a patch that quietly does nothing, with no log line pointing at the
cause. A clear "that's not in the SkyPatcher reference" beats a confident wrong line that wastes a
debugging session. The `housecarl_skypatcher_read` verify step (workflow step 6) is the safety net
for this failure class — an unrecognized key surfaces there as a loud unknown-key warning instead
of a silent in-game no-op — but it is a net, not a license to guess.

Specifically: **Object Modification (OMOD)** patching is enabled in SkyPatcher but has no
documentation and no verified grammar (`references/records/object-modification.md`). Surface the
gap and the leads recorded there; do not guess OMOD syntax.

## Common mistakes

- **Wrong subfolder.** A weapon patch under `npc/` never runs. The subfolder is part of the
  contract — take it from the index, not from a guess.
- **Inventing a filter or operation by analogy.** Each record file lists what *that* type supports;
  `filterByNameContains` exists for armor but not every type. Look it up.
- **A truncated or wrong FormID.** Copy the full FormID from xEdit/CK — a form SkyPatcher can't
  resolve is silently skipped, looking exactly like a syntax bug.
- **Forgetting the player exception.** Race and keyword filters always exclude the player; patch
  the player with `filterByNpcs=Skyrim.esm|7` alone (`grammar-core.md` §4).
- **EditorID on a FormID-only op** (NPC `objectsToAdd`/`factionsToAdd`, Outfit/FormList/LeveledList
  `formsToReplace`) — these need `Plugin|FormID`.
- **Reaching for SkyPatcher when the user means SPID/KID/CID** — those distribute; SkyPatcher
  edits. Check the verb ("distribute" vs "edit/patch/buff") and any named framework.

## Notes

- **Provenance.** The `references/` corpus is reconstructed from SkyPatcher's official Nexus
  documentation (v6.4.1), covering 27 record types (Object Modification / OMOD is a documented
  gap — see `references/records/object-modification.md`). On a SkyPatcher version bump, re-derive
  from the updated articles before trusting it for new operations.
- **Lookup without authoring.** The same reference answers "what filters/operations does record X
  support" or "what are the legal cast types" — open the record file or `value-tables.md`; no patch
  needs to be written.
- **Conflict model.** SkyPatcher's low-conflict property is real but not magic: same-field set
  operations still resolve by filename order; only add/remove operations truly accumulate. Mention
  this when a user layers multiple patches on one record (`grammar-core.md` §2).
  `housecarl_skypatcher_layer` reports these same-field set collisions across the whole load
  order (winner named); `housecarl_skypatcher_read` shows the resolved end state for one record.
