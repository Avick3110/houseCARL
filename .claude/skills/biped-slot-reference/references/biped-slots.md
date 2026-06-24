# Skyrim biped slots — number ↔ bit ↔ FirstPersonFlags value

Skyrim armor/clothing "biped slots" are numbered **30–61**. The slots a record occupies live in
`BodyTemplate.FirstPersonFlags` (xEdit: "First Person Flags") — a 32-bit mask where **each bit is one
slot**. houseCARL reads that field, and the `housecarl_cross_plugin_query` `where ... has` predicate
bit-tests it.

## The mapping (exact)

- **Slot N occupies bit (N − 30).**
- **Its value is 2^(N − 30)** — the decimal number you pass to `has`.
- Example: slot 52 → bit 22 → `2^22` = **4194304** (`0x400000`).

Don't compute the power of two in your head — read it off the table. A wrong exponent queries the wrong
slot and looks like a clean result.

## Slot table (verified against Mutagen's `BipedObjectFlag`)

| Slot | Bit | Value (pass to `has`) | Hex | Mutagen name | Note |
|------|-----|------|-----|------|------|
| 30 | 0 | 1 | 0x1 | Head | vanilla — head/face |
| 31 | 1 | 2 | 0x2 | Hair | vanilla — hair |
| 32 | 2 | 4 | 0x4 | Body | vanilla — body / main torso armor |
| 33 | 3 | 8 | 0x8 | Hands | vanilla — hands |
| 34 | 4 | 16 | 0x10 | Forearms | vanilla — forearms / gauntlets |
| 35 | 5 | 32 | 0x20 | Amulet | vanilla — amulet |
| 36 | 6 | 64 | 0x40 | Ring | vanilla — ring |
| 37 | 7 | 128 | 0x80 | Feet | vanilla — feet / boots |
| 38 | 8 | 256 | 0x100 | Calves | vanilla — calves |
| 39 | 9 | 512 | 0x200 | Shield | vanilla — shield |
| 40 | 10 | 1024 | 0x400 | Tail | vanilla — tail |
| 41 | 11 | 2048 | 0x800 | LongHair | vanilla — long hair |
| 42 | 12 | 4096 | 0x1000 | Circlet | vanilla — circlet |
| 43 | 13 | 8192 | 0x2000 | Ears | vanilla — ears |
| 44 | 14 | 16384 | 0x4000 | — | modder |
| 45 | 15 | 32768 | 0x8000 | — | modder |
| 46 | 16 | 65536 | 0x10000 | — | modder |
| 47 | 17 | 131072 | 0x20000 | — | modder |
| 48 | 18 | 262144 | 0x40000 | — | modder |
| 49 | 19 | 524288 | 0x80000 | — | modder |
| 50 | 20 | 1048576 | 0x100000 | DecapitateHead | vanilla — decapitation (head) |
| 51 | 21 | 2097152 | 0x200000 | Decapitate | vanilla — decapitation (body) |
| 52 | 22 | 4194304 | 0x400000 | — | modder — **SOS** pelvis / the de-facto "pelvis" slot |
| 53 | 23 | 8388608 | 0x800000 | — | modder |
| 54 | 24 | 16777216 | 0x1000000 | — | modder |
| 55 | 25 | 33554432 | 0x2000000 | — | modder |
| 56 | 26 | 67108864 | 0x4000000 | — | modder |
| 57 | 27 | 134217728 | 0x8000000 | — | modder |
| 58 | 28 | 268435456 | 0x10000000 | — | modder |
| 59 | 29 | 536870912 | 0x20000000 | — | modder |
| 60 | 30 | 1073741824 | 0x40000000 | — | modder |
| 61 | 31 | 2147483648 | 0x80000000 | FX01 | vanilla — special-effect slot |

The **named** bits are not contiguous: 30–43 (bits 0–13), then 50/51 (`DecapitateHead`/`Decapitate`), then
61 (`FX01`). Everything else (44–49, 52–60) is an unnamed modder slot. This non-contiguity is the trap —
the names you'd guess sit at 44/45/46 actually live at 50/51/61.

## Naming gotcha — why some slots filter by name and some by number

`FirstPersonFlags` renders through .NET's `[Flags]` enum: when **every** set bit is a Mutagen-named slot
it shows the **name(s)** ("Body", "Body, Hands"); the moment **any** set bit is unnamed it shows the whole
value as a **raw decimal** ("8388608"). So in a `where`:

- **Named slots** (30–43, 50, 51, 61) — `has Body`, `has Feet`, `has DecapitateHead` work by name, and
  `= Body` works too.
- **Modder slots** (44–49, 52–60) — no name exists; use the **number** from the table: `has 4194304`
  (or `has 0x400000`).
- A combo of named slots renders "Body, Hands" — `has Body` still finds it. A combo that includes any
  modder slot renders as one number — `has <bit>` is the only reliable way to find it.
- Watch the two **named** slots inside the modder range: an item on 50/51 renders `DecapitateHead` /
  `Decapitate`, not a number — don't assume everything 44–60 is numeric.

## Community slot conventions (vary by mod — verify, don't assume)

Modder slots carry **no engine-enforced meaning** — a mod may use any free slot however it likes. A couple
of conventions are widespread enough to name, but treat them as hints:

- **52 = SOS / pelvis** — Schlongs of Skyrim and most pelvis-aware bodies/armors. The strongest convention.
- **A "back" slot** (capes, cloaks, backpacks, wings, quivers) — commonly somewhere in **46–48**, but
  mod-specific; don't assume one number.
- **44–49, 53–60** — general modder slots (masks, shoulders, scarves, leg add-ons, fur clips, …); usage
  is whatever the mod chose.

To know for **certain** which slot an item uses, don't rely on the convention — read the record's
`BodyTemplate.FirstPersonFlags` (houseCARL returns it), or query the bit with `has` and inspect the hits.
The bit is ground truth; the convention is a guess.
