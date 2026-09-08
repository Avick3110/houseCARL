---
name: facegen-diagnostics
compatibility: Requires the houseCARL MCP server and a configured Mod Organizer 2 instance.
description: >-
  Diagnoses and repairs the dark / grey / black-face NPC bug in Skyrim SE by diffing which mod wins the head .nif against which wins the face .dds for the same NPC, then the record winner behind them. Use for any discolored face, neck seam, "fine in xEdit but wrong in game", NPCs gone dark after an ESL-compaction or merge, or any FaceGen / facegeom / facetint mention. Not a purple or white face (a missing texture) and not player-only grey (RaceMenu/SKEE); copying a face onto another NPC, or a face gone dark right after a copy, is housecarl:npc-appearance-copy. Load before judging any face bug — the mesh-versus-tint pair decides the fix.
---

# Facegen diagnostics

Sibling skills are named here in the Claude Code plugin form (`housecarl:<skill>`); on Codex the same
skill is the bare folder name. Paths spelled `meshes\actors\…` are the engine's own `Data`-relative
literals, not filesystem paths — houseCARL takes either slash.

## 1. Two independent precedences

A baked NPC face is two preprocessed per-NPC files, a head `.nif` and a face tint `.dds`, and two
separate systems decide who wins them:

- **Plugin load order** decides which mod's **NPC record** wins → `housecarl_records`.
- **The MO2 VFS** decides which mod's **facegen file** wins → `housecarl_asset_status`. Loose beats BSA;
  among loose the higher-priority mod (then overwrite) wins; among BSAs the later-loaded plugin's wins.

Dark face is the **desync** between the two: for one NPC the file winner's source is not the record
winner's appearance source, or nothing wins the computed path at all — the engine then regenerates the
head from the record and drops the tint. That is why **xEdit shows no conflict while the face is dark**.
Checking both winners is the whole advantage; a record-only tool cannot see this.

houseCARL works at the data layer only. It cannot bake facegen **geometry** (that is the Creation Kit's
Ctrl+F4), cannot read a `.dds`'s **pixels**, and cannot judge a **render**. Say those limits out loud
rather than implying more — a verified value is provenance, not a correct face.

## 2. Step 0 — rule these out first

Each of these is out of lane, and the desync flow would mislead. Name the real owner and stop.

- **Player grey, NPCs fine** (after a reload, crash or game update) → RaceMenu/SKEE co-save state. If
  every RaceMenu slider and overlay is gone game-wide, `skee64.dll` did not load: match SKSE ↔ runtime ↔
  RaceMenu and read `skse64.log`. houseCARL is a no-op here.
- **Brown face** matching nothing → weight baked into the save. Re-issue `setnpcweight`; if it is
  save-baked, a new game or ReSaver.
- **Purple or bright-white face** → a missing *texture* file. A different lane entirely.
- **Shiny or oily face, ash pile** → specular/ENB or script state. Not facegen.
- **An `FFxxxxxx` base id, or SPID-distributed appearance** → houseCARL reads *plugin* records, so the
  winner it sees may not be the in-game face. There is no SPID overlay: warn, and route to the
  distributed head parts. **SkyPatcher it can read** — `housecarl_records` with
  `source={"overlay":"skypatcher","state":"post"}` returns the record after the layer replays, race and
  skin included, so read that before declaring a blind spot.
- **An NPC built from a RaceMenu `.jslot` preset with no facegen** → a preset is not facegen. Instruct
  Sculpt → Export Head, or Ctrl+F4. Once the `.nif` and `.dds` exist they can be placed.

## 3. Step 1 — diff the mesh winner against the tint winner

This is the opening move on any face bug that survives Step 0, and on a large order it is where the
findings are. Derive both paths from the FormID (§4) and resolve **both in one call**:

```
housecarl_asset_status(asset_paths=[
  "meshes/actors/character/facegendata/facegeom/Skyrim.esm/00013BBF.nif",
  "textures/actors/character/facegendata/facetint/Skyrim.esm/00013BBF.dds"])
```
```
facegeom/Skyrim.esm/00013BBF.nif   winner: Bijin NPCs (loose)   providers: Bijin NPCs (loose), Skyrim - Meshes0.bsa (BSA)
facetint/Skyrim.esm/00013BBF.dds   ABSENT — no provider
```

Then branch on the two winners:

| The pair | What it is | Where to go |
|---|---|---|
| Same source | Not a pair fault | §5 — the record axis |
| Different sources | A cross-bake: the head from one mod, the tint from another | §6 — re-place the pair from one source |
| One present, one absent | The hard fault, and the one that accounts for nearly every finding on a big order | §6 — place the missing half from the source that has the other; if it exists nowhere, Ctrl+F4 |
| Neither present | Nothing to place | §5 — does this NPC own a face at all; if it does, Ctrl+F4 |

A file winning at the path is necessary, not sufficient. When the pair is same-source and §5 finds the
record clean too, read the winning mesh itself with `housecarl_nif_inspect`: a baked shape name or an
embedded FaceTint path that disagrees with the record is a mesh-side fault, repaired per §11.

Two shortcuts: `housecarl_asset_status` `under=` resolves every file the order provides beneath a folder,
so one call over `meshes/actors/character/facegendata/facegeom/Skyrim.esm` answers for a whole master's
facegen set with no path list; and `housecarl_nif_inspect` `npc=` derives the geom path from the FormID
itself. Multiple providers for one path is the **common, healthy** case on a large modlist — report it
as a verify-if-unexpected signal, never as a detected fault.

## 4. The path derivation, and resolving the NPC

**The path is a pure function of the FormID** — no path is stored in the record:

```
meshes\actors\character\facegendata\facegeom\<defining master>\00<6-hex local id>.nif
textures\actors\character\facegendata\facetint\<defining master>\00<6-hex local id>.dds
```

The folder is the **defining master** — the plugin after the colon in `XXXXXX:Plugin.esp` — and **never
the conflict winner**. For a vanilla NPC an overhaul re-dresses, that is `Skyrim.esm\`, not the
overhaul's folder. There is **no cross-folder fallback**: the engine reads that one keyed path and
regenerates if nothing wins there. So ask "what wins this exact path", never "does it fall back".

Resolving the NPC a user names:

1. **Prefer EditorID, then display name.** `housecarl_records` with `types=["NPC_"]` and
   `where=["editorid contains Lydia"]`. More than one hit ("Guard", "Bandit") → list the candidates and
   let the user pick. Never auto-pick the first.
2. **An xEdit-style FormID** the user read somewhere: the high byte is *that person's* load-order
   index, not yours, so the 6-hex local under it is a hypothesis, not an address — no parameter takes a
   bare local id without its plugin. Find the record by EditorID or name as in step 1, then confirm the
   local id matches before acting on it.
3. **A runtime FormID** — the form the game, console, Papyrus and crash logs print, `FExxxYYY` or
   `XX######` — is read directly by `housecarl_records` and by `housecarl_nif_inspect` `npc=`, resolved
   against the current load order, and the response names the plugin it resolved to. Two traps remain: a
   console click selects the **placed reference**, not the base `NPC_`, so read it back and confirm the
   name before acting on it; and the write tools — `housecarl_place`, `housecarl_apply` and
   `housecarl_forward` share one write door — refuse the runtime form by design, so write with the
   `XXXXXX:Plugin.esp` form the read printed.

## 5. Step 2 — the record axis

Reached when the pair is clean, or when neither file exists. Two questions, both `housecarl_records`.

**Does the winning record change appearance at all?** Compare the appearance fields against the plugin
immediately beneath the winner — a winner can change morph or tint while leaving `HeadParts` alone, so
`HeadParts` on its own is not the test:

```
housecarl_records(formids=["013BBF:Skyrim.esm"], versus="previous_provider",
  project={"form":"delta","fields":["HeadParts","FaceMorph","FaceParts","TintLayers",
                                    "HairColor","HeadTexture","TextureLighting"]})
```
```
013BBF:Skyrim.esm   subject Bijin NPCs SE.esp   vs previous_provider Skyrim.esm
  TintLayers        3 layers        <- 0 layers
  TextureLighting   000000          <- 3C2E28
```

`versus="previous_provider"` is **refused** when the subject defines the record — an uncontested NPC has
no pole beneath it — and that refusal *is* the "no plugin changed this" answer, not an error. Unchanged,
or refused that way, means the NPC rides the master's facegen at the same keyed path — benign, so confirm the master's file resolves. Changed, with
no facegen anywhere, means there is nothing correct to place and the fix is a Creation Kit bake.

**Does this NPC own a face at all?** An NPC whose `Template` is set and whose template flags include
`Traits` inherits its appearance and has **no facegen of its own** — recompute the path against the
*template's* FormID. Ask it with `housecarl_records` on the NPC seed, `walk=` following the `Template`
link and `project={"form":"chain"}`: the chain form carries the per-category active-versus-masked
inheritance report for NPC template chains, so the answer comes back in one call rather than a
hand-read flag. A dark or missing actor whose *correct* file wins points instead at a missing master.

Field spellings are Mutagen's, not xEdit's — confirm any `NPC_` path with `housecarl:mutagen-reference`
before composing a write.

## 6. Step 3 — decide and fix

The invariant this skill owns: **the winning record's appearance and the winning facegen files must come
from the same source.** Two fixes move the two halves; often both are needed.

**Make the right files win.** `housecarl_place` copies the chosen files into one new MO2 mod folder. A
`formid` member with `kind` omitted places **both** FaceGen files:

```
housecarl_place(assets=[{"formid":"013BBF:Skyrim.esm"}],
  source_provider="Bijin NPCs", patch="Facegen Fix")
```
```
placed 2 files into "Facegen Fix"  (meshes\…\00013BBF.nif, textures\…\00013BBF.dds)  source: Bijin NPCs (loose)
```

`source_provider=` names whose copy to read for the whole set — `"*winner"`, a mod folder (reached even
when MO2 is not loading it), `overwrite`, `Data`, or a BSA filename — while a member's own `source=` names
one exact file, a single archive entry included, as `"<archive.bsa>|<entry>"`; whole-archive extraction is
`housecarl_bsa_extract`. One constraint: a `formid` member with no `kind` whose own `source=` is not a
full `.bsa` path is refused, so drive a pair from `source_provider=`, or set `kind=` per member and place
the halves separately. Place **both halves from the same source** — one alone re-creates the mismatch.

**Bring the right appearance onto the record.** When the file is the intended appearance and a
non-appearance plugin won the record, copy the appearance set into a patch:

```
housecarl_apply(
  bundle=["HeadParts","FaceMorph","FaceParts","TintLayers","HairColor","HeadTexture","TextureLighting"],
  assignments=[{"target":"013BBF:Skyrim.esm","from":"013BBF:Skyrim.esm",
                "from_source":"Bijin NPCs SE.esp"}],
  patch="Facegen Fix")
```

`bundle=` is the field set — which paths form an appearance set is knowledge this skill carries, not
something the tool owns — and `assignments=` pairs each target with the record it takes them from.
When the whole record from that plugin is wanted instead, `housecarl_forward` with `source=` copies it
verbatim.

Then tell the user to **enable** the new mod in MO2. A new mod folder lands last in the priority order,
so enabling is the step that makes the write win; re-resolve the path (§7) rather than asserting it.
Each write opens its **own** folder, so doing both leaves two mods to enable — write the record patch
first, then pass its filename as `into=` on the `housecarl_place` call to land the pair in that one mod.

Mesh-side repairs — a stale embedded FaceTint path after a compaction, a baked shape name that does not
match the record, a wrong skin slot — are `housecarl_nif_set` territory and live in the reference (§11).

## 7. Step 4 — verify

"Wrote it" is not "it wins" is not "it renders correctly". Four checks, and two of them are not yours:

| Check | Who runs it | What it settles | What it still cannot prove |
|---|---|---|---|
| Re-resolve the pair with `housecarl_asset_status` | houseCARL | Whether the placed copy now wins, and who still beats it if it does not | Provenance only — a green status survives wrong content, a geometry/tint split, and the save cache |
| The `setnpcweight` probe | the caller, in game | Reloads the actor's 3D head, defeating the save cache | Temporary: it reverts on a cell change. A probe, not the fix |
| The face with FDF **disabled** | the caller, in game | A correct fix renders right without Face Discoloration Fix | If it looks right only with FDF, FDF is masking a desync you have not fixed |
| A new game, or a save where the NPC never loaded | the caller | Whether the residue is baked into the save | Nothing further — this is the authoritative check |

For the in-game probe: open the console, click the NPC, `setnpcweight 50`, close the console. Reach a
distant actor with `prid <RefID>` then `moveto player`. **Never put `coc` in a `.bat` file — it crashes
the game.** `prid`, `moveto` and `setnpcweight` take the in-world RefID, not the base `NPC_` id. If the
face is still wrong after a clean save check, the residue is in the save: hand off to ReSaver to delete
the baked ChangeForm. houseCARL does not edit saves.

## 8. A whole-order sweep is a bulk job

"A bunch of NPCs went dark after I installed X" is an enumerate-and-dedupe job: plan it with
`housecarl:bulk-record-jobs` — every `NPC_` the suspect plugin touches, deduped to load-order winners,
spilled to an artifact — and bring the flagged subset back here for the pair diff and the fix.

Two things to know before you start. `housecarl_check` has **no facegen finding family** — its
`findings=` takes `errors`, `scripts` and `dialogue` only, so a call there returns nothing for this job.
And a whole-order geom-versus-tint map still has to be assembled outside the tool: `under=` with
`format="json"` and `limit=`/`offset=` reads both folder trees, but `housecarl_asset_status` has no file
sink and no formid/kind pair mode, so the join is manual. That gap is **#584** — name it rather than
pretending the sweep is one call.

## 9. Common mistakes, and the rule that replaces each

- **Anchoring the facegen folder to the conflict winner.** Anchor it to the defining master, the plugin
  after the colon. The winner computes a path the engine never reads — the highest-stakes error here.
- **Calling a green file status the all-clear.** "A file wins at the path" is necessary, not sufficient:
  read the winning mesh's shape names and embedded tint path, and hand off the in-game check regardless.
- **Placing only one of the pair.** Place the `.nif` and the `.dds` together, from the same source.
- **Trusting a console-clicked id as the base NPC.** Read it back and confirm the name first — it is a
  placed reference, and a wrong base means a wrong defining master and a wrong folder.
- **Reading multi-provider contention as a fault.** Present it as the healthy default and verify only
  when a specific path's winner is unexpected.
- **Reaching for a file placement on an out-of-lane cause.** Name the real tool for the Step 0 classes;
  placing a file there changes nothing the user can see.

## 10. Make a defensible verdict

A face-bug diagnosis lands on one of two honest outcomes, never a confident guess.

1. **A cause, a fix, and its capability class** — "the record winner is Bijin, but the `.dds` exists
   nowhere in the order while the `.nif` wins from Bijin; place the pair from Bijin as a winning
   override, enable it, then run the in-game `setnpcweight` check." Say which winner is wrong and which
   fix moves which half.
2. **An explicit "houseCARL cannot finish this"** — what you checked, why it stops here, and the exact
   external tool that can: the Creation Kit for a bake, a texture tool for pixels, RaceMenu or ReSaver
   for runtime and save state.

A confidently wrong "place this file and you're done" sends the user to enable a mod that changes
nothing. That is worse than a clear non-answer.

## 11. Where the rest lives

| For | Read |
|---|---|
| Causes and fixes by letter, the symptom table, and which community tool owns a case houseCARL cannot | `references/facegen-causes-and-fixes.md` — read it to pin a specific cause or justify a fix to the user; the flow above drives most diagnoses without it |
| The mesh-side repairs — rewriting a baked shape name, the embedded FaceTint path, or a skin slot — and what each one can and cannot prove | `references/mesh-repairs.md` — read it when §3 says the file wins but the face is still wrong, or when a mesh refuses a write |
| An `NPC_` field path or enum spelling, before composing a `housecarl_apply` op | `housecarl:mutagen-reference` |
| Copying a face onto a *different* NPC, or cloning one as a standalone | `housecarl:npc-appearance-copy` |
