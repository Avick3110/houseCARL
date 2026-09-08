---
name: skypatcher-authoring
description: >-
  Authors and interprets SkyPatcher INI patches — runtime, no-ESP edits that filter Bethesda records and set, add or remove those records' own fields (SkyPatcher 6.4.1 grammar). Load before any SkyPatcher line — the `Plugin.esp|FormID` addressing, the per-type subfolder and the filename gate are non-obvious, and a wrong token fails silently. Use when writing or auditing a SkyPatcher `.ini`, rebalancing weapons, armor, NPCs or leveled lists without an ESP, or asking why a patch line isn't applying. Not for distribution — forms onto NPCs is SPID, keywords onto items is KID, items into containers is CID.
compatibility: Requires the houseCARL MCP server and a configured Mod Organizer 2 instance.
---

# SkyPatcher Authoring

SkyPatcher is an SKSE plugin that edits Bethesda records at runtime from plain-text INI files — no
ESP or ESL is produced for the edit itself. This skill composes and reads those patch strings and
places the file where SkyPatcher will read it. SkyPatcher edits a record's own fields; it does not
distribute. Two neighbouring jobs are someone else's: what fields a record has at the Mutagen/xEdit
level, with their types and writability, is `housecarl:mutagen-reference`; putting forms onto NPCs
or keywords onto items is `housecarl:spid-authoring` / `housecarl:kid-authoring`. On a Codex flat
install these siblings are the bare folder names — `mutagen-reference`, `spid-authoring`,
`kid-authoring`.

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
| Race (RACE) | `race` | `filterByRaces` | `references/race.md` |
| Race Hook (RACE) | `raceHook` | `filterByRaces` | `references/race-hook.md` |
| Object Modification (OMOD) | `objectModification` *(unconfirmed)* | *(undocumented)* | `references/object-modification.md` |

Read `references/grammar-core.md` only when the record file leaves a syntax question open — the
shared patch-string structure, the addressing rules and the operation conventions live there.
Shared enums — cast types, actor values, soul types, the biped slot index — are in
`references/value-tables.md`; open it when a record file names one.

## Compose the patch

1. **Pick the filter.** The primary filter by form from the table, or a cross-cutting one the record
   file lists (`filterByKeywords`, `filterByEditorIdContains`, `filterByModNames`, `hasPlugins`).
   A line with no filter patches every record of that type.
2. **Address the forms** as `Plugin.esp|FormID`, copied whole from xEdit or the Creation Kit, or by
   EditorID — except on the FormID-only operations, which the record file and `grammar-core.md` name.
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
so another mod shipping the same filename cannot overwrite it. Comments start with `;`.

Read `references/placement-and-conflicts.md` before naming the file — the exact subfolder casing,
the per-type toggles, the global `SkyPatcher.ini` switches and the filename-order conflict rule.

The job needs three things from the user: which records to hit, what the field change is, and
whether the INI may be installed into the live setup or only written to a working folder.

## Check before you install

Prove the target set, every address, and the draft file itself before anything is placed in a mod.

- The set: `housecarl_records` with `types=`, `plugins=` and `where=` for the set the filter means to
  hit, plus `counts_only=true` for the cheap census. Record the count and the epoch stamp beside it.
- Each address: `housecarl_records` with `formids=["012EB7:Skyrim.esm"]` and
  `project={"form": "identity"}` — a FormID that resolves to nothing here resolves to nothing in
  game, silently.
- The draft: `housecarl_records` with `formids=` a target and `source={"overlay": "skypatcher",
  "state": "post", "ini": "<absolute path to the draft .ini>", "subfolder": "weapon"}` reads the
  record as the game would see it once that draft is placed — the live layer plus the draft, sorted
  into its type folder by filename. Drop `subfolder` when the draft already sits in a folder of that
  name. Keep that pole on `source=` and pass the plain post pole,
  `versus={"overlay": "skypatcher", "state": "post"}`, with `project={"form": "delta"}` for the
  draft's own effect and nothing else. Warnings the replay produces for a draft line — an unknown
  key, an op with no field mapping, a filter it cannot evaluate — render beside the answer under
  the draft's path.

Stop when the count is what you meant, every address resolves, the delta is the change you intended,
and the replay warned on none of your lines.

## Check after you install

Once the INI is installed, three reads prove it landed.

- `housecarl_skypatcher_layer` with `filter=` the type folder or the INI filename, for the
  file-level verdict — whether the file is read at all, where it sorts, what it conflicts with, and
  which of its ITM findings (an intra-file dead write, a cross-INI duplicate, a no-op write) name
  your file. The first two are authoring slips to fix at the source, and a dead write hides from the
  delta below, which shows only the value that survived it.
- `housecarl_records` with `formids=` a target, `source={"overlay": "skypatcher", "state": "post"}`,
  `versus={"overlay": "skypatcher", "state": "pre"}` and `project={"form": "delta"}`, for the
  before → after on that record, with the replay's warnings for the placed file's lines beside it.
- `housecarl_skse` with `findings='config'` and `filter=` the INI filename, which resolves every
  `Plugin.esp|FormID` the file contains to OK, PLUGIN MISSING, DANGLING or UNPARSEABLE — the machine
  check for a truncated or wrong FormID.

## Never invent a token

Every filter, operation and value comes from the bundled reference. If a token is not there, say so
rather than writing a plausible-sounding one: in game SkyPatcher skips a line it cannot parse and
skips a FormID it cannot resolve, both silently and with no log line, so a fabricated token costs a
debugging session and produces nothing to debug. The replay warns where the game does not, which is
a net under a mistake, not a licence to guess. Object Modification (OMOD) is enabled in SkyPatcher
but has no documented grammar — hand the user the gap and the leads in its row above, never a guess.

Two soft spots in the corpus, and how to read them. Where a worked example disagrees with its own
file's filter list, the primary filter in the table above wins and the example is a carried-over
article typo. A token the corpus marks unverified against the DLL, or names without a grammar, is a
warn case, not a use case.

## Common mistakes

- **Wrong subfolder.** A weapon patch under `npc/` is read by the wrong patcher and does nothing.
  Take the subfolder from the table.
- **A filter or operation assumed by analogy.** `filterByNameContains` exists for armor and not for
  every type. Read it out of that record's own file.
- **A truncated or wrong FormID.** Copy it whole; an unresolvable form is skipped in silence and
  looks exactly like a syntax bug.
- **Forgetting the player exception.** Race and keyword filters always exclude the player. Patch the
  player with `filterByNpcs=Skyrim.esm|7` alone, no other filter on the line.
- **An EditorID on a FormID-only operation** (NPC `objectsToAdd` / `factionsToAdd`, Outfit /
  FormList / Leveled List `formsToReplace`) — these take `Plugin.esp|FormID` only.

## Notes

- **Provenance and floor.** The reference corpus is reconstructed from SkyPatcher's official Nexus
  documentation at **v6.4.1**, covering 27 record types plus one documented gap (OMOD). On a
  SkyPatcher version bump, re-derive from the updated articles before trusting it for new
  operations.
- **Conflict model.** SkyPatcher's low-conflict property is real but not magic: same-field set
  operations still resolve by filename order, and only add/remove operations truly accumulate.
  Say so when a user layers several patches on one record.
- **Lookup without authoring.** The same corpus answers "what filters does this record type
  support" or "what are the legal cast types" with no patch written at all.
