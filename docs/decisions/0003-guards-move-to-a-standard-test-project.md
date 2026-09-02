# ADR 0003 — Guards move to a standard test project, behind a residue countdown

**Class:** ARCHIVE (ADR). Immutable once merged; superseded by a later ADR, never edited.
**Date:** 2026-09-02 · **Status:** accepted · **Issue:** #470

> **Amended 2026-09-03 by #478 — what is true of the tree that PR ships on.** Three descriptions
> below have expired. The decision has not: each is that decision applied to the last two hand-kept
> pieces of it.
>
> - **Four numbers gate, not three** — the probe-file key set, each file's `ci-all` row count, the
>   guard files outside that set, and the standalone CI steps. A guard given its own `ci.yml` step,
>   hosted in a file already counted, moves none of the other three, so the countdown could reach
>   zero with CI still running it. The line total is still derived and printed, not asserted; it is
>   now the fifth number, not the fourth.
> - **The baseline is a per-file map, not a set of totals.** The totals derive from it. A conversion
>   PR deletes its own key instead of lowering numbers every other conversion PR also lowers, so two
>   conversions of different families merge cleanly.
> - **There is no registry row to remove.** The `ci-all` roster is reflected off a `[CiProbe]`
>   attribute on each guard's entry point, so deleting a probe file deletes its row. The Context's
>   "walks a registry of about 130 rows" and the Decision's "the registry's dispatch delegates are
>   the only honest answer to what CI runs" describe the shape this ADR's own principle has since
>   been applied to; the attribute is that answer now.

## Context

houseCARL's regression guards have always lived in a bespoke harness: ~155 `*Probe*.cs` files in
`src/housecarl-generator`, run in one process by a `ci-all` verb that walks a registry of about 130
rows and prints `N/N passed`. It was built for a real reason — the Mutagen assembly and the schema
corpus load once instead of once per probe, which turned roughly twelve minutes of cold starts into
under two.

What it costs is visible once the suite is large. A probe is a procedure of `Check("sentence",
condition)` calls, so a failure names a sentence rather than a test, and there is no way to run one
assertion. Fixtures are pasted rather than shared: most probe files carry their own assertion
helper, their own temp directory, and their own synthetic load order, at roughly a dozen lines of
code per assertion. Arms accumulate that cannot fail — an assertion whose predicate is true of any
well-formed response — and nothing detects them, because "the probe passed" is the only signal the
harness produces. Two separate reviews of this project found such arms by hand.

The deciding evidence came from converting one probe as an experiment. Its 1,189 lines became 1,540
lines of xUnit tests carrying 207 named cases. The conversion did not shrink the code — that
expectation was wrong and is recorded here as wrong — but mutation testing against the converted
family found an arm that could not fail in about three minutes, which two rounds of human-style
review had missed; a failure named the single assertion that broke; test isolation surfaced a hidden
ordering dependency and a process-global that the probe form had concealed; and grids of cases became
data-driven theories derived from the product's own lists instead of hand-written repetition.

## Decision

**New guards are written as tests in a standard xUnit project, `src/housecarl-mcp-tests`, run by
`dotnet test`.** It is born in this PR with the first converted family and the guards for the tool
this PR publishes.

The old harness is not rewritten in one pass. During the window both harnesses are required in CI,
and three rules keep the window honest:

1. **New guards go in the test project.** Nothing new is added to the old harness.
2. **Old guards leave only by whole-family conversion**, one family per PR, deleting the probe in the
   same commit that adds its tests. A converted test file carries a `Converted-from:` marker naming
   the probe it replaces, and a guard asserts that the named probe's source file is gone — so a family
   cannot live in both harnesses at once.
3. **A residue countdown measures what is left**, checked into
   `src/housecarl-mcp-tests/harness-residue-baseline.json` and derived from the source tree on every
   run. Three of its numbers gate with exact equality in both directions: the old harness may not
   grow, and a shrink that is not recorded is also a failure, because a baseline sitting above the
   real figure stops being a countdown and becomes headroom. A fourth number, the total line count, is
   derived and printed but not asserted — it moves on ordinary in-place edits to an existing guard,
   which rule 2 requires, so gating it would have made the correct act a failing build.

**The migration is complete when the gated numbers reach zero**, which is what makes the countdown
load-bearing rather than decorative.

Each measure is derived from the surface it claims, never from a naming convention. This is the part
worth stating explicitly, because three earlier shapes of these measures were each written as a proxy
— a filename glob, a regular expression over the registry's source text, and a method-name convention
— and each was short of the real population, the third having been written to fix the first two. The
registry's dispatch delegates are the only honest answer to "what does CI run", and one guard entry
point turned out to live in a different project entirely, where no naming rule inside the harness
could have found it.

A bridge test in the new project runs `ci-all` and fails on a non-zero exit, so `dotnet test` is a
single entry point for a developer running everything.

## Consequences

- **CI runs two required steps** for as long as the window lasts, plus a check that the test step
  actually selected tests: `dotnet test` exits 0 when its filter matches nothing, so a filter edit
  could otherwise remove every guard from CI and leave the run green.
- **A developer's full `dotnet test` pays for `ci-all`** through the bridge, about ninety seconds. CI
  excludes the bridge tier, because it runs `ci-all` as its own step already.
- **A conversion PR has a fixed shape**: delete the probe, add the tests with their
  `Converted-from:` marker, lower the residue numbers in the same commit, and remove the registry row.
- **The countdown will be edited by hand**, deliberately. Lowering it is the visible, argued act that
  records a family leaving; that visibility is the point, and an automatic number would lose it.
- **Tests are tagged by tier** (`unit`, `integration`, `stdio`, `bridge`) so a fast subset can be run
  during development. An untagged test still runs in CI — the filter excludes one tier rather than
  selecting the others, so forgetting a tag cannot silently drop a guard.
- The old harness's remaining performance argument stands for the ~120 schema and generator probes
  that make the corpus expensive. They convert last, or on touch.
