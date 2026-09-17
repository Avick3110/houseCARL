# The asset layer: which copy of a file the game uses

**Class:** LIVING. Subsystem: `src/housecarl-core/{AssetResolver, AssetSourceSelection, OffOrderAssetSource,
AssetGlob, AssetLinkHarvest, AssetPathHint, AssetRenameService, ArchiveDiscovery, BsaArchive, VoicePath,
VoiceCheck}.cs`, `src/housecarl-mcp/{AssetLayers, AssetTools, AssetArtifact, BsaTools, PlaceTools,
ModsPathAddress}.cs`. Pinned by the `asset-resolver-guard`, `asset-status-guard`, `overwrite-resolve-guard`,
`snapshot-view-guard`, `place-asset-guard`, `nif-source-lane-guard`, `source-chain-guard`, `bsa-contract-guard`,
`bsa-extract-guard`, `bsa-probe`, `facegen-carry-guard` and `voice-carry-guard` probes
(`src/housecarl-generator`) and by `AssetSelectTests`, `AssetProviderTokenTests`, `AssetStatusSetTests`,
`BsaPackCountTests`, `BsaPackReadBackTests`, `RawModsPathRefusalTests` (`src/housecarl-mcp-tests`).

FaceGen's own contracts — the FormID→path transform, the check's classes, what a dark face actually is — live in
[`docs/facegen.md`](../facegen.md) and are not repeated here.

## Precedence

`AssetResolver` answers one question: for a Data-relative path, which sources provide it and which copy the game
uses. The rule is MO2's, extended across the BSAs active plugins load:

- **Loose beats BSA-packed.** The engine loads archives first; a loose file overrides them.
- **Among loose:** MO2's overwrite layer, then each enabled mod highest-priority-first, then the game's Data
  folder. First sighting wins — the same walk `Mo2LoadOrder.BuildFilenameMap` does for plugins.
- **Among BSAs:** the higher plugin load-order rank. Equal ranks (a plugin can ship two archives) tie-break on
  the archive filename, so the provider list is stable across runs.

`ArchiveDiscovery` builds the ranked archive list from the same static profile read the load order uses: the
always-loaded set from Skyrim.ini's `[Archive]` lists takes the low rank block, and each active plugin's `X.bsa`
and `X - Textures.bsa` rank above them in load order. Each archive *filename* resolves through the same
overwrite > mods > Data map, so an archive a higher-priority mod overrides is the one that is read. A Skyrim.ini
that cannot be found is a surfaced warning, never a silent omission — base-game assets would otherwise read as
absent.

**The order is injected.** `AssetResolver.Build` takes the roots, the enabled-mod priority list and the already
resolved active archives with their ranks. The BSA winner is only as correct as those ranks, which the service
computes; the resolver never reads a profile.

## What an answer is allowed to claim

- **Every provider comes back, not just the winner.** `Ambiguous` means more than one source provides the path,
  or a loose copy coexists with a BSA copy — the one edge the common rule cannot promise exactly under MO2's
  managed archives. It flags contention to verify, never a confirmed problem; more than one source is the common
  healthy case.
- **An archive that will not read is named, never treated as empty.** It lands in `BsaFailures` and sets
  `ReadIncomplete`, which is the caveat an `Exists=false` answer depends on: an asset present only in an
  unreadable archive is indistinguishable from an absent one.
- **A bad path fails loud.** `NormalizeQueryPath` rejects a drive-rooted or `..`-escaping path naming the input,
  and collapses `.` and empty segments so the loose walk and the archive-table match answer for one set of
  files. `ValidateRelPath` exposes that one validator to the place lane, whose destination is
  `Path.Combine(modRoot, rel)` — the same escape would write outside the owned folder.

## One build per call

Reads are lazy and freshness is cheap (CLAUDE.md), and the asset layer's shape of that is a snapshot:

- A build holds **string sets only** — each archive's file table, copied out and the reader dropped — plus a
  lazily warmed per-subtree set of loose filenames. **Zero archive handles at rest**, so a `.bsa` stays
  renamable and deletable while the resolver is alive and MO2 or xEdit can move one freely. Pinned by
  `asset-resolver-guard`'s at-rest arm (rename + delete while the resolver lives) and, for the single-entry
  extraction path, by `place-asset-guard` arm B.
- `RefreshIfStale` re-stats the active archives and every warmed loose subtree's directories across all roots,
  and swaps one reference. A changed archive or mod *set* is an order change and the service rebuilds the whole
  resolver instead.
- `Capture()` pins one build as an `AssetView`. A batch resolves off one capture so its hits and its
  `BsaFailures` cannot describe two builds, and the view is immutable and handle-free, which is what lets the
  service do its enumerating, reading and PE/NIF parsing outside `_gate`.

## Naming a source

`AssetSourceSelection` is the one policy for "which provider do I read from", with three poles: the sole
provider (contention is a refusal — the caller chooses), the VFS winner, and a named provider. Two rules hold
it together.

**The named pole is answered first**, ahead of the empty-universe return, because a disabled mod's copy is
exactly a path nothing enabled supplies. **Naming is the consent, and the only door**: the off-order lane —
`OffOrderAssetSource`, which reads a mod folder's disk directly, loose file first then the folder's root
archives — is consulted only under that pole, so a mod nobody named can never contend silently or be reported
as the winner. Bytes read that way are correct bytes from a mod the game is not loading, and
`PlacementSource.OffOrder`/`OwnerEnabled` carry that so the response can say which of the two reasons it is.

**Reserved names mean the layer, never a folder.** `overwrite`, `Data` and an active archive's filename are
answered by the built universe and never reach disk, so a mod folder literally called `Data` cannot shadow the
layer. An *enabled* mod folder name is deliberately not reserved: falling through costs nothing the active
universe already answered and gains the folder's own root archives, including ones no active plugin binds. A
trailing dot or space is refused, because Windows strips both and `mods\Data.` opens `mods\Data` — straight
around the gate.

`OffOrderReason` is a closed enum, one value per exit, so a new outcome is a compiler error at the render rather
than a false sentence. Its unreadable case carries a name and a cause as data: a folder, file or archive that
will not read is an unknown, and reporting it as "this mod does not have it" is the silent-wrong-answer class.

**Refusals name providers, never on-disk paths.** A path in a refusal teaches the caller to round-trip one that
goes stale between resolve and read, and `ModsPathAddress` exists for the other direction — a raw path into
MO2's mods tree reads past the VFS, so it is refused and the caller is handed the address form (mod folder name
+ Data-relative path) every pole already takes.

`AssetSourceSelection.Describe` is the one formatter for a provider in any list a selector is read out of: the
name in **double quotes** with the kind outside them. Single quotes failed the test — `JK's Skyrim` is a real
mod — and Windows forbids `"` in a name, so the delimiter cannot dissolve mid-name. The printed token is the
token a selector accepts; `place-asset-guard`'s I1c arm round-trips a refusal's own tokens back through the
tool, and `AssetProviderTokenTests` pins the render.

`*winner` selects the winner pole. The sigil is what makes the parse total: `*` is illegal in a Windows name, so
the pole space and the provider-name space are disjoint by construction and a bare `winner` always means a
provider called that.

## Selecting a set of paths

`AssetGlob` turns one selector into the paths it names by enumerating its literal directory prefix and keeping
what the pattern matches: `*` within a segment, `?` one character, `**` across separators, case-insensitive
against the whole Data-relative path. The compiled regex is `NonBacktracking`, because `**` nests quantifiers.

Two bounds are contracts rather than tuning. A selector with **no literal directory prefix** is refused: it
would enumerate every loose file in every enabled mod and every archive entry before one path rendered. And the
enumeration is **bounded by matches, not candidates** — the pattern filters inside the walk and the walk stops
the moment the cap is reached — so a caller past its per-call bound refuses having paid the bound's worth of
walking, not the whole order's, and a narrow glob under a wide prefix is never refused for the prefix's size.

`AssetLinkHarvest` is the other selector: every asset path a set of records declares, from a generic
`IAssetLinkGetter` walk over each record's property graph. Generated coverage, not a per-record-type field list
— the cornerstone.

## Voice paths

A dialogue line's audio is resolved by filesystem convention: there is no Voice/Lip/FileName field on INFO or
DialogResponse anywhere in the Mutagen corpus, so a byte-valid INFO with no `.fuz` on disk plays nothing.
`VoicePath` is the pure transform that says where that audio must live — no load order, no runtime FormID, no
file read — following xEdit's `InfoFileName`:

```
Sound\Voice\<defining plugin>\<VoiceType>\<QuestEDID[..10]>_<TopicEDID[..15]>_<00+6hexLocalID>_<ResponseNum>.fuz
```

The folder is the **defining plugin** in the INFO's FormKey, with extension — not the conflict winner. The id
segment masks the load-order index byte to `00`, exactly as FaceGen does. Both EditorID segments are truncated
and may legitimately be empty (a topic with no EditorID gives the real `__` shape). One assumption is worth
knowing: the on-disk voice folder is taken to be the VoiceType's EditorID, true for vanilla and every mod
checked, and the first thing to revisit for a wrong "place audio here" path.

`VoiceCheck` resolves the graph a path needs beyond the FormKey — parent topic off the written patch, voice type
through `INFO.Speaker → Npc.Voice → VoiceType.EditorID`, quest EditorID off the topic — and every miss is a
*named* undetermined reason rather than a false "fine". A line with no own responses that borrows another INFO's
`ResponseData` is named too: it is voiced, just not under a path computable here. The check is a verify step
after a create that already succeeded, so a whole-check failure is reported on `CheckError` and never thrown.

## Carrying assets across a renumber

A compact or merge moves every record to a new FormID, but the files the engine looks up *by* that FormID keep
their old names — so a compacted NPC mod dark-faces and a voiced mod goes mute. `AssetRenameService` carries
them, composing existing primitives: `FaceGenPath`/`VoicePath` for the paths, the resolver for the winning
bytes, `AtomicFile` for the write. Four contracts:

- **Two phases, one helper.** Facegen and voice both ride `CarryItems`: every read stages to a
  `.houseCARL-tmp` sibling, and only once every read is done are the temps committed. A renumber packs the new
  ids into the same window the source used, so in the in-place lane a direct new-id write could clobber a
  *different* record's not-yet-read old-id file and hand it the wrong face or voice. The aliasing torture arm
  lives in `facegen-carry-guard`; `voice-carry-guard` rides the same helper.
- **Non-destructive.** Only the new-FormID copies are written, under the output mod folder. The old files are
  left as harmless orphans the engine no longer looks up.
- **Never fails the write.** The records are already on disk by the time this runs, so an asset it cannot carry
  is a named warning. An NPC with no facegen of its own is normal, not a failure; only found-but-unwritable is.
  A `ReadIncomplete` build means "no facegen"/"no voice" is never silently trusted.
- **Voice discovery scans disk, not the dialogue graph.** A voice filename embeds the quest/topic EditorIDs,
  voice type and response number, none of which a renumber changes, so the id segment is the only thing that
  moves (plus the plugin folder segment on a merge). Re-deriving the graph per INFO silently misses radiant and
  quest-alias lines.

`.seq` is not part of this: a `.seq` lists master-relative on-disk FormIDs, all of which a renumber shifts, so
it is **rebuilt** from the renumbered plugin rather than renamed — and refresh-only. If the source shipped none,
compaction does not invent one (xEdit parity) and the missing-`.seq` case is a named advisory. Strings stay out
entirely: they are plugin-name-keyed.

## Archives

Reads go through **Mutagen's own in-process reader**; only repack drives **BSArch**, because Mutagen 0.53.1
exposes a reader and no writer. Reads deliberately do not shell BSArch: its unpacker is stricter than its own
lister and than the engine, so an archive written by another tool can list and load in game yet unpack to
nothing.

- **List and unpack cross-check the header's own declared file count.** The 24-byte BSA header is read directly,
  independent of Mutagen (whose public `IArchiveReader` exposes no count), so a reader mis-parse down to a short
  or empty list fails loud instead of reporting success.
- **Unpack is path-traversal-guarded and content-aware.** An entry resolving outside the destination refuses
  loud; a file already byte-identical is skipped, so re-extracting into a populated destination reports "already
  present" rather than a spurious rewrite. A single entry is bounded at 2 GB, so a corrupt header declaring a
  multi-GB entry fails rather than OOMing the one server process.
- **Pack is non-destructive, and provenance is why.** BSArch writes to a houseCARL scratch beside the target;
  the scratch is cleared before the run and a stuck stale one refuses up front, so a non-empty scratch after a
  zero-exit run is *this* run's — the alternative let a previous run's bytes ship over the user's archive. A
  non-zero exit is a failed pack whatever it left behind. Only then is the produced archive's header count
  checked against the source scan and the target swapped. `bsa-contract-guard` locks both halves.
- **An unknown format token refuses** rather than coercing to `-sse`, so a typo cannot silently pack for the
  wrong game.

## Writing into the VFS

`housecarl_place` and `housecarl_nif_set`'s default lane write into a houseCARL-owned MO2 mod folder;
originals are never touched, and in-place is a consent-gated opt-in keyed on the resolved file path (and is
declined for an off-order copy, which by definition is not the file the game loads). The write is crash-atomic
and the on-disk size is checked against the bytes, so success is never claimed falsely.

**"Wrote it" is not "it wins".** The response always states the current winner and what the caller must still
do, and there are five distinct answers, because the instruction is wrong in four of them if collapsed: nothing
else provides the path; the destination folder already wins (a re-place); MO2's overwrite layer wins, which sits
above every mod so neither enabling nor sorting reaches it; a BSA or the game's Data folder wins, which any
enabled mod's loose copy beats at any priority; or another mod wins, which a *fresh* folder out-ranks on enable
(MO2 registers an unseen folder at the highest priority) while an `into=` folder must also be sorted above it.
`PlaceWire.WinnerLine` and `EnableAndSort` are the one home for those sentences, because the JSON transport has
to carry them verbatim — a caller told only that the write succeeded would enable nothing.

## Suggesting a root prefix

A model path read off a record (`Model.File` on an NPC, ARMA, STAT) is stored relative to `meshes\`, so passing
it verbatim to an asset tool returns a flat ABSENT that is true for the string as given. `AssetPathHint`
answers that, and it is **verified, never guessed**: it re-resolves the prefixed candidate through the same
view and offers it only if a real provider supplies it. A wrong "did you mean" is worse than none. The mesh
lane additionally names the convention on a miss, explicitly stating that form is not provided either; the
generic lane does not, because it legitimately answers for `sound\`, `scripts\` and `interface\` where a
`meshes\` lecture is noise. Pinned by `asset-prefix-hint-guard`.
