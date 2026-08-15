---
name: npc-appearance-copy
description: >-
  Copy an NPC's face onto another NPC, or clone one as a standalone, via `housecarl_copy` — the seed field set, the Race exclusion, the tint/morph bundle, and the FaceGen mesh+tint carry. Use for a standalone follower, borrowing a face from an overhaul, or a copied NPC rendering dark. Load before the copy — the wrong seed set writes a blank face.
---

# NPC Appearance Copy

## Overview

Copying an NPC's appearance is three operations, not one, and they use three generic tools:

| What moves | Tool | Why it is separate |
|---|---|---|
| The link-bearing appearance (head parts, hair colour, head texture, worn armor) and everything they pull in | `housecarl_copy` | These are *records*. They have to be duplicated under new FormIDs or the result masters the donor. |
| The inline appearance (tints, morphs, skin lighting, weight) | `housecarl_apply`'s `bundle=`/`assignments=` copy zip | These are values on the record itself. Nothing to walk, nothing to duplicate. |
| The baked FaceGen mesh + tint, and the textures they reference | `housecarl_bulk_place_asset` | These are *files*, decided by the MO2 VFS, not by load order. |

**The cost of that split, stated up front:** three calls means three refusal surfaces and a window where the plugin exists and its files do not. An NPC whose record copied but whose FaceGen did not is exactly the dark-face bug. The flow below is not finished at the patch — it is finished at the placement, and if the placement refuses you say so rather than reporting a successful copy.

Diagnosing a face that is *already* wrong is `facegen-diagnostics`. This skill is the authoring side.

## First step — is the donor's appearance actually on the donor?

Read the donor NPC (`housecarl_records`) and look at `Configuration.TemplateFlags` before anything else.

If it includes `Traits`, the donor's own appearance fields are **empty** — the engine takes traits from the record in `Template`, and this NPC is a shell. Copying its fields would set the target's head parts and tints to nothing, which blanks the face. Follow `Template` and copy from *that* record instead.

`housecarl_copy` independently refuses a walk whose seeds resolve to nothing, so a missed template check fails loudly rather than writing a blank. The check is still worth doing first, because the refusal tells you the walk found nothing while this tells you *where the face actually lives*.

## Step 1 — the record copy

```
housecarl_copy(
  from          = "<donor FormID>",
  seed_paths    = ["HeadParts", "HairColor", "HeadTexture", "WornArmor"],
  exclude_types = ["Race:refuse"],
  new_editorid  = "MyFollower",      # OR target = "<existing NPC FormID>"
  patch         = "MyFollower")
```

**The seed set is these four fields** because they are the appearance fields that carry record links. Everything else that makes up a face is inline data and rides Step 2. Naming a fifth field that carries no links is refused by name, so the list is safe to extend when you have a reason.

**`Race:refuse`** is there because a race is not an appearance subtree. Walking into one pulls the skeleton, the sibling races, the whole racial frame — so a race is either kept as an ordinary link or the copy stops and tells you. The exclusion only fires when the race is *inside the source universe*: defined in the donor plugin itself, or not resolving in your active load order.

- Race defined in the donor plugin → the donor's look genuinely depends on its own race, and this copy cannot free it from that. Either keep the donor as a master (re-run with `Race:stop`, which prunes the walk and keeps the link — the readback will then say the patch is *not* standalone, which is the truth) or pick a target that already uses a race you can keep.
- Race not resolving at all → the race mod is disabled or missing. Enable it; nothing here can invent it.

**Destination.** `target=` copies the appearance onto an existing NPC; `new_editorid=` mints a standalone clone. A clone loses every link that still pointed into the donor — factions, outfits, packages, script properties — and each removal is reported by name. That list is not noise: re-author those against your own or vanilla records, or the follower has no faction and no AI package. A link the record model *requires* cannot be stripped, so the clone lane refuses rather than writing an invented null; that is the case for `target=`.

**Reading the donor from an override.** When the appearance you want lives in an override patch rather than in the plugin that defines the NPC, name both, in order:

```
from_source = ["TheOverhaul.esp", "<the plugin in the donor's own FormID>"]
```

First hit wins, so records the overhaul carries come from the overhaul and everything else falls through to the defining plugin. You never have to discover the second name — it is the plugin half of the `from=` FormID. The readback names which source produced each record.

**EditorIDs are preserved on the copies, deliberately.** The engine matches the shape names baked into a FaceGen mesh to head parts *by name*. Rename a copied head part and the mesh no longer matches the record, so the engine regenerates a vanilla head and drops the tint. There is no reason to rename them and a concrete cost if you do.

## Step 2 — the inline appearance bundle

The bundle is `FaceMorph`, `FaceParts`, `TintLayers`, `TextureLighting`, `Weight`, `Height` — but **read the donor first and split that list in two**, because a donor rarely carries all of it:

```
housecarl_apply(
  bundle      = [ ...the members the donor HAS... ],
  assignments = [{ target: "<the new/target FormID>", from: "<donor FormID>",
                   from_source: "<the plugin the appearance came from>" }],
  ops         = [ { formid: "<target>", field_path: "<a member the donor LACKS>", op: "Remove" }, ... ],
  into        = "<Step 1's patch filename>")
```

**Why the split, rather than just naming all six.** `CopyFrom` refuses an unset source — "nothing to copy; use Remove to clear the target" — and `housecarl_apply` is all-or-nothing, so one absent member refuses the whole call. The tempting fix is to drop the absent members from the bundle, and that is the wrong one: it leaves the *target's* own morphs and face parts in place underneath the donor's head parts, which is a face assembled from two different people. Clearing them is what makes the target's face the donor's face and nothing else. So: copy what the donor has, remove what it lacks.

A bundle only names what it copies, so identity and everything outside the list are untouched by construction.

`TextureLighting` earns its place: it is the QNAM colour, it defaults to a value that reads as dark skin, and a face copied without it renders with the wrong skin tone while every other field looks correct.

Two fields are conditional, so they sit outside the bundle:

- **`Race`** — add it to the bundle **only when the target's race differs from the donor's**. Copying an equal value is a no-op, and copying a different one is a real change: FaceGen is race-fitted, so a head baked for one race on another race's skeleton reads wrong even when the records agree.
- **The `Female` bit** — when donor and target differ in sex, set that one bit rather than copying `Configuration.Flags`, which would drag Essential, Unique, Respawn and Protected across with it:

  ```
  ops = [{ formid: "<target>", field_path: "Configuration.Flags", op: "Add", value: "Female" }]
  ```

  (`Remove` clears it.) Head parts and FaceGen are gender-fitted too, so a mismatch here is a visible one.

## Step 3 — the FaceGen pair and the textures

The record copy's readback lists the asset paths its copied records reference — it is the only thing that knows what it copied. Add the textures embedded in the donor's FaceGen mesh, which the records do not name, by reading the mesh:

```
housecarl_nif_inspect(<donor's FaceGen mesh path>, sections = "paths")
```

Merge the two lists, case-insensitively, and that is the set of files worth considering.

**Decide before you place.** Run `housecarl_asset_status` on each path and read the provider chain. Carry a path only if its bytes would **vanish with the donor** — if another enabled mod still supplies it, the file already resolves and copying it just adds a redundant override. Say which paths you skipped and why; a silent skip and a deliberate one look identical afterwards.

**Place the pair under the new FormID:**

```
housecarl_bulk_place_asset(assets = [
  { formid: "<the NEW FormID>", kind: "mesh",
    source: "<the DONOR's FaceGen mesh path>", source_provider: "<the donor's mod>" },
  { formid: "<the NEW FormID>", kind: "tint",
    source: "<the DONOR's FaceGen tint path>", source_provider: "<the donor's mod>" }],
  into = "<Step 1's patch>")
```

The destination is computed from the new FormID and the source is the donor's own path, so source ≠ destination and the placement is a rename — which is exactly what a copied NPC needs, since its FaceGen filename tracks its FormID.

**Name the donor with `source_provider=`, do not take the VFS winner.** On a contested FaceGen path — a replacer out-sorting the base game's BSA — the winner and the donor are different bytes, and the winner's are the ones that can disagree with the head-part and tint records you just copied. The donor's are the ones the appearance you copied was baked from.

**Both files matter, and a miss is not cosmetic.** The mesh alone renders an untinted head; the tint alone renders the wrong head under the right colour; neither means the engine regenerates the face from the record and discards the tint — the dark-face bug. If a placement reports missing or failed, say the patch is written *without* its assets rather than reporting a successful copy.

**A donor in a disabled MO2 mod is a records-only job today.** Asset sources resolve through the VFS, which is built over enabled mods only, so a disabled donor's files cannot be named. Enable the mod for the placement (and disable it again afterwards if you want). Do not compose a path into the mods folder by hand — that is a guess wearing a path, and it silently places the wrong bytes when the folder name is not what you assumed.

## Common mistakes

| Mistake | What it costs |
|---|---|
| Renaming the copied head parts | The mesh's baked shape names stop matching; the engine regenerates a vanilla head. |
| Seeding `Race`, or dropping the `Race:refuse` exclusion | The walk pulls the skeleton and sibling races instead of a face. |
| `Set`ting `Configuration.Flags` to match the donor | Silently carries Essential / Unique / Respawn / Protected across. |
| Omitting `TextureLighting` | Every field reads correct and the skin renders dark. |
| Dropping a bundle member the donor lacks instead of clearing it | The target keeps its own morphs under the donor's head parts — a face built from two people. |
| Stopping at the patch | The records exist, the FaceGen does not — a dark face you authored on purpose. |
| Letting the FaceGen source default to the VFS winner | You place a replacer's face over the records of the donor you actually copied. |
| Reporting "copied" when the strip list is long | A standalone clone with no factions, outfits, packages or scripts is not a working follower. |

## Verification

1. The copy's readback says **standalone: the source is NOT a master**. If it instead alarms that the source *is* among the masters, the operation did not do the one thing it exists to do — read the kept-link list and find out what still points at the donor.
2. The strip list has been dealt with, not just read.
3. Both FaceGen placements landed.
4. Enable and sort the new mod in MO2. Nothing houseCARL writes wins anything until it does — the read-backs describe the file, not the load order.
5. Check in game: face, hair, **and lip-sync while speaking**. The lip-sync exercises morph data baked into the mesh, so a head that looks right standing still can still be the wrong file.

## Notes

- `asset_status` takes one path per call today, so the decide-before-you-place pass costs a call per candidate path. That is a handful per NPC and worth it — the alternative is placing files that were never the donor's.
- The three calls accumulate into one patch when you pass `into=` the same filename, so the result stays one reviewable artifact even though it took three operations to build.
- A record copied out of a generated plugin (a Synthesis or Reqtificator output, an NPC-merge result) is regenerated output, not authored content — see `tool-output-awareness` before copying from one.
