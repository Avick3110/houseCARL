# Changelog

All notable changes to houseCARL are documented here. Versioning is [semantic](https://semver.org);
the `version` in `.claude-plugin/plugin.json` is bumped on each release, so installed users update only
when it changes.

## 1.0.1 — 2026-06-05

- **Descendable reads:** `housecarl_read_record` / `housecarl_batch_record_detail` gain a `depth`
  parameter. `depth=1` (default) is unchanged; `depth>=2` enumerates the contents of lists,
  dictionaries, and sub-structs — each element shown with its index and an identity (e.g.
  `VirtualMachineAdapter.Scripts[0].Properties[5] = [ScriptObjectProperty] Name=...`) — so nested
  elements and their indices are visible in one call instead of probing each `[i]` by hand.
- **Bracket-grammar discoverability:** reading a collection with a dot-index (e.g. `Aliases.0`) now
  returns an actionable hint to use brackets (`Aliases[0]`); bracket indexing is documented in the
  read/write tool descriptions.

## 1.0.0 — 2026-06-03

Initial release.

- Local MCP server (stdio) with [Mutagen](https://github.com/Mutagen-Modding/Mutagen) kept warm in
  memory. Claude Code launches it — no port, no window, no manual start.
- **Reads:** the true load-order winner for any record, plus the full conflict tree on request; batch
  record detail; cross-plugin queries.
- **Writes:** set / add / remove fields, leveled-list and container edits, condition-target
  re-targeting — emitted as a **new** MO2 mod folder (`houseCARL - <name>`), originals untouched.
  Create brand-new records; remove records and individual entries; unused masters cleaned automatically.
- **Reflection-driven coverage:** every record type Mutagen models is readable and writable by
  construction — not a hand-maintained subset.
- **MO2 integration:** the instance is chosen via a folder picker at enable time; the active profile and
  load order are read statically from the instance's profile files and refresh automatically on the next
  tool call (MO2 need not be running).
- **Bundled skills:** `mutagen-reference` (record schemas), `papyrus-reference` (Papyrus + SKSE
  signatures), `skypatcher-authoring`, `spid-authoring`, and `kid-authoring`.
