---
updated: 2026-09-23
covers: [src/housecarl-core/LoadOrderResolver.cs]
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
- Every build trims the winner index (`BuildIndex`); the heap is settled once, in the constructor after the FIRST build only, because on a `RefreshIfStale` re-index the old snapshot is still live and would be copied. Measured in #728, landed in #802.

### Exclusion
- A plugin that will not OPEN, or that holds a record Mutagen cannot PARSE, is excluded whole-plugin and the reason is surfaced (`LoadFailures`, `ExcludedPlugins`), never skipped silently.
- Exclusion is atomic per plugin: keys go into a per-plugin buffer and merge only if the whole plugin enumerated, so a plugin that throws part-way never half-populates the index.
- `Unopenable` is the subset of `Excluded` whose file could not be opened at all, kept as its own set rather than derived from the reason string, because a message is display prose and membership is a fact.
- A master set retains excluded-but-openable plugins on purpose — a clean plugin can override a record whose origin master is excluded, and that master must still appear in the patch header — and skips the unopenable ones, recording each name so a serialize failure can be attributed.
- An ACTIVE baseline master (Skyrim.esm / Update.esm) that cannot be opened refuses every write, thrown from the master-set builders, the single point every write lane funnels through.
- A plugin that opened at index time but not now is `PluginUnreadableException`; one that opened but could not be walked to the end is `PluginUnscannableException`. Two faults, worded differently, both reported as whole-plugin coverage gaps.

### Identity and freshness
- The epoch fingerprint is derived from every plugin's filename, RESOLVED PATH and freshness stamp in priority order, plus the names of the plugins the build excluded; two builds over an unchanged order that indexed the same set fingerprint identically.
- `EpochFormat` tags the formula, so an epoch a different formula wrote is not comparable at all — a different sentence from "your load order changed".
- Two known approximations the epoch shares with the freshness baseline: an unstattable-but-openable file collapses to `FileStamp.Absent`, and an edit that changes neither the last-write time nor the length is invisible to both.
- Every freshness check compares stamps by VALUE, never by wall-clock order: MO2's "Restore Backup" writes an OLDER mtime, and a same-mtime rewrite that changes length is invisible to the mtime term alone.
- `RefreshIfStale` handles content edits to existing plugins; a changed plugin SET is a new order and the caller rebuilds.
- Every baseline is statted BEFORE the read it baselines, so a write landing during the read shows up on the next check rather than being absorbed into the baseline.
- Freshness never fires between tool calls — no watcher, no loop — so an actively re-sorting user cannot make the server thrash.
- The reverse-reference index is held on the RESOLVER, not the snapshot, and carried over when a rebuilt resolver replaces one (`AdoptReverseIndexFrom`); its partitions are keyed on (path, mtime), so only the plugins this order added are walked.

### Sessions and writes
- A per-call `OverlaySession` opens each plugin the call touches AT MOST ONCE and disposes every one when the call returns; the write path takes its master set and its link cache from that same session.
- A write never leaves a mapped handle on the file it is about to serialize: `AllMastersExcept` skips the target's INDEX rather than filtering the returned list, and `ReleaseOverlay` closes a target overlay a winner fetch opened.

### The service's answers
- A body the index says exists but the plugin cannot yield is a NAMED inconsistency, never a silent null (`FetchRecord`); `GetRecord` answers null for a plugin absent from the order, excluded this build, or in the order but not defining the FormKey — a caller that must tell those apart asks `ContainsPlugin` too.
- A refusal naming a plugin the order does not contain carries the INJECTED explanation of why when there is one, and the did-you-mean otherwise. The resolver is built from a bare ordered path list and knows nothing of MO2, so the explanation is injected by the service.
- `OpenOverlay` is the single overlay-open choke point, and it redirects strings lookup to the real game-Data folder only when the plugin's OWN folder carries no strings source for that plugin.
- Light and master-block are separate per-plugin facts read off the same open header: an esp-fe is light in the FormID space and a regular plugin in the order.
- The first active plugin whose KIND could not be read is kept as a position, not a flag: a runtime FormID landing at or after it is refused, one landing before it answers normally.

## Pinned by
- `atrest-probe` (generator, dispatched by name, not in the `ci-all` roster) — zero handles at rest: after a build, after a read through a session, and after a create, every plugin file is renamable and the created patch deletable.
- `snapshot-view-guard` (`ci-all`) — a view captured before a real `RefreshIfStale` still answers all-old, a view taken after it answers all-new, and the service answers one operation off one view.
- `epoch-guard` (`ci-all`) — the fingerprint is deterministic over the world state, a content edit (newer OR older), a reorder and a set change each change it, and every index-backed lane carries the capture's epoch.
- `freshness-capture-guard` (generator) — the by-value stamp comparison catches a restored backup, `SetInstance` stamps its ini baseline before the read, one status line comes from one build, and a read-path refresh defers while a write holds the write gate.
- `FreshnessKeyTests.AnEditThatLeavesTheMtimeAloneIsStillSeenAsStale`, `TheSharedStampSeparatesTwoFilesThatDifferOnlyInLength` and `AnUntouchedOrderIsNotReportedStale` — the last-write-plus-length stamp, and that an untouched order is not reported stale.
- `pkcu-regression` (`ci-all`) — a plugin holding a record Mutagen cannot parse is excluded whole and every other plugin still resolves.
- `excluded-master-guard` (`ci-all`) — one unopenable active plugin does not break every write in the order: it is skipped from the master set and named.
- `RecordsOwnedChildOpenCountTests.ABatchOpensEachPluginOnce_NotOncePerRecordItUnions` and `RecordsRenderCostTests.ADetailRenderOpensOneOverlayPerPluginNotPerRow` — one overlay open per plugin per call, counted through `SessionOverlayOpens`.
- `RuntimeFormIdTests.ALightPluginsRecordReadsByItsRuntimeFormId`, `AFullPluginsRecordReadsByItsLoadIndex`, `ALightIndexNoActivePluginOccupiesIsRefused` and `ADynamicFormIdIsRefusedAsBelongingToNoPlugin` — the two runtime address tables and their refusals.
- `DegradedOrderMarkerTests` — a build that lost plugins carries the marker on every lane, and a healthy one carries none.

## Where
`src/housecarl-core/LoadOrderResolver.cs`: `Build`, `BuildIndex`, `IndexSnapshot`, `IndexView`,
`Capture`, `OverlaySession`, `OpenOverlay`, `RefreshIfStale`, `ComputeEpoch`, the scan
primitives (`WinnerRecordsOfType`, `RecordsIn`, `CollectRecords`, `SeekBody`).
Measured figures: SPEC §3.2 (the reverse-index bounds) and #728 (the index compaction).
