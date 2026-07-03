# Multi-runtime discipline — one DLL for SE + AE + VR

Skyrim SE (1.5.x), AE (1.6.x), and VR (1.4.15) do not share a memory layout or a vtable shape. This
reference is the one the skill leans on **every time it writes engine-touching C++** — anything that reads a
class member, calls a virtual, or casts up an inheritance chain. Read it before authoring any of that,
because the dominant failure mode is **not a compile error**: it is a silently-wrong memory read or a CTD
that appears only once VR is in the target set, from code that compiled clean under a single-runtime test.

The engine that makes one DLL span all three runtimes is **CommonLibSSE-NG** — the `alandtse/CommonLibVR`
branch `ng`, houseCARL's charter-locked target. Everything below is the NG idiom; a lineage caution at the
end explains why code copied from the older po3 fork (or SPID / po3 Papyrus Extender) is *not* portable into
an NG single-DLL build.

**Boundary — this is the C++ / vtable / memory surface only.** A `RelocateVirtual` vtable slot or a
`RUNTIME_DATA` C++ field is never a Papyrus function signature. houseCARL's `papyrus-reference` skill owns
the `.psc` surface; never state a Papyrus signature as though it were derived from these C++ headers. Where
the C++/Papyrus line is crossed, say so and hand off.

Provenance is cited as `path:line` against the pinned NG corpus (`alandtse/CommonLibVR` @ `8c048b3`, its
bundled `CLAUDE.md`, the CommonLibSSE-NG wiki, and real plugins OAR / SPID / po3 Papyrus Extender), so a
surprising claim traces back to source.

## Contents

- [The core problem](#the-core-problem) — why one layout can't serve three runtimes
- [The footgun catalog](#the-footgun-catalog) — **the centerpiece**: mistake → symptom → fix
- [Compile-time vs runtime — the decision rule](#compile-time-vs-runtime--the-decision-rule)
- [The RUNTIME_DATA accessor rule](#the-runtime_data-accessor-rule) — why direct member access reads wrong memory
- [The three conditional patterns](#the-three-conditional-patterns) — exclusive virtuals, exclusive inheritance, chained accessors
- [The preprocessor define set](#the-preprocessor-define-set) — ENABLE / EXCLUSIVE macros + the preset map
- [Lineage caution](#lineage-caution) — po3 vs NG idioms are not portable
- [Not yet verified in-game](#not-yet-verified-in-game) — what still needs an empirical build-test

## The core problem

One DLL, three runtimes, three different memory layouts. VR is the disruptor: it **inserts extra virtual
functions in the middle of many class vtables** and gives some classes **entirely different base classes**,
making its ABI incompatible with AE/SE; AE separately reordered and resized base subobjects versus SE. The
net effect: a struct member sits at a *different byte offset* per runtime, and a virtual at a *different slot
index* per runtime.

> "Some classes in Skyrim VR add new virtual functions in the middle of the vtable structure, which makes
> it ABI-incompatible with AE/SE. A naive virtual function call, therefore, cannot work across all runtimes
> without the plugin being recompiled specifically for VR." — `REL/Relocation.h:979-983`

NG's design goal is a single DLL that works across all supported runtimes (`CLAUDE.md:7`), achieved by
**never baking a runtime-specific layout into cross-runtime code**. Layout-critical facts (a struct's size,
a base class, whether a virtual exists at all) are chosen at *compile time* per build preset; offset, slot,
and feature facts the one DLL must vary are chosen at *load time* via cheap runtime probes that constant-fold
to nothing in single-runtime builds.

The trap you must internalize before anything else: a bug written against one layout **compiles clean under
a single-runtime test preset** and only bites once VR joins the target set. The multi-runtime build — NG
calls its preset `all`, and its live code path `SKYRIM_CROSS_VR` — is the one that must be green
(`CLAUDE.md:510-514`). Testing only `se` hides the entire class of bug this reference exists to prevent.

## The footgun catalog

Almost every mistake below is **invisible in a single-runtime test build** and bites only once VR is added.
Read the Symptom column with that in mind: "compile error in `all`" is the *benign* outcome; "silently wrong
memory / CTD on VR" is the one that ships and crashes a player.

| # | Mistake | Symptom (compile-error / silent-wrong / CTD) | Fix |
|---|---|---|---|
| 1 | Direct member access on a `RUNTIME_DATA` class (`actor->currentProcess`) | Compile error `"is not a member of [class]"` in `all` — **benign, the guardrail firing**. But if the line was *copied from po3-lineage code*, it can compile and silently read a VR-wrong offset | Call the accessor: `actor->GetActorRuntimeData().currentProcess` |
| 2 | A non-`static` `REL::Relocation` | Re-resolves the Address-Library ID on every entry; the singleton contract is broken | Declare it `static` (`oar/src/Offsets.h:17`) |
| 3 | `SKYRIM_REL_VR_VIRTUAL` on a *runtime-exclusive* function | Silently breaks vtable alignment in `all` → wrong dispatch / CTD | Use the three-way conditional + `RelocateVirtual`; reserve `SKYRIM_REL_VR_VIRTUAL` for functions present in **all** runtimes |
| 4 | A naive `virtual` / `override` call on a class whose VR vtable inserts a slot | Wrong function called on VR → CTD or silent misbehavior | `RelocateVirtual<decltype(&C::Fn)>(seAeIdx, vrIdx, this, …)` — see Pattern 1 |
| 5 | An override signature that doesn't match the base **exactly** | With `override`: compile error "did not override any base class methods". **Without** `override`: silently a *new* non-virtual function — the base virtual is never replaced | Match every parameter type exactly |
| 6 | Wrong per-preset `STATIC_ASSERT_SIZE` | "static assertion failed" — the fail-loud backstop catching bad offsets/padding | Supply the per-runtime sizes; guard conditional-member classes with `#if defined(EXCLUSIVE_SKYRIM_FLAT)` |
| 7 | Swapping the two `RelocateVirtual` indices | Still compiles — you silently call the wrong virtual on whichever runtime you weren't thinking about | Copy both indices straight from the CommonLib header comment; **never compute VR = SE + 1** (the delta is not constant) |
| 8 | Assuming a VR-only member/function is present everywhere | Silent wrong result / CTD on the runtime that lacks it | Probe with `IsVR()` / `IsSE()` / `IsAE()`; null-check accessors that return `nullptr` off-runtime |
| 9 | Different base class per runtime, upcast via a naive `static_cast` | Reads wrong memory in `all` — the base isn't in the hierarchy there | Use the generated `As...()` (built on `RelocateMember`), always null-checked — see Pattern 2 |
| 10 | Copying po3-lineage code / offsets into an NG plugin | No VR carried; direct members assumed present; `RELOCATION_ID` yields the wrong type | Use NG idioms; save macro results with `auto` — see Lineage |
| 11 | Explicitly typing `RELOCATION_ID(...)` as `REL::ID` | Drops NG's runtime dispatch → silently AE-only or SE-only | Save the result with `auto` |
| 12 | Treating `VariantID`'s third arg as a VR **ID** | Resolves the wrong VR address | The third arg is a raw VR **offset**, not an Address-Library ID |
| 13 | Only one SKSE entrypoint implemented | Silent load failure — on SE/VR *or* on AE, depending which one you wrote | Supply both `SKSEPlugin_Query` and the `SKSEPlugin_Version` data — use the `SKSEPluginInfo` macro or CMake's `add_commonlibsse_plugin` (see `plugin-skeleton.md`) |
| 14 | Reaching for `EXCLUSIVE_SKYRIM_FLAT` where SE and AE actually differ | Compiles clean, silently wrong in one of SE/AE | Gate with `EXCLUSIVE_SKYRIM_SE` / `EXCLUSIVE_SKYRIM_AE` when SE ≠ AE — FLAT only means "not VR" |
| 15 | Mis-ordering the `Relocate` / `RELOCATION_ID` two-arg form | Wrong id/offset on one runtime | 2-arg = `(SE-and-VR shared, AE)`; 3-arg = `(SE, AE, VR)` |

Sources for the rows above: `CLAUDE.md:432-448,480`, `Actor.h:776-778` (#1); `oar/src/Offsets.h:17` (#2);
`CLAUDE.md:251`, `Common.h:159,186` (#3); `CLAUDE.md:217-241`, `Relocation.h:934-940` (#4);
`CLAUDE.md:388-404,476-478` (#5); `Common.h:287-334` (#6); `Relocation.h:1000-1003` (#7);
`oar/src/Conditions.cpp:814` (#8); `CLAUDE.md:253-296` (#9); Lineage section (#10, #11); `ID.h:589-592`
(#12); wiki §12,21,185-193 (#13); `Common.h:3-30` (#14); wiki §84,94 (#15).

Three deserve a worked look because copy-paste gets them wrong most often.

### The vtable-slot-shift hazard, concretely (Actor)

`RelocateVirtual`'s signature order is **SE/AE index first, VR index second** (verified param names
`a_seAndAEVtableIndex`, `a_vrVtableIndex`, `Relocation.h:1000-1003`). Swap them (footgun #7) and it still
compiles — you silently call the wrong virtual on whichever runtime you didn't have in mind, with no
`static_assert` to catch it.

Because VR inserts extra virtuals mid-vtable, the shift **compounds** down the class — it can start at +1
and grow to +2 as VR inserts a second exclusive slot. `src/RE/A/Actor.cpp` (its `#ifdef SKYRIM_CROSS_VR`
block) is a live catalog:

```cpp
void Actor::Unk_A2()                          // 0x0A2 (SE/AE) -> 0x0A3 (VR)  +1
    { RelocateVirtual<decltype(&Actor::Unk_A2)>(0x0A2, 0x0A3, this); }
void Actor::SetAvoidanceDisabled(bool a_set)  // 0x0A5 -> 0x0A6  (still +1)
    { RelocateVirtual<decltype(&Actor::SetAvoidanceDisabled)>(0x0A5, 0x0A6, this, a_set); }
void Actor::DrawWeaponMagicHands(bool a_draw) // 0x0A6 -> 0x0A8  (jumps to +2)
    { RelocateVirtual<decltype(&Actor::DrawWeaponMagicHands)>(0x0A6, 0x0A8, this, a_draw); }
void Actor::DetachCharController()            // 0x0A7 -> 0x0A9  (+2 carried on)
    { RelocateVirtual<decltype(&Actor::DetachCharController)>(0x0A7, 0x0A9, this); }
```

*Demonstrates: the VR delta is not a constant — it grows as more exclusive slots are inserted.* The +2 gap
rides all the way down (`SetVampireFeed(0x0BF, 0x0C1)` at `Actor.cpp:1736`, `InitiateFlee(0x0DD, 0x0DF)` at
`:1888`). The `Actor.h` header annotates each virtual with its SE/AE slot, and that number equals the
**first** `RelocateVirtual` argument — the built-in cross-check that arg 1 is SE/AE (`Actor.h:379`
`DrawWeaponMagicHands(bool a_draw); // 0A6`). **Author takeaway: copy both indices verbatim from the
CommonLib header comment. Do not compute VR = SE + 1.**

### VariantID — the VR arg is an offset, not an ID (footgun #12)

`REL::VariantID(seID, aeID, vrOffset)` takes an Address-Library ID for SE and AE but a **raw runtime
offset** for VR — `case Module::Runtime::VR: return _vrOffset;` returns it directly, with no `id2offset`
lookup (`ID.h:556-570,589-592`). It exists for cases where you deliberately don't want a VR ID in the VR
address database (RTTI / vtable offsets); "it should not be generally used for primary functionality" —
prefer `RELOCATION_ID` there (wiki §108-114). Production use, always `static` (footgun #2):

```cpp
static REL::Relocation<thkbBlendPoses> hkbBlendPoses{
    REL::VariantID(63192, 64112, 0xB4DD80) };   // seID, aeID, VR OFFSET
static REL::Relocation<tActor_GetEquippedShout> Actor_GetEquippedShout{
    REL::VariantID(37822, 38771, 0x63B4E0) };
```

*Demonstrates: the third slot is a bare offset — passing an Address-Library ID there resolves garbage.*

### The dual-entrypoint requirement (footgun #13)

SE/VR SKSE loads a plugin via a `SKSEPlugin_Query` function returning true; AE SKSE loads via a static
`SKSEPlugin_Version` data structure. A portable plugin must supply **both**, or it silently fails to load on
one branch. Don't hand-roll one: the NG `SKSEPluginInfo` macro declares `SKSEPlugin_Version` and generates
a matching `SKSEPlugin_Query`, and the CMake helper `add_commonlibsse_plugin` generates both
(wiki §12,21,185-193). The full entry scaffold lives in `plugin-skeleton.md` — this catalog only flags the
silent-load-failure trap.

## Compile-time vs runtime — the decision rule

This is the single most important author judgment, and it decides which of the tools below you reach for
(`CLAUDE.md:374-377,217-224,419-426`).

**Use a preprocessor `#if` (baked per build) when the difference is layout-critical** — it *must* be fixed
at compile time because it changes the C++ type's shape:

- class / struct size (`STATIC_ASSERT_SIZE` under an `EXCLUSIVE_*` guard)
- vtable slot count / a runtime-exclusive virtual (it shifts every later slot)
- base-class inheritance (VR's `WorldSpaceMenu` vs SE/AE's `IMenu`)
- whether a member is present at all

**Use a runtime probe when the single DLL must decide while running:**

- offset selection → `REL::Relocate(seAndVr, ae)` or the 3-arg `Relocate(se, ae, vr)`
- feature availability → `if (REL::Module::IsVR()) { /* VR-only path */ }`
- minimum-version gating → compare `a_skse->RuntimeVersion()` against a `SKSE::RUNTIME_*` constant

`REL::Module` is the load-time probe surface: `IsSE()`, `IsAE()`, `IsVR()`, `GetRuntime()`
(`Module.h:62-80,223-264`). It classifies from the exe's minor-version field (`4 → VR, 6 → AE, else → SE`,
`Module.h:339-348`) — you never hand-classify. The zero-cost property: in a single-runtime preset these
probes constant-fold — `IsVR()` compiles to a literal `false` in a non-VR build and to a real check only in
`all` / `flatrim` (`Module.h:255-264`), so a "for VR" branch costs nothing on SE-only and goes live only when
VR is in the target set.

`REL::Relocate` is the author-facing offset selector. The 2-arg `Relocate(a_seAndVr, a_ae)` uses arg1 for
**both SE and VR** and arg2 for AE; the 3-arg form is `(a_se, a_ae, a_vr)` (`Relocation.h:820-849`). It folds
to one arg in a single-runtime build and emits a `Module::IsAE()` branch in SE+AE.

## The RUNTIME_DATA accessor rule

The most pervasive multi-runtime discipline: **any class carrying offset-shifting members hides them behind
a generated accessor and never exposes them as direct C++ fields in a cross-runtime build.** Reach for the
member directly and, at best, you get a compile error; at worst — if the line came from another lineage —
you silently read the wrong bytes.

### Why direct access is designed to fail

The `RUNTIME_DATA_CONTENT` struct body is inlined as real class fields **only** under single-runtime
presets. Under multi-runtime the fields are absent, so `this->member` won't compile — **and that compile
error is the guardrail.** From `RE/A/Actor.h:776-778`:

```cpp
#if defined(EXCLUSIVE_SKYRIM_SE) || defined(EXCLUSIVE_SKYRIM_VR) || defined(EXCLUSIVE_SKYRIM_AE)
    RUNTIME_DATA_CONTENT  // inlined for single-runtime builds; multi-runtime uses GetActorRuntimeData()
#endif
```

*Demonstrates: the fields exist only in single-runtime builds — the `all` build forces you through the
accessor.* `PlayerCharacter` mirrors this (`PlayerCharacter.h:999-1003`). NG's own guide states the rule
and names the error: *"'is not a member of [class]' → Cause: accessing RUNTIME_DATA members directly in
multi-runtime builds → Solution: Use GetRuntimeData() accessor"* (`CLAUDE.md:480-482`).

So `actor->currentProcess` compiles on an `se` preset and dies only in `all` — a single-preset test hides
the bug. **Author rule: always go through the accessor, even in a single-runtime build, so the plugin stays
portable to the `all` target NG defaults to.** This is houseCARL's Q3 discipline ("no silent failure")
applied to memory access — the accessor turns a would-be silent wrong-memory read into either a hard compile
error or a runtime-correct value.

### What the author writes

`RelocateMember<T>(this, seAndAe, vr)` is the primitive — pure pointer math returning a `T&` at the
runtime-correct byte offset (`Relocation.h:1029-1033`). It feeds the SE value for **both** SE and AE, so the
2-arg form can't encode an AE-specific offset; when AE differs, the versioned accessor macros
(`RUNTIME_DATA_ACCESSOR_VERSIONED`, built on `RelocateMemberIfNewer` at the `1.6.629` boundary) express it.
In practice you rarely call the primitive — you call the generated accessor:

```cpp
// The .member idiom CommonLib's own src uses verbatim (src/RE/A/Actor.cpp):
GetActorRuntimeData().boolFlags.set(BOOL_FLAGS::kCanSpeakToEssentialDown);
auto* _race           = GetActorRuntimeData().race;
auto* _currentProcess = GetActorRuntimeData().currentProcess;   // NOT actor->currentProcess
```

*Demonstrates: the accessor returns a runtime-correct reference; you use `.member` on its result, never on
the object.* Consuming an existing NG class is exactly this simple — the accessor is already runtime-correct,
so most plugin authors never *declare* a variant class at all, they just call `GetXxxRuntimeData()` /
`As...()` and null-check the result. A real OAR call-site, reading Actor data behind a safe `As<>` narrow
(`oar/src/Conditions.cpp:658-667`):

```cpp
RE::TESForm* IsEquippedPowerCondition::GetEquippedPower(RE::TESObjectREFR* a_refr) {
    if (a_refr) {
        if (const auto actor = a_refr->As<RE::Actor>()) {
            return actor->GetActorRuntimeData().selectedPower;   // NOT actor->selectedPower
        }
    }
    return nullptr;
}
```

The whole-class offsets behind this are enforced at compile time by `STATIC_ASSERT_SIZE` (per preset) and
`STATIC_ASSERT_OFFSET` (per member) — a wrong offset is a hard build error, not a silent runtime misread
(`Common.h:287-334`). That is the fail-loud backstop the accessor pattern sits on.

## The three conditional patterns

These are the shapes for *declaring* a class that spans runtimes. Most third-party authors consume more than
they declare, but you must recognize all three to read the headers, and you'll write them any time you model
a class NG doesn't already cover. `include/RE/B/ButtonEvent.h` exemplifies **all three at once** and is the
canonical live anchor (verified verbatim, `:12-176`). Every pattern compiles to **zero overhead** in a
single-runtime preset and to a **single `REL::Module::IsVR()` branch** only in `all`.

The recurring three-way skeleton is:

```cpp
#if defined(EXCLUSIVE_SKYRIM_VR)
    // VR-only shape
#elif !defined(ENABLE_SKYRIM_VR)
    // SE/AE-only shape  — use this, NOT a bare #else (CLAUDE.md:296), or it won't compile across all presets
#else
    // multi-runtime shape
#endif
```

Use `!defined(ENABLE_SKYRIM_VR)` for the SE/AE arm, never a bare `#else` — the bare form fails to compile
across every preset (`CLAUDE.md:296`, footgun #9's fine print).

### Pattern 1 — runtime-exclusive virtual functions

**When:** the class has the **same base class** across runtimes but a virtual exists in only one runtime —
typically a VR-only slot inserted mid-vtable (`CLAUDE.md:163-251`). VR gets a real `virtual`; the
multi-runtime build gets a non-virtual placeholder that keeps the C++ vtable identical to SE/AE while
`RelocateVirtual` does the real dispatch:

```cpp
#if defined(EXCLUSIVE_SKYRIM_FLAT)
    // Function doesn't exist in SE/AE-only builds
#elif defined(EXCLUSIVE_SKYRIM_VR)
    virtual void Unk_03();  // 03 - VR only
#else
    void Unk_03();          // 03 - Multi-runtime (non-virtual)
#endif
```

The `.cpp` wrapper, compiled only in multi-runtime builds:

```cpp
#ifdef SKYRIM_CROSS_VR
    void ClassName::Unk_03() {
        if (REL::Module::IsVR()) {
            REL::RelocateVirtual<decltype(&ClassName::Unk_03)>(0x03, 0x03, this);
        }
        // SE/AE: no-op — this function should never be called
    }
#endif
```

*Demonstrates: the placeholder keeps vtable layout SE/AE-identical while real dispatch is deferred to
`RelocateVirtual` at load time.* The real exemplar is `RE::TESCameraState` — header
`RE/T/TESCameraState.h:28-34` is the three-way block above verbatim; `.cpp` `src/RE/T/TESCameraState.cpp:9-17`
is the wrapper verbatim.

**The vtable-shift corollary** (the part authors forget): because VR inserts `Unk_03` at slot `0x03`, every
later virtual shifts +1 in the VR vtable, so **each later function must also be reimplemented with
`RelocateVirtual` carrying both indices**:

```
SE/AE: Begin(01) -> End(02) -> Update(03) -> GetRotation(04)
VR:    Begin(01) -> End(02) -> Unk_03(03) -> Update(04) -> GetRotation(05)
```

`TESCameraState.cpp:19-47` proves it: `Update(0x03,0x04)`, `GetRotation(0x04,0x05)`, and so on. Derived
classes repeat the whole thing — this is the compounding shift the footgun catalog's Actor example walks in
detail. As `CLAUDE.md:226` puts it: *"Every derived class that overrides functions after a runtime-exclusive
function MUST implement RelocateVirtual for those functions."*

### Pattern 2 — runtime-exclusive inheritance

**When:** the class inherits **completely different, incompatible base classes** per runtime
(`CLAUDE.md:253-301`). Inherit the VR base under VR, the SE/AE base under `!defined(ENABLE_SKYRIM_VR)`, and a
single most-compatible common base in multi-runtime; then expose `As<Base>()` upcasts that **return
`nullptr` for the wrong runtime** and otherwise `RelocateMember` to the base's data. The live exemplar is
`RE::ButtonEvent` (`:12-20,75-81`):

```cpp
class ButtonEvent :
#if defined(EXCLUSIVE_SKYRIM_VR)
    public VRWandEvent          // VR
#elif !defined(ENABLE_SKYRIM_VR)
    public IDEvent              // SE/AE
#else
    public InputEvent           // Multi-runtime: inherit from common base class
#endif
{
    [[nodiscard]] VRWandEvent* AsVRWandEvent() noexcept {
        if SKYRIM_REL_CONSTEXPR (!REL::Module::IsVR()) { return nullptr; }
        return &REL::RelocateMember<VRWandEvent>(this, 0, 0);
    }
};
```

*Demonstrates: the base class itself is chosen per preset; the `As...()` upcast is the only safe way to
reach a runtime-specific base, and it can return `nullptr`.* **Always null-check an `As...()` upcast before
dereferencing.** Never `static_cast` up an assumed chain on such a class (footgun #9): in the multi-runtime
build the base isn't in the hierarchy and you read wrong memory. The size proof this is necessary:
`STATIC_ASSERT_SIZE(ButtonEvent, 0x30, 0x30, 0x38, 0x18)` — SE `0x30` / AE `0x30` / VR `0x38` (+8 for
`VRWandEvent`'s extra members) / All `0x18` (`ButtonEvent.h:176`).

The same shape covers `HUDMenu : WorldSpaceMenu` (VR) vs `IMenu` (SE/AE), also in the corpus
(`RE/W/WorldSpaceMenu.h`).

### Pattern 3 — chained-inheritance access

**When:** you need a base class's members but reach it through an inheritance *chain that differs per
runtime* (`CLAUDE.md:303-354`). `ButtonEvent` again — VR walks `ButtonEvent → VRWandEvent → IDEvent`, SE/AE
casts directly, and multi-runtime has no `IDEvent` edge at all so it relocates (`ButtonEvent.h:88-101`):

```cpp
[[nodiscard]] IDEvent* AsIDEvent() noexcept {
#if defined(EXCLUSIVE_SKYRIM_VR)
    return static_cast<IDEvent*>(static_cast<VRWandEvent*>(this));  // walk the chain
#elif !defined(ENABLE_SKYRIM_VR)
    return static_cast<IDEvent*>(this);                            // direct cast
#else
    return &REL::RelocateMember<IDEvent>(this, 0, 0);              // no compile-time edge -> relocate
#endif
}
```

*Demonstrates: single-runtime builds use an efficient `static_cast`; the multi-runtime build, where the edge
doesn't exist, falls back to `RelocateMember`.* On top of this private accessor, the class exposes a **stable
public API identical across runtimes**, each method null-checking the upcast (`ButtonEvent.h:109-138`):

```cpp
std::uint32_t GetIDCode() const noexcept {
    if (auto idEvent = AsIDEvent()) { return idEvent->idCode; }
    return 0;   // safe fallback
}
```

*Demonstrates: a runtime-agnostic public method built on the null-checked accessor — the shape a consumer
actually calls* (always via `if (auto x = As...())` with a safe fallback, never a bare deref). One idiom the
sketches omit but the real header carries: **const-overload delegation** — every non-const `As<Base>()` has
a const twin forwarding via `const_cast`, so const callers compile: `const IDEvent* AsIDEvent() const
noexcept { return const_cast<ButtonEvent*>(this)->AsIDEvent(); }` (`ButtonEvent.h:103-106`).

So one production class is the live exemplar for both Pattern 2 (its inheritance list) and Pattern 3 (this
accessor); in practice the two co-occur.

## The preprocessor define set

Authors and the build system set exactly **three** input defines; every other multi-runtime macro is
*derived* from them at preprocess time inside `include/REL/Common.h`. Author code branches on the derived
macros and never sets them.

| Define | Kind | Meaning |
|---|---|---|
| `ENABLE_SKYRIM_SE` | **input** (CMake) | compile in pre-AE SE (1.5.97) support |
| `ENABLE_SKYRIM_AE` | **input** (CMake) | compile in AE (1.6.x) support |
| `ENABLE_SKYRIM_VR` | **input** (CMake) | compile in VR (1.4.15) support |
| `EXCLUSIVE_SKYRIM_SE` | derived | only SE enabled |
| `EXCLUSIVE_SKYRIM_AE` | derived | only AE enabled |
| `EXCLUSIVE_SKYRIM_VR` | derived | only VR enabled |
| `EXCLUSIVE_SKYRIM_FLAT` | derived | SE and/or AE, **no VR** (an umbrella — means "not VR", not "single non-VR runtime") |
| `SKYRIM_CROSS_VR` | derived | VR **and** (SE or AE) — the one DLL serves both a VR and a non-VR runtime; the multi-runtime code path |

The derivation, verbatim (`Common.h:3-38`):

```cpp
#if !defined(ENABLE_SKYRIM_VR) && !defined(ENABLE_SKYRIM_SE) && defined(ENABLE_SKYRIM_AE)
#   define EXCLUSIVE_SKYRIM_AE
#   define EXCLUSIVE_SKYRIM_FLAT
#elif !defined(ENABLE_SKYRIM_VR) && defined(ENABLE_SKYRIM_SE) && !defined(ENABLE_SKYRIM_AE)
#   define EXCLUSIVE_SKYRIM_SE
#   define EXCLUSIVE_SKYRIM_FLAT
#elif defined(ENABLE_SKYRIM_VR) && !defined(ENABLE_SKYRIM_SE) && !defined(ENABLE_SKYRIM_AE)
#   define EXCLUSIVE_SKYRIM_VR
#elif !defined(ENABLE_SKYRIM_VR) && (defined(ENABLE_SKYRIM_SE) || defined(ENABLE_SKYRIM_AE))
#   define EXCLUSIVE_SKYRIM_FLAT
#endif

#if defined(ENABLE_SKYRIM_VR) && (defined(ENABLE_SKYRIM_AE) || defined(ENABLE_SKYRIM_SE))
#   define SKYRIM_CROSS_VR
#endif
```

*Demonstrates: a single-SE build defines **both** `EXCLUSIVE_SKYRIM_SE` and `EXCLUSIVE_SKYRIM_FLAT`; a
single-AE build defines both `EXCLUSIVE_SKYRIM_AE` and `EXCLUSIVE_SKYRIM_FLAT`. FLAT never appears alone
except in `flatrim`.* `CMakeLists.txt` hard-errors if none of the three `ENABLE_*` are set.

### Preset → define map

CMake presets set only the `ENABLE_*` trio; `Common.h` derives the rest. The five build-invocable presets:

| Preset | ENABLE_ set | EXCL_SE | EXCL_AE | EXCL_VR | EXCL_FLAT | CROSS_VR |
|---|---|---|---|---|---|---|
| **all** | SE + AE + VR | — | — | — | — | yes |
| **flatrim** | SE + AE (VR off) | — | — | — | yes | — |
| **vr** | VR only | — | — | yes | — | — |
| **se** | SE only | yes | — | — | yes | — |
| **ae** | AE only | — | yes | — | yes | — |

### FLAT is "not VR", not "single non-VR runtime" (footgun #14)

The NG `CLAUDE.md` quick-reference table for these defines is a friendly summary that is **lossy in a way
that produces wrong code** — it omits `EXCLUSIVE_SKYRIM_SE` / `EXCLUSIVE_SKYRIM_AE` and implies a single-SE
build gets *only* FLAT. It doesn't: `Common.h:12-20` proves single-SE defines both SE and FLAT. Where the
table and `Common.h` disagree, **`Common.h` wins** — compiled code over hand-written agent notes.

This matters because SE and AE genuinely differ in memory layout for some types, and a `FLAT`-only `#if`
cannot discriminate them. The canonical case is `BaseExtraList` (`RE/E/ExtraDataList.h:43-67`): SE/VR keep a
non-virtual dtor with data at offset `0x00` (size `0x10`); AE 1.6.629+ promoted the dtor to virtual, adding
a vtable pointer and shifting members by 8 (data at `0x08`, size `0x18`). The header discriminates with
`#if defined(EXCLUSIVE_SKYRIM_SE) || defined(EXCLUSIVE_SKYRIM_VR)` vs `#elif defined(EXCLUSIVE_SKYRIM_AE)` —
undecidable from FLAT alone.

**Authoring rule:** gate with `EXCLUSIVE_SKYRIM_FLAT` **only** when the code is identical across SE and AE
and you just need "not VR." Gate with `EXCLUSIVE_SKYRIM_SE` / `EXCLUSIVE_SKYRIM_AE` whenever SE and AE
differ. Reaching for FLAT where SE/AE diverge compiles clean but is silently wrong in one runtime.

## Lineage caution

The current CommonLib is **`alandtse/CommonLibVR` branch `ng`** — the charter-locked target, and the lineage
every idiom above is verified against. Do not switch away from it. Two neighbouring lineages produce code
that looks similar and **is not portable** into an NG single-DLL build:

- **powerof3/CommonLibSSE (po3 `dev`) has no VR axis.** No `VariantID`, no `IsVR`, no `RUNTIME_DATA` accessor
  layer — its `Actor` exposes hot members as plain fields. Lifting `actor->currentProcess` from a po3-era
  tutorial into an NG `all` build is exactly the silent-breakage the accessor rule (footguns #1, #10)
  prevents. po3's `RELOCATION_ID` even resolves to a different type (`REL::ID`, compile-time picked) vs NG's
  runtime-dynamic `REL::RelocationID` — why NG mandates `auto` (footgun #11). **Never emit `SKYRIM_SUPPORT_AE`
  in NG code** — a po3-only toggle (NG's AE axis is `ENABLE_SKYRIM_AE` / `IsAE()`); seeing it flags a po3
  sample.
- **The older CommonLibVR fork model (one DLL per runtime) is not multi-runtime authoring.** SPID and po3
  Papyrus Extender swap the *whole library* per target (`CommonLibSSE` for SE/AE vs `CommonLibVR` for VR)
  rather than shipping one DLL — their idioms read like NG but assume a single fixed layout. **Present OAR,
  not SPID or po3 Papyrus Extender, as the multi-runtime exemplar** — OAR is genuinely NG-lineage (resolves
  CommonLib via `CommonLibSSEPath_NG`, uses `GetRuntimeData` / `As...()` / `IsVR` throughout).

Practical rule: **before generalizing any idiom from a repo, confirm its lineage via `vcpkg.json` / CMake** —
a sample that compiles is not a sample that's NG. Beyond these, CharmedBaryon's original CommonLibSSE-NG fork
is frozen: the NG wiki is CharmedBaryon-era prose, useful as documentation, but where it disagrees with
`alandtse/CommonLibVR` code, the code wins — the same "compiled code over prose" rule the FLAT correction rests on.

## Not yet verified in-game

Everything above is grounded in NG's primary source (headers, `.cpp` bodies, the bundled `CLAUDE.md`), and
the `STATIC_ASSERT_SIZE` / `STATIC_ASSERT_OFFSET` backstop proves layout *consistency* per preset at compile
time. What static source **cannot** prove is that a given offset/slot pair resolves the right member or
function in a *running* game. Treat these as open until a build-and-load test on each runtime confirms them
— never present them as proven runtime behavior:

- That the same `ButtonEvent` / `Actor` source produces a working single DLL across all three live runtimes
  (the core architectural claim). It needs the `all` preset built and loaded under SE, AE, and VR.
- Whether a *swapped or hardcoded* `RelocateVirtual` index reliably CTDs on VR or silently no-ops — i.e. the
  exact compile-error-vs-silent-vs-CTD split for footguns #1, #4, #8, #9. The fixes are correct regardless;
  only the failure *symptom* is unproven.
- That the hard-coded offsets (e.g. `Actor` runtime data at `0xE0` SE / `0xE8` AE, or the AE 1.6.629+
  `BaseExtraList` `0x10`→`0x18` shift) match the *shipping* executables. The static asserts validate
  internal consistency, not agreement with a running game binary.

None of this weakens the authoring rules — the discipline (accessor over direct member, `RelocateVirtual`
over naive virtual, `EXCLUSIVE_SKYRIM_SE`/`_AE` over FLAT when SE ≠ AE) keeps a bug loud instead of silent.
It only flags which *runtime outcomes* still await an empirical `all`-preset build-test before the skill
states them as fact.
