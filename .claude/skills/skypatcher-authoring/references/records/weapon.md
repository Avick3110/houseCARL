# Weapon Patcher — WEAP

`SkyPatcher/weapon/` · `iEnableWeaponPatching` · primary filter `filterByWeapons`
Shared mechanics → `grammar-core.md`. Shared enums → `value-tables.md`.

## Filters

- `filterByWeapons` / `…Excluded` — target weapons by form.
- `filterByModNames` — restrict by source plugin.
- `filterByKeywords` / `…Or` / `…Excluded`.
- `filterByEditorIdContains` / `…Or` / `…Excluded`.
- `filterByFirstPersonModelOr=<form,…>` — by first-person model.
- `filterBySkills=<skill,…>` — by weapon skill. Accepts the combat subset:
  `onehanded, twohanded, marksman, destruction, illusion, conjuration, alteration, restoration`.
- `filterByAnimationTypeOr=<type,…>` — by animation type (list below).
- `hasPlugins` / `hasPluginsOr` — gate on plugin(s) present.
- `restrictToSkills=<skill,…>` — post-match skill restriction (same combat subset).
- `restrictToFlags=boundweapon` — only `boundweapon` is available.
- `filterByHasAmmoFromWeaponList` — named in the article as an independent filter group (no
  further detail documented).

**Animation types** (also values for the `animationType` op):

```
handtohandmelee  onehandsword  onehanddagger  onehandaxe  onehandmace
twohandsword     twohandaxe    bow            staff       crossbow
```

## Operations

- `fullName=~Name~`.
- `animationType=<type>` — from the animation-types list above.
- `skillType=<skill>` — full skill list → `value-tables.md`.
- **Damage:** `attackDamage` · `attackDamageMult` · `attackDamageToAdd` · `bashDamage`.
- **Crit:** `critDamage` · `critDamageToAdd` · `critDamageMult` · `critPercentMult` ·
  `critDamageSetToBase=true` (crit = base damage).
  > `critPercentMult` targets the CRDT *% Mult* field, which is itself a multiplier — so unlike the
  > other `…Mult` ops it is best read as a direct **set** of that field, not a multiply-current
  > (`critPercentMult=2` ⇒ crit multiplier becomes 2.0). Unverified against the DLL; if you can test
  > in game, prefer an explicit value and check the result.
- **Stats:** `weight` · `weightMult` · `value` · `valueMult` · `speed` · `speedMult` · `reach` ·
  `rangeMin` · `rangeMax` · `stagger` · `enchantAmount`.
- **Models/sounds:** `model` · `modelFirstPerson` · `equipSound` (or `null`) · `unequipSound`
  (or `null`) · `attackFailSound` · `impactDataSet` · `altBlockMaterialType` ·
  `blockBashImpactDataSet`.
- `equipSlot=<form>` · `templateWeapon=<form>` · `objectEffect=<form>` (enchantment).
- `weaponHitType=<normal|dismember|explode|no>`.
- `soundLevel=<loud|normal|silent|veryloud|quiet>`.
- `keywordsToAdd` / `keywordsToRemove`.
- `mirrorWeapon=<form>` — copy appearance + weapon data from a template (models, sounds,
  animation data, ranged data, impact data, block material, equip slot, icon).
- Object bounds: `minX/minY/minZ` `maxX/maxY/maxZ` (→ `grammar-core.md` §7).

## Examples

```ini
filterByWeapons=Skyrim.esm|12EB7, Skyrim.esm|13790:attackDamage=99:weight=0:keywordsToAdd=myMod.esp|223
filterByWeapons=Skyrim.esm|4822:restrictToSkills=twohanded
filterByWeapons=Skyrim.esm|13988:attackDamageMult=0.5
filterByWeapons=Skyrim.esm|13988:critDamageSetToBase=true
filterByWeapons=Skyrim.esm|13988:equipSound=null
filterByWeapons=Skyrim.esm|4822:weaponHitType=dismember
filterByWeapons=Skyrim.esm|4822:soundLevel=silent
filterByWeapons=Skyrim.esm|13988:mirrorWeapon=Skyrim.esm|000139B9
```
