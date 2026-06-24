---
name: biped-slot-reference
description: >-
  Find armor, clothing, or any equippable record that sits on a given Skyrim biped slot via houseCARL — translate a slot number, vanilla name, or community label into its `BodyTemplate.FirstPersonFlags` bit value and filter the load order with `housecarl_cross_plugin_query` `where ... has`, matching multi-slot pieces an exact-match misses. Use when the user wants to find or list every armor on a biped slot, tag or patch items by equip slot (slot 52 / SOS / pelvis, back or cape slots, a mask or fur-clip slot), audit biped-slot conflicts between mods, find what occupies slot 32 / 52 / a modder slot, or asks which slot a piece uses or what slot number maps to which FirstPersonFlags value. This finds and filters records BY slot — distributing keywords to those items is `kid-authoring`, editing a record's own fields is `skypatcher-authoring`. Load this before composing any "armor on slot X" query — the slot↔bit mapping is non-obvious (slot N = bit N−30; named vanilla slots render as text while modder slots render as raw numbers), so a hand-guessed mask silently filters the wrong slot.
---

# Biped Slot Reference

## Overview

Skyrim armor and clothing occupy **biped slots** numbered 30–61, stored as a bitmask in a record's
`BodyTemplate.FirstPersonFlags`. This skill turns a slot — given as a number (52), a vanilla name (Body),
or a community label (SOS, pelvis, a back/cape slot) — into the right `housecarl_cross_plugin_query`
filter, so "find every armor on slot X" is one query instead of a guess. It is a reference-lookup skill
with a short query procedure on top (the same look-it-up-never-guess discipline as `mutagen-reference`).
The full slot↔bit↔value mapping lives in `references/biped-slots.md`.

The mapping is easy to get wrong by hand, which is why this skill exists: slot N is bit (N − 30) with
value 2^(N − 30), and the field renders inconsistently (named slots as text, modder slots as raw numbers).
A hand-guessed mask silently filters the wrong slot and looks like a clean answer.

## First step — open the slot table

Read `references/biped-slots.md` for the verified slot 30–61 table (number, bit, the value to pass, hex,
Mutagen name, note) plus the naming gotcha and community conventions. Pull the value for the slot your
task names — don't compute 2^(N−30) in your head.

## The query — `has`, not `=`

Use the `where ... has` predicate (a bitwise set-test) so a multi-slot item still matches the one slot you
asked for:

```
housecarl_cross_plugin_query(
    type="Armor",
    where=["BodyTemplate.FirstPersonFlags has 4194304"])   # slot 52
```

`has <value>` is true when that bit is set **regardless of other slots** — so a body+slot-52 piece
matches. Reach for it whenever an item might occupy more than one slot (most armor does).

- **Modder slots (44–49, 52–60):** pass the **number** from the table — `has 4194304` or `has 0x400000`.
- **Named slots (30–43, 50, 51, 61):** the flag name works too — `has Body`, `has Feet`,
  `has DecapitateHead`.
- **Exact, single-slot only:** `= 4194304` matches a record on slot 52 *and nothing else* — use it only
  when you specifically want single-slot pieces. It silently skips every combo, which is the trap that
  makes a slot look empty when it isn't.

`has` also works on a plain integer field, and a numeric range now works on the flags field too
(`FirstPersonFlags >= 65536` no longer errors) — but for "is this slot occupied", `has` is the tool.

## Why a query can mislead — the rendering split

`FirstPersonFlags` is a `[Flags]` enum: it shows slot **names** when every set bit is named, but a raw
**decimal** the moment any set bit is a modder slot. Two consequences worth holding:

- `= 16` once 0-matched the slot-34 item that renders "Forearms" — flags `=` now compares by bit, so
  `= 16` and `= Forearms` both find it. Prefer `has` regardless, so combos aren't excluded.
- Two **named** slots hide in the modder range: 50 `DecapitateHead` and 51 `Decapitate`. An item there
  renders as that name, not a number — don't assume everything 44–60 is numeric.

## Be sure, don't assume (Q3)

Community slot conventions (SOS = 52, a back slot ≈ 46–48, …) are **hints, not guarantees** — modder slots
carry no engine-enforced meaning, so a mod can use any free slot. When it matters which slot an item really
uses, read its `BodyTemplate.FirstPersonFlags` (houseCARL returns it) or query the bit with `has` and
inspect the hits. The bit is ground truth; the convention is a guess. If you can't confirm a slot, say so
rather than asserting from the convention.

## Scope — finding records BY slot

This skill **locates and filters** records by their biped slot. It does not distribute or edit:

- distribute a **keyword** to the items you found → `kid-authoring`.
- distribute a spell / perk / item to **NPCs** → `spid-authoring`.
- change a record's **own fields** (including its FirstPersonFlags) → `skypatcher-authoring`, or a
  houseCARL record write (a new patch plugin by default, or in place into an existing plugin via the
  in-place lane).
