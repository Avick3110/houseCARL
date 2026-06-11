# Changelog

All notable changes to houseCARL are documented here. Versioning is [semantic](https://semver.org);
the `version` in `.claude-plugin/plugin.json` is bumped on each release, so installed users update only
when it changes.

## 1.2.3 — 2026-06-11

Opens the script-property write surface, adds a write-and-verify loop, and hardens tool-argument handling
and setup — carrying houseCARL's first outside code contributions (thanks, **WraithFallen**). No change to
the tool set.

- **Script-property (VMAD) writes work** *(WraithFallen — #35/#38)*: paths and composes that go through a
  polymorphic field's arms — setting a script property's target object, editing a quest alias's script
  fields, adding a `ScriptObjectProperty` to a script's property list — now validate against the arm's own
  schema and write end-to-end. Before, the write pre-flight couldn't see fields that exist only on one arm
  of a polymorphic type, so those edits were rejected; reads already worked. The surface is generic over
  every polymorphic family — no per-type wiring — and the cases an arm can't support still fail with a
  named reason.
- **Malformed tool arguments coerce or fail by name** *(#36, string-encoded-array case by WraithFallen)*:
  Claude Code's client sometimes sends an array argument as a plain string or as a JSON-array-in-a-string
  (`"[\"A.esp\"]"`); both shapes now bind as the array they spell. A missing required argument refuses by
  name, an uncoercible shape fails with a named reason, and an unexpected error inside any tool now returns
  a named error — never the SDK's generic text.
- **Write-and-verify in one step:** the write tools gain an opt-in `full_readback=` that returns every
  record the write touched or created — in full, read back off the *written file* — so a patch can be
  verified before it's ever enabled in MO2.
- **Honest answer for a plugin that isn't in the load order:** a `plugin=` read naming a plugin that isn't
  in the current order now gets its own named error saying exactly that, instead of the false "does not
  define this record".
- **Consistent answers while the load order changes:** each logical operation now reads from one captured
  index snapshot, so an MO2 change landing mid-query can no longer tear a result (winner, touching list,
  and counters always come from the same view).
- **Setup is self-contained and pre-flights the runtimes:** `houseCARL-Setup.exe` no longer needs .NET
  installed to run, and it checks for *both* required .NET runtimes up front with a specific fix message.
  The install docs are corrected accordingly — installing the ASP.NET Core Runtime does **not** include the
  base .NET runtime.
- **Authoring skills load for reading, not just writing:** the SkyPatcher / SPID / KID skills now also fire
  when *interpreting or auditing* an existing INI — "what does this `_DISTR.ini` do", "is this NPC
  affected", "why isn't this line applying" — so those answers come from the bundled grammar references
  instead of memory.

## 1.2.2 — 2026-06-10

Fixes four issues surfaced auditing a Requiem load order — all in reads and writes, no change to the tool
set.

- **New records get valid FormIDs:** creating a record in a patch that was first written by a bulk apply
  could allocate FormIDs starting at `000000` — the null range the game and other tools reject. houseCARL
  now floors every new-record allocation at `0x800` (the user range Bethesda reserves) from every write
  path, and persists a floored high-water mark into the patch so later edits and removals never regress it.
  Patches that already carry a `000000` record stay readable and editable — nothing is auto-renumbered,
  which would break references.
- **Conflict diffs compare real content:** the conflict tree's "what differs between these overrides"
  comparison looked only at top-level field counts, so two overrides that changed the *contents* of a list
  or struct without changing its length could be reported as identical. The diff now walks the record's
  full depth — list elements compared order-insensitively, sub-structs and nested values compared by value
  — so a genuine deep difference is no longer missed, and the output stays honest when a record is too
  large to fully expand.
- **One unreadable record no longer breaks a whole query:** a single record Mutagen can't parse — for
  example a malformed perk an upstream ESP ships — used to abort an *entire* `housecarl_cross_plugin_query`
  that scans references, returning nothing. houseCARL now isolates the offending record, scans past it, and
  reports how many records were skipped and why, so the rest of the results come through.
- **Form-targeted conditions read correctly:** a condition that points at a form — `HasPerk`,
  `HasMagicEffect`, `GetInFaction`, and the like — used to render a placeholder instead of the form's
  FormID when read. houseCARL now resolves the target through its link, so those condition payloads show
  their real FormID.

## 1.2.1 — 2026-06-08

Adds value-based record querying and a fuller Nexus lookup, and fixes several read and write rough edges.

- **Query by field value:** `housecarl_cross_plugin_query` gains a `where=` filter that matches records
  by a field's *value*, not just by record type or plugin — e.g. `where="MagicSkill = Destruction"` or
  `where="BasicStats.Damage >= 50"`. Operators are `=`, `!=`, `>`, `>=`, `<`, `<=`, and `contains`, and
  multiple `where=` conditions are ANDed. It works on any field you can read (by construction — the
  filterable set is the readable set); a path that can't resolve fails loud rather than silently matching
  nothing.
- **Full Nexus descriptions:** `housecarl_nexus_mod` takes an opt-in `description=true` that returns a
  mod's full Nexus page write-up — cleaned from the page markup to plain text — instead of only the short
  catalogue summary.
- **Write into an active patch:** writing into a patch that is itself active in the load order no longer
  fails with a file-lock error — houseCARL releases every mapped handle on the target before it saves.
- **Deep reads of condition-bearing records:** a deep read (`depth` 5+) of a record that carries
  conditions — a perk, spell, or magic effect — no longer floods the output with .NET reflection
  internals; the descent now stops at the modeled record content, so the real values stay visible.
- **Scoped queries show the scoped record:** under a `plugins=` scope, `housecarl_cross_plugin_query`
  now renders each match from that plugin's own record body rather than the global load-order winner.

## 1.2.0 — 2026-06-07

houseCARL reads Nexus Mods directly, and gains a community-contributed Papyrus performance reviewer.

- **Nexus Mods lookups:** two keyless, read-only tools — `housecarl_nexus_search` (search the Skyrim SE
  catalogue) and `housecarl_nexus_mod` (one mod's version, requirements, and *true* latest release — its
  newest MAIN file, since a mod's own version header can lag) — answer Nexus questions directly through
  the public Nexus catalogue API: no browser, no
  account, no API key. Read-only — houseCARL finds and informs; downloading stays your mod manager's
  "Mod Manager Download" handoff. Offline-tolerant: with no connection it says so plainly and every
  local capability keeps working.
- **`papyrus-optimization` skill:** a bundled Papyrus performance reviewer — classify each part of a
  `.psc` as broken / suboptimal / clean, explain what makes it heavy, and give the fix (event-driven,
  caching, states, native offload). houseCARL's first community-contributed skill, by DrHeisen.

## 1.1.3 — 2026-06-07

Hardens houseCARL against a malformed plugin that could otherwise make every command fail.

- **Resilient load-order indexing:** a single record Mutagen can't parse — for example a malformed package
  data-input count that an upstream ESP ships and the game engine ignores — used to make *every* houseCARL
  command fail with a Mutagen error, because the whole-load-order index is built up front and one bad record
  threw the entire build. houseCARL now isolates the offending plugin: it is excluded from the session and
  reported in `housecarl_load_order_status` (with the reason why), while every other plugin stays fully
  readable. Fix or remove the upstream plugin to restore access to it.

## 1.1.2 — 2026-06-06

Fixes a silent lookup failure in the Papyrus reference skill, and ships the corrected third-party credits.

- **Papyrus reference lookup fix:** the `papyrus-reference` skill documented its function-index grep with a
  format that no longer matched the shipped index, so a lookup written from the docs matched zero lines even
  for functions that are present — silently reporting a real function as "not in the corpus", the exact
  failure the skill exists to prevent. The doc now matches the compact index, uses a full-quoted-token match,
  and adds a self-check that validates the search against a known-present token before trusting an empty result.
- **Corrected attribution:** the bundled third-party notices now credit the distributor-grammar authors by
  name — Zzyxzz (SkyPatcher) and powerofthree (SPID + KID) — and list the KID-authoring skill.

## 1.1.1 — 2026-06-06

houseCARL now points you at your logs — completing the external-tool bridge.

- **Log folders in status:** `housecarl_load_order_status` now surfaces the resolved Papyrus script-log and
  SKSE crash-log folders, so houseCARL knows where to read them when you ask about a Papyrus error or a
  crash. Set a folder explicitly with `housecarl_set_tool_path` (`papyrus_logs` / `crash_logs`); when one is
  unset, houseCARL auto-detects the default location and says so, or tells you exactly how to point it at
  yours. Logs are the one bridge dependency with no wrapping tool — you Read the `.log` files directly.

## 1.1.0 — 2026-06-06

houseCARL now drives the external modding toolchain, not just the data layer.

- **Tool bridge:** `housecarl_set_tool_path` registers — and auto-detects — the external tools houseCARL
  wraps. When a tool a command needs isn't set, houseCARL fails loud with the exact path it wants, rather
  than silently doing nothing.
- **Papyrus compile:** `housecarl_compile_script` compiles a `.psc` through the Creation Kit's
  `PapyrusCompiler.exe`. Compiler warnings are non-fatal, and the recompile is non-destructive — the
  existing `.pex` is overwritten only when the compile succeeds.
- **BSA archives:** `housecarl_bsa_list`, `housecarl_bsa_extract`, and `housecarl_bsa_repack` wrap BSArch
  to inspect, extract from, and repack `.bsa` archives. Repack is non-destructive — the target archive is
  replaced only when the pack succeeds.

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
