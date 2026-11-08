# Walk bundles — the fields that make a record's parts

Hand-kept domain data, not generated: which link fields a modder treats as part of a record, per seed kind. Pass a
bundle as `housecarl_asset_status walk={"follow": [...]}`. The asset walk enters what the seed lists (`walk.depth`,
default 1), then follows these fields from every record it enters, as deep as they go; every other link past depth 1
is a counted stop. A field is named as the record type spells it (check one in `references/` as `SKILL.md` says); it
is followed on every record type that has it, and a field no entered record carries is a note, not an error.

| Seed kind | `walk.follow` |
|---|---|
| NPC: his look and his outfit's worn meshes (replacer, appearance copy, dark face) | `["HeadParts", "ExtraParts", "TextureSet", "HairColor", "HeadTexture", "WornArmor", "Armature", "SkinTexture", "TextureSwapList", "Items"]` |
| Armour: its addons' meshes and their texture sets | `["Armature", "SkinTexture", "TextureSwapList", "Items"]` |
| Weapon, furniture, static, quest | none: their parts are what they list, which depth 1 enters |

What each field reaches:

- `HeadParts` (NPC) → head parts; `ExtraParts`, `TextureSet` (head part) → their extra parts and texture sets.
- `HairColor`, `HeadTexture` (NPC) → his hair colour and face texture set.
- `WornArmor` (NPC) → his skin armour; `Armature` (armour) → its armour addons; `SkinTexture`, `TextureSwapList`
  (armour addon) → their texture sets and swap lists; `Items` (form list) → the swap list's texture sets.
- `Items` (outfit) → its armour. His outfits are links he lists, so depth 1 enters them, and `Items` and `Armature`
  carry on to the worn armour addons.

Known reach, measured on Addvar (`013255:Skyrim.esm`) in the dev instance (280 rows, against 139 for `walk=true`):

- His skin armour lists every race's naked-body addon (horse, werewolf, dragon), so the bundle lists their files:
  that is what `WornArmor` holds.
- A leveled list in an outfit is followed only with `"Entries"` added, and that also follows his inventory's pocket
  loot lists to their potions and books.
- Mentions are never in a bundle: `Race`, `AdditionalRaces`, `ValidRaces`, factions, packages, quests, locations,
  other NPCs. A reached NPC stays a counted boundary whatever the bundle.
