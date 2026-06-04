# Object Modification Patcher — OMOD  ⚠️ DOCUMENTATION GAP

`iEnableObjectModificationPatching` (enabled by default in `SkyPatcher.ini` 6.4.1)

**There is no verified grammar for this patcher.** It is enabled in the shipped DLL but:

- has **no documentation article** in the SkyPatcher Articles tab, and
- has **no source file** in SkyPatcher's public repo (`Zzyxz/SkyPatcher`) — the public source
  is behind the released DLL.

This gap is **documented, not filled** — no
grammar is invented here. Do **not** guess OMOD filters/operations.

## If a user needs OMOD patching

Surface the gap honestly, then offer leads rather than a fabricated answer:

- **Sibling source.** The author's Fallout 4 tool `Zzyxz/RobCo-Patcher` carries the same
  `iEnableObjectModificationPatching` in its source. OMOD grammar is derivable from there, but it
  is **FO4-flavored** and not verified against SkyPatcher's Skyrim port — treat anything derived
  that way as a candidate to verify in-game, not as confirmed grammar.
- **Subfolder.** Not present in the shipped scaffold; by SkyPatcher's naming convention it would
  most likely be `SkyPatcher/objectModification/`, but this is **unconfirmed**.
- **Empirical.** A real-world SkyPatcher OMOD INI (from the SkyPatcher Discord or a mod that uses
  it) would be the authoritative source for a future fill.

When that grammar is confirmed, this file can be replaced with a normal record reference and an
`index.jsonl` entry added.
