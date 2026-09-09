# Mesh-side repairs — the head `.nif`'s own data values

Read this when the pair diff says the correct file wins but the face is still wrong, when a compaction or
merge left a stale reference baked inside a mesh, or when `housecarl_nif_set` refuses a write. Everything
here is `housecarl_nif_inspect` (read) and `housecarl_nif_set` (write) — data values inside one mesh, never
its geometry and never a `.dds`'s pixels.

## Contents

- [1. What a read sees](#1-what-a-read-sees)
- [2. The two embedded texture references](#2-the-two-embedded-texture-references)
- [3. The write ops, and what each repairs](#3-the-write-ops)
- [4. The two verification gates, and the two lanes](#4-gates-and-lanes)
- [5. The header-string form — material, `.tri`, physics xml](#5-the-header-string-form)
- [6. Why these repairs used to be manual](#6-why-these-repairs-used-to-be-manual)
- [7. What a verified write still does not prove](#7-what-a-verified-write-does-not-prove)

---

## 1. What a read sees

`housecarl_nif_inspect` resolves every mesh through the same VFS as `housecarl_asset_status` — the winner
by default, `mod=` for a named provider, whether or not MO2 is loading it — so you can read the copy the
game uses, or compare two mods' baked facegen without leaving the data layer.

The default summary is the header version, whether the stream is Skyrim SE, the block census, any unknown
blocks, and the shape names. `sections=` expands it: `shapes`, `partitions`, `alpha`, `paths`, `shader`,
`strings`, `nodes`, `bones`, or `all`. `npc=` takes an NPC FormID and derives that NPC's
`meshes\actors\character\facegendata\facegeom\<defining master>\00<6 hex>.nif` itself — the folder is the
plugin that **defines** the NPC, never the conflict winner.

A real read of the Lucien facegen (`facegeom\lucien.esp\00005900.nif`): shapes `LucienHead`,
`LucienHair`, `LucienHairLine`, `LucienEyes`, `LucienLashes`, `LucienBrows`,
`MaleMouthHumanoidDefault`; slot-6 FaceTint path `…\facetint\lucien.esp\00005900.dds`; hair alpha
`0x12ED` (blend on) against hairline `0x12EE` (test, threshold 180); partitions 30/31/32
(HEAD/HAIR/BODY). That is the whole mode-ii check — baked names and paths against the record — read
straight from the mesh.

## 2. The two embedded texture references

A head `.nif`'s `BSShaderTextureSet` carries two logically distinct references in one block.
`housecarl_nif_inspect` prints them as `tex[<slot>] (<Name>): <path>` under `sections=paths` or `shapes`,
and the slot number it prints is the **binary** index.

> **The `(<Name>)` is derived; the number is what you address.** The name comes from the shape's shader —
> its type plus its SLSF flags (`sections=shader`) — not from the index, because slot 2 is glow *or*
> skin-subsurface *or* soft-lighting and slot 7 backlight *or* specular depending on them. A slot the
> shader does not determine prints bare (`tex[4]:`), and on a non-Skyrim layout no slot is named at all.
> Always pass `housecarl_nif_set` the **number**.

1. **The per-NPC FaceTint `.dds` — binary slot 6** (NifSkope's slot 7, the subsurface/tint slot). It
   reads as `tex[6] (TintMask): textures\…\facetint\<defining master>\<00…ID>.dds`; the head shape's
   shader type is `FaceTint`, which is what names the slot. A stale value here — the pre-compaction
   FormID, or the source NPC's id after a cross-FormID copy — is the classic renumber fault.
2. **The base skin texture set** — race/body diffuse (`tex[0]`), normal (`tex[1]`) and the rest. A wrong
   value here is the face-versus-body mismatch, not the desync dark-face bug.

## 3. The write ops

`housecarl_nif_set` writes exactly one whitelisted value per call, addressed by `mesh_path=` plus
`target=` (the shape, node or header string as `housecarl_nif_inspect` currently prints it):

| `op=` | Operands | What it repairs |
|---|---|---|
| `set_path` with `texture_slot=` | `path=` | A `BSShaderTextureSet` slot: the FaceTint slot 6, or a skin slot 0/1 |
| `set_path` with no `texture_slot=` | `path=` | A header-string reference — see §5 |
| `rename_shape` / `rename_node` | `new_name=` | A baked shape or node name that does not match the record's head parts |
| `set_flags` | `flags=` | `NiAVObject` flags on a shape or node — the `0x80000` head/hair-class bit |
| `set_alpha` | `alpha_flags=`, `alpha_threshold=` | The alpha property — the hair `0x12ED` / hairline `0x12EE` class |
| `set_partition` | `body_part_id=`, `partition_index=` | A `BSDismember` body-part id |
| `set_scale` | `scale=` | A shape's or node's scale |
| `set_shader_value` | `shader_value=`, `value=` | A shader lighting value: glossiness, specular, emissive, alpha |

An op that does not apply to what `target=` names — `set_partition` on a shape with no `BSDismember`
skin instance — is refused by name with nothing written. A `target=` more than one block answers to is
refused as ambiguous rather than written to the first match. A mesh that is not a Skyrim SE stream is
refused: a normalized cross-game write is untested, not silently attempted.

## 4. Gates and lanes

Every write passes **two offset-immune verification gates before anything lands**: only the block and
value the op claims to touch changed, a reload re-reads the new value, and the block census plus the SE
stream are intact. A failed gate writes **nothing** and says why — there is no partial write to clean up.

Two lanes:

- **Default.** The verified mesh goes into a **new houseCARL MO2 mod folder** at the same path
  (`patch=` names it, `into=` accumulates into an existing one). Originals are untouched, and a
  BSA-packed source becomes a loose winning override this way. Enable that mod in MO2 — a new mod folder
  lands last in the priority order, so enabling is the step that makes the edit win.
- **In place** (`in_place=true` with `acknowledge=true`). Overwrites the winning loose file where it
  sits. Opt-in, per-file consent, **no backup**. Use it only when the user has asked for it.

## 5. The header-string form

A material (`.bgsm`), a `.tri` / BODYTRI, or a physics-xml reference is not a texture slot — it is a
header string. Read the table with `sections=strings`, then `set_path` with **no** `texture_slot=`,
passing `target=` the string exactly as it printed (case-sensitive). Several blocks referencing the same
string are not ambiguous: they all move together. Two header strings are refused by redirect rather than
swapped — a shape's or node's own **name** (use `rename_shape` / `rename_node`) and the **key** an
extra-data block is looked up by (pass the block's value instead, or the engine loses the block).

## 6. Why these repairs used to be manual

FaceGenEslify (and ESLifyEverything / ESLifier) batch-**rename** facegen files to the new compacted
FormIDs after an ESL compaction, but their README leaves the embedded FaceTint path inside each `.nif` as
a manual NifSkope step — which is why a mod could be renamed correctly and still render grey. NPC Facegen
Patcher, an xEdit script, rewrites the **skin** slots inside head `.nif`s so faces match race-specific
body textures, and deliberately leaves the FaceTint slot alone; its own page says it is not for grey or
black face bugs. Both edits are `set_path` calls at the data layer. The bulk cross-mod runs those tools do
are still theirs; a handful of meshes is faster here, and only a Creation Kit re-bake — new geometry —
leaves the data layer entirely.

## 7. What a verified write does not prove

A green two-gate verify confirms the **data value** landed. It does not confirm the face renders right.
A rewritten FaceTint path, a renamed shape, a corrected flag: each still needs the in-game
`setnpcweight` check. Report what you read and what you wrote as fact, and the render as unverified —
never upgrade a name match or a green verify to "the face is correct". The geometry, the `.dds` pixels
and the final render stay unseen.
