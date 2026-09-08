# Race cases, and choosing the provider a placement names

Two things the main flow states in one line each. Read this when the donor and the target are
different races, when a copy refuses on `Race`, or when a placement has to name a provider and the
donor's bytes are not the obvious ones.

## The four Race cases

`exclude_types = ["Race:refuse"]` is the standing choice an appearance copy passes, not a default the
tool supplies: `exclude_types` has none, and a call that omits it walks with no exclusions at all.
Pass it because a race is not an appearance subtree, and a walk into one pulls the skeleton and the
sibling races instead of a face. The exclusion only fires
when the race is **inside the source universe** — defined in the donor plugin itself, or not
resolving in your active load order. Which case you are in decides what is available.

**1. Race defined in the donor plugin, `target=` lane, donor ENABLED.** The donor's look depends on
its own race and this copy cannot free it from that. Re-run with `Race:stop`, which prunes the walk
and keeps the link; the readback then says plainly that those links still point into the source and
that the patch masters it. That is the truth, and it is a choice you can make.

**2. The same, donor DISABLED.** `Race:stop` is refused up front in this lane, and rightly: the
pruned link is kept on your target, so the patch would have to master a plugin the game does not
load. Enable the mod if you want the link kept, or use `Race:refuse` and take a different route.

**3. The `new_editorid=` lane (a clone).** The clone's strip removes links *into the source* anyway,
so a pruned off-order record is not refused here on that ground. What refuses is a link the record
model **requires** that cannot be stripped — and an NPC's `Race` is required, so a race that would
have to be stripped and left empty refuses rather than writing an invented null. A race that resolves
**outside** the source universe — vanilla, or any other mod your order loads — is neither stripped
nor refused, and `Race:stop` keeps it. That is the ordinary case, and it is why "`Race:stop` does not
help on the clone lane whatever the exclusion says" is not true as an unconditional claim.

**4. The race does not resolve at all.** The race mod is disabled or missing. Enable it; nothing here
can invent it.

**When donor and target are different races**, add `Race` to Step 2's bundle as well. FaceGen is
race-fitted: a head baked for one race reads wrong on another's skeleton even when the records agree.

## Choosing the provider a placement names

The rule is **the provider whose bytes match the record you copied**, and it is decided by reading,
not derived.

- **Do not take it from the plugin in the FaceGen path.** For an NPC defined in `Skyrim.esm` that arm
  is the game's own `Data` layer — the vanilla face, not the face you just copied. The plugin in the
  path names which master *defines* the NPC; it says nothing about whose bytes render.
- **Read each candidate.** `housecarl_asset_status` on the FaceGen path lists every provider that has
  a copy — every provider *MO2 loads*, so a switched-off donor is listed by nobody and the path reads
  absent. It is still a candidate: name its mod folder and read it anyway. Inspect them one at a
  time —
  `housecarl_nif_inspect(npc = ["<donor FormID>"], sections = "shapes", mod = "<candidate>")` — and
  keep the copy whose baked shape names match the EditorIDs of the head parts the record copy
  carried. That match is the whole test: the mesh and the record have to agree by name or the engine
  regenerates the head.
- **The winner is a legal answer.** When the copy that matches is the one currently winning the VFS,
  pass `source_provider = "*winner"`; the sigil is part of the token.
- **Name a provider exactly as it is printed inside the double quotes.** The `loose` / `BSA` kind
  after the name is not part of it. A mod MO2 is not loading is reached only because you named it —
  with `source_provider=` omitted, resolution sees only the mods MO2 loads.
