---
name: oar-authoring
description: >-
  Author or interpret Open Animation Replacer (OAR) configs — the runtime, condition-driven
  animation system (config.json / user.json) that supersedes Dynamic Animation Replacer (DAR) and
  still reads DAR's legacy folders — using the bundled OAR reference, not invented syntax. Use when
  the user wants to write or fix a config.json or user.json, add or edit animation conditions, gate a
  moveset, idle, dodge, or attack animation by weapon type, keyword, perk, race, or magic
  effect, set a submod priority, override a mod's conditions without editing it, read or convert a DAR
  _conditions.txt, or use an OAR addon condition (Math, RaySense, IED, Detection, Dialogue) — or asks
  why an animation isn't playing, which submod wins, or what a config does. This authors animation
  CONFIGS — distributing forms to NPCs is SPID, keywords to items is KID, editing record fields is
  SkyPatcher. Load this before writing or reading any OAR config: OAR ignores plugin load order and
  picks winners purely by priority, the grammar is non-obvious (lowercase `conditions` vs capital
  `Conditions`, the IsEquippedType enum), and a wrong token silently no-ops at game load.
---

# OAR Authoring

## What this skill does

Open Animation Replacer (OAR) replaces Skyrim animations **at runtime based on conditions**. An
animation mod ships one or more *replacer mods*, each holding *submods*; every submod is one
`config.json` with a `priority` integer, a set of `conditions`, and the `.hkx` files it can swap in.
When a base animation plays on an actor, OAR finds the highest-priority submod whose conditions are
true for that actor and plays its animation instead. OAR is the successor to Dynamic Animation
Replacer (DAR) and **reads DAR's legacy folders directly**, so a load order mixes both.

Authoring here means: writing or editing `config.json`/`user.json`, building condition sets, setting
priorities, overriding an existing mod's conditions via `user.json`, and reading legacy DAR
`_conditions.txt`. Interpreting means answering "what does this do", "which submod wins", or "why
isn't this animation playing".

The exhaustive lookup tables — full config schema, the ~120 built-in conditions, the authoritative
`IsEquippedType` enum, value-component shapes, the DAR grammar — live in
`references/oar-config-reference.md`. Pull a value from there; do not reconstruct it from memory or a
web search — a web search usually surfaces the vanilla `GetEquippedItemType` enum, which OAR's
`IsEquippedType` deliberately differs from.

## First step — orient before you touch a config

OAR is file-based, not record-based, so houseCARL's record tools don't see it. Work the files
directly (Read / Glob / Grep / Write), and reach for houseCARL only to **resolve the forms** a
condition needs.

1. **Locate the submod.** OAR configs live at
   `…/meshes/actors/<project>/animations/OpenAnimationReplacer/<ModName>/<SubModName>/config.json`.
   Legacy DAR lives under `…/animations/DynamicAnimationReplacer/…`. Glob for both.
2. **Read what's already there.** Read the submod `config.json` (and any `user.json` beside it —
   `user.json` wins). Read the parent `<ModName>/config.json` for `conditionPresets`.
3. **Note required addons.** If a condition name isn't a built-in (check the roster in the
   reference), it comes from an addon DLL (Math/RaySense/IED/Detection/Dialogue). Confirm that DLL is
   installed, or the condition is dead.
4. **Resolve forms with houseCARL.** A condition that references a perk, keyword, race, faction, or
   magic effect needs `{ "pluginName": …, "formID": <local hex> }` or `{ "editorID": … }`. Use
   `housecarl_read_record` / `housecarl_cross_plugin_query` to get the defining plugin + local
   FormID (strip the load-order byte) or the EditorID.

## The mental model — how OAR picks a winner

Internalize this before authoring; most "it doesn't work" reports trace back to it:

- Winners are decided **per original animation**, by sorting every targeting submod by `priority`
  **descending**. **Plugin/ESP load order is ignored entirely.**
- At runtime OAR walks that sorted list and takes the **first submod whose `conditions` are true**.
  If none pass, the base-game animation plays.
- **Higher priority wins.** Equal priorities are ambiguous (no tiebreak) — keep them unique. Authors
  spread large integers (`9007010`, `83030317`) to slot cleanly between other mods.

So to make animation X beat animation Y, X's submod needs a **higher priority** *and* conditions
that pass in the situation you care about. Load order is never the lever.

## Authoring workflow

1. **Pick the target + project.** The submod's `.hkx` files must mirror the original animation's
   path; that path-match is what binds the submod to a base animation. `<project>` is `character`
   for humanoids. A submod with no `.hkx` is using `overrideAnimationsFolder` or is conditions-only
   — that's normal.
2. **Choose a unique priority.** Higher beats lower. To override an existing mod, read its submod
   priority and go above it.
3. **Build the condition set.** Each entry is `{ "condition": "<Name>", "requiredVersion": "1.0.0.0",
   …params }`. Add `"negated": true` to invert. Combine with `AND` / `OR` / `XOR` — and note the
   nested child array is capital-C **`Conditions`**, while the submod's top-level array is lowercase
   **`conditions`**. Get param shapes (Form, Keyword, Numeric, Bool, Comparison, Multi) from the
   reference. For weapon gating, use `IsEquippedType` with the authoritative enum (battleaxe = 6,
   warhammer = 10 — they differ).
4. **Pick the file: `config.json` vs `user.json`.** If you're shipping/owning the mod, write
   `config.json`. If you're **overriding someone else's mod without editing it**, write a
   `user.json` beside their `config.json` — OAR uses `user.json` instead of `config.json` for that
   submod (a full-document shadow, not a field merge, so include the *complete* config you want). In
   a modlist, keep all `user.json` overrides in one dedicated MO2 mod that loads after the originals;
   USVFS overlays them and they win, leaving originals untouched. (A modlist typically keeps these
   in one dedicated overrides mod that loads after the originals.)
5. **Add variants / blend / loop behavior only if needed.** `replacementAnimDatas` drives random
   variants (`weight`, `playOnce`, `variantMode`); `interruptible`, `replaceOnLoop` (default true),
   and the `blendTime*` fields tune transitions. Prefer `replaceOnLoop` over the deprecated
   `keepRandomResultsOnLoop`.
6. **If you used an addon condition, state the dependency.** `MathStatement` needs the Math plugin;
   `IED_*` needs IED Conditions; raycast conditions need RaySense; etc. Without the DLL the line
   becomes an INVALID no-op.

## Reading / interpreting an existing config

The inverse job — answer precisely, and say "I can't tell without X" rather than guess:

- **"What does this submod do?"** Translate each condition using the reference (resolve enum values
  and `editorID`/FormID forms), then state the priority and what it competes against.
- **"Which submod wins?"** Compare priorities of every submod targeting that animation; the highest
  with passing conditions wins. If you can't see all competing mods, say so.
- **"Why isn't it playing?"** Walk the checklist: is a higher-priority submod winning? Do the
  conditions actually pass in that situation (weapon hand, enum value, missing perk)? Is a required
  addon missing (condition shows INVALID)? Does the `.hkx` path mirror the original? Is `user.json`
  shadowing the `config.json` you're reading? Is `disabled` set?

## Common mistakes

- **Lowercase `conditions` vs capital `Conditions`.** Submod top level is `conditions`; the child
  array inside `AND`/`OR`/`XOR`/`PRESET`/`PLAYER`/`TARGET`/`MOUNT` is `Conditions`. Swapping them
  yields an empty child set that silently passes/fails wrong.
- **Confusing OAR's `IsEquippedType` with the vanilla equipped-type enum.** Skyrim's vanilla
  `GetEquippedItemType` (what most web searches surface) says 9=spell / 10=shield / 11=torch; OAR
  deliberately differs — 9=crossbow, 10=warhammer, 11=shield, spells=12–16, torch=18. Use the
  reference table, not the vanilla enum.
- **Battleaxe vs warhammer.** Both are engine `kTwoHandAxe`; OAR splits by keyword (6 vs 10). A
  moveset meant for both must test `6` OR `10`.
- **Editing load order to fix a winner.** Pointless — OAR only reads `priority`. Change the integer.
- **Partial `user.json`.** It fully shadows `config.json`; a half-written `user.json` drops whatever
  it omits. Write the complete config (or let the in-game editor generate it).
- **Using an addon condition without its DLL.** The line degrades to INVALID and never fires.
- **Assuming a no-`.hkx` submod is broken.** It's usually `overrideAnimationsFolder` or
  conditions-only.

## Verification

- Re-scan: OAR parses configs on game load (and the in-game editor can reload a mod live).
- The editor's **Detected Problems** panel flags INVALID conditions (missing addon, bad form) and
  duplicate priorities — the fastest correctness check.
- Confirm the winner by listing priorities of all submods that target the same original animation.
- Confirm any addon condition's DLL is present under `…/SKSE/Plugins/`.

## Real example — overriding a mod's conditions via `user.json`

You want a mod's archery moveset to apply only after the player earns a specific perk, without
editing the mod. The original `…/Bow Rapid Combo V3/Base/config.json` is `[IsActorBase player,
IsEquippedType 7 (bow)]` at priority `9901000`. Put a `user.json` at the matching submod path — in a
separate, dedicated overrides mod that loads after the original — copying that and **adds one
condition**:

```json
{ "priority": 9901000,
  "conditions": [
    { "condition": "IsActorBase", "requiredVersion": "1.0.0.0",
      "Actor base": { "pluginName": "Skyrim.esm", "formID": "7" } },
    { "condition": "IsEquippedType", "requiredVersion": "1.0.0.0",
      "Type": { "value": 7.0 }, "Left hand": false },
    { "condition": "HasPerk", "requiredVersion": "1.0.0.0",
      "Perk": { "pluginName": "<the perk-adding mod>.esp", "formID": "<local hex>" } } ] }
```

USVFS overlays the `user.json` beside the original `config.json`; OAR uses the `user.json` and the
moveset now only applies once the player has that perk. Resolve the `HasPerk` form (defining plugin +
local FormID) with houseCARL.

## Notes

- **DAR back-compat:** OAR converts legacy `_CustomConditions/<priority>/` (with `_conditions.txt`)
  and `<Plugin.esp>/<FormID>/` actor folders into in-memory submods that compete in the same
  priority space. The DAR grammar and the auto-synthesized `IsActorBase` form are in the reference.
- **The in-game editor is the live source of truth** for which conditions exist in a given install
  (core + whatever addons are present) and writes valid `config.json`/`user.json` for you.
- **houseCARL can't introspect OAR configs** (it reads ESP records, not animation files) — read the
  files directly; use houseCARL only to resolve the forms/keywords/perks a condition references.
