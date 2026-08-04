---
name: housecarl
description: Work with Skyrim Special Edition load-order records and assets through the houseCARL MCP server — set or switch the MO2 instance, inspect active or disabled plugins and conflict trees, read records, query across plugins, author reviewable patch ESPs, create or remove records, edit leveled lists and composed structs, diff and resolve, edit NIF meshes and facegen, author dialogue, audit the SkyPatcher and SKSE runtime layers, compact or merge plugins, drive Papyrus compile/decompile and BSA archives, and look mods up on Nexus. Also the router for the bundled Skyrim helper skills (mutagen-reference, papyrus-reference, biped-slot-reference, skypatcher-authoring, spid-authoring, kid-authoring, dialogue-authoring, facegen-diagnostics, oar-authoring, skse-plugin-authoring, papyrus-optimization, tool-output-awareness, bulk-record-jobs). Use whenever the user mentions houseCARL, an MO2 modlist, plugins, load order, conflicts, ESP patches, overrides, a record type (ARMO/WEAP/NPC_/LVLI/MGEF/…), leveled lists, keywords, facegen or dark faces, dialogue, SKSE plugins, Papyrus, BSA archives, Nexus, or a no-ESP runtime distribution — even when the task looks like a single edit, load this first to pick the right tool and read before you write.
---

# houseCARL

Use this skill for data-layer Skyrim Special Edition modding through the configured houseCARL MCP server. houseCARL reads a Mod Organizer 2 instance, resolves the true load-order winner, and writes changes into reviewable patch plugins; original source mods are never edited. Beyond the record read/write core it reaches the whole data layer — assets and NIF meshes, the SkyPatcher and SKSE runtime layers, dialogue, plugin operations, Papyrus compile/decompile, BSA archives, and keyless Nexus lookups — plus the 13 focused helper skills below for the specialist grammars and workflows.

## Core workflow (read before you write)

1. Confirm context when it matters:
   - `housecarl_load_order_status` for profile/plugin status, or to check whether a mod or plugin is active.
   - `housecarl_set_mo2_instance` when the user gives a new MO2 instance folder.
2. Read before any record write:
   - `mutagen-reference` to verify field names, writability, enum values, and composed-struct shapes.
   - `housecarl_read_record` or `housecarl_batch_record_detail` to inspect the current winner. Add `conflict_tree=true` for contested records or when winner provenance matters.
   - `housecarl_cross_plugin_query` to locate records or references across the load order (page big results with `offset=`).
3. Pick the narrowest write tool:
   - `housecarl_set_field` for a single scalar or simple-collection edit.
   - `housecarl_bulk_apply` for several edits in one patch, dict merges, leveled-list entries, effects, or other composed structs.
   - `housecarl_create_record` (or `housecarl_bulk_create` for many at once) for a new top-level record — it needs an EditorID.
   - `housecarl_remove_record` only to drop a record or override from a houseCARL-owned patch — never from a source mod.
4. Accumulate related edits into one patch with `into=<patch filename>` after the first write returns a patch name.
5. Prefer runtime, no-ESP INI systems when they fit the user's intent — `skypatcher-authoring`, `spid-authoring`, `kid-authoring` (see the helper skills below).

## The full tool surface

Beyond the core workflow, reach for the right group. Depth for the specialist areas lives in the helper skills (next section) — load the skill before composing in that area.

**Read / query / resolve**
- `housecarl_records` — the consolidated 2.0 read surface (SELECT × SOURCE × PROJECT in one call): record lists or scans, any plugin's version wherever it lives (active or on disk), form-scoped `project=` shapes. The 1.x read tools below keep working through the 2.0 build.
- `housecarl_read_record`, `housecarl_batch_record_detail` — read one or many records at the true winner (`conflict_tree=true` for provenance).
- `housecarl_read_plugin_file` — read a plugin directly, even one that is disabled or not active.
- `housecarl_cross_plugin_query` — query and filter records or references across the whole order; page with `offset=`, count with `group_by=`.
- `housecarl_resolve` — resolve a list of FormIDs to identity; `housecarl_diff_record` — diff two plugins' versions of a record.
- `housecarl_effect_chain` — trace a magic effect to every spell / enchantment / potion / scroll / ingredient that carries it.
- `housecarl_load_order_status` — enabled/disabled mods & plugins; `housecarl_check_errors` — dangling refs, missing masters, broken links.

**Runtime layers (what xEdit can't see)**
- `housecarl_skypatcher_read` — a record's true state after the SkyPatcher INI layer replays; `housecarl_skypatcher_layer` — the INIs, apply order, conflicts.
- `housecarl_skse_inventory` — SKSE-plugin DLLs, configs, provider/metadata; `housecarl_skse_config_audit` — config references vs the load order; `housecarl_native_pairing_audit` — native Papyrus declarations vs the DLLs implementing them.

**Write / author**
- `housecarl_apply` — the consolidated 2.0 field-write surface (one or many edits × the lane × the read-back in one call): `ops=` for field edits, `bundle=`+`assignments=` to copy a field bundle from one record onto another, and one lane spelling — a new patch, `into=` an existing one, or `in_place="X.esp"` naming the file you intend to overwrite. The 1.x write tools below keep working through the 2.0 build.
- `housecarl_set_field`, `housecarl_bulk_apply`, `housecarl_create_record`, `housecarl_bulk_create`, `housecarl_create_plugin` (header-only trigger plugin), `housecarl_remove_record`, `housecarl_forward_record` (copy-as-override, or revert to another plugin's version), `housecarl_validate_scripts` (unbound script properties).

**Dialogue** — `housecarl_validate_dialogue`, `housecarl_write_seq` (the start-game-enabled quest `.seq`). Depth: `dialogue-authoring`.

**Assets / NIF / facegen** — `housecarl_asset_status` (which mod/BSA wins a Data-relative path), `housecarl_place_asset` / `housecarl_bulk_place_asset` (make a chosen copy win MO2's VFS), `housecarl_nif_inspect` / `housecarl_nif_set` (read/write mesh data values). Depth: `facegen-diagnostics`.

**Plugin operations** — `housecarl_compact_plugin` (ESL-renumber, carries FormID-keyed facegen/voice along), `housecarl_merge_plugins`, `housecarl_copy_npc_appearance` (standalone appearance, no donor master).

**Papyrus / SKSE code** — `housecarl_compile_script` (`.psc` → `.pex`), `housecarl_decompile_script` (`.pex` → `.psc`). Depth: `papyrus-reference`, `papyrus-optimization`, `skse-plugin-authoring`.

**BSA archives** — `housecarl_bsa_list`, `housecarl_bsa_extract`, `housecarl_bsa_repack`.

**Nexus (keyless, no browser)** — `housecarl_nexus_search`, `housecarl_nexus_mod`, `housecarl_nexus_check_updates`, `housecarl_nexus_identify`, `housecarl_nexus_graphql`. For a whole-order update check, start with `housecarl_update_status` (MO2's local update cache, no network), then confirm with `housecarl_nexus_check_updates`.

**Setup** — `housecarl_set_mo2_instance`, `housecarl_set_tool_path` (point houseCARL at an external tool: the Papyrus compiler, BSArch, or the crash / Papyrus log folders).

## Bundled helper skills

Load the specialist skill before composing in its domain:

- **Reference** — `mutagen-reference` (record schemas), `papyrus-reference` (Papyrus / SKSE function signatures), `biped-slot-reference` (armor by biped slot).
- **Runtime distribution grammars** — `skypatcher-authoring` (record edits), `spid-authoring` (spells / perks / items / factions / outfits → NPCs), `kid-authoring` (keywords → items).
- **Content authoring / investigation** — `dialogue-authoring`, `facegen-diagnostics` (the dark-face NPC bug), `oar-authoring` (Open Animation Replacer), `skse-plugin-authoring` (C++ SKSE plugin DLLs).
- **Performance & guardrail** — `papyrus-optimization` (script cost review), `tool-output-awareness` (keep generated tool output out of authored patches).
- **Bulk planning** — `bulk-record-jobs` (catalogues, audits, link graphs, conflict surveys, fan-out extraction — many records into one structured deliverable).

## FormID notes

houseCARL tools use `XXXXXX:Plugin.esp` FormIDs — six hex digits, then the filename of the master that defines the record. SkyPatcher, SPID, and KID each use their own FormID syntax; consult their skills before writing INI lines.

## Safety notes

- houseCARL patches are reviewable output mods. Tell the user which patch was created or extended.
- Don't invent schemas or field paths. If `mutagen-reference` has no entry for a type, say so directly rather than guessing.
- Don't reach for record edits when the user explicitly wants a no-ESP / runtime distribution file — use SkyPatcher, SPID, or KID instead.
- Never edit a source mod in place unless the user has explicitly opted into the in-place lane.
