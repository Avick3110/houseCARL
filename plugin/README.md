# houseCARL

**Comprehensive, data-layer access to your Skyrim Special Edition load order — in plain English.**

houseCARL is a Claude Code plugin. It runs a local MCP server with
[Mutagen](https://github.com/Mutagen-Modding/Mutagen) kept warm in memory, giving Claude direct access to
every plugin record across your Mod Organizer 2 load order. You describe what you want in plain English;
houseCARL does the mechanical work and writes results into a **new** plugin you review and enable in MO2 —
your originals are never touched.

It can:

- **Read any record** at the true load-order winner, with the full conflict tree on request.
- **Author patches** — set / add / remove fields, edit leveled lists and containers, retune records,
  re-target conditions — emitted as a new MO2 mod folder (`houseCARL - <name>`).
- **Create new records** (new FormIDs) and **remove** records or individual entries; unused masters are
  cleaned automatically.
- **Drive the external toolchain** — compile Papyrus scripts through the Creation Kit's compiler, and
  list / extract / repack BSA archives via BSArch; each tool's path is auto-detected or set once.
- Look up **record schemas** (every type Mutagen models) and **Papyrus / SKSE signatures**, and author
  **SkyPatcher**, **SPID**, and **KID** distributor files — through bundled, namespaced skills
  (`/housecarl:mutagen-reference`, `/housecarl:papyrus-reference`, `/housecarl:skypatcher-authoring`,
  `/housecarl:spid-authoring`, `/housecarl:kid-authoring`).

Coverage is **reflection-driven**: the set of record types houseCARL understands *is* the set Mutagen
models, by construction — not a hand-maintained subset.

## Requirements

- **Windows.**
- **.NET 9 — the [ASP.NET Core Runtime 9.0](https://dotnet.microsoft.com/download/dotnet/9.0)** installed.
  houseCARL ships framework-dependent (the runtime is not bundled), and the server needs the ASP.NET Core
  shared framework, so the plain ".NET Runtime" or "Desktop Runtime" is not sufficient — install the
  **ASP.NET Core Runtime** (it includes the base .NET runtime).
- **[Mod Organizer 2](https://www.modorganizer.org/)** with a modlist. houseCARL reads the instance's
  profile files statically — **MO2 does not need to be running.**
- **Claude Code v2.1.143 or newer.** (Earlier builds ignore the plugin's `displayName`; loading a zipped
  plugin with `--plugin-dir ./x.zip` needs ≥ v2.1.128.)

## Setup

When you enable the plugin, Claude Code shows a **folder picker**: choose your **MO2 instance folder** —
the one containing `ModOrganizer.ini` for your modlist. houseCARL derives everything else from it: the mods
folder, the active profile, and the true load order. To switch instances later, just ask houseCARL to set a
new MO2 instance.

Your settings are stored under Claude Code's plugin data directory, so they survive plugin updates.

## Usage

Talk to it. For example:

- "What does the Dragonbane record look like across my load order?"
- "Make a patch that gives every iron weapon +5 damage."
- "Distribute a Frost Resistance ability to all Nords with SPID."
- "Which plugins override the IronSword record, and who wins?"

houseCARL writes each patch as its own MO2 mod folder; enable it in MO2 like any other mod, then review it
in xEdit if you like before playing.

## License

houseCARL is licensed **GPL-3.0-only** — see [LICENSE](LICENSE). This is required by Mutagen (GPL-3.0-only,
no linking exception), which houseCARL bundles. Every third-party component and its license is listed in
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt), along with the corresponding-source pointers.

## Credits

- **[Mutagen](https://github.com/Mutagen-Modding/Mutagen)** by Noggog — the Bethesda-format library
  houseCARL is built on.
- **[papyrus-index](https://github.com/BellCubeDev/papyrus-index)** by **BellCube** — the source corpus for
  the bundled `papyrus-reference` skill. Thank you.
- The **SkyPatcher** author and **powerof3** (SPID) — the public documentation behind the
  distributor-authoring skills.
