# Native Papyrus functions — expose C++ to the VM

How an SKSE plugin gives Papyrus scripts a function the game's own Papyrus can't provide — the
registration idiom, the full C++↔Papyrus type-marshalling map, latent (delayed-return) functions, and
the three channels for calling the other way (C++ → Papyrus). This is the C++ **producer** side. Reach for
it the moment a `.psc` needs to do something vanilla Papyrus + SKSE can't already do.

## Contents

- [The boundary — what this file owns and what it doesn't](#the-boundary)
- [The registration idiom](#the-registration-idiom)
- [The three signature shapes](#the-three-signature-shapes)
- [The type-marshalling map](#the-type-marshalling-map)
- [Arrays — and the skse64 name trap](#arrays-and-the-skse64-name-trap)
- [None / nullptr semantics](#none-nullptr-semantics)
- [What won't marshal](#what-wont-marshal)
- [Returning values](#returning-values)
- [Latent (delayed-return) functions](#latent-functions)
- [Calling into Papyrus (C++ → script)](#calling-into-papyrus)
- [Thread-safety — the one flag](#thread-safety)
- [Production layout that scales](#production-layout)
- [Not yet verified in-game](#not-yet-verified)

---

## The boundary — what this file owns and what it doesn't {#the-boundary}

A native function is **one thing declared in two places**: a C++ function you register, and a matching
`.psc` declaration a script author compiles against. This file owns the **C++ side** and the *pairing
contract* between the two. It does **not** own the `.psc` surface.

The reason the split is strict: native registration is keyed purely by two strings, `(className, fnName)`,
at bind time — nothing in the C++ headers defines or validates the matching `.psc` declaration (the
`native`/`global` keywords, the parameter names, default values, how a latent function looks script-side).
Those are Papyrus syntax, and Papyrus syntax is owned by houseCARL's **papyrus-reference** skill. When you
need the authoritative shape of the `.psc` line, consult that skill; never derive a `.psc` signature from a
C++ header and present it as fact. The consumer `.psc` then compiles via `housecarl_compile_script`.

What this file *does* state about the `.psc` side is only the **pairing rule** — the four things that must
line up for the two halves to connect:

- the script's `Scriptname` equals the registered class-name string;
- the function's name equals the registered function-name string;
- the declaration carries `native` (and `global` exactly when the C++ base is `StaticFunctionTag*`);
- the `.psc` parameters after the base correspond to the C++ parameters, types mapped.

Default parameter values (`Int by = 1`) live **only** in the `.psc` — C++ registration has no notion of
them. Rename either side of a matched pair and the pairing silently breaks; that's why production code
derives the Papyrus name from the C++ name by stringization (below).

**Documentation caveat, worth stating to anyone who searches the web.** This whole C++↔Papyrus surface is
documented only in header doc-comments, unevenly — the latent path carries real prose, the common non-latent
path none — and the current CommonLibSSE-NG wiki has **no** native-function page at all. So any external
native-function tutorial is almost certainly written against the frozen CharmedBaryon-era 3.x fork and may
have drifted from live `ng`. Trust the headers over the tutorial, and re-verify a surprising claim against
`RE/N/NativeFunction.h`, `RE/N/NativeLatentFunction.h`, `RE/N/NativeFunctionBase.h`, `RE/I/IVirtualMachine.h`,
and `SKSE/Interfaces.h`.

---

## The registration idiom {#the-registration-idiom}

The single most important fact: **registration is a callback you hand to SKSE once, from
`SKSEPlugin_Load`, after `SKSE::Init`.** Inside that callback you call `vm->RegisterFunction(...)` once
per function. The whole minimal shape:

```cpp
// Papyrus.h — export only the callback
namespace MyPlugin { bool RegisterFunctions(RE::BSScript::IVirtualMachine* vm); }

// Papyrus.cpp
#include "Papyrus.h"
namespace {
    constexpr std::string_view PapyrusClass = "MyPluginScript";   // == the .psc scriptName you ship

    // SHORT STATIC — base = StaticFunctionTag* → 'global native'; tag is always nullptr, ignore it
    std::int32_t GetVersion(RE::StaticFunctionTag*) { return 1; }

    // LONG STATIC — prepend VM* + stack ID for TraceStack error reporting (po3's STATIC_ARGS)
    float GetReach(RE::BSScript::IVirtualMachine* vm, RE::VMStackID stackID,
                   RE::StaticFunctionTag*, RE::TESObjectWEAP* weapon) {
        if (!weapon) { vm->TraceStack("Weapon is None", stackID); return 0.0f; }
        return weapon->weaponData.reach;
    }
}
bool MyPlugin::RegisterFunctions(RE::BSScript::IVirtualMachine* vm) {
    vm->RegisterFunction("GetVersion", PapyrusClass, GetVersion);
    vm->RegisterFunction("GetReach",   PapyrusClass, GetReach);
    // 4th arg (callableFromTasklets, default false): true ONLY for thread-safe functions
    return true;   // the callback's bool is effectively ignored; true by convention
}

// main.cpp — inside SKSEPlugin_Load, AFTER SKSE::Init(skse):
if (!SKSE::GetPapyrusInterface()->Register(MyPlugin::RegisterFunctions)) {
    SKSE::stl::report_and_fail("Failed to register Papyrus functions.");
}
// Register is variadic: ->Register(RegisterA, RegisterB, RegisterC) works.
```

*The canonical minimal plugin — the copy-paste starting point. Distilled from the official colorglass NG
sample and po3-papyrus-extender (API-identical). Every rule below is a consequence of this shape.*

**Why `Init` must come first, and why it's not just convention.** `SKSE::GetPapyrusInterface()` reads back a
slot only populated *inside* `SKSE::Init` — call `Register(...)` before `Init` and you dereference null
(mechanically enforced, not style). Order is always `SKSE::Init(skse)` →
`SKSE::GetPapyrusInterface()->Register(...)`. The plugin declaration that makes `SKSEPlugin_Load` run is a
prerequisite too, but lives in `plugin-skeleton.md` — this file starts from "the load body is running."

**Late registration is safe — always go through the CommonLib wrapper.** `Register` checks whether the VM
already exists: if it does, your callback runs synchronously; if not, CommonLib defers it and drains the
deferred list at VM-init, after SKSE's own classes — that deferral is what makes registering "late" work.
The catch: the *raw* SKSE interface only drains its list inside the single VM-init hook — register raw after
that fires and it's silently dropped. Use `GetPapyrusInterface()->Register` and you never touch that edge.

**Two booleans, do not conflate them.** (1) The `bool` your `RegisterFunctions` callback *returns* is
effectively **ignored** (the immediate path returns `true` regardless) — return `true` by convention.
(2) The `bool` the `Register()` *call* returns is a real success/fail signal: check it and `report_and_fail`
on `false` (the colorglass idiom above). po3 discards it; prefer report-and-fail so a registration failure is
loud, not silent (houseCARL Q3).

**`Register` is variadic** — `->Register(RegA, RegB, RegC)` registers each in order and short-circuits to
`false` if any fails. Most plugins pass a single top-level dispatcher (see [production layout](#production-layout)).

**Registering natives is opt-in.** A full production plugin with no scripting API never touches this
interface — SPID and OAR contain zero `GetPapyrusInterface` calls. If your plugin exposes nothing to
Papyrus, skip this entire subsystem.

---

## The three signature shapes {#the-three-signature-shapes}

What the C++ signature declares is what registers. An illegal signature fails **at compile time**, never at
runtime — so if it builds, the marshalling is legal. There are three shapes, distinguished by the **first
C++ parameter** (the "base"), which *is* the Papyrus `self`:

| Form | C++ callback shape | `.psc` side (papyrus-reference owns exact syntax) |
|---|---|---|
| **Short static** | `R Fn(RE::StaticFunctionTag*, Args...)` | `global native` |
| **Short method** | `R Fn(FormOrAliasOrEffectPtr, Args...)` registered under that class's name | instance `native` on an existing class |
| **Long** | `R Fn(VM* /*or IVirtualMachine*/, RE::VMStackID, Base, Args...)` | same as the short form it extends |

The base must be exactly one of four things:

- **`RE::StaticFunctionTag*`** → a `global` function. It's an empty tag struct; at dispatch it is **never
  read** (always `nullptr`) — accept it and ignore it. This is the common case for a new plugin: expose
  global functions that take their targets as explicit parameters.
- **a form pointer** (`T*` where `T : RE::TESForm` — `Actor*`, `TESObjectWEAP*`, …), **`RE::BGSBaseAlias*`**,
  or **`RE::ActiveEffect*`** → an **instance method** on that Papyrus class. You get null-self protection for
  free: the dispatcher aborts the call if the self is null, so a member native never runs on a None object.
  Note SKSE can only bind methods to *existing* Papyrus classes — it cannot create brand-new instanced
  types. A new plugin almost always uses global functions instead.

**The long form** prepends `VM*` + `RE::VMStackID` before the base. Use it whenever the function needs to
report script-visible errors — the `VM*` lets you call `vm->TraceStack("Weapon is None", stackID)` so the
failure lands in the user's Papyrus log instead of vanishing. po3 wraps this prefix as its `STATIC_ARGS`
macro. Prefer the long form for anything that validates its arguments.

**What registration derives from the signature, and what it doesn't.** The constructor builds the VM-facing
descriptor from the C++ signature alone — each parameter's and the return's Papyrus type. But **parameter
names are synthesized `param1..paramN`**; your real C++ parameter names never reach the VM. The
human-readable names come from the `.psc` — again, papyrus-reference's lane. A ctor comment notes native
functions support **at most 11 parameters** (skse64's older templates stopped at 10).

**VR is not a factor for basic registration.** In an NG multi-runtime build, the registration-relevant
vtable slots sit ahead of the VR-only extra virtuals, so registration code is byte-identical across SE / AE
/ VR. You only meet the cross-runtime vtable juggling if you reach deeper into the VM than registering
functions. (See `multi-runtime.md` for the general cross-runtime story.)

---

## The type-marshalling map {#the-type-marshalling-map}

**The unifying rule: what the C++ signature declares is what registers.** Legality is decided by template
traits when `RegisterFunction` instantiates the native — return, base, and each parameter each pass their
own convertibility gate. There is no runtime type negotiation: an illegal type is a compile error, not a
runtime surprise. These are the legal currencies:

| C++ type | Param | Return | Papyrus VM type | Notes |
|---|---|---|---|---|
| `std::int32_t` / `std::uint32_t` | yes | yes | Int | the native integer widths |
| other-width integrals (`int8/16/64`, `char`, `size_t`), any `enum` | yes | yes | Int | **compiles, silently narrows** to int32/uint32 — no width guard |
| `float` | yes | yes | Float | direct |
| `double` (any wider FP) | yes | yes | Float | **compiles, silently narrows** to float |
| `bool` (exactly) | yes | yes | Bool | exact match; no "truthy" ints |
| `RE::BSFixedString`, `std::string`, `std::string_view` | yes | yes | String | gate keys on convertible-to-`string_view` |
| `const char*` | **no** | yes | String | **sharp edge** — fails to build as a parameter, and unsafe (dangling) as a return. Rule: return `BSFixedString`/`std::string`, never `const char*` |
| `T*` where `T : RE::TESForm` (`Actor`, `TESObjectWEAP`, `BGSKeyword`…) | yes | yes | the form's script type | script type is looked up from `T::FORMTYPE` at *registration*. Bare `RE::TESForm*` → generic Form; a class without its own `FORMTYPE` silently degrades to its ancestor's |
| `RE::BGSBaseAlias*` / `BGSRefAlias*` / `BGSLocAlias*` | yes | yes | Alias / ReferenceAlias / LocationAlias | fixed VM type IDs, not `FORMTYPE` |
| `RE::ActiveEffect*` (and subclasses) | yes | yes | ActiveMagicEffect | fixed VM type ID |
| `std::vector<T>`, `RE::BSTArray<T>` | yes | yes | Array of T | **duck-typed; marshals by copy both ways** — see below |
| `RE::reference_array<T>` | yes | **no** | Array (same object) | **in-place / write-through** — see below |
| `void` (return) | — | yes | None | result set to None |

The three "compiles but silently narrows" rows — wider integers, `double`, small ints/enums — are the only
members of this map whose failure is a **wrong runtime value** rather than a compile diagnostic. If a script
gets a truncated number back, suspect one of these. Everything else that's wrong is caught by the build.

---

## Arrays — and the skse64 name trap {#arrays-and-the-skse64-name-trap}

**Arrays are duck-typed, not hard-coded to `std::vector`.** Any container that is default-constructible,
non-pointer, and exposes `value_type`/`size_type`/`iterator` + `begin`/`end`/`size`/`push_back` qualifies —
so both `std::vector<T>` and `RE::BSTArray<T>` work by construction. Legal element types are the
builtin-convertibles (so `std::vector<std::string>` is fine) and form/alias/active-effect pointers.
`std::vector<bool>` is a handled special case (Bool[]).

**Plain containers copy in both directions.** An array parameter is unpacked into a fresh container; a
return array is packed into a brand-new VM array. **Mutating a `std::vector` parameter inside the native
does NOT change the script's array** — the script sees a copy. To write through to the caller's array, use
the wrapper below.

**`RE::reference_array<T>` is the write-through array.** It unwraps the caller's VM array into an internal
vector on construction and re-packs every element back into the **same** VM array in its destructor. It is
**move-only**, **fixed size** (you reassign elements, you can't grow it), and its elements are limited to
builtin-convertibles or form pointers. Critically it is **parameter-only** — a `reference_array` *return*
fails to compile. So the rule of thumb:

- **return** an array by value → `std::vector<T>` / `RE::BSTArray<T>`;
- **mutate** the caller's array in place → `reference_array<T>` as an in-parameter.

### The porting trap — skse64's array wrapper names don't exist in NG

If you're porting old skse64 code or following an old tutorial, this is the number-one search miss:
skse64's Papyrus array types **`VMArray<T>` and `VMResultArray<T>` DO NOT EXIST in CommonLibSSE-NG.** NG
replaced named wrappers with plain duck-typed containers. Translate on sight:

| Purpose | skse64 name (retired) | NG substitute |
|---|---|---|
| Array **parameter**, by copy | `VMArray<T>` | `std::vector<T>` **or** `RE::BSTArray<T>` |
| Array **return**, by value | `VMResultArray<T>` | `std::vector<T>` **or** `RE::BSTArray<T>` |
| **In-place** array mutation | manual `VMArray::Set` | `RE::reference_array<T>` (parameter-only, move-only) |

You'll also see `VMResultArray` in skse64's own latent example (SpawnerTask). Read it as *conceptual*
precedent only — translate both the array-type name and the latent idiom before you use it (NG's latent path
is coroutine/task-based, not skse64's `LatentNativeFunctionN` template).

---

## None / nullptr semantics {#none-nullptr-semantics}

Papyrus `None` and C++ `nullptr` are the same idea across the boundary. Always null-check object parameters.

| Direction | Behavior |
|---|---|
| script passes a None object → C++ param | `nullptr`. **Always null-check.** |
| handle dead / unloaded / wrong type | `nullptr` — resolution requires the handle be live and type-matched |
| C++ returns `nullptr` form/alias/effect | script receives None |
| script passes None where a member native's **self** is expected | callback never runs (dispatcher aborts on null base) |
| script passes a None **array** | an **empty container** — C++ cannot tell "None" from "0-length array" |
| C++ returns an empty vector | a real zero-length VM array, **not** None |
| string | never None; unset = `""` |

---

## What won't marshal {#what-wont-marshal}

Every rejection below is confirmed from the headers; only the exact compiler *message* text is unverified.
The point of listing them is so a build failure here reads as "expected — fix the signature," not a mystery.

| Attempt | Why it fails |
|---|---|
| `T&`, `volatile T`, a by-value form (`RE::Actor`), or a pointer outside the TESForm / BGSBaseAlias / ActiveEffect hierarchies (`RE::NiAVObject*`, `int*` out-params) | fails the parameter-convertibility gate → "incomplete type" compile error |
| nested container `std::vector<std::vector<int>>` | passes the shape check, then dies on a `static_assert` ("Invalid target type") |
| `const char*` as a **parameter** | can't be built from the incoming string — instantiation error |
| `reference_array` as a **return**, or any `const` return | rejected by the return gate |
| `reference_array` with an alias/effect element type | element type unsupported → incomplete type |
| a lambda callback — capturing, or captureless without a unary-`+` decay | the callback must be a plain function pointer; a bare lambda's closure type matches no specialization → compile error. A **captureless** lambda works only if you write `+[]{...}`; a **capturing** lambda can never be registered |
| `std::int64_t`, `double`, small ints, enums | **not** a compile error — legal but silently narrowed (the one map region whose failure is a wrong runtime value) |
| a form type with no Papyrus script class and no prior `RegisterObjectType` | **runtime**: logs a "failed to get vm type id" line and registers with a None-typed slot. Fix by calling `RegisterObjectType` first (see [production layout](#production-layout)) |

**Callbacks must be function pointers** (table above), and pass **null-terminated** name arguments (string
literals or stable `constexpr` string_views) — the tasklets path forwards the name to a C-string API, so a
non-terminated view misbehaves when `callableFromTasklets=true`.

---

## Returning values {#returning-values}

For a **non-latent** function the rule is simple: **what you declare is what registers — no cast is ever
needed.** The registered return type is derived from your declared return, and the result is packed as that
same type, so a mismatch can't arise.

- **String:** declare the return `RE::BSFixedString` (canonical). Returning a `std::string` or a
  `std::format(...)` from a `BSFixedString`-declared function compiles fine, because the gate keys on
  string-convertibility. Never `const char*`. A string array is `std::vector<RE::BSFixedString>` or
  `std::vector<std::string>`.
- **Object:** declare the **derived** pointer you actually have and **return it directly — no
  `As<TESForm>()`.** Declaring `RE::Actor*` surfaces `Actor` in Papyrus, and the packed handle matches by
  construction. Returning `std::vector<RE::BGSArtObject*>` uncast works the same way.

The `As<TESForm>()` upcast rule is **latent-only** (next section) — for a non-latent function you never cast.

---

## Latent (delayed-return) functions {#latent-functions}

A **latent** function returns to the game immediately, suspends the calling script stack, and resumes it
later with a result — the mechanism behind Papyrus's `Utility.Wait` and anything else that "waits" without
freezing the game. Reach for it when the work genuinely can't finish synchronously (a timer, an event wait,
a heavy multi-frame job).

The C++ callback must be the **long form returning `RE::BSScript::LatentStatus`**:

```cpp
RE::BSScript::LatentStatus Callback(RE::BSScript::Internal::VirtualMachine* vm,
                                    RE::VMStackID stackID, Base base, Args... args);
// LatentStatus: kFailed (return None + log) or kStarted (script pauses until you resume it)
```

*The latent contract. `R` in `RegisterLatentFunction<R>` is the type the script eventually receives; the
callback itself returns `LatentStatus`. You complete the call later with
`vm->ReturnLatentResult<R>(stackID, result)`.*

Three hard rules, straight from the in-header documentation — these are the ones that bite:

1. **The callback must return fast.** It only *sets up* the work and returns to unblock the game. The actual
   timer / event-wait / crunch runs **elsewhere** (a task, your own scheduler) — never inside the callback.
   The resume step needs the **`stackID` captured from the callback** to know which stack to wake.
2. **Persist across saves.** The player can save mid-latent. Persist the `stackID` plus enough state through
   the co-save serialization and resume on load — otherwise the script event is **permanently blocked**,
   with no result ever returned. (Co-save serialization lives in `threading-and-persistence.md`.)
3. **The result type must match exactly — inheritance is not enough.** If the registered return is
   `TESForm*` and your value is an `Actor*`, you **must** cast: `myActor->As<TESForm>()` before returning.
   This is the exact opposite of the non-latent "no cast" rule above; the latent path packs the declared
   type without the implicit conversion, so a widening mismatch must be spelled out.

**Lineage note.** `RegisterLatentFunction` / `NativeLatentFunction` / `ReturnLatentResult` exist in all NG
lineages (alandtse `ng`, po3-dev, the frozen fork), so the surface is portable. But SKSE's own delay-functor
manager is only a forward-declared stub in CommonLib — an NG plugin normally runs **its own scheduler +
`ReturnLatentResult`** rather than SKSE's delay functors.

---

## Calling into Papyrus (C++ → script) {#calling-into-papyrus}

The other direction — C++ invoking script code — has three channels. Pick by how tightly coupled the two
sides are.

### Direct dispatch with a result callback

Call a Papyrus function directly through the VM and receive the return via a callback you subclass:

```cpp
class MyCallback : public RE::BSScript::IStackCallbackFunctor {
public:
    void operator()(RE::BSScript::Variable a_result) override { /* unpack the return */ }
    void SetObject(const RE::BSTSmartPointer<RE::BSScript::Object>&) override {}
};
auto vm   = RE::BSScript::Internal::VirtualMachine::GetSingleton();
auto args = RE::MakeFunctionArguments(std::move(someForm), 42);
RE::BSTSmartPointer<RE::BSScript::IStackCallbackFunctor> cb{ new MyCallback };
vm->DispatchStaticCall("MyQuestScript", "DoThing", args, cb);
```

*The direct-call idiom. `DispatchStaticCall` runs a global; `DispatchMethodCall` runs a method on a bound
script object or a handle+class name. The result arrives asynchronously through your `IStackCallbackFunctor`.*

`RE::MakeFunctionArguments(args...)` packs each argument (every arg type must be a legal return currency).
Do **not** delete the args object yourself — the VM consumes it during the call. To reach a script instance
attached to a form, resolve a handle from the VM's object-handle policy
(`vm->handlePolicy->GetHandleForObject(form->GetFormType(), form)` — for a script on an alias or active
effect the first argument is that type's *VM type id*, not a FormType), reject the empty handle, then
`FindBoundObject(handle, "ScriptName", object)`.

Three production caveats for this channel:

- **There is no synchronous C++ → Papyrus call.** Dispatch queues the call; the result arrives later,
  through your functor. If C++ needs a script-computed value *during its own setup*, restructure — have a
  small `.psc` shim compute it script-side and push it into a native setter, rather than trying to pull it
  from C++.
- **Your `IStackCallbackFunctor` runs on a VM thread.** Treat it like an event sink: unpack the `Variable`,
  then marshal any game-state mutation through `SKSE::GetTaskInterface()` rather than mutating where you
  stand (`threading-and-persistence.md`).
- **`#undef GetObject`.** If any TU includes `windows.h` (spdlog's MSVC sink pulls it in), the Win32
  `GetObject` macro silently mangles `BSScript::Variable::GetObject` and object-handle code into compile
  errors that never name the real cause. `WIN32_LEAN_AND_MEAN` does **not** cover it.

There is also an NG-exclusive coroutine sugar (`co_await vm->ADispatchStaticCall(...)`), absent from the
older lineages. It's convenient but has no production users in the corpus yet — see [Not yet verified](#not-yet-verified)
before relying on it.

### The event channel — `RegistrationSet` family

For a persistent C++ → script event bus (the "fire an event that any interested script gets") the
production-grade tool is the `SKSE::RegistrationSet` family. It manages the set of listening handles,
persists them to the co-save, and cleans up handles to deleted forms — the bookkeeping you'd otherwise get
wrong:

| Type | Keyed by | Use |
|---|---|---|
| `RegistrationSet<Args...>` | — | global events (`OnActorKilled`) |
| `RegistrationMap<Filter, Args...>` | arbitrary Filter | per-key events (`OnQuestStart` by quest) |
| `RegistrationSetUnique<Args...>` | target REFR FormID | per-object listeners |
| `RegistrationMapUnique<Filter, Args...>` | FormID + Filter | filtered per-object |

**The production thread rule:** when firing from a game-event handler (a `BSTEventSink`), always defer the
send onto the task interface rather than sending synchronously. The `RegistrationSet` family's `QueueEvent`
does this for you (it routes through `SKSE::GetTaskInterface()->AddTask`); its `SendEvent` does not. po3
uses `QueueEvent` in every one of its event handlers. Event sinks themselves are `event-sinks.md`'s lane.

### `ModCallbackEvent` — the lightweight two-way bus

The cheapest bidirectional channel is the mod event: a small `{ eventName, strArg, numArg, sender }` struct
routed through the messaging interface's mod-event source. C++ sends via
`SKSE::GetModCallbackEventSource()->SendEvent(&evn)`, and every script that called
`RegisterForModEvent(eventName, callbackName)` gets its callback queued. Because Papyrus's `SendModEvent`
feeds the **same** source, adding your own sink to it lets C++ hear script-sent (and other plugins') mod
events too. It's the right tool when you want loose coupling and don't need typed arguments beyond one
string, one float, and a sender form.

---

## Thread-safety — the one flag {#thread-safety}

Thread-safety pivots on a single registration argument: `callableFromTasklets` (the 4th arg to
`RegisterFunction`, default `false`).

- **`false` (default)** — the call is deferred to the next frame before your body runs. **Thread-safe**, at
  the cost of up to a frame of latency. This is the right default.
- **`true`** — the call runs immediately in the tasklet. **Only** set this if your function body is itself
  thread-safe. SKSE flags its cheap getters this way; po3 sets it only on trivial pure getters
  (`GetVersion`-style).

Anything that must safely mutate game state from an uncertain context should **not** rely on the flag —
route the work through `SKSE::GetTaskInterface()->AddTask` (main thread) or `AddUITask` (UI thread) instead.
The exact thread on which default-flag bodies run is one of the [unverified](#not-yet-verified) items;
treating "default = deferred and safe, `true` = immediate and your problem" as the contract keeps you
correct regardless.

Three body disciplines that hold regardless of the flag, all learned from production natives:

- **Never block.** No `Sleep`, no spin-wait on a condition (`while (!ref->Is3DLoaded())` is the classic),
  no synchronous disk or network I/O — a blocked native stalls VM stack processing, which under load reads
  as script lag or a whole-game freeze. A "wait" is a latent function or an event registration, never a
  loop in the body.
- **Never let a C++ exception escape.** The VM boundary is not exception-safe; catch inside the body and
  return the benign default.
- **Rate-limit your logging.** A native can be called thousands of times per second from a busy script — a
  per-call `logger::warn` is itself a performance bug.

And never retain a raw form/ref pointer past the call: the VM gives a native no lifetime contract on its
arguments. Store a FormID or handle and re-resolve in the later context (`threading-and-persistence.md`).

---

## Production layout that scales {#production-layout}

One or two functions fit in the minimal skeleton above. A real plugin with dozens of natives wants
structure — this is the shape po3-papyrus-extender uses for ~588 registered functions, and even the tiny
colorglass sample funnels through the same one-callback pattern.

```
src/main.cpp                          # SKSEPlugin_Load: papyrus->Register(Papyrus::Bind)
include/Common.h                      # script-name constant + BIND/BIND_LATENT/BIND_EVENT/STATIC_ARGS macros
src/Papyrus/Manager.cpp               # Papyrus::Bind — the ONE dispatcher, calls every domain Bind
src/Papyrus/ObjectTypes.cpp           # new script object types — bound FIRST
src/Papyrus/Functions/<Domain>.cpp    # one file per Papyrus domain, local Bind(VM&)
include/Papyrus/...                   # header mirror of the src tree
Papyrus/Source/scripts/*.psc          # one .psc per registered class (papyrus-reference's lane)
Papyrus/Scripts/*.pex                 # compiled scripts (via housecarl_compile_script)
```

The load-bearing conventions:

- **One `.cpp` per Papyrus domain** (Actor, Form, Spell…), each with a **local `Bind(VM&)`** that registers
  only its own functions.
- **One top-level dispatcher** (`Papyrus::Bind`) aggregates them. It null-checks the VM and **fails loud** if
  it's missing (`logger::critical("couldn't get VM State"); return false;`), binds **object types first**,
  then every domain. This is the single callback you hand to `Register`.
- **Handlers live in an anonymous namespace**; only the registration callback is exported.
- **One class-name constant, reused everywhere** so the script class name lives in exactly one place.
- **Derive the Papyrus name from the C++ name.** po3's `BIND` macro stringizes the C++ function name
  (`#a_method`) as the registered name — so the two names can't drift. The corollary: rename either side
  and the pair silently breaks (which is why the [boundary](#the-boundary) matters).

**`RegisterObjectType` — for form types vanilla Papyrus lacks.** To bind functions to a type with no vanilla
script class (FootstepSet, LightingTemplate, …), call
`a_vm.RegisterObjectType(static_cast<VMTypeID>(formType), "ClassName")` and ship a one-line stub `.psc`
(`Scriptname FootstepSet extends Form Hidden`). Bind it **before** any function that uses that type — hence
"object types first" in the dispatcher.

**The error-handling contract.** Uniform across po3's corpus: check every pointer argument for null first;
on failure call `a_vm->TraceStack("<Arg> is None", a_stackID)` (or `TraceForm` for form context) and return
a **safe default** (`nullptr` / `false` / `0` / empty vector) — never throw, never crash. This is why the
long signature form (with `VM*` + `stackID`) is worth the extra parameters: it's what lets a bad call
surface in the player's Papyrus log instead of vanishing.

**Version-guarding is consumer-facing, not registration-side.** The DLL always registers its full surface;
guarding is how a *script* detects whether the DLL is present and current. Ship a native version getter, a
presence probe (`IsPluginFound` / SKSE's `GetPluginVersion`, which returns -1 when not loaded), and — SKSE's
trick — a **non-native** script-side version constant so a script can catch a stale-`.psc`-vs-installed-DLL
mismatch. po3 handles per-runtime differences at compile time because it builds one DLL per runtime; an **NG
single-DLL plugin folds those into runtime checks instead** (see `multi-runtime.md`).

**Script-side home — own a new `Hidden` class.** Two models exist: extend a vanilla class (SKSE-internal
only — it merges vanilla + modified `.psc` per class, which two mods can't both do), or **own new `Hidden`
script class(es)** and take target objects as explicit parameters of global functions. The second is the
default for any third-party plugin. The exact `.psc` syntax for those classes is papyrus-reference's lane;
`housecarl_compile_script` compiles them.

---

## Not yet verified {#not-yet-verified}

Everything above is drawn from reading the CommonLibSSE-NG headers and production exemplars. Several
behaviors can only be settled by an actual build + in-game test, and are called out here so the skill never
presents an unverified runtime behavior as proven:

- **The whole latent surface is header-only** — no production plugin in the mined corpus registers a latent
  function. Register one, suspend a script, resume from a task, and confirm the round-trip (including the
  exact-type-match and save-across-latent rules) before teaching it as reliable.
- **The coroutine `co_await ADispatch*` path** likewise has no production users — verify before relying on
  it.
- **Which OS thread** runs default-flag native bodies and VM tasklets, and whether events / latent resumes
  are safe from a background thread. Until measured, route game-state mutation through the task interface.
- **The latent exact-type mismatch failure mode** (silent None? crash? log line?) is unknown.
- **Whether a plugin that omits its SKSE declaration truly fails to load** (asserted by docs, not confirmed
  here) — a `plugin-skeleton.md` concern, noted here for completeness.
- **The parameter-count cap** behavior at 11+ params, **case-insensitive** class/function name matching,
  **duplicate-registration** behavior, and the exact **compiler diagnostic text** for the rejected
  signatures — all header-implied, none build-confirmed.
- **`IFunctionArguments` ownership** after a dispatch call — the VM consumes it synchronously, but whether it
  then frees it or leaks per call is unproven.
- **Where `TraceStack`/`TraceForm` messages land** (expected: the player's Papyrus log) and whether a native
  called from a `.psc` whose DLL is absent fails at the script level or crashes.

None of these blocks writing a working plugin — the registration idiom, the marshalling map, and the
production layout are all solidly header-confirmed; these are the empirical follow-ups a build-test closes.
