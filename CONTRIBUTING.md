# Contributing to houseCARL

Thanks for your interest — houseCARL accepts issues and pull requests.

## Before you start

For anything beyond a typo fix, **open an issue first** describing the problem or proposal. It lets us confirm direction before you invest in an implementation — and lets us tell you if the gap is already fixed on `main` awaiting release.

## Bug reports

The fastest reports to act on include:

- **houseCARL version** — fixes often land on `main` before a release, so the version tells us whether you're hitting a known-fixed gap.
- **Exact repro** — the tool call(s) you made and the full error or wrong output you got. Driving the server directly over stdio and quoting its stderr is gold, but not required.
- **Environment** where relevant — Skyrim SE version, MO2 or manual install, load-order size.

## Pull requests

- **Fork and branch** — PRs come from a branch on your fork, targeting `main`.
- **One logical change per PR.** Small and reviewable beats broad.
- **Link the issue.** Reference the issue your PR resolves with a closing keyword in the description — `Fixes #123` (or `Closes #123`) — so it closes automatically when the PR merges instead of being left open by hand.
- **Update the changelog.** A user-facing change (new or changed tool behaviour, a bug fix users would notice) adds a line to the `## Unreleased` section of `plugin/CHANGELOG.md` in the **same PR** — so release notes accrue as work lands instead of being reconstructed at the cut. `plugin.json` stays untouched until an actual release.
- **CI must be green** — the `build + probes` check is required to merge. First-time contributors' CI runs wait for maintainer approval; that's a GitHub safety default, not distrust.
- **Linear history** — we merge by rebase only. Keep your branch rebased on current `main`.
- **Bring a test.** A fix comes with a test in `src/housecarl-mcp-tests` that fails before the change and passes after; see `standards/TESTING.md`. Anything that needs real game data runs locally, with what you ran and saw noted in the PR.
- **Stay generic.** Record-type support is generated from Mutagen's model, never hand-written per type, and bulk work composes from a small set of generic verbs, never a one-off tool for one job. A change that special-cases one record type, or hard-codes one job's domain knowledge into a tool, will be asked to generalize (domain knowledge belongs in skills as data).
- **Fail loud.** No silent failure and no silently degraded mode: an unsupported path returns a named error saying what was checked and what to try next — never a wrong answer or a bare miss.
- **Naming** — MCP tools are `housecarl_<snake_case>`; see `standards/NAMING.md`.

## License

houseCARL is GPL-3.0. By submitting a pull request you agree your contribution is licensed under GPL-3.0 like the rest of the project.
