---
name: facegen-diagnostics
description: Diagnose and (where houseCARL can) repair the dark / grey / black-face NPC bug in Skyrim SE — resolve the NPC to a FormKey, compare the load-order record winner against the VFS facegen file winner, then place the correct facegen as a winning override (`housecarl_place_asset` / `housecarl_bulk_place_asset`) or forward the matching appearance into an override, instructing the CK / NifSkope / RaceMenu fixes houseCARL cannot perform. Use when an NPC has a dark, grey, black, brown, or discolored face, a head darker than its body or a neck seam, a face "fine in xEdit but wrong in game", a whole mod's NPCs gone dark after an ESL-compaction or merge, a missing or headless face, or the player's own face turned grey — or when the user mentions FaceGen, facegeom/facetint, the dark face bug, or Face Discoloration Fix. Load this before judging any face bug, even one that looks like a simple missing file — the fix hinges on which of two independent precedence systems (record vs file) wins, and a wrong call places facegen where the engine never looks.
---

# Facegen Diagnostics

## Overview

This is an investigation flow for the dark/grey/black-face family of NPC bugs in Skyrim SE. A baked NPC
face is two preprocessed per-NPC files (a head `.nif` and a face tint `.dds`); "dark face" is a **desync**
between the plugin that wins the NPC **record** and the mod/BSA that wins the **facegen file** for that
same NPC. This skill resolves the NPC, computes its facegen path, compares the two winners, and decides
whether to make the right **file** win or forward the right **appearance** into a record — then verifies.

**What houseCARL CAN do:** report the VFS/file winner of a Data-relative path (`housecarl_asset_status`),
place a correct copy as a winning loose override including single-entry in-process BSA extract
(`housecarl_place_asset` / `housecarl_bulk_place_asset`), and read the load-order-winning record + write
appearance edits into a new override plugin (`housecarl_read_record`, `housecarl_set_field`,
`housecarl_create_record`, `housecarl_cross_plugin_query`, `housecarl_batch_record_detail`).

**What houseCARL CANNOT do — instruct, never claim:** bake/regenerate facegen geometry (that is Creation
Kit Ctrl+F4), and read or edit the internal bytes of a `.nif` or `.dds` (it moves whole files; it does not
edit their contents). Anything needing NifSkope, a texture tool, the CK, or a runtime SKSE mod is
**instructed**. Saying this limit out loud is the Q3-honest move, not a failure.

The full cause taxonomy (A–X), fix taxonomy, symptom table, community-tool routing, and path mechanics
live in [`references/facegen-causes-and-fixes.md`](references/facegen-causes-and-fixes.md) — read it to
pin a specific cause or pick a fix; the flow below is enough to drive most diagnoses.

## The two-precedence model (read before judging anything)

Dark face exists because **two independent precedence systems** decide different things:

- **Plugin load order** decides which mod's **NPC record** wins → `housecarl_read_record`.
- **The MO2 VFS / asset order** decides which mod's **facegen FILE** wins → `housecarl_asset_status`.
  **Loose always beats BSA**; among loose, MO2 priority (then overwrite) wins; among BSAs, the later-loaded
  plugin's wins.

The face goes dark whenever, for one NPC, the **file winner's source ≠ the record winner's appearance
source**, or nothing wins the computed path — the engine then regenerates the head from the record and
drops the tint. This is why **xEdit can show no record conflict yet the face is dark**: the desync is
between a record and a *file*. Checking both winners is houseCARL's structural advantage — and the reason a
record-only tool can't solve this.

**The path is a pure function of the FormKey** (no path is stored in the record): folder =
`FormKey.ModKey.FileName` (the **defining master**, NOT the conflict winner); filename = `"00"` + the 6-hex
local id. There is **no cross-folder fallback** — the engine reads one keyed path and regenerates if
nothing wins there. So always ask *"what wins this exact path, and does it match the record?"* — never
*"does it fall back?"* (Mechanics, ESL, and injected-record detail: reference §1.)

## Step 0 — Scope and exclusions first (avoid false positives)

Rule these out before any tool call — each is out of houseCARL's lane and the desync flow would mislead:

- **Player-only grey, NPCs fine** (appeared after a reload / game update / crash) → almost certainly
  RaceMenu/SKEE co-save state, **not** facegen (Causes U/V). If **all** RaceMenu sliders/overlays are also
  gone game-wide, skee64.dll didn't load (V) — suspect first if it followed a Skyrim/Steam update. houseCARL
  is a **no-op**: instruct re-apply preset / OverlayFix / SKEE cosave fix (U), or SKSE↔runtime↔RaceMenu
  version match + read `skse64.log` (V). Stop. (Phrase as "strongly suggests," not "proves.")
- **Brown face** matching nothing → weight/scale baked into the save (Cause Q). Re-issue `setnpcweight`;
  if save-baked, new game or ReSaver. Runtime, not a file fix.
- **Purple / bright-white face** → a missing *texture* file, not facegen desync. Different lane.
- **Shiny/oily face, ash-pile** → specular/ENB or script state. Not facegen.
- **`FFxxxxxx` base id** (runtime-spawned) or **SPID/SkyPatcher-distributed appearance** → houseCARL reads
  *plugin* records, so the winner it sees may not be the in-game face (Cause T). Warn; route to FDF or
  matching the distributed head parts, not `place_asset`. (DynDOLOD is object-LOD, not NPC appearance.)
- **NPC built from a RaceMenu `.jslot` preset, facegen comes up missing** (Cause W) → the preset is not
  facegen; instruct Sculpt→Export Head / Ctrl+F4. Once the `.nif`/`.dds` exist, `place_asset` can win them.

## The front door — resolve the NPC to a FormKey

Users name an NPC by display name, EditorID, or a FormID they read somewhere — rarely as
`XXXXXX:DefiningMaster.esp`. Resolve carefully; one path is a trap:

1. **Prefer EditorID, then name.** Resolve via `housecarl_cross_plugin_query` over `NPC_` (a `where=`
   predicate on the EditorID or the display-name field). On **more than one** hit ("Guard", "Bandit"),
   list the candidates and have the user pick — **never auto-pick the first**.
2. **An xEdit-style FormID** the user already read: drop the high byte (it's that person's load-order
   index), keep the 6-hex local, and let houseCARL attach the defining master from its own load order.
3. **A console-clicked FormID — STOP.** It is a RefID (the placed instance, not the base NPC_) and/or a
   live runtime-indexed id; houseCARL's runtime-FormID↔FormKey bridge is **unshipped**, so the high byte
   (and the ESL `FExxx` slot) can't be mechanically resolved. Route to name/EditorID (have the user run
   `help "<name>" 4` or Skyrim Search SE), or treat the 6-hex local as a *hypothesis* and confirm by
   reading the candidate record back and matching the name. Never silently trust the console high byte — a
   wrong high byte → wrong defining master → wrong facegen folder, exactly the trap.

Once you hold the FormKey, `housecarl_read_record` it and check the **Template + "Use Traits"** exclusion:
if `Template` is set **and** `Configuration.TemplateFlags` includes `Traits` (Cause S), the NPC has no
facegen of its own — recompute the path against the **template's** FormKey, not this one.

## Diagnosis decision tree

**Step 1 — Record winner.** `housecarl_read_record` → which plugin's NPC record wins, and (from the
FormKey) the **defining master**. Confirm the record resolves and its masters are present (a dark/missing
actor where the *correct* file wins points at a missing master, Cause L — not a file fix). houseCARL exposes
FormIDs as `XXXXXX:Plugin.esp`, so folder + filename are computable from the FormKey alone.

**Step 2 — Compute the facegen path (both files, always a pair):**
- `meshes\actors\character\facegendata\facegeom\<DefiningMaster>\<00…ID>.nif`
- `textures\actors\character\facegendata\facetint\<DefiningMaster>\<00…ID>.dds`

**Step 3 — File winner.** `housecarl_asset_status` on **both** paths (it takes raw `asset_paths` with no
FormID/kind, so **you compute and pass both** the `.nif` and the `.dds`). Branch on the result:
- **No winner / absent everywhere** → the engine regenerates and drops tint → dark face. Does the **winning
  record actually change appearance** vs the defining master? Compare **`FaceMorph` / `TintLayers` /
  `HeadTexture`**, not `HeadParts` alone (a winner can change morph/tint while keeping the head-parts list,
  via `housecarl_cross_plugin_query` / `housecarl_batch_record_detail`). Unchanged → it's riding the
  master's facegen at the same keyed path (benign) — confirm the master's file resolves. Changed and no
  facegen exists anywhere → **Cause B/N: nothing correct to place → instruct CK Ctrl+F4** (FDF as a
  color-only band-aid). Recently **ESL-compacted or merged**? A stale old-name file may exist while the new
  path is empty (Cause F/G) — rename/place to the new name **plus** instruct the embedded-`.nif`-path edit.
- **A file wins, but from the wrong source** → Cause A/C/D/E. Is it a **loose file from a different/disabled
  mod or MO2 overwrite** masking the correct copy (E; loose beats BSA even from a disabled mod)? The correct
  copy **trapped in a losing/double BSA** (D)? A **non-appearance edit** that won the record while an
  overhaul's file still wins (C)?
- **A file wins from the right source, record looks right, still dark** → "file present" is necessary but
  not sufficient: the `.nif`'s internal head-part block names may not match the record (mode ii), which
  houseCARL **cannot inspect** — instruct CK re-bake / FDF, and confirm masters (Cause L) before concluding.

**Step 4 — Decide which copy is correct (the judgment this skill owns).** The invariant: **the winning
record's appearance and the winning facegen files must come from the SAME source.** The place tools are
deliberately dumb about which copy is correct — *this skill decides*, then drives them with an **explicit
`source=`** for a real desync fix (auto-resolve is a re-assert convenience, useful for sole-BSA→loose or to
own the copy):
- **Record winner is the intended appearance, wrong file wins** → **Fix B**: `place_asset` the correct
  facegen as a winning loose override.
- **The file is the intended appearance, a non-appearance plugin won the record** → **Fix C**: forward the
  appearance fields into a new override (houseCARL's editorial minimal set — `HeadParts, FaceMorph,
  FaceParts, TintLayers, HairColor, HeadTexture, TextureLighting`, + optional `WornArmor`).
- Often **both** (Fix B + Fix C together).

Placing both files of an NPC at once: `housecarl_bulk_place_asset` with `formid` and **no `kind`** expands
to mesh+tint via the pure path transform (an explicit `source=` for that both-case must be a **bare `.bsa`**
path — for a loose file or a `<bsa>|<entry>` source, set `kind=` and place the two separately). The single
`housecarl_place_asset` **requires** `kind` (`mesh`/`tint`) with a `formid`. **Place both halves from the
SAME source mod** — a same-FormKey forward is safe by construction (the `.nif`'s embedded `.dds` path
already resolves at the destination); only cross-FormKey / renumber / re-folder cases need the
NifSkope/FaceGenEslify escalation (reference Fix B/E).

**Step 5 — Verify ("wrote it" ≠ "it wins" ≠ "it renders correctly").** This is the Q3 backbone — houseCARL
confirms **provenance, not appearance** (it cannot read mesh/texture bytes or render):
- **5a — VFS check (houseCARL).** Re-run `housecarl_asset_status` to confirm the placed copy actually wins,
  and tell the user to **enable + sort** the new mod above the current winner (the tool reports the winner
  to sort above; trust its reported winner over the abstract rule — a "Manage Archives"-on user can rank a
  BSA above loose). This is necessary but **not sufficient** — a green status survives (1) winning-but-wrong
  content (houseCARL never validated the head inside the file), (2) a geometry/tint split, and (3) the save
  cache (Skyrim bakes facegen into the save for any already-loaded actor).
- **5b — In-game correctness handoff (the user's eyes, by design).** Hand the user this:
  1. `` ` `` (console) → **click the NPC** → `setnpcweight 50` → `` ` ``. This reloads the actor's 3D head
     in place, defeating the save cache. It is a *verification probe*, not the fix (temporary; reverts on
     cell change).
  2. `prid <RefID>` then `moveto player` (or `player.moveto <RefID>`) to reach them. **Never put `coc` in a
     `.bat` — it CTDs.** `prid`/`moveto`/`setnpcweight` need the in-world **RefID**, not the NPC_ base id.
  3. Look at the face. Correct → done. Still wrong → wrong file content (re-pick the source — houseCARL
     can't see inside the `.nif`/`.dds`) or a baked save (→ 5d).
- **5c — FDF-off litmus.** A genuinely-correct fix renders right with **FDF disabled**. If it looks right
  only with FDF installed, FDF is *masking* a desync the placed file didn't fix — still report and fix the
  underlying desync.
- **5d — True clean check.** Because facegen is baked into the save, the only fully-authoritative check is a
  **new game or a save where the NPC never loaded**. If `setnpcweight` + visual still shows wrong, the
  residue is in the save → hand off to Fallrim Tools (ReSaver) to delete the NPC's baked ChangeForm by base
  id (also resets faction ranks). houseCARL does not perform save edits.

## Batch flow ("a bunch of NPCs went dark after I installed X")

Mirror Dark Face Issue Reporter at the VFS layer — **enumerate → compute-all → asset_status-all →
bulk_place**:
1. `housecarl_cross_plugin_query` the suspect plugin's `NPC_` records (or query across the load order and
   filter to records whose **load-order winner** is X). **Dedupe to winners only.**
2. `housecarl_batch_record_detail` for FormKey + defining master per NPC in one batch.
3. Compute both facegen paths per NPC.
4. `housecarl_asset_status` each path. Three batch signatures: (i) *every* path resolves to nothing/vanilla
   → ESLify/merge FormID desync (Cause F/G, the "universal" case); (ii) record winner ≠ file winner
   consistently → record-vs-asset desync; (iii) only a subset dark → per-NPC missing/incompatible facegen.
5. `housecarl_bulk_place_asset` the correct copies into one fresh reviewable mod.

**Boundary:** houseCARL can batch-detect and batch-relocate/rename existing correct facegen (covers Cause
F/G — pure file-name/folder desyncs). If the batch reveals the facegen **exists nowhere** (true
missing/regenerate), the fix is **CK Ctrl+F4** — houseCARL cannot bake and must instruct.

## Common mistakes

- **Anchoring the facegen folder to the conflict winner.** The folder is the **defining master**
  (`FormKey.ModKey.FileName`) — for a vanilla-NPC overhaul that's `Skyrim.esm\`, not the overhaul's folder.
  Using the winner computes a path the engine never reads. The single highest-stakes mechanical error.
- **Trusting a console-clicked FormID.** It's a RefID and/or runtime-indexed; the bridge is unshipped.
  Route to name/EditorID — a wrong high byte points at the wrong defining master.
- **Calling "a file wins" the all-clear.** "File present at the path" is necessary, not sufficient — mode ii
  (`.nif` head-part block names ≠ record) dark-faces with a file present, and houseCARL can't see `.nif`
  internals. Always hand off the in-game check.
- **Declaring victory on a green `asset_status`.** That's provenance, not appearance — never skip Step 5b.
  Skipping it is a Q3 violation (a victory you provenance-checked but never appearance-checked).
- **Placing only one of the pair.** `.nif` and `.dds` go together, from the same source — one alone
  re-creates a mismatch.
- **Treating multi-provider / "Ambiguous" as a problem.** At a large modlist's scale, more than one source
  providing a path is the **common, healthy** case — present it neutrally; it's a "verify if unexpected"
  signal, not a detected fault.
- **Reaching for `place_asset` on an out-of-lane cause.** Player-only grey (U/V), `.jslot` presets (W),
  NiOverride overlays (X), `FFxxxxxx`/SPID-distributed (T), brown/save-baked (Q) — name the real tool, don't
  place a file that does nothing.

## Make a defensible verdict (no silent wrong answers)

A face-bug diagnosis lands on one of two honest outcomes, never a confident guess:

1. **A diagnosis with the cause, the fix, and its capability class** — "Cause A: `read_record` winner is
   Bijin, but `asset_status` shows the `.nif`/`.dds` won by a stale loose copy from a disabled mod. Fix B:
   `place_asset` Bijin's pair as a winning override; then enable+sort and run the in-game `setnpcweight`
   check." Name which winner is wrong and which fix moves which half.
2. **An explicit "I can't fully resolve this — here's what I checked and what to do next"** — when the cause
   is out of lane (the file wins and the record looks right, so it's likely mode ii inside the `.nif`, which
   I can't inspect → CK re-bake / in-game check), or houseCARL is structurally a no-op (RaceMenu/SKEE,
   save-baked, runtime-distributed). Say what you confirmed, why houseCARL can't finish it, and the exact
   external tool that can.

A confidently wrong "place this file and you're done" sends the user to enable a mod that changes nothing —
worse than a clear non-answer. Prefer the honest gap and the right external tool.

## Notes

- **Pair everything.** Query, place, extract, and forward both the `.nif` (FaceGeom) and the `.dds`
  (FaceTint) for a FormKey — fixing one without the other still dark-faces.
- **Two embedded `.nif` references are both invisible to houseCARL** — the FaceTint `.dds` path (NifSkope
  slot 7) and the skin diffuse/normal paths (slots 1/2). Route by symptom: stale FaceTint → FaceGenEslify;
  wrong skin path → NPC Facegen Patcher; general missing tint → FDF. Never claim to know which slot is
  broken (reference §6).
- **FaceGenEslify renames files; it does NOT auto-edit the embedded `.nif` path** (that's a manual NifSkope
  step). houseCARL's conclusion is unchanged — it can rename/place files but must *instruct* the embedded
  edit on cross-FormKey/renumber cases.
- **Field names:** confirm any NPC_ field path/spelling via the `mutagen-reference` skill before composing a
  `set_field`/`create_record` — the appearance set uses Mutagen spellings (`TextureLighting` = the QNAM
  Color field; `TintLayers` is one token).
- **The place tools report the required enable+sort and never claim the fix took effect on write** — carry
  that honesty through to the user.
