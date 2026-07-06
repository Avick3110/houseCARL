# Plugin skeleton — declaration, the SKSE lifecycle, init, and logging

The runtime spine of an SKSE plugin: how the DLL declares itself so the loader accepts it, how it wires
up inside `SKSEPlugin_Load`, and how the game hands it a fixed sequence of lifecycle messages that gate
what game state is legal to touch. Read this before writing (or auditing) a plugin's entry points — the
ordering rules here are load-bearing and several are counter-intuitive.

The declaration and init idioms below target **CommonLibSSE-NG** — alandtse/CommonLibVR branch `ng`, the
current lineage. Two other lineages appear in the wild and diverge in ways that matter (see "Lineage
divergence"); this reference teaches the `ng` shapes and flags the others so you recognize them, not so
you switch.

Where a plugin registers Papyrus native functions, this reference names the C++-side registration call
and stops. The `.psc` signature surface — what a Papyrus function's parameters and return type are — is
owned by houseCARL's `papyrus-reference` skill; do not treat any Papyrus signature quoted here as
authoritative. Setting up native functions in depth is `native-papyrus-functions.md`.

## Contents

1. Declaring the plugin — the export triad and the declarative macro
2. `SKSE::Init` and the interface surface
3. The canonical load skeleton (order matters)
4. Logging setup
5. The messaging lifecycle — nine messages and when each game state is legal
6. The no-replay rule (load-bearing)
7. Lineage divergence
8. Not yet verified in-game

---

## 1. Declaring the plugin — the export triad and the declarative macro

SKSE has **two loader generations**, and a DLL that loads on every runtime must satisfy both. The
difference is whether the loader *runs* your code to decide compatibility:

- **AE-era SKSE** (since 2.1.2; current 2.2.6) maps the DLL as a resource, reads an exported **data
  blob** `SKSEPlugin_Version`, validates it without executing anything, and only then loads the DLL and
  calls `SKSEPlugin_Load`.
- **SE 1.5.x and VR SKSE** (the Query era) instead call an exported **function** `SKSEPlugin_Query` and
  let it fill in a `PluginInfo` and return true/false to accept or refuse.

So a portable plugin exports a **triad**: `SKSEPlugin_Version` (the AE data path), `SKSEPlugin_Query`
(the SE/VR function path), and `SKSEPlugin_Load` (the real entry point, called on both). On an AE runtime
a Query-only plugin is silently skipped with `no version data` in the log — it does not even appear in the
error dialog. **The VR loader still needs the Query+Load pair**; if you drop the Query function, VR never
sees the plugin.

The loader resolves all three symbols by **exact unmangled name string**, so every export must be
`extern "C" __declspec(dllexport)`. NG wraps that as `SKSE_EXPORT`.

### The declarative path (the default to teach)

One macro emits the whole triad. This is what you write for a new NG plugin:

```cpp
#include <SKSE/SKSE.h>
using namespace REL::literals;

SKSEPluginInfo(
    .Version = "1.0.0.0"_v,               // or { 1, 0, 0, 0 }
    .Name = "My Plugin",
    .Author = "Jane Doe",                  // optional
    .SupportEmail = "jane@example.com",    // optional
    .StructCompatibility = SKSE::StructCompatibility::Independent,        // default
    .RuntimeCompatibility = SKSE::VersionIndependence::AddressLibrary     // default
    // exact-version lane instead: .RuntimeCompatibility = { "1.5.97.0"_v, "1.6.353.0"_v }
    // .MinimumSKSEVersion = 0  (default; leave it 0)
)

SKSEPluginLoad(const SKSE::LoadInterface* skse) {   // expands to SKSEPlugin_Load
    SKSE::Init(skse);
    return true;   // false → SKSE logs "reported as incompatible during load" and unloads you
}
```

`SKSEPluginInfo` demonstrates the one-macro-to-both-symbols shortcut: it emits the `SKSEPlugin_Version`
data blob **and** a generated `SKSEPlugin_Query` that returns `true` unconditionally.

**Consequence of the always-true generated Query:** an SE-era plugin used to refuse the Creation Kit or a
too-old runtime *inside* `SKSEPlugin_Query`. With the NG macro that Query always accepts, so **those
checks must move into `SKSEPlugin_Load`** — call `skse->IsEditor()` / `skse->RuntimeVersion()` there and
`return false` to refuse. Designated initializers must keep field declaration order.

The compatibility fields are literal promises the loader enforces, not documentation. The defaults
above — Address Library plus struct-independence — are what make one DLL loadable across SE, all AE
runtimes, and VR. The full promise→enforcement mapping (and the failure line each broken promise
produces) lives in `load-failures.md`; the multi-runtime footguns are in `multi-runtime.md`.

### The hand-written export triad (auditing older / production plugins)

None of the big production plugins use the macro — SPID, OAR, and po3-PapyrusExtender all hand-write the
triad in `main.cpp`. When you read one, expect this shape (NG-lineage, from `oar/src/main.cpp:81-112`):

```cpp
extern "C" DLLEXPORT constinit auto SKSEPlugin_Version = []() {
    SKSE::PluginVersionData v;
    v.PluginVersion(Plugin::VERSION); v.PluginName(Plugin::NAME); v.AuthorName("Ersh");
    v.UsesAddressLibrary();
    v.CompatibleVersions({ SKSE::RUNTIME_SSE_LATEST });  // harmless alongside the independence flag
    v.UsesNoStructs();
    return v;
}();
extern "C" DLLEXPORT bool SKSEAPI SKSEPlugin_Query(const SKSE::QueryInterface* a_skse, SKSE::PluginInfo* a_info) {
    a_info->infoVersion = SKSE::PluginInfo::kVersion;
    a_info->name = Plugin::NAME.data();
    a_info->version = Plugin::VERSION.pack();
    if (a_skse->IsEditor()) { return false; }   // CK refusal lives HERE on NG — the macro's Query can't do it
    return true;
}
```

This demonstrates the hand-rolled equivalent of the macro: the same two symbols, with the editor refusal
placed explicitly in the Query where an older plugin expects it.

Prefer the intent-revealing alias `SKSE::RUNTIME_SSE_LATEST` for the runtime constant (both OAR and
po3-PapyrusExtender write it). There is **no bare `RUNTIME_LATEST`** in `ng` — some templates miswrite it
and would fail to compile if that arm were selected. If you see `RUNTIME_LATEST` in a template's AE arm,
it's a defect, not an idiom.

There is also a rare **third** export, `SKSEPlugin_Preload`, detected at scan time and called in an
earlier phase than Load. NG ships no wrapper for it — a plugin that needs it hand-exports the function.
Most plugins never do.

---

## 2. `SKSE::Init` and the interface surface

`SKSE::Init(skse)` is a one-shot, mutex-guarded capture that **must run inside `SKSEPlugin_Load`**, not
later. In order it: fail-fasts if the interface pointer is null; **eagerly forces the Address Library
database to initialize** (every version-mismatch abort fires here — see `load-failures.md`); captures the
plugin handle; queries every SKSE interface once into a static singleton; and runs any queued
init-event callbacks.

**Why it must run during Load, not deferred:** SKSE only makes this plugin's handle valid *during* the
load call and resets it to an invalid value afterward. Init captures that handle and every later
Serialization / Messaging / Trampoline proxy call reuses it automatically. Defer Init and you capture the
wrong handle; before Init, the interface accessors return null.

### The interface inventory (valid only after Init)

| Accessor (`SKSE::…`) | What it gives you |
|---|---|
| `GetPluginHandle()` / `GetReleaseIndex()` | this plugin's handle; the SKSE build's release index |
| `GetMessagingInterface()` | `RegisterListener(cb)` (defaults sender `"SKSE"`), `RegisterListener(sender, cb)`, `Dispatch(...)`, `GetEventDispatcher(...)` — the lifecycle-message and plugin-to-plugin channel (§5) |
| `GetPapyrusInterface()` | `Register(fn…)` of native-registration callbacks; runs the callback immediately if the VM already exists, else queues it — so it works from Load or later. `.psc` side is `papyrus-reference`; C++ side is `native-papyrus-functions.md` |
| `GetSerializationInterface()` | the co-save API: `SetUniqueID`, the Save/Load/Revert/FormDelete callbacks, record read/write, and `ResolveFormID` / `ResolveHandle` for remapping after a load-order change. Depth: `threading-and-persistence.md` |
| `GetTaskInterface()` | `AddTask` / `AddUITask` — marshal work back to a game thread. Depth: `threading-and-persistence.md` |
| `GetTrampolineInterface()` / `AllocTrampoline(size)` / `GetTrampoline()` | SKSE's shared branch/local pools for hooks. Depth: `hooking.md` |
| `GetScaleformInterface()` | inject into Scaleform (UI) movies |
| the five `Get…EventSource()` accessors | typed `BSTEventSource`s — mod-callback (Papyrus `SendModEvent`) traffic, camera transitions, crosshair-ref changes, action events, node updates. Depth: `event-sinks.md` |
| `GetObjectInterface()` + the three object-manager accessors | SKSE-side Papyrus object-lifetime managers |
| `RegisterForAPIInitEvent(fn)` | run `fn` at the end of Init (or immediately if Init already ran) |

The accessors are null-checkable, never throwing. If SKSE's interface version is newer than the header's,
NG **warns** `interface definition is out of date` but still returns the pointer — interface skew degrades,
it does not refuse.

Pre-flight checks (`IsEditor()`, `RuntimeVersion()`, `SKSEVersion()`) live on the `LoadInterface` you
receive in `SKSEPlugin_Load` — this is where NG plugins do the refusals an SE plugin did in Query.
`LoadInterface::GetPluginInfo(name)` (detect another loaded plugin) is only valid *after* the PostLoad
message, so it belongs in a message handler, not the Load body.

---

## 3. The canonical load skeleton (order matters)

The composite every production plugin follows, each step in its required position:

```cpp
extern "C" DLLEXPORT bool SKSEAPI SKSEPlugin_Load(const SKSE::LoadInterface* a_skse)
{
    InitializeLog();                                        // FIRST — before Init (see §4)

    SKSE::Init(a_skse, false);                              // false = we own the logger (SPID, po3)

    SKSE::AllocTrampoline(14 * numberOfWriteCall5Hooks);    // once, up front, before any hook

    SKSE::GetMessagingInterface()->RegisterListener(MessageHandler);

    // only if the plugin needs them:
    SKSE::GetPapyrusInterface()->Register(Papyrus::Bind);
    const auto ser = SKSE::GetSerializationInterface();
    ser->SetUniqueID('MYID');
    ser->SetSaveCallback(SaveCB);  ser->SetLoadCallback(LoadCB);
    ser->SetRevertCallback(RevertCB);  ser->SetFormDeleteCallback(FormDeleteCB);

    return true;   // false → SKSE logs "reported as incompatible during load", unloads the plugin
}
```

This demonstrates the invariant sequence: **logging → Init → trampoline → register listener → optional
Papyrus/serialization registration → return true.** Reversing the messaging line ahead of Init
deref-crashes, because `GetMessagingInterface()` returns null until Init has run.

`AllocTrampoline` is called **exactly once**, before installing any hook, sized at **14 bytes per
5-byte call/branch hook**. Size it for *all* your hooks up front — exhausting the allocation later
aborts the process with `Failed to handle allocation request`. Trampoline detail is in `hooking.md`.

### The load-vs-message split — the discipline the whole lifecycle turns on

At `SKSEPlugin_Load`, **no game data exists yet.** What is safe here vs what must wait for a message:

- **Inside `SKSEPlugin_Load`:** logging init, `SKSE::Init`, trampoline allocation, message-listener /
  Papyrus / serialization / Scaleform registration, reading your own INI, checking for co-installed DLLs
  via `GetModuleHandle`, and installing hooks on **code** addresses. All of these touch only your own
  DLL and the process image — no forms, no `TESDataHandler`.
- **Deferred to messages:** *every* `TESForm` lookup and every data-dependent decision waits for
  `kDataLoaded`; acquiring another plugin's API waits for `kPostLoad` / `kPostPostLoad`.

All three production exemplars sequence identically by phase: **Load** = log + Init + trampoline +
register; **kPostLoad** = install your hooks / register your event sinks / detect co-installed DLLs;
**kPostPostLoad** = query *other* plugins' interfaces; **kDataLoaded** = form lookups and distribution;
the per-save messages = state resets. Event-sink registration timing is expanded in `event-sinks.md`.

---

## 4. Logging setup

Every exemplar hand-rolls the same spdlog idiom, and it runs **first in `SKSEPlugin_Load`, before
`SKSE::Init`**:

```cpp
void InitializeLog() {
    auto path = SKSE::log::log_directory();               // std::optional<fs::path>
    if (!path) stl::report_and_fail("Failed to find standard logging directory"sv);
    *path /= std::format("{}.log", Version::PROJECT);     // …/SKSE/<Plugin>.log
    auto sink = std::make_shared<spdlog::sinks::basic_file_sink_mt>(path->string(), true); // truncate
    auto log  = std::make_shared<spdlog::logger>("global log"s, std::move(sink));
    log->set_level(spdlog::level::info);  log->flush_on(spdlog::level::info);
    spdlog::set_default_logger(std::move(log));            // SKSE::log::* AND CommonLib internals route here
    spdlog::set_pattern("[%H:%M:%S:%e] %v"s);
    logger::info("{} v{}", Version::PROJECT, Version::NAME);
}
```

This demonstrates the portable logging setup and why it goes first. `log_directory()` resolves the
correct per-edition folder under `Documents\My Games\…\SKSE\` (SE vs VR vs GOG — the exact discrimination
is in `multi-runtime.md`) and returns `std::nullopt` on failure rather than throwing. Because you install
the **default** spdlog logger, `SKSE::log::{info,warn,error,…}` capture both your own lines and
CommonLib's internal diagnostics (the interface-out-of-date warning, trampoline stats).

**The `a_log` footgun.** Pass `false` when you manage your own logger: `SKSE::Init(skse)` with the
default `a_log = true` runs CommonLib's built-in `log::init()` on AE builds, which **truncates**
`<PluginName>.log` and **replaces** your default logger — losing every line you logged before Init.
SPID and po3 pass `false` to avoid this; OAR exhibits the collision. This is exactly why logging goes
first *and* Init is told `false`.

---

## 5. The messaging lifecycle — nine messages and when each game state is legal

After `SKSEPlugin_Load`, the game hands every plugin a fixed sequence of nine lifecycle messages. Each
one marks a transition in what game state exists, so **which message you handle *is* your statement about
what's legal to touch.** The enum values 0–8 are ABI-stable.

| # | `MessagingInterface::` | When it fires | Game state legal here | Payload |
|---|---|---|---|---|
| 0 | `kPostLoad` | after **every** plugin's `SKSEPlugin_Load` ran — still in CRT startup, before the game's `main()` | your own image; register listeners for *other* plugins | none |
| 1 | `kPostPostLoad` | immediately after kPostLoad, back-to-back | same; **the phase for querying other plugins' interfaces** | none |
| 2 | `kPreLoadGame` | just **before** the engine reads a savegame | pre-load state reset | `char*` `.ess` path / `strlen` |
| 3 | `kPostLoadGame` | after the load attempt finishes — **fires on FAILED loads too** | post-load fix-ups; reset on a failed load | success bool **cast into the pointer value** / len 1 |
| 4 | `kSaveGame` | **before** the engine writes the save | flush your co-save state | `char*` name / `strlen` |
| 5 | `kDeleteGame` | just before the `.ess` + `.skse` cosave are deleted | clean up per-save files | `char*` `.ess` path / `strlen` |
| 6 | `kInputLoaded` | after input init, just before the main menu initializes | input systems up; **forms not yet safe** | none |
| 7 | `kNewGame` | after a new game is created, before it has loaded | new-game state init | chargen `TESQuest*` / `sizeof(void*)` |
| 8 | `kDataLoaded` | after the data handler loaded **all forms** from every ESM/ESL/ESP | **the earliest legal point for any form lookup** | none |

**Startup order:** `SKSEPlugin_Load` (all plugins) → `kPostLoad` → `kPostPostLoad` → *(game `main()`
starts)* → `kInputLoaded` → `kDataLoaded`; then per player action `kNewGame`, or
`kPreLoadGame` → `kPostLoadGame`; save/delete as they occur.

**`kDataLoaded` is the form gate.** Any `TESForm` / `TESDataHandler` access before it returns incomplete
or null data. This is the single most-consulted rule in the lifecycle: form lookups and distribution go
here, never in the Load body.

**`kPostLoad` / `kPostPostLoad` are the plugin bring-up window.** They dispatch back-to-back from the
loader itself right after every `SKSEPlugin_Load` ran. The two-phase design is deliberate: **register**
your listeners during kPostLoad, **defer any Dispatch to other plugins** to kPostPostLoad, so everyone
has registered before anyone starts talking. All three exemplars do inter-plugin interface exchange at
kPostPostLoad.

### Payload handling gotchas (also feeds crash triage)

- **`kPostLoadGame`**: the payload **is** the success bool cast into the pointer value, **not** a pointer
  to a bool — test `msg->data != nullptr`, never dereference it. It fires even on failed loads, so
  reset your state here after a corrupt-save failure.
- **`kPreLoadGame` / `kDeleteGame` / `kSaveGame`**: `data` is an engine-owned `char*` valid **only for
  the synchronous callback** — copy it immediately, e.g.
  `std::string savePath{ static_cast<char*>(msg->data), msg->dataLen };`.
- **`kNewGame`**: `data` is the chargen `TESQuest*`.

**Make one-time work idempotent.** The per-save messages fire once per save action by design, and whether
`kDataLoaded` can ever re-fire is an open question (§8) — but a handler is cheap to make re-fire-proof and
expensive to debug when a message arrives more often than you assumed. Guard anything that must happen
exactly once — hook installs above all (a double `write_call` chains a hook into itself) — with a
`std::once_flag` or a static bool. `AddEventSink` needs no guard; it dedups (`event-sinks.md`). And per-save
state resets belong in the per-save messages, never mixed into the once-only path.

### The register idiom and its two traps

Inside `SKSEPlugin_Load`, after Init:
`SKSE::GetMessagingInterface()->RegisterListener(MessageHandler)`. The one-argument form is exactly
`RegisterListener("SKSE", callback)`, so it subscribes to the nine lifecycle messages.

- **Never pass a null sender.** `RegisterListener(nullptr, cb)` on the raw interface subscribes to every
  loaded plugin *except SKSE and except yourself* — so it silently **never** delivers the lifecycle
  messages. Always name `"SKSE"` (matched case-insensitively).
- **Register-for-SKSE at Load; register for *other* plugins at kPostLoad.** `RegisterListener` returns
  **false when the named sender isn't loaded yet**. `"SKSE"` always exists, so you register for it at
  Load. Another plugin may not have registered by the time your Load runs, so subscribe to *it* at
  kPostLoad. A repeat registration for the same sender is silently deduplicated — treat one listener per
  plugin as the model.

### Plugin-to-plugin interface exchange (the synchronous-fill pattern)

Message delivery is **synchronous and in-thread** — the dispatcher calls each listener inline before
returning. That is what makes the "hand you a stack struct, you fill it" API-exchange sound:

```cpp
// consumer side
struct DescriptionFrameworkMessage {
    enum : uint32_t { kMessage_GetInterface = 0xfbdfacfe };   // random constant — avoid type collisions
    void* (*GetApiFunction)(unsigned int) = nullptr;
};
DescriptionFrameworkMessage message;                          // stack struct, null fn ptr
SKSE::GetMessagingInterface()->Dispatch(
    DescriptionFrameworkMessage::kMessage_GetInterface,
    (void*)&message, sizeof(DescriptionFrameworkMessage*),
    "DescriptionFramework");                                  // receiver = provider's registered name
if (message.GetApiFunction)                                   // provider filled it, synchronously
    api = static_cast<...*>(message.GetApiFunction(1));
```

This demonstrates the in-thread fill: because delivery is synchronous, the provider has populated your
stack struct by the time `Dispatch` returns. Sender identity is not spoofable — NG always dispatches
under your own plugin handle, so the receiver sees your real name. A dispatch to an absent optional
integration logs `Failed to dispatch message` — expected noise, not an error. Do the exchange at
kPostPostLoad.

### Crash-guard asymmetry

Exceptions inside **kPostLoad / kPostPostLoad** handlers are caught by SKSE's guard (logged
`<plugin> crashed during postload`, the game keeps loading). **Every later message** — kDataLoaded, the
save/load messages — dispatches from a game-function hook with **no such guard**, so a crashing handler
there takes the whole game down. This asymmetry is why crash triage cares which message was last logged;
`load-failures.md` carries the log fingerprints crash-diagnostics reads.

---

## 6. The no-replay rule (load-bearing)

**SKSE does not replay messages. A listener registered after a message already fired permanently misses
it — there is no catch-up.**

This is the rule that makes registration *timing*, not just registration, load-bearing. Concretely:

- Register your `"SKSE"` listener **inside `SKSEPlugin_Load`**. kPostLoad and kPostPostLoad dispatch
  immediately after all plugins' Load functions run; a listener you register from, say, kDataLoaded has
  already missed both — and will never see them for this process.
- The reason you register for *another* plugin at kPostLoad rather than at your own Load is the same rule
  read from the other side: that plugin may not have finished registering when your Load runs, so
  registering too early for it can fail — but you must still be registered before *its* messages fire.
- It also means there is no "I'll check later whether kDataLoaded happened" fallback. If your listener
  wasn't installed in time, the form gate never opens for you. Install listeners at Load, full stop.

A late listener is the quiet failure mode behind "my plugin loaded but nothing ever happened" — the DLL
is fine, the registration simply arrived after the train left.

---

## 7. Lineage divergence

Three CommonLib lineages exist; this reference teaches the current one. Keep the differences straight
when auditing an unfamiliar plugin — do not switch lineages on their account.

- **alandtse/CommonLibVR branch `ng`** — the current, charter-locked lineage and the target of every
  idiom above. Handles all three runtimes (SE/AE/VR) from one DLL; `SKSEPluginInfo` and
  `PluginDeclaration` exist here.
- **powerof3/CommonLibSSE `dev` (po3-dev)** — a **different lineage with no VR support**. Plugins are
  built **per-runtime** (`PluginVersionData` exists only under an AE `#ifdef`); there is **no**
  `SKSEPluginInfo` and no `PluginDeclaration`. SPID is a po3-lineage plugin — its `#ifdef SKYRIM_AE`
  split around the version blob is normal *there* and must not be generalized to NG.
- **A newer libxse / commonlibsse lineage** has been observed in the field; treat it as its own line and
  verify against its own headers rather than assuming NG semantics.
- **CharmedBaryon/CommonLibSSE-NG** is **frozen**. Its wiki is the only real narrative documentation, but
  it is written against that frozen 3.x/4.x fork and diverges from `ng` in ways that will not compile —
  its **declarative-plugin system** (`OnSKSEPluginLoad`, `OnPluginMessage`, `UseSKSEPluginLoader`, the
  CMake `DECLARATIVE` option, multi-listener registration) **does not exist in `ng`**. On the live
  lineage you write `SKSEPlugin_Load` imperatively and register one listener imperatively. Read the wiki
  for concepts, cross-check every identifier against `ng` headers.

---

## 8. Not yet verified in-game

The AE loader and interface behavior above is proven against current SKSE and CommonLibSSE-NG source. A
few claims rest on wiki prose or exemplar code because the SE/VR loaders and the running game aren't in
the source corpus. Treat these as needing a build-test on a real install before you rely on them:

- **The SE/VR Query protocol end-to-end** — exactly when Query runs, and the "one DLL, three runtimes"
  acceptance across SE 1.5.97 + AE + VR. The AE half is code-proven; the SE/VR half is inferred.
- **`kInputLoaded` vs `kDataLoaded` relative order** — every sample lists input-first, but they fire from
  independent hooks and no source proves the order.
- **Whether `kDataLoaded` fires once per process** or can re-fire on an in-game data reload.
- **`kSaveGame`'s payload form** — bare save name vs full path is unconfirmed at runtime.
- **Thread affinity** of each message, and of the `AddTask` / `AddUITask` queues (game vs UI thread is
  community lore, not source-proven).
- **The OAR `REL::Module::reset()` "Clib-NG bug workaround"** — the bug it addresses is undocumented;
  investigate before recommending or omitting it.
