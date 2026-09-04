# houseCARL

houseCARL is an MCP server that gives Claude direct, data-layer access to a Skyrim Special Edition load order through a live Mod Organizer 2 instance: every plugin record, every Papyrus script, every asset path, the runtime layers, and the modlist state. The modder works in plain English in Claude Code; Claude does the mechanical work and writes results into a new plugin the modder reviews and enables. Editing an existing plugin in place is an opt-in with per-plugin consent.

It is one C# process built on Mutagen. Users start at [README.md](README.md). This file is for whoever is changing the code.

> `dev/` is a private working corpus (product PRFAQ, tool-surface spec, backlog, decisions). It is gitignored, so links into it do not resolve in a clone.

## Design

- **Record coverage is generated, never hand-written.** A build-time generator walks Mutagen's record interfaces and emits the schema and validation data. The set of record types houseCARL handles is the set Mutagen models. Do not add a per-record-type schema or write mapping by hand. Where Mutagen lags xEdit, say so loudly; never paper over it.
- **One grammar, closed under composition.** Every operation, single or bulk, is one call composed from a small set of orthogonal axes: select a set of records (one is a set of one), project what to read, apply one of a few generic write verbs. There is no verb per job and no single/bulk tool pair (the one left from 1.x, `place_asset` / `bulk_place_asset`, is a leftover to fold, not a pattern). A new need lands as a value on an existing axis; if it truly cannot, add one general primitive, never a job-shaped tool. The surface contract is `dev/projects/tool-surface-2.0/SPEC.md`; look the verb set up there rather than reciting it. Domain knowledge (field bundles, forbidden prefixes) lives in skills as data.
- **Errors are one plain sentence: what went wrong and what to try.** A tool never returns a silently wrong answer or a silently degraded mode. When it cannot do the thing, it says so.
- **Reads are lazy, freshness is cheap.** Records parse on access from a binary overlay; nothing holds the load order in memory or keeps plugin file handles open at rest; a change on disk is picked up by an mtime check. One process, no daemon, no live tracking of MO2.

A design question that these four do not settle goes to the PRFAQ (`dev/PRFAQ/`, the problem statement and press release first) and then to Aaron. If something blocks you, say so before working around it.

## How to work

1. **Branch first.** `main` is read-only. Work in a worktree at `.claude/worktrees/<name>` on branch `claude/<name>`. Check `git branch --show-current` before the first edit.
2. **Build and test.**
   ```
   dotnet build housecarl.sln -c Release
   dotnet test src/housecarl-mcp-tests -c Release --no-build --filter "tier!=bridge"
   dotnet src/housecarl-generator/bin/Release/net9.0/housecarl-generator.dll ci-all
   ```
   Tests drive the built server. How to write one: [standards/TESTING.md](standards/TESTING.md).
3. **Commit small.** One change per commit, plain imperative subject under 72 characters.
4. **Open the PR.** A paragraph saying what changed and why, `Closes #N` for the issue, and a line under `## Unreleased` in `plugin/CHANGELOG.md` if a user would notice the change. Run one review pass (`/code-review`) and fix what is real. Aaron reviews and merges on his word with `gh pr merge <N> --rebase --delete-branch`; then remove the worktree.
5. **Write plainly.** Comments are one line saying what. Commit messages, PR bodies, and issues use ordinary words: bug, test, review, fix. No project jargon, no lore.

## Where things live

| Path | What |
|---|---|
| `src/housecarl-mcp/` | The MCP server and tool surface. `LoadOrderService.cs` here holds most of the load-order logic |
| `src/housecarl-core/` | Record, asset, read, and write engines; the load-order resolver |
| `src/housecarl-generator/` | Build-time schema generator; also the probe runner (`ci-all`) |
| `src/housecarl-mcp-tests/` | xUnit tests against the built server |
| `src/housecarl-setup/` | Installer |
| `plugin/` | The shipped Claude Code plugin's manifest, changelog, and notices; skills are copied in at build |
| `.claude/skills/` | Skill sources (`/housecarl:<name>`) |
| `docs/` | Architecture notes and design decisions |
| `standards/` | Testing, naming, and skill authoring |
| `dev/projects/tool-surface-2.0/` | The tool-surface charter and spec (private) |
| `dev/BACKLOG.md` | What is next, in order (private) |
| `dev/DECISIONS.md` | Aaron's rulings, one line each (private) |

MCP tools are named `housecarl_<snake_case>`; namespaces, classes, and files are named for what they do, not for the brand. Everything else: [standards/NAMING.md](standards/NAMING.md).

## Don't

- Don't work on `main`.
- Don't hand-write coverage for a record type, or add a tool for one job.
- Don't add a guard, sweep, or process rule to catch a mistake. Fix the mistake; if it recurs, fix the code that allows it.
- Don't work around a block silently. Say what blocks you.
- Don't add a new domain to `LoadOrderService.cs` (already 9,000 lines). A new subsystem gets its own file.
- Don't edit `dev/PRFAQ/` or `Housecarl [Legacy]/`. Both are frozen reference.
