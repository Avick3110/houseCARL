---
updated: 2026-10-31
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
  names every arm consulted, not the last one tried. `FetchMany` answers many keys at once: each arm
  reads the keys still unanswered in one batch (an active-order arm through the read door,
  `BodyGather.Copies`) and falls back to its single-key fetch where it has no batch. Each key's hit,
  fault or miss is the one `Fetch` gives, with two differences: its `PulledBy` is empty (the caller
  holds who pulled it), and a throw out of an arm's batch read itself, such as the mid-call change,
  cancellation or out-of-memory the door rethrows, leaves the call rather than becoming one key's fault.
- Walk provenance is per node: each reached node records which arm produced its body and the full
  pull chain from a seed, which is what makes a cap refusal actionable.
- A seed path's shape is decided off the DECLARED type before any value is read, so a null cannot be
  mistaken for an absence of shape; null and empty are states of a shape, not shapes. An unknown or
  unsupported seed path is a refusal, never zero seeds.
- Cycles are found by a post-walk pass over the recorded edge set, not by the BFS parent map: a
  mutual reference between two siblings is a real cycle no ancestry test can see. Any other repeat
  is a re-convergence and is deduped. A refusal carries nothing usable rather than a partial copy.
- The reverse index is lazy, partitioned per plugin and keyed on (path, last-write, length) beside the order-wide
  epoch, generational so a read is never torn, and plugin-atomic so a half-read plugin leaves no
  partial edges. Its measured build cost and held size are in
  `dev/projects/tool-surface-2.0/SPEC.md` §3.2 and its 2026-09-05 amendment.
- It answers in CANDIDATES; the scan that follows still judges the body the caller means. A plugin
  the walk could not read makes the positive question SHORT and the orphan sweep OVER-inclusive, and
  the accounting states whichever reading the asking lane needs.
- Each partition also keeps, built in the same loop as its edges, the keys whose copy holds no edge
  (deleted, or not a link container), the keys whose link walk threw, and each lenient-read note. A
  transitive reverse walk judges a candidate off its winner plugin's partition with no body read:
  `ReferencersOf` given `winnerOf` names the candidates whose edge comes from that partition, and
  `WinnerFact` says what that partition saw of the copy. It reads the body, through the read door,
  only when the winner's partition is missing or unreadable, or when the winner plugin carries the key
  twice (`IndexView.WinnerHoldsOnce`): the index merges both copies, while a body read and
  `references=` take the first.
- A forward hop is one pass through the read door with no 2,000-row cap per pass: it holds one
  group's asked bodies plus the hop's node facts at once, inside the door's bound. The rise in the
  forward walk's peak (1.66 → 1.89 GB on ARR's Player walk) is likely the hop's facts.
- A transitive reverse walk spends its node budget BEFORE a candidate is verified, so a spent budget
  stops the body reads as well as the reach and a raised budget on a retry sees the same graph; the
  hop the cut landed on is marked rather than reading as a hop that reached nothing.
- The transitive reverse selection takes an optional expand predicate, built from `walk.through`
  and the stop exclusions: a record it rejects is still reached but kept as a boundary, never added to
  the next frontier. Unset, the next frontier is exactly the reached list.

## Pinned by

- `ClosureWalkGraphTests` / `SourceChainTests` — the walk's caps, cycles and refusals, and the chain's
  first-hit-wins, fault-stops and miss-names-every-arm rules.
- `AssetWalkDoorTests.ManyKeysAnswerAsOneKeyAtATime` — `FetchMany`'s hit, fault and miss are `Fetch`'s, each batch
  is read once and a later arm reads only the keys still unanswered; `AssetWalkDoorFaultTests.ABrokenGroupIsTheArmsFaultNotAMiss`
  — a group the door's walk faults on is the arm's fault, not a miss.
- `RecordsWalkCycleTests` / `RecordsWalkCostTests` / `RecordsWalkUnscannableTests` — the walk lanes.
- `RecordsReverseIndexTests` — the unbounded reverse selection, the orphan sweep and the index's
  in-band accounting line.
- `RecordsWalkCostTests.AReverseWalkReadsNoCandidateBody` — a reverse walk's check reads no candidate
  group; `ReverseWalkDupeFormIdTests` — a winner holding a key twice is judged on its first copy.
- `ReverseWalkTypesTests` — `walk.through` and `walk.exclusions` on a walk: what it expands through,
  where it stops, what it leaves out, and the refusals.

## Where

`src/housecarl-core/ClosureWalk.cs` and `SourceChain.cs` (the walk and its ordered source universe),
`ReverseReferenceIndex.cs` (the reverse edge and the unbounded reverse selection).
Entry points: `housecarl_records` (`references=`, `walk=`).
