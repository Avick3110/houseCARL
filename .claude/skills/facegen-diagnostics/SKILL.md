---
name: facegen-diagnostics
description: Diagnose and (where houseCARL can) repair the dark / grey / black-face NPC bug in Skyrim SE — resolve the NPC to a FormKey, compare the load-order record winner against the VFS facegen file winner, read and write the winning facegen mesh's data values (`housecarl_nif_inspect` / `housecarl_nif_set` — baked shape names, embedded FaceTint/skin texture paths, flags, alpha, partitions; rewrite a wrong path or rename a shape, verified), place the correct facegen as a winning override (`housecarl_place_asset` / `housecarl_bulk_place_asset`), or forward the matching appearance into an override — instructing the CK bake / RaceMenu / geometry fixes houseCARL still cannot perform. Use when an NPC has a dark, grey, black, brown, or discolored face, a head darker than its body or a neck seam, a face "fine in xEdit but wrong in game", a whole mod's NPCs gone dark after an ESL-compaction or merge, a missing or headless face, or the player's own face turned grey — or when the user mentions FaceGen, facegeom/facetint, the dark face bug, or Face Discoloration Fix. Load this before judging any face bug, even one that looks like a simple missing file — the fix hinges on which of two independent precedence systems (record vs file) wins, and a wrong call places facegen where the engine never looks.
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
(`housecarl_place_asset` / `housecarl_bulk_place_asset`), read the load-order-winning record + write
appearance edits into a new override plugin (`housecarl_read_record`, `housecarl_set_field`,
`housecarl_create_record`, `housecarl_cross_plugin_query`, `housecarl_batch_record_detail`), and **read the
data values *inside* the winning facegen `.nif`** (`housecarl_nif_inspect`) — its baked shape names, the
embedded texture-set paths (the FaceTint `.dds` path at slot 6 and the skin diffuse/normal at slots 0/1),
NiAVObject flags + scale, alpha property, BSDismember partitions, bones, node tree, and header strings. It
resolves the mesh through the same VFS (winner by default; `mod=` for a specific provider), so you can read
the copy the game actually uses. And it can now **write a whitelisted set of those `.nif` data values back**
(`housecarl_nif_set`): rewrite a `BSShaderTextureSet` slot (`set_path` — the embedded FaceTint slot 6 or skin
slots 0/1), rename a baked shape or node (`rename_shape` / `rename_node`), and set `NiAVObject` flags
(`set_flags` — the `0x80000` head/hair-class bit), alpha (`set_alpha` — the hair `0x12ED` / hairline `0x12EE`
class), a `BSDismember` partition (`set_partition`), or scale (`set_scale`). Every write passes **two
offset-immune verification gates before anything lands** (only the value the op claims to touch changed; a
reload re-reads it; census + SE-stream intact) — a failed verify writes **nothing** and says why. By default
the edited mesh goes into a **new houseCARL MO2 mod folder** at the same path (originals untouched; enable +
sort it above the current winner — a BSA-packed source becomes a loose winning override this way);
`in_place=true` overwrites the winning loose file itself (opt-in, one-time per-file acknowledge, **no
backup**).

**What houseCARL CANNOT do — instruct, never claim:** bake/regenerate facegen **geometry** (that is Creation
Kit Ctrl+F4 — `nif_set` edits data *values*, never vertices/tris); read the `.dds` **pixel content or
format** (it reads the `.nif`'s *reference* to a `.dds`, never the tint/skin image itself); edit the
non-texture string refs — a **material / `.tri` / physics-xml** path (those live under `nif_inspect`
`sections=strings`; `set_path` only swaps `BSShaderTextureSet` texture slots, so a material-path swap is
still NifSkope); and judge whether the geometry or the final **rendered** face is *correct* (a written,
verified value is provenance at the data layer, not a render — a name/path/flag can be right while the mesh
still looks wrong, so the in-game check always stands). Anything needing the CK, a texture tool, a material
edit, or a runtime SKSE mod is **instructed**. Saying these limits out loud is the Q3-honest move, not a
failure — and the precise line is now "I can read the mesh's values *and* write the whitelisted ones
(verified before they land), but I can't touch its geometry, its `.dds` pixels, or tell you it renders
right."

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
  path is empty (Cause F/G) — place it at the new name **plus** rewrite the embedded FaceTint slot to match
  with `nif_set set_path texture_slot=6` (the step that used to be a manual NifSkope edit).
- **A file wins, but from the wrong source** → Cause A/C/D/E. Is it a **loose file from a different/disabled
  mod or MO2 overwrite** masking the correct copy (E; loose beats BSA even from a disabled mod)? The correct
  copy **trapped in a losing/double BSA** (D)? A **non-appearance edit** that won the record while an
  overhaul's file still wins (C)?
- **A file wins from the right source, record looks right, still dark** → "file present" is necessary but
  not sufficient (mode ii). This is where **`housecarl_nif_inspect`** now earns its keep — inspect the
  winning `.nif` and check three things against the record: (1) do its **baked shape names** correspond to
  the winning record's **HeadParts** (a facegen built for a *different* NPC, or missing the expected head
  parts, is the classic mode-ii dark-face — and an HDPT-EDID shape-name mismatch is now `nif_set`
  `rename_shape`-fixable); (2) does the **slot-6 FaceTint path** point at this NPC's
  `…\facetint\<DefiningMaster>\<00…ID>.dds` (a stale/renumbered embedded path is Cause F(b) — houseCARL now
  both *sees* it's wrong **and** rewrites it with `nif_set` `set_path texture_slot=6`); (3) do the **skin
  diffuse/normal slots (0/1)** point where you expect (a wrong skin path is Cause I/R — also `set_path`-fixable
  at slot 0/1). If the names/paths all match yet the face is still dark, the residue is in what houseCARL
  still can't judge — the geometry itself, the `.dds` pixels, or a baked save — so confirm masters (Cause L),
  then instruct CK re-bake / FDF and hand off the in-game check. **Reading the mesh narrows mode ii from
  "undiagnosable" to "name/path checked", and `nif_set` now *repairs* the name/path/flag class it finds — but
  neither replaces the in-game render check** (a matching, verified name/path is necessary, not sufficient).

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
already resolves at the destination). A cross-FormKey / renumber / re-folder forward leaves the embedded
FaceTint slot pointing at the *source* FormID — that fixup used to be a NifSkope escalation, but is now
`nif_set set_path texture_slot=6` on the placed `.nif` to the destination's `…\facetint\<Master>\<00…ID>.dds`
(reference Fix B/E). Only a CK re-bake (new geometry) still leaves the data layer.

**Step 5 — Verify ("wrote it" ≠ "it wins" ≠ "it renders correctly").** This is the Q3 backbone — houseCARL
confirms **provenance** (and, via `nif_inspect`, the mesh's **data values** — names/paths/flags), **but not
the rendered appearance** (it reads no `.dds` pixels, judges no geometry, and does not render):
- **5a — VFS check (houseCARL).** Re-run `housecarl_asset_status` to confirm the placed copy actually wins,
  and tell the user to **enable + sort** the new mod above the current winner (the tool reports the winner
  to sort above; trust its reported winner over the abstract rule — a "Manage Archives"-on user can rank a
  BSA above loose). This is necessary but **not sufficient** — a green status survives (1) winning-but-wrong
  content (`nif_inspect` catches the *name/path* class of wrongness — shape names ≠ record, stale tint path —
  and `nif_set` *repairs* that class, but its two-gate verify confirms the **data value** changed, never that
  the geometry or `.dds` pixels are right), (2) a geometry/tint split, and (3) the save cache (Skyrim bakes
  facegen into the save for any already-loaded actor). So after a `nif_set` repair, still run 5b — a verified
  write is not a verified render.
- **5b — In-game correctness handoff (the user's eyes, by design).** Hand the user this:
  1. `` ` `` (console) → **click the NPC** → `setnpcweight 50` → `` ` ``. This reloads the actor's 3D head
     in place, defeating the save cache. It is a *verification probe*, not the fix (temporary; reverts on
     cell change).
  2. `prid <RefID>` then `moveto player` (or `player.moveto <RefID>`) to reach them. **Never put `coc` in a
     `.bat` — it CTDs.** `prid`/`moveto`/`setnpcweight` need the in-world **RefID**, not the NPC_ base id.
  3. Look at the face. Correct → done. Still wrong → wrong file content (re-pick the source; `nif_inspect`
     can tell you whether it's a *visible* wrongness — shape names ≠ record, stale tint/skin path — or the
     kind it can't see: wrong geometry, `.dds` pixels) or a baked save (→ 5d).
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
   For (iii)'s *incompatible* half — a file wins but the face is still wrong — `housecarl_nif_inspect` on a
   sample of the subset separates "wrong content baked in" (shape names / tint path ≠ record → mode ii) from
   "genuinely absent" (asset_status already said so), so you don't `bulk_place` a copy that was never the
   problem.
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
  (`.nif` shape names ≠ record) dark-faces with a file present. `housecarl_nif_inspect` now lets you *check*
  the mode-ii name/path match instead of guessing, and `housecarl_nif_set` *repairs* the name/path/flag class
  it finds — but a matching, verified name is still necessary-not-sufficient (geometry, pixels, and the
  render stay unseen), so always hand off the in-game check.
- **Treating a green `nif_set` verify as a fixed face.** The two verification gates confirm the *data value*
  landed (and that a bad write aborts touching nothing) — not that the face renders right. A rewritten
  FaceTint path or renamed shape still needs the 5b in-game check; a verified write is not a verified render.
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
   is out of lane (the file wins, the record looks right, and `nif_inspect` shows the mesh's shape names and
   tint/skin paths *also* match — so the residue is the geometry, the `.dds` pixels, or a baked save, none of
   which houseCARL can judge → CK re-bake / in-game check), or houseCARL is structurally a no-op (RaceMenu/SKEE,
   save-baked, runtime-distributed). Say what you confirmed (now including what you read *inside* the mesh),
   why houseCARL can't finish it, and the exact external tool that can.

A confidently wrong "place this file and you're done" sends the user to enable a mod that changes nothing —
worse than a clear non-answer. Prefer the honest gap and the right external tool.

## Notes

- **Pair everything.** Query, place, extract, and forward both the `.nif` (FaceGeom) and the `.dds`
  (FaceTint) for a FormKey — fixing one without the other still dark-faces.
- **The two embedded `.nif` texture references are now READABLE *and* WRITABLE — via `nif_inspect` +
  `nif_set`** — the FaceTint `.dds` path (binary slot 6 / NifSkope slot 7) and the skin diffuse/normal paths
  (binary slots 0/1 / NifSkope 1/2). Read to *see which slot holds what*, then rewrite the wrong one in place
  with `nif_set set_path texture_slot=<n>` (verified before it lands). This absorbs the embedded-path step
  the community tools leave to NifSkope: a stale FaceTint path (slot 6) — the FaceGenEslify manual step — is
  now houseCARL-doable; a wrong skin diffuse/normal path (slot 0/1) — the NPC Facegen Patcher edit — is now
  houseCARL-doable; general missing tint still → FDF. What houseCARL still cannot do is read the `.dds`
  **pixels** or swap a non-texture (material/`.tri`/xml) string ref (reference §6).
- **FaceGenEslify renames files; it does NOT auto-edit the embedded `.nif` path** (its own README leaves that
  a manual NifSkope step). houseCARL can rename/place the files **and now performs that manual step itself** —
  read the slot-6 FaceTint path to confirm it's stale (≠ the current `<DefiningMaster>\<00…ID>.dds`), then
  `nif_set set_path texture_slot=6` to the correct path. So on a compaction/renumber, the file rename and the
  embedded-path rewrite are both at the data layer; only the CK re-bake (new geometry) still leaves the tool.
- **Read from the winning copy, or a named provider.** `nif_inspect` resolves through the VFS like
  `asset_status` (winner by default; `mod=` for a specific provider), so you can compare two mods' baked
  facegen — "does the file that *wins* carry this NPC's shape names, or is a different mod's copy on top?" —
  without leaving the data layer. Real read of the Lucien facegen (`FaceGeom\lucien.esp\00005900.nif`):
  shapes `LucienHead / LucienHair / LucienHairLine / LucienEyes / LucienLashes / LucienBrows /
  MaleMouthHumanoidDefault`; slot-6 FaceTint path `…\facetint\lucien.esp\00005900.dds`; hair alpha
  `0x12ED` (blend on) vs hairline `0x12EE` (test, threshold 180); partitions 30/31/32 (HEAD/HAIR/BODY);
  bones `NPC Head [Head]`, … — the whole mode-ii / tint-path check, read straight from the mesh.
- **Field names:** confirm any NPC_ field path/spelling via the `mutagen-reference` skill before composing a
  `set_field`/`create_record` — the appearance set uses Mutagen spellings (`TextureLighting` = the QNAM
  Color field; `TintLayers` is one token).
- **The place tools report the required enable+sort and never claim the fix took effect on write** — carry
  that honesty through to the user.
