---
updated: 2026-10-30
covers: [src/housecarl-core/AssetResolver.cs, src/housecarl-core/NifLinks.cs, src/housecarl-core/AssetWalkEdges.cs, src/housecarl-mcp/AssetWalk.cs, src/housecarl-mcp/AssetWalkWire.cs, src/housecarl-core/AssetSourceSelection.cs, src/housecarl-core/OffOrderAssetSource.cs, src/housecarl-core/AssetGlob.cs, src/housecarl-core/AssetLinkHarvest.cs, src/housecarl-core/AssetPathHint.cs, src/housecarl-core/AssetDataPath.cs, src/housecarl-core/Mo2SkipRules.cs, src/housecarl-core/AssetRenameService.cs, src/housecarl-core/ArchiveDiscovery.cs, src/housecarl-core/BsaArchive.cs, src/housecarl-core/VoicePath.cs, src/housecarl-core/VoiceCheck.cs, src/housecarl-mcp/AssetLayers.cs, src/housecarl-mcp/AssetResults.cs, src/housecarl-mcp/AssetTools.cs, src/housecarl-mcp/AssetArtifact.cs, src/housecarl-mcp/BsaTools.cs, src/housecarl-mcp/PlaceTools.cs, src/housecarl-mcp/ModsPathAddress.cs]
---
# The asset layer: which copy of a file the game uses

## What it is

FaceGen's own contracts — the FormID→path transform, the check's classes, what a dark face is — are in
[`docs/facegen.md`](../facegen.md), not here.

## Contracts

### Precedence

- **Loose beats BSA-packed.**
- **Among loose:** overwrite, then each enabled mod highest-priority-first, then the game's Data folder; first
  sighting wins, the same walk `Mo2LoadOrder.BuildFilenameMap` does for plugins.
- **Among BSAs:** the higher plugin load-order rank, with the archive filename as a deterministic tie-break.

`ArchiveDiscovery` ranks the archives off the same static profile the load order reads: the `[Archive]` set of the
INI the game reads (`GameIni`, see [`load-order-resolver.md`](load-order-resolver.md): the profile's own INIs or My
Games, SkyrimCustom.ini over Skyrim.ini per key) takes the low block, each active plugin's `X.bsa` and
`X - Textures.bsa` rank above it in load order, and each archive *filename* resolves through the same overwrite > mods
> Data map. An archive list that cannot be found is a surfaced warning, never a silent omission. The order build's
string-table winners (`GameLanguage.Read`) take the same archive list and ranks, so a change to the winner rule here
moves where plugin names are read from too.

### Visibility

Precedence decides which copy wins among files the VFS holds; visibility decides what it holds. MO2 maps the whole mod
folder, and USVFS skips two things: a file whose name ends with a skip suffix, and anything under a folder named as a
skip directory. `Mo2SkipRules.Shows` is that one predicate, applied to the loose layer where a path is resolved
(`ResolveProviders`), where a folder is walked (`EnumerateUnder`) and where a root's top-level files are listed
(`LooseRootFiles`), so every consumer of any of them sees the same Data view. It is applied on the Data-relative path and not inside a directory listing, because one listing cannot see a
`.git` segment above it.

- **The lists are MO2's own**, read off `ModOrganizer.ini` (`skip_file_suffixes`, `skip_directories`) at each asset
  build: unset keeps MO2's defaults, `.mohidden` and `.git`; `@Invalid()` or a blank value is the empty list, which
  skips nothing; a value that is not a list keeps the default and is a build warning naming the key. An empty item is
  dropped, since it would hide every file. Explicit-paths mode has no ini and takes the defaults.
- **Only file names and folder names.** A folder named `x.mohidden` and Root Builder's `root\` are real VFS paths, so
  they stay listed; archives are not filtered; the watch baseline stays the full listing, so hiding or unhiding a file
  between two calls is seen.
- One limit, said rather than papered over: an edit to the skip lists alone, with no mod, profile or file change
  beside it, is read on the next asset rebuild, not the next call.

**The order is injected.** `AssetResolver.Build` takes the roots, the enabled-mod priority list and the resolved
archives with their ranks. The BSA winner is only as correct as those ranks, which the service computes; the
resolver reads no profile.

### What an answer may claim

- **Every provider comes back, not just the winner.** `Ambiguous` is more than one provider, or a loose copy
  coexisting with a BSA copy — the one edge the common rule cannot promise under MO2's managed archives. It flags
  contention to verify, never a verdict.
- **An archive that will not read is named, never treated as empty.** It lands in `BsaFailures` and sets
  `ReadIncomplete`, which is the caveat an `Exists=false` depends on.
- **A loose root that will not read is named the same way.** A walk or a subtree listing that throws lands in
  `RootFailures` and sets `ReadIncomplete` too, so a root neither lane could read is said rather than silently
  omitted. **These lanes name the roots**, because a hedge that does not name the folder leaves the modder nothing to
  act on: `asset_status`, the three `skse` families, the SkyPatcher layer and the NIF batch, beside their archive
  failures; the check sweep, dialogue family included, at the response root above its family sections, beside the
  failed archives and build warnings of its facegen and scripts families; the `place`
  lane, above its rows, which is where its source-scan hedge points; `nif_set`'s no-copy refusals (a named source,
  the `*winner` pole, or no source), which share that hedge, under the error line (a text-only lane with no
  max_chars, so cut at the server default); the create lane, under the coverage reports whose hedges depend on them;
  and the compact and merge carry notes, under the first pass that hedged. The dialogue and `place` hedges fire on a
  failed archive too, which those responses do not list, so they point at the named roots for the loose half only
  and send the archive half to `asset_status`, which names it.
  Whichever place, it is **once per response and in both transports**: a lane that names them twice in one document
  prints one list twice, so the response unions its families' or checks' lists and renders them once — a union, not
  whichever ran last, because two families in one call can answer off two asset builds. `BatchRender.CaveatBlockCut`
  is the one cut rule, with no transport and no caller's indent priced into it, and every json lane writes the array
  through `JsonWire.WriteCaveatCut` off that same cut — so the text lines and the json `root_read_failures` of one
  build name the same roots. The `skse` families cut their warnings and archive failures by it too, and the SkyPatcher
  layer its warnings: a block's lists share a quarter of `max_chars` max-min fair, each charged what its cut took.
  Bounding the json array against the json stream instead prices an element differently and the two counts drift
  apart, which is what the `skse` families did. A block inside a capped document is charged before that document's
  rows, never appended once its budget is spent. One exception, named as such rather than papered over:
  `ScriptPropertyCheck`'s per-property ".pex not on disk" reason has no block to point at, so it names the first root
  and counts the rest inside the sentence. A directory that will not even stat counts: `Directory.Exists` answers "not there" for one this account cannot reach, so an absence is trusted only where a
  readable ancestor lists the name as missing; the ancestor stats and their listings are memoized for the build — for
  THIS verdict only, never as a freshness baseline (see the watch below).
  **A memo never makes a failure.** Before a root is called unreadable the disk is asked again, uncached, so a name
  that has gone since the listing was cached, and a name that is a file rather than a directory, are absences like any
  other. It is filled lazily and kept for the life of the build, so a later
  call names a root an earlier one found unreadable — the flag is the build's, not the call's. **What clears it is a
  new build**, which `RefreshIfStale` makes when an archive or a warmed subtree changes and the service makes when
  the mod set or profile changes. Granting permission moves no mtime, so it clears nothing on its own: the modder
  toggles something in MO2, or restarts the server, and the next call reads the folder again.
- **A folder is warmed across roots top-down**, each root answered off listings of its mod folder and of each folder
  below it that its parent lists as a directory. Each view (one `Capture()` or one single-shot read) carries its own
  **read pass**, and a listing serves every pass begun before it was taken, so a call warming many folders lists each
  directory on the way once, not once per folder per root, and a concurrent call neither invalidates nor re-lists for
  it. A listing taken before a call began never answers it, so the next call lists again for any subtree it has not
  yet warmed. A subtree an earlier call warmed answers from `LooseCache` until `RefreshIfStale` runs, which the service
  skips while a write is in flight (#1128). A folder only passed through keeps its names, not its files. A listing
  that will not read is tried once per pass, then falls back to the stat-and-prove path.
- **A freshness baseline is read at the warm, never off an older memo.** Every directory a warmed subtree puts under
  watch is baselined by what that warm reads — a listing taken in the warm's own read pass, which is also what
  the answer came from; on the fallback path, the whole listing for a root's own copy, or for a root that has nothing
  there, two stats on the missing name (no directory, no file), taken only once the absence is proved; where it is
  not (a name listed that will not stat, or an ancestor that stats but will not list, both of which look absent to
  the two stats) the root is a named failure and nothing is watched. A memo from an earlier pass answers the
  absence VERDICT and nothing else, because it can predate the warm by any number of calls, and a baseline older
  than the warm makes a file that goes and comes back invisible for the life of the build.
- **A bad path fails loud.** `NormalizeQueryPath` refuses a drive-rooted or `..`-escaping path naming the input, and
  collapses `.` and empty segments so the loose walk and the archive-table match answer for one set of files. A last
  segment of only dots or spaces, which Windows opens as no name, is refused. The loose lookup spells each segment as
  Windows opens it (the last loses trailing dots and spaces, any other one trailing dot), so it finds the file a file
  open would; the archive match and the reported path use the path as asked.
  `ValidateRelPath` exposes that one validator to the place lane, whose destination is `Path.Combine(modRoot, rel)`.

### One build per call

- A build holds **string sets only** — each archive's table copied out and the reader dropped — plus a lazily warmed
  per-subtree set of loose filenames. **Zero archive handles at rest.**
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

### Naming a source

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
  the token a selector accepts.
- `*winner` selects the winner pole. `*` is illegal in a Windows name, so the pole and provider-name spaces are
  disjoint by construction and a bare `winner` always means a provider called that.

### Selecting a set of paths

`AssetGlob` turns one selector into the paths it names: enumerate the literal directory prefix, keep what the
pattern matches. `*` within a segment, `?` one character, `**` across separators, case-insensitive against the whole
Data-relative path, compiled `NonBacktracking` because `**` nests quantifiers. A selector with no literal directory
prefix walks from the Data root: `**/*goldbrand*` is "where does a file with this name live", across every loose
root and archive table. `*goldbrand*` stays one segment deep, and when it matches nothing the note names the `**/`
form. A bare name with no wildcard (`0004.nif`) that matches nothing is pointed at `**/0004.nif` only when that form,
capped at one match and narrowed to `provider=`, finds a file; otherwise no glob is named. That probe has no literal
prefix, so on a miss it is one more whole-order walk. The one bound is a contract: the enumeration is **bounded by matches, not candidates** — the pattern filters
inside the walk, which stops at the cap. A whole-order walk measured 2.4 s warm loose and 0.4 s for every archive
table over 326,491 paths, so the bound, not the prefix, is what keeps a sweep affordable.

`provider=` narrows the same walk to one provider's files: a loose root by name, and every active archive whose
filename it is or whose MO2 layer it names (`AssetSourceSelection.NameMatches`, the rule `source_provider=` uses), so
a mod name covers the archives it ships, and `Data` covers the Data folder's loose files and the base-game archives
(their owning layer is `Data`). Alone it walks the provider's whole tree; with `under=` it walks only
beneath those selectors; beside `asset_paths=` or `formids=` alone it walks nothing and only reads those rows. A name that is not in the active load order (a disabled mod, an inactive archive, a typo) is refused by name, since
it would walk nothing. Each row is
still resolved against every root, so the provider's standing is a reading of `AssetHit.Providers`, no new
resolution, matched by name as the walk is: **wins** when the winning copy's name matches the provider, **shadowed**
when the provider ships a copy and the winning copy's does not (the row names that winner; an archive's copy is
shadowed by its own mod's loose copy), **not provided** for an explicit path it does not ship. The standing rides the
text row as one line, the json row as `provider_standing`, and the artifact as a `provider_standing` column (null with
no provider and on an error row; the json member is absent in both cases). A mod's `.bsa` files are themselves loose files in its folder, and MO2 maps them into Data, so they are
part of what the mod provides.

The `counts_only=` census has two layer tables: the layers that **win** paths and the layers that ship a copy another
layer wins over (**shadowed**), each layer counted once per path, so a mod's loose copy beside its own archive's copy
is one layer that wins. What a layer provides is its winning count plus its shadowed count. With `provider=` the
census adds `provides / wins / shadowed` for that provider; the tables count by MO2 layer, not by provider name.

`bsa_list` and `bsa_extract` `under=` use the same grammar through `AssetGlob.Matcher`, over one archive's own
listing. The match bound does not apply there: the listing is already finite and already read. `*` still stays within one segment, so `**/*.pex` is the form that finds an archive's scripts. A plain path
matches that file or everything beneath that folder. As in `asset_status`, each selector that matches nothing is named,
and a blank or bad selector beside a usable one is noted while the rest answer; only a call with no usable selector refuses.

`AssetLinkHarvest` is the other selector: every asset path a set of records declares, from a generic
`IAssetLinkGetter` walk over each record's property graph. Generated coverage, not a per-record-type field list.

### Voice paths

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

### Carrying assets across a renumber

A renumber changes record ids while the files the engine looks up *by* FormID keep their old names.
`AssetRenameService` carries them, composing existing primitives. What it carries goes by lane: in place, the moved
records only, since an unmoved record keeps its id and so its file paths; into a new file, every record the plugin
defines, since the new mod folder starts empty. A new-file copy takes the face or voice winning at compaction time
(see [write-path.md](write-path.md)). Four contracts:

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

### Archives

Reads go through **Mutagen's own in-process reader**; only repack drives **BSArch**, because Mutagen 0.53.1 exposes
no writer. Reads do not shell BSArch: its unpacker is stricter than its own lister and than the engine.

- **List and unpack cross-check the header's own declared file count**, read directly because Mutagen's public
  `IArchiveReader` exposes no count, so a mis-parse down to a short or empty list fails loud.
- **Unpack is path-traversal-guarded and content-aware:** an entry resolving outside the destination refuses loud, a
  byte-identical file is skipped, and a single entry is bounded at 2 GB so a corrupt header cannot OOM the process.
- **Pack is non-destructive, on provenance.** BSArch writes to a houseCARL scratch cleared before the run, and a
  stuck stale one refuses up front, so a non-empty scratch after a zero-exit run is *this* run's. A non-zero exit is
  a failed pack whatever it left behind. Only then is the header count checked against the source scan and the
  target swapped.
- **An unknown format token refuses** rather than coercing to `-sse`.

### Writing into the VFS

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

### From a link to a Data path

A path a file names is not always Data-relative: one FaceGen NIF carries `Actors\…`, `textures\…` and
`data\textures\…` in its slots. `AssetDataPath.For` is the one door from such a string to the Data path the game
opens, keyed by link kind (texture, model). A leading separator is dropped first, as `asset_status` drops it. A
relative string loses a leading `data\`, and what follows must then start with the kind's root folder; with no leading
`data\`, the root is added where it is missing, by Mutagen's root name and its test, which has no separator boundary
(`TexturesHD\…` counts as rooted). A folder named `data` deeper in a relative string is part of the path:
`textures\mymod\data\foo.dds` stays as it is. A drive-rooted string goes through Mutagen's typed asset link, which maps
it through the last `data` folder in it and needs the root folder right after that folder; one with no such folder is refused. The
record harvest still reads Mutagen's `DataRelativePath`, which also cuts at a deeper `data` folder. The door's result
then passes `NormalizeQueryPath`, so a `..` or no-name segment is refused too. A refusal is one line naming the string,
why, and to give the path from the root folder on, never a dropped link. `NormalizeQueryPath` itself is unchanged: a modder's own path is Data-relative as typed. `nif_inspect`'s
paths section and the root-prefix hint below go through the door.

### A mesh's links

`NifLinks.Read` is every file one mesh names: each texture-set slot, each effect-shader texture (source, greyscale,
env map, normal, env mask), and the root node's `HDT Skinned Mesh Physics Object` and `BODYTRI` string extra data.
Only the roots are read for the two strings, because FSMP reads only the root's own extra data. Each string goes
through the door above by kind: a texture as a texture, `BODYTRI` as a model (meshes-relative), an SMP XML as
Data-relative through `ValidateRelPath`, never typed (a typed texture door would put it under `Textures\`). A string
the door refuses is a link with its reason, never a dropped one. `asset_status links=true` reads the winning copy of
every present `.nif` row and lists them, each Data path with its winner; a plain row is unchanged. A `to_file=` or
ceiling-spill artifact row carries the same `links` member (and the manifest's row schema names it only then);
`counts_only=` has no rows and refuses `links=`.

### Walking the links

`asset_status walk=true` is SPEC §3's construct with a second edge kind, record or file → file
(`AssetLayers.AssetWalk`, `AssetWalkEdges`). It seeds from the selection (every row the selectors chose) and from
`records=`, read through the `from_source=` pole list the copy uses (`BuildSourceChain`; default `winner`, a plugin
in a disabled mod by name). From a record it enters form links only into records a named `from_source=` plugin
defines (the plugins copy binds, so an override donor's own head parts are entered) or, for a seed read through
`winner`, the seed's own plugin defines, to 3 hops, so a base master's records reached from a donor's override are
boundaries; links into other plugins are counted as boundaries in a note, and records past the depth are counted too, so
nothing is cut silently. Each record contributes:

- the paths it lists (`AssetLinkHarvest`);
- Mutagen's inferred links: `Scripts\<name>.pex` (the `.psc` is dropped, a `:` maps to a folder as the script checks
  do), a start-game-enabled quest's `Seq\<plugin>.seq`, and any other inferred file;
- each fragment `FileName` in its script adapter, found by reflection over the adapter, with a note that Mutagen
  infers none;
- on an NPC, the FaceGen pair its face is drawn from: its own when the Traits template flag is off (or on with no
  Template, said in a note, since Mutagen's link is gated on the flag), else the first NPC
  down the Template chain with it off, every member of a leveled-list template (nested lists included) marked one
  of N; a loop or an unresolved link is a note naming the chain, and the other members keep their pairs. The pair is Mutagen's resolved link over a cache holding only the
  root's race, so a race without `FaceGenHead` gives none, which `FaceGenPath.For` alone cannot tell. The race and the
  template members are read through the record sources, then the active order; a race neither holds is a note;
- on a dialogue line, the `.fuz` paths Mutagen resolves over the whole order (built once, when a line is reached), or
  the same-stem `.xwm` / `.wav` and `.lip` the line ships as when no `.fuz` does (shipped by the order or by
  `source_provider=`, so a disabled donor's format counts); both listed, with no claim on which
  plays, when both ship. A line Mutagen finds no voice for is a note naming why (no Speaker, a speaker that is not an
  NPC or has no voice type), and one that throws is a note. Mutagen's resolution overflows the stack, killing the
  server, on a Traits template loop anywhere in the mods it reads (the active order, then the off-order `from_source=`
  plugins), so those mods' winning NPCs are checked for one first (`AssetWalkEdges.TraitsLoop`); with one, no line's voice is resolved and a note names the loop;
- on an NPC, the voice of each dialogue line it speaks: the active order's referencers from the reverse-reference
  index whose winning copy names it as Speaker, or in a `GetIsID` run on the Subject that passes for it and fails for
  any other (`AssetWalkEdges.Speaks`), and the lines an off-order `from_source=` plugin defines, which no index
  holds, read off that plugin by the same test; each line through the edge above. A line that only mentions it (a script
  property, a `GetIsID` on the Target) is not walked, nor is one reached only by voice type.

Each file node then resolves: from-source when `source_provider=` ships it (`AssetSourceSelection`, so a disabled mod
answers off its own disk), elsewhere when another provider wins it (named), present with no source named, or absent;
a string that names no Data path is an error node. A present `.nif` walks on through its links, read off the copy
that answers it, and a `.pex` through its parent class. FaceGen, `.seq` and voice nodes are **keyed**: they sit at a
path named by the defining plugin and id, so a copy that keeps the FormKey places them as walked, and a renumbering
one carries them through the carry above. With a source, the response ends with the from-source set as the
`assets=` array `place` takes in one call. The walk is bounded by the `asset_status` path bound and 2,000 records,
each said when reached. A walk past `max_chars`, text or json, is written whole to a results-directory file (one row
per node, SPEC §2.1.1) that the response names; when the `assets=` array itself would not fit, the response points at
the file's `from_source` rows instead. Out, and said: reverse file edges, SKSE configs and behaviour files.

### Suggesting a root prefix

A model path read off a record is stored relative to `meshes\`, so passing it verbatim returns a flat ABSENT that is
true for the string as given. `AssetPathHint` is **verified, never guessed**: it maps the path through the door above
once per root kind, re-resolves that candidate through the same view, and offers it only if a real provider supplies
it. A root the door adds is printed as the hint always spelt it (`textures\`, not Mutagen's `Textures\`). A candidate
that dropped a `data\` gets its own reason, which says the prefix is not part of a Data path and claims nothing about
whether the engine reads the `data\` form. The mesh lane also names the convention on a miss, stating that form is not
provided either; the generic lane does not, because it legitimately answers for `sound\`, `scripts\` and `interface\`.

## Pinned by

- *Precedence*: `AssetResolverTests` — overwrite, then mods highest-priority-first, then Data, first sighting wins
  (`OverwriteWinsAndEveryLooseProviderIsListedInPrecedenceOrder`, `WithoutOverwriteTheHigherPriorityModWins`); loose
  beats BSA-packed (`ALooseCopyBeatsTheArchiveCopyWhichStaysListed`); the higher plugin rank wins among BSAs
  (`AmongArchivesTheHigherPluginRankWins`).
- *Precedence*: `ArchiveDiscoveryTests` — `X.bsa` and `X - Textures.bsa` bind at their plugin's rank above the
  Skyrim.ini block (`APluginBindsItsCoNamedAndTexturesArchivesAtOneRank`,
  `BaseArchivesRankBelowPluginsAndALaterPluginOutranksAnEarlierOne`); an archive filename resolves to the
  higher-priority mod's copy (`ADuplicateArchiveNameResolvesToTheHigherPriorityMod`, which places no copy in overwrite
  or Data, so only the mods leg of the map is pinned); a Skyrim.ini that cannot be found is a surfaced warning
  (`AMissingSkyrimIniIsAWarningAndInventsNoBaseArchive`).
- *What an answer may claim*: `AssetResolverTests` — an archive that will not read is one named `BsaFailure` and sets
  `ReadIncomplete` (`AnUnreadableArchiveIsOneNamedFailureAndAGoodOneStillResolves`); a drive-rooted or `..` path is
  refused loud (`ADriveRootedOrEscapingPathIsRejected`; `PathFormsResolveToTheSameWinner` covers slash direction, a
  leading separator and case on loose files). No test uses a `.` or empty segment, so the collapse the sentence
  describes is not pinned.
- *What an answer may claim*: `AssetRootWalkFailureTests` — `asset_status` names a loose root it could not read, on the
  sweep and on the single-path lane; `UnreadableRootNamedTests` and `UnreadableRootNamedLanesTests` — the other lanes
  the sentence lists name it too; `UnreadableRootNamedLanesTests.AMergedSweepNamesTheRootOnceForTheWholeResponse` —
  once per response; `TheNamedRootsBlockIsChargedBeforeTheCoverageRows` and
  `TheJsonCreateDocumentChargesTheRootsToItsRows` in the same class — the block is charged before the rows;
  `ThePerPropertyReasonNamesTheRootItCouldNotRead` in the same class — the `ScriptPropertyCheck` exception.
- *What an answer may claim*: Pinned by `AssetLooseFreshnessTests` — a freshness baseline is read at the warm, never
  off an older memo (`AFileDeletedAndPutBackBetweenCallsIsSeen`, `ASubtreeDeletedAfterASweepMemoizedItsParentIsSeenComingBack`,
  `AFileAddedToASecondRootAfterAnotherFolderWarmedIsSeen`), and never off a listing an earlier call took, checked or
  not (`AFileDeletedWithNoCheckBetweenCallsReadsAbsent`); a read pass lists each directory once, the next call lists
  again, and the absence stays watched (`APassListsEachDirectoryOnceAndALaterCallListsItAgain`); a segment resolves as
  a file open does (`ATrailingDotOrSpaceResolvesAsAFileOpenDoes`), an archive entry as asked
  (`AssetResolverTests.ATrailingDotReachesTheLooseFileButNotTheArchiveEntry`), and one that opens as no name is refused
  (`ASegmentWindowsOpensAsNoNameIsRefused`, `PlaceOffOrderLaneTests.ADestinationEndingInADotsOnlySegmentIsRefused`);
  two interleaved passes do not list for each other (`TwoInterleavedPassesDoNotListForEachOther`); a folder that will
  not list is tried once per pass (`AFolderThatWillNotListIsTriedOncePerPass`); a file holding a name under a folder
  only passed through is still watched (`AFileHoldingANameUnderAPassedThroughFolderIsStillWatched`); on the fallback path an absence is watched only
  once it is proved (`AModFolderThatWillNotListDoesNotMakeEveryCallStale`,
  `AMemoThatWronglyProvesAnAbsenceCostsOneRebuildNotOnePerCall`); a memo never makes a failure
  (`AnAbsenceUnderAFolderGivenBackAfterItWouldNotListIsProvedAndWatched`).
- *One build per call*: **Zero archive handles at rest:** pinned by `AssetResolverTests.TheResolverHoldsNoArchiveHandleAtRest`
  (rename *and* delete while the resolver lives) and, for single-entry extraction,
  `PlaceCoreTests.TheArchiveIsNotHeldOpenAfterTheRead`.
- *One build per call*: `AssetResolverTests.ResolveManyAndACapturedViewAnswerFromOneBuild` reads a batch and a captured
  view off one build; `ACapturedViewKeepsItsBuildAcrossARebuild` in the same class pins the `Capture()` sentence
  against a rebuild (a file added and refreshed after the capture stays absent in the view).
- *One build per call*: `AssetLooseFreshnessTests` — the watch is made of names
  (`AppearAndVanishAreSeenEvenInsideOneTimestampTick`), an unrelated file beside a root's answering ancestor does not
  discard the build (`AnUnrelatedFileAppearingInAModFolderDoesNotDiscardTheBuild`), and a loose file's bytes are never
  cached (`AFilesChangedBytesAreSeenOnTheNextRead`).
- *Naming a source*: `PlaceSourcePoleTests.EveryProviderTokenInTheAmbiguityRefusalIsAcceptedBack` round-trips a
  refusal's own tokens back through the tool, and `AssetProviderTokenTests` pins the render.
- *Naming a source*: `PlaceOffOrderLaneTests` — naming an unticked mod reads its loose copy, then its root archives,
  and no other pole widens with it; `ANameTheActiveOrderKnowsIsServedByItNotByAModsFolderOfThatName` and
  `PlaceOffOrderGuardTests.ANameTheActiveOrderKnowsNeverFallsThroughToAFolderOfThatName` — a name the built universe
  knows never reaches a mods folder of that name; `ATrailingDotNameNeverReachesDisk` — a trailing-dot spelling never
  reaches disk.
- *Naming a source*: `NifSourceLaneTokenTests` and `NifSourceLaneReachTests` — every printed provider token is one
  `source_provider=` accepts, and naming a mod reaches its loose files and its root archives.
- *Naming a source*: `RawModsPathRefusalTests` — a raw path into MO2's mods tree is refused and handed the address
  form.
- *Selecting a set of paths*: `AssetSelectTests.AnUnanchoredGlobFindsTheNameInEveryFolderLooseAndArchive` — a
  selector with no literal directory prefix walks from the Data root; `AssetProviderSelectTests.AnUnanchoredGlobStillStopsAtTheMatchBound`
  — that walk keeps the match bound; `AGlobNarrowsTheSweepToTheFilesItMatches` in the first class —
  `*` within a segment, `**` across separators; `AssetStatusSetTests.ANarrowGlobUnderAWideFolderIsNotRefusedForTheFoldersSize`
  — the enumeration is bounded by matches, not candidates.
- *Selecting a set of paths*: `AssetProviderSelectTests` — a mod, an archive filename and a mod that ships archives
  each select exactly their own files; an unknown name is refused; `under=` narrows a provider; each row's standing
  (wins, shadowed by the named winner, not provided) on the text, json and artifact lanes; the census's
  `provides / wins / shadowed` line and the shadowed-layers table, with a layer's own archive copy not shadowing it.
- *Selecting a set of paths*: `BsaFilterTests` — `bsa_list` and `bsa_extract` `under=` keep an exact path, a `**` glob,
  a plain folder and everything beneath it but not a sibling prefix, and the union of several selectors; each selector
  that matches nothing is named, and a blank or bad one beside a usable one is noted; an extract writes only the matches
  and refuses an empty match.
- *Carrying assets across a renumber*: the four contracts are pinned by `CompactFacegenCarryTests` and
  `CompactVoiceCarryTests` — the two-phase carry (the overlapping-window arm), the old files left as
  orphans, a record with nothing to carry is not a failure, and voice found by scanning disk.
- *Archives*: the reader's byte parity with BSArch, and its reading of archives BSArch rejects, are pinned by the
  manual `bsa-probe` command (`BsaProbe`; needs a local BSArch and archive, so it is not on CI).
- *Archives*: `BsaExtractTests.AnEntryResolvingOutsideTheDestinationIsRefused` — unpack refuses an entry resolving outside
  the destination; `ASecondUnpackIntoTheSameFolderWritesNothing` in the same class — it skips a byte-identical file.
- *Archives*: `BsaPackContractTests` locks both halves of the pack contract — a stuck stale scratch refuses up front
  and a failing pack leaves the prior archive untouched — and the unknown format token refusal.
- *Archives*: `BsaPackReadBackTests.PackRefusesAScratchFromANonZeroExit` — a non-zero exit is a failed pack whatever it
  left behind; `PackRefusesAnArchiveThatCountsShort` in the same class and
  `BsaPackCountTests.ACountMismatchRefusesNamingBothNumbers` — the header count is checked against the source scan.
- *Visibility*: `AssetVisibilityTests` — a hidden file and a `.git` folder are not listed under their folders and the
  census does not count them; a hidden file asked by name is absent; custom suffix and directory lists replace the
  defaults; `@Invalid()` skips nothing; an empty item hides nothing; an unreadable list keeps the default with a warning
  naming the key; hiding and unhiding between calls is seen; a hidden folder and `root\` stay listed;
  `LooseRootFilesTests.AModRootListsItsOwnTopLevelFilesWithoutCountingAWarmListing` — a hidden root file is not listed.
- *From a link to a Data path*: `AssetLinkDataPathTests` — a `data\textures\…` or `data\meshes\…` path gets the
  verified hint naming its Data form, with the `data\` reason, on `asset_status`, `nif_inspect` and `place`; a miss
  gets no hint on `asset_status`, and on a mesh tool the convention sentence names its Data form, unverified; each NIF slot's Data path answers as a direct `asset_status` call; a slot the door refuses is its own
  error line, saying why and what to give; a folder named `data` below the first segment stays in the path, a leading
  separator is dropped, and a leading or drive-rooted Data folder is still cut.
- *A mesh's links* and *Walking the links*: `AssetWalkTests` — the root SMP string and a `data\` slot resolve with
  their winners, and a row without `links=` is unchanged; a disabled donor's walk marks each node from-source,
  elsewhere (named) or absent and `place` takes the set; the FaceGen pair is race-gated and keyed; a templated NPC
  pulls its root's face itself; a leveled template reaches each member one of 2 and a loop is a note; a script reaches
  its `.pex` and no `.psc`; a start-game-enabled quest reaches its keyed `.seq`. Voice, fragments and a `.pex`'s
  parent are not pinned by a test; they were checked on the dev instance only.
- *Suggesting a root prefix*: Pinned by `AssetPrefixHintTests` — the prefixed candidate is offered only when a real
  provider supplies it. Its non-asset-root cases (`sound\`) assert the same thing as its plain-miss cases, so it
  cannot see whether the generic lane prints the convention note: that half is not pinned.

## Where

`src/housecarl-core/AssetResolver.cs` and `ArchiveDiscovery.cs` hold the build, its precedence and the archive ranks;
`AssetSourceSelection.cs` and `OffOrderAssetSource.cs` decide which provider a read comes from; `AssetGlob.cs` and
`AssetLinkHarvest.cs` are the two selectors; `Mo2SkipRules.cs` is the visibility rule (its ini read is in
`Mo2Instance.cs`); `AssetDataPath.cs` is the link-to-Data-path door; `AssetPathHint.cs` is the root-prefix hint; `VoicePath.cs` and
`VoiceCheck.cs` are the voice paths; `AssetRenameService.cs` is the renumber carry; `BsaArchive.cs` lists, unpacks and
packs. `src/housecarl-mcp/AssetLayers.cs` is the service lane over all of it: the class `AssetLayers`, which the head
builds over itself and the output instance and reaches through one-line delegators, with the SkyPatcher replay in `SkyPatcherReplay.cs`.
`ModsPathAddress.cs` refuses a raw mods
path, and `AssetArtifact.cs` is `asset_status`'s `to_file=` artifact. `AssetResults.cs` holds the records the lanes
return. The tool fronts here are `AssetTools.cs`, `PlaceTools.cs` and `BsaTools.cs`. The other families' fronts and
renders are in their own notes: NIF in [`nif.md`](nif.md), SKSE in [`skse-layer.md`](skse-layer.md), SkyPatcher in
[`skypatcher-layer.md`](skypatcher-layer.md). Tools:
`housecarl_asset_status`, `housecarl_place`, `housecarl_bsa_list`, `housecarl_bsa_extract`, `housecarl_bsa_repack`; the
NIF, SKSE and SkyPatcher tools are listed in their family notes.

What the area needs from outside itself is the members of `IAssetHost`, declared at the top of `AssetLayers.cs`, and
the `OutputLocations` instance its constructor takes, for the patch folders `nif_set` and `place` write into. The
members every area shares come through the door it extends, `ILoadOrderHost` in `src/housecarl-mcp/LoadOrderHost.cs`
([`load-order-service.md`](load-order-service.md)).
