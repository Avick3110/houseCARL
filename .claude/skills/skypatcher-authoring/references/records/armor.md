# Armor Patcher — ARMO

`SkyPatcher/armor/` · `iEnableArmorPatching` · primary filter `filterByArmors`
Shared mechanics → `grammar-core.md`. Shared enums → `value-tables.md` (biped slot index).

Changes to an armor also apply to all ARMA (armor addons) attached to it.

## Filters

- `filterByArmors` / `…Excluded` — target armors by form.
- `filterByModNames` — restrict by source plugin.
- `filterByArmorTypes` / `…Excluded` — by class: `heavy`, `light`, `clothing`.
- `filterByKeywords` / `…Or` / `…Excluded`.
- `filterByBipedSlots` / `…Or` / `…Excluded` — by biped slot **index** (→ `value-tables.md`).
- `filterByEditorIdContains` / `…Or` / `…Excluded`.
- `filterByNameContains` / `…Or` / `…Excluded` — substring of the full name.
- `filterByArmorAddons` / `…Or` / `…Excluded` — by armor addon (EditorID substring ok).
- `hasPlugins` / `hasPluginsOr`.
- `restrictToBipedSlots=<index,…>` — post-match biped-slot restriction.
- `restrictToKeywords=<form/keyword,…>` — post-match keyword restriction.

## Operations

- `fullName=~Name~`.
- **Rating:** `damageResist` · `damageResistMultiply` · `damageResistMatch=<form>` (match another
  armor's rating) · `dwMatch=<form>` (match rating **and** weight) · `enchantAmount`.
- **Stats:** `weight` · `weightMult` · `value` · `valueMult`.
- `armorType=<lightarmor|heavyarmor|clothing>` — note: op values differ from the
  `filterByArmorTypes` values (`heavy|light|clothing`).
- `objectEffect=<form>` (or `null`) — add/remove an enchantment.
- `equipSlot=<form>` · `templateArmor=<form>` · `altBlockMaterialType=<form>` ·
  `blockBashImpactDataSet=<form>`.
- `pickUpSound=<form>` · `putDownSound=<form>`.
- `modelMatch=<form>` — set model (incl. sounds + inventory model) to another armor's.
- `mirrorArmor=<form>` — copy appearance + armor data: biped model data, armor addons, world
  model, inventory icon, pickup/putdown sounds, equip slot, block-bash impact data set, alt
  block material type, armor rating, value.
- `armorAddonsToAdd` / `armorAddonsToRemove` / `clearArmorAddons=true`.
- `bipedSlotsToAdd=<index,…>` / `bipedSlotsToRemove=<index,…>` — by index (→ `value-tables.md`).
- `keywordsToAdd` / `keywordsToRemove`.
- `setFlags` / `removeFlags` — flags: `nonplayable`, `shield`.
- Object bounds: `minX/minY/minZ` `maxX/maxY/maxZ`.

## Examples

```ini
filterByArmors=Skyrim.esm|00012E49:damageResistMatch=Skyrim.esm|0001396B   ; iron → daedric rating
filterByArmors=Skyrim.esm|00012E49:dwMatch=Skyrim.esm|0001396B             ; rating + weight
filterByKeywords=Skyrim.esm|6BBD2:damageResistMultiply=3
filterByKeywords=Skyrim.esm|1393B:damageResist=50:objectEffect=Skyrim.esm|8B65E
filterByArmors=Skyrim.esm|6BBD2:armorType=lightarmor
filterByBipedSlots=0,1:damageResistMultiply=2
filterByArmors=Skyrim.esm|6BBD2:bipedSlotsToRemove=11:bipedSlotsToAdd=12
filterByArmors=Skyrim.esm|1395C:mirrorArmor=Skyrim.esm|12E49
filterByArmorTypes=heavy, light
```
