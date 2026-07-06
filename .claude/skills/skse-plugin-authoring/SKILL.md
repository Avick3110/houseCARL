---
name: skse-plugin-authoring
description: Author, build, or audit a native SKSE plugin DLL — C++ / CommonLibSSE-NG code, the layer beneath every SPID/KID/SkyPatcher distributor, framework, and crash logger — NOT an ESP/record plugin and NOT a .psc Papyrus script. Use when the user wants to write or build an SKSE plugin or a .dll, add a native or SKSE Papyrus function, create a BSTEventSink to react to game events, hook the game (trampoline, vtable, Address Library / versionlib), target SE + AE + VR from one DLL, set up a CommonLibSSE / CommonLibSSE-NG project, or audit / explain what an existing open-source SKSE plugin does. This is the C++ producer side — editing records (ARMO, NPC_, DIAL) is the mutagen/record tools, and the .psc Papyrus signature surface is owned by `papyrus-reference` — "plugin" here means a compiled native DLL, not an .esp. Load this before scaffolding, building, or reading any SKSE C++, even a "tiny" one-function DLL — the multi-runtime, Address-Library, and lifecycle rules are non-obvious and compile clean while failing at load or CTDing in game.
---

# SKSE Plugin Authoring

## Overview

Authoring an SKSE plugin means writing a native **C++ DLL** on **CommonLibSSE-NG** — the layer that sits
*beneath* the data-layer world houseCARL usually operates in. SPID, KID, SkyPatcher, MCM frameworks, OAR,
and every crash logger are SKSE DLLs; this skill is how you build one, extend one, or read one. The output
is a `SKSE/Plugins/<name>.dll` a user drops into a mod folder — not an `.esp`, and not a `.psc`.

Claude Code **can** do the whole loop natively: it compiles C++ against CommonLibSSE-NG on this machine,
scaffolds the project, and iterates build → deploy → check-the-log. What it **needs** is the toolchain in
place — MSVC, CMake, vcpkg (or xmake) — which is the Step-0 gate below. Nothing here calls a houseCARL MCP
tool for the C++ build itself; the C++ compiler is the engine, and houseCARL tools touch only the `.psc`
edge and file placement.

**Audience note — this skill serves a different persona.** houseCARL's other skills serve the *modlist-
builder* working at the data layer (records, distributors, conflicts). This one serves the *mod author
writing C++*. When a request is plainly about editing a record or distributing a form, that is a different
skill (see the boundary rules below) — don't route it here just because the word "plugin" appears.

## Route first — which job is this?

Three shapes of work land here, plus a deferred fourth (porting). Identify the job before touching a
reference; each has a different entry point and a different set of references.

- **(a) Write a new plugin** — an event sink, a hook, or general native code that reacts to or patches the
  game. Runs the toolchain gate, then the skeleton, then whichever capability reference the behavior needs.
- **(b) Add a native Papyrus function** *(the flagship)* — expose a new function to `.psc` scripts that
  vanilla Papyrus + SKSE can't provide. Runs the toolchain gate, the skeleton, then
  `native-papyrus-functions.md`. This is the C++ producer side of a function whose `.psc` declaration is
  owned by `papyrus-reference` — see the boundary rule below.
- **(c) Audit / explain an existing plugin** — read an open-source SKSE plugin (a repo, or the source behind
  a DLL in a load order) and say what it does. **No toolchain gate** — this is pure reading. Go straight to
  `plugin-dissection.md`.

**(d, deferred) Porting someone else's old plugin across runtimes** (dragging a SE-only DLL's source up to
SE+AE+VR) is a **deferred lane** — the reference set teaches authoring and multi-runtime discipline for code you write, not
a mechanical port procedure. If asked to port, say so plainly: you can *author* fresh multi-runtime code
(`multi-runtime.md`) and *read* the existing plugin (`plugin-dissection.md`), but a turnkey port workflow
isn't in this skill yet — surface that rather than improvising one.

## Step 0 — the toolchain gate (jobs a and b, always)

Before writing a line of plugin logic, confirm the build environment exists. A plugin you can't compile and
load-test is a plugin you're guessing about, and houseCARL's Q3 rule forbids pretending a build succeeded.
Preflight MSVC (VS2022 Build Tools), CMake, and vcpkg (or xmake) per `toolchain-setup.md`; if any is
missing, **gate loudly** — teach the setup from that reference and stop, rather than emitting C++ that can
never be built here. The same reference carries the scaffold (the proven project file-set) and the tight
build → deploy → verify (`skse64.log`) loop that answers "does it load?" in seconds. Job (c) skips this
gate entirely — reading source needs no compiler.

## The Papyrus boundary — state it wherever the line is crossed

A native Papyrus function is **one thing declared in two places**: the C++ function you register (this
skill), and the matching `.psc` declaration a script author compiles against (**not** this skill). This
skill authors only the **C++ producer side** and the pairing contract. The `.psc` signature surface — the
`native`/`global` keywords, parameter names, defaults, how a latent function reads script-side — is owned
by houseCARL's **`papyrus-reference`** skill. Never derive a `.psc` signature from a C++ header and present
it as authoritative; when you need the script-side line, consult `papyrus-reference`. The consumer `.psc`
then compiles via `housecarl_compile_script` (never a hand-rolled `PapyrusCompiler.exe` call — it quotes
spaced paths and won't hit originals). Say this boundary out loud any time a task straddles the C++/Papyrus
line, so nobody treats a header-derived signature as the real one.

## The references — nine files, read the ones your job touches

Each reference is self-contained and opens with its own contents list. Read for the job, not front to back.

- **`toolchain-setup.md`** — the Step-0 gate: set up MSVC / CMake / vcpkg (or xmake), get the CommonLibSSE-NG
  submodule, scaffold a project, and run the build→deploy→verify loop. Read first for jobs (a) and (b).
- **`plugin-skeleton.md`** — the plugin declaration (the `SKSEPluginInfo` export triad), `SKSE::Init` and
  the interface surface, the nine-message SKSE lifecycle (and which game state is legal when), and logging.
  Read right after the toolchain gate for any new plugin.
- **`event-sinks.md`** — receive game events via `BSTEventSink`: the mechanism, the mandatory null-guard, the
  `ScriptEventSourceHolder` event inventory, and registration timing (default to `kDataLoaded`). Read when
  the plugin reacts to something the game does.
- **`hooking.md`** — patch game code: why you never compile an address (the REL / Address-Library layer), the
  Trampoline and its budgets, and call / branch / vtable hooks. Read before writing any hook — several traps
  compile clean and CTD at runtime.
- **`native-papyrus-functions.md`** — the flagship: expose a new Papyrus function from C++, the full
  type-marshalling map, latent functions, calling back into Papyrus, and the pairing rule to `papyrus-reference`.
  Read for job (b).
- **`multi-runtime.md`** — one DLL for SE + AE + VR: the core layout problem, the footgun catalog (the
  centerpiece), the compile-time-vs-runtime decision rule, and the three conditional patterns. Read before
  authoring any engine-touching code — member reads, virtual calls, upcasts — the moment VR is in scope.
- **`threading-and-persistence.md`** — the production plumbing: game-thread task marshalling, co-save
  (`.skse`) serialization, runtime FormID → live-form lookup, and the config/logging boilerplate. Read when
  the plugin mutates game state off-thread or must remember state across a save.
- **`load-failures.md`** — why a DLL refuses to load or CTDs at startup: the popup-caption vs log-line
  discriminators, the rejection table (exact `skse64.log` strings → fix), and the CommonLib self-abort
  surface. Read when a build won't load; also the crash-diagnostics handoff surface.
- **`plugin-dissection.md`** — read an unfamiliar open-source plugin and explain it: the five-lens flow
  (entry point → lifecycle → engine touch-points → config → intent) and the DLL-name → mod attribution seam.
  Read for job (c).

## Common mistakes

- **Ignoring the multi-runtime footguns.** The dominant failure here is *not* a compile error — it's a
  silently-wrong memory read or a VR-only CTD from code that built clean on a single-runtime test. Direct
  struct-member access, a naive virtual call, an upcast — each sits at a different byte offset or vtable slot
  per runtime. Read `multi-runtime.md`'s footgun catalog before writing engine-touching C++, not after the
  VR CTD report.
- **Dropping an export from the triad.** A portable plugin exports `SKSEPlugin_Version` (AE data path),
  `SKSEPlugin_Query` (SE/VR function path), and `SKSEPlugin_Load`. Drop `Query` and **VR never sees the
  plugin** — it's silently skipped, not an error dialog. `plugin-skeleton.md` has the triad; the declarative
  macro emits all three for you.
- **Mutating game state off the main thread.** Most game-state writes must happen on Skyrim's main game
  thread, but the code that *discovers* the work (an event sink, a hook thunk, a Papyrus call) runs
  elsewhere. Marshal onto the game thread — `threading-and-persistence.md` Part 1 — rather than mutating
  where you stand, which is a race and an intermittent CTD.
- **Self-requeueing an `AddTask` for repeated work.** The SKSE task queue drains pop-until-empty each pass,
  so a task that re-queues itself runs again in the *same* drain — the whole "loop" executes inside one
  frame and hard-freezes the main thread (field-verified, twice). Pace repeats from your own worker thread
  posting one-shot tasks, or an update hook — `threading-and-persistence.md` Part 1.
- **Fighting another plugin for state it manages.** A framework that owns an engine value (scale, morphs,
  camera…) re-asserts it on its own schedule; a second writer produces a visible ping-pong war it can't
  win. Consume the framework's published API, or correct the *input* it computes from — `hooking.md`'s
  "when to hook, sink, or call native" ranking.
- **Hand-rolling the setup instead of the toolchain.** The scaffold, the PCH, the `/Zc:preprocessor` flag,
  and the per-runtime presets in `toolchain-setup.md` are consumer *obligations* CommonLibSSE-NG needs;
  guessing at them produces a project that won't configure. Use the proven file-set.
- **Copying a non-NG idiom.** The CommonLibSSE-NG *wiki* documents a declarative loader (`OnSKSEPluginLoad`,
  `DECLARATIVE`) that **does not exist** in the `ng` branch — a plugin using those macros won't compile.
  Likewise `REX::INFO` is a tell of a non-NG (po3/libxse) lineage. Hand-write `SKSEPlugin_Load` and call
  `SKSE::Init()` yourself, the live NG idiom.
- **Emitting the plugin declaration twice.** When the build system generates the export triad
  (`add_commonlibsse_plugin` / the xmake plugin rule), a hand-written `SKSEPluginInfo` or manual export
  triad on top produces duplicate exports and confusing load failures. One metadata path, never both —
  `toolchain-setup.md`.
- **Treating a C++ header as the `.psc` truth.** A hooked thunk signature or a vtable slot is not a Papyrus
  function signature. When the task needs the script-side line, that's `papyrus-reference`, not a header.

## Not yet verified in-game (honesty)

Every reference ends with a **"Not yet verified in-game"** section, and it means what it says. The corpus
was built by reading the CommonLibSSE-NG source, its wiki, and real production plugins (SPID, OAR, po3
Papyrus Extender) — a strong paper foundation, but an in-game validation pass (V0–V3) is **still owed**.
Present the runtime behaviors those sections flag as **untested**, not proven: a claim that a sink fires at
a given phase, that a marshalled task lands on the right thread, that a hook survives a specific runtime —
these are read-from-source expectations awaiting an empirical build-test. Carry that caveat to the user
rather than promising "this will work in game" from a clean compile. A clean build proves the code compiles;
it does not prove the runtime behaves.

## Notes

- **Lineage — build against alandtse `ng`, don't switch.** "CommonLibSSE-NG" names several lineages.
  **`alandtse/CommonLibVR` branch `ng`** (`commonlibsse-ng` @ 4.x) is the current, charter-locked target:
  one DLL for SE+AE+VR. **CharmedBaryon/CommonLibSSE-NG** is the frozen origin (since 2023-05) — recognize
  it in the wild, don't build on it. **powerof3/CommonLibSSE `dev`** is a *different* package (`commonlibsse`,
  no VR) whose idioms may not map. **libxse** is an emerging fork lineage (flagged 2026-07); its newer
  `commonlibsse` template is po3-derived, not NG (the `REX::INFO` tell). Where a reference notes a divergence
  it's so you read borrowed code correctly — never as a recommendation to switch.
- **Crash-diagnostics handoff.** `load-failures.md` (symptom → `skse64.log` line → cause → fix) and
  `plugin-dissection.md` (DLL-name → mod attribution) are the surfaces houseCARL's future crash-diagnostics
  skill consumes in reverse. When a task is really "diagnose this user's crash," those two references are the
  bridge.
- **houseCARL tool touch-points are thin.** The C++ build runs on the native compiler, not a houseCARL tool.
  The only tool in this skill's lane is `housecarl_compile_script` for the **`.psc` consumer side** of a
  native function (job b). Deploying the built DLL is plain file placement — copy it to a mod's
  `SKSE/Plugins/<name>.dll`; there is no deploy tool, and none is needed.
