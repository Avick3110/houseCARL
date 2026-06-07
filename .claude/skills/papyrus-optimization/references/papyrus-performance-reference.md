# Papyrus Performance Reference (the "why" behind the rubric)

This is the depth layer for the `papyrus-optimization` skill. `SKILL.md` carries the review
workflow and the rubric; this file carries the cost model, the mechanics, the full technique
catalogue, the thresholds, and the myth table that justify each rubric verdict. Load it when you
need the *reason* behind a classification, an exact number, or a citation — not on every review.

Sourcing is **official-first**. "Official" = the Bethesda Creation Kit wiki, canonical live mirror
**`ck.uesp.net`** (the `creationkit.com` domain is frequently down; a second open mirror is
`fallout.wiki`). Community sources (forum benchmarks, modder writeups, decompiled source) are
secondary and tagged `[community]`. Benchmark *magnitudes* are single-source where noted; the
*mechanisms* are corroborated by official docs.

For function signatures, parameters, and flags, use the `papyrus-reference` skill — this file is
about cost and habits, not call shapes.

## Contents

1. The one formula — cost = frequency × latency
2. The execution model (threaded, latency-bound VM; the budget; persistence; saves)
3. What makes a script heavy (the cost drivers)
4. Optimization techniques (the fixes, with before→after)
5. What is "acceptable" — thresholds and the myths
6. Sources

---

## 1. The one formula — cost = frequency × latency

> **A script's cost ≈ (how often its code runs) × (how much latent/native work each run does).**

Every rule below is a corollary. A 200-line script that runs once at quest start is free. A
three-line `OnUpdate` firing every 0.1 s with two native calls can saturate the VM. When you read a
script, look first at **what triggers it** and **how many engine calls live on the hot path** — not
at the line count. Two consequences that cut against intuition:

- **Pure computation is nearly free; engine calls are not.** Arithmetic, string assignment,
  branches, and array element access cost almost nothing. The expensive thing is *talking to the
  game* (any native/external call). Optimizing almost always means **fewer engine calls, less
  often** — not tighter arithmetic.
- **Micro-optimizations only matter on the hot path.** The canonical benchmark explicitly cautions
  the differences "only matter [if] you are creating a loop that will run hundreds or thousands of
  times" ([gamesas: Performance characteristics](https://www.gamesas.com/performance-characteristics-papyrus-functions-and-operati-t260999.html), `[community]`).
  A `b == true` that runs once is not worth touching; the same line in a 0.1 s loop is.

---

## 2. The execution model

### 2.1 Event-driven, not autonomous

Papyrus code runs **only in response to the game or another script** — inside an `Event` block or a
fragment. There is no main loop you write; the only "run repeatedly" is *registering* for an update
event ([Papyrus Introduction](https://ck.uesp.net/wiki/Papyrus_Introduction)). The cheapest script
is one the engine only wakes when something it cares about happens.

### 2.2 Threaded, lock-per-instance VM

"Papyrus is a threaded scripting language… the game can run multiple scripts in parallel." Only
**one thread at a time** may touch a script instance; the first thread locks it and others queue, in
**unpredictable** order. A script that hogs CPU is suspended so others run ([Threading Notes](https://ck.uesp.net/wiki/Threading_Notes_(Papyrus))).

### 2.3 Any external call suspends your thread — the source of latency

The most important mechanical fact:

> "Any time a thread makes an external call (to a function on a different object, to a global
> function, or to a function in a script with a different self), its execution is suspended… The
> nature of the function does not matter: it can be native or non-native, latent or not… Even
> native non-delayed functions (like Debug.Trace) are external calls." ([Threading Notes](https://ck.uesp.net/wiki/Threading_Notes_(Papyrus)))

**Not** external: reading/writing a property **defined in the same script** (lowered to a variable
access) and **all array operations** (read, write, length, find, rfind) (ibid.). And:

> "native functions all return after at least one frame (the non-delayed functions excepted)… the
> VM can process about a few thousands of calls per frame." (ibid.)

So a **delayed native call costs ~one frame of latency.** Community quantifies it: ~90% of a
script's wall-time is "simply waiting for delayed native functions," and "if you call 30 native
functions and the game is running at 30 fps, it will take (roughly) 1 second to get through them
all" ([gamesas: Game engine vs Papyrus efficiency](https://www.gamesas.com/game-engine-papyrus-efficiency-questions-t373162.html), `[community]`, corroborating the official "≥1 frame" statement).

> **Papyrus is latency-bound, not compute-bound.** A "heavy" script usually issues many delayed
> native calls on a frequent trigger — each parks the script for a frame. Reduce the *count* and
> *frequency* of engine calls and you have done most of the optimization.

### 2.4 The per-frame budget is tiny

`Skyrim.ini` `[Papyrus]` defaults, verbatim ([INI Settings (Papyrus)](https://ck.uesp.net/wiki/INI_Settings_(Papyrus))):

| Setting | Default | What it does |
|---|---|---|
| `fUpdateBudgetMS` | **1.2** | Time/frame for the main Papyrus update loop (function dispatch). |
| `fExtraTaskletBudgetMS` | **1.2** | Extra/frame for running script tasklets (byte-code), borrowed from another thread. |
| `fPostLoadUpdateTimeMS` | **500.0** (2000 old consoles) | Extra script time on the load screen for cell/quest setup. |
| `iMinMemoryPageSize` | **128** | Smallest VM stack-page allocation (bytes). |
| `iMaxMemoryPageSize` | **512** | Largest VM stack-page allocation (bytes). |
| `iMaxAllocatedMemoryBytes` | **76800** (~75 kB) | Total VM **stack-frame** memory cap. |
| `bEnableLogging`/`bEnableTrace` | **0** | Papyrus log + trace; off by default ("may improve performance due to less disk activity"). |

At 60 fps a frame is ~16.67 ms, of which Papyrus gets ~**1.2 ms** by default — the rest is rendering
and the engine ([Thallassa](https://thallassathoughts.wordpress.com/2016/09/16/myths-and-legends-papyrus-ini-settings/),
[STEP](https://stepmodifications.org/wiki/Guide:Skyrim_INI/Papyrus), `[community]`). The official
note on `fUpdateBudgetMS` is explicit: raising it buys script time **"at the cost of reduced game
framerate,"** and "most of the time the VM won't take this entire time slice." This is a description
of how little time scripts get — not a dial to turn (see §5 myths).

### 2.5 Stacks, suspension, stack dumps

A stack is **suspended** by a latent call — `Utility.Wait(n)` parks it for `n` real seconds
([Persistence](https://ck.uesp.net/wiki/Persistence_(Papyrus))). When too many suspended stacks pile
up, the VM logs the canonical **stack dump** (`Suspended stack count is over our warning threshold…
VM is freezing… VM is frozen`) ([INI Settings](https://ck.uesp.net/wiki/INI_Settings_(Papyrus))). A
stack dump is a **read-only diagnostic, not a crash** — it cannot damage a save; it is a symptom that
the VM was overstressed (~5 s by default) ([Stack Dumps are harmless](https://www.nexusmods.com/skyrimspecialedition/articles/4625), `[community]`). Fix the cause; never silence the log.

### 2.6 Persistence — the quiet cost

Persistence keeps an `ObjectReference` loaded so it "consume[s] processor time and memory when other
references in the same area will have disappeared and unloaded" ([Persistence](https://ck.uesp.net/wiki/Persistence_(Papyrus))). Official causes + checklist:

- A **running function** (incl. long latent ones) pins its object until it returns.
- A **property pointed at a placed reference** flags that reference **permanently persistent** —
  *"nothing you do during runtime will unload the object,"* even reassigning. **"If possible, you
  should not use properties to point at references directly."**
- A **variable** holding a reference pins it only until no variable points at it; clear with `None`.
- **Registering for an event** (`OnUpdate`, `OnLOS`, `OnSleep`, `OnAnimationEvent`…) pins until
  unregister.

Official checklist (verbatim): *avoid long-running functions; avoid properties to references if
possible; ref variables should point only as long as needed; only register for updates as long as
you need them.*

### 2.7 The save game bakes script state

The game can save at any instant — between or mid-line — and resumes scripts on load *"assuming you
don't change anything"* ([Save File Notes](https://ck.uesp.net/wiki/Save_File_Notes_(Papyrus))).
Consequences that drive bloat and corruption:

- Remove a script after a save and it **still exists on load** (orphaned script); deleting one
  entirely "may result in oddities."
- Removed properties/variables **linger in the save** until the next save.
- **Auto** properties restore from the master; **non-auto** properties and uninitialized variables
  come back blank/zeroed.
- A variable pointing at a form from a **removed plugin** becomes a permanent "missing" placeholder
  (ID #0) — restoring the plugin **does not** recover it once re-saved.
- This is why a leftover `RegisterForUpdate` from an uninstalled mod **keeps firing forever**, baked
  into the save, erroring each tick (§3.1).

---

## 3. What makes a script heavy

Roughly ordered by damage.

1. **Polling.** `RegisterForUpdate` with a small interval **can freeze the game**; if the handler
   runs longer than its interval, calls "are queued faster than they are processed… can lead to
   savegames as large as one gigabyte!"; a removed mod's handler "still… registered… periodic errors
   on every tick" ([RegisterForUpdate](https://ck.uesp.net/wiki/RegisterForUpdate_-_Form)). `OnUpdate`
   doesn't fire in menus and "can start piling up on itself," increasing save size ([OnUpdate](https://ck.uesp.net/wiki/OnUpdate_-_Form)).
2. **Many latent/native calls per run.** Each delayed native ≈ one frame; loops calling natives per
   iteration are the usual culprit.
3. **High-frequency events with heavy bodies.** `OnHit`, `OnAnimationEvent`, cloak
   `OnMagicEffectApply`/`OnEffectStart` fire often; heavy work with no cheap early-out runs every fire.
4. **Repeated uncached lookups.** `Game.GetPlayer()` every event, `GetFormFromFile` in logic,
   `GetWornForm() as Armor` chains. A cached `PlayerRef` property is **~1000×** faster than
   `Game.GetPlayer()` (`[community]` benchmark; mechanism is the §2.3 external-call rule).
5. **Cross-script reads on the hot path.** A property on **another** object is an external call (~2×
   a local read).
6. **Cloak/scan distribution.** Cloaks re-apply to every actor in range each cycle — cost scales with
   range (the cloak effect's *magnitude* encodes range), cycle rate, handler weight. Community
   consensus: distribute via **SPID** instead. *Contested as a universal ban* — a single well-scoped
   cloak with a cheap handler is not catastrophic ([gamesas](https://www.gamesas.com/will-scripts-work-cloak-spell-t259235.html), `[community]`).
7. **Wrong data structure.** Container `GetItemCount` as a counter forces native inventory serialisation
   per access; arrays cap at **128**, sized by a compile-time literal, reallocate on resize ([Arrays](https://ck.uesp.net/wiki/Arrays_(Papyrus))).
8. **Persistence leaks.** Never-unregistered updates, properties pinning refs, ref vars never cleared
   to `None` (§2.6).
9. **`Utility.Wait` loops** holding a thread for their whole duration.
10. **Logging in hot paths.** Every `Debug.Trace`/`Notification` is an external call + disk I/O.

---

## 4. Optimization techniques

### 4.1 Cache anything that doesn't change
```papyrus
; ✗ external call every time
Actor player = Game.GetPlayer()
; ✓ fill once in the CK; thereafter a free same-script property access
Actor Property PlayerREF Auto
```
Same for a target actor inside a frequent handler — store `akTarget` in a variable in
`OnEffectStart`, reuse it, instead of re-calling `GetTargetActor()` each event.

### 4.2 Prefer properties to runtime form lookups
Fill a `Form`/`Spell`/`Keyword` property in the CK rather than `Game.GetFormFromFile(...)` in logic
(`GetFormFromFile` is for *optional* cross-mod soft deps). `[community]`; the CK wiki makes no cost
claim here, so treat as best-practice, not an official number.

### 4.3 Replace polling with events — highest leverage
If an engine event already signals the condition, register for it instead of polling.

| Instead of polling for… | Use |
|---|---|
| player got hit | `OnHit` (or PO3 `OnHitEx`, filters in C++) |
| started/stopped sneaking/attacking | `OnAnimationEvent` / `RegisterForActorAction` |
| a specific magic effect landed | PO3 `OnMagicEffectApplyEx` |
| inventory/equip changed | `OnItemAdded` / `OnObjectEquipped` |
| distribute to many NPCs | **SPID** (no per-frame scan) instead of a cloak |

Re-register listeners in `OnPlayerLoadGame` so they survive save/load.

### 4.4 If you must poll, poll correctly
```papyrus
Event OnInit()
    RegisterForSingleUpdate(5.0)
EndEvent
Event OnUpdate()
    ; …work…
    If bKeepGoing                      ; explicit exit
        RegisterForSingleUpdate(5.0)   ; re-arm only if still needed
    EndIf
EndEvent
```
Why this beats `RegisterForUpdate`: the next tick can't start before the previous finished (no
pile-up); an error stops it instead of erroring every tick forever; you control the exit
([RegisterForUpdate](https://ck.uesp.net/wiki/RegisterForUpdate_-_Form)). Pick the **longest interval
the feature tolerates**. Bare `RegisterForUpdate` is legitimate only for a script guaranteed not to
live long, or millisecond-critical timing. An `OnPlayerLoadGame` re-arm is cheap insurance, but not strictly required for a
single-update chain — a pending `RegisterForSingleUpdate` is itself serialized and generally
re-fires after load, so the chain usually survives on its own.

### 4.5 Use states to gate events at the engine level
Defining an event **empty inside a state** switches it off — *"simply define that event or function
in the state and leave it empty"* ([States](https://ck.uesp.net/wiki/States_(Papyrus))). Community
holds this is cheaper than a boolean guard because the engine skips dispatching an empty-state handler
rather than entering the function to check a flag ([cipscis: States](http://www.cipscis.com/skyrim/tutorials/states.aspx), `[community]`).
```papyrus
Event OnHit(...)
    DoThing()
    GoToState("Done")
EndEvent
State Done
    Event OnHit(...)
    EndEvent           ; empty — engine stops calling it
EndState
```
Switch state **before** any external call in the handler (an external call can let another thread in
first). Return to default with `GoToState("")`.

### 4.6 Offload to native (SKSE) instead of looping in Papyrus
SKSE natives are **not** slower than vanilla natives (`[community]`). High-value offloads (confirm
signatures via `papyrus-reference`):

| Papyrus pattern | Native replacement | Source |
|---|---|---|
| Cloak → iterate hit actors | `MiscUtil.ScanCellNPCs(center, radius, keyword, ignoreDead)` | PapyrusUtil |
| Container-count counter | `StorageUtil.AdjustIntValue` / `GetIntValue` | PapyrusUtil |
| Hand-rolled array list | `StorageUtil.FormListAdd/Get/Count/Has` (dynamic, persistent) | PapyrusUtil |
| Loop testing for an active spell/effect | `PO3_SKSEFunctions.HasActiveSpell` / `HasActiveMagicEffect` | PapyrusExtender |
| `OnHit` + Papyrus `If` filters | `PO3_Events_AME.RegisterForHitEventEx` → `OnHitEx` | PapyrusExtender |
| Poll for "did effect X apply" | `PO3_Events_AME.RegisterForMagicEffectApplyEx` → `OnMagicEffectApplyEx` | PapyrusExtender |
| Script-maintained behaviour override | `ActorUtil.AddPackageOverride` | PapyrusUtil |

### 4.7 Persistence & cleanup hygiene
Symmetric add/remove (`AddSpell`/`AddPerk` in `OnEffectStart` ↔ `RemoveSpell`/`RemovePerk` in
`OnEffectFinish`); `UnregisterForUpdate` when a chain ends; clear ref variables to `None`; prefer a
**`ReferenceAlias`**-extending script over directly scripting an `ObjectReference`/`Actor` (aliases
sever cleanly when the quest stops, avoiding orphaned registrations after uninstall); don't point
properties at placed references.

### 4.8 Minimize cross-script overhead
Cache a remote property into a local once; use `Import ScriptName` to call its globals without a
wrapper; make stateless helpers **`Global`** (call cost fastest→slowest: inline → native global →
user global → local function).

### 4.9 Micro-optimizations — hot path only
Bytecode-demonstrated ([Dennis Soemers: 11 micro-optimisations](https://dennissoemers.github.io/skyrim/papyrus/programming/optimisations/microoptimisations-skyrim/), `[community]`):
- Call the native, not its wrapper: `GetActorValue("Health")` not `GetAV`; `GetReference() as Actor`
  not `GetActorReference()`; `GetValue() as int` not `GetValueInt()`.
- `If (b)` not `If (b == true)`; `If (!b)`/`If (b == false)` not `If (b != true)`.
- Assign booleans directly: `bool a = someExpr`.
- Match literal types: `Utility.Wait(1.0)` not `Wait(1)`; `If (f > 1.0)` not `> 1`.
- `x as int` not `Math.Floor(x)` (~14× the conversion).
- Factor offsets: `x + Utility.RandomInt(5, 20)` not `RandomInt(x+5, x+20)`.
- Prefer **auto** properties (VM optimizes them slightly; [Variables and Properties](https://ck.uesp.net/wiki/Variables_and_Properties)).

### 4.10 Tooling
External editor + compiler; `Debug.Trace` freely in testing, gated/removed for release; profile with
[PapyrusProfiler](https://github.com/DennisSoemers/PapyrusProfiler) (counts call *frequency*, not
time) or `Debug.StartStackProfiling` + speedscope.

---

## 5. What is "acceptable" — thresholds and the myths

### 5.1 Honest thresholds
No single enforced "max ms per script"; "acceptable" is the **frequency × latency** product against
~1.2 ms/frame.
- **One-shot / event-driven work is always acceptable**, however long — it runs rarely.
- **Update intervals:** the longest the feature tolerates. Sub-second polling, *multiplied across
  many scripts*, is where saturation builds. Under "a few seconds," use a `RegisterForSingleUpdate`
  chain, never bare `RegisterForUpdate`.
- **Native calls per hot-path run:** keep them low. "30 native calls at 30 fps ≈ 1 second."
- **Polling is acceptable only when** no event exists, the interval is as long as tolerable, and the
  chain has an exit.
- **Micro-opts are don't-care off the hot path.**

### 5.2 Symptoms of crossing the line
Saturation shows as **deferred, not lost** work — "Papyrus does not arbitrarily discard things, you
just get lag": late doors/containers, slow MCM, late dialogue/quest stages ([Nexus: Script Heavy Mods](https://www.nexusmods.com/skyrim/articles/52598), `[community]`). **Stack dumps** in the log are the
explicit warning (harmless symptom; trace to cause).

### 5.3 Myths — do not propagate these

| Myth | Reality | Source |
|---|---|---|
| "Raise `fUpdateBudgetMS`/tasklet budget to fix lag." | Stolen from frame-draw time — trades FPS for dispatch, creates no capacity; the VM usually doesn't use the whole slice. Narrow exception: a small bump (1.2→1.6) on an FPS-capped system with headroom. | [INI Settings](https://ck.uesp.net/wiki/INI_Settings_(Papyrus)) `[official]`; [Thallassa](https://thallassathoughts.wordpress.com/2016/09/16/myths-and-legends-papyrus-ini-settings/), [STEP](https://stepmodifications.org/wiki/Guide:Skyrim_INI/Papyrus) `[community]` |
| "Enlarge `iMin/iMaxMemoryPageSize` to stop stack dumps." | VM allocates new stacks on demand; needs more, not bigger. **"DON'T TOUCH THESE."** | [Thallassa](https://thallassathoughts.wordpress.com/2016/09/16/myths-and-legends-papyrus-ini-settings/) `[community]` |
| "Multiply `iMaxAllocatedMemoryBytes` massively." | It's a **stack** cap; exceeding it makes the VM *wait*, not crash. Inflating causes "stack thrashing… intermittent game stuttering, erratic game behavior and CTDs." | [INI Settings](https://ck.uesp.net/wiki/INI_Settings_(Papyrus)) `[official]` + [Thallassa](https://thallassathoughts.wordpress.com/2016/09/16/myths-and-legends-papyrus-ini-settings/) `[community]` |
| "Papyrus is just a slow interpreter." | The slowness is architectural **latency** (one frame per delayed native), not weak compute. | [Threading Notes](https://ck.uesp.net/wiki/Threading_Notes_(Papyrus)) `[official]`; [gamesas](https://www.gamesas.com/game-engine-papyrus-efficiency-questions-t373162.html) `[community]` |
| "A stack dump corrupts your save." | Read-only diagnostic; fix the cause, don't silence the log. | [Stack Dumps are harmless](https://www.nexusmods.com/skyrimspecialedition/articles/4625) `[community]` |
| "`fPostLoadUpdateTimeMS` is unsafe." | Safe; only lengthens the load screen (keep < 1000). | [Thallassa](https://thallassathoughts.wordpress.com/2016/09/16/myths-and-legends-papyrus-ini-settings/) `[community]` |

The legitimate "make Papyrus faster" is an **engine mod**, not an INI edit:
[Papyrus Tweaks NG](https://www.nexusmods.com/skyrimspecialedition/mods/77779) raises the antique
"100 operations per tasklet" throughput cap (untouched since 2011), adds **SpeedUpNativeCalls** (syncs
read-only getter natives to a spinlock instead of the framerate — attacking the latency bottleneck
directly), caches `GetFormFromFile`, and makes the stack-dump timeout configurable (`[community]`).

---

## 6. Sources

**Official — Creation Kit wiki (`ck.uesp.net`):**
[Papyrus Introduction](https://ck.uesp.net/wiki/Papyrus_Introduction) ·
[Threading Notes](https://ck.uesp.net/wiki/Threading_Notes_(Papyrus)) ·
[Persistence](https://ck.uesp.net/wiki/Persistence_(Papyrus)) ·
[Save File Notes](https://ck.uesp.net/wiki/Save_File_Notes_(Papyrus)) ·
[INI Settings](https://ck.uesp.net/wiki/INI_Settings_(Papyrus)) ·
[OnUpdate](https://ck.uesp.net/wiki/OnUpdate_-_Form) /
[RegisterForUpdate](https://ck.uesp.net/wiki/RegisterForUpdate_-_Form) /
[RegisterForSingleUpdate](https://ck.uesp.net/wiki/RegisterForSingleUpdate_-_Form) ·
[States](https://ck.uesp.net/wiki/States_(Papyrus)) ·
[Arrays](https://ck.uesp.net/wiki/Arrays_(Papyrus)) ·
[Variables and Properties](https://ck.uesp.net/wiki/Variables_and_Properties).

**Community — benchmarks, guides, tools:**
[gamesas: Performance characteristics](https://www.gamesas.com/performance-characteristics-papyrus-functions-and-operati-t260999.html) (canonical micro-benchmark; single source for magnitudes) ·
[gamesas: Game engine vs Papyrus efficiency](https://www.gamesas.com/game-engine-papyrus-efficiency-questions-t373162.html) ·
[Dennis Soemers: 11 micro-optimisations](https://dennissoemers.github.io/skyrim/papyrus/programming/optimisations/microoptimisations-skyrim/) + [PapyrusProfiler](https://github.com/DennisSoemers/PapyrusProfiler) ·
[Thallassa: Papyrus INI myths](https://thallassathoughts.wordpress.com/2016/09/16/myths-and-legends-papyrus-ini-settings/) ·
[STEP: Skyrim INI/Papyrus](https://stepmodifications.org/wiki/Guide:Skyrim_INI/Papyrus) ·
[Beyond Skyrim — Arcane University: Scripting Best Practices](https://wiki.beyondskyrim.org/wiki/Arcane_University:Scripting_Best_Practices) ·
[cipscis tutorials](http://www.cipscis.com/skyrim/tutorials/) ·
[Nexus: On Script Heavy Mods and Engine Overload](https://www.nexusmods.com/skyrim/articles/52598) /
[Stack Dumps are harmless](https://www.nexusmods.com/skyrimspecialedition/articles/4625) ·
[Papyrus Tweaks NG](https://www.nexusmods.com/skyrimspecialedition/mods/77779) ·
source: [Grimy decomp](https://github.com/Rukan/Grimy-Skyrim-Papyrus-Source), [PapyrusUtil](https://github.com/noxsidereum/PapyrusUtil), [PapyrusExtenderSSE](https://github.com/powerof3/PapyrusExtenderSSE).

> **Certainty note.** Official mechanics (threading/latency model, budget defaults, persistence, save
> baking, update behavior, the memory-inflation warning) are fact. Benchmark *magnitudes* ("~1000×",
> "~13× batching") are individual community measurements — directionally reliable, consistent with the
> official mechanism, not precise constants. "States gate at zero engine cost" and "scripted cloaks
> are always bad" are community consensus with an official basis but contested edges; flag them as
> such when you cite them.
