---
updated: 2026-09-23
covers: [src/housecarl-mcp/LoadOrderService.cs]
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
- `_writeGate` serializes the whole resolve, stage and commit of every plugin write; `SetInstance` takes it too, so an instance switch cannot tear a write in flight. Where both gates are held the order is `_writeGate` then `_gate`.
- A read-path freshness refresh is DEFERRED while a write holds `_writeGate` — probed with `TryEnter`, never blocking — because a rebuild transiently maps every plugin including the one the write is serializing and dispose-swaps the resolver that write captured; a skipped refresh serves the last good snapshot and re-checks next call.

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
- `write-mutex-guard` (`ci-all`) — concurrent same-default-name writes each allocate their own folder and commit their own bytes.
- `ProfileRewriteTests.AWarmAssetCallAnswersOffTheKeptBuildSaysSoAndFollowsTheProfileOnceItIsFree`, `TheRecordIndexRefusesRatherThanAnswerOffASupersededBuild` and `AColdRecordBuildAfterAHoldDoesNotStrandTheKeptAssetBuild` — the held-profile split between the two lanes.
- `freshness-capture-guard` (`ci-all`), arm 5 — a read while a write holds `_writeGate` completes without waiting, serves the last good snapshot, and the next call refreshes: the deferred-refresh sentence, not the mapped-plugin reason it gives.

## Where
`src/housecarl-mcp/LoadOrderService.cs`: the `Resolver` and `Assets` getters, `SetInstance`,
`RefreshOnProfileChange`, `ReResolve`, `EnsurePathsDerived`, `StatusData`, `UpdateCache`,
`NamedProfileComposition`, `PapyrusSourceImportDirs`, `_gate` and `_writeGate`.
Tools: `housecarl_load_order_status`, `housecarl_set_mo2_instance`, `housecarl_update_status`.
