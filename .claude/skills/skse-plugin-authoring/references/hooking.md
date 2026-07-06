# Patching game code — REL relocation, the trampoline, and the hook primitives

How a native SKSE plugin intercepts the game's own code: how it names a game address
without baking it into the DLL, and how it redirects a call, a virtual, or a run of
instructions through your code. Read this before writing any hook — three of the rules
here are "compiles clean, CTDs at runtime" traps a build never warns you about, and
getting the address layer wrong takes the whole DLL down on the next game update.

This is the C++ memory-patching layer — it says nothing about the `.psc` Papyrus surface. A
hooked C++ thunk signature and a vtable slot are not Papyrus function signatures; never treat
one as the other, and when you need a Papyrus signature that is `papyrus-reference`'s job, not
this file's.

The CommonLib lineage this targets is **alandtse/CommonLibVR branch `ng`** — the
charter-locked library. Where a mechanism differs across lineages (powerof3's `dev` fork has
no VR and no `VariantID`; the CharmedBaryon fork is frozen, its wiki prose cross-check only)
the difference is flagged so you can read borrowed code correctly — but do not switch lineages
on the strength of a note here.

## Contents

- [Why you never compile an address](#why-you-never-compile-an-address)
- [The address type zoo](#the-address-type-zoo) — `REL::ID`, `RelocationID`/`RELOCATION_ID`, `VariantOffset`, `Relocation<T>`
- [The must-be-static rule](#the-must-be-static-rule) · [thunk signatures](#thunk-signatures-must-byte-match) · [vtable index hazard](#vtable-hooks) — the three CTD-at-runtime correctness gates
- [The trampoline and its two budgets](#the-trampoline-and-its-two-budgets) · [the four write verbs](#the-four-write-verbs) · [the struct + static-thunk idiom](#the-struct--static-thunk-idiom)
- [Vtable hooks](#vtable-hooks) · [byte patches and mid-function detours](#byte-patches-and-mid-function-detours)
- [Re-entrancy](#re-entrancy--a-hook-on-a-funnel-function-can-call-itself) — funnels that re-trigger themselves
- [Verify before you patch — houseCARL discipline](#verify-before-you-patch--housecarl-discipline)
- [When to hook, sink, or call native](#when-to-hook-sink-or-call-native) · [not yet verified in-game](#not-yet-verified-in-game)

---

## Why you never compile an address

Skyrim's exe moves every function and global to a new address on each game update, and ASLR
shifts the module base on every launch — a DLL that compiled in a literal `0x14012A0B0`
would crash after any patch. The Address Library layer (the `REL` namespace) exists so no
absolute address ever enters the DLL. You name a stable **Address Library ID** (meh321's
"versionlib" database, published per exe version); `REL` looks it up to a byte offset at
runtime and adds the live module base, so one DLL runs on every game version the Address
Library covers. The whole edifice is one identity:

```cpp
ID::address() == Module::get().base() + IDDatabase::get().id2offset(id)
```

That yields the correct live address regardless of game version or ASLR (`ID.h:452,456,459`);
the database is parsed once per process into a named shared-memory map shared across every
SKSE plugin (`ID.cpp:106-116`).

**The library fails loud on a missing ID** — if an ID isn't in the database for the running
version, `REL` aborts with an "incompatible with the address library" diagnostic
(`ID.h:191-198`), never a silent wrong address; a missing database *file* points the user at
the address-library mod page (`ID.cpp:120-129`). What it *cannot* catch is an ID that
resolves to a **wrong-but-present** address — a stale or mistyped ID that happens to exist
points at the wrong code and crashes when you call or hook it. `REL` proves *presence*, not
*semantic correctness* — which is why the patch layer offers byte-pattern verification (see
[Verify before you patch](#verify-before-you-patch--housecarl-discipline)) as a pre-write
guard.

---

## The address type zoo

The author-facing types, in the order you reach for them:

| Type | Constructed with | Resolves to | Use when |
|---|---|---|---|
| `REL::ID` | one Address Library ID | `base + db[id]` | a single-target build, or an ID identical across versions |
| `REL::RelocationID` | `(seID, aeID)` or `(seID, aeID, vrID)` | picks ID by `GetRuntime()`, then `base + db[id]` | **the default for one DLL across SE + AE**; the 2-arg form reuses the SE ID for VR |
| `REL::Offset` | raw module-relative byte offset | `base + offset` (no DB lookup) | you already know a fixed offset, not an ID |
| `REL::VariantOffset` | `(seOff, aeOff, vrOff)` | picks a raw offset by `GetRuntime()` | a fixed offset that differs per runtime — especially a call-site offset *inside* a function |
| `REL::VariantID` | `(seID, aeID, vrOffset)` | SE/AE via DB, **VR via raw offset** | VR lacks addresslib coverage for this target; also the form the generated vtable tables use |
| `REL::Relocation<T>` | any of the above (± an extra byte offset) | a typed handle over the resolved address | **the wrapper you actually hold and use** |

Sources: `ID.h:437-462` (`ID`), `:464-548` (`RelocationID`), `:551-610` (`VariantID`); `Offset.h:7-93` (`Offset`/`VariantOffset`); `Relocation.h:558-803` (`Relocation<T>`).

**`REL::Relocation<T>` — the workhorse.** `Relocation<T>` resolves the address **once in its
constructor**, caches it, and thereafter acts as the typed handle: `operator*` / `operator->`
to dereference a data pointer,
`operator()` to call when `T` is a function type, `.address()` / `.get()` for the raw
address (`Relocation.h:666-697`; cached in `_impl` at `:582`). That one-time resolution is
what makes the static rule below load-bearing.

**`RELOCATION_ID(se, ae)` — and the lineage trap.** This is the canonical macro for one DLL
across both game families. In `ng` it expands to a **runtime-dispatched dual-ID object**:

```cpp
#define RELOCATION_ID(a_se, a_ae) REL::RelocationID(a_se, a_ae)   // ng SKSE/Impl/PCH.h:660
```

The 2-arg ctor stores `_seID`, `_aeID`, and `_vrID = _seID` — **VR reuses the SE ID**
(`ID.h:469-482`); at access `RelocationID::id()` switches on `Module::GetRuntime()` and
routes the chosen ID through the same `id2offset` lookup. A 3-arg `RelocationID(se, ae, vr)`
overload (`ID.h:484-498`) targets VR at a *different* function — so the "VR can't have its
own target" limit is the 2-arg macro's, not `RelocationID`'s.

The trap: **the same macro means something structurally different across lineages.** In
powerof3's `dev` fork, `RELOCATION_ID(SE, AE)` expands to a single *compile-time-fixed*
`REL::ID` chosen by build define (`po3-dev PCH.h:546/548`), not a runtime dual-ID object —
so code copied between lineages can compile yet mis-resolve. The `ng` mitigation: **save the
result with `auto`**, so your variable takes whichever type the macro produces wherever you
compile (`ng-wiki/Runtime-Targeting.md:96,105`). Teach `auto` for any `RELOCATION_ID` result.

**`REL::VariantOffset` — the NG-native call-site selector.** An Address Library ID locates
a **function**; the byte offset of a specific call *instruction inside* it drifts across
game versions independently. The `ng`-native answer is `REL::VariantOffset(seOff, aeOff,
vrOff)` — a **runtime class**, not a macro (`Offset.h:32`); all three args are raw byte
offsets, and `.offset()` picks one at run time via `switch (Module::GetRuntime())`
(`Offset.h:59-77`). One DLL serves all three runtimes.

```cpp
// oar/src/Hooks.h:17-19 (verbatim) — resolve the function by ID, then add the runtime-correct call-site offset inside it
const REL::Relocation<uintptr_t> mainHook{ REL::VariantID(35565, 36564, 0x5BAB10) };
SKSE::AllocTrampoline(14);
_Nullsub = trampoline.write_call<5>(
    mainHook.address() + REL::VariantOffset(0x748, 0xC26, 0x7EE).offset(),
    Nullsub);
```

SE and VR offsets are frequently identical (VR often shares SE's binary layout), but you still
pass three explicit values.

**Do not emit `OFFSET(se, ae)` / `OFFSET_3(se, ae, vr)` in NG code.** Those are compile-time
preprocessor macros defined per-project in a PCH (e.g. `spid/SPID/src/PCH.h:75-84`), not in
any CommonLib header — they resolve to a single value at build time (a single-runtime
binary) and are **undefined in `ng` headers** (a compile error there). `VariantID` /
`VariantOffset` are `ng`-only (absent from `po3-dev`). You'll see the `OFFSET` macros in
borrowed po3-lineage code; read them, but write `VariantOffset` for `ng`. The full
multi-runtime footgun catalog is `multi-runtime.md`.

---

## The must-be-static rule

**(Correctness gate 1 of 3.)** Because `Relocation` caches its resolved address at
construction, a non-static function-local re-runs the `id2offset` binary search on every
construction — and a function-local is reconstructed on every call. `static` resolves the ID
once and persists it. CommonLibSSE-NG's own author guidance (`ng/CLAUDE.md:432-436`):

```cpp
// WRONG — reconstructed (and re-looked-up) every call:
REL::Relocation<Type*> singleton{ REL::ID(12345) };
// CORRECT — resolved once, persists across calls:
static REL::Relocation<Type*> singleton{ REL::ID(12345) };
```

Store every hook's captured original the same way:
`static inline REL::Relocation<decltype(thunk)> func;`. This is co-equal with the [thunk
signature](#thunk-signatures-must-byte-match) and [vtable index](#vtable-hooks) gates — all
three compile clean and bite only at runtime.

---

## The trampoline and its two budgets

A `write_call` / `write_branch` hook overwrites the instruction at the target site with a
jump into your code, but that site is only 5 or 6 bytes — too small for an absolute 64-bit
address. The **trampoline** is a near-module scratch region SKSE reserves so the short jump
can reach a small stub that in turn holds the full 64-bit target. Reserve space with
`SKSE::AllocTrampoline(N)` *before* writing; every write draws sequentially from that shared
bump-pointer pool, and under-reserving — even by a codegen body's size — `report_and_fail`s
at the next allocate (`API.cpp:256-268`, `Trampoline.cpp:114-124`). **Allocate before you
write, sized to the hooks you install.**

The "14 bytes per hook" shorthand is only half the story — there are **two budget
formulas**:

- **`write_call<5>` / `write_branch<5>`** funnel into a 14-byte stub (`FF 25 00000000` +
  8-byte absolute address; `static_assert(sizeof == 0xE)`), so each needs
  `AllocTrampoline(14)` (`Trampoline.cpp:141-160`).
- **`write_branch<6>`** (the mid-function-detour form) allocates only a bare 8-byte
  pointer slot (`Trampoline.cpp:198-202`), so a branch-to-Xbyak-stub detour reserves
  `8 + patch.getSize()` — 8 for the slot plus the codegen body `trampoline.allocate(patch)`
  copies in.

Both disciplines are production-valid: SPID pre-allocates the whole budget up front
(`spid/SPID/src/main.cpp:187-190`, `AllocTrampoline(14 * 14)`); OAR calls
`AllocTrampoline(14)` immediately before each `write_call` (`oar/src/Hooks.h:18,41,46,…`).
Pick either; just size to the hooks.

---

## The four write verbs

`REL::Relocation` is the entry point for all four code-patching verbs. The flow is always
**resolve target via ID → hold as a static `Relocation` → call the write verb on it.**

| Verb | Mechanism | Trampoline? | Source |
|---|---|---|---|
| `write_call<N>(fn)` | routes through `SKSE::GetTrampoline().write_call<N>` | **yes** | `Relocation.h:760-772` |
| `write_branch<N>(fn)` | routes through `SKSE::GetTrampoline().write_branch<N>` | **yes** | `Relocation.h:746-758` |
| `write_vfunc(idx, fn)` | swaps a pointer at `address() + sizeof(void*)*idx` | **no** | `Relocation.h:780-795` |
| `REL::safe_write<T>(addr, bytes)` | protect → memcpy → restore over a byte range | **no** | `Relocation.cpp:8-24` |

The `N` on `write_call<N>` / `write_branch<N>` **selects the opcode form and must match the
byte length of the instruction being replaced** (`Trampoline.h:81-129`); both **return the
original address** — store it as your struct's `func` and chain to it:

| Template | Opcode written at the site | Reaches |
|---|---|---|
| `write_branch<5>` | `0xE9` JMP rel32 (5 B) | any 64-bit addr, via trampoline |
| `write_branch<6>` | `FF /4` JMP r/m64 (6 B) | any 64-bit addr (RIP-relative absolute) |
| `write_call<5>` | `0xE8` CALL rel32 (5 B) | via trampoline |
| `write_call<6>` | `FF /2` CALL r/m64 (6 B) | any 64-bit addr |

**These verbs patch *instructions*, not functions.** `write_call<N>` / `write_branch<N>` overwrite one
existing call/branch instruction (or, with the Xbyak detour below, a hand-measured run of instructions).
CommonLib ships **no prologue-copying detour** — nothing relocates a function's first instructions and hands
you back a callable original. Pointing `write_branch` at a function's *entry* replaces the function outright
with no way to chain: the returned "original" is only meaningful when the bytes you displaced were themselves
a branch/call. To intercept a whole function *and still call it*, hook its call sites, take its vtable slot,
or bring a real detour library (safetyhook / MinHook) — don't bend these primitives into one.

---

## The struct + static-thunk idiom

The canonical production hook is a **struct carrying a static `thunk` and a static `func`**,
identical in shape whether it's a call-hook or a vtable-hook:

```cpp
// po3-papyrus-extender/src/Game/HookedEventHandler.cpp:8-25
namespace BooksRead {
    struct Read {
        static bool thunk(RE::TESObjectBOOK* a_this, RE::TESObjectREFR* a_reader) {
            const auto result = func(a_this, a_reader);        // 1) chain the original first
            if (a_this && a_reader->IsPlayerRef()) {           // 2) do our work
                GameEventHolder::GetSingleton()->booksRead.QueueEvent(a_this);
            }
            return result;
        }
        static inline REL::Relocation<decltype(thunk)> func;   // 3) the captured original (STATIC)
    };
    void Install() {
        REL::Relocation<std::uintptr_t> bookMenu{ RELOCATION_ID(50122, 51053), OFFSET_3(0x22D, 0x231, 0x295) };
        stl::write_thunk_call<Read>(bookMenu.address());       // (this example is po3-lineage — note the OFFSET_3 macro)
    }
}
```

The three parts: `thunk` carries the byte-exact target signature; it calls `func(...)` to
chain to the captured original; `func` is `static inline REL::Relocation<decltype(thunk)>`,
filled with the original at install time. Binding `func`'s type from `decltype(thunk)` means
**the thunk's own signature also types the pass-through call** — so a wrong thunk signature
poisons both the interception ABI and the re-call to the original at once.

**The ergonomic wrappers are per-project, not library-provided.** The core library ships
only the raw primitives (`write_call<N>`, `write_branch<N>`, `write_vfunc`); the convenience
wrappers you'll see in borrowed code — `write_thunk_call`, `write_vfunc<F,idx,T>`,
`hook_function_prologue`, `install_hook` — are defined in each project's own PCH
(po3-papyrus-extender at `PCH.h:122-167`; SPID's `install_hook` in a third-party submodule,
`adya/SKSEHooking`, unread in this corpus). For `ng`-native work, **write the raw core API —
OAR's form — and treat the wrappers as optional author-defined sugar.** OAR, the only
`ng`-lineage exemplar, uses none.

**Keep the thunk tiny and cold-path free.** A hooked site can run thousands of times a second. Inside a
thunk: validate only what you actually touch, do the minimal work, chain to `func`. No per-call logging
(log the install once, at install time), no allocation, no lock acquisition you can avoid, no config
parsing, no Papyrus dispatch. Anything heavy gets its inputs snapshotted (by handle, not raw pointer) and
deferred through the task interface (`threading-and-persistence.md`).

---

## Thunk signatures must byte-match

**(Correctness gate 2 of 3.)** A hooked thunk's signature must byte-match the target function
exactly — return type, every parameter in order and type, and for a member/virtual function
the leading explicit `this` — or the game corrupts its stack and CTDs at call time. The `ng`
guidance elevates this (`ng/CLAUDE.md:388`, "Always verify virtual function signatures match
the base class exactly, including all parameters") with a worked case where a dropped
trailing parameter silently fails to override:

```cpp
// ng/CLAUDE.md:396-404 — base is: void OnVisible(NiCullingProcess&, std::int32_t)
void OnVisible(NiCullingProcess& a_process) override;                               // WRONG — missing param; does not actually override
void OnVisible(NiCullingProcess& a_process, std::int32_t a_alphaGroupIndex) override; // CORRECT — matches the base signature
```

**Why the mismatch compiles clean.** Every install path type-erases your function
pointer through `stl::unrestricted_cast<std::uintptr_t>` before the write — a reinterpret-
style cast with no compatibility check (`PCH.h:607-608`). The target's true signature never
reaches the compiler at the write site, so no diagnostic is possible (`Relocation.h:771`
`write_call`, `:794` `write_vfunc`, `:757` `write_branch`; `Trampoline.h:126-128`).
Lineage-independent — po3-dev uses the identical cast (`po3-dev/Relocation.h:353,367,390`).

**Explicit `this` for member/virtual hooks.** Because you install a free/static function
where the game expects a member call, the thunk's first parameter must be the explicit
`this`, typed to the concrete object (`oar/src/Hooks.h:94`, `RE::hkbClipGenerator* a_this`
leading). Omit it and every later argument shifts one register slot → immediate stack
corruption. Preserve the full parameter tail too (`Hooks.h:97`).

**The check before you install any hook** — compilation proves *none* of these:
1. return type matches (width matters — `bool` vs `void` vs pointer);
2. every parameter present, in order, typed (a dropped trailing param is the classic
   silent break);
3. a leading explicit `this` for member/virtual hooks;
4. store the original as `static inline REL::Relocation<decltype(thunk)> func;`.

---

## Vtable hooks

Point a `REL::Relocation<std::uintptr_t>` at a class's `VTABLE_<Class>[n]` entry, then call
`write_vfunc(idx, Hook)` — it swaps the pointer at that slot and returns the original for
chaining. **No trampoline**; it just overwrites a table entry:

```cpp
// alandtse-ng/include/REL/Relocation.h:780-795 — the core primitive
std::uintptr_t write_vfunc(const std::size_t a_idx, const std::uintptr_t a_newFunc)
    requires(std::same_as<U, std::uintptr_t>)
{
    const auto addr   = address() + (sizeof(void*) * a_idx);      // slot = vtableBase + 8*idx (x64)
    const auto result = *reinterpret_cast<std::uintptr_t*>(addr); // capture the original
    safe_write(addr, a_newFunc);                                  // overwrite the slot
    return result;                                                // hand the original back to chain
}
```

The `requires(std::same_as<value_type, std::uintptr_t>)` constraint means `write_vfunc` is
**only callable on a `REL::Relocation<std::uintptr_t>`** — declare the handle with exactly
that type, never a typed pointer (`:782`, `:792`). It's memory-safe because `safe_write` flips
the target page to `PAGE_EXECUTE_READWRITE`, writes, then restores protection
(`Relocation.cpp:8-24`) — mandatory, since game vtables sit in read-only sections.

**The `VTABLE[n]` array shape.** Each RE class exposes `VTABLE = VTABLE_<Class>`, an
`std::array<REL::VariantID, N>` where **N = the number of distinct vtables the class has**
(one per inherited base with virtuals):

```cpp
// alandtse-ng/include/RE/Offsets_VTABLE.h — N = distinct vtables (BSExtraData 1; TESObjectREFR 4; Actor 10)
constexpr std::array<REL::VariantID, 1> VTABLE_BSExtraData{ REL::VariantID(228901, 186634, 0x159d008) };  // :62
```

- **Element `[n]` = which vtable** (which inherited base). `[0]` is the primary (first-base)
  vtable; `[n>0]` is the (n+1)th inherited base's vtable. To hook a virtual a class overrides
  from a **secondary** base, use the matching `[n]`, not `[0]` — OAR reaches
  `TESObjectREFR`'s `IAnimationGraphManagerHolder` interface (its 4th base) via `[3]`
  (`oar/src/Hooks.h:75-76`; inheritance order at `TESObjectREFR.h:104-108`).
- **Each `VariantID(seID, aeID, vrOffset)`** resolves the runtime-correct vtable *base*
  automatically — addresslib for SE/AE, a raw offset for VR, switched on `GetRuntime()`
  (`ID.h:556-596`). You never pick the base per runtime — only the function index.

```cpp
// oar/src/Hooks.h:22-23 — primary-vtable hook: function index 0x4 in hkbClipGenerator's [0] vtable
REL::Relocation<std::uintptr_t> hkbClipGeneratorVtbl{ RE::VTABLE_hkbClipGenerator[0] };
_hkbClipGenerator_Activate = hkbClipGeneratorVtbl.write_vfunc(0x4, hkbClipGenerator_Activate);
static inline REL::Relocation<decltype(hkbClipGenerator_Activate)> _hkbClipGenerator_Activate; // :121 — stored original, invoked to chain
```

### The multi-runtime index hazard

**(Correctness gate 3 of 3.)** The vtable *base* is runtime-selected automatically, but the
**function index you pass to `write_vfunc` is not.** Skyrim VR inserts extra virtual functions
mid-vtable, shifting every later slot — so where SE/AE run
`Begin(01)→End(02)→Update(03)→GetRotation(04)`, VR runs
`Begin(01)→End(02)→Unk_03(03)→Update(04)→GetRotation(05)`, and a single index correct on SE/AE
is off-by-N on VR (instant CTD, or a silent wrong-function hook — `ng/CLAUDE.md:217-224`).

Two author responses: per-runtime builds use a compile-time index switch (`#ifndef SKYRIMVR
0x0AB #else 0x0AD #endif`, po3's `Character::Resurrect` at `HookedEventHandler.cpp:695-701`);
a single `ng` build uses runtime dispatch on the **calling** side via `REL::RelocateVirtual`.
Its mechanics and the whole vtable-slot-shift hazard are `multi-runtime.md`'s territory — go
there before you ship a single hardcoded index across SE/AE/VR.

---

## Byte patches and mid-function detours

**The lightest "hook" is not a hook** — a static byte overwrite via `REL::safe_write<T>`, e.g.
NOPing an instruction or flipping an opcode (OAR neutralizes a store and flips `movsx`→`movzx`,
`oar/src/Hooks.h:61-62`). No `AllocTrampoline` needed.

**The mid-function detour — `write_branch<6>` + Xbyak.** When the hook point is a *run of
instructions* (not a clean call site or vtable slot), the tool is `write_branch<6>` to an
Xbyak-generated stub — a **first-class primary tool**, not a rare escape hatch (OAR uses it
six times in one function, `oar/src/Hooks.cpp:379-608`). The canonical OAR shape, in order:

```cpp
// oar/src/Hooks.cpp:444-467 (shape)
struct PatchFunc3A : Xbyak::CodeGenerator {                 // 1. build replacement code as an Xbyak stub
    explicit PatchFunc3A(uintptr_t a_originalAddr, uintptr_t a_jumpAddr) {
        Xbyak::Label originalLabel, jumpLabel;
        cmp(edx, static_cast<uint16_t>(-1));                //    re-emit / augment the displaced logic
        je(jumpLabel);
        jmp(originalLabel);
        L(originalLabel); jmp(ptr[rip]); dq(a_originalAddr); //    resume in game (path A)
        L(jumpLabel);     jmp(ptr[rip]); dq(a_jumpAddr);     //    resume in game (path B)
    }
};
PatchFunc3A patch(func3.address() + 0x32, func3.address() + 0x300);
patch.ready();
SKSE::AllocTrampoline(8 + patch.getSize());                 // 2. reserve 8 (branch<6> slot) + the stub's size
REL::safe_write<uint8_t>(func3.address() + 0x29, patchNop9); // 3. NOP the displaced bytes...
trampoline.write_branch<6>(func3.address() + 0x29, trampoline.allocate(patch)); //    ...then branch to the pooled stub
```

The mechanics that make it correct:

- **The NOP width is the displaced-byte count, not 6.** `write_branch<6>` overwrites only 6
  bytes; any original bytes past those 6 must be pre-NOP'd so no partial instruction survives.
  OAR carries sled widths of 5, 9, 10, 12, 13 bytes (`Hooks.cpp:334-335, 428-430`) and picks
  the one matching the site.
- **`jmp(ptr[rip]); dq(originalAddr)` is the return-to-game tail** — the stub embeds the 8-byte
  absolute resume address inline (`dq`) and jumps through it; the resume address is the game
  address just *past* the patched region. A branchy detour embeds two tails, one per condition.
- **Why `FF /4` (6-byte) not `E9` (5-byte)?** The 6-byte form is a RIP-relative absolute
  `jmp r/m64` reading a full 64-bit target from the slot, so it reaches the pool at any
  distance; `E9 rel32` is limited to ±2 GB (`Trampoline.h:89-92`). AE-vs-SE register
  differences are branched *inside* the generator via `REL::Module::IsAE()` (`Hooks.cpp:354-358`).

---

## Re-entrancy — a hook on a funnel function can call itself

A hook (or event handler) whose body performs an engine side effect can re-trigger the very path it
patched **synchronously, on the same stack**: toggling controls fires user-event notifications, a UI
refresh re-runs observers, an equip fires equip events. If your dedup/guard state is armed *after* the
side effect, the re-entrant call sees an unarmed guard and recurses — unbounded recursion that presents in
game as a freeze (the main thread pinned by the recursion) and then a **stack-overflow CTD**, a combination
that reads as a pure hang and hides the crash log. Two disciplines, both distilled from real shipped-plugin
failures:

- **Arm guard state before the first side effect**, not after the call that can re-enter.
- **Give every funnel a re-entrancy guard** — a function-local `static bool` (or `thread_local`) with an
  RAII reset. It's cheap, and it protects *every* caller of the funnel, not just the code path you were
  thinking about when you wrote it.

The corollary for null-safety: a handle that resolved fine on the first, legitimate call can resolve
**null** on a re-entrant or storm-repeated one. Null-check every resolution inside a handler, even for
payloads the dispatching framework "always" populates.

---

## Verify before you patch — houseCARL discipline

Here the library's direction and production practice diverge; name the gap rather than paper
over it. **The library now offers a verified-write family — but only in `ng`.** On top of the
plain unverified `void safe_write(dst, src, count)`, alandtse-ng adds `safe_write` overloads
that return `bool` and write only if the expected bytes match, plus a `verify_code` primitive
and `VERIFY_AND_PATCH` / `VERIFY_AND_FILL` macros (`Relocation.h:207-460`). The paired
byte-pattern DSL is `make_pattern`:

```cpp
// the ng-recommended idiom (docs/VERIFICATION_MIGRATION.md:33-35, 100-107)
auto pattern = make_pattern<"48 8B 05 ?? ?? ?? ??">();   // hex pairs + ?? wildcards; parsed at compile time
if (!REL::safe_write(address, patch_data, sizeof(patch_data), pattern)) {
    logger::warn("Patch verification failed at {:x} - game may have updated", address);
    return false;                                        // don't crash, just skip the patch
}
```

`make_pattern` is `consteval`, so a malformed pattern is a compile error, and its wildcards
let a pattern survive minor game updates (`Pattern.h:107-189`). Two failure disciplines must
not be conflated: a **verified `safe_write` silently skips** on mismatch (returns `false`,
writes nothing — safety depends on the caller checking the bool), while
**`PatternMatcher::match_or_fail` fails loud** with a version-mismatch `report_and_fail`.
Note that `verify_code(addr, nullptr, 0)` returns `true` — an empty expected buffer disables
verification silently.

**Lineage, and how "deprecated" is overstated.** `make_pattern` is shared across lineages,
but the verified-*write* wrapper is `ng`-only (po3-dev's `safe_write` is void-only — scope
any verify teaching to `ng`). The `[[deprecated]]` on the plain form is **opt-in**: it and
the runtime `AUDIT:` warning are gated behind `REL_AUDIT_UNVERIFIED_PATCHES`, a migration
switch you add to *find* unverified patches then remove (`Relocation.h:204-207`,
`VERIFICATION_MIGRATION.md:44-77`) — in a default build the plain form compiles silently. So
the library *offers* verification and *flags* the unverified form under an audit build; it
does not deprecate it by default.

**The honest divergence: production ships raw offsets.** None of the three exemplars use the
verified/pattern surface — a grep for `make_pattern` / `match_or_fail` / `verify_code` across
OAR, SPID, and po3-papyrus-extender returns zero hits, and the wiki documents none. The field
norm is raw hardcoded offsets; verification is the library's *recommended* direction, not an
established community practice.

**houseCARL's position.** Because the unverified path either silently skips or wrongly writes
across a game update, houseCARL treats **verify-before-patch as its own discipline** — the Q3
"no silent failure" value applied to memory patching. It prefers `make_pattern` + a checked
bool (skip-safe) or `match_or_fail` (fail-loud) over a bare hardcoded offset, and says plainly
that this is a houseCARL choice *diverging from* exemplar practice, not a community norm —
present it to the modder that way.

---

## When to hook, sink, or call native

Reach for the lightest tool that does the job — this ranking is empirical, from production:

| Technique | Use when | Trampoline? |
|---|---|---|
| **event-sink** (`BSTEventSink`) | the engine already dispatches the event you need — no code patching, safest | no |
| **native function call** | the game already exposes the function; you just want to *call* it, not intercept it | no |
| **vtable-hook** (`write_vfunc`) | intercept a **virtual** member fn for **all instances** of a class; minimal footprint | no |
| **call/branch-hook** (`write_call` / `write_branch<5>`) | intercept **one specific call site** or a non-virtual function | yes (14 B) |
| **mid-function detour** (`write_branch<6>` + Xbyak) | the hook point is a run of instructions, not a clean site | yes (8 + stub) |
| **byte-patch** (`safe_write`) | a static instruction rewrite (NOP, opcode flip) | no |

**Prefer a sink to a hook whenever the engine already dispatches the event.** SPID ships a
full plugin with **zero** vtable/call hooks (a grep for `write_vfunc` in SPID finds nothing),
using event sinks instead; po3-papyrus-extender and SPID hook only where no native event
exists (po3 hooks BooksRead, FallDamage, ItemCrafted, Weather, FastTravel — all sinkless).
The engine-event inventory and registration timing are `event-sinks.md` — go there before
hooking anything the engine might already broadcast; exposing or calling native functions is
`native-papyrus-functions.md`.

**Prefer a plugin's published API to patching it — and never become the second writer.** When
the behavior you want to change lives in another plugin (a framework managing scale, morphs,
camera, UI state…), check for an exported interface first (`RequestPluginAPI_*` exports, or a
messaging interface exchange — `plugin-skeleton.md`) before reaching for a hook. And if a
framework *actively manages* some engine state, do not install yourself as a second writer to
that state: it re-asserts its own value on its own schedule, and the result is a visible
ping-pong war you cannot win from outside — each round a pop the player sees. Correct the
**input** the framework computes from (a config value, a query it makes, a value it reads once
at setup) rather than fighting its **output**. When consuming another plugin's API, vendor its
published interface header at the exact installed version — never hand-model another plugin's
vtable from guesswork.

**Install once.** One install entry point per hook cluster, one message-phase branch, every
original written a single time — no re-install path, no per-object hooking (vtable writes
patch the shared class vtable once). Match the install phase to the hook's data dependency:
OAR installs inside `SKSEPlugin_Load` for pure engine-code hooks; SPID at `kPostLoad` gated
on having work; po3-papyrus-extender at `kDataLoaded` for hooks that depend on live
singletons. The message-phase lifecycle is `plugin-skeleton.md`; a failed load is
`load-failures.md`.

---

## Not yet verified in-game

Everything above is read from CommonLibSSE-NG source and three production plugins, but a few
runtime behaviors are asserted by that code without being exercised in a build we've run.
Treat these as sound-but-unproven; confirm the exact failure mode with a build-test first:

- **Wrong-but-present ID / mistyped offset.** Fail-loud is proven only for a *missing* ID;
  whether an ID/offset resolving to a wrong-but-valid address CTDs on invocation or
  mis-executes silently is unconfirmed.
- **Thunk-signature mismatch.** The gate exists because a byte-mismatched thunk CTDs, but
  whether a dropped param / wrong return width / missing `this` crashes immediately, later,
  or corrupts silently is not exercised in the corpus.
- **Per-runtime vtable index.** No live confirmation that a hardcoded index (OAR's `0x4`
  for `hkbClipGenerator`, po3's `0x0AB` for `Character::Resurrect`) lands on the intended
  virtual on each of SE, AE, and VR — a per-runtime check code inspection can't make.
- **Newly authored mid-function detour.** For a detour you write yourself (vs reusing OAR's
  validated offsets), the NOP width matching instruction boundaries and clean control resume
  are per-site properties needing disassembly. The resume-offset principle (resume = site +
  displaced bytes) is teachable; concrete offsets are not.
- **Install-phase timing.** Whether installing too early (before the target is mapped)
  crashes or no-ops vs a later phase is asserted by the phase rule, not proven here.
