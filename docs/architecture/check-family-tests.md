---
updated: 2026-09-23
covers: [src/housecarl-mcp-tests/CheckErrorsFamilyTests.cs, src/housecarl-mcp-tests/CheckErrorsFixtures.cs, src/housecarl-mcp-tests/CheckErrorsWorld.cs, src/housecarl-mcp-tests/CheckErrorsWorldTests.cs, src/housecarl-mcp-tests/ScriptsFamilyTests.cs, src/housecarl-mcp-tests/ScriptsFixtures.cs, src/housecarl-mcp-tests/EpochCheckSweepTests.cs, src/housecarl-mcp-tests/EpochWorld.cs, src/housecarl-mcp-tests/DialogueFamilyTests.cs, src/housecarl-mcp-tests/DialogueWorld.cs]
---
# The check families' tests: which lane a fact is driven from, and why

## What it is
The errors, scripts and dialogue families of `housecarl_check` are asserted from **two** driving lanes, and
which lane a given fact uses is a decision, not a convenience. This file is the home of that decision, cited
from `CheckErrorsFixtures`, `ScriptsFixtures` and `CheckErrorsFamilyTests` under ADR 0001 (a terse comment is
terse because the knowledge has a public home).

## Contracts

### The two lanes

**LIVE — a real service call over a synthetic MO2 world.** `Svc.CheckErrors(...)` /
`Svc.ValidateScripts(...)` / `Svc.CheckDialogue(...)` against `CheckErrorsWorld`, `ScriptsWorld`, `EpochWorld`
or `DialogueWorld`, rendered through `Wire.RenderCheck` / `JsonWire.RenderCheck` — byte-identically the entry
point `CheckTools` calls. This is the default, and most facts use it.

**DTO — a hand-shaped result rendered through the same renderer.** `CheckErrorsFixtures.Result(...)` /
`ScriptsFixtures.Result(...)` build an `ErrorCheckResult` / `ScriptCheckResult` directly and hand it to the
same `Wire.RenderCheck` / `JsonWire.RenderCheck`. The renderer under test is identical; only the input's
provenance differs.

### The rule

**Drive LIVE unless the world cannot produce the shape the fact is about.** A fact about what the SWEEP
computes must be live — a hand-shaped result would be asserting the fixture, not the engine. A fact about what
the RENDERER does with a given result may be DTO-driven, and must be whenever the shape is one a synthetic
world cannot be made to emit without engineering the failure itself.

The shapes that force the DTO lane today — the population, derived from every `Result(...)` call in
`CheckErrorsFamilyTests` and `ScriptsFamilyTests` (8 sites) plus the one place a result is hand-shaped
without going through `Result(...)` — `FactS13`'s `listing with { ExcludedPlugins = … }`, 9 sites in all —
each named in the test that uses it:

| shape | why the live world cannot make it |
|---|---|
| a per-record or per-plugin **scan error** (`PluginErrors.ScanError`, `UnscannableRecords`) | it needs Mutagen to throw on a record body mid-walk; a plugin crafted to do that is a fixture engineered around a library's internals, and it would re-break whenever Mutagen's parser changes |
| an **excluded-plugin roster** of a chosen size | the world has one unparseable plugin; a roster wide enough to be CUT needs several, and each is sixteen bytes of garbage carrying no other fact |
| a **row-width** pair differing only in one field's length (the floor arm) | the floor is a property of the render, not of any world; two worlds differing only in an EditorID's length would be two whole MO2 instances asserting one number |
| an **empty histogram axis** (a sweep that ran the walk and tallied nothing) | every world that carries findings tallies them; an axis that ran and found nothing needs a world with no findings at all, which then proves nothing else |
| a **cap band** wider than the world's own body | the band has to reach caps at which this world simply renders whole |

Everything else — totals, class exclusion, budget and cut accounting over real findings, the baseline split,
the histograms, the epoch stamps, the off-order qualifier, the dialogue merge — is live.

### What the DTO lane must not be used for

- **Never for a fact about the sweep's own computation.** `TotalDangling`, `BaselineDangling`,
  `RecordsWithScripts`, which properties the property filter keeps: hand-shaping the result makes the
  assertion circular. Those are live or they are nothing.
- **Never to avoid a world that is merely inconvenient to build.** The five rows above are the population; a
  sixth needs a reason written beside it, in the test.

### The fixture-known totals have their own test

`CheckErrorsWorldTests` pins `CheckErrorsWorld`'s `TotalDangling` / `BaselineDangling` / `ScannedPlugins`
against a live sweep. Every live fact test takes its numbers from those constants, so a drift in the world
fails in one place with a clear message rather than in every fact test at once. `ScriptsWorld` carries the
same arrangement in `ScriptsWorldTests` (landed in #486 PR 1).

### The lock facts build their own world

`DialogueFamilyTests`' three lock facts (`FactD3`, `FactD4a`, `FactD4b`) construct a `DialogueWorld` with
`new()` rather than taking the shared collection fixture, because a held file is unreadable to everything else
in the process. Each also calls `Svc.Stats()` once, unlocked, before taking the hold — see `DialogueWorld`'s
own doc and issue #353 for the behaviour that makes this necessary.

## Pinned by
Nothing pins this.

## Where
- `src/housecarl-mcp-tests/CheckErrorsFamilyTests.cs`, `ScriptsFamilyTests.cs`, `DialogueFamilyTests.cs` and
  `EpochCheckSweepTests.cs` — the family facts.
- `src/housecarl-mcp-tests/CheckErrorsFixtures.cs` and `ScriptsFixtures.cs` — the DTO lane's `Result(...)`
  builders and the readers the DTO tests share.
- `src/housecarl-mcp-tests/CheckErrorsWorld.cs`, `EpochWorld.cs` and `DialogueWorld.cs` — the live worlds;
  `CheckErrorsWorldTests.cs` — the fixture-known totals.

## Related

- `docs/architecture/test-project-fixtures.md` — the Papyrus `.pex` writer and the file-lock harness these
  tests are built on.
- `docs/decisions/0005-the-1x-tools-are-deleted-not-deprecated.md` — "guards on a deleted tool die with the
  tool; behaviour that survives gets a fresh test", the rule #486 PR 2 discharges.
- `docs/architecture/check-families.md` and `docs/architecture/check-scripts-and-dialogue-families.md` — the
  contracts these tests assert.
