# Reference Patcher — REFR

`SkyPatcher/reference/` · `iEnableReferencePatching` · primary filter `filterByRefs`
Shared mechanics → `grammar-core.md`.

Patches placed object references (REFR) — the actual instances in the world. REFR patching is
governed by the global `iUpdateRefs` setting; the author flagged this feature as still in
development, so treat it as the least mature patcher and test in-game.

## Filters

- `filterByRefs` — target references by form.

## Operations

- `disable=true` — disable a reference. **NPCs are excluded** from disabling.

## Examples

```ini
; Disable the crate and carriage outside Whiterun's main gate:
filterByRefs=JKs Skyrim.esp|D61, JKs Skyrim.esp|D6D:disable=true

; Disable flowers and clutter across two plugins:
filterByRefs=Update.esm|109B, Update.esm|13D8, Skyrim.esm|000BCC81, Skyrim.esm|000BC725:disable=true
```
