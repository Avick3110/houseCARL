# Light Patcher — LIGH

`SkyPatcher/light/` · `iEnableLightPatching` (on unless `iEnableLightPatching=0`) · primary filter
`filterByLights`. New in 7.1.0. Shared mechanics → `grammar-core.md`.

## Filters

- `filterByLights` / `…Excluded` — target lights by form. The listed lights are always patched:
  exclusions and `filterByModNames` do not apply to them. Without EditorID filters the line only
  patches these lights.
- `filterByModNames` — narrower, by source plugin.
- `filterByEditorIdContains` / `…Or` / `…Excluded` — needs powerofthree's Tweaks (`grammar-core.md` §4).

Lights have no keyword filters. A line without any filter changes every light.

## Operations

- `radius=<n>` · `fade=<n>` · `falloffExponent=<n>` · `fov=<n>` · `nearDistance=<n>` · `time=<n>`.
- `color=<r,g,b>` — needs exactly three values (`color=255,180,110`).
- `flickerPeriod=<n>` (since 8.0.1 the same value as the Creation Kit) · `flickerIntensityAmplitude=<n>` ·
  `flickerMovementAmplitude=<n>`.
- `setFlags` / `removeFlags` — `setFlags` is applied before `removeFlags`. Flags:

```
dynamic  cancarry  negative  flicker  deepcopy  offbydefault  flickerslow  pulse  pulseslow
spotlight  spotshadow  hemishadow  omnishadow  portalstrict
```

## Examples

```ini
filterByLights=Skyrim.esm|0001F2E9:radius=512:color=255,180,110
filterByEditorIdContains=Torch:setFlags=flicker:removeFlags=pulse
```
