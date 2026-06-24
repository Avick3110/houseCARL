# houseCARL

**Comprehensive, data-layer access to your Skyrim Special Edition load order — in plain English.**

houseCARL is a Claude Code plugin. It runs a local MCP server with
[Mutagen](https://github.com/Mutagen-Modding/Mutagen) kept warm in memory, giving Claude direct access to
every plugin record across your Mod Organizer 2 load order. You describe what you want in plain English;
houseCARL does the mechanical work and, by default, writes results into a **new** plugin you review and
enable in MO2 — your originals untouched. When you ask for it, an opt-in **in-place lane** edits an existing
plugin directly instead.

It can:

- **Read any record** at the true load-order winner, with the full conflict tree on request.
- **Author patches** — set / add / remove fields, edit leveled lists and containers, retune records,
  re-target conditions — emitted as a new MO2 mod folder (`houseCARL - <name>`). Or **forward a named
  plugin's version of a record** as a winning override (xEdit's "copy as override into"), or revert to vanilla.
- **Create new records** (new FormIDs) and **remove** records or individual entries; unused masters are
  cleaned automatically. Author a whole nested dialogue conversation in one call, validate a dialogue
  graph on demand, write the `.seq` file a plugin's start-game-enabled quests need, and author an empty
  header-only **trigger plugin** when a mod just needs `Foo.esp` to exist.
- **Edit an existing plugin in place** — on request, edit / create / remove records directly inside an
  existing plugin (including one houseCARL didn't author) instead of writing a separate patch. Opt-in, gated
  by a one-time per-plugin consent prompt, and it keeps **no backup**; the default new-patch lane stays the
  default.
- **Trace a magic effect** — resolve a MagicEffect to every spell, enchantment, potion, scroll, and
  ingredient that carries it, with each one's magnitude, in a single call.
- **VFS asset layer** — read which copy of any Data-relative file (mesh, texture, script, sound,
  interface) actually wins your load order (the overwrite folder, a specific mod, Data, or inside a BSA),
  and place a file as a winning override into a new MO2 mod folder; loose-vs-BSA aware, with FaceGen as the
  headline use case. "Wrote it" is reported honestly as not yet "it wins" — you still enable and sort the
  new mod in MO2.
- **Drive the external toolchain** — compile Papyrus scripts through the Creation Kit's compiler, and
  list / extract / repack BSA archives via BSArch; each tool's path is auto-detected or set once.
- **Decompile compiled scripts** — reconstruct reviewable `.psc` source from any `.pex` (Mutagen-native,
  no external tool needed), measured at 98.8% byte-exact recompile round-trips across every provable
  script in a 3,400-plugin load order; anything it can't prove fails loudly, never silently wrong.
- **Look mods up on Nexus** — search the Skyrim SE catalogue and read any mod's version, requirements,
  and latest release straight from Nexus Mods, no browser needed. Read-only; downloading stays your mod
  manager's job.
- Look up **record schemas** (every type Mutagen models) and **Papyrus / SKSE signatures**, author
  **SkyPatcher**, **SPID**, and **KID** distributor files, **author Skyrim dialogue** and **Open Animation
  Replacer configs**, **review Papyrus scripts for performance**, **diagnose the dark / grey / black-face NPC
  bug**, **find armor by equip slot**, and **recognize generated tool output** — through 11 bundled,
  namespaced skills (`/housecarl:mutagen-reference`, `/housecarl:papyrus-reference`,
  `/housecarl:skypatcher-authoring`, `/housecarl:spid-authoring`, `/housecarl:kid-authoring`,
  `/housecarl:dialogue-authoring`, `/housecarl:papyrus-optimization`, `/housecarl:facegen-diagnostics`,
  `/housecarl:oar-authoring`, `/housecarl:tool-output-awareness`, `/housecarl:biped-slot-reference`).

Coverage is **reflection-driven**: the set of record types houseCARL understands *is* the set Mutagen
models, by construction — not a hand-maintained subset.

## Requirements

- **Windows.**
- **.NET 9 — both the .NET Runtime 9.0 *and* the ASP.NET Core Runtime 9.0**, from the same
  [download page](https://dotnet.microsoft.com/download/dotnet/9.0). houseCARL ships framework-dependent
  (the runtime is not bundled), and the server needs the ASP.NET Core shared framework *on top of* the
  base .NET runtime. On Windows these are **two separate installers** — the ASP.NET Core Runtime
  installer does **not** include the base .NET Runtime — so install both.
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
- "Search Nexus for the most-endorsed archery overhauls — what's the top one's latest version and requirements?"

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
- **Zzyxzz** (SkyPatcher) and **powerofthree** (SPID and KID) — the public documentation behind the
  distributor-authoring skills.
- **DrHeisen** — contributed the `papyrus-optimization` skill (houseCARL's first community-contributed
  skill, a Papyrus performance reviewer), the `oar-authoring` skill (Open Animation Replacer config
  authoring), and the `tool-output-awareness` skill (keeping generated-tool output out of authored patches).
  Thank you.
