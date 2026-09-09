# Dark faces: what causes them, and what fixes each

A baked NPC face is two preprocessed per-NPC files — a head mesh (`.nif` under `facegeom`) and a face tint
(`.dds` under `facetint`) — and two independent systems decide who wins them. Plugin load order decides which
mod's `NPC_` record wins. The MO2 virtual file system decides which mod's file wins. A dark, grey or black face
is almost always the **desync** between those two: the file that wins was baked from a different appearance than
the record that wins, or nothing wins the keyed path at all and the engine regenerates the head from the record
and drops the tint. That is why xEdit shows no conflict while the face is dark.

`housecarl_check findings=["facegen"]` reports that desync one row per NPC. This page is the reference behind it:
what each class actually means, what fixes it, and the cases the check deliberately does not claim.

## The path is a pure function of the FormID

```
meshes\actors\character\facegendata\facegeom\<defining master>\00<6-hex local id>.nif
textures\actors\character\facegendata\facetint\<defining master>\00<6-hex local id>.dds
```

The folder is the **defining master** — the plugin after the colon in `XXXXXX:Plugin.esp` — never the conflict
winner. For a vanilla NPC an overhaul re-dresses, that is `Skyrim.esm\`, not the overhaul's folder. There is no
cross-folder fallback: the engine reads that one path and regenerates if nothing wins there. The leading index
byte is masked to `00` because the file already lives in the defining plugin's own named folder.

## The check's classes, and the fix for each

| Class | What it means | Fix |
|---|---|---|
| `tint_absent` | The mesh wins; the `.dds` has no provider anywhere. | Place the pair from the mesh's source. If the tint exists nowhere, re-bake (Ctrl+F4). |
| `mesh_absent` | The tint wins; the `.nif` has no provider. | Place the pair from the tint's source. |
| `bake_absent` | The NPC needs a bake and has neither half anywhere. | Nothing to place — re-bake in the Creation Kit. |
| `split_bake` | Both halves win, from different products: the head from one mod, the tint from another. | Re-place both halves from one source. |
| `stale_bake` | A clean same-source pair whose winning record disagrees with the facegen owner's plugin on the face fields. | Forward the appearance from the facegen owner, or re-bake. |
| `family_split` | Both halves win, from two mods of one product or a repack of its own archive. Benign by default; a few carry real risk. | Usually nothing. Verify only if that NPC renders wrong. |
| `foreign_index` | A same-local-id file in the same folder carrying a different load-order index byte — a bake keyed to somebody else's order. | Rename it to the canonical `00`-prefixed name, or re-bake. |
| `inert` | The key resolves to a placed reference, to no record at all, to a plugin not in the order, or the filename is malformed. | Nothing. No actor reads that path. |

Two exclusions the check applies silently in the counts and never as a fault. An NPC whose `Template` is set with
the `Traits` flag inherits its appearance and has no bake of its own — recompute the path against the template's
FormID if you need it. An NPC whose race lacks the `FaceGenHead` flag (a horse, a dragon, a draugr shell) has no
baked head at all.

## The causes behind the classes

**Record winner versus file winner.** The dominant cause. Load order picks record B while the VFS picks mod A's
file, so the head that renders was built from an appearance the winning record does not carry. Often a
non-appearance plugin — a bug-fix patch, a behaviour mod — wins the record and reverts head parts toward vanilla
while the overhaul's file still wins.

**A winner that ships no facegen.** An override changed the appearance but the author shipped only the plugin, so
the record points at a bake that exists nowhere.

**Precedence traps in the files themselves.** A correct bake sitting in a BSA that loses the VFS race; a leftover
loose file from an old, disabled or uninstalled mod winning over the correct archive — loose beats BSA even when
the mod providing it is disabled.

**ESL compaction and merges.** Compacting FormIDs renumbers local ids but does not rename the facegen files, so
the engine looks up the new id and finds nothing. A merge changes the defining master too, so both the folder and
the filename move. There is a second half to this one: each head `.nif` embeds the FaceTint `.dds` path in
texture slot 6, and a rename alone leaves that path pointing at the old FormID. `housecarl_nif_set` rewrites it
(`op="set_path"`, `texture_slot=6`); FaceGenEslify and its siblings automate the rename but leave that as a manual
step.

**Head parts and dependencies.** A record referencing a head part whose mesh is absent or replaced, or a required
appearance framework (High Poly Head, KS Hairdos) disabled while the record still references it, gives partial
darkness, clipping, or floating geometry rather than a flat dark face.

**Baked against the wrong order.** Facegen generated in the Creation Kit while a different override resolved
produces the right filename with the wrong content — a wrong face, not a dark one.

**A missing master or an orphan override.** The record cannot resolve at all. The tell is a dark actor whose
*correct* file wins — a file fix will not cure it.

## What is not this

These are real dark-face reports that the check cannot see, and reaching for a file placement on one changes
nothing the user can observe.

- **A purple or bright-white face** is a missing texture file, a different lane entirely. A dark face has a file;
  it is the wrong one.
- **Player-only grey**, with NPCs fine, after a reload, crash or game update, is RaceMenu/SKEE co-save state. If
  every RaceMenu slider and overlay is gone game-wide, `skee64.dll` did not load: match SKSE, the runtime and
  RaceMenu, and read `skse64.log`.
- **A brown face matching nothing** is weight baked into the save. Re-issue `setnpcweight`; if it is save-baked, a
  new game or ReSaver.
- **Grey that clears on save-and-reload** with a matched record and file is texture memory — oversized or
  uncompressed `.dds` files. Compress them with Cathedral Assets Optimizer or Ordenator.
- **An appearance distributed at runtime** by SPID, or an `FFxxxxxx` base id on a dynamically spawned actor, is
  not in the plugin records houseCARL reads. SkyPatcher *is* readable: `housecarl_records` with
  `source={"overlay":"skypatcher","state":"post"}` returns the record after that layer replays.
- **A face built from a RaceMenu `.jslot` preset** with no exported head is not facegen at all. Sculpt → Export
  Head, or Ctrl+F4, and then the files can be placed.
- **A neck seam with a correctly coloured face** is a skin-path problem, not a tint one: the head `.nif` hardcodes
  the vanilla skin while a per-race body framework gives a different body. `housecarl_nif_inspect` reads the
  slot-0/1 skin paths and `housecarl_nif_set` rewrites them.

## Fixing it

**Make the right files win.** `housecarl_place` with a `formid` member and no `kind` places both halves at once,
into a new MO2 mod folder the user then enables. Place both halves from the same source — one alone re-creates the
mismatch. A same-FormID, same-defining-master placement needs no mesh edit: the `.nif` already embeds the path
that resolves to the destination's `.dds`. Only a renumber or a re-home breaks that, and then slot 6 needs
rewriting.

**Bring the right appearance onto the record.** When the file is the intended appearance and a non-appearance
plugin won the record, `housecarl_forward` copies the whole `NPC_` from the appearance plugin into a winning
override. For a partial mask, `housecarl_apply` with the seven face fields — `HeadParts`, `FaceMorph`,
`FaceParts`, `TintLayers`, `HairColor`, `HeadTexture`, `TextureLighting` — writes just those.

**Re-bake, when nothing correct exists.** Only the Creation Kit writes new geometry. Open the CK, load the plugin
and Set as Active File, reach the actors through the Actors tree (Ctrl+F4 does not fire on results found through
the `*All` search), select them and press Ctrl+F4. Do not include child actors — a unique child TRI gives the
"shiny potato" result. houseCARL has no CK automation; this is a procedure to hand over, never a claim to have
done it.

**Face Discoloration Fix** is a runtime safety net, not a fix: it regenerates tint from the record so a missing
bake renders in the right colour rather than black. It does not restore a custom sculpt that lived only in the
baked mesh, and it masks the on-disk desync — so "looks fine in game" with FDF installed does not mean the
diagnosis is clean.

## What a green result does not prove

The check reports provenance: which mod wins each half, and which plugin wins the record. It cannot read a
`.dds`'s pixels, cannot bake geometry, and cannot judge a render. Verification past that point is not
houseCARL's: reload the actor's head in game with `setnpcweight`, check the face with FDF disabled, and confirm on
a save where the NPC never loaded — residue baked into a save is ReSaver's job, not this tool's.
