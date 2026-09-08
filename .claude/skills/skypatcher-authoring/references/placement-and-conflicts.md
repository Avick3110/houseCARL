# SkyPatcher Placement & Conflicts

Everything about the *file* rather than the line: where an INI goes, what its name does, which
file wins when two touch the same field, and the global switches that can turn a whole type off.
The patch string itself is in `grammar-core.md`.

Sections: 1. The folder tree · 2. Subfolder casing · 3. The two filename behaviours ·
4. Mod-manager collisions · 5. Conflict resolution · 6. Global settings (`SkyPatcher.ini`).

---

## 1. The folder tree

```
Data/
└── SKSE/
    └── Plugins/
        ├── SkyPatcher.dll
        ├── SkyPatcher.ini            ← global settings (§6)
        └── SkyPatcher/
            ├── npc/                   ← one subfolder per record type
            ├── weapon/
            ├── armor/
            └── … (the subfolder column of the router table in SKILL.md)
```

- An INI goes in the **subfolder for its record type**. A race patch must live under `race/`,
  a weapon patch under `weapon/`, etc. INIs in the wrong subfolder are read by the wrong patcher
  (or ignored).
- You may **nest freely** inside a type folder to organize by mod:
  `SkyPatcher/npc/MyMod/bandits/file.ini`.
- **Comments** start with `;`.

## 2. Subfolder casing

Subfolder casing is taken from the shipped mod (`constructibleObject`, `formList`, `magicEffect`,
`movementType`, `raceHook`, `soulGem` are camelCase; `encounterzone` is all lowercase). Windows
file systems are case-insensitive, but match the shipped casing.

## 3. The two INI filename behaviours

| Filename shape | When it loads |
|---|---|
| `anything.ini` (e.g. `myEdits.ini`) | **Always** loaded. |
| `Plugin.esm.ini` / `Plugin.esp.ini` (name = a plugin filename) | **Only** when that plugin is active in the load order; otherwise skipped. |

The plugin-gated form is how you ship conditional patches: name the file after the plugin
whose records you patch, and it self-disables when that plugin is absent. This is distinct
from the `hasPlugins` filter (`grammar-core.md` §6) — the filename gate decides whether the
*file* is read at all; `hasPlugins` decides whether a *line* applies.

## 4. Mod-manager collisions

Two mods that both ship `Skyrim.esm.ini` in the same SkyPatcher subfolder will overwrite each
other (same path). Always nest plugin-named INIs in a mod-specific subfolder:
`SkyPatcher/npc/MyMod/Skyrim.esm.ini`.

## 5. Conflict resolution

INIs within a type folder are read in filename order `0`→`z`. If two lines set the **same
field** of the same record, the later-sorted file wins (`zPatch.ini` beats `mPatch.ini`).
Add/remove operations (e.g. `keywordsToAdd`, `formsToAdd`) **accumulate** rather than
overwrite, so multiple INIs can add to the same record without conflict.

## 6. Global settings — `SkyPatcher.ini`

`Data/SKSE/Plugins/SkyPatcher.ini` holds three sections:

- **`[Patcher]`** — one `iEnable<Type>Patching=1|0` per record type (all on by default). Turning
  a type off skips its whole subfolder.
- **`[Log]`** — `iEnablelog=0|1`.
- **`[Features]`** — global behaviors. The load-bearing ones:
  - `iAllowLeveledListsAddedToContainers=0` — off by default; LLs added to containers can CTD
    for some users (see `container.md` / `leveled-list.md`).
  - `iEnableUnlevelNPCs=0` — unlevels NPCs and encounter zones when on.
  - `iEnableSetLevelDirectlyByPCMult=0` — controls how a delevelled NPC's level is computed.
  - `iUpdateNPC=1` — apply NPC changes to already-spawned actors while playing (visuals,
    perks, spells). `iUpdateNPCExclude` + `iUpdateNPCExcludeList` carve out exceptions.
  - `iRefreshNPCStats=1` — refresh NPC stats at runtime when mods are added/updated/removed.
  - `iUpdateRefs=1` — enable REFR patching (see `placed-reference.md`).
  - `iUpdateNPCVisualsOnLoad` — 0 none / 1 by function / 2 by disable+enable.

These are user/global settings, not per-patch — a patch author rarely ships them, but should
know `iAllowLeveledListsAddedToContainers` and the `iUpdateNPC`/`iRefreshNPCStats` behaviors
because they change whether a patch takes effect on an existing save.
