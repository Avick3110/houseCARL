---
name: npc-appearance-copy
description: >-
  Copies one NPC's face onto another, or clones an NPC as a standalone, via housecarl_copy —
  records, inline face values, the FaceGen files and the tint path baked inside the placed mesh,
  which are four separate calls. Use for a standalone follower, for borrowing a face from an
  overhaul, or when a copy you just made renders dark; a face that was already wrong before any copy
  is housecarl:facegen-diagnostics. Load before the copy — the wrong field set writes a blank face,
  and the copy is not done when the patch is written.
compatibility: Requires the houseCARL MCP server and a configured Mod Organizer 2 instance.
---

# NPC Appearance Copy

## Overview

Copying an NPC's appearance moves three different things, and each one takes its own call:

| What moves | Tool | Why it is separate |
|---|---|---|
| The link-bearing appearance — head parts, hair colour, head texture, worn armor — and what they pull in | `housecarl_copy` | *Records*: duplicate them under new FormIDs or the result masters the donor |
| The inline appearance — tints, morphs, skin lighting, weight | `housecarl_apply`'s `bundle=`/`assignments=` zip | Values on the record itself. Nothing to walk, nothing to duplicate |
| The baked FaceGen mesh and tint | `housecarl_place` | *Files*, decided by MO2's virtual file system, not by load order |

**A fourth call finishes it.** The placed mesh still names the donor's tint inside its own bytes, and
`housecarl_nif_set` repoints it. Four calls is four refusal surfaces and a window in which the plugin
exists and its files do not — a record that copied while its FaceGen did not is the dark-face bug.
The flow is not finished at the patch, and not at the placement either: it is finished when the
placed mesh points at the copy's own tint and you have re-read it. If a step refuses, say so instead
of reporting a successful copy.

A face that was *already* wrong, before any copy of yours, is `housecarl:facegen-diagnostics`; this
skill is the authoring side. A sibling carries the `housecarl:` prefix on a Claude Code plugin
install and its bare folder name on Codex (`facegen-diagnostics`); the tool names are the same on
both.

The four Race cases, and how to choose the provider a placement names:
`references/race-and-provider-cases.md` — read it when the donor and the target are different
races, when the copy refuses on `Race`, or when a placement has to name a provider and the donor's
bytes are not the obvious ones.

## Before the copy — is the face on the donor?

Read the donor with `housecarl_records` and look at `Configuration.TemplateFlags` first. If it lists
`Traits`, the donor's own appearance fields are **empty**: the engine takes traits from the record in
`Template`, and this NPC is a shell. Follow `Template` and copy from that record instead.

This check is the guard, not a courtesy. `housecarl_copy` refuses a seed on its *shape* — decided on
the declared type before anything is read. An unset value is not that case: a seed the source leaves
unset **clears** the target's, so a missed template check writes the blank face and reports success.

## Step 1 — the record copy

```
housecarl_copy(
  from          = "<donor FormID>",
  seed_paths    = ["HeadParts", "HairColor", "HeadTexture", "WornArmor"],
  exclude_types = ["Race:refuse"],
  new_editorid  = "MyFollower",      # OR target = "<existing NPC FormID>"
  patch         = "MyFollower")
```

**The seed set is those four fields** because they are the appearance fields that are a record link
or a list of record links. Everything else in a face is inline data and rides Step 2. The list is
safe to extend when you have a reason: the shape is judged on the field's *declared* type, so a field
the donor happens to carry none of is still accepted, while a path that is not a field, or whose
entries are structures rather than links (`Factions`, `Perks`, `Items`), is refused by name. Those
belong in Step 2's zip, where `Merge` and `ReplaceAll` choose between merging into the target's
entries and replacing them.

**A seed the donor leaves unset clears the target's.** Deliberate, and the same rule as Step 2's
partition: a copy that leaves the target's own head parts or worn armor under the donor's face
produces a face assembled from two records. The readback says `cleared` rather than copied.

**`Race:refuse` is the standing choice, and it is yours to pass** — the tool applies no exclusion you
do not name, so a call that omits `exclude_types` walks with none. Pass it because a race is not an
appearance subtree: a walk into one pulls the skeleton and the sibling races. It only fires when the
race is inside the source universe:
defined in the donor plugin itself, or not resolving in your active load order. When donor and
target are different races, or a copy refuses on `Race`, the four cases and what each costs are in
the reference file the overview names.

**Destination.** `target=` copies the appearance onto an existing NPC; `new_editorid=` mints a
standalone clone. A clone loses every link still pointing into the donor — factions, outfits,
packages, script properties — each removal reported by name. That list is not noise: re-author those
against your own or vanilla records, or the follower has no faction and no AI package.

**EditorIDs are preserved on the copies, deliberately.** The engine matches the shape names baked
into a FaceGen mesh to head parts *by name*. Rename a copied head part and the mesh stops matching
the record, so the engine regenerates a vanilla head and drops the tint.

## Step 2 — the inline face values

The bundle is `FaceMorph`, `FaceParts`, `TintLayers`, `TextureLighting`, `Weight`, `Height`. How
much work this step does depends on which destination Step 1 used.

### The `target=` lane

Read the donor first and split those six in two, because a donor rarely carries all of them:

```
housecarl_apply(
  bundle      = [ ...the members the donor HAS... ],
  assignments = [{ target: "<the target FormID>", from: "<donor FormID>",
                   from_source: "<the plugin the appearance came from>" }],
  ops         = [{ formid: "<target>", field_path: "<a nullable member the donor LACKS>", op: "Remove" },
                 # only when the donor carries no tint layers:
                 { formid: "<target>", field_path: "TintLayers", op: "ReplaceAll", composes: [] }],
  into        = "<Step 1's patch filename>")
```

**Copy what the donor has, clear what it lacks.** Dropping the absent members and copying only the
rest is the tempting fix and the wrong one: it leaves the *target's* own morphs and face parts
underneath the donor's head parts, a face built from two people. A bundle only names what it copies,
so identity and everything outside the list is untouched by construction, and the call is
all-or-nothing — it lands whole or writes nothing. Naming a member the donor lacks in the bundle
instead of clearing it fails that whole write: the zip copies each path with `CopyFrom`, which
refuses an unset source rather than clearing it.

**Clearing takes two verbs.** `Remove` clears the nullable members — `FaceMorph`, `FaceParts`,
`TextureLighting`. `TintLayers` is a list of modeled elements, where a valueless `Remove` refuses
instead of clearing; its whole-clear is `op: "ReplaceAll"` with `composes: []`. `Weight` and
`Height` are not nullable, so a donor always carries them.

`TextureLighting` earns its place: it is the QNAM colour, it defaults to a value that reads as dark
skin, and a face copied without it renders the wrong skin tone while everything else looks right.

Two fields are conditional, so they sit outside the bundle:

- **`Race`** — bundle it **only when the target's race differs from the donor's**. FaceGen is
  race-fitted, so a head baked for one race reads wrong on another's skeleton.
- **The `Female` bit** — when donor and target differ in sex, set that one bit rather than copying
  `Configuration.Flags`, which drags Essential, Unique, Respawn and Protected with it:

  ```
  ops = [{ formid: "<target>", field_path: "Configuration.Flags", op: "Add", value: "Female" }]
  ```

  (`op: "Remove"` clears it.) Head parts and FaceGen are gender-fitted too.

### The `new_editorid=` lane

Nearly nothing to do. `housecarl_copy` is a whole-record duplicate — every field carries by
construction — so all six bundle members are already on the clone, and so is its sex. One op does
real work: the clone carries the **donor's** display `Name` until you set it.

```
housecarl_apply(
  ops  = [{ formid: "<the clone's FormID>", field_path: "Name", op: "Set", value: "<the name>" }],
  into = "<Step 1's patch filename>")
```

## Step 3 — the FaceGen pair

The record copy's readback lists the asset paths its copied records reference. Add the textures baked
into the donor's mesh, which no record names, by reading the mesh — deriving its path from the
FormID rather than composing one:

```
housecarl_nif_inspect(npc = ["<donor FormID>"], sections = "paths", mod = "<the donor's mod folder>")
```

Paths here are **Data-relative and printed with backslashes** —
`meshes\actors\character\facegendata\facegeom\<defining master>\00<6 hex>.nif`. Both tools take
either slash on input and neither takes a drive-rooted path. Write a path back into a mesh exactly as
it was printed: Step 4 stores the string you pass verbatim.

**`mod=` is not optional.** Without it the read resolves through the VFS and returns the *winner's*
mesh, and on a contested FaceGen path the winner is exactly the mesh whose bytes are not the donor's.
`mod=` reaches a mod MO2 is not loading, so a switched-off donor needs no switching on.

**Decide before you place.** Merge the two path lists case-insensitively and hand every candidate to
`housecarl_asset_status` in ONE call. Carry a path only if its bytes would vanish with the donor: if
another enabled mod supplies it, the file already resolves and a copy is a redundant override. Say
which paths you skipped. If the donor's mod is switched off, every path only it provides reads
**absent** — the carry case, not the skip case.

```
housecarl_place(assets = [
  { formid: "<the NEW FormID>", kind: "mesh",
    source: "<the donor's FaceGen mesh path>", source_provider: "<the provider chosen below>" },
  { formid: "<the NEW FormID>", kind: "tint",
    source: "<the donor's FaceGen tint path>", source_provider: "<the provider chosen below>" }],
  into = "<Step 1's patch>")
```

**Two members, not one.** Omitting `kind=` places both FaceGen files, but that form reads the
destination's own bytes: `assets[].formid` names the **destination**, while the files being read live
under the *donor's* plugin and FormID, so each member carries its own `source=` — and a `formid`
member with no `kind` whose `source=` is not a full `.bsa` path is refused. The destination comes
from the new FormID, so the placement is a rename: a copy's FaceGen filenames track its own FormID.

**Which provider to name** is the one whose bytes match the record you copied — sometimes the VFS
winner, which `source_provider` spells `*winner`. Decide it by reading, not from the plugin in the
path: inspect each candidate with `housecarl_nif_inspect mod="<candidate>"` and keep the copy whose
baked shape names match the copied head parts' EditorIDs. Name it as the readback prints it inside
the double quotes; the `loose` / `BSA` kind after it is not part of the name. Never compose a
mods-folder path by hand — nothing refuses one today, and that guard, issue #617, is not in this
release.

## Step 4 — the tint path inside the mesh

`housecarl_place` renames the file; it does not touch the bytes inside it. The head shape's `tex[6]`
still names the **donor's** tint, so the copy reads its colour out of the donor's mod and renders
fine for exactly as long as that mod stays installed.

```
housecarl_nif_set(
  mesh_path    = "<the placed mesh, Data-relative>",
  op           = "set_path",
  texture_slot = "6",
  target       = "<the head shape's name, as nif_inspect prints it>",
  path         = "<the copy's own tint path>",
  mod          = "<the mod folder the placement wrote>",
  into         = "<that same folder>")
```

**`mod=` is not optional here either**, and what it costs depends on the lane. On the `new_editorid=`
lane nothing in the active order provides the clone's FaceGen path — the placement's folder is not
enabled yet, enabling it is the caller's last step — so a read without `mod=` refuses `ABSENT`. On
the `target=` lane it is worse: the placed path *is* the target's own FaceGen path, which the vanilla
archives already provide, so the read silently takes the winner — the target's original head, whose
shape names still match — edits that, and writes it over the mesh Step 3 placed. Writing back `into=`
that same folder keeps the repointed mesh with the files it belongs to.

Then re-read it, naming the folder again —
`housecarl_nif_inspect(mesh_paths = ["<the placed mesh>"], sections = "paths", mod = "<that same folder>")`
— and check that slot 6 names the copy's own tint.

## Common mistakes, and the rule that replaces each

| Mistake | What it costs | The rule that replaces it |
|---|---|---|
| Copying from a `Traits`-templated donor | Its appearance fields are empty, so the seeds clear the target's: a blank face, reported as a success | Read `Configuration.TemplateFlags` first and copy from the record in `Template` |
| Seeding `Race`, or omitting `exclude_types` because you expect a `Race` default | There is none: the walk enters the race and pulls the skeleton and sibling races | Pass `Race:refuse` yourself; bundle `Race` in Step 2, and only when the two races differ |
| Renaming the copied head parts | The mesh's baked shape names stop matching; the engine regenerates a vanilla head | Leave the EditorIDs the copy writes alone |
| Omitting `TextureLighting` | Every field reads correct and the skin renders dark | Carry Step 2's bundle whole, `TextureLighting` included |
| Dropping a bundle member the donor lacks instead of clearing it | The target keeps its own morphs under the donor's head parts — a face built from two people | Copy what the donor has and clear what it lacks — `Remove`, or `ReplaceAll` with `composes: []` for `TintLayers` |
| Stopping at the patch | The records exist, the FaceGen does not — a dark face you authored on purpose | The copy is four calls; Step 3 places the FaceGen pair |
| Stopping at the placement | The placed mesh still points at the donor's tint: correct until the donor is uninstalled | Finish with Step 4 — repoint the tint inside the placed mesh, naming `mod=` |
| Letting the FaceGen source default to the VFS winner | You place a replacer's face over the records of the donor you actually copied |
| Reporting "copied" when the strip list is long | A clone with no factions, outfits, packages or scripts is not a working follower |

## Verification — what this session can check

1. The copy's readback says **standalone: the source is NOT a master**. When every source named was
   a base-game master it says **appearance transplant** instead, and that is the correct outcome
   there, not a failure — nothing is being removed from an always-loaded master.
2. The strip list has been dealt with, not just read.
3. Both FaceGen placements landed.
4. The re-read of the placed mesh shows the **copy's own** tint in slot 6.
5. `housecarl_check(plugins = ["<the patch>"])` comes back clean: it sweeps a patch that is not yet
   enabled off-order, so this runs before anything is switched on.

## Verification — what only the caller can do

Two things are the caller's, and this session cannot do either. Say so rather than leaving them in a
checklist that reads as unfinished work:

1. Enable the new mod in MO2.
2. Look at the result in game — face, hair, **and lip-sync while speaking**. Lip-sync exercises morph
   data baked into the mesh, so a head that looks right standing still can still be the wrong file.

## Notes

- All four calls accumulate into one artifact when you pass `into=` the same patch plugin's filename:
  `housecarl_nif_set` and `housecarl_place` find the houseCARL-owned folder by the plugin it holds,
  so the filename reaches the folder on those two as well as on `housecarl_copy` and
  `housecarl_apply`.
- Read the donor before every step that copies from it. Each call names the fields it touched, and a
  field it does not name is a field you have to account for yourself.
