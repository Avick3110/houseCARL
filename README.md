# houseCARL

**Comprehensive, data-layer access to your Skyrim Special Edition load order — in plain English, through Claude or Codex.**

houseCARL runs a local MCP server with [Mutagen](https://github.com/Mutagen-Modding/Mutagen) — the
Bethesda-format library — kept warm in memory, giving your AI assistant direct access to every plugin
record across your Mod Organizer 2 load order, at the **true load-order winner**, with the full conflict
tree on request. You describe what you want in plain English; houseCARL does the mechanical work and, by
default, writes results into a **new** plugin you review and enable in MO2 — your originals untouched. When
you ask for it, an opt-in **in-place lane** edits an existing plugin directly instead (see below).

Coverage is **reflection-driven**: a build-time generator walks Mutagen's record interfaces and emits
houseCARL's schema automatically, so the set of record types houseCARL understands *is* the set Mutagen
models — by construction, not a hand-maintained subset.

## What it can do

- **Read any record** at the true load-order winner, with the full conflict tree on request — plus batch
  record detail and cross-plugin queries. Read a plugin that *isn't* in your active order too — even one
  inside a disabled mod — with a raw, clearly OUT-OF-LOAD-ORDER-flagged look at its own records, so you can
  inspect a donor mod before you enable it.
- **Author patches** — set / add / remove fields, edit leveled lists and containers, retune records,
  re-target conditions — emitted as a new MO2 mod folder (`houseCARL - <name>`). Or **forward a named
  plugin's version of a record** as a winning override (xEdit's "copy as override into"), or revert a record
  to vanilla.
- **Create new records** (new FormIDs) and **remove** records or individual entries; unused masters are
  cleaned automatically. Author a whole nested dialogue conversation in one call, validate a dialogue
  graph on demand, catch script (VMAD) properties a record declares but never binds (a silent `None` at
  runtime), and write the `.seq` file a plugin's start-game-enabled quests need. Author an empty
  header-only **trigger plugin** when a mod just needs `Foo.esp` to exist.
- **Edit an existing plugin in place** — when you ask, houseCARL edits, creates, and removes records
  directly inside an existing plugin (including one it didn't author) instead of writing a separate patch.
  Opt-in, gated by a one-time per-plugin consent prompt, and it keeps **no backup** — so the default
  new-patch lane above stays the default.
- **Compact and merge plugins** — ESL-compact a plugin into the light-master FormID window, carrying its
  FormID-keyed assets (facegen, voice) along so a compacted mod's faces don't go dark; or merge several
  plugins into one, renumbering only on collision and dropping now-unused masters — with the donor mods
  swapped out at the MO2 layer, originals intact.
- **Copy an NPC's appearance into a standalone** — lift a face (head parts, tints, the FaceGen mesh and
  textures) from a donor NPC into a fresh record that carries no dependency on the donor's plugin — the
  build behind a portable follower or a face transplanted between mods.
- **Trace a magic effect** — resolve a MagicEffect to every spell, enchantment, potion, scroll, and
  ingredient that carries it, with each one's magnitude, in a single call.
- **VFS asset layer** — read which copy of any Data-relative file (mesh, texture, script, sound,
  interface) actually wins your load order (the overwrite folder, a specific mod, Data, or inside a BSA),
  and place a file as a winning override into a new MO2 mod folder; loose-vs-BSA aware, with FaceGen as the
  headline use case. "Wrote it" is reported honestly as not yet "it wins" — you still enable and sort the
  new mod in MO2.
- **Read and write a mesh's internals** — open the winning copy of a `.nif` and read the values baked
  inside it — shape names, embedded skin / FaceTint texture paths, flags, alpha, partitions — and write a
  whitelisted value back (fix a wrong texture path, rename a shape), verified against a two-gate read-back.
  The mesh half of the dark-face fix, beside the record half.
- **See the SKSE-plugin layer** — inventory the DLLs and their config files under `Data\SKSE\Plugins`, each
  resolved to the mod that wins it (with the full conflict chain), and read each winning DLL's declared version
  metadata — name, author, target runtime, Address-Library flag — statically, without loading it.
- **See through the SkyPatcher layer** — inventory every SkyPatcher INI in your load order at once (apply
  order, VFS shadows, INI-vs-INI conflicts, and dead / duplicate / no-op writes), or read one record's
  *true* state after the whole SkyPatcher layer has replayed over it — so a runtime INI edit is as visible
  as a plugin override. Report-only.
- **Drive the external toolchain** — compile Papyrus scripts through the Creation Kit's compiler, and
  list / extract / repack BSA archives via BSArch; each tool's path is auto-detected or set once.
- **Decompile compiled scripts** — reconstruct reviewable `.psc` source from any `.pex` (Mutagen-native,
  no external tool needed), measured at 98.8% byte-exact recompile round-trips across every provable
  script in a 3,400-plugin load order; anything it can't prove fails loudly in the output, never
  silently wrong.
- **Look mods up on Nexus** — search the Skyrim SE catalogue and pull any mod's version, requirements,
  and *true* latest release straight from Nexus Mods, without opening a browser. Read-only: it finds
  and informs; downloading stays your mod manager's "Mod Manager Download" handoff.
- **Look things up, author distributor files, and review scripts** through 12 bundled, namespaced skills:
  record schemas (every type Mutagen models), Papyrus / SKSE signatures, SkyPatcher / SPID / KID
  distributor grammars, Skyrim dialogue authoring, Open Animation Replacer config authoring, SKSE plugin
  (C++/CommonLibSSE-NG) authoring, Papyrus performance review, dark-face NPC diagnosis, equip-slot lookup,
  and tool-output awareness.

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

1. Download **`houseCARL-1.7.0.zip`** from the [latest release](https://github.com/Avick3110/houseCARL/releases).
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
skills, builds the setup utility, and packs `release/houseCARL-1.7.0.zip`. Install the output with
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
- **`oar-authoring`** — author or interpret Open Animation Replacer (OAR) configs: `config.json` /
  `user.json`, condition sets, submod priorities (OAR ignores load order — higher priority wins), the
  source-verified `IsEquippedType` enum, addon conditions (Math / RaySense / IED / …), and DAR legacy
  folders. File-based; uses houseCARL only to resolve the forms a condition references. (DrHeisen.)
- **`tool-output-awareness`** — recognize the plugins and assets that generated tools produce (Reqtificator,
  ParallaxGen, DynDOLOD, Synthesis, TexGen, xLODGen, NPC Plugin Chooser 2) and keep their re-derived records
  and asset paths out of an authored patch — so you never bake a regenerable artifact into a hand patch that
  goes stale the next time the tool runs. (DrHeisen.)
- **`biped-slot-reference`** — turn a biped slot (a number like 52, a vanilla name like Body, or a community
  label like SOS / pelvis) into the `FirstPersonFlags` bit to query on, so finding every armor on an equip
  slot — multi-slot pieces an exact match misses included — is one `cross_plugin_query ... has` lookup
  instead of power-of-two mental math.
- **`skse-plugin-authoring`** — author, build, or audit a native **SKSE plugin DLL** (C++ on CommonLibSSE-NG:
  the layer beneath every SPID / KID / SkyPatcher distributor, framework, and crash logger) — scaffold the
  MSVC / CMake / vcpkg toolchain, write a plugin from scratch (lifecycle, event sinks, hooks), expose new
  native Papyrus functions from C++, target SE + AE + VR from one DLL, or read an open-source plugin's source
  to explain what it does. Distinct from ESP/record work and from `.psc` Papyrus (owned by `papyrus-reference`);
  its runtime claims still await an in-game validation pass.

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
- **DrHeisen** — contributed the `papyrus-optimization` skill (houseCARL's first community-contributed
  skill, a Papyrus performance reviewer), the `oar-authoring` skill (Open Animation Replacer config
  authoring), and the `tool-output-awareness` skill (keeping generated-tool output out of authored patches).
  Thank you.
