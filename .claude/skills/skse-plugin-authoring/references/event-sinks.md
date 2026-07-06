# Event sinks — receiving game events in C++

How an SKSE plugin hears what the game does. Skyrim publishes events (a death, a menu opening, a
key press, a cell load) through a small observer system; a plugin implements a *sink*, registers it
against the right *source*, and its handler runs each time the event fires. This is the "react to the
game" spine you hang most plugin behavior off.

This reference covers the C++ / CommonLibSSE-NG side only. Where an event forwards into a Papyrus
registration (a `.psc` `RegisterForModEvent` handler and its argument list), the Papyrus signature is
owned by houseCARL's `papyrus-reference` skill — this doc names the C++ call and the shared event-name
string and stops there. The plugin declaration, the SKSE messaging lifecycle, and `SKSE::Init` live in
`plugin-skeleton.md`; setting up the toolchain is `toolchain-setup.md`.

## Contents

- [The one safe default](#the-one-safe-default)
- [The mechanism: sink, source, ProcessEvent](#the-mechanism-sink-source-processevent)
- [The mandatory null-guard and the return value](#the-mandatory-null-guard-and-the-return-value)
- [Sink lifetime — why sinks are singletons](#sink-lifetime--why-sinks-are-singletons)
- [The ScriptEventSourceHolder inventory](#the-scripteventsourceholder-inventory)
- [Other event channels](#other-event-channels)
- [Registration timing](#registration-timing)
- [The sink ↔ serialization bridge](#the-sink--serialization-bridge)
- [The end-to-end skeleton](#the-end-to-end-skeleton)
- [Not yet verified in-game](#not-yet-verified-in-game)

## The one safe default

**Register game-singleton event sinks at `kDataLoaded`, unless you have a proven, null-guarded reason to
go earlier.** `kDataLoaded` is the last of the four launch messages (`kPostLoad → kPostPostLoad →
kInputLoaded → kDataLoaded`); by the time it fires, every form is loaded and every game singleton you
reach for exists. Registering earlier is possible for some sources but is the single thing authors most
often get wrong — the object you register against may still be null. Start at `kDataLoaded` and only move
earlier with evidence and a null-check (see [Registration timing](#registration-timing)).

## The mechanism: sink, source, ProcessEvent

The whole system is two header-inline types (`RE/B/BSTEvent.h`):

- **`BSTEventSource<Event>`** — the publisher. Engine-owned, usually embedded in a holder singleton. It
  keeps an array of raw, *unowned* sink pointers and dispatches to each when an event fires.
- **`BSTEventSink<Event>`** — the interface your plugin implements. One virtual method:

```cpp
RE::BSEventNotifyControl ProcessEvent(const Event* a_event,
                                      RE::BSTEventSource<Event>* a_eventSource) override;
```

*The one method every sink overrides. You get a pointer to the event payload; you return whether dispatch
should continue.*

You register through four operations on the source (or on the holder that owns it):

- **`AddEventSink(sink)`** — subscribe. **Null-safe** (a null argument is a no-op) and **idempotent**
  (registering the same sink twice never doubles delivery — membership is checked at add time). This is
  why you never hand-roll a "have I already registered?" scan; the API dedups for you.
- **`PrependEventSink(sink)`** — subscribe at the *front* of dispatch order, for priority. SPID uses this
  so its death-distribution sink runs before its outfit sink on the same `TESDeathEvent`.
- **`RemoveEventSink(sink)`** — unsubscribe. Null-safe. Rarely needed (see below).
- **`SendEvent(event)`** — dispatch (usually the engine's job; a plugin may call it to inject an event).

Dispatch is **synchronous** — `SendEvent` calls each sink inline, on whatever thread fired the event,
under a spin lock held for the whole dispatch. Two consequences worth internalizing: a slow handler
blocks every other thread touching that source, so **keep handlers short and defer heavy work**; and
nothing pins engine dispatch to the main thread, so treat every handler as *potentially off-main-thread*
and marshal main-thread-only work through `SKSE::GetTaskInterface()->AddTask` (see
`threading-and-persistence.md`). Mutating the same source from inside its own handler is safe — the lock
is recursive per-thread and adds/removes made during a dispatch are deferred to the next one.

Two more production realities of this dispatch model:

- **Events arrive in storms.** One player action can fire dozens of correlated events back-to-back — an
  outfit swap is one equip event per item, a cell transition a burst of load/attach events. Doing the full
  job per event multiplies an already-expensive operation. Coalesce: have the handler mark dirty state and
  queue **one** deferred task that does the real work once per burst, skipping the enqueue when a task is
  already pending.
- **A handler whose body performs engine side effects (control toggles, UI refreshes, equips) can
  synchronously re-fire events into itself.** Arm any guard state *before* the side effect, and null-check
  every handle resolution even on payloads the engine "always" populates — the full re-entrancy discipline
  is in `hooking.md`.

## The mandatory null-guard and the return value

`SendEvent` forwards the event pointer verbatim — it does **not** null-check it before calling your sink.
Every production handler in the corpus opens the same way, so treat this as the mandatory first line:

```cpp
if (!a_event) { return RE::BSEventNotifyControl::kContinue; }
```

*The universal defensive open — no source is proven to pass null, but the guard is cheap and every
shipped plugin writes it.*

`BSEventNotifyControl` has exactly two values: **`kContinue`** (return this — normal) and **`kStop`**.
`kStop` aborts delivery of *that one event* to every sink later in dispatch order — it is a `break` in
the loop. It does not unregister anything and has no effect on later events. In practice **production code
returns `kContinue` unconditionally** — a corpus-wide search across SPID, OAR, and po3's extender finds
zero handlers that return `kStop`. Reach for `kStop` only when you deliberately mean to starve other
sinks of an event, and know you are doing it.

## Sink lifetime — why sinks are singletons

The source holds a **raw, unowned pointer** to your sink and never auto-unregisters on destruction.
Destroy a still-registered sink and the next `SendEvent` virtual-calls into freed memory. There are two
legal patterns, and only two:

1. **Static-lifetime singleton sink (the default).** Never destroyed during gameplay, so it never needs
   removal. This is why every global sink in the corpus derives from `REX::Singleton<T>` (a function-local
   static with a protected constructor and deleted copy/move — the ready-made base) or clib_util's
   equivalent. You register it once and forget it; there is no teardown path to get wrong.
2. **Dynamic sink** (tied to some object that comes and goes). Must call `RemoveEventSink(this)` before it
   is destroyed. OAR's per-animation-clip sinks are the only dynamic case in the whole corpus, and they
   track a `_bRegisteredSink` flag to remove themselves on teardown.

Global singleton sinks are **never unregistered** in shipped code — a corpus-wide grep finds zero
`RemoveEventSink` calls outside OAR's dynamic per-object case. Prefer the singleton pattern unless the
thing you are listening on genuinely has an object lifetime.

A sink that listens to several events multiply-inherits one `BSTEventSink<T>` base per event and overrides
each `ProcessEvent`. When you register such a sink, **pass the explicit template argument**
(`AddEventSink<RE::TESDeathEvent>(sink)`) so the compiler selects the right source base and sink upcast in
one step. On Clang, the multiple-inheritance shape trips a known false-positive warning; add
`-Wno-delete-non-abstract-non-virtual-dtor` (the warning is wrong — the sink destructor *is* virtual).

## The ScriptEventSourceHolder inventory

`RE::ScriptEventSourceHolder` is the game's central script-event hub — one game-owned singleton reached
via `RE::ScriptEventSourceHolder::GetSingleton()`. It sources the `TES*` gameplay events. Registration is
one null-guarded line:

```cpp
auto* holder = RE::ScriptEventSourceHolder::GetSingleton();
if (holder) { holder->AddEventSink<RE::TESDeathEvent>(sink); }
```

*The standard holder registration. The explicit `<RE::TESDeathEvent>` selects the correct source base.*

The table below is the lookup an author scans for "which event carries the FormID / the killer / the
cell." Payloads are heterogeneous: some carry a strong `NiPointer<TESObjectREFR>` you can use directly,
some carry a bare `FormID` you must resolve with `RE::TESForm::LookupByID<T>(id)`, some carry an
`ObjectRefHandle`. **"Fires on" is derived from the event name** — the exact firing conditions are not
proven from the corpus (see [Not yet verified in-game](#not-yet-verified-in-game)), and the Papyrus `OnX`
counterparts are `papyrus-reference`'s surface, not asserted here.

| Event (sizeof) | Payload (type → name @ offset) | Fires on (name-derived) |
|---|---|---|
| `TESActivateEvent` (0x10) | `NiPointer<TESObjectREFR> objectActivated` 00; `actionRef` 08 | a reference is activated; `actionRef` = activator |
| `TESActiveEffectApplyRemoveEvent` (0x18) | `NiPointer caster` 00; `target` 08; `u16 activeEffectUniqueID` 10; `bool isApplied` 12 | active magic effect applied/removed |
| `TESActorLocationChangeEvent` (0x18) | `NiPointer actor`; `BGSLocation* oldLoc`; `newLoc` | actor changes BGSLocation |
| `TESBookReadEvent` (0x10) | `NiPointer ref` 00; `FormID baseFormID` 08; `u16 uniqueID` 0C | a book is read |
| `TESCellAttachDetachEvent` (0x10) | `NiPointer reference` 00; `bool attached` 08 | reference attaches/detaches with its cell |
| `TESCellFullyLoadedEvent` (0x8) | `TESObjectCELL* cell` | a cell finishes loading |
| `TESCombatEvent` (0x18) | `NiPointer actor` 00; `targetActor` 08; `EnumSet<ACTOR_COMBAT_STATE> newState` 10 (kNone/kCombat/kSearching) | actor combat-state change |
| `TESContainerChangedEvent` (0x18) | `FormID oldContainer` 00; `newContainer` 04; `baseObj` 08; `i32 itemCount` 0C; `ObjectRefHandle reference` 10; `u16 uniqueID` 14 | items move; **all-FormID/handle — must be resolved** |
| `TESDeathEvent` (0x18) | `NiPointer actorDying` 00; `actorKiller` 08; `bool dead` 10 | actor death; `dead` flag suggests dying-vs-dead phases |
| `TESEnterBleedoutEvent` (0x8) | `NiPointer actor` | actor enters bleedout |
| `TESEquipEvent` (0x18) | `NiPointer actor` 00; `FormID baseObject` 08; `originalRefr` 0C; `u16 uniqueID` 10; `bool equipped` 12 | actor equips/unequips |
| `TESFastTravelEndEvent` (0x08) | `float fastTravelEndHours` 00 | fast travel completes; **SE/AE-only source** (see VR note) |
| `TESFormDeleteEvent` (0x08) | `FormID formID` 00 | a form (usually a runtime ref) is deleted — the standard cache-purge signal |
| `TESFurnitureEvent` (0x18) | `NiPointer actor` 00; `targetFurniture` 08; `EnumSet<FurnitureEventType> type` 10 (kEnter/kExit) | actor enters/exits furniture |
| `TESGrabReleaseEvent` (0x10) | `NiPointer ref` 00; `bool grabbed` 08 | player grabs/releases a reference |
| `TESHitEvent` (0x20) | `NiPointer target` 00; `cause` 08; `FormID source` 10; `projectile` 14; `EnumSet<Flag,u8> flags` 18 (kPowerAttack/kSneakAttack/kBashAttack/kHitBlocked). **Public ctors.** | something is hit; richest combat payload |
| `TESInitScriptEvent` (0x8) | `NiPointer objectInitialized` | reference's script initializes |
| `TESLoadGameEvent` (0x1) | *(empty)* | a save game is loaded — the notification IS the payload |
| `TESLockChangedEvent` (0x8) | `NiPointer lockedObject` | lock state changes on a reference |
| `TESMagicEffectApplyEvent` (0x18) | `NiPointer target` 00; `caster` 08; `FormID magicEffect` 10 | magic effect applies to a target |
| `TESMoveAttachDetachEvent` (0x10) | `NiPointer movedRef` 00; `bool isCellAttached` 08 | ref moved into/out of attached cell space |
| `TESObjectLoadedEvent` (0x8) | `FormID formID` 0; `bool loaded` 4 | a form's 3D loads/unloads; **FormID-only** |
| `TESOpenCloseEvent` (0x18) | `NiPointer ref` 00; `activeRef` 08; `bool opened` 10 | door/container opened or closed |
| `TESPlayerBowShotEvent` (0x10) | `FormID weapon` 00; `FormID ammo` 04; `float shotPower` 08; `bool isSunGazing` **0C** | player fires a bow |
| `TESQuestInitEvent` (0x4) | `FormID formID` | quest initializes |
| `TESQuestStageEvent` (0x10) | `void* finishedCallback` 00 (opaque); `FormID formID` 08; `u16 stage` 0C; `u8 itemIndex` 0E | quest stage is set |
| `TESQuestStartStopEvent` (0x8) | `FormID formID` 00; `bool started` 04; `bool failed` 05 | quest starts or stops |
| `TESResetEvent` (0x8) | `NiPointer object` | reference resets |
| `TESResolveNPCTemplatesEvent` (0x8) | `FormID templateID` 00 | NPC template resolution (leveled-actor fill) |
| `TESSleepStopEvent` (0x1) | `bool interrupted` 0 | sleep ends |
| `TESSpellCastEvent` (0x10) | `NiPointer object` 00; `FormID spell` 08 | a spell is cast by `object` |
| `TESSwitchRaceCompleteEvent` (0x8) | `NiPointer subject` | race switch completes |
| `TESTopicInfoEvent` (0x20) | `BSTSmartPointer callback` 00; `NiPointer speakerRef` 08; `FormID topicInfoFormID` 10; `EnumSet<TopicInfoEventType> type` 14 (kTopicBegin/kTopicEnd); `u16 stage` 18 | dialogue topic-info begins/ends |
| `TESTrackedStatsEvent` (0x10) | `BSFixedString stat` 00; `i32 value` 08 | a tracked stat changes |
| `TESUniqueIDChangeEvent` (0x10) | `FormID oldBaseID` 00; `newBaseID` 04; `objectID` 08; `u16 oldUniqueID` 0C; `newUniqueID` 0E | an item's unique ID is reassigned |
| `TESWaitStopEvent` (0x1) | `bool interrupted` 0 | player wait ends |

Author gotchas worth calling out:

- **`TESPlayerBowShotEvent.isSunGazing` sits at offset 0x0C, not 0x09** as the upstream header comment
  claims — the `float shotPower` before it occupies 0x08–0x0B, and the struct is 0x10. Trust the layout,
  not the stale comment.
- **`TESLoadGameEvent` is empty** — sink it purely to know a load happened.
- **Start/stop asymmetry.** `TESSleepStopEvent`/`TESWaitStopEvent` are fully defined, but their *Start*
  twins (`TESSleepStartEvent`/`TESWaitStartEvent`) are among the opaque events below.
- **`TESHitEvent` has public constructors** (default + a 5-arg `(target, aggressor, weapon, projectile,
  flags)`), so synthesizing a hit event is straightforward — but whether a synthetic send reaches Papyrus
  `OnHit` handlers is unverified.

### The opaque events — you can register, but the payload is undereferenceable

Eighteen more events are *sourced* by the holder but have **no struct definition anywhere in
CommonLibSSE-NG** (a shared upstream gap — po3's fork omits them too). A sink registers and compiles fine
(the sink only ever touches the payload through a pointer), but the payload pointer is undereferenceable
until you hand-author the layout — and getting it wrong reads garbage or crashes. The high-value ones for
an author to reverse-engineer are the **Scene** trio (`TESSceneEvent` / `TESScenePhaseEvent` /
`TESSceneActionEvent` — the finest-grained hook for scripted set-pieces without polling Papyrus) and the
**Trigger** trio (`TESTriggerEnterEvent` / `TESTriggerEvent` / `TESTriggerLeaveEvent`). Two of the
eighteen — `TESSleepStartEvent` and `TESWaitStartEvent` — have a known layout portable from skse64 (each
`{ float, float }`, 8 bytes). The full set: `BGSEventProcessedEvent`, `TESCellReadyToApplyDecalsEvent`,
`TESDestructionStageChangedEvent`, `TESMagicWardHitEvent`, `TESObjectREFRTranslationEvent`,
`TESPackageEvent`, `TESPerkEntryRunEvent`, `TESQuestStageItemDoneEvent`, `TESSceneEvent`,
`TESSceneActionEvent`, `TESScenePhaseEvent`, `TESSellEvent`, `TESSleepStartEvent`, `TESTrapHitEvent`,
`TESTriggerEvent`, `TESTriggerEnterEvent`, `TESTriggerLeaveEvent`, `TESWaitStartEvent`. The sink mechanism
is identical to the defined events — defining the struct correctly is the only missing piece.

## Other event channels

Not everything comes through `ScriptEventSourceHolder`. The channels below cover the rest of what a plugin
commonly needs. Each is reached through its own singleton and takes the same `AddEventSink` shape unless
noted.

| Channel | Source & how obtained | Payload | Note |
|---|---|---|---|
| **SKSE mod events** | `SKSE::GetModCallbackEventSource()` | `ModCallbackEvent { BSFixedString eventName; strArg; float numArg; TESForm* sender; }` | sink it to hear mod events; **send** to it to reach Papyrus `RegisterForModEvent` (see below) |
| **SKSE camera state** | `SKSE::GetCameraEventSource()` | `CameraEvent { TESCameraState* oldState; newState; }` | identify state via `TESCameraState::id` |
| **SKSE crosshair ref** | `SKSE::GetCrosshairRefEventSource()` | `CrosshairRefEvent { NiPointer crosshairRef; }` | null = target lost |
| **SKSE actor actions** | `SKSE::GetActionEventSource()` | `ActionEvent { type; Actor* actor; TESForm* sourceForm; slot; }` | Type covers weapon swing, spell cast/fire, bow draw/release, sheathe |
| **SKSE NiNode update** | `SKSE::GetNiNodeUpdateEventSource()` | `NiNodeUpdateEvent { TESObjectREFR* reference; }` | after actor 3D / equipment refresh |
| **Menu open/close** | `RE::UI::GetSingleton()` | `MenuOpenCloseEvent { BSFixedString menuName; bool opening; }` | `UI::GetSingleton()->AddEventSink<RE::MenuOpenCloseEvent>(sink)` |
| **Menu mode change** | same UI singleton | `MenuModeChangeEvent { BSFixedString menu; mode; }` | |
| **Raw input** | `RE::BSInputDeviceManager::GetSingleton()` | `InputEvent*` linked list | sink is `BSTEventSink<RE::InputEvent*>` — see the input note below |
| **Player cell enter/leave** | `player->AsBGSActorCellEventSource()` | `BGSActorCellEvent { ActorHandle actor; FormID cellID; kEnter/kLeave }` | use the accessor, **not** a `static_cast` |
| **Story / stat events (19)** | `T::GetEventSource()` per struct | fully modeled (see below) | `T::GetEventSource()->AddEventSink<typename T::Event>(sink)` |
| **Footsteps** | `RE::BGSFootstepManager::GetSingleton()` | `BGSFootstepEvent { ActorHandle actor; BSFixedString tag; }` | |
| **Animation events (per object)** | `graph->GetEventSource<RE::BSAnimationGraphEvent>()` | `BSAnimationGraphEvent { tag; TESObjectREFR* holder; payload; }` | per-graph; **dynamic sink — must unregister** |

**The five SKSE dispatchers** (ModEvent, Camera, Crosshair, Action, NiNodeUpdate) are static globals
inside the SKSE DLL, captured by `SKSE::Init`. They are never null after `SKSE::Init`, so you can register
against them any time after that — no `kDataLoaded` wait needed.

**Raising a Papyrus-visible mod event from C++** is the send direction of the mod-event channel: construct
a `SKSE::ModCallbackEvent` and `SendEvent` it on `GetModCallbackEventSource()`; SKSE's own handler is a
sink there and forwards to every Papyrus `RegisterForModEvent` registration. The `.psc` handler signature
for that event name is `papyrus-reference`'s surface — this is the C++ mechanism only.

**The story/stat events** (`ActorKill`, `BooksRead`, `CriticalHit`, `LevelIncrease`, `SkillIncrease`,
`ShoutAttack`, `SpellsLearned`, `LocationDiscovery`, and others) are all fully modeled, each with a static
`GetEventSource()`. Register with `T::GetEventSource()->AddEventSink<typename T::Event>(sink)`. A few
(`SpellsLearned`, `SoulsTrapped`, `ItemsPickpocketed`, `ChestsLooted`) expose a static `SendEvent` helper
to inject the vanilla stat event yourself.

**Raw input** deserves its own note because the payload is a linked list, not a single struct. The source
hands you `RE::InputEvent* const*`; walk the list, switch on `GetEventType()`, and downcast with the
type-checked `As*Event()` accessors:

```cpp
RE::BSEventNotifyControl ProcessEvent(RE::InputEvent* const* a_event,
                                      RE::BSTEventSource<RE::InputEvent*>*) override
{
    for (auto* e = a_event ? *a_event : nullptr; e; e = e->next) {
        if (e->GetEventType() != RE::INPUT_EVENT_TYPE::kButton) continue;
        const auto* btn = e->AsButtonEvent();
        if (!btn) continue;
        if (btn->IsDown()) {
            auto key  = btn->GetIDCode();     // NOT ->idCode
            auto held = btn->HeldDuration();  // NOT ->heldDownSecs
        }
    }
    return RE::BSEventNotifyControl::kContinue;
}
```

*The input-list traversal. Read every `ButtonEvent` field through an accessor, never a raw member — see
the lineage note below for why.* The traversal body itself is production-attested (OAR walks the list
identically). What is **not** attested is the registration shell — no shipped plugin in the corpus
registers a `BSTEventSink<RE::InputEvent*>` against `BSInputDeviceManager`; the input idiom is flagged as
build-test below. (OAR reaches the same argument by hooking the dispatch function instead — see
`hooking.md`.)

### Lineage notes (CommonLibSSE-NG)

The current CommonLib is **alandtse/CommonLibVR branch `ng`** (the charter-locked target). Two divergences
matter for event handling, both from NG's multi-runtime (SE + AE + VR) support — do not switch lineages to
avoid them, just follow the rule:

- **`ButtonEvent` — always read through accessors.** `value` / `heldDownSecs` / `idCode` sit at
  runtime-dependent offsets, and under cross-VR builds the raw members are compiled out entirely. Use
  `GetIDCode()` / `Value()` / `HeldDuration()` / `IsDown()` etc. Direct member access isn't merely
  discouraged — it is compile-broken or address-wrong on VR/cross builds.
- **`PlayerCharacter` event sources — use the versioned accessors.** The player-scoped bases
  (`AsBGSActorCellEventSource()`, `AsBGSActorDeathEventSource()`, `AsPositionPlayerEventSource()`) sit at
  different offsets per runtime, so reach them through those generated accessors, never a `static_cast`.
  They are proven correct for SE/AE; their VR correctness is unproven from headers.

(For context: po3's `powerof3/CommonLibSSE dev` fork is a different lineage with no VR support and no these
accessors — cross-reference only, never the primary. The CharmedBaryon 3.x fork is frozen. See
`multi-runtime.md` for the full conditional-build picture.)

## Registration timing

Register your sinks inside the SKSE messaging callback, **never in `SKSEPlugin_Load` itself** (the game
singletons don't exist yet there). The default is `kDataLoaded`. How early you *can* go depends on how the
source's singleton is built:

- **Function-call singletons** (`GetSingleton()` calls a game getter that's alive very early) can register
  as early as `kPostLoad` with a null-guard. `ScriptEventSourceHolder` and the story-event sources are
  these — SPID registers its death sink at `kPostLoad`. But holder availability that early isn't
  code-provable, so `kDataLoaded` stays the safe default.
- **Pointer-deref singletons** (`GetSingleton()` returns `*slot` on a game-populated pointer that's null
  until the game constructs the object) must wait for a milestone. `UI`, `ControlMap`, and
  `BSInputDeviceManager` are these — they aren't guaranteed live until `kInputLoaded`.

| Channel family | Earliest-safe message | Note |
|---|---|---|
| SKSE 5 dispatchers | `SKSEPlugin_Load` (after `SKSE::Init`) | static globals, never null |
| ScriptEventSourceHolder sinks | `kDataLoaded` (default); `kPostLoad` works with null-guard | the `kPostLoad` edge is build-test |
| UI menu sinks | `kDataLoaded` (proven) | `kInputLoaded` edge is build-test |
| Input / ControlMap sinks | `kInputLoaded` (earliest); `kDataLoaded` also safe | plugin-sink delivery is build-test |
| Story-event sinks | `kDataLoaded` | proven |
| Per-object graph (anim) | not milestone-bound — register when you hold the object | proven per-object |

What the launch messages are *for*, so you pick the right one:

- **`SKSEPlugin_Load`** — register SKSE *interfaces*: `GetPapyrusInterface()->Register`, the serialization
  callbacks, `GetMessagingInterface()->RegisterListener`, `AllocTrampoline`.
- **`kPostLoad`** — install hooks, read your own INI, detect incompatible DLLs.
- **`kPostPostLoad`** — request *other* plugins' interfaces (it exists so every plugin finished
  `kPostLoad` first).
- **`kInputLoaded`** — earliest point UI / input / control singletons are guaranteed live.
- **`kDataLoaded`** — the default sink-registration and form-lookup milestone.
- **`kPreLoadGame` / `kPostLoadGame` / `kNewGame` / `kSaveGame`** — per-session lifecycle, for
  priming/resetting per-save state. **Not** for one-time sink registration.

## The sink ↔ serialization bridge

When a sink accumulates per-form state — a `RegisterForModEvent`-style registration set or a plain
FormID-keyed cache — that state must survive save/load, get its stale IDs re-mapped when the load order
shifts, and get wiped on new-game or `coc`-into-a-fresh-game. **All of that is C++ plumbing owned by this
skill.** The only thing on the Papyrus side is the event *name* and its argument list.

CommonLibSSE-NG ships the `RegistrationSet` family (`RegistrationSet`, `RegistrationMap`,
`RegistrationSetUnique`, `RegistrationMapUnique`) — a ready-made VMHandle registry with `Save` / `Load` /
`Revert` built in. The load path re-resolves every stored FormID and handle (`ResolveFormID` /
`ResolveHandle`), silently dropping anything that no longer resolves — which is the *entire point* of the
co-save format, since a raw save of numeric IDs corrupts when the load order changes. You wire it up with
four `SerializationInterface` callbacks in `SKSEPlugin_Load`, and the `Revert` callback (which fires
before every load and on new-game) clears the state so it never bleeds across games. The full worked
setup — the four callbacks, the record-tag demux, `SendEvent` vs `QueueEvent` for on-thread vs
main-thread dispatch, and the hand-rolled cache variant — lives in `threading-and-persistence.md`.

## The end-to-end skeleton

A minimal, copy-paste-correct event-sink plugin covering the full `ScriptEventSourceHolder` path. Every
construct here traces to a verified fact above. The `SKSEPluginInfo` declaration block the DLL needs to
load at all is orthogonal to the sink path and lives in `plugin-skeleton.md`.

```cpp
// MyEventSink.h
#pragma once
namespace MyPlugin
{
    using EventResult = RE::BSEventNotifyControl;

    class MyEventSink final :
        public REX::Singleton<MyEventSink>,                  // static lifetime: never removed
        public RE::BSTEventSink<RE::TESQuestStartStopEvent>,
        public RE::BSTEventSink<RE::TESObjectLoadedEvent>
    {
    public:
        static void Register()
        {
            auto* holder = RE::ScriptEventSourceHolder::GetSingleton();
            if (!holder) { return; }                                   // null-guard
            holder->AddEventSink<RE::TESQuestStartStopEvent>(GetSingleton());  // idempotent, null-safe
            holder->AddEventSink<RE::TESObjectLoadedEvent>(GetSingleton());
        }

        EventResult ProcessEvent(const RE::TESQuestStartStopEvent*,
                                 RE::BSTEventSource<RE::TESQuestStartStopEvent>*) override;
        EventResult ProcessEvent(const RE::TESObjectLoadedEvent*,
                                 RE::BSTEventSource<RE::TESObjectLoadedEvent>*) override;
    };
}
```

*The singleton multi-event sink: `REX::Singleton<Self>` for lifetime, one `BSTEventSink<T>` base per
event, and one templated `AddEventSink<T>` per base.*

```cpp
// MyEventSink.cpp
#include "MyEventSink.h"
namespace MyPlugin
{
    EventResult MyEventSink::ProcessEvent(const RE::TESQuestStartStopEvent* a_event,
                                          RE::BSTEventSource<RE::TESQuestStartStopEvent>*)
    {
        if (!a_event) { return EventResult::kContinue; }               // mandatory first line
        if (auto* quest = RE::TESForm::LookupByID<RE::TESQuest>(a_event->formID)) {
            // a_event->started tells start vs stop
        }
        return EventResult::kContinue;                                 // kStop would starve later sinks
    }

    EventResult MyEventSink::ProcessEvent(const RE::TESObjectLoadedEvent* a_event,
                                          RE::BSTEventSource<RE::TESObjectLoadedEvent>*)
    {
        if (!a_event) { return EventResult::kContinue; }
        if (auto* ref = RE::TESForm::LookupByID<RE::TESObjectREFR>(a_event->formID)) {
            // a_event->loaded distinguishes load vs unload
        }
        return EventResult::kContinue;
    }
}
```

*The handler body: null-guard, resolve the FormID payload with `LookupByID<T>`, do short work, return
`kContinue`.*

```cpp
// main.cpp
#include "MyEventSink.h"

void MessageHandler(SKSE::MessagingInterface::Message* a_msg)
{
    switch (a_msg->type) {
    case SKSE::MessagingInterface::kDataLoaded:      // forms + game singletons live
        MyPlugin::MyEventSink::Register();
        break;
    default: break;
    }
}

extern "C" DLLEXPORT bool SKSEAPI SKSEPlugin_Load(const SKSE::LoadInterface* a_skse)
{
    SKSE::Init(a_skse);                              // MUST precede any interface use
    const auto messaging = SKSE::GetMessagingInterface();
    if (!messaging->RegisterListener("SKSE", MessageHandler)) {
        return false;
    }
    return true;
}
```

*The wiring: install the message listener in `SKSEPlugin_Load` after `SKSE::Init`, register sinks at
`kDataLoaded`.*

To add another event: one `BSTEventSink<T>` base, one `ProcessEvent` override, one `AddEventSink<T>` line.
Clang users add `-Wno-delete-non-abstract-non-virtual-dtor`.

## Not yet verified in-game

The event *mechanism* (sink/source layout, the four operations, dispatch, the null-guard idiom, singleton
lifetime, the registration API, the RegistrationSet spine) is derived directly from CommonLibSSE-NG source
and cross-checked against skse64 and shipped plugins — treat it as solid. Several **runtime behaviors**,
though, could not be proven from source and need a build-and-run test before you rely on them. Never
present these as proven:

- **Firing semantics of each event** — the "fires on" column is name-derived. Whether `TESDeathEvent`
  double-fires (`dead=false` then `dead=true`), exact trigger conditions, and edge cases are unconfirmed.
- **Which thread each engine source dispatches on** — nothing pins it to the main thread. Write handlers
  as potentially off-main-thread and marshal main-thread work.
- **The raw-input sink registration shell** — the traversal body is production-attested, but no shipped
  plugin proves that registering a `BSTEventSink<RE::InputEvent*>` against `BSInputDeviceManager` actually
  delivers per-frame, nor that `kStop` suppresses downstream input, nor the required message. Treat the
  input-sink idiom as unverified until you build-test it. (The proven alternative is a dispatch-function
  hook — see `hooking.md`.)
- **The `kPostLoad` / `kInputLoaded` early-registration edges** — `kDataLoaded` is proven for every
  channel; earlier windows are inferred, not observed.
- **Synthetic sends reaching Papyrus** — whether `SendActivateEvent` / `SendSpellCastEvent` / a mod-event
  send actually fire the corresponding Papyrus handlers.
- **VR specifics** — the `TESFastTravelEndEvent` null-source crash on VR, VR touchpad event downcasts, and
  the versioned player accessors' VR correctness are all header-inferred, not run.
- **AE story-event fragility** — po3 compiles out `SpellsLearned` Papyrus forwarding on AE because it
  crashes; verify any story-event sink per-runtime before relying on it across SE/AE/VR.

When you reach the empirical build-test phase, these are the gates to burn down. Until then, houseCARL's
Q3 rule holds: an unverified runtime behavior is stated as unverified, never as fact.
