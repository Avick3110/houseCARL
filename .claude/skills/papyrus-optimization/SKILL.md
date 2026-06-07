---
name: papyrus-optimization
description: Review and optimize Papyrus (`.psc`) scripts — classify each part as broken, suboptimal, or clean, explain what makes it heavy, and give the fix (event-driven, caching, states, native offload). Use when the user shares or points at a `.psc`, asks why a mod causes script lag, stack dumps, or Papyrus log spam, wants to know if a script in the load order is heavy, asks to review, optimize, or speed up a script, or questions a `RegisterForUpdate`/`OnUpdate` loop, a cloak scan, `Utility.Wait` chains, uncached `Game.GetPlayer()`/`GetFormFromFile`, persistence or save-bloat, or whether an update interval is acceptable. Load this before judging any `.psc`, even if it looks like a trivial one-liner — Papyrus cost is latency, not line count, and the heavy patterns hide in scripts that look simple.
---

# Papyrus Optimization

## Overview

This skill is a read-and-assess investigation flow for Papyrus (`.psc`) source. It lets you read a
script and classify each part as **🔴 broken**, **🟡 suboptimal**, or **🟢 clean**, name *what* makes
a part heavy, and give the specific fix — so you catch performance problems a signature lookup can't
see. It is the cost-and-habits complement to the `papyrus-reference` skill (which gives function
signatures): that one tells you *what a call is*; this one tells you *what it costs and whether to
make it*. The deep cost model, exact numbers, full technique catalogue, and citations live in
[`references/papyrus-performance-reference.md`](references/papyrus-performance-reference.md) — load it
when you need the *why* behind a verdict; the workflow below is enough for most reviews.

It reads script *text* rather than houseCARL's record/conflict surfaces, so it composes with normal
file reading (or `housecarl_bsa_extract` + the decompiler when the script is packed) — no L2 query
surface is required.

## First step: establish the trigger before judging anything

The whole review hinges on one formula:

> **cost = how often the code runs × how much latent/native work each run does.**

So before classifying any line, find **what wakes this code** — a one-shot event (`OnInit`,
`OnEffectStart`), a real engine event (`OnHit`, `OnActivate`), or a periodic update
(`RegisterForUpdate`/`OnUpdate` chain). The same line is 🟢 in a one-shot and 🔴 in a 0.1 s loop.
Why this comes first: it sets the cost multiplier for everything else, and it stops you from
red-flagging a long-but-rare init script or acquitting a short-but-constant poller. Read the triggers,
then read the body.

## The three tiers

- **🔴 Broken** — incorrect, leaks, will saturate/freeze the VM, or violates an engine contract. Fix
  before shipping. (Silent no-ops, persistence/registration leaks, polling that can freeze the game,
  missing cleanup that corrupts state, event signatures the engine never calls.)
- **🟡 Suboptimal** — correct, but a strictly-better idiom exists. Fix if it's on a hot path or the
  fix is cheap; otherwise note and move on. (Uncached lookups, bare `RegisterForUpdate`, polling where
  an event exists, wrong data structure, hot-path logging, off-the-shelf micro-misses.)
- **🟢 Clean** — idiomatic and as optimized as it usefully gets. Leave it. (Event-driven, early-out
  guards, cached refs, single-update chains with an exit, states for gating, native offload, symmetric
  cleanup.)

Judge against frequency × latency, not line count — a finding's tier rises with how often its trigger
fires.

## Review checklist

Walk these top-to-bottom. The early questions catch 🔴; the later ones catch 🟡. For the mechanism
behind any item, see the reference file's matching section.

1. **Trigger?** One-shot, event, or periodic update? (Sets the multiplier for everything below.)
2. **Event contracts valid?** Does every `Event` block match a real engine event's name and signature?
   A misspelled/invented event silently never fires → 🔴. Is it expecting `OnUpdate` during menus
   (won't fire)?
3. **Polling safe?** Any `RegisterForUpdate`? Small interval (freeze risk) → 🔴. No
   `UnregisterForUpdate` / no `OnPlayerLoadGame` re-arm / handler possibly longer than its interval
   (save-bloat) → 🔴/🟡. Could it be a `RegisterForSingleUpdate` chain, or fully event-driven?
4. **Latent loops?** A `Utility.Wait` chain inside an event holding the thread → 🟡/🔴 by duration and
   frequency. Prefer a single-update chain.
5. **Cached?** Repeated `Game.GetPlayer()` / `GetFormFromFile` / `GetWornForm`-style native chains
   resolving unchanging values each run → 🟡 (🔴 in a tight loop).
6. **Persistence clean?** Property pointed at a placed reference (permanent persistence)? Ref variables
   never cleared to `None`? Registrations never undone? → 🔴 (leak) / 🟡.
7. **Cleanup symmetric?** Every `Add*`/`Register*` matched by a `Remove*`/`Unregister*`? Does a chain
   have an exit and a state machine a `GoToState("")` back? Missing → 🔴.
8. **Hot-path weight?** In a frequently-firing handler (`OnHit`, `OnAnimationEvent`, cloak): is there a
   cheap early-out before the expensive work? Many native calls on the path? No guard / many calls →
   🟡.
9. **Right tool?** Container-as-counter, 128-cap array bookkeeping, or a cloak scan where a native
   (`StorageUtil`, `MiscUtil.ScanCellNPCs`, PO3 filtered events) does it in one call → 🟡.
10. **States vs flags?** Re-entrancy/do-once handled by a boolean guard where an empty-state handler
    would gate at the engine level → 🟡.
11. **Logging gated?** `Debug.Trace`/`Notification` on a hot path in a release build → 🟡.
12. **Micro (hot path only):** abbreviated AV wrappers, `== true`, int-literal-to-float, `Math.Floor`
    vs `as int`, non-`Global` stateless helpers — inside a loop/frequent event → 🟡; elsewhere → 🟢
    (don't-care).

## Symptom → tier → fix

| Symptom in the script | Tier | Why | Fix |
|---|---|---|---|
| `Event` name/signature matches no engine event | 🔴 | Never called — silent no-op | Correct to the real event signature |
| `RegisterForUpdate(0.1)`-style small interval | 🔴 | Can freeze the VM; bloats saves | `RegisterForSingleUpdate` chain, longest tolerable interval |
| Update handler longer than its interval | 🔴 | Calls queue faster than they drain → 1 GB saves | Single-update chain (next tick waits for previous) |
| `RegisterForUpdate` with no unregister / survives uninstall | 🔴 | Registration baked into save, fires forever, errors each tick | Single-update chain + `OnPlayerLoadGame` re-arm; alias script |
| Property pointed at a placed reference | 🔴 | Reference permanently persistent — never unloads | Point at base form; resolve ref at runtime, clear when done |
| `Add*` with no matching `Remove*` on effect end | 🔴 | Spell/perk leak when effect expires/dispels | Symmetric `OnEffectFinish` cleanup |
| `Utility.Wait` chain in an event for a timed sequence | 🟡/🔴 | Blocks/pins the thread for the duration | Convert to a `RegisterForSingleUpdate` tick chain |
| Polling for a condition that has an event | 🟡 | Idle ticks doing nothing | Register for the event (`OnHit`, `OnAnimationEvent`, PO3 `OnHitEx`…) |
| `Game.GetPlayer()` / `GetFormFromFile` re-called every run | 🟡 | External call each time (~1000× a cached read) | Cache in an auto property / `OnInit` |
| Cloak spell scanning nearby actors | 🟡 | Re-applies to everyone in range each cycle | SPID for distribution, or `MiscUtil.ScanCellNPCs` |
| Container `GetItemCount` used as a counter | 🟡 | Native inventory serialisation per access | `StorageUtil.AdjustIntValue`/`GetIntValue` |
| Cross-script property read in a hot loop | 🟡 | External call (~2× local) per read | Cache to a local variable once |
| Boolean guard for do-once / re-entrancy | 🟡 | Event still fires, enters function, checks flag | Empty-state handler gates at engine level |
| `Debug.Trace` on a hot path (release) | 🟡 | External call + disk I/O every fire | Gate behind a debug flag / remove |
| `GetAV`, `== true`, `Wait(1)`, `Math.Floor` *in a loop* | 🟡 | Extra bytecode/cast/call layer per iteration | Native names, `If(b)`, `1.0`, `as int` |
| Event-driven, early-out guard, cached refs, single-update chain w/ exit, symmetric cleanup | 🟢 | Minimal frequency × latency | Leave it |

**Red flags at a glance:** `RegisterForUpdate(` with a small literal · an `OnUpdate` with no exit
condition · `Utility.Wait` inside a loop · a property whose value is a placed reference ·
`Game.GetPlayer()` inside a loop or frequent event · a cloak/scan distribution · `GetItemCount` as a
number · `Add*`/`Register*` with no matching `Remove*`/`Unregister*` · `Debug.Trace` in
`OnHit`/`OnUpdate`.

## Common review mistakes

- **Judging a line before its trigger.** A 7-second `Utility.Wait` chain is alarming in a 0.1 s loop
  and fine in a once-per-game flourish. Always resolve step 1 first — otherwise you flag rare code and
  miss constant code.
- **Chasing micro-opts off the hot path.** `b == true` or `Wait(1)` in a one-shot is 🟢, not 🟡. The
  micro-optimizations only pay inside loops that run hundreds/thousands of times; spending the review
  there on rare code is wasted, and rewriting it adds risk for no gain.
- **Only hunting for bad patterns.** Recognise and *credit* the good ones — a cached `PlayerRef`, an
  early-out guard, an `OnPlayerLoadGame` re-arm. A review that calls everything 🟡 is as useless as one
  that calls everything 🟢.
- **Repeating INI folklore.** "Raise the Papyrus budget / memory page size to fix lag" is debunked
  (reference §5.3). Don't recommend INI edits as a script fix; fix the script, or point at an engine
  mod (Papyrus Tweaks NG) for genuine engine limits.
- **Inventing a signature to justify a verdict.** If a verdict depends on a function's exact behavior,
  confirm it via `papyrus-reference` or the read source — don't assert a signature from memory.

## Make a defensible verdict (no silent wrong answers)

A script review must land on one of two honest outcomes, never a confident guess:

1. **A classification with a reason and a fix** — "🟡: `Game.GetPlayer()` on lines 81/94/204 re-resolves
   the player each call; cache it in an auto `PlayerREF` property. Low severity here because the
   trigger is a one-shot init." Tie the tier to the trigger and name the concrete fix.
2. **An explicit "I can't tell yet — here's what I checked and what to look at next"** — e.g. when the
   trigger frequency depends on how an effect is applied (a magic effect's delivery, a quest alias's
   fill), or when behavior hinges on a function whose semantics you haven't confirmed. Say what you'd
   need (the effect setup, the calling script, a `papyrus-reference` lookup).

A confidently wrong "this is fine" or "this is broken" is worse than a clear non-answer — it sends the
user to fix the wrong thing or ship a real problem. Prefer the honest gap.

## Worked example

Two real decompiled snippets, classified.

🟢 **Event-driven with an early-out** — fires only on a blocked hit; guards before the expensive cast:
```papyrus
Event OnHit(ObjectReference akAggressor, Form akSource, Projectile akProjectile, \
            bool abPowerAttack, bool abSneakAttack, bool abBashAttack, bool abHitBlocked)
    If abHitBlocked
        GrimyAbCounterStrikeSpell.Cast(spellTarget, spellTarget)
    EndIf
EndEvent
```
Why 🟢: the engine wakes it only on a hit (no idle cost), and the `abHitBlocked` guard means the cast
runs only on the rare blocked hit. Trigger is event-driven; hot-path work is gated. Nothing to fix.

🔴 **`Utility.Wait` bleed chain** — blocks the thread ~3 s, pinning the object:
```papyrus
Event OnEffectStart(Actor akTarget, Actor akCaster)
    akTarget.DamageActorValue("Health", dmg)
    Utility.Wait(0.6)
    akTarget.DamageActorValue("Health", dmg)
    Utility.Wait(0.6)
    ; …×5
EndEvent
```
Why 🔴 (in context): the `Wait` chain holds this script's thread for the full bleed, pinning the actor
and blocking other events on the script. Fix: drive the ticks with a `RegisterForSingleUpdate` chain
that re-arms until the bleed count is spent, so the thread is free between ticks.

## Notes

- **Severity is contextual.** Every tier in the tables assumes a hot-path trigger; down-weight on rare
  triggers per step 1. State the trigger in the verdict so the severity is auditable.
- **Official vs community, contested claims.** The cost mechanics are official (Creation Kit wiki);
  benchmark magnitudes ("~1000× faster cached") are single community measurements — directionally
  right, not exact. "States gate at zero engine cost" and "scripted cloaks are always bad" are
  community consensus with contested edges. The reference file flags each; carry the flag into the
  verdict rather than overstating.
- **Signatures:** confirm any function's parameters/flags via `papyrus-reference` before relying on
  them in a fix.
- **Packed scripts:** when only a `.pex` is shipped, extract with `housecarl_bsa_extract` and decompile
  before reviewing — you can only classify source you can read.
