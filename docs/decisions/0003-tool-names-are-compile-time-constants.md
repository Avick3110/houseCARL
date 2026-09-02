# ADR 0003 — Tool names are compile-time constants

**Class:** ARCHIVE (ADR). Immutable once merged; superseded by a later ADR, never edited.
**Date:** 2026-09-02 · **Status:** accepted · **Issue:** #475

## Context

houseCARL's shipped code named its own MCP tools by spelling them. Measured across
`housecarl-mcp` and `housecarl-core`: 609 occurrences of a `housecarl_*` name in 69 files, of
which 330 were in string literals — a tool's own `[McpServerTool(Name = …)]` argument, the
description and refusal prose that tells a caller which tool to reach for next, alias rows that
redirect a retired name to its successor, and code paths that write the name into an artifact
manifest or a results-store file path.

A string has no referent the compiler can check. Nothing connected the sentence "use
`housecarl_read_record` for the winner" to the tool of that name, so removing a tool left every
one of those sentences standing and correct-looking. Finding them again meant somebody
noticing them — a hand-built population, which is short by exactly what nobody thought of.

That mattered because a large deletion was coming: the 1.x read and check tools are removed
wholesale, and the removal's checklist was going to be a list somebody assembled by reading.
Two class-stops in the preceding write-family deletion traced to the same root — populations
noticed rather than derived.

## Decision

**Every tool name in shipped code is a reference to a compile-time constant, not a literal.**
One constant per declared tool, in a single generated class, `HousecarlCore.ToolNames`.

Consequences of the shape, each load-bearing:

- **`const`, not `static readonly`.** The names are spliced into `[McpServerTool(Name = …)]`,
  `[Description(…)]` and similar attribute arguments, which must be constant expressions.
  `"…" + ToolNames.Records + "…"` is one; a `static readonly` field is not. This is forced, not
  preferred.
- **The registry lives in `housecarl-core`, not `housecarl-mcp`.** The project reference runs
  mcp → core, and 25 of the rewritten sites are in core, so a registry in mcp could not reach
  them. Placement follows the derived population.
- **The population is DECLARED, not registered.** A constant exists for every tool whose
  `[McpServerTool(Name = …)]` attribute names it in source. That is deliberately not the set the
  SDK actually scans, which additionally requires `[McpServerToolType]` on the declaring type.
  One tool is declared and has never been registered; its attribute still spells its name, so it
  is owed a constant. A completeness check written against the *registered* set would report
  that constant as spurious.
- **Retired spellings get no constant.** A name that no longer names a tool has no constant
  whose deletion should break anything, and a second hand-kept population is the hazard this
  decision exists to remove. Retired names stay literals in the alias table that redirects them.
- **The registry is generated, and regeneration is idempotent.** It is emitted from the
  attributes by script. The script resolves an attribute argument that is already a constant
  reference back through the registry, so it can be re-run on its own output; it refuses rather
  than guessing when an argument is neither a literal nor a resolvable constant.

## Consequences

**What this buys.** Deleting a tool means deleting its constant, and the compiler then names the
surviving sites that still refer to it. The deletion checklist becomes a build-error list.

**The checklist is iterative, not one-shot.** Two separate things shorten the first error list,
and the second hides more than the first.

Within a project, C# binds declarations — attribute arguments, const initializers — before method
bodies and stops once the first phase has failed, so body-level references surface only on a later
pass. Deleting a constant with 34 references across 8 files reported 10 diagnostics in 5 files
on the first build.

Across projects, a failed build skips everything downstream of it. The registry lives in the core
project and the tool surface depends on it, so a constant deleted in core fails core and the tool
project is never compiled at all — every site there is invisible until core is green again.
Measured: deleting one constant with 11 references across 6 files in both projects reported
diagnostics in exactly one file — its single core-project reference; the five tool-project files
appeared only once core built again.

Build, fix what is named, build again, until green. A short first error list is not "that was all
of them", and an error list confined to one project is not evidence that the other is clean.

**Nothing a caller sees changed** when the constants were introduced. The names are identical, so
`tools/list` is byte-identical before and after, and the compiled string literals fold back to
the same values. There is no consumer-observable change and therefore no changelog entry.

**Two residues remain, both by rule.** Deletion-flagged 1.x tool bodies keep their literals —
they are condemned code, and editing them is work that dies with them. The guard suite is a
separate project that is itself being retired and converted; nothing leaves it except by a
conversion change. So the compile-error checklist covers the shipped assemblies, not every file
in the repository.

**A guard becomes redundant.** A source-scanning check that held every `housecarl_` token in
caller-facing prose against the set of real tool names loses most of its population, because a
constant reference cannot name a tool that does not exist. It does not fail; it empties. The one
property it still carried that constants do not give — catching a name that is *declared but not
registered* — moves to a completeness check over the registry rather than staying behind in a
harness that is being retired.
