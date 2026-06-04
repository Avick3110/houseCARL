# Cell Patcher — CELL

`SkyPatcher/cell/` · `iEnableCellPatching` · primary filter `filterByCells`
Shared mechanics → `grammar-core.md`.

Edits cell lighting, fog, water, and flags. Color ops are per-channel; **each RGB channel is
0–255.**

## Filters

- `filterByCells` — target cells by form.
- `skipRecordByLightingTemplateFromMod=<plugin,…>` — skip if the cell's current lighting template
  comes from a listed mod (yield to it).
- `skipRecordByModNameContains=<substr,…>` — skip if the record's source mod name contains a substring.

## Operations

### Fog & clipping
- `fogNear` · `fogFar` · `fogPower` · `clipDist`.

### Directional light rotation
- `directionalXY` (horizontal, X/Y) · `directionalZ` (vertical, Z).

### Colors (per channel, 0–255)
- Ambient: `ambient{Red,Green,Blue}`.
- Directional: `directional{Red,Green,Blue}`.
- Fog near color: `fogColorNear{Red,Green,Blue}`.
- Directional ambient cube: `directionalAmbient{X,Y,Z}{Min,Max}{Red,Green,Blue}`
  (e.g. `directionalAmbientXMinRed`).
- Directional ambient specular: `directionalAmbientSpecular{Red,Green,Blue}`.

### Form references (use `null` to clear)
- `lightingTemplate` · `acousticSpace` · `musicType` · `skyRegion` · `imageSpace` ·
  `encounterZone` · `waterType`.
- `waterHeight=<n>` (no null).

### Flags
- `setInheritanceFlags` / `removeInheritanceFlags` — control what the interior inherits:
  `ambientcolor, directionalcolor, fogcolor, fognear, fogfar, directionalrotation,
  directionalfade, clipdistance, fogpower, fogmax, lightfadedistances`.
- `setCellFlags` / `removeCellFlags`:
  `isinteriorcell, haswater, cantravelfromhere, nolodwater, hastempdata, publicarea,
  handchanged, showsky, useskylighting, warntoleave`.

## Examples

```ini
filterByCells=Skyrim.esm|0007BEF8, Dawnguard.esm|000ACED1:fogNear=400:fogFar=12000
ambientRed=32:ambientGreen=48:ambientBlue=64
directionalAmbientXMinRed=64:directionalAmbientXMinGreen=64:directionalAmbientXMinBlue=128
setInheritanceFlags=fogcolor, fogfar, clipdistance
setCellFlags=showsky, haswater
lightingTemplate=Skyrim.esm|000C6528
encounterZone=Skyrim.esm|0001C84E
waterHeight=512.0
skipRecordByLightingTemplateFromMod=ELFX.esp, Lux.esp
```
