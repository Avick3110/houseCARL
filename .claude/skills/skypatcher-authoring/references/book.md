# Book Patcher — BOOK

`SkyPatcher/book/` · `iEnableBookPatching` · primary filter `filterByBooks`
Shared mechanics → `grammar-core.md`. Shared enums → `value-tables.md` (skills).

## Filters

- `filterByBooks` / `…Excluded` — target books by form.
- `filterByModNames` — restrict by source plugin.
- `filterByAlternateTextures` — match books carrying a given texture set.
- `filterByCastingPerk=<form,…>` — by the book's casting perk.
- `filterByFlags=<flag,…>` — by book flags (at least one must match). Flags below.
- `filterByKeywords` / `…Or` / `…Excluded`.
- `filterByEditorIdContains` / `…Or` / `…Excluded`.

**Book flags** (also values for `setFlags`/`removeFlags`):

```
advancesactorvalue   canttake   teachesspell   hasbeenread
```

## Operations

- `fullName=~Name~`.
- `weight` · `weightMult` · `value` · `valueMult`.
- `keywordsToAdd` / `keywordsToRemove`.
- `pickUpSound=<form>` · `putDownSound=<form>` · `inventoryArt=<form>`.
- `teachSpell=<form>` — spell the book teaches.
- `teachSkill=<skill>` — skill the book trains (→ `value-tables.md`).
- `setFlags` / `removeFlags` — from the book-flags list above.
- `type=<booktome|notescroll>`.
- `model=<form|path>` · `alternateTexturesToAdd=TextureSet~Name3D~Index3D` · `alternateTexturesToRemove` · `alternateTexturesClear=true`.
- Object bounds: `minX/minY/minZ` `maxX/maxY/maxZ`.

## Examples

```ini
filterByFlags=advancesactorvalue
filterByBooks=Skyrim.esm|13988:teachSkill=alchemy
filterByBooks=Skyrim.esm|13988:type=booktome
removeFlags=advancesactorvalue, canttake

; Model + texture swap (EditorID example):
filterByBooks=Book1CheapBiographyofBarenziahvI:model=clutter\books\BCSSEBookTall.nif:alternateTexturesToAdd=BCS_BookBiographyQueenBarenziah01~Cover~2:inventoryArt=BCS_Book_BQB01
```
