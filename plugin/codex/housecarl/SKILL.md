---
name: housecarl
description: Work with Skyrim Special Edition load-order records through the houseCARL MCP server — set or switch the MO2 instance, inspect active plugins and conflict trees, read records, query across plugins, author reviewable patch ESPs, create or remove records, and edit leveled lists or composed structs. Also the routing skill for the bundled Skyrim helpers (mutagen-reference, papyrus-reference, skypatcher-authoring, spid-authoring, kid-authoring). Use whenever the user mentions houseCARL, an MO2 modlist, plugins, load order, conflicts, ESP patches, overrides, a record type (ARMO/WEAP/NPC_/LVLI/MGEF/…), leveled lists, keywords, or a no-ESP runtime distribution — even when the task looks like a single edit, load this first to pick the right tool and read before you write.
---

# houseCARL

Use this skill for data-layer Skyrim Special Edition modding through the configured houseCARL MCP server. houseCARL reads a Mod Organizer 2 instance, resolves the true load-order winner, and writes changes into reviewable patch plugins; original source mods are never edited.

## Core workflow

1. Confirm context when it matters:
   - `housecarl_load_order_status` for profile/plugin status, or to check whether a mod or plugin is active.
   - `housecarl_set_mo2_instance` when the user gives a new MO2 instance folder.
2. Read before any record write:
   - `mutagen-reference` to verify field names, writability, enum values, and composed-struct shapes.
   - `housecarl_read_record` or `housecarl_batch_record_detail` to inspect the current winner. Add `conflict_tree=true` for contested records or when winner provenance matters.
   - `housecarl_cross_plugin_query` to locate records or references across the load order.
3. Pick the narrowest write tool:
   - `housecarl_set_field` for a single scalar or simple-collection edit.
   - `housecarl_bulk_apply` for several edits in one patch, dict merges, leveled-list entries, effects, or other composed structs.
   - `housecarl_create_record` for a new top-level record (it needs an EditorID).
   - `housecarl_remove_record` only to drop a record or override from a houseCARL-owned patch — never from a source mod.
4. Accumulate related edits into one patch with `into=<patch filename>` after the first write returns a patch name.
5. Prefer runtime, no-ESP INI systems when they fit the user's intent:
   - `skypatcher-authoring` for SkyPatcher record edits.
   - `spid-authoring` for distributing spells, perks, items, factions, outfits, or packages to NPCs.
   - `kid-authoring` for distributing keywords onto items.
6. `papyrus-reference` before answering any Papyrus or SKSE function-signature question.

## FormID notes

houseCARL tools use `XXXXXX:Plugin.esp` FormIDs — six hex digits, then the filename of the master that defines the record. SkyPatcher, SPID, and KID each use their own FormID syntax; consult their skills before writing INI lines.

## Safety notes

- houseCARL patches are reviewable output mods. Tell the user which patch was created or extended.
- Don't invent schemas or field paths. If `mutagen-reference` has no entry for a type, say so directly rather than guessing.
- Don't reach for record edits when the user explicitly wants a no-ESP / runtime distribution file — use SkyPatcher, SPID, or KID instead.
