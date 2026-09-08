# Projectile Patcher — PROJ

`SkyPatcher/projectile/` · `iEnableProjectilePatching` · primary filter `filterByProjectiles`
Shared mechanics → `grammar-core.md`.

## Filters

- `filterByProjectiles` / `…Excluded` — target projectiles by form.
- `filterByModNames` — restrict by source plugin.
- `filterByEditorIdContains` / `…Or` / `…Excluded`.

## Operations

- `range` · `rangeMult` · `speed` · `speedMult` · `gravity` · `gravityMult`.
- `sound=<form>` · `explosion=<form>`.
- `type=<missile|grenade|beam|flamethrower|cone|barrier|arrow>`.
- `setFlags` / `removeFlags` — projectile flags (below).

**Projectile flags:**

```
hitscanexplosion   explosionalttrigger   muzzleflash   canturnoff   canpickup
supersonic         pinslimbs             passsmtransparent
disablecombataimcorrection               continuousupdate
```

## Examples

```ini
filterByProjectiles=Skyrim.esm|13988:range=9001
filterByProjectiles=Skyrim.esm|13988:setFlags=supersonic, canpickup
filterByProjectiles=Skyrim.esm|13988:type=missile
filterByProjectiles=Skyrim.esm|123:explosion=Skyrim.esm|321
```
