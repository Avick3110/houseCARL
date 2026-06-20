# houseCARL

**Comprehensive, data-layer access to your Skyrim Special Edition load order — in plain English, through Claude or Codex.**

houseCARL runs a local MCP server with [Mutagen](https://github.com/Mutagen-Modding/Mutagen) — the
Bethesda-format library — kept warm in memory, giving your AI assistant direct access to every plugin
record across your Mod Organizer 2 load order, at the **true load-order winner**, with the full conflict
tree on request. You describe what you want in plain English; houseCARL does the mechanical work and
writes results into a **new** plugin you review and enable in MO2 — your originals are never touched.

Coverage is **reflection-driven**: a build-time generator walks Mutagen's record interfaces and emits
houseCARL's schema automatically, so the set of record types houseCARL understands *is* the set Mutagen
models — by construction, not a hand-maintained subset.

## What it can do

- **Read any record** at the true load-order winner, with the full conflict tree on request — plus batch
  record detail and cross-plugin queries.
- **Author patches** — set / add / remove fields, edit leveled lists and containers, retune records,
  re-target conditions — emitted as a new MO2 mod folder (`houseCARL - <name>`).
- **Create new records** (new FormIDs) and **remove** records or individual entries; unused masters are
  cleaned automatically. Author a whole nested dialogue conversation in one call, validate a dialogue
  graph on demand, and write the `.seq` file a plugin's start-game-enabled quests need.
- **VFS asset layer** — read which copy of any Data-relative file (mesh, texture, script, sound,
  interface) actually wins your load order (the overwrite folder, a specific mod, Data, or inside a BSA),
  and place a file as a winning override into a new MO2 mod folder; loose-vs-BSA aware, with FaceGen as the
  headline use case. "Wrote it" is reported honestly as not yet "it wins" — you still enable and sort the
  new mod in MO2.
- **Drive the external toolchain** — compile Papyrus scripts through the Creation Kit's compiler, and
  list / extract / repack BSA archives via BSArch; each tool's path is auto-detected or set once.
- **Decompile compiled scripts** — reconstruct reviewable `.psc` source from any `.pex` (Mutagen-native,
  no external tool needed), measured at 98.8% byte-exact recompile round-trips across every provable
  script in a 3,400-plugin load order; anything it can't prove fails loudly in the output, never
  silently wrong.
- **Look mods up on Nexus** — search the Skyrim SE catalogue and pull any mod's version, requirements,
  and *true* latest release straight from Nexus Mods, without opening a browser. Read-only: it finds
  and informs; downloading stays your mod manager's "Mod Manager Download" handoff.
- **Look things up, author distributor files, and review scripts** through bundled, namespaced skills:
  record schemas (every type Mutagen models), Papyrus / SKSE signatures, SkyPatcher / SPID / KID
  distributor grammars, Skyrim dialogue authoring, Papyrus performance review, and dark-face NPC
  diagnosis.

## Requirements

- **Windows.**
- **.NET 9 — both the .NET Runtime 9.0 *and* the ASP.NET Core Runtime 9.0**, from the same
  [download page](https://dotnet.microsoft.com/download/dotnet/9.0). houseCARL ships framework-dependent
  (the runtime is not bundled), and the server needs the ASP.NET Core shared framework *on top of* the
  base .NET runtime. On Windows these are **two separate installers** — the ASP.NET Core Runtime
  installer does **not** include the base .NET Runtime — so install both. The setup utility checks for
  both and tells you exactly which is missing.
- **[Mod Organizer 2](https://www.modorganizer.org/)** with a modlist. houseCARL reads the instance's
  profile files statically — **MO2 does not need to be running.**
- **An AI host:** [Claude Code](https://claude.com/claude-code) (v2.1.143 or newer) — either the terminal
  CLI or the Claude desktop app, which has Claude Code built in (houseCARL runs in Claude Code sessions,
  not the plain chat) — **or** OpenAI Codex.

## Install

### Download — recommended (modders)

1. Download **`houseCARL-1.3.0.zip`** from the [latest release](https://github.com/Avick3110/houseCARL/releases).
2. Unzip it and run **`houseCARL-Setup.exe`**.
3. Pick your host — **`[1] Claude Code`**, **`[2] Codex`**, or **`[3] Both`**. The installer wires
   everything up:
   - **Claude** → installs the skills to `~/.claude/skills/housecarl/` and registers the server in
     `~/.claude.json` (both the Claude Code CLI and the desktop app read these). Persistent — no
     per-session flag.
   - **Codex** → installs the server under `%LOCALAPPDATA%\houseCARL\server\`, installs the skills (with a
     `$housecarl` umbrella entry point) under `~/.agents/skills/`, and registers the server in
     `~/.codex/config.toml`.
4. Fully restart your host (quit and reopen the Claude desktop app / restart every Codex session), then
   tell houseCARL your **MO2 instance folder** — the one containing `ModOrganizer.ini`. It prompts you on
   first use; you can switch instances anytime by asking it to set a new one.

> **Updating an existing install?** Fully quit Claude Code (and Codex) before re-running
> `houseCARL-Setup.exe` — it can't replace the server while a session is running it. If one is, it stops and
> tells you to quit and re-run.

### Build from source (developers / verifiers)

Requires the **.NET 9 SDK** (not just the runtime), Windows, and PowerShell.

```powershell
git clone https://github.com/Avick3110/houseCARL.git
cd houseCARL
./scripts/build-plugin.ps1
```

The script regenerates the reflection rulebook, publishes the server framework-dependent (trimming **off**
— houseCARL is reflection-driven, so trimming would strip types and silently lose coverage), bundles the
skills, builds the setup utility, and packs `release/houseCARL-1.3.0.zip`. Install the output with
`houseCARL-Setup.exe`, `claude --plugin-dir ./dist/housecarl`, or the bundled local-marketplace
descriptor — see the script header for details.

## Usage

Talk to it.

houseCARL writes each patch as its own MO2 mod folder (refresh mo2 required)

## How it works

houseCARL is a single C# process running an MCP server, with Mutagen kept warm for both reading and
writing. Reads use Mutagen's lazy binary overlay — records parse on access, so the load order isn't held
fully in memory. Writes go through a small set of generic op verbs over the same reflection layer, always
into a new plugin. The active load order is read **statically** from your MO2 profile's `loadorder.txt` /
`modlist.txt` / `plugins.txt` (no USVFS, no live MO2 hooking) and refreshes automatically on the next tool
call via cheap mtime checks. No plugin file handles are held at rest, so MO2 and xEdit can move or delete
plugins freely while houseCARL is running.

The only outbound network use is the read-only **Nexus Mods lookups** (catalogue search + mod detail).
They're **keyless** — the public Nexus catalogue API needs no account or API key — and offline-tolerant: if
there's no connection they say so plainly and every local capability keeps working. houseCARL reads Nexus;
it never downloads or installs (that stays your mod manager's `nxm` "Mod Manager Download" handoff).

## Bundled skills

Namespaced under `/housecarl:` in Claude (and reachable via `$housecarl` in Codex):

- **`mutagen-reference`** — every record type's schema (fields, types, writability, enums, polymorphic
  arms), generated by reflection over Mutagen.
- **`papyrus-reference`** — Papyrus + SKSE function signatures (vanilla Skyrim, SKSE, and ~45 popular
  SKSE-plugin sources), from [BellCube's papyrus-index](https://github.com/BellCubeDev/papyrus-index).
- **`skypatcher-authoring`**, **`spid-authoring`**, **`kid-authoring`** — author SkyPatcher INI,
  SPID `_DISTR.ini`, and KID `_KID.ini` distributor files from a grammar reference rather than invented
  syntax.
- **`papyrus-optimization`** — review a Papyrus script for performance: classify each part broken /
  suboptimal / clean, explain what makes it heavy, and give the fix. The cost-and-habits complement to
  `papyrus-reference`, and houseCARL's first community-contributed skill (DrHeisen).
- **`facegen-diagnostics`** — walk the dark / grey / black-face NPC bug end to end: compare which plugin
  wins the NPC record against which mod or BSA wins the facegen file, then place the correct facegen as a
  winning override or forward the matching appearance into a new plugin, instructing the CK / NifSkope /
  RaceMenu steps houseCARL can't perform.
- **`dialogue-authoring`** — author or audit Skyrim dialogue at the data layer: create topics (DIAL) and
  lines (INFO) in a new plugin, wire them to a branch and quest, attach result scripts, write the
  start-game-enabled `.seq`, and validate a whole topic's or quest's dialogue graph — encoding the
  counter-intuitive bookkeeping a byte-valid line skips (and so plays nothing in game).

## License

houseCARL is licensed **GPL-3.0-only** — see [LICENSE](LICENSE). This is required by Mutagen (GPL-3.0-only,
no linking exception), which houseCARL is built on and bundles. Every third-party component and its license
is listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt), along with the corresponding-source
pointers.

## Credits

- **[Mutagen](https://github.com/Mutagen-Modding/Mutagen)** by Noggog — the Bethesda-format library
  houseCARL is built on.
- **[papyrus-index](https://github.com/BellCubeDev/papyrus-index)** by **BellCube** — the source corpus
  for the bundled `papyrus-reference` skill. Thank you.
- **Zzyxzz** (SkyPatcher) and **powerofthree** (SPID and KID) — whose public documentation the
  distributor-authoring grammar facts were drawn from.
- **DrHeisen** — contributed the `papyrus-optimization` skill, houseCARL's first community-contributed
  skill: a Papyrus performance reviewer. Thank you.
