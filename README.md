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

## Design

Four rules. The surface follows from them.

1. **Record coverage is generated.** A build-time generator reflects over Mutagen's record interfaces and emits the schema and validation data. The set of record types houseCARL handles is the set Mutagen models. Where Mutagen lags xEdit, the tools report the gap.
2. **One grammar, closed under composition.** Every operation is one call composed from orthogonal axes: select a set of records, project what to read, apply one of a fixed set of write verbs. There is no verb per job and no single/bulk tool pair. One record is a set of one.
3. **Errors are one sentence: what went wrong and what to try.** A tool does not return a wrong answer or a degraded answer without stating so. An unknown field, an illegal verb, an illegal enum value, or a FormLink at a record of the wrong type is refused by name before any file is opened for writing.
4. **Reads are lazy; freshness is a cheap check.** Records parse on access from a binary overlay. The load order is not held in memory. A change on disk is detected by a last-write-plus-size check on the next call.

## The tool surface

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

`records` is the read surface for the record plane. A read is composed from four axes:

| Axis | Decides | Values |
|---|---|---|
| SELECT | which records | `formids` `types` `plugins` (with `defined_in`) `conflicts_only` `where` `references` `walk` |
| SOURCE | whose version | the winner (default), a plugin filename active or not, a `{file, mod}` pair, the SkyPatcher overlay pre or post replay; `versus` names a second pole for `delta` and `tree` |
| PROJECT | the shape of the answer | `identity` `summary` `fields` `rows` `everything` `aggregate` `delta` `tree` `chain` `info_order` |
| TRANSPORT | the rendering | `format` (text, json, dense) `limit` `offset` `max_chars` `counts_only` `to_file` |

A write is composed from the op list, the lane and the transport:

| Axis | Values |
|---|---|
| Verb | `Set` `Add` `Remove` `SetAtIndex` `InsertAtIndex` `ReplaceAll` `Merge` `CopyFrom` |
| Lane | `patch=` a new plugin (default), `into=` an existing houseCARL patch, `in_place=` a named plugin overwritten, `dry_run=` |
| Transport | `readback` `format` `max_chars` |

One read. Every WEAP Requiem.esp touches whose damage, as Requiem.esp sets it, is 50 or more, reading two fields from that plugin. `where_source="winner"` and `fields_source="winner"` ask the same question of the load-order winner.

```jsonc
housecarl_records(
  plugins = {"names": ["Requiem.esp"]},
  types   = ["WEAP"],
  where   = ["BasicStats.Damage >= 50"],
  project = {"form": "fields", "fields": ["BasicStats.Damage", "Keywords"]})
```

One write. Two ops on one record, validated and stopped before disk.

```jsonc
housecarl_apply(
  ops = [
    {"formid": "013989:Skyrim.esm", "field_path": "BasicStats.Damage", "op": "Set", "value": "12"},
    {"formid": "013989:Skyrim.esm", "field_path": "Keywords", "op": "Add", "value": "0A8668:Skyrim.esm"}],
  patch   = "IronSwordRebalance",
  dry_run = true)
```

`dry_run` runs the full pipeline (winner resolution, schema pre-flight, every op applied in memory, the reference check) and stops before disk. It returns what would change, or the refusal the real call would return.

**Predicates.** `where=` accepts comparisons (`BasicStats.Damage >= 50`), EditorID tests (`editorid startswith REQ_`), flag tests over bit fields (`BodyTemplate.FirstPersonFlags has Body`, `has_any`, `has_none`), presence (`VirtualMachineAdapter exists`), membership from a file (`formid not in @<path>`), one link step (`Perks->editorid startswith REQ_NULL_`), a provenance term (`winner = X.esp`), a containment step (`*parent.EditorID`), and quantified steps over a list (`Effects[*none].BaseEffect->editorid startswith REQ_`, `Effects[*count] > 2`). Predicates are ANDed.

**FormIDs.** A record is addressed as `XXXXXX:Plugin.esp`, or in the runtime form the console, Papyrus log and crash log print (`FExxxYYY`, `XX######`), resolved against the current order. Reads accept both forms. Writes accept the plugin form only; a runtime form is refused with the plugin form to use.

**Large results.** Every response on the record plane carries an `epoch` stamp identifying the index build it was answered from. A result over the render limit is written in full to a JSONL file whose first line is a manifest, and the response names the file. The file re-enters a later call as `formids=["@<path>"]`, epoch-checked.

**Findings.** `check` runs derived-findings families over the order: `errors` (dangling FormLinks, missing masters, unparseable records), `scripts` (script properties the VMAD does not bind), `dialogue` (dialogue graph validation over seeded topics and quests — this family takes seeds, it does not sweep), `facegen` (per NPC: the mod winning the head `.nif`, the mod winning the face `.dds`, the plugin winning the record, the mismatch class). Each family's description states what it does not cover.

## Coverage

The schema is generated from Mutagen.Bethesda.Skyrim 0.54.4 by reflection at build time: 1,174 types, at full field depth, with per-field type, cardinality, writability, nullability and xEdit signature. The `mutagen-reference` skill carries the same generated data for offline lookup.

A record type Mutagen does not model is absent from the reference and from the tools. A request for it is refused by name as a library coverage gap. The server is published with trimming off: trimming a reflection-driven server strips types and loses coverage without an error.

Record identity is not writable. `FormKey` on a record, and `ModKey` and `Master` on the mod header and master references, are refused by the write pre-flight and reported as not writable by the reference.

## Runtime layers

Four layers below the plugin plane decide what the game runs. houseCARL reads all four.

- **The MO2 virtual file system.** Which copy of a Data-relative path wins: the overwrite folder, a mod folder, game Data, or a BSA, with the conflict chain. `place` writes a winning override into a new mod folder and reports that the file is written and not yet winning.
- **NIF internals.** Shape names, embedded skin and FaceTint texture paths, flags, alpha, partitions, read from the winning copy. A whitelisted subset is written back with readback verification.
- **The SKSE plugin layer.** Every DLL and configuration file under `Data\SKSE\Plugins`, resolved to its winning mod, with each DLL's declared version metadata read without loading it. Two audits: native Papyrus functions declared by scripts against the DLLs that must implement them; form references declared in SKSE configuration files against the load order.
- **The SkyPatcher INI layer.** Every INI in apply order, with VFS shadows, INI-versus-INI conflicts and no-op writes; or one record's state after the layer has replayed over it. A draft INI not yet in a mod folder can be included in the replay.

The SKSE and SkyPatcher tools report what a file declares. They do not observe the running game.

Two independent precedences decide an NPC's face: the VFS for the baked files, the load order for the record. `check findings=["facegen"]` joins them. Causes and fixes: [docs/facegen.md](docs/facegen.md).

## Writing

Three lanes, mutually exclusive.

| Lane | Parameter | Target | Existing files |
|---|---|---|---|
| New patch (default) | `patch=` | A new mod folder `houseCARL - <name>` holding one plugin whose masters span every referenced plugin | Untouched |
| Extend | `into=` | An existing houseCARL patch; a record already in it is edited, one not in it is copied in from the winner first | The named patch only |
| In place | `in_place=` | The named plugin, including one houseCARL did not author; re-laid out on save as xEdit and the CK do | The named plugin is overwritten |

A patch name already taken by an earlier houseCARL patch is suffixed. A name matching a plugin on disk that the order is not loading is refused, naming the place and the file.

The in-place lane keeps no backup and has no undo. The first in-place write to a given plugin returns a confirmation instead of writing; the call is repeated with `acknowledge=true`. The acknowledgement covers the overwrite of that plugin only. The pre-flight and record verify run on every call.

**Pre-flight.** Every op is checked before any file is opened for writing: record type, field path, enum value, verb against cardinality, value range, writability, record identity, the target type of every FormLink. Each failing op is named and counted. One failing op refuses the whole call.

**Readback.** Each edited field is read back from the written file. `readback=true` deep-reads every touched record. The readback reports the file's content. The patch has no effect until it is enabled and sorted in MO2.

**Beyond field edits.** Create records with fresh FormIDs, including nested dialogue structures. Remove records and list entries. Forward a named plugin's version of a record as a winning override, or revert a record to vanilla. Copy an NPC's appearance closure into a standalone record with no dependency on the donor. Create an empty plugin. ESL-compact a plugin, carrying its FormID-keyed FaceGen and voice files. Merge plugins with collision-only renumbering. Unused masters are trimmed on every write.

Dialogue records have bookkeeping that a byte-valid insert does not satisfy: [docs/dialogue.md](docs/dialogue.md).

## Skills

Seven skills ship with the plugin: `/housecarl:<name>` in Claude Code, `$housecarl` in Codex. Each is a `SKILL.md` and a `references/` tree. Reference corpora are read by grep, never loaded whole. Four of the seven — `mutagen-reference`, `papyrus-reference`, `spid-authoring`, `kid-authoring` — carry a `references/index.jsonl` mapping a name to the file that holds it; the two generated corpora carry the line as well.

| Skill | Carries |
|---|---|
| `mutagen-reference` | The schema of every modelled record type: fields, types, cardinality, writability, enum values. Emitted by the same generator pass as the server's rulebook. |
| `papyrus-reference` | Papyrus, SKSE and shipped SKSE-plugin API signatures (PapyrusUtil, JContainers, MCMHelper, po3, SkyUI), 7,345 entries, from [papyrus-index](https://github.com/BellCubeDev/papyrus-index). A function the corpus does not carry is reported as absent. |
| `skypatcher-authoring` | SkyPatcher 6.4.1 INI grammar and the offline check of a draft INI through the overlay. |
| `spid-authoring` | SPID 7.3.0 `_DISTR.ini` grammar. |
| `kid-authoring` | KID 3.5.0 `_KID.ini` grammar. |
| `open-animation-replacer` | OAR 3.0.0 `config.json` / `user.json` conditions, submod priorities, DAR `_conditions.txt` conversion. |
| `skse-plugin-authoring` | Native SKSE plugin DLLs on CommonLibSSE-NG: lifecycle, event sinks, trampoline and Address Library hooks, native Papyrus functions, SE + AE + VR from one DLL. |

Dialogue and facegen are not skills. Their tools carry the bookkeeping, and the measured facts are in [docs/dialogue.md](docs/dialogue.md) and [docs/facegen.md](docs/facegen.md), cited from the tool descriptions.

## Requirements

- Windows.
- .NET Runtime 9.0 and ASP.NET Core Runtime 9.0, from the [.NET 9 download page](https://dotnet.microsoft.com/download/dotnet/9.0). Both are required. The ASP.NET Core installer does not include the base runtime. The setup utility checks for both and names the one that is missing.
- [Mod Organizer 2](https://www.modorganizer.org/) with a profile. MO2 does not need to be running.
- Claude Code v2.1.143 or newer (the terminal CLI, or the Claude desktop app's Code tab), or OpenAI Codex. houseCARL runs inside the host.

## Install

### From a release

1. Download `houseCARL-<version>.zip` from the [latest release](https://github.com/Avick3110/houseCARL/releases).
2. Extract it and run `houseCARL-Setup.exe`.
3. Select the host: `[1] Claude Code`, `[2] Codex`, `[3] Both`, `[4] Uninstall`.
   - Claude Code: skills to `~/.claude/skills/housecarl/`, server registered in `~/.claude.json`. The CLI and the desktop app both read these.
   - Codex: server under `%LOCALAPPDATA%\houseCARL\server\`, skills flat under `~/.agents/skills/` with a `$housecarl` entry point, server registered as `[mcp_servers.housecarl]` in `~/.codex/config.toml`.
4. Read the plan. Setup prints what it found on the machine and then every path it is about to write, and writes nothing until Enter; `q` quits.
5. Restart the host. The closing block says what was written and what to do next. On first use, houseCARL asks for the MO2 instance folder, the one containing `ModOrganizer.ini`. The instance can be changed at any time by asking.

Flags, for an unattended run: `--claude` / `--codex` / `--both` pick the host, `--yes` skips the confirm at the plan, `--uninstall` removes instead of installing, `--skip-runtime-check` skips the .NET check. A run whose input is redirected has nobody to answer a question, so it stops and names the flag that answers it rather than assuming one.

Updating: quit Claude Code and Codex first. Setup cannot replace a server a session is running; if one is, it stops and says so.

Uninstalling: `[4] Uninstall`, or `--uninstall` with a host flag. It removes the skill folders it recorded installing, the server, the rest of the files a houseCARL package ships, and the `housecarl` entry in `~/.claude.json` and `~/.codex/config.toml` — each config copied to a `.houseCARL.uninstall.bak` beside it first, and every other byte in it left as it was. Anything under those locations that houseCARL did not install stays where it is, and the run names it. The saved MO2 instance sits beside the server and goes with it; the patches houseCARL wrote live in the MO2 mods folder and are left alone. When it is over, the run lists what it actually took off.

### From source

Requires the .NET 9 SDK, Windows and PowerShell.

```powershell
git clone https://github.com/Avick3110/houseCARL.git
cd houseCARL
./scripts/build-plugin.ps1
```

The script regenerates the rulebook, publishes the server framework-dependent with trimming off, bundles the skills, builds the setup utility, and packs `release/houseCARL-<version>.zip`. Install the output with `houseCARL-Setup.exe`, with `claude --plugin-dir ./dist/housecarl`, or through the bundled local marketplace descriptor. The script header has the details.

## Usage

```
> point houseCARL at D:\Modding\ARR

> which plugins override Hulda's NPC record, and what does each change
  records: formids=[<Hulda>], project={"form": "tree"}

> which line answers Hulda's greeting, and which plugin moved it
  records: types=["DIAL"], where=["Quest = <quest>"], project={"form": "info_order"}

> set the iron sword's damage to 12, into a patch called IronRebalance
  records: formids=["013989:Skyrim.esm"], project={"form": "fields", "fields": ["BasicStats.Damage"]}
  apply:   ops=[{"formid": "013989:Skyrim.esm", "field_path": "BasicStats.Damage", "op": "Set", "value": "12"}],
           patch="IronRebalance", dry_run=true
  apply:   the same call without dry_run

> every weapon in the order whose winning damage is 50 or more, as a table
  records: types=["WEAP"], where=["BasicStats.Damage >= 50"],
           project={"form": "fields", "fields": ["BasicStats.Damage"]}, format="dense"
```

A write resolves the record's load-order winner and overrides it into the patch. The edit is made once, on that record; which plugins touched it before is what `project={"form": "tree"}` shows, not something the write repeats per plugin.

Each write lands as its own MO2 mod folder, `houseCARL - <patch>`. Refresh MO2, review the plugin, sort it, enable it. houseCARL does none of those.

## How it works

One process, MCP over stdio. The load order is read from the MO2 profile's `loadorder.txt`, `modlist.txt` and `plugins.txt`. No USVFS, no hook into MO2. Records parse on access from Mutagen's lazy binary overlay. A structural index (FormKey to winning plugin and override count, plus the touching-plugin list for contested records) is built by opening each plugin once and disposing it; measured at 125–185 MB on a 3,400-plugin order. A call that needs record bodies opens each plugin at most once for the call. No plugin handle is held between calls, so MO2, xEdit and Explorer operate on the plugins normally while houseCARL runs.

Freshness is a last-write-plus-size check on every call. A rebuild is an immutable snapshot swapped in as one reference. A plugin the build cannot read is excluded whole with the reason, and every response from that build says which plugins are missing.

Writes are verified by a per-kind oracle: 17 cells, each performing one mutation two ways — through the reflection engine, and through a hand-written typed Mutagen setter — with the two output plugins required to be byte-identical. The engine is blind to which record it edits, so proving a kind proves every record carrying that kind. The oracle is run by hand against a real `Skyrim.esm`. 134 probe harnesses run in CI against real plugins.

The only outbound network use is the Nexus lookups. They need no account or API key. Offline, they say so, and every local tool keeps working. `update_status` reads MO2's local cache first and checks at the exact-file level.

`decompile_script` reconstructs `.psc` from `.pex` without an external tool. Measured on a 3,400-plugin order: 98.80% of 10,189 provable script pairs decompile and recompile byte-exact. A script it cannot prove is reported as such in the output.

## Development

| Path | What |
|---|---|
| `src/housecarl-mcp/` | The MCP server and tool surface |
| `src/housecarl-core/` | Record, asset, read and write engines; the load-order resolver |
| `src/housecarl-generator/` | Build-time schema generator; the probe runner |
| `src/housecarl-mcp-tests/` | xUnit tests against the built server |
| `src/housecarl-setup/` | Installer |
| `plugin/` | Plugin manifest, changelog, notices; skills are copied in at build |
| `.claude/skills/` | Skill sources |
| `docs/` | Architecture notes and decision records |
| `standards/` | Testing and naming |

```powershell
dotnet build housecarl.sln -c Release
dotnet test src/housecarl-mcp-tests -c Release --no-build --filter "tier!=bridge"
dotnet src/housecarl-generator/bin/Release/net9.0/housecarl-generator.dll ci-all
```

Read [CLAUDE.md](CLAUDE.md) before changing anything, then the note in `docs/architecture/` for the subsystem. Decisions are recorded one per file in `docs/decisions/`. Contribution process: [CONTRIBUTING.md](CONTRIBUTING.md).

## Upgrading from 1.x

2.0.0 replaces the 1.x tool surface with 31 tools built from one grammar. There is no alias layer and no deprecation window. A 1.x tool name is refused with one sentence naming its successor and the call shape to use. The rows those sentences come from are in `src/housecarl-mcp/AliasTable.cs`.

## Licence

GPL-3.0-only. See [LICENSE](LICENSE). Required by Mutagen (GPL-3.0-only, no linking exception), which houseCARL is built on and bundles. Third-party components and licences are listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) with corresponding-source pointers.

## Credits

- [Mutagen](https://github.com/Mutagen-Modding/Mutagen), Noggog. The Bethesda-format library houseCARL is built on.
- [papyrus-index](https://github.com/BellCubeDev/papyrus-index), BellCube. Source corpus for `papyrus-reference`.
- Zzyxzz (SkyPatcher) and powerofthree (SPID, KID). Public documentation the distributor grammars were drawn from.
- DrHeisen. The `open-animation-replacer` skill, and two earlier skills since retired.
