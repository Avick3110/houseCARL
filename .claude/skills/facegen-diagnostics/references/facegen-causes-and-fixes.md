# FaceGen causes, fixes, and mechanics — deep reference

Load this when the SKILL.md decision tree points here: to identify a specific cause, pick the matching
fix and its capability class, read the path/keystone mechanics, or look up which community tool owns a
case houseCARL cannot. The decision tree in `SKILL.md` is enough for most diagnoses; this file is the
*why* and the long tail.

## Contents
- [1. The keystone — facegen path is a pure function of the FormKey](#1-the-keystone)
- [2. The desync model — record winner vs file winner](#2-the-desync-model)
- [3. Cause taxonomy A–X](#3-cause-taxonomy)
- [4. Fix taxonomy A–I (with capability class)](#4-fix-taxonomy)
- [5. Symptom → likely cause](#5-symptom--likely-cause)
- [6. The two embedded `.nif` texture references — now READABLE and WRITABLE (`nif_inspect` + `nif_set`)](#6-embedded-nif-references)
- [7. Edge cases and gotchas](#7-edge-cases-and-gotchas)
- [8. Community tools reference](#8-community-tools)
- [9. Residual uncertainty — what to route AROUND, not assert](#9-residual-uncertainty)

---

## 1. The keystone

A baked NPC face is **two preprocessed per-NPC files** the Creation Kit exports on Ctrl+F4:

```
Head mesh ("FaceGeom"):  meshes\actors\character\facegendata\facegeom\<DefiningMaster>\<file>.nif
Face tint ("FaceTint"):  textures\actors\character\facegendata\facetint\<DefiningMaster>\<file>.dds
```

The two trees diverge only at `meshes`↔`textures` and `facegeom`↔`facetint`; the `<DefiningMaster>\<file>`
tail is identical. Paths are case-insensitive (the MO2 VFS already is). **The `.dds` stays opaque binary to
houseCARL** (it reads no tint/skin pixels) — but the `.nif` no longer is: `housecarl_nif_inspect` reads its
**data values** (shape names, the embedded texture-set paths, NiAVObject flags, alpha, partitions, bones,
node tree, header strings) and `housecarl_nif_set` **writes a whitelisted subset of them back** (texture-slot
paths, shape/node names, flags, alpha, partitions, scale), verified before landing. What houseCARL still
cannot do to either file is touch the **geometry**, read a `.dds`'s **image**, swap a **material/xml** string
ref, or judge the **rendered** result — it edits data values and places/moves whole files; it does not remesh
a `.nif` or read a `.dds`'s pixels (§6).

**The path is a pure function of the FormKey** — the engine links facegen to the NPC's FormID, not to any
path stored in the record. There is no path field in the NPC record. So houseCARL derives the exact
expected path from the FormKey alone, exactly as `housecarl-core`'s `FaceGenPath.For` does:

> **folder = `FormKey.ModKey.FileName`** (the defining master's filename, including extension —
> `Skyrim.esm`, `Dragonborn.esm`, `diverse skyrim.esp`)
> **filename = `"00" + FormKey.ID.ToString("X6")`** (the masked 24-bit local id) + `.nif` / `.dds`

Worked examples (confirmed across the CK wiki, Nexus articles 2090 / 52817, FaceGenEslify, SkyLady):
`000012C4` in `ExampleMod.esp` → `…\facegeom\ExampleMod.esp\000012C4.nif`; Dragonborn NPC Elmus
`0001A51A` → `…\Dragonborn.esm\0001A51A.nif`; non-zero high byte `14004085` in `test.esp` →
`…\test.esp\00004085.nif` (the index byte is zeroed regardless of value).

**Use this `FormKey.ModKey.FileName` + `"00"+ID.X6` framing — not "render the full 8-hex FormID and zero
the index region."** The two are equivalent in result, but the second phrasing invites a reimplementation
that slices a *runtime* FormID and introduces a load-order-index bug. Mutagen's `FormKey.ID` is already
the bare local id, so the simple form is ESL-safe by construction (Mutagen 0.53.1 confirmed: an `.esl`
FormKey → `…\MyLight.esl\000001FF.nif`).

**Folder = the defining master, never the conflict winner.** The subfolder is the plugin the record's
FormID is *prefixed to* — for an injected/override NPC, the master it is injected INTO (where the FormID
high byte points), NOT the plugin that authors the record, and NOT the load-order winner. An override mod
that merely edits an existing vanilla NPC ships its facegen under `Skyrim.esm\`, not its own folder. This
is the single highest-stakes mechanical point: anchoring the folder to the conflict winner computes a path
the engine never reads and places facegen where it does nothing. (`FaceGenPath.For` is correct by
construction here — it uses `FormKey.ModKey`, which is the prefix master.)

**There is NO cross-folder fallback.** The engine computes ONE path and uses whatever loose/BSA file wins
there. If nothing valid resolves, it does NOT look in the master's folder or `Skyrim.esm` — it falls into
runtime head *regeneration* from the record's morph/head-part data and drops the tint (dark face). So the
diagnostic question is always *"what wins this exact path, and does it match the record?"* — never *"does
it fall back?"*

**ESL / ESPFE:** the on-disk filename never contains "FE". `FaceGenPath` renders the current (post-
compaction) local id from Mutagen's FormKey, so it stays correct by construction — the folder is unchanged
on eslification (same plugin file), only the filename tracks the renumbered local id. A freshly-eslified
mod's loose facegen sits at the OLD names until renamed/regenerated; `place_asset` must target the NEW
(current-FormKey) name. (`<0x800` "hardcoded" injected records, which xEdit issue #848 can mislabel as
"new": compute the path AND flag the condition rather than assert silently. Rare for NPC_.)

---

## 2. The desync model

Dark/grey/black face is a **desync between two independent precedence systems**:

- **Plugin load order** decides which mod's **NPC record** wins — what `housecarl_read_record` returns.
- **The MO2 VFS / asset order** decides which mod's **facegen FILE** wins — what `housecarl_asset_status`
  returns. **Loose always beats BSA**; among loose, MO2 mod priority (then the overwrite folder) decides;
  among BSAs, the BSA whose plugin loads later wins.

Dark/wrong face occurs whenever, for the same NPC, the **file winner's source ≠ the record winner's
appearance source**, or no file resolves at the computed path. The engine then regenerates the head from
the record and drops the preprocessed tint → untinted (dark/grey/black) head with a sharp neck seam
against the correctly-tinted body. This is why **xEdit can show NO record conflict yet the face is dark** —
the desync is between a record and a *file*, not between two records. Checking the file winner
(`asset_status`) alongside the record winner (`read_record`) is houseCARL's structural advantage.

Two trigger conditions both land in this same path, so "a file is present" is **necessary but not
sufficient**: (i) no valid head mesh at the computed path; or (ii) a file IS present but its internal
shape/head-part names don't match the winning record's head-parts list (mode ii — "Dawnguard vampires are
notorious for this"). **`housecarl_nif_inspect` now reads those shape names and the embedded tint/skin paths**,
so mode ii moves from blind to *checkable* — compare the mesh's baked names against the record's HeadParts.
A name/path *mismatch* is a confirmed mode-ii finding; a name/path *match* is necessary-not-sufficient (the
geometry, the `.dds` pixels, and the render stay unseen), so it still routes to CK re-bake / the in-game check.

**"Rides the master's facegen" is the SAME keyed path, not a fallback.** A pure stat/AI override of a
vanilla NPC (no appearance change, ships no facegen) renders fine because the vanilla file already wins
that exact `Skyrim.esm\…` path. It dark-faces only if no file wins there or the record's head parts no
longer match. So a no-facegen override does NOT "always dark-face."

A **separate, non-desync dark class** comes from broken/missing **skin-texture path** references (a mod
overwrote/renamed the `.dds` a head `.nif` or record points to). FDF does **not** fix this. Keep it
distinct: head-parts/facegen mismatch → tint-dropped-on-regen (FDF/`asset_status`/`place_asset`/CK domain);
broken skin texture → a texture-path problem (Cause R).

---

## 3. Cause taxonomy

Fix layers: **houseCARL-file** (`asset_status`/`place_asset`) · **houseCARL-record**
(`read_record`/`set_field`/`create_record`) · **houseCARL-nif** (`nif_inspect` reads the `.nif`'s data
values; `nif_set` writes the whitelisted ones — texture-slot paths, shape/node names, flags, alpha,
partitions, scale — verified before landing) · **CK-instructed** (Ctrl+F4 bake — houseCARL cannot make
geometry) · **nif-dds-instructed** (what's left for NifSkope / a texture tool: the `.dds` **pixels** and
non-texture string refs — material / `.tri` / xml — that `set_path` doesn't reach) · **runtime-mod**.
Note the split shifted with Wave 2: `nif_inspect` turned "houseCARL can't see it" into "houseCARL reads it",
and `nif_set` now turns most of the embedded-value edits (texture path, shape name, flags/alpha/partition)
into "houseCARL fixes it at the data layer" — leaving only pixels, geometry, and material/xml refs
instructed.

| # | Cause | Mechanism | houseCARL tell | Fix class |
|---|---|---|---|---|
| **A** | **Record-winner vs file-winner desync** (dominant) | Load order picks record B; VFS picks mod A's facegen file → file built from a different mod's appearance than the winning record | `read_record` winner ≠ `asset_status` winner on the `.nif`/`.dds` pair | houseCARL-file and/or -record |
| **B** | **Winner ships NO facegen** | Override changed appearance but author shipped only the plugin → record points at facegen that exists nowhere | record appearance changed (compare below) + `asset_status` = no winner at the path | CK-instructed; or houseCARL if a correct source exists |
| **C** | **Non-appearance edit reverts head parts** | A later non-facegen plugin (USSEP-style) wins the record and reverts head parts toward vanilla while overhaul A's file still wins | `cross_plugin_query` shows the non-appearance plugin winning head parts; `asset_status` shows A's file still winning | houseCARL-record (often + -file) |
| **D** | **Facegen trapped in a losing/double BSA** | Correct facegen is in a BSA that loses the VFS race, or buried in a RAR-in-BSA the game can't read | `asset_status` shows a wrong loose/BSA copy winning, or the expected BSA losing | houseCARL-file (extract the entry to loose) |
| **E** | **Stale loose facegen masks correct BSA** | A leftover loose file (old/disabled/uninstalled mod, leftover extraction, wrong priority) wins because **loose beats BSA even from a disabled mod** | `asset_status` shows a loose file winning over the expected BSA | houseCARL-file |
| **F** | **ESL / compaction FormID renumber** | `Compact FormIDs for ESL` renumbers local IDs but does NOT rename facegen files → engine looks up the new id, finds nothing. (a) filename mismatch; (b) FaceTint path embedded in each `.nif` | universal dark face for the whole eslified mod; `asset_status` on the new path = no winner | (a) houseCARL-file; (b) houseCARL-nif (`set_path` slot 6) |
| **G** | **Merge / renumber (zMerge, Merge Plugins)** | New defining master AND new local id → both folder and filename change; facegen not carried | dark face for merged NPCs; new computed path absent | houseCARL-file (+ houseCARL-nif if embedded path changed) |
| **H** | **Head-part / hair / brow conflict or missing HDPT** | Record references an HDPT whose mesh is absent/replaced, or whose internal shape names don't match | partial dark/clipping; `read_record` head-parts list vs `nif_inspect` shape names; mesh winner ≠ expected | split: houseCARL-record / houseCARL-nif (`rename_shape`) / CK |
| **I** | **Face-vs-body texture mismatch** (unique-bodies-by-race) | Head `.nif` hardcodes the vanilla skin path; a per-race body framework gives a unique body but the head still loads vanilla → head/body color mismatch. **NOT the dark-face bug.** | detect the *scenario*, and `nif_inspect` READS the `.nif` skin slots (0/1) so you see the wrong path; `nif_set set_path` at slot 0/1 now REWRITES it (the NPC Facegen Patcher edit) | houseCARL-nif (`set_path` slots 0/1) |
| **J** | **Skin-tone / TextureLighting / HeadTexture record mismatch** | Winner's TintLayers / TextureLighting (QNAM) / HeadTexture / HairColor inconsistent with body race/skin | wrong-colored but correct-shaped head; `read_record` these fields vs intended source | houseCARL-record (pair with re-bake/FDF) |
| **K** | **CK baked against the wrong load order/masters** | Facegen baked while the wrong override resolved (missing master) → right filename, wrong content | wrong (not dark) face; `read_record` shows the true winner to bake against | CK-instructed |
| **L** | **Missing master / orphan override** | Override copied without its master (or master disabled) → record can't resolve | dark/missing actor, but `asset_status` shows the correct file winning; `read_record` masters | houseCARL-record (masters-by-construction) / report |
| **M** | **Missing appearance dependency** (High Poly Head, KS Hairdos) | A required resource mod is disabled while the record still references it | headless/floating parts; `read_record` head-parts links unresolved, or asset absent | runtime-mod / report (install) |
| **N** | **Custom/beast race, no baseline facegen ever baked** | Never exported facegen (or for the wrong race) | guaranteed black face; `asset_status` = no winner anywhere | CK-instructed |
| **O** | **LE/Oldrim facegen `.nif` ported to SE without re-bake** | SE will not read Oldrim facegen `.nif` format | dark face for an LE-ported NPC mod; a file may win that the engine still can't use | CK-instructed |
| **P** | **Texture-memory grey/black face** (rare) | Face/body textures too large/uncompressed (8k, non-BC `.dds`) → tint fails to apply though record+file match. **Tell: not permanent — save+reload temporarily clears it.** | `asset_status`/BSA listing flags oversized/uncompressed `.dds` | nif-dds-instructed (Cathedral Assets Optimizer / Ordenator) |
| **Q** | **Weight/scale-induced brown face** (runtime) | In-game weight ≠ the weight the tint was baked at (commonly from `setnpcweight`/`setscale`, baked into the save) | plain brown face matching nothing; `read_record` Weight | runtime/console (re-issue `setnpcweight`); save-baked → ReSaver |
| **R** | **Broken/missing skin-texture path** (distinct dark class) | A mod overwrote/renamed the skin `.dds` a head `.nif`/record points to. If the broken ref is INSIDE the `.nif`, `nif_inspect` READS which slot/path is broken and `nif_set set_path` REWRITES it to a valid `.dds`; if the `.dds` itself is missing, that's a file fix (or the pixels, which stay opaque) | black face; **FDF does NOT fix it**; `asset_status` on the texture path / `read_record` HeadTexture (TXST) / `nif_inspect` the in-`.nif` skin slots | houseCARL-nif (`set_path`) and/or houseCARL-file |
| **S** | **Template + "Use Traits" actor** (exclusion, not a defect) | NPC inherits appearance from a template ActorBase → has NO facegen of its own; facegen lives under the **template's** FormKey | `read_record` Template set AND `Configuration.TemplateFlags` includes `Traits` → recompute against the template's FormKey | n/a — redirect diagnosis |
| **T** | **Runtime-distributed appearance (SPID/SkyPatcher) / `FFxxxxxx` NPCs** | Appearance applied at runtime, or on a dynamically-spawned actor → no pre-baked facegen; the plugin-winner record houseCARL sees ≠ the in-game face | recognize the scenario (SPID/SkyPatcher present; `FF` base id); houseCARL reads *plugin* records, can't see the distributed result | runtime-mod / report (not `place_asset`) |
| **U** | **RaceMenu/SKEE co-save state dropped — player-only grey** | SKEE skipped the actor's co-save → NiOverride sculpt/tint held only at runtime is dropped | **player** (+ a few RaceMenu-touched NPCs) grey, ordinary NPCs fine; appears after reload/update/crash | runtime-mod / report — houseCARL **no-op** |
| **V** | **skee64.dll didn't load** (version mismatch / fatal error) | No runtime overlay/tint application runs (often after a Skyrim/Steam auto-update) | grey **player** face **plus all RaceMenu sliders/overlays gone game-wide** | runtime-mod / report — houseCARL **no-op** |
| **W** | **Sculpt in `.jslot`/preset, never exported to facegen** | A `.jslot`/runtime sculpt holds the geometry/tint but no `.nif`/`.dds` was ever exported for the FormKey | an NPC built from a preset is grey though the modder "has the preset"; `asset_status` = facegen missing | partial: CK/RaceMenu-instructed to bake, then houseCARL-file |
| **X** | **NiOverride runtime overlays expected on an NPC** | SKEE warpaint/body/face overlays are runtime-applied, not in the exported `.dds` unless baked | NPC missing warpaint/makeup, or wrong/grey in that region | runtime-mod / report (no-op for the overlay; tintmask `.dds` itself → `place_asset`) |

---

## 4. Fix taxonomy

Each fix: procedure + capability class. **Always handle facegen as a PAIR** — query/place/forward both
the `.nif` (FaceGeom) and the `.dds` (FaceTint); fixing one without the other still dark-faces.

### Fix A — Regenerate facegen in the Creation Kit (Ctrl+F4) — **CK-instructed**
The only correct fix when no correct facegen exists anywhere and the record content is what you want
(Cause B/N), or after ESL/merge renumber. Hand the user verbatim: open CK; load the plugin and **"Set as
Active File"** (this keys output to that plugin's name); ensure required masters are loaded; in the Object
Window go to the **Actors tree directly** (Ctrl+F4 does NOT fire if actors were found via the `*All`
search); select the actors; press **Ctrl+F4**, confirm, wait for "Done." Pitfalls: do NOT select
child/RaceChild actors (unique child TRI → "shiny potatoes"); some unique faces (Astrid) corrupt others on
regen; large batches need RAM/VRAM (batch by race if <32 GB). **houseCARL has zero CK automation — hand
over the procedure, never claim to do it.**

### Fix B — Place the correct existing facegen as a winning loose override — **houseCARL-file**
When a correct copy exists somewhere (mod or BSA) but loses VFS precedence (Cause D/E, and the file side of
A/C/F/G). `asset_status` the two paths to see the current winner, then `place_asset`/`bulk_place_asset` to
extract the correct entry (single-entry in-process BSA extract) and write it as a winning loose override
into a fresh enable+sort MO2 mod. **Place BOTH `.nif` and `.dds` as a pair, from the SAME source mod.**

> **Same-FormKey forward is SAFE by construction — do not over-refuse.** The `.nif` embeds the FaceTint
> `.dds` path as a pure function of `(defining-master, local FormID)`. A same-FormKey, same-defining-master
> forward (the normal "make Mod A's appearance win for this NPC" case) changes neither — so the copied
> `.nif` already embeds exactly the path that resolves to the destination's `.dds`. No NifSkope edit is
> needed; this is why EasyNPC copies facegen as exact copies. Place the pair together so another mod's
> `.dds` can't win the tint slot (a softer "looks strange" mismatch).
>
> **Fix B needs a follow-up embedded-path rewrite ONLY in cross-FormKey / renumber / re-folder cases** —
> where the destination FormID or defining plugin differs from what's baked into the source `.nif`
> (ESLify/compaction changed the FormID; re-homing changed the `<Plugin>` segment; a merge renumbers/
> re-masters). The engine DOES honor the embedded path, so the placed `.nif`'s slot-6 FaceTint path must be
> repointed at the destination — which **houseCARL now does itself** with `nif_set set_path texture_slot=6`
> (verified), no longer a NifSkope escalation. A bulk run across many such NPCs is still faster in
> FaceGenEslify.

Then re-run `asset_status` and tell the user to enable + sort, and run the in-game verification (SKILL.md
Step 5).

### Fix C — Forward NPC appearance into a winning override record — **houseCARL-record**
When a behavior/other plugin won the record while the intended appearance mod's facegen files are present
(Cause C/J; record side of A). The one-call way to re-assert the appearance mod's whole record is
**`housecarl_forward_record`** — name the appearance plugin as the source and it copies that plugin's `NPC_`
verbatim into a winning override (nested appearance fields included). For a *partial* appearance mask —
forwarding only some of the face-determining fields onto a different base record — `read_record` the winner
and the appearance source, then `create_record`/`set_field` to write an override copying just that subset.

> **The face-determining minimal set is houseCARL's OWN editorial set** (all real, writable NPC_ fields,
> Mutagen spellings): **`HeadParts, FaceMorph, FaceParts, TintLayers, HairColor, HeadTexture,
> TextureLighting`** (+ optionally **`WornArmor`** for body/skin-tone consistency). `TextureLighting` is
> the `Color` field xEdit shows as **QNAM**; `TintLayers` is one token. Do NOT describe this as "what
> facefixer does" — facefixer copies a 14-field mask with **NO `Race`**; FacegenBaseline *does* forward
> `Race`. `Race` is a real writable NPC_ field houseCARL may forward, but don't claim facefixer fixes a
> wrong-`Race` NPC.

**Pairing rule:** Fix C only cures dark face if the matching facegen FILES also win the VFS — so Fix C and
Fix B are siblings, often applied together. Forwarding TintLayers/WornArmor without the matching body skin
causes a neck seam. For an existing-NPC override the defining master (and facegen folder) does NOT change;
houseCARL cannot bake new geometry, so Fix C still needs a matching facegen file to exist somewhere.

### Fix D — Correct skin-tone via record edits — **houseCARL-record (+ houseCARL-nif for a baked path)**
For head/body color mismatch that is a *record* problem (Cause J): `set_field` TextureLighting (QNAM
Color), re-forward TintLayers/WornArmor/HeadTexture to a consistent source. If the mismatch is instead baked
into the `.nif`'s **texture-slot** paths, `nif_inspect` READS which slot is wrong and `nif_set set_path`
REWRITES it (verified) — the `.dds` **pixels** stay unreadable, and a **material**-path edit is still
NifSkope, but a plain diffuse/normal/tint slot swap is now houseCARL-doable. Editing TintLayers/morph WITHOUT
regenerating facegen can itself reintroduce discoloration until Ctrl+F4 or FDF applies — pair record
appearance edits with a re-bake/FDF.

### Fix E — ESLify / compaction repair — **houseCARL-file + houseCARL-nif**
For Cause F/G. (a) rename/place the `.nif`/`.dds` to the new (post-compaction) FormID — **houseCARL-file**
(computable from the current FormKey); (b) rewrite the FaceTint `.dds` path embedded in each `.nif`'s
`BSShaderTextureSet` slot 6 to the new FormID — `nif_inspect` READS that slot to *confirm it still points at
the old FormID*, and `nif_set set_path texture_slot=6` REWRITES it (verified before landing). Both halves are
now at the data layer.
**FaceGenEslify automates only the rename — its embedded-path edit is a MANUAL NifSkope step (its own
README step 9); houseCARL now performs that manual step itself via `set_path`, so don't route the user to
NifSkope for it.** (ESLifyEverything/ESLifier may automate both; unverified.) Or CK re-bake (Fix A) writes
correct files for the new IDs. Best practice to state: **don't compact facegen-carrying plugins** unless
running a fixer — and note that a bulk rename across many NPCs is still faster in a dedicated batch tool than
one `set_path` per mesh.

### Fix F — Face Discoloration Fix (FDF, runtime) — **runtime-mod (instructed)**
An SKSE plugin that prevents the engine from discarding tint during the regen fallback, so a
missing/mismatched facegen renders with correct **color** instead of black — a runtime equivalent of
Ctrl+F4 **for tint only**. Hard limits to state: (1) fixes **discoloration only** — NOT the intended custom
sculpt (a conflict that broke the look yields a colored-but-wrong face); (2) regenerates from the record's
morph data, so a custom sculpt that lived only in the baked `.nif` is lost; (3) ~30–70 ms/NPC vs ~0.2 ms to
load a preprocessed file; (4) does NOT fix Cause R, Cause M, or the RaceMenu/SKEE classes (U–X).
**Diagnostic consequence:** with FDF installed, dark faces may be suppressed in-game even though the
on-disk desync is still present — "looks fine in game" does not mean the diagnosis is clean (the FDF-off
litmus). Recommend FDF as a safety net; still report and fix the underlying desync.

### Fix G — Add missing master / remove orphan override — **houseCARL-record**
For Cause L. houseCARL writes **masters-by-construction**, so any override it authors carries correct
masters — it structurally avoids the bug a hand-edited plugin causes. Diagnostically: a dark face where
`asset_status` shows the **correct** file winning AND the record looks right points away from VFS desync
toward a master/resolution problem — confirm via `read_record` before reaching for a file fix (Fix B will
not cure a missing-master dark face).

### Fix H — Rewrite skin slots in the `.nif`; instruct texture compression — **houseCARL-nif (+ instructed)**
For Cause I (the **skin** texture slots inside each head `.nif` — what NPC Facegen Patcher rewrites, leaving
the FaceTint slot untouched) and Cause P (oversized textures → Cathedral Assets Optimizer/Ordenator).
houseCARL detects the *scenario*, reads the source TXST record, **reads the `.nif`'s slot paths** via
`nif_inspect` (naming the exact wrong slot — e.g. "slot 0 diffuse still points at the vanilla skin"), and now
**rewrites those slots itself** with `nif_set set_path texture_slot=0/1` (verified) — doing NPC Facegen
Patcher's per-mesh edit at the data layer. **Routing rule:** wrong/stale **FaceTint** path (slot 6) →
`set_path` slot 6 (was FaceGenEslify's manual step); wrong **skin diffuse/normal** path (slots 0/1) →
`set_path` slot 0/1 (was NPC Facegen Patcher); a **bulk** patch across a whole mod's meshes is still faster
in NPC Facegen Patcher; oversized `.dds` **pixels** (Cause P) stay a texture tool; general missing-tint
regeneration → FDF.

### Fix I — RaceMenu/SKEE runtime repair — **runtime-mod / report (houseCARL no-op, except W's file half)**
For Causes U–X. **U:** re-open `showracemenu`/`showlooksmenu player 1`, re-apply/re-load the preset; install
OverlayFix or the SKEE cosave Load Crash Fix; read `skse64.log`. **V:** match SKSE↔Skyrim runtime↔RaceMenu
versions; delete stray loose `CharGen.pex`/`NiOverride.pex`/`RaceMenu*.pex`; read `skse64.log`. **W:**
instruct Sculpt→Export Head (writes to `Data\SKSE\Plugins\CharGen\`, **distinct** from the facegen VFS
paths) or CK Ctrl+F4 — once the `.nif`/`.dds` exist, `place_asset` them. **X:** instruct the re-applying
overlay framework; if the *tintmask `.dds`* itself is the problem, that's the normal facegen-tint path
(`place_asset` covers it). houseCARL has **no read or write** into the SKSE co-save / SKEE runtime state —
surfacing that limit IS the Q3-honest move.

---

## 5. Symptom → likely cause

| Symptom | Most likely | Notes |
|---|---|---|
| **Only the PLAYER is grey; NPCs fine** (after reload/update/crash) | U/V (RaceMenu/SKEE runtime) | **The single strongest exclusion — OUT of lane.** The desync family hits NPCs; the player's face is runtime/co-save state. Phrase "strongly suggests," not "proves." |
| **Player grey AND all RaceMenu sliders/overlays missing** game-wide | V (skee64.dll disabled) | Out of lane; suspect first if it appeared right after a Skyrim/Steam update. |
| **Dark/grey/black face, sharp neck seam** (head darker than body) | A–H, N (record↔facegen desync) | The canonical dark-face bug. FDF masks the *color*; the real fix is matching record↔file or re-baking. |
| **Brown face** matching nothing | Q (weight/scale, save-baked) | Re-issue `setnpcweight`; if save-baked, new game or ReSaver. Runtime, not a file fix. |
| **Grey/black with matched record+file**, clears on save+reload | P (texture memory) | Oversized/uncompressed `.dds` → compress. (R = broken skin path — FDF will NOT fix R.) |
| **Purple or bright-white face** | missing **texture file/path** | A referenced texture isn't found — a texture-asset problem, not facegen (dark face *has* a file, the wrong one). |
| **Neck seam, face correctly colored but ≠ body** | I or J | I = `.nif` hardcodes vanilla skin — `nif_inspect` shows the slot-0/1 skin path and `nif_set set_path` rewrites it (the NPC Facegen Patcher edit, now at the data layer). J = record QNAM/TintLayers inconsistent (houseCARL-record). |
| **Headless / floating parts / missing geometry** | M or H | Required head-part/hair mod disabled, or a HDPT doesn't resolve — report/install, or forward correct HeadParts. |
| **NPC built from a RaceMenu preset is grey** | W (sculpt in `.jslot`, never exported) | The preset alone is not facegen. Instruct Export Head / Ctrl+F4; then `place_asset`. |
| **NPC missing warpaint/makeup overlay** specifically | X (NiOverride runtime overlay) | Overlays are runtime SKEE state, not facegen tint — out of lane for the overlay portion. |
| **Shiny/oily face** | specular/ENB | **Not facegen.** Out of lane. |
| **Ash-pile / disintegration** | death/disintegration script state | **Not facegen.** Out of lane. |
| **"Shiny potato" child head** after a re-bake | child re-baked in CK (unique TRI) | A CK-bake hazard — do NOT blanket-recommend Ctrl+F4 for children/special actors. |

---

## 6. Embedded `.nif` texture references — now READABLE *and* WRITABLE

The head `.nif`'s `BSShaderTextureSet` carries TWO logically distinct texture references in ONE block.
**`housecarl_nif_inspect` READS both** (as `tex[<slot>]: <path>` under `sections=paths` / `shapes`;
the slot number it prints is the **binary** index), and **`housecarl_nif_set set_path` REWRITES either**
(pass `texture_slot` + the new `path`; verified before it lands) — both the old "can read neither" and the
"can read, can't write" boundaries are retired:

1. **The per-NPC FaceTint `.dds`** — **binary slot 6** (NifSkope slot 7, the subsurface/tint slot). This is
   the FaceGenEslify target. `nif_inspect` prints it as `tex[6]: …\facetint\<DefiningMaster>\<00…ID>.dds` —
   e.g. the real Lucien read: `tex[6]: textures\...\facetint\lucien.esp\00005900.dds` — and `nif_set
   op=set_path target=<shape> texture_slot=6 path=<new>` rewrites it.
2. **The base SKIN texture set** — race/body diffuse (`tex[0]`), normal (`tex[1]`), etc. This is the NPC
   Facegen Patcher target (it rewrites skin slots `fti-6, fti-5, fti-4, fti-3, fti+1` and deliberately
   leaves the FaceTint slot untouched); `nif_set set_path texture_slot=0/1` rewrites the diffuse/normal slot.

Consequence — the boundary moved from *read* all the way to *verified write*: **Cause I, Cause R's in-`.nif`
form, and the FaceTint-path half of Fix E are now both DIAGNOSABLE *and* FIXABLE inside the file** — read the
mesh, see which slot holds which path, name the exact mismatch (e.g. "slot 6 still points at the
pre-compaction FormID"), then `set_path` it right. What houseCARL still cannot do is read the `.dds`'s
**pixels**, swap a **material / `.tri` / xml** string ref (`set_path` only reaches `BSShaderTextureSet`
texture slots), or vouch for the **render**. So the honest line is now: *"I read slot N — it pointed at X,
wrong because Y — and rewrote it to Z; the write is verified at the data layer, so confirm the face in
game."* Route by the slot you read: stale FaceTint (slot 6) → `set_path` slot 6 (was FaceGenEslify's manual
NifSkope step); wrong skin diffuse/normal (slots 0/1) → `set_path` slot 0/1 (was NPC Facegen Patcher); a
material path → still NifSkope; general missing tint → FDF; a **bulk** cross-mod rename → still faster in the
batch tool. You no longer guess the broken slot, and for a single mesh you no longer hand off the edit.

---

## 7. Edge cases and gotchas

- **The front door is the first failure point.** Resolve via EditorID (best) or name (disambiguate). A
  **console-clicked FormID is a RefID and/or a runtime-indexed base id** — houseCARL's FormID↔FormKey
  bridge is unshipped, so the high byte (and the ESL `FExxx` slot) can't be mechanically resolved. Route to
  name/EditorID, or confirm the 6-hex local as a hypothesis. Never trust the console high byte as identity.
- **No cross-folder fallback** (§1). Step 3 asks "what wins this exact path, and does it match the record?"
- **"Rides the master's facegen" is the same keyed path, not a fallback** (§2).
- **ESL/FE prefix:** filename never contains "FE"; compute from the current FormKey. Folder unchanged on
  eslification. A freshly-eslified mod's loose facegen sits at the OLD names until renamed.
- **Beast/custom races:** identical path scheme. Common failure is a custom-race mod that never exported
  facegen (Cause N) → `asset_status` no winner → CK bake, not `place_asset`.
- **Male/female:** NO separate folder or filename rule — sex affects which head parts/tint the CK bakes.
  Risk: `asset_status` "file wins" ≠ "file is the right sex" — but `nif_inspect`'s **shape names** often give
  it away (a `Male…`/`Female…` baked part, e.g. `MaleMouthHumanoidDefault` in the real Lucien read), so a
  wrong-sex facegen is now partly checkable at the data layer. Still not a render guarantee — confirm in-game.
- **`nif_inspect` reads the winner, or a named provider (`mod=`).** It resolves through the same VFS as
  `asset_status`, so you can read the copy the game uses OR a specific mod's copy and *compare their baked
  shape names / tint paths* — "the winning file carries a different NPC's shapes than mod A's copy" is now a
  data-layer finding, not a guess. It reads values, never pixels or geometry; the render check still stands.
- **SPID/SkyPatcher-distributed appearance:** houseCARL reads *plugin* records, so the plugin-winner it
  sees may not be the in-game face. Warn; route to FDF or matching the distributed head parts. (DynDOLOD
  does object-LOD, **not** NPC appearance — do not group it with SPID/SkyPatcher.)
- **RaceMenu/SKEE is a whole out-of-lane class** (U–X). Player-only grey is the strongest exclusion. A
  `.jslot` is not facegen (W); NiOverride overlays are runtime state (X). Don't conflate the CharGen working
  folder (`Data\SKSE\Plugins\CharGen\`) with the facegen VFS paths.
- **BSA-vs-loose stale facegen:** loose always beats BSA — including from a **disabled** mod or MO2
  overwrite (Cause E). The classic "fine in xEdit/CK but dark in game." Trust `asset_status`'s reported
  winner (a "Manage Archives"-on user can invert the rule). Drop "archive-invalidation /
  bInvalidateOlderFiles" language for SE — that's LE-era; SE honors BSAs natively.
- **Vanilla/CC baseline:** vanilla NPCs ship matching facegen in the base-game BSAs under
  `Skyrim.esm`/`<DLC>.esm`; record↔file are in sync out of the box. The baseline file lives under the
  **defining-master** folder, not an overhaul's — anchoring to the conflict winner points at the wrong
  subfolder (keep the defining-master rule).
- **Pair invariant:** always query/place/extract BOTH `.nif` and `.dds` for a FormKey. For a same-FormKey
  forward the embedded path is valid by construction (Fix B) — place the pair together, don't over-refuse.
- **Verification ≠ provenance:** a green `asset_status` proves the right file wins, not that it renders
  correctly. Hand off the in-game `setnpcweight`/`prid`/`moveto` check; never put `coc` in a `.bat`;
  facegen is baked into the save (a true clean check is a new game or ReSaver removal).
- **Children/special actors:** never blanket-recommend a CK re-bake (unique child TRI → "shiny potatoes";
  Astrid-type faces corrupt others).
- **LE→SE ports:** SE will not read Oldrim facegen `.nif` format — re-bake in SE CK (Cause O).

---

## 8. Community tools

houseCARL drives the *file*, *record*, and (Wave 2) the whitelisted *`.nif`-value* halves itself — the
per-mesh embedded texture-path / shape-name / flag / alpha / partition edits; the rest (bulk batch runs, the
`.dds` pixels, geometry, RaceMenu state) it names and instructs.

| Tool | What it does | When it's the right tool |
|---|---|---|
| **Face Discoloration Fix (FDF)** (Nexus 42441) | Runtime SKSE; NOPs the tint-discard on regen so a missing/mismatched facegen renders with correct **color** | Universal **color** safety net; when only CK can bake but the user wants a quick mask. Fixes color, not the sculpt, not skin textures, not RaceMenu state. Its presence can mask on-disk desync. |
| **EasyNPC** (Nexus 52313) | Consolidates many NPC overhauls into one standalone mod, copying BOTH the chosen face mod's record edits AND its facegen (to BSA); uses `FormKey.ModKey.FileName` + `"00"`+local-id (same stack as houseCARL) | Whole-load-order NPC consolidation; eliminating desync by co-locating record+facegen |
| **NPC Plugin Chooser** (GitHub Piranha91) | Per-NPC GUI patcher; copies chosen records AND forwards matching facegen; names the two winners explicitly — **LoadOrder winner** (plugin) vs **FaceGenOrder winner** (file) | Per-NPC choice across overlapping overhauls, doing both halves |
| **facefixer** (Synthesis-Collective) | Synthesis/Mutagen; `DeepCopyIn`s a 14-field appearance mask (**no `Race`**) onto each load-order-winning NPC | Record-side appearance forward (assumes the matching facegen already wins). The list houseCARL Fix C is modeled on — but the editorial minimal set is houseCARL's own. |
| **FacegenBaseline** (GitHub SteveTownsend) | Synthesis; for NPCs whose winner did NOT change HeadParts vs the master, forwards a baseline mod's appearance (records only, no files). Forward set DOES include `Race`. | Backfilling baseline appearance for NPCs a non-appearance edit reverted (Cause C). Its `HeadParts.SetEquals` gate is **necessary-not-sufficient** — see §9. |
| **FaceGenEslify** (Nexus 46208) / ESLifyEverything / ESLifier | xEdit pre/post-compaction FormID dump + EXE that batch-**renames** facegen to the new compacted FormIDs. **Its embedded `.nif` FaceTint-path edit is a MANUAL NifSkope step (README step 9) — which houseCARL now performs itself via `nif_set set_path` slot 6.** | The **bulk** rename across a whole eslified mod (Cause F/G); for a handful of meshes, houseCARL does both the rename and the embedded-path rewrite at the data layer |
| **NPC Facegen Patcher** (Nexus 41008) | xEdit script; rewrites the **skin** texture slots inside head `.nif`s (leaves the FaceTint slot untouched) so face textures match race-specific body textures. "Not used to fix grey/black/brown face bugs." | The **bulk** face-vs-body fixup across many meshes (Cause I). For a single mesh, `nif_set set_path` slot 0/1 does the same edit at the data layer — NOT the desync dark-face bug either way |
| **Dark Face Issue Reporter** (Nexus 42133) | xEdit script; enumerates every NPC from a chosen master, checks each record's HeadParts against the resolvable NIF, flags `DarkFaceIssue`; emits in-game `prid`/`moveto` batch files. Does NOT auto-fix. | Whole-mod / load-order batch diagnosis. The batch model the SKILL.md batch flow mirrors — now at BOTH layers: the VFS winner (`asset_status`) *and* the record-HeadParts-vs-baked-NIF-names check (`nif_inspect`), which is DFIR's core comparison done at the data layer. |
| **RaceMenu / SKEE + fixers** (RaceMenu 19080; cosave Load Crash Fix 173617; OverlayFix 138586; CharGen Export SE 29954) | Runtime sculpt/tint/overlay via skee64.dll, persisted in the SKSE co-save; Export Head writes `.nif`/`.dds` to `Data\SKSE\Plugins\CharGen\` | The out-of-lane grey-face class (U–X). houseCARL no-op except placing an already-exported head. |

---

## 9. Residual uncertainty

Route AROUND these — do not assert them. The reliable tell is the **file-winner-vs-record-winner mismatch**,
not predicting dark face from field deltas.

- **Exact engine field(s) that invalidate a cached facegen.** Sources agree a HeadParts-list mismatch
  triggers the regen (mode ii), but no source enumerates every field. Don't predict dark face from
  record-field deltas alone — use the file-vs-record-winner mismatch.
- **FacegenBaseline's `HeadParts.SetEquals` proxy is necessary-not-sufficient.** A winner that changed only
  morph/tint/HeadTexture but NOT HeadParts is misclassified as "no appearance change." So houseCARL's own
  Cause-B / Step-3 "did the winner change appearance" check must compare **`FaceMorph` / `TintLayers` /
  `HeadTexture`** in addition to `HeadParts`, not `HeadParts` alone.
- **Mode ii is now NAME/PATH-detectable *and* repairable, but not render-verifiable.** `nif_inspect` reads
  the `.nif`'s baked shape names and embedded tint/skin paths, so a *mismatch* against the record's HeadParts
  is a confirmed mode-ii finding — no longer a blind spot — and `nif_set` (`rename_shape` / `set_path` /
  `set_flags` / `set_alpha`) now *repairs* the name/path/flag class it finds, verified before landing. The
  residual is narrower but real: a name/path *match*, and even a *verified write*, is necessary-not-sufficient
  — the geometry, the `.dds` pixels, and the final render stay unseen — so "file wins AND names/paths match
  (or were just rewritten), still dark" is the case that forces the in-game handoff for the last mile. Report
  what you read and wrote (names/paths, verified) as fact and the render as unverified; don't upgrade a
  name-match or a green `nif_set` verify to "the face is correct."
- **SE vs LE `.nif` rejection (Cause O):** hard format rejection vs partial render is unconfirmed at the
  binary level — affects how absolute the "must re-bake LE ports" instruction should be.
- **ESL facegen filename** is correct by construction (`FaceGenPath` renders the current FormKey's local
  id); an on-rig eyeball against a known ESPFE follower is a confidence-raiser, not a gate.
- **Console-clicked-FormID resolution** is blocked until the runtime-FormID bridge ships — route around it
  via name/EditorID.
