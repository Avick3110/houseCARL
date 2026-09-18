namespace HousecarlMcp;

/// <summary>The retired tool names and the successor each call is refused with, in words; nothing is accepted or
/// executed, and the rows have no removal date.</summary>
internal static class AliasTable
{
    /// <summary>Retired tool names mapped to their successor call shape; a row only ever answers a call naming a tool
    /// the server does not register.</summary>
    static readonly (string Old, string Successor)[] RetiredTools =
    {
        // The `Old` column is a literal, never a ToolNames constant: the redirect outlives the tool it names.
        ("housecarl_read_record",
         "absorbed into " + ToolNames.Records + ": formids=[\"XXXXXX:Plugin.esp\"] — project={\"form\": \"fields\", \"fields\": […]} for named fields, \"everything\" for the full body; plugin= is source=; conflict_tree=true is project={\"form\": \"tree\"}."),
        ("housecarl_batch_record_detail",
         "absorbed into " + ToolNames.Records + ": the same formids= list with a project= form (summary | fields | everything); plugin= is source=; to_file=/@file re-entry unchanged."),
        ("housecarl_resolve",
         "absorbed into " + ToolNames.Records + ": formids=[…] with project={\"form\": \"identity\"} — the labeling form."),
        ("housecarl_cross_plugin_query",
         "absorbed into " + ToolNames.Records + ": the same scan terms, set-valued — type= is types=, editorid_contains= is where=[\"editorid contains …\"], group_by= is project={\"form\": \"aggregate\", \"group_by\": …}, fields= lives inside project={\"form\": \"fields\"}."),
        ("housecarl_read_plugin_file",
         "absorbed into " + ToolNames.Records + ": source=\"X.esp\" reads that plugin WHEREVER it lives (active or on disk out of the order — the response states which); types=/where= scan the file's records; formids= reads specific ones."),
        ("housecarl_diff_record",
         "absorbed into " + ToolNames.Records + ": project={\"form\": \"delta\"} — source= is the subject (was plugin_a), versus= the reference (was plugin_b; structured poles carry the mod disambiguator), project.fields narrows the comparison."),
        ("housecarl_effect_chain",
         "absorbed into " + ToolNames.Records + ": project={\"form\": \"chain\"} with walk={\"direction\": \"reverse\", \"follow\": \"Effects[].BaseEffect\"} and the MGEF in formids= (types= still narrows the carrier types)."),
        ("housecarl_skypatcher_read",
         "absorbed into " + ToolNames.Records + ": source={\"overlay\": \"skypatcher\", \"state\": \"post\"} reads the post-INI body; pre-vs-post is project={\"form\": \"delta\"} with the two overlay poles."),

        // The SKSE layer: three families of one substrate, so all three rows differ only in the findings= value.
        ("housecarl_skse_inventory",
         "absorbed into " + ToolNames.Skse + ": findings=\"inventory\", which is also the DEFAULT when findings= is omitted. filter=, peek= and max_chars= are unchanged, and peek= still requires filter=."),
        ("housecarl_native_pairing_audit",
         "absorbed into " + ToolNames.Skse + ": findings=\"pairing\". filter= and max_chars= are unchanged; peek= belongs to findings=\"inventory\" and is refused here."),
        ("housecarl_skse_config_audit",
         "absorbed into " + ToolNames.Skse + ": findings=\"config\". filter= and max_chars= are unchanged; peek= belongs to findings=\"inventory\" and is refused here."),

        // The write side; each redirect names the parameter migration too.
        ("housecarl_set_field",
         "absorbed into " + ToolNames.Apply + ": one op is a set of one — ops=[{formid, field_path, value}] (verb= is op=). patch_name= is patch=, full_readback= is readback=, and the target=+in_place=true pair is in_place=\"X.esp\" (the file being overwritten)."),
        ("housecarl_bulk_apply",
         "absorbed into " + ToolNames.Apply + ": operations= is ops= (verb= is op=), and from_file= is the @file convention — ops=\"@<absolute path>\". patch_name= is patch=, full_readback= is readback=, target=+in_place=true is in_place=\"X.esp\". Copying a field bundle BETWEEN records is bundle= + assignments=."),

        ("housecarl_create_record",
         "absorbed into " + ToolNames.Create + ": one record is a set of one — records=[{record_type, editorid, ops}] (operations= is ops=, verb= is op=). record_type/editorid/parent/collection/grid are members of the record, not top-level arguments. patch_name= is patch=, full_readback= is readback=, and the target=+in_place=true pair is in_place=\"X.esp\" (the file being written into)."),
        ("housecarl_bulk_create",
         "absorbed into " + ToolNames.Create + ": records= is unchanged in shape except operations= is ops= (verb= is op=), and it also accepts \"@<absolute path>\". The nested one-shot is unchanged: declare a parent BEFORE the children whose parent= names its editorid, and '@editorid' still references a same-call sibling. patch_name= is patch=, full_readback= is readback=, target=+in_place=true is in_place=\"X.esp\"."),
        ("housecarl_remove_record",
         "absorbed into " + ToolNames.Remove + ": formids= is SET-VALUED — drop many records in one re-serialize (one is a set of one). The houseCARL-patch lane is into=\"MyPatch.esp\" (removal edits an artifact that EXISTS; patch= names a NEW one everywhere else on the surface), and the target=+in_place=true pair is in_place=\"X.esp\"."),
        // The derived-findings sweeps; validate_dialogue's row names two destinations, the sweep and records.
        ("housecarl_check_errors",
         "absorbed into " + ToolNames.Check + ": the same sweep is findings=[\"errors\"], which is also the DEFAULT when findings= is omitted. type= is SET-VALUED types= (one type is a set of one; a set sweeps their union); formids=/editorid_contains=/exclude=/counts_only=/limit=/max_chars=/format= are unchanged; the response is sectioned per family and states which families it did not run."),
        ("housecarl_validate_scripts",
         "absorbed into " + ToolNames.Check + ": findings=[\"scripts\"] (or a class inside it — 'unbound_object', 'unbound_scalar', 'unbound', 'bound_null'). property_contains= and the record scope are unchanged, exclude= now scopes this family too, and the record listing is under the max_chars bound it was not under before."),

        ("housecarl_validate_dialogue",
         "split in two. The findings are " + ToolNames.Check + " findings=[\"dialogue\"] with seeds= taking the same DIAL / QUST / DLVW / DLBR FormIDs — graph and branch wiring, LinkTo and previous-link targets, .fuz files, result scripts, CK-parity subrecords, malformed conditions and the .seq check — and limit= caps how many SEEDS one call expands. The effective merged INFO order is " + ToolNames.Records + " project={\"form\": \"info_order\"} with the DIAL in formids=: it is an ordered sequence rather than a finding, and the sweep's dialogue boundary says so."),

        // The place fold: both rows carry the destination-shape migration as well as the tool name.
        ("housecarl_place_asset",
         "absorbed into " + ToolNames.Place + ": one destination is a set of one — assets=[{path|formid, kind?, source?, source_provider?}] (asset_path= is the member's path=). A formid with NO kind now places BOTH FaceGen files, so kind= is no longer required. patch_name= is patch=, and source_provider=/kind= can also be given once for the whole set."),
        ("housecarl_bulk_place_asset",
         "absorbed into " + ToolNames.Place + ": assets= is unchanged in shape except asset_path= is path=, and source_provider=/kind= may now be given once for the whole set instead of per member. patch_name= is patch=; into= is unchanged."),

        // The standalone NPC copy spanned two substrates, so its successor is two calls and the row names both.
        ("housecarl_copy_npc_appearance",
         "split in two. The records are " + ToolNames.Copy + ": source_formid= is from=, seed_paths=[\"HeadParts\", \"HairColor\", \"HeadTexture\", \"WornArmor\"] walks the appearance, exclude_types=[\"Race:refuse\"] keeps the walk out of the race, and new_editorid=/target= are the same two destinations (source_plugin=+source_mod= is from_source=, an ORDERED list — name the override then the defining plugin; patch_name= is patch=). new_name= has no parameter: a clone carries the DONOR's display name, and renaming it is an op on Name in the " + ToolNames.Apply + " call that copies the tint bundle. The FaceGen and textures are " + ToolNames.Place + ": the copy lists the asset paths and, for a source that resolved into an MO2 MOD FOLDER, names that folder — pass it as source_provider= and the files come out of it even when MO2 does not load that mod. A source with no such folder says so rather than naming one."),

        ("housecarl_forward_record",
         "absorbed into " + ToolNames.Forward + ": from_plugin= is source= (an ACTIVE plugin — whose version to copy). patch_name= is patch=, full_readback= is readback=, and the target=+in_place=true pair is in_place=\"X.esp\". formids=, dry_run= and the into= replace-on-collision semantics are unchanged."),
    };

    /// <summary>The retired-name rows, for the test that holds them against the surface they redirect into.</summary>
    internal static IReadOnlyList<(string Old, string Successor)> AllRetiredTools => RetiredTools;

    /// <summary>The successor teaching for a retired tool name, or null. Case-insensitive on the full name.</summary>
    internal static string? RetiredToolHint(string? toolName)
    {
        if (string.IsNullOrEmpty(toolName)) return null;
        foreach (var (old, successor) in RetiredTools)
            if (old.Equals(toolName.Trim(), StringComparison.OrdinalIgnoreCase))
                return $"error: {old} is not on this surface — {successor}";
        return null;
    }
}
