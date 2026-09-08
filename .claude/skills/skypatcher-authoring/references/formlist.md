# FormList Patcher — FLST

`SkyPatcher/formList/` · `iEnableFormlistPatching` · primary filter `filterByFormLists`
Shared mechanics → `grammar-core.md`.

> **No filter → ALL form lists are affected.** Always filter unless you really mean every list.

## Filters

- `filterByFormLists` — target form lists by form.
- `filterByModNames` — restrict by source plugin.

## Operations

- `formsToAdd=<form,…>` — add entries.
- `formsToRemove=<form,…>` — remove entries.
- `formsToReplace=<formA=formB>` — replace in place. **FormID only.** (The docs write the pair
  with `=`; Leveled List uses `~` for the same op — verify empirically if unsure.)
- `clear=true` — remove all forms.

## Examples

```ini
filterByFormLists=Skyrim.esm|246EE7:formsToAdd=Skyrim.esm|59A71
filterByFormLists=Skyrim.esm|246EE7:formsToRemove=Skyrim.esm|59A71
filterByFormLists=Skyrim.esm|246EE7:formsToReplace=Skyrim.esm|59A71=Skyrim.esm|59A72
filterByFormLists=Skyrim.esm|456:clear=true
```
