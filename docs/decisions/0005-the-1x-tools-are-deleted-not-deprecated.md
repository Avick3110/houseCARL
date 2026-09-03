# ADR 0005 — The 1.x tools are deleted, not deprecated

**Class:** ARCHIVE (ADR). Immutable once merged; superseded by a later ADR, never edited.
**Date:** 2026-09-03 · **Status:** accepted · **Issue:** #468

## Context

houseCARL 1.x published 45 tools. The 2.0 surface replaces most of them with a much smaller set of
composed tools: `housecarl_records` alone absorbs eight read tools, `housecarl_check` absorbs two more
plus part of a third, and four write tools absorb six.

"Absorbs" is exact here rather than approximate. Each 2.0 tool was designed as the closure of the
parameter planes its ancestors occupied — one record or many is a set, the shape of the answer is a
form, whose version to read is a pole — so every call the old tool could express has a spelling on the
new one. That is what made deletion a real option instead of a wish.

The question this decision answers is what happens to the old names. The usual answer is a deprecation
window: keep the tool, mark it deprecated, remove it a release or two later. That is a good answer when
callers are code you cannot change on a schedule you control. It is a poor answer here, for reasons
that are particular to this project:

- **The caller is a language model reading tool descriptions.** A deprecated tool is still a tool in
  `tools/list`, and its description still teaches. Two tools that do the same thing, one of them
  discouraged in prose, is worse guidance than one tool — the model has to be persuaded rather than
  simply told.
- **Every surviving 1.x tool has to be maintained as if it were live.** Its refusals, its remedies and
  its guards all continue to cost review attention, and the project measured that cost: at the point
  this deletion began, the guard suite was 1:1 with product code by line count, and a large share of it
  was pointed at tools with a decided end date.
- **There are no installed users to protect on this schedule.** The 1.9.0 zip is the last 1.x release
  and the next version bump is 2.0.0, so nobody receives a build in which a tool is deprecated. The
  window would exist only in the development tree.

## Decision

**A 1.x tool whose capability the finished 2.0 surface covers is deleted, in the change that ships its
replacement. There is no deprecation window.**

Four consequences of the shape, each load-bearing:

- **Rolling, not staged.** Deletion is not deferred to a single cut at 2.0.0. Each change that ships a
  2.0 replacement deletes the tools it replaces, so the deleted code cannot accumulate work. Where a
  replacement shipped before this rule existed, a catch-up change deletes the arrears; this ADR ships
  on the last of those.

- **The population is derived from the specification's absorption map, never hand-listed.** A 1.x tool
  the map does not cover is surfaced as a question — either it is chartered a survivor by name, or the
  2.0 design has a coverage gap. It is never a silent survivor, and never a silent deletion. One tool
  is a chartered survivor today: the NPC appearance copy, which is the only home of a carry the generic
  copy tool cannot yet express (#387).

- **A deleted name is not a dead end.** `AliasTable`'s retired-tool rows answer a call naming a tool
  the server no longer has, with the successor AND that tool's parameter migration — what the old
  parameters are called now. Where a tool split, the row names both destinations, because sending a
  caller to the half that deliberately does not answer their question is its own dead end. Those rows
  outlive the tools by design, so a retired name is spelled there as a literal: it is the one place in
  shipped code where deleting a tool's constant means restoring its spelling rather than removing the
  site.

- **Guards on a deleted tool die with the tool; behaviour that survives gets a fresh test in the same
  change.** A guard whose subject is a deleted tool's response is not evidence about anything once the
  tool is gone, and repointing it at the successor was tried and rejected: it preserves the old
  guard's shape, which is what the guard rewrite (ADR 0003) exists to leave behind. So the arms are
  deleted, and each one whose behaviour the successor still carries is written fresh as a test against
  that successor, in the same change — never left as a promise for later.

## Consequences

- **Nothing shipped to a user changes at the moment of deletion**, because no 1.x release follows.
  What changes is the development tree and the next release's tool list.

- **A caller working from 1.x documentation lands on the successor in one hop.** That is a property
  with a test rather than an intention: every tool 1.9.0 published is either still on the surface or has
  a retired-tool row whose named successors are all registered and all named in the live response.

- **Comments and prose that named a deleted tool are repaired where they are false about the current
  surface, and left alone where they are accurate about history.** "This section renders X" becomes
  false the moment X is deleted; "absorbs X" does not.

- **The bundled skills are not repaired here.** They name deleted tools, and they are rewritten wholesale
  against the shipped surface at the skill wave. Repairing them twice would be work done to be thrown
  away, and the maintainer ruled that the intervening breakage is acceptable because it reaches only
  people cloning the repository before 2.0.0. The reference counts are recorded on the change that
  deletes each family so that wave inherits a measurement rather than a survey.

- **The old guard harness shrinks by whole files, and only where the file's whole subject went.** A file
  that also guards engine behaviour keeps that half; its residue entry stays, and only its tool-layer
  block is removed. The countdown that measures the harness's remaining size (ADR 0003) is what makes
  that shrink visible, and it moves in this change by exactly the files that left.
