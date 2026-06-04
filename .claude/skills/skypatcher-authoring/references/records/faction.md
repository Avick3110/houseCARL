# Faction Patcher — FACT

`SkyPatcher/faction/` · `iEnableFactionPatching` · primary filter `filterByFactions`
Shared mechanics → `grammar-core.md`.

## Filters

- `filterByFactions` — target factions by form.
- `filterByModNames` — restrict by source plugin.

## Operations

- `setFlags` / `removeFlags` — comma-separated faction flags (below).

**Faction flags** (as written in the source article — see the note):

```
hiddenfromnpc          specialcombat            playerisexpelled
playerisenemy          trackcrime               ignorescrimes_murder
ignorescrimes_assult   ignorescrimes_stealing   ingorescrimes_trespass
donotreportcrimesagainstmembers   crimegold_usedefaults   ignorescrimes_pickpocket
vendor                 canbeowner               ignorescrimes_werewolf
```

> **Spelling caution.** Two flags are written in the docs as `ignorescrimes_assult` (looks like
> "assault") and `ingorescrimes_trespass` (looks like "ignore" transposed). These may be the
> literal tokens SkyPatcher parses, or article typos. Use the documented spelling first; if a
> flag has no effect, try the corrected spelling and verify in-game.

## Examples

```ini
setFlags=ignorescrimes_murder, vendor
removeFlags=donotreportcrimesagainstmembers, vendor
```
