---
name: skypatcher-authoring
description: >-
  Authors and interprets SkyPatcher INI patches — runtime, no-ESP edits that filter Bethesda records and set, add or remove those records' own fields (SkyPatcher 8.0.1 grammar). Load before any SkyPatcher line — the `Plugin.esp|FormID` addressing, the per-type subfolder and the filename gate are non-obvious, and a wrong token fails silently. Use when writing or auditing a SkyPatcher `.ini`, rebalancing weapons, armor, NPCs or leveled lists without an ESP, putting items into a container or chest, or asking why a patch line isn't applying. Not for distribution — distribution to NPCs by group (faction, race, level, trait) is SPID, keywords onto item records is KID.
compatibility: Requires the houseCARL MCP server and a configured Mod Organizer 2 instance.
---

# SkyPatcher Authoring

SkyPatcher is an SKSE plugin that edits Bethesda records at runtime from plain-text INI files — no
ESP or ESL is produced for the edit itself. This skill composes and reads those patch strings and
places the file where SkyPatcher will read it. SkyPatcher edits a record's own fields, an individual
NPC's included; it does not distribute. Two neighbouring jobs are someone else's: what fields a
record has at the Mutagen/xEdit level, with their types and writability, is `mutagen-reference`;
distributing a form to NPCs is `spid-authoring`, which is *best* for groups — faction, race, level,
trait — and can also name one NPC, though for a single NPC SkyPatcher is usually the *better* fit; a
keyword onto item records is `kid-authoring`.

## Route to the record type

Find the record type in the table, then read `references/<file>.md` — that type's own filters,
operations and worked examples. Filter and operation availability varies per type, so the record
file is authoritative; do not carry one across from another type. Read one record file, not the set.

| Record type (xEdit sig) | Subfolder | Primary filter | Reference file |
|---|---|---|---|
| NPC (NPC_) | `npc` | `filterByNpcs` | `references/npc.md` |
| Weapon (WEAP) | `weapon` | `filterByWeapons` | `references/weapon.md` |
| Armor (ARMO) | `armor` | `filterByArmors` | `references/armor.md` |
| Ammo (AMMO) | `ammo` | `filterByAmmos` | `references/ammo.md` |
| Spell (SPEL) | `spell` | `filterBySpells` | `references/spell.md` |
| Scroll (SCRL) | `scroll` | `filterByScrolls` | `references/scroll.md` |
| Enchantment (ENCH) | `enchantment` | `filterByEnchs` | `references/enchantment.md` |
| Magic Effect (MGEF) | `magicEffect` | `filterByMgefs` | `references/magic-effect.md` |
| Alchemy / Ingestible (ALCH) | `ingestible` | `filterByAlchs` | `references/alchemy-ingestible.md` |
| Ingredient (INGR) | `ingredient` | `filterByIngs` | `references/ingredient.md` |
| Book (BOOK) | `book` | `filterByBooks` | `references/book.md` |
| Misc Item (MISC) | `misc` | `filterByMiscs` | `references/misc.md` |
| Soul Gem (SLGM) | `soulGem` | `filterBySoulGems` | `references/soul-gem.md` |
| Outfit (OTFT) | `outfit` | `filterByOutfits` | `references/outfit.md` |
| FormList (FLST) | `formList` | `filterByFormLists` | `references/formlist.md` |
| Leveled List (LVLI / LVLN) | `leveledList` | `filterByLLs` / `filterByLLNPCs` | `references/leveled-list.md` |
| Container (CONT) | `container` | `filterByContainers` | `references/container.md` |
| Constructible Object (COBJ) | `constructibleObject` | `filterByCobjs` | `references/constructible-object.md` |
| Cell (CELL) | `cell` | `filterByCells` | `references/cell.md` |
| Location (LCTN) | `location` | `filterByLocations` | `references/location.md` |
| Encounter Zone (ECZN) | `encounterzone` | `filterByEncounterZones` | `references/encounter-zone.md` |
| Reference (REFR) | `reference` | `filterByRefs` | `references/placed-reference.md` |
| Faction (FACT) | `faction` | `filterByFactions` | `references/faction.md` |
| Movement Type (MOVT) | `movementType` | `filterByMovementTypes` | `references/movement-type.md` |
| Projectile (PROJ) | `projectile` | `filterByProjectiles` | `references/projectile.md` |
| Light (LIGH) | `light` | `filterByLights` | `references/light.md` |
| Race (RACE) | `race` | `filterByRaces` | `references/race.md` |
| Race Hook (RACE) | `raceHook` | `filterByRaces` | `references/race-hook.md` |
| Object Modification (OMOD) | `objectModification` *(unconfirmed)* | *(undocumented)* | `references/object-modification.md` |

Read `references/grammar-core.md` only when the record file leaves a syntax question open — the
shared patch-string structure, the addressing rules and the operation conventions live there.
Shared enums — cast types, actor values, soul types, the biped slot index — are in
`references/value-tables.md`; open it when a record file names one.

## Compose the patch

1. **Pick the filter.** The primary filter by form from the table (the direct list), or a finder
   the record file lists (`filterByKeywords`, `filterByEditorIdContains`, …). Finders on one line
   add up: each finds records on its own, so two finders patch both sets, not their overlap.
   Narrow with a restriction, an `…Excluded` key or `filterByModNames`; none of them touch the
   direct list. A line with no filter patches every record of that type, except in the Leveled
   List and Reference patchers, where it patches nothing. `grammar-core.md` §5 has the full rule.
2. **Address the forms** as `Plugin.esp|FormID`, copied whole from xEdit or the Creation Kit, or by
   EditorID — except on the FormID-only keys, which the record file and `grammar-core.md` name. An
   EditorID of anything but keywords, globals, races, quests, voice types, head parts, cells,
   worldspaces, idles and music types needs powerofthree's Tweaks.
3. **Build the string:** chain segments with `:`, list values with `,`, pack compound values with
   `~` (`mgefsToAdd=Plugin.esp|id~Magnitude~Duration~Area`). Rename with `fullName=~New Name~`;
   clear a form field with `null`.
4. **Pick the operation from the record file**, preferring a relative `…Mult` or `…ToAdd` over an
   absolute set wherever the current winner is generated or the order is tiered — an absolute set
   flattens a whole balance ladder to one number, a multiply preserves it.

Worked pair. Ask: "double the damage of every iron weapon in my load order, no ESP." Line:

```ini
filterByKeywords=WeapMaterialIron:attackDamageMult=2
```

The keyword is addressed by EditorID here; `Plugin.esp|FormID`, copied whole from xEdit, is the
other legal form and the only one on a FormID-only operation.

File: `Data/SKSE/Plugins/SkyPatcher/weapon/MyBalance/ironWeapons.ini` — a plain (always-loading)
name, nested under a mod-specific folder, in the `weapon` subfolder the table gives.

## Place the INI

The INI goes at `Data/SKSE/Plugins/SkyPatcher/<subfolder>/<name>.ini`, with `<subfolder>` taken from
the table. A plain filename always loads; a filename that matches a plugin (`Plugin.esp.ini`) loads
only when that plugin is active, so it self-gates — the right shape for a patch against one mod, the
wrong shape when the targets span many plugins. Nest a plugin-named INI in a mod-specific subfolder
so another mod shipping the same filename cannot overwrite it. Any name with `.ini` in it is read
(`x.ini.bak` too); a name ending `.INI` is not. Comments start with `;`.

Read `references/placement-and-conflicts.md` before naming the file — the exact subfolder casing,
the per-type toggles (a missing toggle is **off**, Light excepted), the global `SkyPatcher.ini`
switches and the read-order conflict rule.

The job needs three things from the user: which records to hit, what the field change is, and
whether the INI may be installed into the live setup or only written to a working folder.

## Check before you install

Prove the target set and every address before anything is placed in a mod.

- The set: count the records the filter means to hit, and record the count beside the line.
- Each address: resolve each `Plugin.esp|FormID` against the load order — a FormID that resolves to
  nothing here resolves to nothing in game, silently.

Stop when the count is what you meant and every address resolves.

## Never invent a token

Every filter, operation and value comes from the bundled reference. If a token is not there, say so
rather than writing a plausible-sounding one: in game SkyPatcher ignores a key it does not know
without a message, so the line can then patch every record of its type, and it skips a missing
EditorID silently (a broken FormID logs "Invalid form identifier"). A fabricated token costs a
debugging session or patches far more than meant. Object Modification (OMOD) has a toggle but "has
no effect in Skyrim" and no documented grammar — hand the user the gap, never a guess.

Two soft spots in the corpus, and how to read them. Where a worked example disagrees with its own
file's filter list, the primary filter in the table above wins and the example is a carried-over
article typo. A token the corpus marks unverified against the DLL, or names without a grammar, is a
warn case, not a use case.

## Common mistakes, and the rule that replaces each

- **Wrong subfolder.** A weapon patch under `npc/` is read by the wrong patcher and does nothing.
  Take the subfolder from the table.
- **A filter or operation assumed by analogy.** `filterByNameContains` exists for armor and not for
  every type. Read it out of that record's own file.
- **A truncated or wrong FormID.** Copy it whole; an unresolvable form is skipped in silence and
  looks exactly like a syntax bug.
- **Forgetting the player exception.** Keyword, race, faction and EditorID filters never find the
  player, and a filterless line skips him. Patch the player by naming him in
  `filterByNpcs=Skyrim.esm|7`.
- **Reading two filters as AND.** `filterByRaces=…:filterByFactions=…` patches every Nord and every
  bandit. To narrow, use a `restrictTo…` key or an `…Excluded` key.
- **An EditorID on a FormID-only key** (NPC `objectsToAdd` / `factionsToAdd`, Reference
  `filterByRefs` / `filterByRefsExcluded`) — these take `Plugin.esp|FormID` only.

## Notes

- **Provenance and floor.** The reference corpus is reconstructed from SkyPatcher's official Nexus
  documentation at **v8.0.1** (re-derived 2026-11-03), covering 28
  record types plus one documented gap (OMOD). Enchantment has no 8.0.1 article and keeps its
  earlier text. On a SkyPatcher version bump, re-derive from the updated articles before trusting it
  for new operations.
- **Conflict model.** SkyPatcher's low-conflict property is real but not magic: same-field set
  operations still resolve by read order (folder first, then subfolders level by level, A to Z),
  and only add/remove operations truly accumulate.
  Say so when a user layers several patches on one record.
- **Lookup without authoring.** The same corpus answers "what filters does this record type
  support" or "what are the legal cast types" with no patch written at all.
