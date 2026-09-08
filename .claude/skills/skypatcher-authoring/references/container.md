# Container Patcher — CONT

`SkyPatcher/container/` · `iEnableContainerPatching` · primary filter `filterByContainers`
Shared mechanics → `grammar-core.md`.

## Filters

- `filterByContainers` — target containers by form.
- `filterByEditorIdContains` / `…Or` / `…Excluded`.

## Operations

- `addToContainers=<form~count>` — add an object with a count.
- `addOnceToContainers=<form~count>` — add only if not already present.
- `objectMultCount=<form~mult>` or `=<mult>` — multiply object counts (filtered or all).
- `removeFromContainers=<form,…>` — remove objects.
- `removeContainerObjectsByCount=<form~count,…>` — remove a specific count of an object.
- `removeContainerObjectsByKeywords=<form>` — remove objects by keyword.
- `replaceInContainers=<formA~formB>` — replace all instances; count preserved.
- `clear=true` — remove all objects.

> **Leveled Lists in containers can CTD** for some users. Adding LLs is gated by the global
> `iAllowLeveledListsAddedToContainers` (off by default in `SkyPatcher.ini`). If you ship a patch
> that relies on it, add a disclaimer to your mod.

## Examples

```ini
filterByContainers=Skyrim.esm|1338A7:addToContainers=myTestESP.esp|E4001733~100
filterByContainers=Skyrim.esm|1338A7:removeFromContainers=Skyrim.esm|f, Skyrim.esm|8531F
filterByContainers=Skyrim.esm|1338A7:removeContainerObjectsByCount=Skyrim.esm|f~200
filterByContainers=Skyrim.esm|1338A7:removeContainerObjectsByKeywords=Skyrim.esm|00123456
objectMultCount=2.0
clear=true
```
