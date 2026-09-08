# Outfit Patcher — OTFT

`SkyPatcher/outfit/` · `iEnableOutfitPatching` · primary filter `filterByOutfits`
Shared mechanics → `grammar-core.md`.

> **No filter → ALL outfits are affected.** Always filter unless you really mean every outfit.

## Filters

- `filterByOutfits` — target outfits by form.
- `filterByModNames` — restrict by source plugin.
- `filterByForms` / `…Or` / `…Exclude` — match outfits that **contain** the given object(s).

## Operations

- `formsToAdd=<form,…>` — add objects to the outfit.
- `formsToRemove=<form,…>` — remove objects from the outfit.
- `formsToReplace=<formA=formB>` — replace in place. **FormID only.** (Docs write the pair with
  `=`; Leveled List uses `~` for the same op.)
- `clear=true` — empty the outfit (remove all records).

## Examples

```ini
filterByOutfits=Skyrim.esm|246EE7:formsToAdd=Skyrim.esm|59A71
filterByOutfits=Skyrim.esm|246EE7:formsToRemove=Skyrim.esm|59A71
filterByOutfits=Skyrim.esm|246EE7:formsToReplace=Skyrim.esm|59A71=Skyrim.esm|59A72
filterByOutfits=Skyrim.esm|246EE7:clear=true
```
