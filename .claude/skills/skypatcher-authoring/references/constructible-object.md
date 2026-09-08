# Constructible Object Patcher — COBJ

`SkyPatcher/constructibleObject/` · `iEnableConstructibleObjectPatching` · primary filter `filterByCobjs`
Shared mechanics → `grammar-core.md`.

Patches crafting recipes (COBJ) — their ingredients, output, count, and workbench.

## Filters

- `filterByCobjs` / `…Excluded` — target recipes by form.
- `filterByIngredients` / `…Or` / `…Excluded` — by an ingredient in the recipe.
- `filterByKeywords` / `…Or` / `…Excluded` — **keywords of the created object**.
- `filterByWorkBenchKeywords=<keyword,…>` — by the recipe's workbench keyword
  (e.g. `CraftingSmithingForge`, `CraftingTanningRack`).
- `filterWorkbenchKeywordsExcluded=<keyword,…>` — exclude by workbench keyword.
- `filterByEditorIdContains` / `…Or` / `…Excluded`.

## Operations

- `workbenchKeyword=<form>` — set the recipe's workbench (`null` removes it).
- `createdObject=<form>` — set the recipe's output.
- `count=<n>` — number of objects created · `countMult=<n>` — multiply it.
- `addToCobjs=<item~count>` — add an ingredient with a count.
- `removeFromCobjs=<item>` — remove all instances of an ingredient.
- `removeFromCobjsByCount=<item~count>` — remove an ingredient with a specific count.
- `changeCobjsCount=<form~count>` — set an ingredient's count (`null~0` = all ingredients to 0).
- `changeCobjsCountByMult=<n>` — multiply ingredient counts.
- `replaceInCobjs=<formA~formB>` — replace an ingredient; count preserved.
- `clear=true` — remove all ingredients.
- `keywordsToAdd` / `keywordsToRemove` — on the **created object**.
- `restrictToKeywords=<form,…>` — restrict by a keyword on the created object (else ignore).

## Examples

```ini
filterByWorkBenchKeywords=CraftingSmithingForge, CraftingTanningRack
filterByCobjs=Skyrim.esm|13988:count=5
filterByCobjs=Skyrim.esm|13988:addToCobjs=Skyrim.esm|34d7~5
filterByCobjs=Skyrim.esm|13988:removeFromCobjsByCount=Skyrim.esm|34d7~5
filterByCobjs=Skyrim.esm|13988:createdObject=Skyrim.esm|34d7
changeCobjsCount=Skyrim.esm|0005ACE5~2
changeCobjsCountByMult=0.5
replaceInCobjs=Skyrim.esm|0005ACE5~Skyrim.esm|0005ACE4
filterByCobjs=Skyrim.esm|13988:restrictToKeywords=Skyrim.esm|34d7
```
