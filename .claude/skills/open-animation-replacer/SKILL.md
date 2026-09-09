---
name: open-animation-replacer
description: >-
  Authors and interprets Open Animation Replacer (OAR) configs against the OAR 3.0.0 schema — config.json / user.json conditions, submod priorities, DAR _conditions.txt conversion. Load before composing or judging any condition, priority or DAR folder — OAR picks winners by priority, not load order, and a wrong token no-ops. Use when gating animations by weapon, keyword, perk, or race, editing or auditing an OAR config, or asking why an animation isn't playing or which submod wins. Not for .hkx or Nemesis/FNIS behaviour files, and not for record edits — those are SkyPatcher or SPID INIs.
compatibility: Requires the houseCARL MCP server and a configured Mod Organizer 2 instance.
---

# Open Animation Replacer

## What this skill does

Open Animation Replacer (OAR) replaces animations **at runtime, by condition**. A submod is one
`config.json` carrying a `priority` integer, a `conditions` array, and the `.hkx` files it swaps in.
OAR also reads Dynamic Animation Replacer's legacy folders directly, so a load order mixes both and
they compete in one priority space. Authoring means writing `config.json` / `user.json`, condition
sets, priorities, and converting a legacy DAR `_conditions.txt`; interpreting means answering "what
does this do", "which submod wins", or "why isn't this animation playing".

**Not this skill.** `.hkx` animation assets, and Nemesis / FNIS / Pandora behaviour files — OAR is
runtime and needs no behaviour regeneration for its own replacements. A change that belongs in a
record goes to `housecarl:skypatcher-authoring` (item, NPC and leveled-list properties) or
`housecarl:spid-authoring` (spells, perks, items and keywords onto NPCs).

The full schema, the ~120-condition roster and the DAR grammar are in
`references/oar-config-reference.md`; pull a value from there rather than from memory or a web
search — a web search surfaces the vanilla `GetEquippedItemType` enum, which OAR's `IsEquippedType`
deliberately differs from.

## Where the lookup tables are

One file ships beside this one: `references/oar-config-reference.md` — exhaustive, source-verified,
opening with its own table of contents. Load the one section you need.

| Need | Section |
|---|---|
| Folder layout and the `<project>` names | §1 |
| Mod-level and submod-level `config.json` schema | §2, §3 |
| Condition object schema and the value-component shapes | §4 |
| The authoritative `IsEquippedType` enum | §5 |
| The ~120 built-in condition roster | §6 |
| Which conditions come from which addon DLL | §7 |
| The DAR grammar, both folder forms, the function mapping, the `AND`/`OR` binding note | §8 |
| `user.json` shadow semantics | §9 |
| Priority and winner resolution | §10 |
| The global `OpenAnimationReplacer.ini` | §11 |
| FormID form and the embedded-null gotcha | §12 |

Every step below names the section it needs, so one section can be read on its own.

## First step — orient before you touch a config

1. **Locate the submod and see who wins the file.** Run `housecarl_asset_status` with
   `under=["meshes/actors/character/animations/OpenAnimationReplacer/**/config.json",
   "meshes/actors/character/animations/DynamicAnimationReplacer/**/_conditions.txt"]` — legacy DAR's
   marker file is `_conditions.txt`, not `config.json`. Name a creature project explicitly when one
   is in play, `canine`, `draugr` or `dragon` in place of `character`; do **not** write `*` in the
   project segment, because a selector is enumerated from the literal directory in front of its first
   wildcard, so `meshes/actors/*/…` walks every facegen, body and armor mesh under `meshes/actors`
   before a row renders, and `limit=`/`offset=` do not bound that — they window the render. A DAR
   Form B `<Plugin.esp>/<FormID>/` folder carries no marker file at all, so sweep
   `.../DynamicAnimationReplacer/**/*.hkx` too when an actor-base override is in play. Add a third
   selector for the base-clip layer, `meshes/actors/character/animations/<original.hkx>` — a mod that
   replaces the original file outright competes for the same frames and neither replacer selector
   sees it. All three selectors are rooted at `character/animations/`, so the sweep covers the
   third-person graph only: the first-person graph lives in the sibling tree
   `meshes/actors/character/_1stperson/animations/`, is a different set of frames, and does not
   compete with anything found here. When the job is about first-person animations, root the same
   three selectors at that path instead — do not add it to a third-person sweep, where every hit
   would be a false competitor.
   The call
   resolves every file the VFS provides beneath each selector, names which mod wins each one, and
   reports loudly when an archive could not be read; page a large sweep with `limit=` and `offset=`,
   cap it with `max_chars=`.
   Without the houseCARL server, fall back to Glob over those paths — and say you did, because the
   fallback cannot name the VFS winner. Folder layout and the `<project>` names:
   `references/oar-config-reference.md` §1.
2. **Read what is already there.** The submod `config.json`, any `user.json` beside it (`user.json`
   wins — it fully shadows the `config.json`), and the parent `<ModName>/config.json` for its
   `conditionPresets`. `user.json` shadow semantics: `references/oar-config-reference.md` §9.
3. **Note required addons.** A condition name absent from the built-in roster comes from an addon DLL
   (Math / RaySense / IED / Detection / Dialogue). Confirm it is installed under `…/SKSE/Plugins/`,
   or the line is a dead no-op. Which conditions come from which addon DLL:
   `references/oar-config-reference.md` §7.
4. **Resolve the forms a condition names.** A perk, keyword, race, faction or magic effect needs
   `{ "pluginName": …, "formID": … }` or `{ "editorID": … }`, where `formID` is the record's
   **local** id in its defining plugin. Read it with `housecarl_records`:
   `formids=["XXXXXX:Plugin.esp"]` with `project={"form":"identity"}` when you have the FormID (the
   runtime spelling a console, Papyrus or crash log prints is accepted too, and the response names
   the plugin it resolved to); `types=["PERK"]` (or `KYWD`, `RACE`, `FACT`, `MGEF`) with
   `where=["editorid startswith REQ_"]` when you only know the EditorID — that is a scan, so leave
   `project=` off and read the default summary rows, which already carry each match's identity; the
   identity form labels a `formids=` list and is refused on a scan, and a body scan must be bounded
   by `types=` or `plugins=`. Without the server, read the form from the mod's own plugin or its
   Nexus page, and say you did. FormID form and the embedded-null gotcha:
   `references/oar-config-reference.md` §12.

## The mental model — how OAR picks a winner

Internalize this before authoring; most "it doesn't work" reports trace back to it.

- Winners are decided **per original animation**, by sorting every targeting submod by `priority`
  **descending**. **Plugin/ESP load order is ignored entirely** — it is never the lever.
- A legacy DAR `_CustomConditions/<n>/` folder's **name is its priority**, in the **same global
  priority space** as native OAR integers. `2000030002` and `9007010` are compared directly.
- At runtime OAR walks that sorted list and takes the **first submod whose `conditions` pass**. If
  none pass, the base-game animation plays.
- **Higher priority wins.** Equal priorities are ambiguous — there is no tiebreak — so keep them
  unique. Authors spread large integers (`9007010`, `83030317`) to slot between other mods.

To make animation X beat animation Y, X's submod needs a **higher priority** *and* conditions that
pass in the situation you care about. Priority and winner resolution:
`references/oar-config-reference.md` §10.

## Authoring workflow

1. **Pick the target and the project.** The submod's `.hkx` files must mirror the original
   animation's path; that path match is what binds the submod to a base animation. `<project>` is
   `character` for humanoids. A submod with no `.hkx` is using `overrideAnimationsFolder` or is
   conditions-only — that is normal.
2. **Choose a unique priority.** Higher beats lower. To override an existing mod, read its submod
   priority and go above it. Include legacy DAR folder names in the comparison — a Form A folder's
   name is its priority, a Form B `<Plugin.esp>/<FormID>/` folder's priority is 0
   (`references/oar-config-reference.md` §8), so anything positive beats it. Go just above the
   highest submod that actually **wins the frames** in your situation — the one whose conditions
   pass there — not above the highest integer in the load order. Parking near int32 max also
   outranks the situational overlays that live up there (Look Around, RaySense and the like), which
   a conversion has no reason to suppress.
3. **Build the condition set.** Each entry is
   `{ "condition": "<Name>", "requiredVersion": "1.0.0.0", …params }`; add `"negated": true` to
   invert. Combine with `AND` / `OR` / `XOR`. **The submod's top-level array is lowercase
   `conditions`; the child array inside `AND`/`OR`/`XOR`/`PRESET`/`PLAYER`/`TARGET`/`MOUNT` is
   capital-C `Conditions`.** Condition object schema and the value-component shapes (Form, Keyword,
   Numeric, Bool, Text, Comparison, NiPoint3, Multi): `references/oar-config-reference.md` §4; the
   built-in roster is §6. The authoritative `IsEquippedType` enum is §5 — battleaxe 6, warhammer 10;
   do not use the vanilla enum.
4. **Pick the file — `config.json` or `user.json`.** Shipping or owning the mod: write
   `config.json`. Overriding someone else's mod without editing it: write a `user.json` beside their
   `config.json`. It is a **full-document shadow, not a field merge**, so it must hold the complete
   config you want. In a modlist, keep every `user.json` override in one dedicated MO2 mod that loads
   after the originals — USVFS overlays them there and they win, leaving the originals untouched.
   `user.json` shadow semantics: `references/oar-config-reference.md` §9.
5. **Add variants, blend or loop behaviour only if needed.** `replacementAnimDatas` drives random
   variants (`weight`, `playOnce`, `variantMode`); `interruptible`, `replaceOnLoop` (default true)
   and the `blendTime*` fields tune transitions. Prefer `replaceOnLoop` over the deprecated
   `keepRandomResultsOnLoop`.
6. **State any addon dependency you took on.** `MathStatement` needs the Math plugin, `IED_*` needs
   IED Conditions, raycast conditions need RaySense.

## Reading / interpreting an existing config

The inverse job. Answer precisely, and say "I can't tell without X" rather than guess.

- **"What does this submod do?"** Translate each condition using the reference — resolve enum values
  and the `editorID` / FormID forms — then state the priority and what it competes against.
- **"Which submod wins?"** Compare the priorities of every submod targeting that animation, legacy
  DAR folders included; the highest one whose conditions pass wins. If you cannot see every competing
  submod, say so and name what you would need to see.
- **"Why isn't it playing?"** Walk the list: is a higher-priority submod winning; do the conditions
  actually pass in that situation (weapon hand, enum value, missing perk); is a required addon DLL
  missing so the condition reads INVALID; does the `.hkx` path mirror the original; is a `user.json`
  shadowing the `config.json` you are reading; is `disabled` set?

## Common mistakes, and the rule that replaces each

- **Write the submod's top-level array as lowercase `conditions` and every nested child array as
  capital-C `Conditions`.** Swapping them produces a file that parses cleanly, loads cleanly, and
  silently evaluates an empty child set — the error you never find by inspection.
- **Take `IsEquippedType` values from the reference, never from a web search.** Skyrim's vanilla
  `GetEquippedItemType` is what a search surfaces and OAR deliberately differs from it. The
  authoritative table is `references/oar-config-reference.md` §5.
- **Test battleaxe and warhammer as two separate values.** Both are engine `kTwoHandAxe`; OAR splits
  them, so a moveset meant for both needs the two values OR-ed.
- **Change the `priority` integer to change a winner.** Editing MO2 or plugin order does nothing —
  OAR reads only `priority`.
- **Write a complete `user.json`.** It fully shadows `config.json`, so anything omitted is dropped,
  not inherited. Let the in-game editor generate one if you want a guaranteed-complete starting point.
- **Confirm an addon condition's DLL is installed before you use its condition.** Without it the line
  degrades to INVALID and never fires.
- **Treat a submod with no `.hkx` as normal.** It is usually `overrideAnimationsFolder` or a
  conditions-only host, not a broken install.

## Verification

Check offline first, then in game.

1. **Enumerate the competition.** Re-run the `housecarl_asset_status` `under=` sweep over the OAR and
   DAR trees: every competing `config.json` and the mod that wins each file. That is both the winner
   input and the check that you edited the copy the game actually loads.
2. **List the priorities.** For the original animation you target, list every submod that targets it
   with its priority, legacy DAR folder names included; confirm yours sits where you intended and
   that no two are equal.
3. **Splice yours in and recompute the winner.** Put your submod into that competitor list at its
   new priority, re-sort, and walk the list again for the situation you care about — reading each
   competitor's conditions to see which of them would pass there. Asserting a priority is not the
   check; recomputing the winner table with your submod in it is. Name what you suppressed as well
   as what you beat.
4. **Re-read your own file.** Top-level array `conditions`, every nested array `Conditions`, every
   `formID` a local id in the plugin its `pluginName` names.
5. **Enforce in game.** OAR parses configs on game load and the in-game editor reloads a mod live.
   The editor's **Detected Problems** panel is the enforcement: it flags INVALID conditions (missing
   addon, unresolvable form) and duplicate priorities. Everything above is guidance; this panel
   decides.

Loop steps 1-5 until Detected Problems is empty and the priority list shows your submod winning in
the situation you care about. That pair is the stop condition.

## Worked example — overriding a mod's conditions via `user.json`

A mod's archery moveset should apply only after the player earns a specific perk, without editing the
mod. The original `…/Bow Rapid Combo V3/Base/config.json` carries `name`, `priority` `9901000` and
`keepRandomResultsOnLoop`, with `IsActorBase player` plus `IsEquippedType 7` as its conditions. Put a
`user.json` at the matching submod path in the dedicated overrides mod, holding **every key the
original carries** plus the one new condition — a shadow drops what it omits, so read the original
first and carry across whatever it has, `overrideAnimationsFolder` and `replacementAnimDatas`
included, or the shadowed submod wins its slot and replaces nothing:

```json
{ "name": "Base",
  "priority": 9901000,
  "keepRandomResultsOnLoop": true,
  "conditions": [
    { "condition": "IsActorBase", "requiredVersion": "1.0.0.0",
      "Actor base": { "pluginName": "Skyrim.esm", "formID": "7" } },
    { "condition": "IsEquippedType", "requiredVersion": "1.0.0.0",
      "Type": { "value": 7.0 }, "Left hand": false },
    { "condition": "HasPerk", "requiredVersion": "1.0.0.0",
      "Perk": { "pluginName": "<the perk-adding mod>.esp", "formID": "<local hex>" } } ] }
```

Resolve the `HasPerk` form with `housecarl_records` as in step 4 above. OAR now uses the `user.json`
for that submod and the moveset applies only once the player has the perk; the original mod is
untouched, so it can be updated without losing the override.

## Worked example — converting a DAR `_conditions.txt`

Legacy folder `…/DynamicAnimationReplacer/_CustomConditions/777000/` holds `2hm_idle.hkx` and a
`_conditions.txt`:

```
IsActorBase("0Kaidan.esp" | 0x00002f9a) AND
IsEquippedRightType(5) AND
Random(0.2) AND
NOT IsSneaking() AND
NOT IsInCombat()
```

The folder name is the priority. Map each function to its OAR condition — the full table is
`references/oar-config-reference.md` §8: `IsEquippedRight` → `IsEquipped` with `"Left hand": false`,
`IsEquippedLeft` → the same with `true`; `IsEquippedRightType` / `IsEquippedLeftType` →
`IsEquippedType` with `Type` and the hand flag; `IsEquippedRightHasKeyword` /
`IsEquippedLeftHasKeyword` → `IsEquippedHasKeyword` with the hand flag; a Form B
`<Plugin.esp>/<FormID>/` folder pair → the auto-synthesized `IsActorBase`. Type `5` is Greatsword in
§5's table — DAR's type numbers are the same numbering, `n → n`, so never translate them. `0x00002f9a`
becomes the local hex `"2F9A"`: a DAR argument is already the local id in the plugin named beside it,
so drop the `0x` and the leading zeros and nothing else.

DAR has no parenthesis grouping, but the binding is settled: **`OR` binds tighter than `AND`**, so a
chain is an `AND` of `OR`-groups, and an `OR`-group is a run of lines ending in `OR` plus the next
line that yields a condition — a blank line or a `;` comment is skipped without closing the group
(§8, from OAR's `Parsing.cpp`). Read the binding before you write anything: here the first four lines
end in `AND` and the last ends in nothing, so there is no `OR` group and the set is flat.

Where a chain does carry an `OR`, the group is nested and reaches one line further than it looks.
This three-line chain

```
NOT IsInCombat() AND
IsEquippedRight("Skyrim.esm" | 0x0001397E) OR
IsEquippedRight("Skyrim.esm" | 0x00013980)
```

is an `AND` set of two members, the second of which is an `OR` group holding both `IsEquipped`
conditions — the run of `OR`-terminated lines plus the next line that yields a condition:

```json
"conditions": [
  { "condition": "IsInCombat", "requiredVersion": "1.0.0.0", "negated": true },
  { "condition": "OR", "requiredVersion": "1.0.0.0", "Conditions": [
      { "condition": "IsEquipped", "requiredVersion": "1.0.0.0", "Left hand": false,
        "Form": { "pluginName": "Skyrim.esm", "formID": "1397E" } },
      { "condition": "IsEquipped", "requiredVersion": "1.0.0.0", "Left hand": false,
        "Form": { "pluginName": "Skyrim.esm", "formID": "13980" } } ] } ]
```

The group reaches one line past the last `OR`, which is the easy thing to get wrong: in the 777000
chain above, had lines 2 and 3 ended in `OR` instead of `AND`, the group would run to line 4 as well
and hold **three** members — `IsEquippedType`, `Random` and `NOT IsSneaking` — not the two that ended
in `OR`. Reading such a chain flat left-to-right inverts what it gates on.

`Random` takes a state block and a comparison rather than a bare number. Its six keys and its
`2.3.0.0` version floor are in the reference's DAR mapping table, read off shipped configs — copy
this shape, and check the installed OAR is at least 2.3.0.0 or the condition is flagged invalid and
the submod never wins:

```json
{ "name": "Kaidan greatsword idle (from DAR 777000)",
  "description": "Converted from DAR _CustomConditions/777000. Kaidan, greatsword in the right hand, out of combat, not sneaking, 20% of the time. No line ends in OR, so this is one flat AND set.",
  "priority": 777000,
  "conditions": [
    { "condition": "IsActorBase", "requiredVersion": "1.0.0.0",
      "Actor base": { "pluginName": "0Kaidan.esp", "formID": "2F9A" } },
    { "condition": "IsEquippedType", "requiredVersion": "1.0.0.0",
      "Type": { "value": 5.0 }, "Left hand": false },
    { "condition": "Random", "requiredVersion": "2.3.0.0",
      "State": { "scope": "Local", "shouldResetOnLoopOrEcho": false },
      "Minimum random value": { "value": 0.0 },
      "Maximum random value": { "value": 1.0 },
      "Comparison": "<", "Numeric value": { "value": 0.2 } },
    { "condition": "IsSneaking", "requiredVersion": "1.0.0.0", "negated": true },
    { "condition": "IsInCombat", "requiredVersion": "1.0.0.0", "negated": true } ] }
```

Keeping the priority at the folder name preserves the conversion's standing exactly. Raise it only
to beat a submod that currently wins these frames, and then by verification step 3 — recompute, do
not assume. Here the competitor that decides it is a greatsword moveset with **no** actor gate, which
passes on Kaidan; the boss and player-gated submods in the same band do not, whatever their priority.

The config is only half the folder. Carry the legacy folder's `.hkx` files across to the new submod
at their mirrored `<project>/<original.hkx>` paths, or point `overrideAnimationsFolder` at the legacy
folder — a config alone parses, loads and wins its priority slot while replacing nothing, and that
failure shows up in game, not in Detected Problems.

## Notes and provenance

- **DAR back-compat.** OAR converts both legacy folder forms into in-memory submods competing in the
  same priority space; leaving a mod legacy is fine.
- **The in-game editor is the live source of truth** for which conditions a given install has, and it
  writes valid `config.json` / `user.json` for you.
- **houseCARL cannot introspect OAR configs** — it reads records, not animation files. Read the
  configs directly; houseCARL does the two things above, the locate sweep and the form resolution.
- **This skill is not inherited.** A subagent asked to do OAR work needs it loaded in its own
  context — name it in the task that spawns the subagent, or the subagent works without it.
- **Provenance.** OAR 3.0.0, the DLL shipped in Open Animation Replacer (Nexus 92109); the reference
  is generated from `ersh1/OpenAnimationReplacer`, branch `main`. To refresh: re-read that branch's
  `src/Parsing.cpp`, `src/Conditions.h`, `src/Conditions.cpp`, `src/BaseConditions.h` and
  `src/OpenAnimationReplacer.cpp`, update
  `references/oar-config-reference.md` section by section, and change its header's version line.
