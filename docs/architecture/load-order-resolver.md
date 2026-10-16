---
updated: 2026-10-16
covers: [src/housecarl-core/LoadOrderResolver.cs, src/housecarl-core/IndexTypes.cs, src/housecarl-core/MutagenGroups.cs]
---
# The load-order resolver

## What it is
`LoadOrderResolver` is a held structural index over the active order — winner and override
depth per FormKey, plus the ordered touching list for the keys more than one plugin touches —
with no record bodies and no plugin file handles at rest. The service that owns its lifecycle is
[`load-order-service.md`](load-order-service.md).

## Contracts

### The index
- The resolver holds no plugin file handles at rest, because a Windows mmap overlay opened without `FILE_SHARE_DELETE` locks its file against delete, rename and overwrite — exactly what MO2, xEdit and Explorer need to do.
- The build enumerates every plugin one at a time, low to high priority: open, enumerate, dispose, so at most one plugin handle is open at any instant.
- `Index` carries every FormKey; `Overriders` carries multi-override keys only, because a singleton's sole overrider IS its winner.
- The order is INJECTED: override counts, depths and containment are order-independent, and winner identity is only as correct as the path list handed to `Build`.
- One build is one immutable `IndexSnapshot`, swapped in as a single volatile reference write, so a reader outside the service's gate can never see a new index beside old overriders.
- A logical operation captures that snapshot ONCE (`Capture`) and answers every question off the captured view, so no one response mixes two adjacent builds.
- Each `Index` entry also carries a one-byte code for the winning copy's record class, interned while the build already holds every record (`IndexTypes`), so it costs no read and nothing is hand-written. The code stands for the class and for its seek type (`WriteEngine.SeekTypeFor`: the flat group's getter, so every Global variant shares `IGlobalGetter`). The entry is packed as `(ushort winner, ushort count, byte type)`, the same 40 bytes as before; `Build` refuses an order past 65,535 plugins, far above the engine's 254 full plus 4,096 light. Codes run 0 to 254: Mutagen models about 133 Skyrim classes, and interning a 256th class throws rather than reuse `Mixed` or wrap.
- A key whose providers differ in class carries `IndexTypes.Mixed`, and each provider's own class code sits in `MixedTypes`, aligned with `Overriders`. That includes providers that share a seek type (a GlobalFloat overridden as a GlobalShort), so every provider's class is kept, not only the winner's.
- Every build trims the winner index (`BuildIndex`); the heap is settled once, in the constructor after the FIRST build only, because on a `RefreshIfStale` re-index the old snapshot is still live and would be copied. Measured in #728, landed in #802.

### Exclusion
- A plugin that will not OPEN, or that holds a record Mutagen cannot PARSE, is excluded whole-plugin and the reason is surfaced (`LoadFailures`, `ExcludedPlugins`), never skipped silently.
- Exclusion is atomic per plugin: keys go into a per-plugin buffer and merge only if the whole plugin enumerated, so a plugin that throws part-way never half-populates the index.
- `Unopenable` is the subset of `Excluded` whose file could not be opened at all, kept as its own set rather than derived from the reason string, because a message is display prose and membership is a fact.
- A master set retains excluded-but-openable plugins on purpose — a clean plugin can override a record whose origin master is excluded, and that master must still appear in the patch header — and skips the unopenable ones, recording each name so a serialize failure can be attributed.
- An ACTIVE baseline master (Skyrim.esm / Update.esm) that cannot be opened refuses every write, thrown from the master-set builders, the single point every write lane funnels through.
- A plugin that opened at index time but not now is `PluginUnreadableException`; one that opened but could not be walked to the end is `PluginUnscannableException`. Two faults, worded differently, both reported as whole-plugin coverage gaps. `WinnerRecordsOfType` given a collector collects either one and walks on to the next plugin; without one it throws it. Partway through a plugin only a parse fault (`RecordParseFault`) is collected; anything else, a cancellation above all, goes on up.

### Identity and freshness
- The epoch fingerprint is derived from every plugin's filename, RESOLVED PATH and freshness stamp in priority order, plus the names of the plugins the build excluded; two builds over an unchanged order that indexed the same set fingerprint identically.
- `EpochFormat` tags the formula, so an epoch a different formula wrote is not comparable at all — a different sentence from "your load order changed".
- Two known approximations the epoch shares with the freshness baseline: an unstattable-but-openable file collapses to `FileStamp.Absent`, and an edit that changes neither the last-write time nor the length is invisible to both.
- Every freshness check compares stamps by VALUE, never by wall-clock order: MO2's "Restore Backup" writes an OLDER mtime, and a same-mtime rewrite that changes length is invisible to the mtime term alone.
- `RefreshIfStale` handles content edits to existing plugins; a changed plugin SET is a new order and the caller rebuilds.
- Every baseline is statted BEFORE the read it baselines, so a write landing during the read shows up on the next check rather than being absorbed into the baseline.
- Freshness never fires between tool calls — no watcher, no loop — so an actively re-sorting user cannot make the server thrash.
- The reverse-reference index is held on the RESOLVER, not the snapshot, and carried over when a rebuilt resolver replaces one (`AdoptReverseIndexFrom`); its partitions are keyed on (path, mtime), so only the plugins this order added are walked.

### Fetching one record
- Every in-order fetch is typed by the index, through one door: `SeekBody` for one record and `CollectRecords` for a gather, which groups the wanted keys by the seek type their copy in that plugin carries and walks each type's groups once. No caller passes a type. On Mutagen 0.54.4 every pass over a top-level group copies the whole group, so an untyped seek of a Skyrim.esm armor addon cost 1.5 s and 1.37 GB; typed it is about 1 ms (#1158).
- Seek types that share top-level groups (cells, worldspaces and the references and navmeshes in them) share one read of each group in a gather: `CollectRecords` reads the group once and walks it once per type. Which groups hold which type is `MutagenGroups`, derived from Mutagen's getter interfaces by containment (never form links); the base record interface, held by every group, maps to null and walks flat. `CostCounters.GroupCopies` counts each group a gather's typed walks actually read, by one rule whether its seek types share it or one walks it alone: a group the plugin lacks, or one every wanted type was already found before, is not read and not counted. The flat walk, and a typed walk with no group map (the base interface), count nothing.
- A copy the build says a plugin does not hold is absent with no read. The flat walk is kept only for a key the build did not see, or a typed walk that misses because the plugin changed under the call; the next call's freshness check rebuilds. `CostCounters.TypedSeeks` and `TypedCollectPasses` count only fetches the typed walk answered, so a fallback shows as a gap.
- `IndexView.WinnerClassOf` answers the winning record's class off the index with no body read: the write pre-flight's link-target check and the facegen check's "not an NPC" row read it.
- Off-order files are not indexed, so their lanes still walk the file themselves.

### Sessions and writes
- A per-call `OverlaySession` opens each plugin the call touches AT MOST ONCE and disposes every one when the call returns; the write path takes its master set and its link cache from that same session.
- A write never leaves a mapped handle on the file it is about to serialize: `AllMastersExcept` skips the target's INDEX rather than filtering the returned list, and `ReleaseOverlay` closes a target overlay a winner fetch opened.

### The service's answers
- A body the index says exists but the plugin cannot yield is a NAMED inconsistency, never a silent null (`FetchRecord`); `GetRecord` answers null for a plugin absent from the order, excluded this build, or in the order but not defining the FormKey — a caller that must tell those apart asks `ContainsPlugin` too.
- A refusal naming a plugin the order does not contain carries the INJECTED explanation of why when there is one, and the did-you-mean otherwise. The resolver is built from a bare ordered path list and knows nothing of MO2, so the explanation is injected by the service.
- `OpenOverlay` is the single overlay-open choke point, and it redirects strings lookup to the real game-Data folder only when the plugin's OWN folder carries no strings source for that plugin.
- Light and master-block are separate per-plugin facts read off the same open header: an esp-fe is light in the FormID space and a regular plugin in the order.
- `RecordsIn` walks its plugins in load order, each once, and yields a FormKey several of them touch only from the highest-loading one, so its answer does not depend on the order the names were passed in. An empty scope is the whole order minus excluded plugins; a name not in the order is refused by name, off the caller's captured snapshot; a scoped plugin that no longer opens throws `PluginUnreadableException`, ending the stream.
- The first active plugin whose KIND could not be read is kept as a position, not a flag: a runtime FormID landing at or after it is refused, one landing before it answers normally.

## Pinned by
- `ScopedRecordSourcePairingTests.ATwoPluginScopeYieldsTheHigherLoadingPluginsBodyOnce` — `RecordsIn` over two plugins yields the higher-loading one's body once, under either name order.
- `atrest-probe` (generator, a manual probe run by name) — zero handles at rest: after a build, after a read through a session, and after a create, every plugin file is renamable and the created patch deletable.
- `SnapshotViewPinningTests` — a view captured before a real `RefreshIfStale` still answers all-old, a view taken after it answers all-new, and the service answers one operation off one view.
- `EpochFingerprintTests` — the fingerprint is deterministic over the world state, a content edit (newer OR older), a reorder and a set change each change it, and every index-backed lane carries the capture's epoch.
- `FreshnessCaptureTests` — the by-value stamp comparison catches a restored backup, `SetInstance` stamps its ini baseline before the read, one status line comes from one build, and a read-path refresh defers while a write holds the write gate.
- `FreshnessKeyTests.AnEditThatLeavesTheMtimeAloneIsStillSeenAsStale`, `TheSharedStampSeparatesTwoFilesThatDifferOnlyInLength` and `AnUntouchedOrderIsNotReportedStale` — the last-write-plus-length stamp, and that an untouched order is not reported stale.
- `MalformedPkcuExclusionTests` — a plugin holding a record Mutagen cannot parse is excluded whole and every other plugin still resolves.
- `ExcludedMasterWriteTests` — one unopenable active plugin does not break every write in the order: it is skipped from the master set and named.
- `RecordsOwnedChildTests.ABatchOpensEachPluginOnce_NotOncePerRecordItUnions` and `RecordsRenderCostTests.ADetailRenderOpensOneOverlayPerPluginNotPerRow` — one overlay open per plugin per call, counted through the service's `CostCounters.SessionOverlayOpens`.
- `RuntimeFormIdTests.ALightPluginsRecordReadsByItsRuntimeFormId`, `AFullPluginsRecordReadsByItsLoadIndex`, `ALightIndexNoActivePluginOccupiesIsRefused` and `ADynamicFormIdIsRefusedAsBelongingToNoPlugin` — the two runtime address tables and their refusals.
- `DegradedOrderMarkerTests` — a build that lost plugins carries the marker on every lane, and a healthy one carries none.
- `TypedIndexTests` — every lane's seeks and gather walks are typed, a key a later plugin redefines as another record type is sought in each provider's own groups, a Global variant keeps one seek type, each provider of a mixed key keeps its own class, and a plugin without a copy is not read; `IndexTypesTests` — a 256th class throws; `TypedIndexEquivalenceTests` — on the shared worlds every copy the typed door returns equals the one a flat walk finds.
- `MutagenGroupsTests` — the derived group map pins cells, references and navmeshes to one group set and NPCs to their own, and for every seek type walks exactly the groups Mutagen's own switch reads; `SharedGroupGatherTests` — a gather over cells, their references, an actor and two traps reads the shared groups once; `SharedGroupFallbackTests` — a reference that misses its shared-group walk comes back through the flat walk; `SharedGroupFaultTests` — a fault reading a shared group is named as itself, not as a reflection wrapper.

## Where
`src/housecarl-core/LoadOrderResolver.cs`: `Build`, `BuildIndex`, `IndexSnapshot`, `IndexView`,
`Capture`, `OverlaySession`, `OpenOverlay`, `RefreshIfStale`, `ComputeEpoch`, the scan
primitives (`WinnerRecordsOfType`, `RecordsIn`, `CollectRecords`, `SeekBody`); `src/housecarl-core/IndexTypes.cs`, the class codes; `src/housecarl-core/MutagenGroups.cs`, the group map.
Measured figures: SPEC §3.2 (the reverse-index bounds) and #728 (the index compaction).
