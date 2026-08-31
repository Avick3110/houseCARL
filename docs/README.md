# docs/ — engineering knowledge, in the open

**Class:** LIVING (this file). The convention itself is defined in
[`standards/HOUSECARL_DOC_HYGIENE.md`](../standards/HOUSECARL_DOC_HYGIENE.md) §8 — this
README only routes.

houseCARL is built and maintained by AI sessions under a non-coding owner, so the
knowledge a maintainer would normally carry in their head has to live somewhere readable —
and somewhere *public*, because contributors and review sessions don't have access to the
private working corpus. That somewhere is here.

## Layout

- **`architecture/`** — one short LIVING note per subsystem: what it is, how it's shaped,
  the contracts that hold it together. Notes land here as subsystems get touched under the
  §8 comment-register rule; an empty or thin folder means the migration is young, not that
  the subsystem is undocumented — the code and its probe suite remain the ground truth.
- **`decisions/`** — Architecture Decision Records (ADRs): short numbered files, one per
  decision — context, the decision, consequences. Immutable once merged; a change of mind
  is a *new* ADR that names the one it supersedes. A PR that makes an architectural
  decision lands its ADR in the same PR.

## Reading order for a newcomer

1. The repo [README](../README.md) — what houseCARL is and how to use it.
2. [CLAUDE.md](../CLAUDE.md) — how the project operates.
3. `decisions/` in order — why the architecture is the way it is.
4. The `architecture/` note for whatever subsystem you're entering.
