# ADR 0001 — Terse comments; engineering knowledge moves to a public `docs/` home

**Class:** ARCHIVE (ADR). Immutable once merged; superseded by a later ADR, never edited.
**Date:** 2026-08-31 · **Status:** accepted (owner-ruled)

## Context

houseCARL has no persistent human reader: the owner does not read code, and the AI
sessions that write it do not persist between sessions. The codebase adapted by carrying
its context in essay-register comments — paragraphs inside source files explaining why
code is shaped the way it is, what was tried, and what guarantees hold.

Two costs surfaced. Every session pays to read the essays in every file it opens, whether
or not it needs them. And a prose paragraph asserting a guarantee can be false with
nothing to notice — an August 2026 review found several comments asserting guarantees the
code did not provide, while the probe suite (which *can* notice) sat one pointer away.

A third problem hid underneath: most of the durable knowledge in those paragraphs had its
only other home in a private working corpus, invisible to the public repo, outside
contributors, and any session without local access.

## Decision

1. **Source comments are terse.** They state constraints and contracts the code cannot
   express — one or two lines — and nothing else.
2. **A guarantee's home is the probe that proves it**; the comment is a pointer to that
   probe. A guarantee no probe covers doesn't get written as a comment.
3. **Narrative and rationale move to `docs/`, in the repo, public:**
   `docs/architecture/` (living per-subsystem notes) and `docs/decisions/` (these ADRs).
   A PR that makes an architectural decision lands its ADR in the same PR.
4. **One home per fact.** The move is a replacement, never a copy — the comment is
   deleted where the note is written.

Full convention: `standards/HOUSECARL_DOC_HYGIENE.md` §8.

## Consequences

- New code is written to this rule immediately. Existing essay comments are cleaned
  opportunistically as files are touched — no bulk scrub, and none in code already
  flagged for deletion by the 1.x retirement.
- The public repo becomes self-explanatory over time: a contributor or review session
  can learn why the architecture is the way it is without private-corpus access.
- `docs/` is itself a surface that can go stale; the same-PR rule, the one-home rule,
  and ADR immutability are the protections.
