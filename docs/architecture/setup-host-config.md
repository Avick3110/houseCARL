---
updated: 2026-11-25
covers: [src/housecarl-setup/HostConfig.cs, src/housecarl-setup/TomlFile.cs]
---
# How setup edits a host's config

## What it is
houseCARL-Setup keeps one entry in `~/.claude.json` and one table in `~/.codex/config.toml`. Both registrations go
through `HostConfig`, which finds the entry, sets only setup's keys on it, and writes nothing when that changes nothing.

## Contracts
- Setup's keys are `type` and `command` on the Claude entry and `command` in the Codex table; `args: []` goes only into
  an entry setup creates. Every other key is the user's or the host's and stays. No list of those keys is kept.
- An entry is setup's when its command's file name is `housecarl-mcp.exe`, wherever the file sits. Any other entry (a
  wrapper, a `url`, a `command` that is not one single-line string) is replaced whole and the run says so.
- In `config.toml` only the bytes of the `command` value change. The table is found however its header is spelled; a line
  inside a multi-line value is never a header or a key, unless the file leaves a bracket or triple quote open.
- In `~/.claude.json` only the `mcpServers` object is rewritten, and only when setup's keys on the entry change.
- A copy is taken only before a change. The first name that is free or already holds the same bytes is the copy
  (`.houseCARL.bak`, `.houseCARL.2.bak`, ...); one holding other bytes is never written over. A removal does the same
  under `.houseCARL.uninstall.bak`.
- The plan and the summary print the registration's own answer: registered, already registered, or replaced; the kept
  settings by key name, never a value; the copy's real name; and, for a kept `enabled = false`, that houseCARL is
  still turned off in Codex.
- A removal takes the whole entry, its settings with it.

## Known limits
- The settings of an entry that is replaced whole are not kept; the copy holds them.
- A kept `enabled_tools` or `disabled_tools` that names 1.x tools is kept as it is.
- A Codex entry with no `[mcp_servers.housecarl]` header (an inline or dotted table) is not found, so a second one is
  added (#1449).

## Pinned by
- `SetupSettingsTests` — the kept keys, the bytes that change, the entries replaced whole, and the copies.
- `SetupRunTextTests` — what the built exe says in the plan and the summary.
- `SetupUninstallTests` — what a removal takes and leaves.

## Where
`src/housecarl-setup/HostConfig.cs` (the registrations, the copy step), `TomlFile.cs` (the table finder), called from
`Program.cs`, `Plan.cs` and `Uninstall.cs`.
