# houseCARL

houseCARL is an MCP server that exposes a Skyrim Special Edition load order at the data layer. It reads a Mod Organizer 2 instance's profile and presents every plugin record, every Data-relative asset, the compiled Papyrus, the SKSE plugin layer and the SkyPatcher INI layer to an AI assistant as a set of tools. It is built on [Mutagen](https://github.com/Mutagen-Modding/Mutagen). The host is [Claude Code](https://claude.com/claude-code) or OpenAI Codex.

A write produces a new plugin in a new MO2 mod folder. Editing an existing plugin in place is an opt-in that names the file. MO2 does not need to be running. No plugin file handle is held between calls.

| | |
|---|---|
| Process | One C#/.NET 9 executable, MCP over stdio |
| Substrate | Mutagen.Bethesda.Skyrim 0.54.4 |
| Tools | 31 |
| Skills | 7 |
| Record coverage | 133 record types, 242 sub-structures, 497 polymorphic arms, 280 enums, generated at build time |
| Licence | GPL-3.0-only |

## Tools

All 31 tools carry the `housecarl_` prefix.

| Substrate | Tools |
|---|---|
| Records | `records` `check` `apply` `create` `remove` `forward` `copy` `write_seq` `create_plugin` `compact_plugin` `merge_plugins` |
| Assets | `asset_status` `place` |
| NIF | `nif_inspect` `nif_set` |
| Papyrus | `compile_script` `decompile_script` |
| BSA | `bsa_list` `bsa_extract` `bsa_repack` |
| SkyPatcher | `skypatcher_layer` |
| SKSE | `skse` |
| Nexus | `nexus_search` `nexus_mod` `nexus_graphql` `nexus_check_updates` `nexus_identify` |
| Session | `set_mo2_instance` `set_tool_path` `load_order_status` `update_status` |

Every operation is one call composed from orthogonal axes: select a set of records, project what to read, apply one of a fixed set of write verbs. There is no verb per job and no single/bulk tool pair. The axes, the predicate language, the write verbs and worked examples are in the [repository README](https://github.com/Avick3110/houseCARL#readme).

## Skills

Seven skills ship with the plugin: `/housecarl:<name>` in Claude Code, `$housecarl` in Codex. Each is a `SKILL.md` and a `references/` tree. Reference corpora are read by grep, never loaded whole.

| Skill | Carries |
|---|---|
| `mutagen-reference` | The schema of every modelled record type: fields, types, cardinality, writability, enum values. Emitted by the same generator pass as the server's rulebook. |
| `papyrus-reference` | Papyrus, SKSE and shipped SKSE-plugin API signatures (PapyrusUtil, JContainers, MCMHelper, po3, SkyUI), 7,345 entries, from [papyrus-index](https://github.com/BellCubeDev/papyrus-index). A function the corpus does not carry is reported as absent. |
| `skypatcher-authoring` | SkyPatcher 6.4.1 INI grammar and the offline check of a draft INI through the overlay. |
| `spid-authoring` | SPID 7.3.0 `_DISTR.ini` grammar. |
| `kid-authoring` | KID 3.5.0 `_KID.ini` grammar. |
| `open-animation-replacer` | OAR 3.0.0 `config.json` / `user.json` conditions, submod priorities, DAR `_conditions.txt` conversion. |
| `skse-plugin-authoring` | Native SKSE plugin DLLs on CommonLibSSE-NG: lifecycle, event sinks, trampoline and Address Library hooks, native Papyrus functions, SE + AE + VR from one DLL. |

Dialogue and facegen are not skills. Their tools carry the bookkeeping: `check findings=["facegen"]` reports the dark, grey and black-face NPC bug one row per NPC, and the dialogue order rules are in [docs/dialogue.md](https://github.com/Avick3110/houseCARL/blob/main/docs/dialogue.md).

## Requirements

- Windows.
- .NET Runtime 9.0 and ASP.NET Core Runtime 9.0, from the [.NET 9 download page](https://dotnet.microsoft.com/download/dotnet/9.0). Both are required. houseCARL ships framework-dependent, and the ASP.NET Core installer does not include the base runtime. The setup utility checks for both and names the one that is missing.
- [Mod Organizer 2](https://www.modorganizer.org/) with a profile. MO2 does not need to be running.
- Claude Code v2.1.143 or newer (the terminal CLI, or the Claude desktop app's Code tab), or OpenAI Codex.

## Install

### From a release

1. Download `houseCARL-<version>.zip` from the [latest release](https://github.com/Avick3110/houseCARL/releases).
2. Extract it and run `houseCARL-Setup.exe`.
3. Select the host: `[1] Claude Code`, `[2] Codex`, `[3] Both`, `[4] Uninstall`.
   - Claude Code: skills to `~/.claude/skills/housecarl/`, server registered in `~/.claude.json`. The CLI and the desktop app both read these.
   - Codex: server under `%LOCALAPPDATA%\houseCARL\server\`, skills flat under `~/.agents/skills/` with a `$housecarl` entry point, server registered as `[mcp_servers.housecarl]` in `~/.codex/config.toml`.
4. Read the plan. Setup prints what it found on the machine and then every path it is about to write, and writes nothing until Enter; `q` quits.
5. Restart the host. The closing block says what was written and what to do next.

Flags, for an unattended run: `--claude` / `--codex` / `--both` pick the host, `--yes` skips the confirm at the plan, `--uninstall` removes instead of installing, `--skip-runtime-check` skips the .NET check. A run whose input is redirected has nobody to answer a question, so it stops and names the flag that answers it rather than assuming one.

Updating: quit Claude Code and Codex first. Setup cannot replace a server a session is running; if one is, it stops and says so. Setup overwrites only the files in the package, so the saved MO2 instance and tool paths in `houseCARL.user.json` survive a re-run.

Uninstalling: `[4] Uninstall`, or `--uninstall` with a host flag. It removes the skill folders it recorded installing, the server, the rest of the files a houseCARL package ships, and the `housecarl` entry in `~/.claude.json` and `~/.codex/config.toml` — each config copied to a `.houseCARL.uninstall.bak` beside it first, and every other byte in it left as it was. Anything under those locations that houseCARL did not install stays where it is, and the run names it. `houseCARL.user.json` sits beside the server and goes with it; the patches houseCARL wrote live in the MO2 mods folder and are left alone.

### From source

Requires the .NET 9 SDK, Windows and PowerShell.

```powershell
git clone https://github.com/Avick3110/houseCARL.git
cd houseCARL
./scripts/build-plugin.ps1
```

The script regenerates the rulebook, publishes the server framework-dependent with trimming off, bundles the skills, builds the setup utility, and packs `release/houseCARL-<version>.zip`. Install the output with `houseCARL-Setup.exe`, with `claude --plugin-dir ./dist/housecarl`, or through the bundled local marketplace descriptor. The `--plugin-dir` and marketplace routes read the plugin manifest: builds before v2.1.143 ignore the plugin's `displayName`, and loading a zipped plugin with `--plugin-dir ./x.zip` needs v2.1.128 or newer.

## Pointing it at a modlist

On first use, houseCARL asks for the MO2 instance folder, the one containing `ModOrganizer.ini`. On the `--plugin-dir` and marketplace routes the folder is a required plugin setting instead, so Claude Code shows a folder picker when the plugin is enabled and the server boots already pointed at it. Everything else follows from the folder: the mods folder, the active profile, and the load order. The instance can be changed at any time by asking.

```
> point houseCARL at D:\Modding\ARR

> which plugins override Hulda's NPC record, and what does each change

> drop iron sword damage to 12 in every mod that touches it, into a patch called IronRebalance
```

## Writing

Three lanes, mutually exclusive. `patch=` is the default: a new mod folder `houseCARL - <name>` holding one plugin, with existing files untouched. `into=` extends an existing houseCARL patch, and touches that patch only. `in_place=` overwrites the named plugin, including one houseCARL did not author; it keeps no backup and has no undo, and the first in-place write to a given plugin returns a confirmation instead of writing.

Every op is checked before any file is opened for writing: record type, field path, enum value, verb against cardinality, value range, writability, record identity, the target type of every FormLink. One failing op refuses the whole call. `dry_run=true` runs the full pipeline and stops before disk.

Each write lands as its own MO2 mod folder. Refresh MO2, review the plugin, sort it, enable it. houseCARL does none of those.

## Licence

GPL-3.0-only. See [LICENSE](LICENSE). Required by Mutagen (GPL-3.0-only, no linking exception), which houseCARL is built on and bundles. Third-party components and licences are listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) with corresponding-source pointers.

## Credits

- [Mutagen](https://github.com/Mutagen-Modding/Mutagen), Noggog. The Bethesda-format library houseCARL is built on.
- [papyrus-index](https://github.com/BellCubeDev/papyrus-index), BellCube. Source corpus for `papyrus-reference`.
- Zzyxzz (SkyPatcher) and powerofthree (SPID, KID). Public documentation the distributor grammars were drawn from.
- DrHeisen. The `open-animation-replacer` skill, and two earlier skills since retired.

The grammar, the predicate language, coverage, the runtime layers and the internals are in the [repository README](https://github.com/Avick3110/houseCARL#readme).
