---
name: tool-output-awareness
description: Recognize which load-order plugins and mods are generated TOOL OUTPUTS — Reqtificator (`Requiem for the Indifferent.esp`), ParallaxGen (`PGPatcher.esp` / `PG_1.esp`), DynDOLOD (`DynDOLOD.esm` / `.esp`, `Occlusion.esp`), Synthesis, TexGen, xLODGen, and NPC Plugin Chooser 2 (the mod with a generated `NPC_Token.json`) — and keep their regenerated records and asset paths out of any patch you author, because the user re-runs these tools, so a frozen copy goes stale or silently breaks (ParallaxGen rewrites mesh/texture paths every run). Use whenever you create a patch, override or forward a record, resolve a conflict, pull a conflict tree, or study how a mod works and the winner — or a referenced mesh/texture — traces to one of these generators. Reading one is fine only when debugging an in-game problem it may have caused; otherwise don't base your work on it, and never copy its changes into a patch. Load this before pulling a conflict tree or creating any patch, not after a regenerated plugin's data is already baked in and breaks on the next tool run.
---

# Tool Output Awareness

## Overview

Several tools in a Skyrim build don't ship records a human authored — they **generate** them, and the
user **re-runs the tool**, overwriting the output every time. Reqtificator, ParallaxGen, DynDOLOD,
Synthesis, TexGen, xLODGen and NPC Plugin Chooser 2 are all in this class. Their plugins (and the meshes /
textures they emit) are **derived artifacts**, not design intent.

This is an operational guardrail, not a procedure: it changes how you treat the load-order winner across
every read, conflict pull, and patch you author. The failure it prevents is concrete and houseCARL hits it
repeatedly — it reads the true load-order winner, sees a generated plugin sitting on top, and **forwards
that generated data into the patch it creates**. The next time the user runs the tool, that frozen copy is
either stale, a duplicate the tool fights, or — for path-bearing data like ParallaxGen's rewritten mesh and
texture paths — simply wrong, because the generator assigns different paths on the next run.

## The core rule

Two parts, and they apply at **both** the record layer (xEdit / plugin records) and the file layer (loose
meshes and textures in the mod folder):

1. **Never copy a tool output's changes into a patch you create — ever.** Not its records, not its
   computed field values, not a repoint to a path it generated. This holds even while debugging: a fix that
   bakes regenerated data into a hand patch is wiped or contradicted on the next tool run.
2. **Outside debugging, don't even base your work on a tool output.** When you create a patch, author a new
   mod, or study how a mod works, look **past** the generated overrides to the real authored records
   underneath. Reading a generated plugin to learn a mod's design tells you what the *tool* produced, not
   what the *mod* does.

The reason both hold: tool outputs sit **on top of** (or are regenerated from) the authored load order. The
generator runs again and re-derives its result. Author against the **source** that feeds the generator and
let it regenerate; freeze nothing derived.

## Recognize a tool output

Identify by the most reliable signal available, in this order. The first two are unambiguous; the third
needs the false-positive check below.

**1 — Fixed plugin filenames (bulletproof).** The plugin name *is* the signature, whatever mod folder holds
it:

| Generator | Output plugin(s) |
|---|---|
| Reqtificator (Requiem) | `Requiem for the Indifferent.esp` |
| ParallaxGen | `PGPatcher.esp`, `PG_1.esp` |
| DynDOLOD | `DynDOLOD.esm`, `DynDOLOD.esp`, `Occlusion.esp` |

**2 — Generated marker files (bulletproof).** A file the tool writes into its output mod folder identifies
the mod no matter what its plugin is named:

- **NPC Plugin Chooser 2** → the mod folder contains a generated **`NPC_Token.json`**. Its plugin can be
  named anything (e.g. an `… NPC Appearances.esp`) — go by the token, not the name.
- **ParallaxGen** also drops **`ParallaxGen_Diff.json`** in its output folder (confirms it beyond the fixed
  plugin names; this folder also holds the regenerated `meshes\` / `textures\`).

**3 — The generated output folder (needs the false-positive check).** These have no fixed plugin name; the
output is the **mod directory the tool wrote into**, conventionally named "… Output":

- **Synthesis** → the Synthesis output mod. Treat **every plugin inside it** as generated. Plugin names are
  pipeline-configured and often do **not** contain "Synthesis" (a real output folder can hold, e.g., a
  `… High Poly Head Patcher.esp`) — identify the **folder**, then the whole mod, not by per-plugin name.
- **TexGen** → output folder whose name contains "texgen"; **textures only, no plugin** (pure file layer).
- **xLODGen** → output folder whose name contains "xlodgen"; **meshes + textures, no plugin** (pure file
  layer).

### Not a tool output (false positives to rule out)

- **Resource / fixes / add-on / patch mods that merely carry a tool's name in the folder** are **inputs**
  the tool consumes, not its output: e.g. `DynDOLOD 3 Resources`, `DynDOLOD DLL NG and Scripts`,
  `DynDOLOD TexGen Fixes (…)`, a `… DynDOLOD Add-On`, or a downloaded `… xEdit-Synthesis Patch`. The
  decisive test: does the mod hold the generator's **own plugin / marker**, or is it the **directory the
  tool wrote into**? If neither, it's an input — patch it like any normal mod.
- **"It was downloaded / has a Nexus mod id" does NOT mean "not a tool output."** Modlists routinely
  pre-generate these outputs and redistribute them, so the output mod can carry a real Nexus id and install
  file. `meta.ini` install source is therefore **not** a reliable discriminator — trust the fixed plugin
  filenames and marker files instead.

## Authoring vs debugging: the branch that decides everything

Ask one question before you read or write anything: **am I debugging an in-game problem, or authoring?**

- **Debugging an in-game issue** (a CTD, a visual bug, a wrong stat, a dark face, a LOD seam, a purple
  mesh): a tool output is a legitimate — often the *prime* — suspect. **Read it freely.** That's the only
  context where you reference one. But still obey rule 1: don't "fix" it by baking corrected data into a
  hand patch the next tool run will discard. Fix the **source mod or the tool's configuration** and have
  the user re-run the tool; if a true standalone override is genuinely required, say plainly that it must be
  re-applied (or the tool re-run) after every regeneration.
- **Authoring** — creating a patch, building a new mod, or learning how a mod works: treat tool outputs as
  **transparent**. Don't read them as your basis, don't forward them. Look past them (next section).

## Patch against the source, not the tool output

When the load-order winner of a record you need to patch is a tool output, do **not** build on that winner.
Pull the conflict tree and look underneath:

1. Read with the conflict tree — `housecarl_read_record` or `housecarl_batch_record_detail` with
   `conflict_tree: true`, or `housecarl_cross_plugin_query` for who-touches-what.
2. **Classify each override.** Mark the tool-output overrides (by the signals above).
3. **Base your patch on the highest NON-tool-output override** — the real authored winner — and its
   masters. That record carries the design intent; the generator re-stacks its derived result on top when
   it next runs, incorporating your change rather than being frozen by it.
4. Author your edit (`housecarl_set_field`, `housecarl_bulk_apply`, `housecarl_create_record`) against that
   source, and re-run the generator as the final build step.

**Requiem is the textbook case.** `Requiem for the Indifferent.esp` is the Reqtificator's merged output, and
some lists also re-wrap per-mod Reqtificator results into renamed patches (see "Modlist-specific renames").
Read it only to learn the *target* values Requiem assigns; author your override against the **source**
record, then re-run the Reqtificator — it leaves a correct direct override alone or rebalances it. Copying
its values into your patch freezes Requiem's derived numbers and fights the next Reqtificator run.

## Assets: the same rule at the file layer

ParallaxGen, TexGen, xLODGen, DynDOLOD and NPC Plugin Chooser 2 regenerate **meshes and textures**, not just
records. The same guardrail applies to `housecarl_place_asset` / `housecarl_bulk_place_asset` /
`housecarl_asset_status` and any NIF path work:

- Don't treat a **generated** mesh/texture as the source of truth to copy or forward into a new mod.
- Don't **repoint a record to a generated asset path** — ParallaxGen reassigns those paths every run, so the
  link breaks on regeneration.
- Fix the **source** mesh/texture in the originating mod, or the tool's settings, then re-run the tool. When
  debugging a generated asset (e.g. a purple parallax mesh, a dark NPC-merge facegen), read it to diagnose,
  but route the fix to the source — see `facegen-diagnostics`.

## Modlist-specific renames

Lists rename and re-wrap generators' output. The three "folder-named" tools emit user-chosen names, and a
list may fold Reqtificator results into custom-named per-mod patches or name its NPC-Chooser output anything.
The **principle** is fixed — a plugin that carries a generator's derived data is a tool output for that data,
however it's named — but the specific names are a property of the modlist, not of this skill. Learn them from
the modlist's own `CLAUDE.md` / project memory, where modlist facts belong, rather than hardcoding them here.
When a plugin looks like it carries generated data but you can't confirm it against the signals above, say so
and ask — don't silently treat it as (or as not) a tool output.

## Common mistakes

- **Forwarding the load-order winner without checking what it is.** The single highest-frequency error: the
  winner is `DynDOLOD.esp` / `Requiem for the Indifferent.esp` / `PGPatcher.esp`, and its data goes straight
  into the new patch. Always classify the winner before you forward it.
- **Reading a tool output to learn how a mod works.** RFTI / Synthesis show you the *generated* version of a
  mod, not its design. Study the mod's own records; the generated override is derived noise for that purpose.
- **Trusting plugin names for Synthesis.** Synthesis plugins are named by the pipeline and frequently lack
  "Synthesis" — identify the output **folder** and treat all its plugins as generated.
- **Treating a "… Resources" / "… Fixes" / "… Add-On" mod as the tool's output.** Those are inputs. Patch
  them normally.
- **Using "it has a Nexus id" to rule out tool-output status.** Pre-generated outputs are redistributed with
  real Nexus metadata; go by the plugin filename / marker file.
- **"Fixing" a generated asset by baking it into a patch or new mod.** The next tool run ignores it. Fix the
  source or the tool config and regenerate.

## Before you ship a patch

A quick self-check that catches the failure mode:

- For every record in the patch: is its data sourced from a **tool output**? If yes, re-base it on the real
  authored record and confirm the generator will re-apply on the next run.
- Does any field hold a **generated path** (a ParallaxGen mesh/texture path, a DynDOLOD-injected reference)?
  If yes, remove it — fix the source and regenerate instead.
- Did the patch gain a **tool-output plugin as a master** (e.g. `Requiem for the Indifferent.esp`,
  `DynDOLOD.esp`)? That's a strong signal you forwarded derived data — re-base on the source master.

## Real example

A `STAT` for a rock wins the load order in `PGPatcher.esp` (ParallaxGen), which rewrote its model path to a
generated parallax mesh under the PG output folder. You're asked to patch that rock's material/keyword.

- **Wrong:** `housecarl_read_record` returns the `PGPatcher.esp` winner; you forward it, keeping the
  generated `meshes\…\rock_parallax.nif` path. Next ParallaxGen run regenerates with a different path → your
  patch points at a stale/incorrect mesh, and you've also taken `PGPatcher.esp` as a master.
- **Right:** pull `conflict_tree: true`, see `PGPatcher.esp` on top and the real authored `STAT` (the rock
  mod, or its master) beneath. Patch the keyword against **that** source record — leave the model path alone.
  Re-run ParallaxGen as the build step; it re-applies its mesh edit on top of your now-correct keyword. No
  generated data is frozen into the patch.

## Notes

- The debugging exception is about **reading**, not fixing: a tool output may be the cause, so inspect it —
  but the fix still lands on the source / config + a regeneration, never as frozen output in a patch.
- This guardrail composes with `facegen-diagnostics` — asset-layer fixes (e.g. a dark NPC-merge facegen)
  route to the source, not into a frozen patch.
- Generated marker files seen in practice — `NPC_Token.json` (NPC Plugin Chooser 2) and
  `ParallaxGen_Diff.json` (ParallaxGen) — are the most robust folder-level signals when a plugin name is
  ambiguous.
