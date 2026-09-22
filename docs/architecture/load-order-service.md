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

### The service's answers
- The index build is lazy, so startup and `tools/list` are instant, and it is serialized on one gate because the server dispatches tool calls concurrently.
- A refresh that lands in MO2's own profile-rewrite window keeps the snapshot already built and does NOT advance the baseline, so the next call re-checks and follows the new profile.
- A profile change that could not be RE-READ is remembered: the asset lane keeps answering and says so in its warnings, and the record lane refuses, because the record index IS the load order.
- A mid-write read that resolves no paths keeps the last good snapshot and does not advance the baseline, so the next call recovers once MO2 finishes writing.
- The asset resolver is built only on an asset query, never forces the record index build, and is dropped whenever the active mod or archive SET changes.
- A record build that lands while an asset build was KEPT across a profile change drops that asset build instead of advancing the baseline past it, so the next asset call rebuilds rather than silently serving the old answer.
- A body the index says exists but the plugin cannot yield is a NAMED inconsistency, never a silent null (`FetchRecord`); `GetRecord` answers null for a plugin absent from the order, excluded this build, or in the order but not defining the FormKey — a caller that must tell those apart asks `ContainsPlugin` too.
- A refusal naming a plugin the order does not contain carries the INJECTED explanation of why when there is one, and the did-you-mean otherwise. The resolver is built from a bare ordered path list and knows nothing of MO2, so the explanation is injected by the service.
- `OpenOverlay` is the single overlay-open choke point, and it redirects strings lookup to the real game-Data folder only when the plugin's OWN folder carries no strings source for that plugin.
- Light and master-block are separate per-plugin facts read off the same open header: an esp-fe is light in the FormID space and a regular plugin in the order.
- The first active plugin whose KIND could not be read is kept as a position, not a flag: a runtime FormID landing at or after it is refused, one landing before it answers normally.

### Configuration modes
- INSTANCE, the product default: one MO2 instance folder, roots and active profile derived from `ModOrganizer.ini`, and a profile switch picked up on the next tool call.
- EXPLICIT, a dev override: the three roots are configured directly, no ini is read and no profile-switch watch runs.
- UNCONFIGURED: the server still boots and every tool returns the prompt for the MO2 path until `housecarl_set_mo2_instance` is called.

## Pinned by
- `ProfileRewriteTests.AWarmAssetCallAnswersOffTheKeptBuildSaysSoAndFollowsTheProfileOnceItIsFree`, `TheRecordIndexRefusesRatherThanAnswerOffASupersededBuild` and `AColdRecordBuildAfterAHoldDoesNotStrandTheKeptAssetBuild` — the held-profile split between the two lanes.
- `RuntimeFormIdTests.ALightPluginsRecordReadsByItsRuntimeFormId`, `AFullPluginsRecordReadsByItsLoadIndex`, `ALightIndexNoActivePluginOccupiesIsRefused` and `ADynamicFormIdIsRefusedAsBelongingToNoPlugin` — the two runtime address tables and their refusals.

## Where
`src/housecarl-mcp/LoadOrderService.cs`: the `Resolver` and `Assets` getters, `SetInstance`,
`RefreshOnProfileChange`, `ReResolve`, `EnsurePathsDerived`, `StatusData`, `UpdateCache`,
`NamedProfileComposition`, `PapyrusSourceImportDirs`, `_gate` and `_writeGate`.
Tools: `housecarl_load_order_status`, `housecarl_set_mo2_instance`, `housecarl_update_status`.
