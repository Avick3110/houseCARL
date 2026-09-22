---
updated: 2026-09-23
covers: [src/housecarl-core/ClosureWalk.cs, src/housecarl-core/SourceChain.cs, src/housecarl-core/ReverseReferenceIndex.cs]
---
# Walk and reverse

## What it is

The walk half of the selection surface: the closure walk and its ordered source universe, and the
whole-order reverse index behind an unbounded `references=`. The `where=` predicate and containment
are `docs/architecture/select-and-walk.md`. The walk expands through Mutagen's `EnumerateFormLinks`.

## Contracts

- The closure walk's source universe is an ORDERED list of poles tried in order, first hit wins. It
  is fallback, never merge, and never the set-valued SOURCE a comparison uses. An arm that HAS the
  record but cannot parse it STOPS the chain rather than answering with a later arm's bytes; a miss
  names every arm consulted, not the last one tried.
- Walk provenance is per node: each reached node records which arm produced its body and the full
  pull chain from a seed, which is what makes a cap refusal actionable.
- A seed path's shape is decided off the DECLARED type before any value is read, so a null cannot be
  mistaken for an absence of shape; null and empty are states of a shape, not shapes. An unknown or
  unsupported seed path is a refusal, never zero seeds.
- Cycles are found by a post-walk pass over the recorded edge set, not by the BFS parent map: a
  mutual reference between two siblings is a real cycle no ancestry test can see. Any other repeat
  is a re-convergence and is deduped. A refusal carries nothing usable rather than a partial copy.
- The reverse index is lazy, partitioned per plugin and keyed on (path, mtime) beside the order-wide
  epoch, generational so a read is never torn, and plugin-atomic so a half-read plugin leaves no
  partial edges. Its measured build cost and held size are in
  `dev/projects/tool-surface-2.0/SPEC.md` §3.2 and its 2026-09-05 amendment.
- It answers in CANDIDATES; the scan that follows still judges the body the caller means. A plugin
  the walk could not read makes the positive question SHORT and the orphan sweep OVER-inclusive, and
  the accounting states whichever reading the asking lane needs.
- A transitive reverse walk spends its node budget BEFORE a candidate is verified, so a spent budget
  stops the body reads as well as the reach and a raised budget on a retry sees the same graph; the
  hop the cut landed on is marked rather than reading as a hop that reached nothing.

## Pinned by

- `ClosureWalkProbe` / `SourceChainProbe` — the walk's caps, cycles and refusals, and the chain's
  first-hit-wins, fault-stops and miss-names-every-arm rules.
- `RecordsWalkCycleTests` / `RecordsWalkCostTests` / `RecordsWalkUnscannableTests` — the walk lanes.
- `RecordsReverseIndexTests` — the unbounded reverse selection, the orphan sweep and the index's
  in-band accounting line.

## Where

`src/housecarl-core/ClosureWalk.cs` and `SourceChain.cs` (the walk and its ordered source universe),
`ReverseReferenceIndex.cs` (the reverse edge and the unbounded reverse selection).
Entry points: `housecarl_records` (`references=`, `walk=`).
