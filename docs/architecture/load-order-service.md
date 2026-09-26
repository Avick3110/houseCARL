---
updated: 2026-09-24
covers: [src/housecarl-mcp/LoadOrderService.cs, src/housecarl-mcp/LoadOrderHost.cs, src/housecarl-mcp/ServiceResults.cs]
---
# The load-order service

## What it is
`LoadOrderService` owns the resolver's lifecycle: it derives the roots, builds lazily, keeps the
build fresh, and is the one place the tools reach the core engines. The index it builds is
[`load-order-resolver.md`](load-order-resolver.md). Where the roots and the profile files come from is
`docs/architecture/mo2-instance.md`; that note owns the profile files, the priority model and
the config file.

## Contracts

### Sessions and writes
- `_writeGate` serializes the whole resolve, stage and commit of every plugin write; `SetInstance` takes it too, so an instance switch cannot tear a write in flight. Where both gates are held the order is `_writeGate` then `_gate`. The `Resolver` and `Assets` getters are the one exception: they take `_gate` first and only try `_writeGate` with `Monitor.TryEnter`, never waiting on it, so the reverse order never turns into a wait.
- A read-path freshness refresh is DEFERRED while a write holds `_writeGate` — probed with `TryEnter`, never blocking — because a rebuild transiently maps every plugin including the one the write is serializing and dispose-swaps the resolver that write captured; a skipped refresh serves the last good snapshot and re-checks next call.
- Lock order is `_gate` then `_classParentsLock`. Nothing pins it.

### The shared head door
- `ILoadOrderHost`, in `src/housecarl-mcp/LoadOrderHost.cs` with the `AssetCapture` it returns, is how an area reaches the head members areas share. It carries the head members the area split assigned to it from the start (`Resolver`, `Assets`, `CaptureRoots()`, `CaptureAssets()`, `WriteGate`, `Rulebook`), plus any head member two areas take (`CapturePinAndAssets`, `Types`), and each only once some area actually takes it through the door: today `Resolver`, `Assets`, `CaptureRoots()`, `CaptureAssets()`, `CapturePinAndAssets(afterPin)`, `WriteGate`, `Types` and `Rulebook`. The head implements each member once, explicitly, next to what it wraps; rows relayed from other areas sit together in one block of the head.
- `Resolver` and `Assets` are the head's own getters, with their freshness and lock behaviour above. `Assets` is the live asset resolver, for a core check that captures it itself per seed.
- `CaptureRoots()` returns `Mo2Roots` (profile, data, mods and overwrite folders) from one `_gate` hold: it derives the roots first and throws whatever derivation throws. It takes no configured check: an unconfigured service yields four empty roots, and explicit mode always yields an empty overwrite root. Callers that need a configured instance have already gone through `Resolver` in the same call.
- `CaptureAssets()` takes one `_gate` hold: it captures the asset build through the `Assets` getter, which checks the service is configured and derives the roots first, then reads the warnings, profile name, the four roots (carried as one `Mo2Roots`), active archives and enabled mods of that same build. The caller works on the capture outside the hold.
- `CapturePinAndAssets(afterPin)` is the one-hold pin-plus-assets capture two areas take (the SkyPatcher layer scan and the facegen and script sweeps): one `_gate` hold pins the index view with its resolver, runs `afterPin` (a test seam, null in the product), then captures the asset build without a second profile refresh, so a profile switch cannot split the pin from the assets.
- `WriteGate` is the same object as `_writeGate`, so the lock order above holds through it: take the write gate first, then capture.
- `Types` is the service's `TypeLookup` (a `type=` string to its getter types), built from the corpus on first use and kept for the service's life, so a `CorpusRulebook.CorpusPath` set before a service's first type resolution governs that service. It is never process-wide. The read, check, write and asset lanes resolve types through it.
- `Rulebook` is the service's `CorpusRulebook` (corpus.json), loaded on first use from the `CorpusPath` set then; concurrent first calls publish one instance, and a failed load is not kept. The read area's scan takes it to judge a `where=` quantifier's shape; the write lanes use the same instance.
- Each area's own interface extends `ILoadOrderHost` and carries the rest: the head members only that area takes, plus rows relayed from areas that are not their own classes yet. The first is `IAssetHost`, in `src/housecarl-mcp/AssetLayers.cs`; the second is `ICheckHost`, in `src/housecarl-mcp/RecordChecks.cs`; the third is `IReadHost`, at the top of `src/housecarl-mcp/RecordReads.cs`.

### The service's answers
- The index build is lazy, so startup and `tools/list` are instant, and it is serialized on one gate because the server dispatches tool calls concurrently.
- A refresh that lands in MO2's own profile-rewrite window keeps the snapshot already built and does NOT advance the baseline, so the next call re-checks and follows the new profile.
- A profile change that could not be RE-READ is remembered: the asset lane keeps answering and says so in its warnings, and the record lane refuses, because the record index IS the load order.
- A mid-write read that resolves no paths keeps the last good snapshot and does not advance the baseline, so the next call recovers once MO2 finishes writing.
- A profile switch publishes the new roots only together with the order read from them: when the new profile's read is held or empty, the old roots, resolver and ini stamp all stay, and the next call retries. So a pin and the roots taken beside it always describe one profile.
- The asset resolver is built only on an asset query, never forces the record index build, and is dropped whenever the active mod or archive SET changes.
- A record build that lands while an asset build was KEPT across a profile change drops that asset build instead of advancing the baseline past it, so the next asset call rebuilds rather than silently serving the old answer.

### Configuration modes
- INSTANCE, the product default: one MO2 instance folder, roots and active profile derived from `ModOrganizer.ini`, and a profile switch picked up on the next tool call.
- EXPLICIT, a dev override: the three roots are configured directly, no ini is read and no profile-switch watch runs.
- UNCONFIGURED: the server still boots and every tool returns the prompt for the MO2 path until `housecarl_set_mo2_instance` is called.

## Pinned by
- `write-mutex-guard` (`ci-all`) — concurrent same-default-name writes each allocate their own folder and commit their own bytes: the serialized resolve, stage and commit, not the rest of that bullet.
- Nothing pins the lock order, the getters' `TryEnter` exception to it, or `SetInstance` taking `_writeGate`.
- `ProfileRewriteTests.AWarmAssetCallAnswersOffTheKeptBuildSaysSoAndFollowsTheProfileOnceItIsFree`, `TheRecordIndexRefusesRatherThanAnswerOffASupersededBuild` and `AColdRecordBuildAfterAHoldDoesNotStrandTheKeptAssetBuild` — the held-profile split between the two lanes.
- `ReadPinTests.ASwitchToAProfileWithNoActivePluginsKeepsTheOldRootsWithTheOldView` — a switch to a profile whose order reads empty publishes neither its roots nor its order, and the next call takes both once it reads.
- `ReadPinTests.ASwitchToAProfileHeldOpenKeepsTheOldRoots` — a switch to a profile MO2 holds open keeps the old profile name and roots while the record lane refuses, and takes the new ones once it is released.
- `freshness-capture-guard` (`ci-all`), arm 5 — a read while a write holds `_writeGate` completes without waiting, serves the last good snapshot, and the next call refreshes: the deferred-refresh sentence, not the mapped-plugin reason it gives.

## Where
`src/housecarl-mcp/LoadOrderService.cs`: the `Resolver` and `Assets` getters, `SetInstance`,
`RefreshOnProfileChange`, `RederiveIfIniChanged`, `ReResolve`, `EnsurePathsDerived`, `StatusData`, `Stats`, `UpdateCache`,
`NamedProfileComposition`, `PapyrusSourceImportDirs`, `Dispose`, `CapturePin()` and the `ViewPin` record it
returns (nested in the service), `CapturePinAnd<T>` (one `_gate` hold: the pin, a seam, then a second capture),
over which `CapturePinAndAssets` and `CapturePinAndRoots` (`IReadHost`'s: the pin and the four roots, for the read
area's pole lanes, which pass it the test seam `AfterReadPinForGuard`) are one-liners, the class-parent cache
(`ClassParentsForDecompile`, `InvalidateClassParents`), `_gate` and `_writeGate`, `Types` (the
`TypeLookup`, the door member, whose map is built on the first type resolution), and the explicit
`ILoadOrderHost`, `IAssetHost`, `ICheckHost` and `IReadHost` members. `src/housecarl-mcp/LoadOrderHost.cs` declares `ILoadOrderHost` and
`AssetCapture`; `IAssetHost` is at the top of `src/housecarl-mcp/AssetLayers.cs`.
The head's asset-facing surface is one-line delegators to `_assetLayers`, the `AssetLayers` it builds over itself in
its constructor: `AssetStatus`, `SkseInventory`, `SkseConfigAudit`, `NativePairingAudit`, `SkyPatcherLayer`,
`NifInspect`, `NifSet`, `PlaceAssets`. `AssetArea` hands tests the instance, to set its seams. The read area reaches the SkyPatcher replay through
`IReadHost.OpenSkyPatcherReplay`, relayed to `_assetLayers` in the head's relay block.
The checks-facing surface is the same shape over `_checks`, the `RecordChecks` it builds after `_assetLayers`:
`ValidateDialogue`, `CheckDialogue`, `CheckErrors`, `ValidateScripts`, `CheckFaceGen`, `SweepScopeError`, with
`CheckArea` for tests; `ICheckHost` is at the top of `src/housecarl-mcp/RecordChecks.cs`.
`src/housecarl-mcp/ServiceResults.cs` holds the result records the head's lanes and the other areas' lanes return;
the asset records are in `AssetResults.cs`.
Tools: `housecarl_load_order_status`, `housecarl_set_mo2_instance`, `housecarl_update_status`.
