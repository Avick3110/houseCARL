---
name: skse-plugin-authoring
description: >-
  Author, build, or audit a native SKSE plugin DLL against CommonLibSSE-NG (alandtse ng) — "plugin"
  here means a compiled DLL, not an .esp or .psc — and load this before any SKSE C++ is written. Use
  for an SKSE plugin or .dll, declaring and registering a native Papyrus function, BSTEventSinks,
  game hooks (trampoline, Address Library), SE + AE + VR targeting, or reading someone else's SKSE
  DLL source. Multi-runtime and lifecycle rules compile clean yet fail at load.
compatibility: >-
  Requires the houseCARL MCP server and a configured Mod Organizer 2 instance. Building also
  needs MSVC (VS2022 Build Tools), CMake, vcpkg or xmake, and a CommonLibSSE-NG (alandtse ng)
  checkout.
---

# SKSE Plugin Authoring

## Overview

An SKSE plugin is a native **C++ DLL** built against CommonLibSSE-NG and shipped as
`SKSE/Plugins/<name>.dll` — not an `.esp`, and not a `.psc`. SPID, KID, SkyPatcher, the MCM
frameworks, OAR and every crash logger are SKSE DLLs; this skill is how you write one, extend one, or
read one.

**Audience note.** This skill serves the mod author writing C++, not the modlist builder working at
the data layer. The word "plugin" in a request is not a routing signal: editing a record or
distributing a form is a different skill.

**The houseCARL surface here is thin.** `housecarl_compile_script` is the only tool in the build
lane, and it compiles the consumer `.psc`, never the C++; the read tools for job (c) and for
load-failure triage are `housecarl_skse` (the SKSE layer of the live order — DLL inventory, the
declaration-to-implementation pairing, config references) and `housecarl_load_order_status`. Sibling
skills are written `housecarl:<name>` on a Claude Code install; a Codex install sees the bare folder
name (`papyrus-reference`).

## Route first — which job is this?

Four shapes of work land here. Identify the job before opening a reference: each has its own entry
point, and the reference list below says which files that job touches.

- **(a) Write a new plugin** — an event sink, a hook, or native code that reacts to or patches the
  game. Runs the toolchain gate, then the skeleton, then whichever capability the behaviour needs.
- **(b) Add a native Papyrus function** *(the flagship)* — expose a function to `.psc` scripts that
  vanilla Papyrus and SKSE cannot provide. Runs the toolchain gate, then the section below: **both
  halves of the pair are written here**, the C++ registration and the `.psc` declaration.
- **(c) Audit or explain an existing plugin** — someone else's repo, or a DLL already sitting in the
  load order. **No toolchain gate**: this is reading, not building. `housecarl_skse` finds the DLL,
  the mod that wins the VFS for it, and the static manifest the SKSE loader itself reads.
- **(d, deferred) Port someone else's old plugin across runtimes** — an SE-only DLL's source dragged
  up to SE+AE+VR. **This skill does not do it.** Say so plainly rather than improvising: it can
  author fresh multi-runtime code and read the existing plugin, but a port procedure is not here.

## Step 0 — the toolchain gate (jobs a and b)

Before writing a line of plugin logic, confirm the build environment exists: MSVC (VS2022 Build
Tools), CMake, vcpkg **or** xmake, and a CommonLibSSE-NG checkout. If any of it is missing, **gate
loudly and stop** — teach the setup and stop there, rather than emitting C++ that cannot be built on
this machine. **Never report a build as succeeding that was not run:** say which of configure, build
and load actually happened, and call an unverified runtime behaviour unverified.

**CMake + vcpkg is the default**; take xmake when the request asks for it, or when the project
already builds with it. Both file-sets are in the toolchain reference, which marks how far each one
is proven — use them rather than hand-rolling, because `/Zc:preprocessor` and a force-included PCH
carrying the game headers are consumer obligations whose failures name something other than
themselves.

The loop is build → deploy → verify. Deploying is plain file placement — copy the built DLL to a
mod's `SKSE/Plugins/<name>.dll`; no houseCARL tool takes a build output. Then launch and read
`skse64.log` for the plugin's own line, which answers "does it load?" in seconds;
`housecarl_load_order_status` names the resolved SKSE crash-log and Papyrus script-log folders, so
triage never starts with a filesystem hunt.

## A native Papyrus function — both halves are written here

The `.psc` declaration for a native you are inventing is written here, beside its C++ registration:
`housecarl:papyrus-reference` looks up functions that already exist and cannot answer for one that
does not.

Registration is keyed by two strings, `(className, functionName)`, resolved at bind time; nothing in
the C++ headers defines or validates the matching `.psc` line. **Four things must line up:**

- the script's `Scriptname` equals the registered class-name string;
- the function's name equals the registered function-name string;
- the declaration carries `native`, and `global` exactly when the C++ base parameter is
  `RE::StaticFunctionTag*`;
- the `.psc` parameters after the base correspond one-for-one to the C++ parameters, types mapped.

**A global native.** Base `StaticFunctionTag*` on the C++ side is `global` on the script side.

```cpp
std::int32_t GetActorCountInCell(RE::StaticFunctionTag*, RE::TESObjectCELL* a_cell);
vm->RegisterFunction("GetActorCountInCell", "HCDemo", GetActorCountInCell);
```
```papyrus
Scriptname HCDemo Hidden
Int Function GetActorCountInCell(Cell akCell) global native
```

**A latent function.** The C++ callback is the long form returning `LatentStatus`, registered with
`RegisterLatentFunction<R>` and completed later with `vm->ReturnLatentResult<R>(stackID, result)`.
Papyrus has no `latent` keyword, so **the declaration is spelled exactly like a non-latent one** —
the latency shows up only in the caller's stack suspending.

```cpp
RE::BSScript::LatentStatus WaitForBounty(RE::BSScript::Internal::VirtualMachine* vm,
                                         RE::VMStackID stackID, RE::StaticFunctionTag*,
                                         RE::TESFaction* a_faction);   // kStarted / kFailed
vm->RegisterLatentFunction<std::int32_t>("WaitForBounty", "HCDemo", WaitForBounty);
```
```papyrus
Int Function WaitForBounty(Faction akFaction) global native
```

**A narrowing return type.** `size_t`, `std::int64_t` and `double` all marshal — as Int, Int and
Float — so this **compiles and then silently truncates**. Declare `std::int32_t` / `std::uint32_t`
for Int and `float` for Float, and do the widening in C++ before you return.

```cpp
std::size_t CountRefs(RE::StaticFunctionTag*);   // WRONG: narrows to Int32 with no diagnostic
std::int32_t CountRefs(RE::StaticFunctionTag*);  // right: the width the VM actually carries
```
```papyrus
Int Function CountRefs() global native
```

Then compile the consumer `.psc` with `housecarl_compile_script`, never a hand-rolled
`PapyrusCompiler.exe` call — it quotes spaced paths and will not touch originals.

## The references — nine files, read the ones your job touches

Each reference is self-contained and opens with its own contents list. Read for the job, not front to
back.

- Read `references/toolchain-setup.md` first for jobs (a) and (b) — it is the gate, the scaffold and
  the build loop.
- Read `references/plugin-skeleton.md` straight after the gate, for any new plugin: the export triad,
  `SKSE::Init`, the interface surface, the nine-message lifecycle and logging.
- Read `references/native-papyrus-functions.md` for job (b), together with the declaration grammar
  above: the registration idiom, the full type-marshalling map, latent functions, calling back in.
- Read `references/event-sinks.md` when the plugin has to react to something the game does —
  `BSTEventSink`, the null-guard, the event inventory, registration timing (default `kDataLoaded`).
- Read `references/hooking.md` before writing any hook — several traps compile clean and CTD at
  runtime: the Address-Library layer, the Trampoline and its budgets, the hook-sink-or-call ranking.
- For an SE+AE target read only `references/multi-runtime.md`'s "SE+AE only" route — the `Common.h`
  derivation, FLAT-is-not-SE-or-AE, the dual entry point, `auto` for `RELOCATION_ID`, and the
  accessor rule; read the rest of the file when VR is in scope.
- Read `references/threading-and-persistence.md` when the plugin mutates game state off-thread or
  must remember state across a save: task marshalling, co-save serialization, FormID lookup.
- Read `references/load-failures.md` when a build will not load — the popup-versus-log
  discriminators and the rejection table (exact `skse64.log` strings → fix).
- Read `references/plugin-dissection.md` for job (c), beside `housecarl_skse` for a DLL already in
  the order: the five-lens read of an unfamiliar plugin and the DLL-name → mod attribution seam.

## Common mistakes

- **Gate with `EXCLUSIVE_SKYRIM_SE` / `_AE` where SE and AE differ, and read every runtime-varying
  member through its accessor.** A dual SE+AE build defines `EXCLUSIVE_SKYRIM_FLAT` alone — FLAT
  means "not VR", not "one non-VR runtime" — so assuming it defines `_SE` and `_AE` is exactly
  backwards. Direct member access, a naive virtual call and an upcast each sit at a different offset
  or vtable slot per runtime; all of it compiles clean under a single-runtime test.
- **Export the whole triad.** A portable plugin exports `SKSEPlugin_Version` (AE data path),
  `SKSEPlugin_Query` (SE/VR function path) and `SKSEPlugin_Load`. Drop `Query` and VR never sees the
  plugin — silently skipped, no error dialog.
- **Keep one metadata path.** When the build system generates the export triad
  (`add_commonlibsse_plugin`, or the xmake plugin rule), a hand-written `SKSEPluginInfo` on top gives
  duplicate exports and a load failure that names nothing.
- **Set the log up before `SKSE::Init`, and never re-init it after.** `SKSE::Init(skse)` truncates a
  log you have just configured, taking every pre-`Init` line with it.
- **Marshal state changes onto the game thread.** Most game-state writes must run on Skyrim's main
  thread, but the code that *discovers* the work — an event sink, a hook thunk, a Papyrus call — runs
  elsewhere. Mutating where you stand is a race and an intermittent CTD.
- **Pace repeats from your own thread, not by self-requeueing `AddTask`.** The SKSE task queue drains
  pop-until-empty each pass, so a task that re-queues itself runs again in the *same* drain: the
  whole "loop" executes inside one frame and hard-freezes the main thread (field-verified, twice).
- **Consume a framework's API instead of fighting it.** A framework that owns an engine value (scale,
  morphs, camera) re-asserts it on its own schedule; a second writer produces a ping-pong war it
  cannot win. Use its published API, or correct the input it computes from.

## Lineage and honesty

**Build against alandtse `ng`, and do not switch.** "CommonLibSSE-NG" names several lineages.
**`alandtse/CommonLibVR` branch `ng`** (`commonlibsse-ng` @ 4.x) is the target: one DLL for SE+AE+VR.
**CharmedBaryon/CommonLibSSE-NG** is the frozen origin — recognise it in the wild, do not build on
it. **powerof3/CommonLibSSE `dev`** is a *different* package (`commonlibsse`, no VR) whose idioms may
not map. **libxse** is a po3-derived fork lineage; `REX::INFO` is the tell. Where a reference notes a
divergence, that is so you read borrowed code correctly — never a recommendation to switch. The same
goes for idioms: the NG wiki documents a declarative loader (`OnSKSEPluginLoad`, `DECLARATIVE`) that
does **not** exist on the `ng` branch, so hand-write `SKSEPlugin_Load` and call `SKSE::Init` yourself.

**This corpus is paper-verified.** It was built by reading the CommonLibSSE-NG source, its wiki and
real production plugins; an in-game validation pass is still owed, and every reference ends with a
"Not yet verified in-game" section saying which of its claims that covers. Present those runtime
behaviours as untested, not proven: a clean build proves the code compiles, not that the game
behaves.
