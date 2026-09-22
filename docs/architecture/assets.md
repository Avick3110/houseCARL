---
updated: 2026-09-23
covers: [src/housecarl-core/AssetResolver.cs, src/housecarl-core/AssetSourceSelection.cs, src/housecarl-core/OffOrderAssetSource.cs, src/housecarl-core/AssetGlob.cs, src/housecarl-core/AssetLinkHarvest.cs, src/housecarl-core/AssetPathHint.cs, src/housecarl-core/AssetRenameService.cs, src/housecarl-core/ArchiveDiscovery.cs, src/housecarl-core/BsaArchive.cs, src/housecarl-core/VoicePath.cs, src/housecarl-core/VoiceCheck.cs, src/housecarl-mcp/AssetLayers.cs, src/housecarl-mcp/AssetTools.cs, src/housecarl-mcp/SkseTools.cs, src/housecarl-mcp/SkyPatcherTools.cs, src/housecarl-mcp/NifTools.cs, src/housecarl-mcp/AssetArtifact.cs, src/housecarl-mcp/BsaTools.cs, src/housecarl-mcp/PlaceTools.cs, src/housecarl-mcp/ModsPathAddress.cs]
---
# The asset layer: which copy of a file the game uses

**Class:** LIVING. Subsystem: the files in `covers:` above. Pinned by the `asset-resolver-guard`, `asset-status-guard`, `overwrite-resolve-guard`,
`snapshot-view-guard`, `place-asset-guard`, `nif-source-lane-guard`, `source-chain-guard`, `asset-prefix-hint-guard`,
`bsa-contract-guard`, `bsa-extract-guard`, `bsa-probe`, `facegen-carry-guard` and `voice-carry-guard` probes
(`src/housecarl-generator`) and by `AssetSelectTests`, `AssetProviderTokenTests`, `AssetStatusSetTests`,
`BsaPackCountTests`, `BsaPackReadBackTests`, `RawModsPathRefusalTests`, `UnreadableRootNamedTests`,
`AssetLooseFreshnessTests`, `UnreadableRootNamedLanesTests` (`src/housecarl-mcp-tests`).

FaceGen's own contracts — the FormID→path transform, the check's classes, what a dark face is — are in
[`docs/facegen.md`](../facegen.md), not here.

## Precedence

- **Loose beats BSA-packed.**
- **Among loose:** overwrite, then each enabled mod highest-priority-first, then the game's Data folder; first
  sighting wins, the same walk `Mo2LoadOrder.BuildFilenameMap` does for plugins.
- **Among BSAs:** the higher plugin load-order rank, with the archive filename as a deterministic tie-break.

`ArchiveDiscovery` ranks the archives off the same static profile the load order reads: Skyrim.ini's `[Archive]` set
takes the low block, each active plugin's `X.bsa` and `X - Textures.bsa` rank above it in load order, and each
archive *filename* resolves through the same overwrite > mods > Data map. A Skyrim.ini that cannot be found is a
surfaced warning, never a silent omission.

**The order is injected.** `AssetResolver.Build` takes the roots, the enabled-mod priority list and the resolved
archives with their ranks. The BSA winner is only as correct as those ranks, which the service computes; the
resolver reads no profile.

## What an answer may claim

- **Every provider comes back, not just the winner.** `Ambiguous` is more than one provider, or a loose copy
  coexisting with a BSA copy — the one edge the common rule cannot promise under MO2's managed archives. It flags
  contention to verify, never a verdict.
- **An archive that will not read is named, never treated as empty.** It lands in `BsaFailures` and sets
  `ReadIncomplete`, which is the caveat an `Exists=false` depends on.
- **A loose root that will not read is named the same way.** A walk or a subtree listing that throws lands in
  `RootFailures` and sets `ReadIncomplete` too, so a root neither lane could read is said rather than silently
  omitted. **Every lane that hedges on `ReadIncomplete` renders the root beside its hedge**, because a hedge that does
  not name the folder leaves the modder nothing to act on: `asset_status`, the three `skse` families, the SkyPatcher
  layer and the NIF batch beside the archive failures, and the facegen and scripts sweep heads, the create lane's
  voice and result-script coverage reports and the compact/merge carry notes under their own. The one exception is
  `ScriptPropertyCheck`'s per-property ".pex not on disk" reason, which has no caveat block to point at and so names
  the first root and counts the rest inside the sentence. A directory that will not even stat counts:
  `Directory.Exists` answers "not there" for one this account cannot reach, so an absence is trusted only where a
  readable ancestor lists the name as missing; the ancestor stats and their listings are memoized for the build — for
  THIS verdict only, never as a freshness baseline (see the watch below).
  **A memo never makes a failure.** Before a root is called unreadable the disk is asked again, uncached, so a name
  that has gone since the listing was cached, and a name that is a file rather than a directory, are absences like any
  other. It is filled lazily and kept for the life of the build, so a later
  call names a root an earlier one found unreadable — the flag is the build's, not the call's. **What clears it is a
  new build**, which `RefreshIfStale` makes when an archive or a warmed subtree changes and the service makes when
  the mod set or profile changes. Granting permission moves no mtime, so it clears nothing on its own: the modder
  toggles something in MO2, or restarts the server, and the next call reads the folder again.
- **A freshness baseline is read at the warm, never off a memo.** Every directory a warmed subtree puts under watch is
  baselined by the listing that warm itself takes — the whole listing for a root's own copy, the ancestor's listing for
  a root that has nothing there. The build's `Dirs`/`Children` memos answer the absence VERDICT and nothing else,
  because a memo can predate the warm by any number of calls, and a baseline older than the warm makes a file that goes
  and comes back invisible for the life of the build. Pinned by `AssetLooseFreshnessTests`.
- **A bad path fails loud.** `NormalizeQueryPath` refuses a drive-rooted or `..`-escaping path naming the input, and
  collapses `.` and empty segments so the loose walk and the archive-table match answer for one set of files.
  `ValidateRelPath` exposes that one validator to the place lane, whose destination is `Path.Combine(modRoot, rel)`.

## One build per call

- A build holds **string sets only** — each archive's table copied out and the reader dropped — plus a lazily warmed
  per-subtree set of loose filenames. **Zero archive handles at rest:** pinned by `asset-resolver-guard`'s at-rest
  arm (rename *and* delete while the resolver lives) and, for single-entry extraction, `place-asset-guard` arm B.
- `RefreshIfStale` re-stats the active archives and re-lists the build's **watched directories**, and swaps one
  reference. A changed archive or mod *set* is an order change, and the service rebuilds the resolver. Warming a
  subtree puts one directory per root under watch, and the watch is made of **names**, never of a directory's
  last-write: that timestamp comes off a clock coarser than a write next to a delete, so two different listings inside
  one tick would read as no change. A root whose copy of the subtree IS on disk is watched by that directory's whole
  listing, so any file coming or going is seen. A root that has nothing there is answered by the deepest ancestor that
  lists and does not hold the next name, and only THAT name appearing counts — so a root with nothing under `meshes\`
  at all lands on its own mod folder, and an unrelated file written beside it does not throw the build away. A
  name a plain FILE holds is a real absence too, watched for the name becoming a directory. The cost is one listing per
  watched DIRECTORY: it grows with the directories that ANSWER — one per (root, answering directory) pair — and not
  with roots times subtrees. Which directory answers is what decides how far that collapses: roots answering from one
  shared ancestor, typically their own mod folder, add nothing for further subtrees, while roots whose deepest listing
  ancestor differs per subtree (two subtrees under different `meshes\` subfolders) add one each, as does every root
  that PROVIDES the subtree. Two paths are watched whole rather than by name — a subtree a root provides, and the Data-ROOT subtree, where
  the mod folder's own listing IS what resolution reads, so there a top-level file does discard the build. One thing
  this cannot see, by the same rule the failure lanes already state: a name that is listed, is no file, yet will not
  stat is a root failure named on the build, and giving the permission back moves no name, so only a rebuild clears
  it. A loose file's BYTES are never cached, so a rewrite needs no invalidation at all.
- `Capture()` pins one build as an `AssetView`, so a batch's hits and its `BsaFailures` cannot describe two builds.
  The view is immutable and handle-free, which is what lets the service enumerate, read and parse outside `_gate`.

## Naming a source

`AssetSourceSelection` is the one policy for which provider a read comes from: the sole provider (contention is a
refusal — the caller chooses), the VFS winner, or a named provider.

- **The named pole is answered first**, ahead of the empty-universe return, because a disabled mod's copy is exactly
  a path nothing enabled supplies.
- **Naming is the consent, and the only door.** `OffOrderAssetSource` reads a named mod folder's disk directly —
  loose file first, then its root archives — and is consulted only under that pole, so a mod nobody named can never
  contend silently or be reported as the winner. `PlacementSource.OffOrder`/`OwnerEnabled` carry that the copy is
  one the game is not loading, and which of the two reasons it is.
- **Reserved names mean the layer, never a folder.** `overwrite`, `Data` and an active archive's filename are
  answered by the built universe and never reach disk, so a mod folder called `Data` cannot shadow the layer. An
  *enabled* mod folder name is deliberately not reserved: falling through reaches that folder's own root archives,
  including ones no active plugin binds. A trailing dot or space is refused, because Windows strips both and
  `mods\Data.` opens `mods\Data`.
- `OffOrderReason` is a **closed enum**, one value per exit, so a new outcome is a compiler error at the render
  rather than a false sentence. Its unreadable case carries a name and a cause as data: a folder, file or archive
  that will not read is an unknown, and calling it "this mod does not have it" is the silent-wrong-answer class.
- **Refusals name providers, never on-disk paths**, which go stale between resolve and read. `ModsPathAddress`
  covers the other direction: a raw path into MO2's mods tree reads past the VFS, so it is refused and the caller is
  handed the address form — mod folder name plus Data-relative path — that every pole takes.
- `AssetSourceSelection.Describe` is the one formatter for a provider in any list a selector is read out of: the
  name in **double quotes**, which Windows forbids inside a name, with the kind outside them. The printed token is
  the token a selector accepts — `place-asset-guard` arm I1c round-trips a refusal's own tokens back through the
  tool, and `AssetProviderTokenTests` pins the render.
- `*winner` selects the winner pole. `*` is illegal in a Windows name, so the pole and provider-name spaces are
  disjoint by construction and a bare `winner` always means a provider called that.

## Selecting a set of paths

`AssetGlob` turns one selector into the paths it names: enumerate the literal directory prefix, keep what the
pattern matches. `*` within a segment, `?` one character, `**` across separators, case-insensitive against the whole
Data-relative path, compiled `NonBacktracking` because `**` nests quantifiers. Two bounds are contracts: a selector
with **no literal directory prefix** is refused, and the enumeration is **bounded by matches, not candidates** —
the pattern filters inside the walk, which stops at the cap.

`AssetLinkHarvest` is the other selector: every asset path a set of records declares, from a generic
`IAssetLinkGetter` walk over each record's property graph. Generated coverage, not a per-record-type field list.

## Voice paths

There is no Voice/Lip/FileName field on INFO or DialogResponse anywhere in the Mutagen corpus: a line's audio is
resolved by filesystem convention, so a byte-valid INFO with no `.fuz` on disk plays nothing. `VoicePath` is the
pure transform that says where it must live, following xEdit's `InfoFileName`:

```
Sound\Voice\<defining plugin>\<VoiceType>\<QuestEDID[..10]>_<TopicEDID[..15]>_<00+6hexLocalID>_<ResponseNum>.fuz
```

The folder is the **defining plugin** in the INFO's FormKey, with extension, never the conflict winner; the id
segment masks the load-order index byte to `00`, as FaceGen does; both EditorID segments are truncated and may
legitimately be empty (a topic with no EditorID gives the real `__` shape). One stated assumption: the on-disk voice
folder is taken to be the VoiceType's EditorID — true for vanilla and every mod checked, and the first thing to
revisit for a wrong "place audio here" path.

`VoiceCheck` resolves the rest of the graph (parent topic off the written patch, voice type through
`INFO.Speaker → Npc.Voice → VoiceType.EditorID`, quest EditorID off the topic), and every miss is a *named*
undetermined reason, never a false "fine" — including a line that borrows another INFO's `ResponseData`, which is
voiced but not computable here. It runs after a create that already succeeded, so a whole-check failure rides
`CheckError` and is never thrown.

## Carrying assets across a renumber

A renumber moves every record while the files the engine looks up *by* FormID keep their old names.
`AssetRenameService` carries them, composing existing primitives. Four contracts, pinned by `facegen-carry-guard`
and `voice-carry-guard`:

- **Two phases, one helper.** Facegen and voice both ride `CarryItems`: every read stages to a `.houseCARL-tmp`
  sibling, and only once every read is done are the temps committed. A renumber packs the new ids into the window
  the source used, so in the in-place lane a direct new-id write could clobber a *different* record's not-yet-read
  old-id file.
- **Non-destructive.** Only the new-FormID copies are written, under the output mod folder; the old files are left
  as orphans the engine no longer looks up.
- **Never fails the write.** The records are already on disk, so an asset it cannot carry is a named warning. An
  NPC with no facegen of its own is normal; only found-but-unwritable is a failure, and a `ReadIncomplete` build
  means "no facegen"/"no voice" is never silently trusted.
- **Voice discovery scans disk, not the dialogue graph.** A voice filename embeds the quest/topic EditorIDs, voice
  type and response number, none of which a renumber changes, so only the id segment moves — plus the plugin folder
  segment on a merge. Re-deriving the graph per INFO silently misses radiant and quest-alias lines.

`.seq` is not part of this: it lists master-relative on-disk FormIDs, all of which a renumber shifts, so it is
**rebuilt** from the renumbered plugin rather than renamed, and refresh-only — a source that shipped none gets a
named advisory, not an invented file (xEdit parity). Strings stay out: they are plugin-name-keyed.

## Archives

Reads go through **Mutagen's own in-process reader**; only repack drives **BSArch**, because Mutagen 0.53.1 exposes
no writer. Reads do not shell BSArch: its unpacker is stricter than its own lister and than the engine. The reader's
byte parity with BSArch, and its reading of archives BSArch rejects, are pinned by the opt-in `bsa-probe`.

- **List and unpack cross-check the header's own declared file count**, read directly because Mutagen's public
  `IArchiveReader` exposes no count, so a mis-parse down to a short or empty list fails loud.
- **Unpack is path-traversal-guarded and content-aware:** an entry resolving outside the destination refuses loud, a
  byte-identical file is skipped, and a single entry is bounded at 2 GB so a corrupt header cannot OOM the process.
- **Pack is non-destructive, on provenance.** BSArch writes to a houseCARL scratch cleared before the run, and a
  stuck stale one refuses up front, so a non-empty scratch after a zero-exit run is *this* run's. A non-zero exit is
  a failed pack whatever it left behind. Only then is the header count checked against the source scan and the
  target swapped. `bsa-contract-guard` locks both halves.
- **An unknown format token refuses** rather than coercing to `-sse`.

## Writing into the VFS

`housecarl_place` and `housecarl_nif_set`'s default lane write into a houseCARL-owned MO2 mod folder; originals are
never touched. `housecarl_nif_set` alone has an in-place lane: a consent-gated opt-in keyed on the resolved file
path, declined for an off-order copy. The write is crash-atomic and the on-disk size is checked against the bytes.

**"Wrote it" is not "it wins".** The response states the current winner and what the caller must still do, in five
arms, because collapsing them makes four of them wrong: nothing else provides the path; the destination folder
already wins (a re-place); MO2's overwrite layer wins, which sits above every mod so neither enabling nor sorting
reaches it; a BSA or the game's Data folder wins, which any enabled mod's loose copy beats at any priority; or
another mod wins, which a *fresh* folder out-ranks on enable — MO2 registers an unseen folder at the highest
priority — while an `into=` folder must also be sorted above it. `PlaceWire.WinnerLine` and `EnableAndSort` are the
one home, because the JSON transport carries them verbatim.

## Suggesting a root prefix

A model path read off a record is stored relative to `meshes\`, so passing it verbatim returns a flat ABSENT that is
true for the string as given. `AssetPathHint` is **verified, never guessed**: it re-resolves the prefixed candidate
through the same view and offers it only if a real provider supplies it. The mesh lane also names the convention on
a miss, stating that form is not provided either; the generic lane does not, because it legitimately answers for
`sound\`, `scripts\` and `interface\`. Pinned by `asset-prefix-hint-guard`.
