# Movement Type Patcher — MOVT

`SkyPatcher/movementType/` · `iEnableMovementTypePatching` · primary filter `filterByMovementTypes`
Shared mechanics → `grammar-core.md`.

Edits the per-direction movement speeds of a movement-type record.

## Filters

- `filterByMovementTypes` / `…Excluded` — target movement types by form.

## Operations

Each operation sets a movement speed:

```
walkLeft     runLeft
walkRight    runRight
walkForward  runForward
walkBack     runBack
rotateInPlaceWalk    rotateInPlaceRun
rotateWhileMovingRun
```

## Examples

```ini
filterByMovementTypes=Skyrim.esm|123:walkLeft=35
```
