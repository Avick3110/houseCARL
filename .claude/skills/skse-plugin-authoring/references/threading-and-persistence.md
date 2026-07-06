# Threading & persistence — the production plumbing

The four seams a native SKSE plugin (C++ / CommonLibSSE-NG) crosses to do work at the right time, in the
right thread, against the right form, and remember it across a save — the "do it safely and remember it"
spine:

1. **[Game-thread marshalling](#part-1--game-thread-marshalling)** — an off-thread mutation onto the main game thread.
2. **[Co-save serialization](#part-2--co-save-serialization)** — round-trip state through the `.skse` co-save.
3. **[Runtime FormID & form lookup](#part-3--runtime-formid--form-lookup)** — `localID + plugin` → a live pointer under the *current* load order.
4. **[Config, logging & stl plumbing](#part-4--config-logging--stl-plumbing)** — the `SKSEPlugin_Load` boilerplate.

First read the [lineage & boundary](#lineage--the-boundary-you-must-respect); the tail lists
[what's not yet verified in-game](#not-yet-verified-in-game).

---

## Lineage & the boundary you must respect

**Lineage.** This reference targets **alandtse/CommonLibVR branch `ng`** — the one CommonLibSSE-NG lineage
that serves SE, AE, and VR from a single dynamic-runtime DLL. Two exemplars below (SPID, po3-papyrus-extender)
*default* their SSE build to a different lineage (`powerof3/CommonLibSSE`, no VR); OAR is pure-NG. For every
surface here the author-facing call site is byte-identical across both lineages, so the idioms transfer
unchanged. Do **not** recommend switching lineages.

**The one loader trap in this lineage.** The CommonLibSSE-NG *wiki* (CharmedBaryon-era) documents a
**declarative plugin system** — `OnSKSEPluginLoad`, `DECLARATIVE`, `OnSKSEMessage`, a generated
`SKSEPlugin_Load`. **That subsystem is not present in the `ng` branch this skill targets**, and a plugin
using those macros will not compile. Hand-write `SKSEPlugin_Load` and call `SKSE::Init()` yourself — the
live idiom every exemplar uses. (The metadata half — `SKSEPluginInfo(...)` / `PluginDeclaration` — *is*
real; only the declarative *loader* is absent. Full skeleton is `plugin-skeleton.md`.)

**The Papyrus boundary.** houseCARL's `papyrus-reference` skill owns the `.psc` surface; this reference
restates no Papyrus signature as authoritative. Where the line is crossed it defers: `RegistrationSet`
persists the **C++ side** of a script's `RegisterFor*` subscription but the `.psc` mod-event **names** are
`papyrus-reference`'s; the Papyrus `GetFormFromFile(localID, plugin)` global is the `.psc` analogue of the
C++ `LookupForm` below, named for the boundary only. The event-sink surface (`BSTEventSink`) that
*discovers* the work marshalled here is `event-sinks.md`; the trampoline/hook thunks that also run off-thread
are `hooking.md`.

---

## Part 1 — Game-thread marshalling

Most game-state mutation **must happen on Skyrim's main game thread** — but the code that *discovers* work
to do runs elsewhere. An event sink (`BSTEventSink::ProcessEvent`), a hook/trampoline thunk, a Papyrus
native function invoked by the VM, or any worker thread you spawn can fire **off** the game thread.
Mutating a form, a reference, or the NiNode scene graph from there races the engine. The strongest evidence
this is not paranoia: SKSE routes its *own* sensitive work (`RegenerateHead`, `UpdateWeight`, tint/hair
updates) through `AddTask` so their bodies touch the game only on the game thread
(`skse64/skse64/InternalTasks.cpp:27-89`).

### The one idiom

```cpp
// Marshal game-state mutation onto the main game thread.
// Runs once, on the next task-pool processing tick, on the game thread.
SKSE::GetTaskInterface()->AddTask([/* capture by value */]() {
    // ... safe to touch forms / references / NiNodes here ...
});
```

*Demonstrates the canonical marshal-back call.* The `TaskFn` lambda overload heap-allocates an internal
wrapper (`new Task(...)`, `alandtse-ng/src/SKSE/Interfaces.cpp:179-182`) that **self-deletes** after it runs
(`Task::Dispose()` does `delete this`). So the lambda overload needs **no manual cleanup** — the 99% path.

### The four entry points

`TaskFn = std::function<void()>` (`alandtse-ng/include/SKSE/Interfaces.h:183`):

| Call | Takes | When to use |
|---|---|---|
| `AddTask(TaskFn)` | a lambda | **99% case** — run on the game thread; SKSE heap-allocs + auto-deletes. |
| `AddTask(TaskDelegate*)` | your own delegate | You manage lifetime; your `Dispose()` frees it. Only SKSE's own pooled `InternalTasks` use this. |
| `AddUITask(TaskFn)` | a lambda | Run in the **UI/menu message-processing** context (Scaleform, HUD, crosshair, menu). Different queue. |
| `AddUITask(UIDelegate_v1*)` | your own UI delegate | Author-managed UI delegate. |

A `TaskDelegate` you supply is a two-pure-virtual interface (`Run()` + `Dispose()`); there is no `RemoveTask`.

### Which queue runs where, and the gotchas

- **`AddTask` → game thread.** Upstream `AddTask` just locks and pushes onto a queue, safe from *any*
  thread; the delegate's `Run()`/`Dispose()` fire later inside `BSTaskPool::ProcessTasks`, which SKSE
  branch-hooks into the game's own task-pool processing (`skse64/skse64/Hooks_Threads.cpp:19-40`). **That
  hook site is what puts your lambda on the game thread.**
- **`AddUITask` → UI processing.** A *separate* queue, drained in the game's UI event-queue processing
  (`Hooks_UI.cpp`). Use it only for work that must run in the UI/menu context.
- **One-shot, FIFO — and the drain is pop-until-empty.** `ProcessTasks` drains the whole queue each pass,
  *including tasks added during the drain*. An `AddTask` lambda runs **once** — and a task that
  re-`AddTask`s itself is picked up in the **same** pass, so the "loop" runs to completion inside one
  frame. This is field-verified, the hard way: a self-requeued per-frame pump hard-froze the main thread
  for the entire intended duration of the effect, twice, in two different production plugins. **Never
  self-requeue a task for repeated or per-frame work.** For repeats, pace from your own worker thread
  (sleep one tick → post one one-shot task, coalescing if the previous task hasn't run yet) or use an
  update hook.
- **No thread-affinity guard anywhere.** Neither `AddTask` nor `Run` checks the thread, so mutating a form
  off-thread *without* `AddTask` produces **no compile-time or runtime error** — just a silent race or
  crash. That silence is why the marshalling discipline must be taught, not assumed.

### The safe-vs-unsafe decision

| Context you're running in | Touch forms/refs/NiNodes directly? | Do this |
|---|---|---|
| A callback SKSE already runs on the main thread (some messaging callbacks; Papyrus native fns) | Often yes | Do work directly; still prefer `AddTask` for scene-graph mutation |
| An **event sink** (`BSTEventSink::ProcessEvent`) | **Assume no** — sinks can fire off-thread | Wrap the mutation in `AddTask` |
| A **hook / trampoline thunk** | **Assume no** — runs on whatever thread hit the code | Wrap the mutation in `AddTask` |
| A worker thread you spawned | No | `AddTask` |
| UI / menu / Scaleform / HUD state | Needs the UI context | `AddUITask` |

Reading immutable-ish data may be tolerable off-thread, but **any mutation of the scene graph, an actor's
3D, or engine reference state goes inside `AddTask`** — when unsure, marshal; it's cheap.

### The capture-safety refinement (every exemplar does this)

Capture a Skyrim **handle** (`ObjectRefHandle`/`ActorHandle` via `GetHandle()`/`CreateRefHandle()`), **not**
a raw pointer, and re-resolve it with `handle.get()` **inside** the task — the referenced object may be
destroyed between enqueue and execution.

```cpp
// SPID (spid/SPID/src/Outfits/OutfitManager+Equip.cpp:58-66)
const auto handle = actor->GetHandle();
SKSE::GetTaskInterface()->AddTask([handle]() {
    if (const auto actorPtr = handle.get()) {         // re-resolve, guard
        // ... mutate actorPtr ...
    }
});
```

*Demonstrates the safe default: capture-by-handle, re-resolve-and-guard inside the lambda.* (For pure
scene-graph work where a strong `NiPointer` keeps the node alive, capturing the resolved `root` directly is
also seen — but the handle pattern is the safe default for forms and references.) The `AddUITask` shape is
the same idiom in the UI queue — e.g. PO3's
`AddUITask([]{ RE::PlayerCharacter::GetSingleton()->UpdateCrosshairs(); })` (`Game.cpp:418-423`).

### Lock discipline across plugin boundaries

Two field-learned rules, both deadlock-shaped — and a deadlock presents as a hard freeze, not a crash,
which makes it the most expensive failure to triage. Design it out:

- **Assume every callback another plugin hands you runs under that plugin's own lock.** Frameworks
  routinely invoke their listener lists while still holding the mutex guarding the very state a listener
  would want to query. Calling back into the framework's API from inside your handler *while holding your
  own lock* is a textbook lock-order inversion: thread A holds the framework's lock and wants yours,
  thread B holds yours and wants the framework's. Discipline: read everything you need from the callback's
  arguments first; if you must take your own lock, snapshot IDs/state under it, **release it**, and only
  then call back into the other plugin.
- **Never hold any of your locks while dispatching outward.** A `SendEvent`, a messaging `Dispatch`, or a
  call through another plugin's interface can synchronously run arbitrary third-party code — code that may
  legally call back into you on the same stack.

---

## Part 2 — Co-save serialization

Co-save serialization is a fixed callback + record protocol. A plugin claims a namespace via
`SerializationInterface::SetUniqueID` and registers Save / Load / Revert (and optional FormDelete)
callbacks **once, inside `SKSEPlugin_Load`**. On save, SKSE partitions the `.skse` file by plugin (your UID
is your section signature) and your callback writes typed chunks; on load, SKSE hands you **only your own**
chunks, one at a time, and your callback dispatches on the 4-char type code and version. You never call
Save / Load / Revert yourself — SKSE drives them.

**The load-bearing rule:** any FormID or VMHandle written to a co-save **must** be re-resolved on load
through `ResolveFormID` / `ResolveHandle`, because the top byte of a FormID (and the mod-index bits of a
handle) is a **load-order mod index** that shifts when the user reorders, adds, or removes plugins between
saves. Skip the re-resolve and a saved handle silently points at the wrong form.

### The one-time registration

```cpp
// Namespace tag + record type codes + version in one enum (author-chosen 4CCs).
enum : std::uint32_t { kMyPluginID = 'MYPL', kSerVersion = 1, kRecord_Handles = 'HNDL' };

SKSEPluginLoad(const SKSE::LoadInterface* a_skse) {
    SKSE::Init(a_skse);
    auto* ser = SKSE::GetSerializationInterface();
    ser->SetUniqueID(kMyPluginID);          // claims this plugin's co-save section
    ser->SetSaveCallback(MySave);           // SKSE calls these; you never do
    ser->SetLoadCallback(MyLoad);
    ser->SetRevertCallback(MyRevert);       // wipes state before every load / new game
    ser->SetFormDeleteCallback(MyFormDel);  // optional: drop regs for deleted forms
    return true;
}
```

*Demonstrates the full five-setter wiring, done once* (canonical: `po3-papyrus-extender/src/main.cpp:110-115`).
Your **UID must be globally unique across the whole load order** — SKSE logs a collision but does not abort
(`skse64/skse64/Serialization.cpp:107-118`), so two plugins sharing a UID silently corrupt each other's
sections. Pick a distinctive 4CC.

### Write side

```cpp
void MySave(SKSE::SerializationInterface* intfc) {
    if (!intfc->OpenRecord(kRecord_Handles, kSerVersion)) return;  // start a chunk
    std::size_t n = data.size();
    intfc->WriteRecordData(n);                    // POD write = sizeof(T) bytes
    for (auto& h : data) intfc->WriteRecordData(h);
    // OpenRecord again for the next record type...
}
```

*Demonstrates chunked writing: one `OpenRecord` per record type, then flat POD writes.* `WriteRecordData<T>`
serializes **flat POD only** (`sizeof(T)` bytes) — there is **no** built-in string/vector serialization, so
for variable-length data write a length prefix then the bytes, and read them back the same way. Beware
convention drift: po3 writes `length()+1` (NUL included), SPID writes raw `length()`; a round-trip must pair
matching write/read helpers.

### Read side — dispatch on type, gate on version

```cpp
void MyLoad(SKSE::SerializationInterface* intfc) {
    std::uint32_t type, version, length;
    while (intfc->GetNextRecordInfo(type, version, length)) {
        if (version != kSerVersion) { /* skip or migrate */ continue; }
        switch (type) {
            case kRecord_Handles: LoadHandles(intfc); break;
            default: break;
        }
    }
}
```

*Demonstrates the load loop — `GetNextRecordInfo` terminates cleanly at your boundary because SKSE feeds you
only your own chunks.* Two production shapes for the version gate: po3 *skips* on mismatch; SPID *branches to
per-version readers* so old saves still load. The version constant is your **migration lever** — bump it to
evolve the format without corrupting old saves.

**Read byte-exact, and bounds-check before you allocate.** `ReadRecordData` returns a **byte count**, not a
bool — the common truthy check (`if (!intfc->ReadRecordData(v))`) catches only the zero-byte failure and
silently accepts a short read. Require `ReadRecordData(v) == sizeof(v)` for every POD read. And treat every
count or string length read from a co-save as untrusted input: a truncated or corrupted save hands you a
garbage length, and a `resize(garbage)` is an instant allocation blow-up. Bound counts and lengths against
sane maxima before allocating; treat a violation as a failed record (warn + skip), never as state to limp
on with.

### The re-resolve rule, concretely

A FormID's top byte is that load-order mod index. `ResolveFormID` rewrites it using a `saved→current` map
SKSE builds from a `'PLGN'` record it writes into **every** co-save (the saved plugin list, name + index) —
so authors piggy-back on this and never write the plugin list themselves. The minimal correct read:

```cpp
// po3 helper (po3-papyrus-extender/include/Common.h:36-43)
bool read_formID(SKSE::SerializationInterface* i, RE::FormID& id) {
    i->ReadRecordData(id);
    if (id != 0) return i->ResolveFormID(id, id);   // <-- never skip this
    return true;
}
```

*Demonstrates the mandatory re-resolve on read.* `ResolveHandle` is the VMHandle twin, mandatory for every
Papyrus object handle read from a co-save. A `false` return means "unknown form" (its plugin left the load
order) — production loaders **fail soft**: warn and skip that entry rather than aborting the whole load.
(The two resolvers use *different* bit math on their ESL branches — reproduce each from its own function.)

### RegistrationSet — CommonLib's ready-made round-trip

The common need — *a set of Papyrus script handles subscribed to a mod-event name, surviving a save* — is
already solved, and this is the bridge `event-sinks.md` points at: an event sink discovers the game event, a
`RegistrationSet` fans it back out to every subscribed script across saves. Construct with the Papyrus event
name; `Register`/`Unregister` at runtime, `Save`/`Load`/`Revert` from callbacks, `SendEvent(Args...)` to fire.

| Type | Keyed by | Use when |
|---|---|---|
| `RegistrationSet<Args...>` | nothing | flat "register for event" (`OnActorKilled`) |
| `RegistrationMap<Filter, Args...>` | a filter value | filtered by a form/type/quest (`OnQuestStart` by quest FormID) |
| `RegistrationSetUnique<Args...>` | target FormID | per-reference event (`OnEnterFurniture` on a specific ref) |
| `RegistrationMapUnique<Filter, Args...>` | target FormID + Filter | per-reference **and** filtered (`OnHitEx`) |

You declare one as a member with its event name — `SKSE::RegistrationSet<const RE::Actor*, const RE::Actor*>
actorKill{ "OnActorKilled"sv };` (po3, `include/Serialization/EventHolder.h:70`). The critical value is that
its serialization **is the re-resolve round-trip you'd otherwise hand-write** — on load it reads each raw
VMHandle, calls `ResolveHandle`, and **drops any handle that fails to resolve**
(`alandtse-ng/src/SKSE/RegistrationSet.cpp:179-197`); the `Map`/`Unique` variants also re-resolve their
FormID filter/target. The `.psc`-side mod-event names and argument lists belong to `papyrus-reference`.

Two rules keep state clean across the save boundary:

- **Revert = wipe, always.** `Revert` is literally `Clear()` (`RegistrationSet.cpp:199-202`), and SKSE fires
  it **before every load and on new-game**, so a previous session's registrations cannot leak into the next.
  Never persist a registration across the Revert boundary — that is how stale handles bleed between saves.
- **FormDelete drops dead handles.** `SetFormDeleteCallback` receives a `VMHandle` when a backing form is
  deleted at runtime, letting you `Unregister` it so the co-save never persists a dead handle
  (`RegistrationSet` exposes `Unregister(RE::VMHandle)` for exactly this).

**On timing:** `SetUniqueID` and the callbacks are registered synchronously inside `SKSEPlugin_Load`; the
callbacks themselves fire later, driven by SKSE at save/load/revert. A manager's *non*-serialization setup
(its own event sinks) is deferred to `kDataLoaded` — but the serialization registration is not.

---

## Part 3 — Runtime FormID & form lookup

CommonLibSSE-NG resolves forms at runtime through two authoritative entry points, plus the DataHandler
name-lookup that composes a runtime FormID from a plugin name:

| Call | Takes | Returns |
|---|---|---|
| `TESForm::LookupByID<T>(id)` | a **fully-resolved runtime** FormID (index bits already correct) | `T*` load-order winner or `nullptr` (typed overload nulls on type mismatch) |
| `TESForm::LookupByEditorID<T>(edid)` | an EditorID string | `T*` or `nullptr`; the runtime EditorID map is **sparse** on stock SE/AE |
| `TESDataHandler::LookupForm<T>(localID, "Plugin.esp")` | a **plugin-local** FormID + plugin filename | `T*`/`nullptr`; typed overload FormType-checks |

**Rule of thumb:** you almost never have a stable full runtime FormID at author time — you know "record
`0x1234` in `Whiterun.esp`." So the primary idiom is `LookupForm(localID, "Plugin.esp")`, **not**
`LookupByID` with a hand-written 8-digit constant. Reserve `LookupByID` for FormIDs obtained at runtime (off
another form) or hard-coded vanilla `Skyrim.esm` IDs (index `0x00`, so local == runtime).

### How the mod index resolves — and why it moves

`LookupForm` composes the runtime FormID off **live members of the loaded plugin file** (`TESDataHandler.cpp:113-141`):

```cpp
FormID formID  = file->compileIndex          << (3 * 8);  // high byte  <<24
formID        += file->smallFileCompileIndex << ((1*8)+4); // 12-bit ESL <<12
formID        += a_localFormID;                            // low bits
```

`compileIndex`/`smallFileCompileIndex` are a function of the **current** load order — **that is the seam**:
the same record resolves to a different runtime FormID under a different load order, which is why you pass
the local ID + plugin name and let the runtime compose the address.

### The FormID address space

| High byte | Meaning | Local space |
|---|---|---|
| `0x00`–`0xFD` | a full (non-light) plugin at that load index | `~16.7M` IDs |
| `0xFE` | **any** light/ESL plugin; 12-bit sub-index in `0x00FFF000` | only **4096** IDs (`0x000`–`0xFFF`) |
| `0xFF` | **dynamic / runtime-generated** (no backing file) | n/a |

**The FE/light caveat, burned into houseCARL's own tooling:** an ESL's local FormID space is only the low
12 bits, because bits `0x00FFF000` are stolen for the light sub-index — *why* `housecarl_compact_plugin`
targets the `0x800–0xFFF` window, and why "FE xxx yyy" in xEdit reads as `FE` + sub-index `xxx` + local
`yyy`. When you call `LookupForm(0x812, "MyLight.esl")` you pass the **local** `0x812`, not `0xFE000812`.
And **never persist a `0xFF`-range (dynamic) FormID** assuming stability across saves; treat `0xFF` as "no
defining file" (SPID keys permanent storage off the character FormID, `IsDynamicForm()`, `PCLevelMultManager.cpp:11`).

### The null-safe, type-safe idiom

```cpp
auto* dh = RE::TESDataHandler::GetSingleton();          // valid only after data load
RE::TESForm* anyForm = nullptr;

if (formID && modName) {
    anyForm = dh->LookupForm(*formID, *modName);        // localID + "Plugin.esp"  ← primary path
} else if (formID) {
    anyForm = RE::TESForm::LookupByID(*formID);         // already-resolved / vanilla id
} else if (editorID) {
    anyForm = RE::TESForm::LookupByEditorID(*editorID); // sparse map — may miss
}

if (!anyForm) { /* fail loud, log + return; never deref */ return; }

auto* typed = anyForm->As<RE::BGSKeyword>();            // or use the typed LookupForm<T> overload
if (!typed) { /* wrong FormType — fail */ return; }
```

*Demonstrates the resolver's control flow (distilled from SPID's production path,
`spid/SPID/src/FormData.h:199-266`).* Two disciplines are load-bearing. **Null-handling is the single most
important habit here** — every lookup can fail (plugin not loaded, record removed, wrong local ID,
ESL-vs-full mismatch) and returns `nullptr`/`0`; fail loud (log + return, or throw), never dereference an
unchecked result, and prefer the typed `LookupForm<T>` overload, which folds the FormType check in so a
wrong record type fails safely instead of becoming an invalid `static_cast`. Second, **empty EditorID is a
keyword crash hazard** — `GetFormEditorID()` returns `""` for most runtime records, and for keywords
specifically an empty EditorID crashes the game downstream, so SPID guards it (`src/FormData.h:236-239`).

### Timing — lookups need the load order populated

`TESDataHandler::GetSingleton()` is only *meaningful* **after the load order is populated** — data-loaded,
not at SKSE plugin-load time. The pointer exists earlier, but the forms maps it queries are empty
pre-data-load, so name lookups return `nullptr` for everything. Resolve forms in a `kDataLoaded` handler,
not in `SKSEPlugin_Load`, as SPID does.

### MergeMapper — required for redistributable plugins

A record the user merged into another plugin now lives under a different `modName`/`localID`. NG-lineage
production plugins wrap `LookupForm` in a MergeMapper-aware helper that asks MergeMapper for the remapped
plugin+FormID *before* resolving:

```cpp
// OAR (oar/src/Utils.cpp:570-584)
RE::TESForm* LookupForm(RE::FormID localID, std::string_view modName) {
    RE::FormID formID;
    if (g_mergeMapperInterface) {
        auto [newMod, newID] = g_mergeMapperInterface->GetNewFormID(modName.data(), localID);
        formID = RE::TESDataHandler::GetSingleton()->LookupFormID(newID, newMod);
    } else {
        formID = RE::TESDataHandler::GetSingleton()->LookupFormID(localID, modName);
    }
    return formID ? RE::TESForm::LookupByID(formID) : nullptr;
}
```

*Demonstrates the MergeMapper wrapper — resolve the remapped `modName`/`localID` first, fall back to the raw
pair when MergeMapper is absent.* Prefer this wrapper over a raw `LookupForm` for any **redistributable**
plugin.

### Awareness — houseCARL's parked runtime-FormID bridge

houseCARL's data layer addresses records as `localID:DefiningMaster.esp` — deliberately mod-index-free,
where CommonLib's runtime uses a mod-index-bearing FormID that shifts every load order. houseCARL's
**parked** runtime-FormID bridge is exactly `localID:Master.esp` → `LookupForm(localID, "Master.esp")` →
pointer; the correctness rule if it is ever built: **always go through `LookupForm(localID, master)`; never
emit or trust a hard-coded 8-digit runtime FormID.**

---

## Part 4 — Config, logging & stl plumbing

Every plugin stands up logging, often a config parse, a fail-loud verb, and a handful of `SKSE::stl`
helpers before it does anything else — the least glamorous but most-copied surface, and CommonLib does
**not** hand it to you ready-made.

### Logging — SKSE::log ships only the front end

`SKSE::log` (from `<SKSE/Logger.h>`) provides only the source-location-capturing macros — `trace`, `debug`,
`info`, `warn`, `error`, `critical` — plus `log_directory()` and `init()`. It does **not** stand up a
logger: until you install a default spdlog logger, **every `logger::info(...)` goes nowhere.** And the trap
is that `SKSE::log::init()` is a **no-op by default** (its body is behind an `#ifdef`; no exemplar calls
it). Hand-roll your own `InitializeLog()`:

```cpp
// Minimal copyable form (po3-papyrus-extender/src/main.cpp:76-95)
void InitializeLog() {
    auto path = logger::log_directory();
    if (!path) { stl::report_and_fail("Failed to find standard logging directory"sv); }
    *path /= "po3_papyrusextender64.log"sv;
    auto sink = std::make_shared<spdlog::sinks::basic_file_sink_mt>(path->string(), true);
    auto log  = std::make_shared<spdlog::logger>("global log"s, std::move(sink));
    log->set_level(spdlog::level::info);
    log->flush_on(spdlog::level::info);
    spdlog::set_default_logger(std::move(log));
    spdlog::set_pattern("[%H:%M:%S] [%l] %v"s);
    logger::info(FMT_STRING("{} v{}"), Version::PROJECT, Version::NAME);
}
```

*Demonstrates the whole logger stand-up: locate the log dir, fail loud if absent, wire a file sink, set the
default logger and pattern.* Call it **first thing in `SKSEPlugin_Load`, before `SKSE::Init`**.
`log_directory()` returns `std::nullopt` if the Documents known-folder lookup fails — the exact case the
`report_and_fail` guard covers. For a **user-tunable** level, read it from the ini via
`spdlog::level::from_str` (`off → info` fallback), as SPID does. Note the `set_pattern` flag grammar
(`%l` level, `%v` message, `%s`/`%#` source loc, `%t` thread, `%e` ms), `from_str`, `basic_file_sink_mt`,
`set_default_logger` are **spdlog** API (a vcpkg dependency), not CommonLib — cite spdlog's own docs.

### The fail-loud verb — report_and_fail

`[[noreturn]] SKSE::stl::report_and_fail(std::string_view a_msg)` (`alandtse-ng/include/SKSE/Impl/PCH.h:601`)
is the canonical "cannot continue, tell the user, stop" verb. It logs at `critical`, pops a Win32
`MessageBoxW` (caption = the plugin DLL filename, body = `file(line): msg`), and calls `TerminateProcess`.
Use it for an unrecoverable precondition (missing log dir; a hard incompatibility like OAR's DAR-present
check, `oar/src/main.cpp:27`). It is the fail-loud spine that matches houseCARL's own no-silent-failure
discipline (Q3) — a plugin that cannot do its job says so and stops, rather than limping on in a silently
degraded mode.

### The PCH namespace-alias convention

Every exemplar sets terse aliases in its own PCH — `namespace logger = SKSE::log;` and
`namespace stl { using namespace SKSE::stl; }` (OAR spells the latter `util`). That is why call sites read
`logger::info` / `stl::report_and_fail` / `util::report_and_fail` interchangeably — all the same underlying
`SKSE::log` / `SKSE::stl`. The library also injects `namespace stl = SKSE::stl` into `RE` and `REL`, so you
reach the helpers through whichever namespace you're already inside.

### The SKSE::stl helper inventory (all in `alandtse-ng/include/SKSE/Impl/PCH.h`)

| Helper | Purpose |
|---|---|
| `report_and_fail` | `[[noreturn]]` fail-loud (see above) |
| `utf8_to_utf16` / `utf16_to_utf8` | `optional<wstring>` / `optional<string>`; `nullopt` on failure |
| `enumeration<E,U>` | type-safe flag-enum wrapper over `REX::EnumSet` (the type behind record/form flag fields) |
| `unrestricted_cast<To>(From)` | compile-time-dispatched "reinterpret anything" escape hatch |
| `adjust_pointer<T>(U*, ptrdiff_t)` | cv-correct byte-offset pointer adjust (multiply-inheriting RE types) |
| `emplace_vtable<T>(T*)` | write `T`'s real game vtable ptr into slot 0 |
| `atomic_ref<T>` / `scope_exit` | `std::atomic_ref` over `volatile T&` / RAII cleanup-on-scope-exit |

Note: **`to_underlying` is `std::to_underlying`** (C++23), **not** a CommonLib helper. Both live lineages
carry `REX::EnumSet` and the `enumeration` shim identically, so verify the namespace against **code**, not
the wiki.

### Config-file parsing — SimpleIni, pick one route

Production plugins parse config with **brofield's SimpleIni** (`CSimpleIniA`, UTF-8) — no exemplar uses
TOML. Two routes converge on `CSimpleIniA` via different vcpkg deps — **pick one, not both:**

| Route | vcpkg dependency | Getter style |
|---|---|---|
| **clib-util helper** (SPID) | `"clib-util"` only (vendors SimpleIni transitively) | `clib_util::ini::get_value(...)` template — auto-writes a commented default |
| **raw simpleini** (OAR) | `"simpleini"` | hand-rolled typed getters with a null-guard |

```cpp
// Pattern A — clib_util helper (SPID, src/main.cpp:155-162): least code, auto-writes a commented default
CSimpleIniA ini;
ini.SetUnicode();                               // always first: treat file as UTF-8
ini.LoadFile(settingsPath);                     // a missing file is fine (guard on the SI_Error sign)
clib_util::ini::get_value(ini, logLevelStr, "Log", "LogLevel", ";  Log level ...\n");
(void)ini.SaveFile(settingsPath);               // materializes value + comment on first run
```

*Demonstrates the read-with-default-then-write-back idiom.* On first run a missing key returns the C++
default, `get_value` inserts it with its comment, and `SaveFile` flushes a fully-commented config to disk;
an existing key's user edit is preserved.

**Pattern B** (OAR, `src/Settings.cpp:4-29`) skips clib-util: a hand-rolled getter reads the key only if
`GetValue(section, key)` is non-null, so an absent key leaves the compiled C++ default untouched (no
write-back). Use it when you want no clib-util dependency or precise "absent key ⇒ keep default" control.

Guard every `LoadFile`/`SaveFile` on the **sign** of the `SI_Error` return — `>= 0` is success
(`SI_OK`/`SI_UPDATED`/`SI_INSERTED`), `< 0` is failure (`SI_FAIL`/`SI_NOMEM`/`SI_FILE`). Config paths sit
under `Data/SKSE/Plugins/`.

---

## Not yet verified in-game

The idioms above are traced from the pinned CommonLibSSE-NG / SKSE64 source and three production plugins.
A handful of **runtime** behaviors are visible in the source but can't be *proven* from headers — they need
an empirical build-and-run before you rely on them. Treat these as sound-but-unconfirmed:

- **The exact thread `AddTask` runs on** for the current AE runtime (1.6.x), and whether an off-thread
  form/NiNode mutation *without* `AddTask` actually corrupts vs races benignly. The game-thread claim is
  well-established community knowledge and matches the source's intent, but the shallow SKSE64 clone can't
  confirm the current-runtime hook target.
- **The FE/light co-save round-trip** — whether the 12-bit light sub-index remaps correctly across a real
  load-order change. `ResolveFormID`'s code path handles it, but this is the silent-corruption edge for FE
  plugins; gate it empirically. (Do *not* hand-re-derive with `GetLoadedLightModIndex` on top of
  `ResolveFormID` — that double-remaps.) Also unconfirmed: whether `ResolveFormID` returns `false` (vs a
  stale FormID) when a plugin was removed between saves.
- **Which record types keep a non-empty runtime EditorID** on stock SE/AE (keywords yes; general records
  typically no); **`report_and_fail` truly popping a MessageBox and terminating**, a plugin with no log
  setup still loading, and **relative config paths** resolving against the game exe's directory.
