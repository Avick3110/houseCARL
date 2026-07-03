# Reading an unfamiliar CommonLibSSE plugin

You're handed an open-source SKSE plugin — a GitHub repo, or the source behind a DLL in someone's load
order — and asked what it does: which engine surfaces it touches, what runtimes it claims, whether it's a
framework other mods depend on. This reference is the reasoning order for answering that from the source
alone; reading a plugin well is also how you copy the right idiom before *writing* one (the rest of the skill).

This is an investigation flow, not a fixed tool script. Five lenses, ordered so each builds on the last — at
each you grep a small set of anchor symbols, read what they point to, and accrete an answer. You will often
stop early: a plugin with no `write_*` calls has no hooks to trace, and lens 3b is a quick "none."

## Contents

- [The five-lens flow at a glance](#the-five-lens-flow-at-a-glance)
- [Lens 1 — entry point and declared support](#lens-1--entry-point-and-declared-support)
- [Lens 2 — the message-handler lifecycle](#lens-2--the-message-handler-lifecycle)
- [Lens 3 — engine touch-points](#lens-3--engine-touch-points)
- [Lens 4 — config and external deps](#lens-4--config-and-external-deps)
- [Lens 5 — inferring intent](#lens-5--inferring-intent)
- [The navigation backbone: Manager singletons](#the-navigation-backbone-manager-singletons)
- [Detecting the CommonLib lineage (the folder-name trap)](#detecting-the-commonlib-lineage-the-folder-name-trap)
- [A worked example: SPID mechanism by mechanism](#a-worked-example-spid-mechanism-by-mechanism)
- [The DLL-name → mod attribution seam (crash-diagnostics handoff)](#the-dll-name--mod-attribution-seam-crash-diagnostics-handoff)
- [Not yet verified in-game](#not-yet-verified-in-game)

The idioms below are anchored to `alandtse/CommonLibVR` branch `ng` (the current, charter-locked CommonLib
lineage) as the authority, worked on three production plugins: **SPID** (Spell Perk Item Distributor, the
primary example), **OAR** (Open Animation Replacer), and **po3 Papyrus Extender**. File:line citations point
into those repos to trace a surprising claim to source — treat them as pins, not gospel (repos move).

> **The `.psc` surface is not yours to declare.** Where a plugin registers native Papyrus functions, you
> learn from C++ only that the functions *exist* and where their native bodies live. The authoritative
> `.psc`-side signatures belong to houseCARL's `papyrus-reference` skill — never lift a Papyrus declaration
> out of a C++ header and state it as canonical.

---

## The five-lens flow at a glance

| # | Lens | Grep anchors | What you learn |
|---|------|-------------|----------------|
| 1 | **Entry point + declared support** | `SKSEPlugin_Load` / `SKSEPluginLoad`, `SKSEPlugin_Version`, `SKSEPlugin_Query`, `PluginVersionData`, `IsEditor`, `RuntimeVersion` | Plugin name/author; declared runtime support (SE/AE/VR); Address-Library use; editor refusal; version floor |
| 2 | **Lifecycle** | `RegisterListener`, `MessagingInterface::`, `a_message->type`, `HandleMessage` | The plugin's timeline: what it does at each engine phase |
| 3 | **Engine touch-points** | `BSTEventSink` / `ProcessEvent` / `AddEventSink` (passive); `write_call` / `write_branch` / `write_vfunc` / `install_hook` (active); `AllocTrampoline`; `GetPapyrusInterface` / `Register` / `Bind` (native funcs) | Which events it listens to, which functions it detours, what Papyrus surface it adds |
| 4 | **Config + external deps** | config suffix / `get_configs` / `CSimpleIniA` / `rapidjson`; `GetSerializationInterface` / `SetUniqueID`; `GetModuleHandle` / `RequestPluginAPI`; **`.gitmodules` + `CMakeLists.txt`** (lineage) | Config file grammar; co-save state; optional partner coupling; which CommonLib lineage it builds against |
| 5 | **Intent (synthesis)** | — (reason over 1–4) | What the mod actually does, in one sentence |

---

## Lens 1 — entry point and declared support

Grep `SKSEPlugin_Load` first — where the plugin begins. The body is a fixed shape: `InitializeLog();
SKSE::Init(a_skse); [SKSE::AllocTrampoline(n);] [register interfaces]; RegisterListener(...); return true;`

**Two export shapes coexist, and both are common.** The older explicit form is what all three exemplars use:

```cpp
extern "C" DLLEXPORT bool SKSEAPI SKSEPlugin_Load(const SKSE::LoadInterface* a_skse)   // spid main.cpp:179
```

The modern NG macro form expands to the exact same export — it's sugar, not a different mechanism:

```cpp
#define SKSEPluginLoad(...) SKSE_EXPORT bool SKSEPlugin_Load(__VA_ARGS__)   // alandtse-ng Interfaces.h:711
```

*Demonstrates: the entry-point symbol is the same either way — grep `SKSEPlugin_Load` and you catch both.*

**Declared runtime support is a separate export**, and this is the plugin's self-declared compatibility
contract. The AE/NG form is a `constinit SKSE::PluginVersionData`:

```cpp
// spid main.cpp:103-113 (inside #ifdef SKYRIM_AE)
extern "C" DLLEXPORT constinit auto SKSEPlugin_Version = []() {
    SKSE::PluginVersionData v;
    v.PluginVersion(Version::MAJOR);
    v.PluginName("Spell Perk Item Distributor");
    v.AuthorName("powerofthree");
    v.UsesAddressLibrary();                        // depends on Address Library
    v.UsesUpdatedStructs();                        // (OAR instead declares v.UsesNoStructs();)
    v.CompatibleVersions({ SKSE::RUNTIME_SSE_LATEST });
    return v;
}();
```

*Demonstrates: the version block names the plugin, its author, whether it needs Address Library, and which
runtimes it claims — read it as the "what am I compatible with" manifest.*

**The older SE/VR path is `SKSEPlugin_Query`**, which carries the editor guard and the version floor. In
SPID this is an `#ifdef` alternative to the AE block — one or the other compiles, never both:

```cpp
// spid main.cpp:121-136
if (a_skse->IsEditor()) {
    logger::critical("Loaded in editor, marking as incompatible"sv);
    return false;                                  // refuse to load in the Creation Kit
}
const auto ver = a_skse->RuntimeVersion();
if (ver < SKSE::RUNTIME_SSE_1_5_39) {              // version floor
    logger::critical(FMT_STRING("Unsupported runtime version {}"), ver.string());
    return false;
}
```

*Demonstrates: the `IsEditor()` refusal is a fast tell this is a game-runtime plugin, not a CK extension;
the `RuntimeVersion()` compare is the declared version floor.* All three exemplars carry the same
`IsEditor()` refusal.

**What to take away from lens 1:** "which runtimes does it claim, and would it refuse to load here?" is the
first question in any load-failure diagnosis (see `load-failures.md`). The `AddressLibrary` / `UsesNoStructs`
choice tells you whether the plugin depends on Address Library being installed. The multi-runtime `#ifdef`
scaffolding is the subject of `multi-runtime.md`.

---

## Lens 2 — the message-handler lifecycle

Grep `RegisterListener`. One call wires a single handler to the SKSE messaging bus:

```cpp
SKSE::GetMessagingInterface()->RegisterListener(MessageHandler);   // spid main.cpp:192
```

OAR passes a sender filter and checks the return, which is the more defensive form:

```cpp
if (!SKSE::GetMessagingInterface()->RegisterListener("SKSE", MessageHandler)) {   // oar main.cpp:133
    return false;
}
```

The handler is a `switch (a_message->type)`. Each case corresponds to an engine lifecycle point. The
conventional meanings:

| Message type | Conventional use | Exemplar |
|---|---|---|
| `kPostLoad` | install hooks; detect base deps | SPID reads INI + installs its load hook |
| `kPostPostLoad` | request **optional** other-plugin interfaces (all plugins are loaded by now) | SPID requests MergeMapper |
| `kInputLoaded` | input systems are up | OAR starts directory caching |
| `kDataLoaded` | forms exist → look them up, register event sinks | SPID looks up forms + sets up distribution; po3 registers its Papyrus + serialization |
| `kPreLoadGame` | read the save path / per-save state | SPID reads `savePath` from `a_message->data` |
| `kNewGame` / `kPostLoadGame` | reset per-game state | SPID marks a new game started |

The lifecycle detail — which phase is safe for what, and why `kDataLoaded` is the earliest point forms
exist — is the subject of `plugin-skeleton.md`. Here you're reading, not authoring; you just need to map
each case to "what does this plugin do at this phase."

**Read the whole handler, not just the switch.** "The switch cases *are* the timeline" under-counts: in
SPID, two managers are dispatched **outside** the switch — one before it, one after:

```cpp
// spid main.cpp
Outfits::Manager::GetSingleton()->HandleMessage(a_message);         // :23  — BEFORE the switch
switch (a_message->type) { /* ... the cases above ... */ }
DeathDistribution::Manager::GetSingleton()->HandleMessage(a_message); // :87  — AFTER the switch
```

*Demonstrates: delegated message handling can bracket the switch on both sides — an auditor who greps only
the switch body silently misses two managers.*

**Delegation order can be load-bearing**, and SPID says so in a comment:

```cpp
// spid main.cpp:20-23
// The order at which managers handle messages is important,
// since they need to register for events in specific order to work properly
// (e.g. Death event must be handled first by Death Manager, and then by Outfit Manager)
```

*Demonstrates: the physical dispatch order (Outfit at :23, Death at :87) is realizing a stated ordering
contract — treat it as a behavioral fact when you explain the plugin, not incidental code arrangement.*

---

## Lens 3 — engine touch-points

This is where a plugin actually reaches into the engine. Three distinct mechanisms; grep for each and
enumerate what you find. A plugin with only event sinks and no `write_*` / `AllocTrampoline` is event-only —
it observes the game but detours no code.

### 3a — passive: BSTEventSink

A manager class inherits the sink, overrides `ProcessEvent`, and registers itself. Grep `BSTEventSink` and
`AddEventSink` to enumerate every event the plugin listens to:

```cpp
struct Manager : public RE::BSTEventSink<RE::TESFormDeleteEvent> { ... };        // the sink declaration
scripts->AddEventSink<RE::TESFormDeleteEvent>(GetSingleton());                   // registration
RE::BSEventNotifyControl Manager::ProcessEvent(const RE::TESFormDeleteEvent*, ...) { ... }  // the handler
```

*Demonstrates: the `BSTEventSink<EventT>` template argument names the exact event; one class can override
`ProcessEvent` for several event types.* po3's EventHandler shows a common cluster —
`TESCellFullyLoadedEvent`, `TESQuestStartStopEvent`, `TESQuestStageEvent`, `TESObjectLoadedEvent`,
`TESGrabReleaseEvent`. The full event inventory and registration timing (you cannot register a sink before
the event source exists) is the subject of `event-sinks.md`.

### 3b — active: trampoline and vtable hooks

Two primitives, defined in the authority: `write_call<N>` / `write_branch<N>`
(`alandtse-ng Trampoline.h:107` / `:82`) rewrite a call site to detour into your code; `write_vfunc<Class,
index>` swaps a virtual-table slot. The usual hook-struct shape is a static `thunk()` that does its work and
then calls the saved original:

```cpp
// spid DistributeManager.cpp:28-48, installed at :79
struct ShouldBackgroundClone {
    static bool thunk(RE::Character* actor) { /* our work */ return func(actor); }  // calls the original
    static inline REL::Relocation<decltype(thunk)> func;                            // holds the original
};
stl::install_hook<ShouldBackgroundClone>();
```

*Demonstrates: the canonical hook idiom — a static `thunk` plus a static `REL::Relocation func` that stores
the address of the code you displaced, so you can call through to it. The static-`func` requirement pairs
with the singleton pattern (see the navigation backbone below).*

Direct vtable and thunk-call forms look like this in po3:

```cpp
stl::write_vfunc<RE::ReanimateEffect, 0x14, Start>();                              // vtable-slot swap
REL::Relocation<std::uintptr_t> target{ RELOCATION_ID(41659, 42742), OFFSET(0x526, 0x67B) };
stl::write_thunk_call<CalculateDetection>(target.address());                       // patch a call site
```

*Demonstrates: the `RELOCATION_ID(seID, aeID)` / `REL::VariantID` / vtable index **names the engine function
being patched** — record it, because it tells you exactly what game behavior the plugin is intercepting.*

The full machinery — the REL Address-Library layer, the Trampoline, and each hook flavor — is the subject of
`hooking.md`. For dissection you only need to *find* the targets and note what they patch.

**The trampoline budget is a hook-count tell.** `SKSE::AllocTrampoline(bytes)` in `SKSEPlugin_Load`
pre-reserves detour space; a comment often documents the per-hook cost, so the number hints at how many
hooks the plugin installs:

```cpp
// spid main.cpp:187-190
// Each write_call<5> hook needs 14 bytes (FF 25 00000000 + 8-byte absolute address).
SKSE::AllocTrampoline(14 * 14);
```

*Demonstrates: a large `AllocTrampoline` reservation signals an actively-hooking plugin; no `AllocTrampoline`
plus only `BSTEventSink`s reads as event-only.* Treat "event-only" as a sound heuristic, not a proof — all
three exemplars install hooks, so it isn't demonstrated here from the negative side.

### 3c — native Papyrus functions

Grep `GetPapyrusInterface` to detect a scripting-extension surface:

```cpp
// po3 main.cpp:107-108
const auto papyrus = SKSE::GetPapyrusInterface();
papyrus->Register(Papyrus::Bind);
// Bind(VM* a_vm) fans out to per-object helpers, each calling a_vm->RegisterFunction(...):
// bool Bind(VM* a_vm) { ObjectTypes::Bind(*a_vm); Actor::Bind(*a_vm); ArmorAddon::Bind(*a_vm); ... }
```

*Demonstrates: `GetPapyrusInterface()->Register(Bind)` is the whole scripting-extension tell; the per-object
`Bind` helpers are where the individual functions are registered.* The marshalling rules and how to author
these are the subject of `native-papyrus-functions.md`.

**Boundary (repeat):** from C++ you learn only that the functions exist and where their native bodies live.
The canonical `.psc` declarations are owned by `papyrus-reference`. Do not reproduce a Papyrus signature from
a C++ header and present it as authoritative.

---

## Lens 4 — config and external deps

### Config discovery and format

Distributor-style plugins glob the Data folder for a filename suffix, then parse each match — the suffix plus the parser tells you the grammar the mod's users author against:

```cpp
// spid LookupConfigs.cpp:94, :108
std::vector<std::string> files = distribution::get_configs(R"(Data\)", "_DISTR"sv);   // the suffix
CSimpleIniA ini; ini.SetUnicode(); ini.SetMultiKey(); /* ... */ ini.LoadFile(path.c_str());
```

*Demonstrates: `get_configs(..., "_DISTR")` + `CSimpleIniA` means "users author `*_DISTR.ini` in INI grammar"
— which is exactly what houseCARL's `spid-authoring` skill owns.* JSON/CSV-config plugins pull
`rapidjson` / `rapidcsv` / `simpleini` as vcpkg deps instead; the parser names the format.

### Co-save state serialization

```cpp
// po3 main.cpp:110-115
const auto serialization = SKSE::GetSerializationInterface();
serialization->SetUniqueID(Serialization::kPapyrusExtender);
serialization->SetSaveCallback(...); serialization->SetLoadCallback(...);
serialization->SetRevertCallback(...); serialization->SetFormDeleteCallback(...);
```

*Demonstrates: presence of `GetSerializationInterface` + `SetSaveCallback` means the plugin writes
save-game-scoped runtime state (a co-save); absence means it's stateless across saves.* The serialization
plumbing and runtime FormID handling are the subject of `threading-and-persistence.md`.

### External plugin coupling — two directions

**Consumes (optional).** Two guarded shapes, both written so the plugin degrades gracefully when the partner
is absent:

- Hard-presence check — `GetModuleHandle("Other.dll")`. SPID uses it to detect `po3_Tweaks` for logging;
  OAR uses it as a *conflict guard*, refusing to load if `DynamicAnimationReplacer.dll` is present.
- Typed interface request — `Get<X>Interface()` / `RequestPluginAPI(...)`, always null-checked (SPID
  requests MergeMapper; OAR requests TrueHUD).

**Exposes (framework).** The mirror image tells you the plugin is a *framework other mods build on*. It
exports its own `RequestPluginAPI_<X>` C function that returns a version-gated interface singleton:

```cpp
// oar main.cpp:146-165
extern "C" DLLEXPORT OAR_API::Animations::IAnimationsInterface* SKSEAPI
RequestPluginAPI_Animations(const OAR_API::Animations::InterfaceVersion a_interfaceVersion, ...) {
    const auto api = OAR_API::Animations::AnimationsInterface::GetSingleton();
    switch (a_interfaceVersion) {
        case OAR_API::Animations::InterfaceVersion::V1: return api;
    }
    return nullptr;                                 // wrong/outdated version denied
}
```

*Demonstrates: finding `RequestPluginAPI_*` **exports** (not requests) means this plugin is a framework; the
`InterfaceVersion` switch is how it serves or denies callers by version.* OAR exports four such interfaces.

### Lineage

The last thing lens 4 pins is which CommonLib lineage the plugin builds against — and this has a real trap.
It gets its own section below.

---

## Lens 5 — inferring intent

Intent is inferred, never stated. Combine three facts: the records/forms the plugin looks up and mutates,
the lifecycle points it acts at (lens 2), and the engine surfaces it touches (lens 3). The three exemplars
are three clean archetypes you'll recognize again and again:

- **Runtime distributor (SPID).** At `kDataLoaded` it looks up forms, installs a load hook, and adds
  keywords/spells to NPCs → **distributes forms at runtime with no ESP.**
- **Animation replacer (OAR).** No forms; a directory-cache scan plus dozens of Havok / animation-graph
  hooks → **replaces animations by condition.**
- **Scripting-extension library (po3 Papyrus Extender).** Only `GetPapyrusInterface()->Register` plus
  serialization callbacks, no Data-folder config glob → **adds native Papyrus functions and events.**

Match the plugin you're reading to the closest archetype, then refine with the specifics you gathered.

---

## The navigation backbone: Manager singletons

Plugin functional units are almost always **singleton `Manager` classes** reached via `GetSingleton()`, and
the entry-point handler delegates to them. To trace any capability: find its Manager class, read its
`Register()` (which sinks and hooks it installs) and its `ProcessEvent` / `thunk` bodies. `Register()` is the
canonical install site — `void Register() { Event::Register(); DETECTION::Register(); }`.

This pattern and the static-`func` hook member (lens 3b) go together for a concrete reason: singletons that
hold engine addresses must use a `static REL::Relocation` — the authority states it plainly, "REL::Relocation
Must Be Static" (`alandtse-ng CLAUDE.md:432`). The Manager singleton is where that static state lives.

**Log anchor.** `InitializeLog()` runs first in `SKSEPlugin_Load`, setting the plugin's log to
`SKSE::log::log_directory() / <ProjectName>.log` — the name that maps a `.log` in
`Documents/My Games/Skyrim Special Edition/SKSE/` back to *this* plugin (the first move in a "plugin X isn't
working" investigation). In-source `LOG_HEADER("…")` markers (`"DEPENDENCIES"`, `"HOOKS"`, `"INI"`,
`"EVENTS"`) double as a phase table-of-contents in that log.

---

## Detecting the CommonLib lineage (the folder-name trap)

The naive instruction "identify the CommonLib lineage from `vcpkg.json`" is **wrong for the worked exemplar**
and makes you conclude a real SKSE plugin "isn't one." SPID's `vcpkg.json` names **no CommonLib at all** — it
comes in as a **git submodule** via `add_subdirectory`, and never appears in the consumer's `vcpkg.json`
across this corpus. Read lineage in this priority order:

**1 — `.gitmodules` (the submodule URL and branch): the primary signal.**

- `url = …/powerof3/CommonLibSSE` → powerof3 lineage (no VR, no VariantID).
- `url = …/alandtse/CommonLibVR` (branch `ng`) → alandtse NG lineage — the live lineage, VR + VariantID.
- **Read the URL, never the folder name.** A submodule *path* named `extern/CommonLibSSE` can point at
  `alandtse/CommonLibVR.git`. OAR does exactly this: its submodule directory is `CommonLibSSE` but its URL is
  the alandtse NG fork. **The folder name lies; the URL is the truth.**

**2 — `CMakeLists.txt`: which submodule actually compiles.** When `.gitmodules` lists more than one candidate
(the legacy multi-target plugins list both lineages), the CMake build option disambiguates:
`set(CommonLibName "CommonLibSSE"/"CommonLibVR")` gated on `BUILD_SKYRIMAE` / `BUILD_SKYRIMVR`, then
`add_subdirectory(...)` and `target_link_libraries(... ${CommonLibName}::${CommonLibName})`.

**3 — `vcpkg.json`: only in the rare vcpkg-port pattern.** A CommonLib-less `vcpkg.json` means "look
elsewhere — go read `.gitmodules`," **not** "no CommonLib."

| Pattern | Exemplar | Where lineage lives | Trap |
|---|---|---|---|
| Legacy multi-target (both lineages as submodules; CMake picks one) | SPID, po3 Papyrus Extender | `.gitmodules` (2 entries) + CMake `CommonLibName` | `vcpkg.json` shows nothing; `.gitmodules` shows two — the CMake option disambiguates |
| Modern single-submodule NG | OAR | `.gitmodules` (1 entry, URL = `alandtse/CommonLibVR@ng`) | Submodule *directory* is `CommonLibSSE` but the URL is the NG fork — folder name lies |
| CommonLib-as-vcpkg-port | (none in corpus) | consumer `vcpkg.json` names `commonlibsse-ng` | Not demonstrated here — don't assert it as a common path |
| Modern packaged NG (`add_commonlibsse_plugin`) | (authority only) | `find_package(CommonLibSSE)` + `add_commonlibsse_plugin(...)` | The idiom the library documents for consumers; none of the three exemplars use it |

**Lineage caution.** The powerof3 `dev` lineage has no VR and no VariantID — never generalize a VR/VariantID
idiom from a flatrim-only build. The alandtse NG lineage defines `ENABLE_SKYRIM_SE/AE/VR` and supports
one-DLL multi-runtime (see `multi-runtime.md`); it is the charter-locked target. Read lineage accurately —
this is not a recommendation to switch a plugin's lineage. (A newer libxse/commonlibsse line and the frozen
CharmedBaryon fork also exist — recognize them, don't build on them; when in doubt the alandtse NG code is
the authority, and any wiki prose must be re-checked against it.)

---

## A worked example: SPID mechanism by mechanism

SPID (Spell Perk Item Distributor) is the cleanest single tour of the flow, because it uses most of the
mechanisms at once. Run the lenses:

- **Lens 1 — entry/declaration.** `SKSEPlugin_Load` (`main.cpp:179`) inits logging, calls `SKSE::Init`,
  reserves `AllocTrampoline(14 * 14)`, registers `MessageHandler`. The AE `PluginVersionData` names it "Spell
  Perk Item Distributor" by "powerofthree", declares `UsesAddressLibrary()` + `UsesUpdatedStructs()`, claims
  `RUNTIME_SSE_LATEST`; the SE/VR `#else` carries the `IsEditor()` refusal and a `RUNTIME_SSE_1_5_39` floor.
  → *A game-runtime, Address-Library, multi-runtime plugin.*
- **Lens 2 — lifecycle.** The handler brackets its switch with two delegated managers (Outfit before, Death
  after — order documented as load-bearing): `kPostLoad` reads INI + installs a distribution hook,
  `kPostPostLoad` requests MergeMapper (optional), `kDataLoaded` looks up forms + sets up distribution,
  `kPreLoadGame`/`kNewGame` handle per-save state. → *Real work lands at `kDataLoaded`, once forms exist.*
- **Lens 3 — touch-points.** Passive: a `BSTEventSink<TESFormDeleteEvent>` manager. Active: the
  `ShouldBackgroundClone` load hook via `install_hook` (static-`thunk`/static-`func`). No
  `GetPapyrusInterface`. → *Distributes on actor load via a hook, cleans up on form-delete via a sink.*
- **Lens 4 — config + deps.** `get_configs(Data\, "_DISTR")` + `CSimpleIniA`, plus a fixed settings INI
  (`po3_SpellPerkItemDistributor.ini`). Consumes MergeMapper (optional); no serialization → stateless across
  saves. Lineage: `vcpkg.json` names no CommonLib, `.gitmodules` lists both powerof3 and alandtse, CMake
  `CommonLibName` picks one per target. → *Users author `*_DISTR.ini`; a legacy multi-target build.*
- **Lens 5 — intent.** Looks up forms → hooks actor load → adds keywords/spells/items to NPCs, driven by
  user `*_DISTR.ini` with no ESP. → **A runtime distributor.**

### Mechanism → where SPID uses it → sibling reference

| Mechanism | Where SPID uses it | Reference that owns it |
|---|---|---|
| Entry point + version declaration | `SKSEPlugin_Load` / `PluginVersionData` / `SKSEPlugin_Query` in `main.cpp` | `plugin-skeleton.md` |
| Editor guard + version floor | `IsEditor()` + `RUNTIME_SSE_1_5_39` in the `SKSEPlugin_Query` path | `load-failures.md` |
| Message-handler lifecycle | `RegisterListener(MessageHandler)` + the `kDataLoaded` switch case | `plugin-skeleton.md` |
| BSTEventSink (passive) | `BSTEventSink<TESFormDeleteEvent>` manager | `event-sinks.md` |
| Trampoline / call hook (active) | `ShouldBackgroundClone` via `install_hook` + `AllocTrampoline` | `hooking.md` |
| Multi-runtime `#ifdef` scaffolding | `SKYRIM_AE` vs SE/VR export split | `multi-runtime.md` |
| Config discovery + format | `get_configs(..., "_DISTR")` + `CSimpleIniA` | (the `spid-authoring` skill owns the `.ini` grammar) |
| CommonLib lineage detection | `.gitmodules` (two entries) + CMake `CommonLibName` | this file (lineage section) |

SPID registers no native Papyrus functions and no serialization co-save, so `native-papyrus-functions.md`
and the co-save half of `threading-and-persistence.md` have no SPID example — read po3 Papyrus Extender there.

---

## The DLL-name → mod attribution seam (crash-diagnostics handoff)

Dissection produces exactly the artifacts a crash investigation needs, but **the triage flow itself belongs
to the future crash-diagnostics skill.** This section records the seam so that skill can pick it up; it is
not a triage procedure.

**The load-bearing fact: the SKSE loader keys each plugin by its DLL filename.** The loader scans the plugin
directory for `*.dll` and records the bare filename as the plugin's recorded identity — the string it logs
and matches plugins by (`skse64 PluginManager.cpp` around `:363`–`:370`). The in-memory *handle* passed to a
plugin is a numeric token, not the filename; "keyed by filename" is the identity the loader logs and a
crash-log reader sees, not the runtime handle.

Two comprehension cautions this creates, both of which a triage flow must respect:

- **DLL name → mod is an attribution step, not a given.** A plugin's DLL filename is its loader identity and
  the string a crashlog frame or SKSE loader message attributes a fault to — but mapping that DLL back to the
  *mod* (and to the source you'd dissect) is its own lookup. The `.log`-name → plugin map from the log anchor
  above and the DLL-name identity here are the two halves of that attribution.
- **"In a crash frame" ≠ "the culprit."** A DLL appearing in a crash frame means its code was on the stack
  when the fault hit — not that it caused the fault. A hook (lens 3b) puts a plugin's code directly in the
  execution path of a game function, so an unrelated fault in that function can surface a hooking plugin's
  name. The plugin's engine touch-points (lens 3) tell you *where* a fault it genuinely caused would
  manifest; whether *this* crash is that is a triage judgment the crash-diagnostics skill makes.

So "which plugin, and where in it" composes from lens-1 identity + the log anchor + lens-3 touch-points —
the comprehension debt this reference pays forward. The triage *reasoning* over a real crashlog is out of
scope here by design (crash-diagnostics ships with its own tool surface).

---

## Not yet verified in-game

Everything above is derived from reading source. A handful of behaviors are **conventions consistent across
all three exemplars and widely-held community practice, but not yet confirmed by an empirical build-and-run**.
Treat them as reliable working assumptions, not proven facts — and never present them to a user as proven:

- **SKSE message timing/ordering.** That `kDataLoaded` truly fires after all forms exist, and `kPostPostLoad`
  truly follows every plugin's `kPostLoad`, is documented here as convention. It was not traced into the SKSE
  loader dispatch source, so exact engine-guaranteed timing is unconfirmed.
- **Declared compatibility actually matches the runtime.** Whether a `CompatibleVersions` / version-floor
  declaration means the DLL actually loads (rather than being rejected as incompatible) is only observable by
  running the built DLL against a specific game build. This is the core of `load-failures.md`.
- **Per-runtime hook correctness.** Static reading finds the hook targets, but cannot confirm the
  `RELOCATION_ID` / `VariantID` / vtable index resolves correctly on SE *and* AE *and* VR — that's an
  empirical build/run gate.
- **Log path and level.** Whether the log lands at `<ProjectName>.log` and honors the INI `LogLevel` is a
  runtime observation, not a static fact.
- **Two corpus gaps.** No fully **NG-native worked exemplar** (`SKSEPluginLoad` + `add_commonlibsse_plugin`)
  exists in the three studied plugins — that idiom is confirmed only in the alandtse NG headers / `CLAUDE.md`,
  not from a production consumer; and the **CommonLib-as-vcpkg-port** lineage is asserted from the producer
  manifest alone, no consumer exemplar. Where you rely on either, say so.
