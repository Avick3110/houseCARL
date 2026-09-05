using System.Text.Json;

namespace HousecarlMcp;

/// <summary>The alias layer's data: the old → new parameter table, plus migration hints and retired tool
/// names. This is temporary build scaffolding, deleted at 2.0.0; the machinery in <see cref="ToolCallShim"/>
/// is permanent. Every entry is schema-gated by construction — a rename fires only when the tool does not
/// declare the old spelling and does declare the candidate, so a row for a not-yet-shipped name is inert.</summary>
internal static class AliasTable
{
    /// <summary>One directed rename: an old spelling to the candidates it may become, in priority order. The
    /// first candidate the tool declares decides; if it is declared but already supplied the entry stops rather
    /// than falling through, since reinterpreting a stray onto another axis is how a wrong guess binds silently.
    /// Names are stored normalized (lowercase, underscores stripped), so case variants need no entry.
    /// <paramref name="ExceptTools"/> suppresses a candidate on tools where that word means something else.</summary>
    internal readonly record struct Rename(string Old, string[] Candidates, (string Tool, string Candidate)[]? ExceptTools = null);

    /// <summary>The rename rows, in both directions: old callers must keep binding on renamed tools and new
    /// vocabulary must already bind on not-yet-renamed ones. A parameter that became grammar rather than a new
    /// name is not a rename — those live in <see cref="Dissolutions"/>.</summary>
    static readonly Rename[] Renames =
    {
        // formid/formids is set-valued on 2.0; symmetric so singular tools accept a plural guess and vice versa.
        new("formid",  new[] { "formids" }),
        new("formids", new[] { "formid" }),

        // plugin (whose-version) becomes source, never a {file, mod} form. On a tool declaring both source and
        // plugins, source wins by order. The four plugin spellings stay a full clique among themselves — callers
        // pair them every way. housecarl_place needed an exception while its source= was a top-level file path;
        // it declares no top-level source= now (a source is a member of assets=), so the rows are schema-gated
        // off there by construction and the exception is gone with the parameter.
        new("plugin",  new[] { "source", "plugins", "pluginname", "pluginnames" }),
        // `source` is a candidate here so the row still has a route on a tool that declares no plugin spelling
        // at all; it goes last so a tool with a scope word keeps getting `plugins`.
        new("plugins", new[] { "plugin", "pluginname", "pluginnames", "source" }),
        // plugin_name becomes patch, else a plugin scope word. `patch` is suppressed on write_seq: there it names
        // the OUTPUT FOLDER, so plugin_name= would silently put the .seq in a folder named after the plugin.
        // `source` goes last so a tool declaring both a scope word and the pole keeps getting the scope word.
        // `patch` is suppressed on the two tools where it names an output FOLDER rather than a plugin: write_seq
        // and place. On both, plugin_name= would silently name the folder after a plugin.
        new("pluginname",  new[] { "patch", "plugins", "plugin", "pluginnames", "source" },
            ExceptTools: new[] { (ToolNames.WriteSeq, "patch"), (ToolNames.Place, "patch") }),
        new("pluginnames", new[] { "plugins", "plugin", "pluginname", "source" }),
        // Reverse: the pole spelling on not-yet-renamed tools. nexus_mod is excepted — its mod= is a Nexus mod
        // ID, not the provider disambiguator, and the Nexus tools are not part of the rename.
        new("source", new[] { "plugin", "mod" },
            ExceptTools: new[] { (ToolNames.NexusMod, "mod") }),

        // Selection is set-valued types; record_type stays a create operand and normalizes distinctly, so it
        // needs no entry here.
        new("type",  new[] { "types" }),
        new("types", new[] { "type" }),

        // One name for the new artifact: patch. Candidate order matters on tools declaring several output names —
        // merge_plugins declares patch_name and output (patch= must reach output), bsa_repack declares patch_name
        // and archive_name (patch= must reach the .bsa). Hence output, then archivename, before patchname.
        // `patchname` also falls through to `into` because it is the habitual output spelling on every write
        // tool, so it must reach removal's into=. `archivename`/`output` do not: each names one specific tool's
        // artifact, neither of which is a removal habit.
        new("patchname",   new[] { "patch", "into" }),
        new("archivename", new[] { "patch" }),
        new("output",      new[] { "patch" }),
        // `into` is last: the artifact a removal edits already exists, so on the remove tool a bare patch= means
        // into=. Every tool with a new-artifact spelling declares one of the four candidates above first.
        new("patch",       new[] { "output", "archivename", "patchname", "pluginname", "into" }),

        // Unmanaged filesystem output: out_path. output_dir= (bsa_extract) and dest= (compile_script) name the
        // same concept, so all three stay a clique.
        new("outputdir", new[] { "outpath", "dest" }),
        new("dest",      new[] { "outpath", "outputdir" }),
        new("outpath",   new[] { "outputdir", "dest" }),

        // One verb name (op) and the bulk list (ops).
        new("verb", new[] { "op" }),
        new("op",   new[] { "verb" }),
        new("operations", new[] { "ops" }),
        new("ops",        new[] { "operations" }),

        // readback absorbs full_readback; filter absorbs load_order_status's lookup.
        new("fullreadback", new[] { "readback" }),
        new("readback",     new[] { "fullreadback" }),
        new("lookup", new[] { "filter" }),
        new("filter", new[] { "lookup" }),

        // nif_set's target= becomes the NIF element. It must NOT map to in_place: that would let a bare target=
        // engage the opt-in in-place lane, which no call may enter without spelling it, and on compact_plugin
        // (bool in_place=, no target=) it would turn a named-unknown refusal into a type error. The in-place
        // spellings are ToolCallShim.LaneCorrections' job instead. target_formid is likewise not renamed onto
        // target, since target= is the in-place filename on the write tools; it rides the hint below.
        new("target", new[] { "element" }),

        // from_plugin and the mod= disambiguator both become source.
        new("fromplugin", new[] { "source" }),
        new("mod", new[] { "source" }),

        // Set-valued file selection per substrate: paths. The per-tool spellings are disjoint, so schema-gating
        // picks the right one for the reverse row.
        new("assetpaths", new[] { "paths" }),
        new("meshpaths",  new[] { "paths" }),
        new("meshpath",   new[] { "paths" }),
        new("paths", new[] { "assetpaths", "meshpaths", "meshpath", "script", "pex" }),

        // bsa_repack's repack input.
        new("sourcefolder", new[] { "fromfolder" }),
        new("fromfolder",   new[] { "sourcefolder" }),
    };

    /// <summary>The full tables, for the test that runs the resolver over the real published schemas and
    /// asserts per row exactly where it activates.</summary>
    internal static IReadOnlyList<Rename> AllRenames => Renames;
    internal static IReadOnlyList<Dissolution> AllDissolutions => Dissolutions;

    /// <summary>Directed lookup: the candidates for a normalized old spelling, or null.</summary>
    internal static Rename? RenameFor(string normalizedKey)
    {
        foreach (var r in Renames)
            if (r.Old == normalizedKey) return r;
        return null;
    }

    /// <summary>Whether a rename entry suppresses a candidate on this registered tool.</summary>
    internal static bool IsExcluded(in Rename entry, string candidate, string? toolName)
    {
        if (entry.ExceptTools is null || toolName is null) return false;
        foreach (var (tool, cand) in entry.ExceptTools)
            if (cand == candidate && tool == toolName) return true;
        return false;
    }

    /// <summary>A dissolution: the old parameter became grammar (a pole, a predicate, the walk), so no rename
    /// can express it and the unknown-parameter refusal carries a migration hint instead. Each hint is gated on
    /// the replacement grammar's carrier parameters — ALL of them must be declared by the tool — so it never
    /// fires as noise on a tool that never had the old parameter. Names normalized.
    /// Wording: a hint can fire on a tool that merely happens to declare the carrier, so it must never claim the
    /// old parameter was retired — it states concretely where the job lives, with no bare ellipses.</summary>
    internal readonly record struct Dissolution(string Old, string[] GateParams, string Hint);

    static readonly Dissolution[] Dissolutions =
    {
        new("editoridcontains", new[] { "where" }, "not a parameter here — this job is the where= predicate grammar: where=[\"editorid contains <text>\"]"),

        // The flat spellings below became sub-parameters of the project= form object, so a stray flat one gets
        // the form-scoping rule by name. All gated on `project`.
        new("conflicttree", new[] { "project" }, "not a parameter here — the provider tree is a PROJECT form: project={\"form\": \"tree\"}"),
        new("fields",       new[] { "project" }, "not a parameter here — field paths live inside the form: project={\"form\": \"fields\", \"fields\": [\"<path>\", …]}"),
        new("depth",        new[] { "project" }, "not a parameter here — depth lives inside the fields/everything forms: project={\"form\": \"fields\", \"fields\": […], \"depth\": <n>}"),
        new("groupby",      new[] { "project" }, "not a parameter here — aggregation is a PROJECT form: project={\"form\": \"aggregate\", \"group_by\": \"winner\"}"),
        new("resolvenames", new[] { "project" }, "not a parameter here — display annotation lives inside the fields/everything forms: project={\"form\": \"fields\", \"fields\": […], \"resolve_names\": true}"),
        new("propertycontains", new[] { "where", "findings" }, "not a parameter here — script-property predicates belong to the where= grammar: where=[\"<property path> contains <text>\"]"),
        new("mgefformid",   new[] { "walk" }, "not a parameter here — seed the walk= construct with the MGEF's FormID instead"),
        new("closure",      new[] { "walk" }, "not a parameter here — the closure copy is expressed as the walk= construct"),
        new("winnerfields", new[] { "fieldssource" }, "not a parameter here — the display pole spells it: fields_source=\"winner\""),
        new("fromfile",     new[] { "ops" }, "not a parameter here — pass the file with the @file convention: ops=\"@<path>\""),
        new("plugina", new[] { "versus" }, "not a parameter here — the comparison poles are source= (subject) and versus= (reference)"),
        new("pluginb", new[] { "versus" }, "not a parameter here — the comparison poles are source= (subject) and versus= (reference)"),
        new("moda",    new[] { "versus" }, "not a parameter here — structured source=/versus= poles carry the mod disambiguator"),
        new("modb",    new[] { "versus" }, "not a parameter here — structured source=/versus= poles carry the mod disambiguator"),
        new("sourceformid", new[] { "assignments" }, "not a parameter here — the assignments= zip carries from= per assignment"),
        new("sourceplugin", new[] { "assignments" }, "not a parameter here — the assignments= zip carries from_source= per assignment"),
        new("sourcemod",    new[] { "assignments" }, "not a parameter here — the assignments= zip carries from_source= per assignment"),
        new("targetformid", new[] { "assignments" }, "not a parameter here — the assignments= zip carries target= per assignment (§5.2's record pole)"),

        // The five per-record create operands became members of a records= element, so a caller spelling them at
        // the top level gets the one-hop form. Gated on `records` — the activation set is whatever tools declare
        // that, which is worth checking rather than assuming. (`operations` needs no row: it is a live rename to
        // ops=, and inside a records element that is where it lands.)
        new("recordtype",  new[] { "records" }, "not a parameter here — one record is a set of one: records=[{\"record_type\": \"Keyword\", \"editorid\": \"MyKeyword\"}]"),
        new("editorid",    new[] { "records" }, "not a parameter here — the editorid belongs to its record: records=[{\"record_type\": \"…\", \"editorid\": \"…\"}]"),
        new("parent",      new[] { "records" }, "not a parameter here — nesting is per record: records=[{…, \"parent\": \"XXXXXX:Plugin.esp\"}] (a parent may also be an EARLIER sibling's editorid)"),
        new("collection",  new[] { "records" }, "not a parameter here — the child-list is per record: records=[{…, \"parent\": \"…\", \"collection\": \"Persistent\"}]"),
        new("grid",        new[] { "records" }, "not a parameter here — the exterior-cell grid is per record: records=[{\"record_type\": \"Cell\", …, \"grid\": \"5,-12\"}]"),
    };

    /// <summary>The migration hint for a normalized unknown key, or null — gated on EVERY one of the
    /// entry's carrier parameters being declared in <paramref name="declaredNormalized"/>.</summary>
    internal static string? DissolutionHint(string normalizedKey, IReadOnlySet<string> declaredNormalized)
    {
        foreach (var d in Dissolutions)
            if (d.Old == normalizedKey && d.GateParams.All(declaredNormalized.Contains)) return d.Hint;
        return null;
    }

    // ---- retired tool names ----

    /// <summary>Retired tool names mapped to their successor call shape, turning the SDK's generic unknown-tool
    /// error into a one-hop redirect. A row only ever answers a call naming a tool the server does NOT register,
    /// because the SDK resolves registered names before the shim runs.</summary>
    static readonly (string Old, string Successor)[] RetiredTools =
    {
        // The `Old` column is a literal, never a ToolNames constant: the redirect has to outlive the tool it
        // redirects away from, so deleting that tool must not delete this row.
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

        // The SKSE layer. Three families of one substrate, so all three rows name the same tool and differ only in the
        // findings= value; the declared-vs-runtime ceiling they each used to carry is now written once on that tool.
        ("housecarl_skse_inventory",
         "absorbed into " + ToolNames.Skse + ": findings=\"inventory\", which is also the DEFAULT when findings= is omitted. filter=, peek= and max_chars= are unchanged, and peek= still requires filter=."),
        ("housecarl_native_pairing_audit",
         "absorbed into " + ToolNames.Skse + ": findings=\"pairing\". filter= and max_chars= are unchanged; peek= belongs to findings=\"inventory\" and is refused here."),
        ("housecarl_skse_config_audit",
         "absorbed into " + ToolNames.Skse + ": findings=\"config\". filter= and max_chars= are unchanged; peek= belongs to findings=\"inventory\" and is refused here."),

        // The write side. Each redirect names the parameter migration too, since a caller arriving from old docs
        // has the old parameter habits as well as the old tool name.
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
        // The derived-findings sweeps. validate_dialogue's successor teaching names TWO destinations: the finding
        // classes went to the merged check surface, but the effective merged INFO order is an ordered sequence
        // rather than a finding and lives on records — a row naming only the sweep would send the caller asking
        // "why does the wrong line play" to a surface that deliberately does not answer it.
        ("housecarl_check_errors",
         "absorbed into " + ToolNames.Check + ": the same sweep is findings=[\"errors\"], which is also the DEFAULT when findings= is omitted. type=/formids=/editorid_contains=/exclude=/counts_only=/limit=/max_chars=/format= are unchanged; the response is sectioned per family and states which families it did not run."),
        ("housecarl_validate_scripts",
         "absorbed into " + ToolNames.Check + ": findings=[\"scripts\"] (or a class inside it — 'unbound_object', 'unbound_scalar', 'unbound', 'bound_null'). property_contains= and the record scope are unchanged, exclude= now scopes this family too, and the record listing is under the max_chars bound it was not under before."),

        ("housecarl_validate_dialogue",
         "split in two. The findings are " + ToolNames.Check + " findings=[\"dialogue\"] with seeds= taking the same DIAL / QUST / DLVW / DLBR FormIDs — graph and branch wiring, LinkTo and previous-link targets, .fuz files, result scripts, CK-parity subrecords, malformed conditions and the .seq check — and limit= caps how many SEEDS one call expands. The effective merged INFO order is " + ToolNames.Records + " project={\"form\": \"info_order\"} with the DIAL in formids=: it is an ordered sequence rather than a finding, and the sweep's dialogue boundary says so."),

        // The S2 write fold. Both old names carry the destination-shape migration as well as the tool name: the
        // set of destinations is one parameter now, and the single tool's "one file per call" restriction is gone.
        ("housecarl_place_asset",
         "absorbed into " + ToolNames.Place + ": one destination is a set of one — assets=[{path|formid, kind?, source?, source_provider?}] (asset_path= is the member's path=). A formid with NO kind now places BOTH FaceGen files, so kind= is no longer required. patch_name= is patch=, and source_provider=/kind= can also be given once for the whole set."),
        ("housecarl_bulk_place_asset",
         "absorbed into " + ToolNames.Place + ": assets= is unchanged in shape except asset_path= is path=, and source_provider=/kind= may now be given once for the whole set instead of per member. patch_name= is patch=; into= is unchanged."),

        // The standalone NPC copy was one verb over two substrates, so its successor is two calls and the row says
        // both — a row naming only the record half would leave the caller with a face and no FaceGen.
        ("housecarl_copy_npc_appearance",
         "split in two. The records are " + ToolNames.Copy + ": source_formid= is from=, seed_paths=[\"HeadParts\", \"HairColor\", \"HeadTexture\", \"WornArmor\"] walks the appearance, exclude_types=[\"Race:refuse\"] keeps the walk out of the race, and new_editorid=/target= are the same two destinations (source_plugin=+source_mod= is from_source=, an ORDERED list — name the override then the defining plugin; patch_name= is patch=). new_name= has no parameter: a clone carries the DONOR's display name, and renaming it is an op on Name in the " + ToolNames.Apply + " call that copies the tint bundle. The FaceGen and textures are " + ToolNames.Place + ": the copy lists the asset paths and names, per source, the MO2 MOD FOLDER it read from — pass that folder as source_provider= and the files come out of it even when MO2 does not load that mod."),

        ("housecarl_forward_record",
         "absorbed into " + ToolNames.Forward + ": from_plugin= is source= (an ACTIVE plugin — whose version to copy). patch_name= is patch=, full_readback= is readback=, and the target=+in_place=true pair is in_place=\"X.esp\". formids=, dry_run= and the into= replace-on-collision semantics are unchanged."),
    };

    /// <summary>The retired-name rows, for the test that holds them against the surface they redirect into: a
    /// merged surface gaining a family whose ancestor has no row here is what it makes visible.</summary>
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
