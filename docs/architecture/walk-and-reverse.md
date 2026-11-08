---
updated: 2026-11-07
covers: [src/housecarl-core/ClosureWalk.cs, src/housecarl-core/SourceChain.cs, src/housecarl-core/ReverseReferenceIndex.cs, src/housecarl-mcp/WalkGrammar.cs]
---
# Walk and reverse

## What it is

The walk half of the selection surface: the closure walk and its ordered source universe, and the
whole-order reverse index behind an unbounded `references=`. The `where=` predicate and containment
are `docs/architecture/select-and-walk.md`. The walk reads a record's links only through the one link door,
`RecordLinks` (`docs/architecture/read-engine.md`), as the asset walk and the records walk do.

## Contracts

- The closure walk's source universe is an ORDERED list of poles tried in order, first hit wins. It
  is fallback, never merge, and never the set-valued SOURCE a comparison uses. An arm that HAS the
  record but cannot parse it STOPS the chain rather than answering with a later arm's bytes; a miss
  names every arm consulted, not the last one tried. `FetchMany` is the chain's only read, and one key
  is a set of one: each arm reads the keys still unanswered in one batch (an active-order arm through
  the read door, `BodyGather.Copies`; an off-order file arm off the Mutagen link cache it builds once).
  A fault's `PulledBy` is empty (the caller holds who pulled it), and a throw out of an arm's batch read
  itself, such as the mid-call change, cancellation or out-of-memory the door rethrows, leaves the call
  rather than becoming one key's fault.
- The closure walk reads by depth: when it reaches a key it has not read, it reads that key and every
  key still queued at the same depth that it would enter (not seen, inside the scope), at most as many
  as the node cap leaves room for, in one `FetchMany`. The answers, nodes, refusals and their order are
  the key-at-a-time walk's.
- Copy's walk (`ClosureWalk.Run`) and the asset walk (`AssetWalk.WalkRecords`) are two loops on purpose: copy
  decides each key when it dequeues it and refuses at any cap, miss or fault, so it never writes a partial closure;
  the asset walk decides each link as it follows it, by the seed's own home, and notes what it left and goes on,
  except at a 'refuse' exclusion, which fails the call. What
  they share is code: the chain and its `FetchMany`, and `SourceChain.NamedPlugins`, the plugins the arms name (copy
  drops base masters from it, the asset walk keeps them). The few answers they must give alike over one graph both
  reach whole (the records entered and their breadth-first order, one read per depth, a null link skipped) are
  `CopyAssetWalkAgreementTests`. Where they part is a record whose links read only leniently, or not at all: the
  asset walk follows the lenient links and notes it, or notes the record and follows nothing; copy refuses there
  (`WalkRefusalKind.UnreadLinks`, with the pull chain), because it could not duplicate that record. A deleted record
  links to nothing in both (#1316).
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

## One walk grammar

- `WalkGrammar` (`src/housecarl-mcp/WalkGrammar.cs`) is the one parse of a walk's values and seeds (#1317). Every
  walk calls it and nothing else parses these values, so the names, defaults and refusal words are the same on every
  walk by construction. `Parse` takes `depth`, `max_nodes`, `through` (already read through the list door) and
  `exclusions` plus the asking walk's default depth and budget reading, and returns typed `Values` or one refusal:
  depth below 1; max_nodes below 1, or above `RecordsWalk.Ceiling` on a per-seed reading only (forward, carrier); an
  empty `through`; a `through` or `exclusions` entry naming no type; a blank `match`; a severity that is missing or
  not stop/refuse. On the asset walk (`edgeKinds`), a `through` token is a record type or one of the file edge kinds
  (`WalkEdgeKind`, assets.md "Walking the links"), carried apart in `Values.Through` and `Values.Edges`; one that is
  neither is refused naming both kinds of value, and `seed` is refused (seed rows are always shown). The records walk
  refuses an edge kind by name and any other non-type in its words as before (#1278). `through` and `exclusions` expand to display names (a polymorphic base to its arms), each
  exclusion type once at its first entry, so `[{Global, stop}, {GlobalFloat, refuse}]` stops at every global and
  copy's `ClosureWalk` dictionary never sees a duplicate.
- The records walk keeps its spellings and defaults (depth 16, max_nodes 2,000). The asset walk takes
  `walk=true` (its defaults) or `walk={depth, max_nodes, through, exclusions}` (`ReadAssetWalk`), depth 3, one
  shared budget with no ceiling, counting every record entered, seeds included; `seed_paths`, `follow` and
  `direction` are refused by name. Copy takes `ParseExclusions` alone as `exclusions=[{match, severity}]`;
  `exclude_types=` is refused naming the new spelling, and a bare type no longer defaults to refuse.
- The asset walk judges `through`'s record types and the exclusions where it enters a record, on the read body's
  type, so the type is the same whichever source arm read it. A stop is not entered (no files, no links) and is counted
  in a note; a type outside `through` is not entered and is counted per type in the records walk's "left out, not in
  walk.through:" line, which on the asset walk also counts, per edge, the rows `through`'s edge kinds did not show; a refuse fails the call in `WalkExclusionMatch.RefuseSentence`'s words. Seeds are never judged.
  Its depth and budget notes name `walk.depth=` / `walk.max_nodes=` and say to raise them.
- `ParseSeed` turns one seed token into its key or `bad FormID '<token>': <why>`; the records walk's rows carry those
  words alone, and the asset walk's error node adds ` Expected 'XXXXXX:Plugin.esp'.` after them. Each walk carries a malformed seed
  as its own row: the forward reading form (`WalkSeedResult.Malformed`), the carrier reading form and the transitive
  reverse walk put the raw token in the selection, which the list lane renders as a per-item error row; the bad token
  is not counted as a reached record and is not sent to `form='chain'`; the chain forms keep their own rows; the
  asset walk adds an error node at the token, edge `seed`. All seeds malformed answers only the
  error rows. An unresolved seed still answers as each walk did.
- Not covered: `exclusions` is an object list, so it takes no `@file` on any of the three tools; an unknown member
  inside the walk object (`walk={"depht": 1}`) is dropped, not refused: by the binder on the records walk, by the
  asset walk's own read of the object (`WalkGrammar.ReadAssetWalk`) on `asset_status`.

## Pinned by

- `WalkGrammarTests` — the shared refusal cases against both walks, the per-seed ceiling, the asset walk's depth,
  budget, through and exclusions, its refusal of the records walk's shaping values, and a malformed seed's row on the
  asset, reverse and forward walks; `through`'s token refusals on both walks (`ThroughRefusesATokenNamingWhatItTakes`)
  and every edge kind as a token (`ThroughTakesEveryEdgeKindTheWalkEmits`). `WalkBadSeedReadingFormTests` — a malformed seed's row on the carrier reading
  form, alone and beside a good seed. `CopyWireParseTests` — copy's exclusions: signature, base type, first match,
  unknown type, the retired `exclude_types=`, and the same patch byte for byte as the old spelling's core input.

- `ClosureWalkGraphTests` / `SourceChainTests` — the walk's default caps, cycles and refusals, and the chain's
  first-hit-wins, fault-stops and miss-names-every-arm rules; `ClosureWalkGraphTests.EachDepthIsReadInOneBatch`
  — one batch per depth, reaching what the key-at-a-time walk reaches.
- `CopyEndToEndTests` — the closure copy's own caps and reached order, end to end; the contract is in
  `docs/architecture/write-path.md`.
- `SourceChainNamedPluginsTests` — the arms' plugins: `winner` and a spelling with no plugin extension give none (a bare
  `.esp` would give `ModKey.Null`, see `SourceChain.NamedPlugins`), a base master and an off-order file arm are returned. `CopyAssetWalkAgreementTests` — copy's walk and the asset walk enter
  one graph in the same order, read each depth's group once and skip a null link;
  `ALenientLinkSetIsWalkedWithANoteByOneAndRefusedByTheOther` — where they part at a lenient perk.
  `UnreadLinksWalkTests` — each walk at a lenient, a corrupt and a deleted perk, and copy's refusal there.
- `AssetWalkDoorTests.ManyKeysAnswerAsOneKeyAtATime` — `FetchMany` over many keys answers each key's hit, fault and miss as it does over that key alone (a set of one; no other per-key read exists), each batch
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
