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
- `ILoadOrderHost`, in `src/housecarl-mcp/LoadOrderHost.cs` with the `AssetCapture` it returns, is how an area reaches the head members areas share. It carries only members some area actually takes through it: today `Resolver`, `Assets`, `CaptureRoots()`, `CaptureAssets()`, `CapturePinAndAssets(afterPin)` and `WriteGate`. The head implements each member once, explicitly, next to what it wraps; rows relayed from other areas sit together in one block of the head.
- `Resolver` and `Assets` are the head's own getters, with their freshness and lock behaviour above. `Assets` is the live asset resolver, for a core check that captures it itself per seed.
- `CaptureRoots()` returns `Mo2Roots` (mods, data, overwrite and profile folders) from one `_gate` hold: it derives the roots first and throws whatever derivation throws. It takes no configured check: an unconfigured service yields four empty roots, and explicit mode always yields an empty overwrite root. Callers that need a configured instance have already gone through `Resolver` in the same call.
- `CaptureAssets()` takes one `_gate` hold: it captures the asset build through the `Assets` getter, which checks the service is configured and derives the roots first, then reads the warnings, profile name, four roots, active archives and enabled mods of that same build. The caller works on the capture outside the hold.
- `CapturePinAndAssets(afterPin)` is the one-hold pin-plus-assets capture two areas take (the SkyPatcher layer scan and the facegen and script sweeps): one `_gate` hold pins the index view with its resolver, runs `afterPin` (a test seam, null in the product), then captures the asset build without a second profile refresh, so a profile switch cannot split the pin from the assets.
- `WriteGate` is the same object as `_writeGate`, so the lock order above holds through it: take the write gate first, then capture.
- Each area's own interface extends `ILoadOrderHost` with the members only that area takes, plus rows relayed from areas that are not their own classes yet. The first is `IAssetHost`, in `src/housecarl-mcp/AssetLayers.cs`; the second is `ICheckHost`, in `src/housecarl-mcp/RecordChecks.cs`.

### The service's answers
- The index build is lazy, so startup and `tools/list` are instant, and it is serialized on one gate because the server dispatches tool calls concurrently.
- A refresh that lands in MO2's own profile-rewrite window keeps the snapshot already built and does NOT advance the baseline, so the next call re-checks and follows the new profile.
- A profile change that could not be RE-READ is remembered: the asset lane keeps answering and says so in its warnings, and the record lane refuses, because the record index IS the load order.
- A mid-write read that resolves no paths keeps the last good snapshot and does not advance the baseline, so the next call recovers once MO2 finishes writing.
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
- `freshness-capture-guard` (`ci-all`), arm 5 — a read while a write holds `_writeGate` completes without waiting, serves the last good snapshot, and the next call refreshes: the deferred-refresh sentence, not the mapped-plugin reason it gives.

## Where
`src/housecarl-mcp/LoadOrderService.cs`: the `Resolver` and `Assets` getters, `SetInstance`,
`RefreshOnProfileChange`, `ReResolve`, `EnsurePathsDerived`, `StatusData`, `Stats`, `UpdateCache`,
`NamedProfileComposition`, `PapyrusSourceImportDirs`, `Dispose`, the class-parent cache
(`ClassParentsForDecompile`, `InvalidateClassParents`), `_gate` and `_writeGate`, and the explicit
`ILoadOrderHost` and `IAssetHost` members. `src/housecarl-mcp/LoadOrderHost.cs` declares `ILoadOrderHost` and
`AssetCapture`; `IAssetHost` is at the top of `src/housecarl-mcp/AssetLayers.cs`.
The head's asset-facing surface is one-line delegators to `_assetLayers`, the `AssetLayers` it builds over itself in
its constructor: `AssetStatus`, `SkseInventory`, `SkseConfigAudit`, `NativePairingAudit`, `SkyPatcherLayer`,
`NifInspect`, `NifSet`, `PlaceAssets`, the self-capturing `OpenSkyPatcherReplay` overload. `AssetArea` hands tests the instance, to set its seams.
`src/housecarl-mcp/ServiceResults.cs` holds the result records the head's lanes and the other areas' lanes return;
the asset records are in `AssetResults.cs`.
Tools: `housecarl_load_order_status`, `housecarl_set_mo2_instance`, `housecarl_update_status`.
