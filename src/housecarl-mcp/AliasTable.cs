using System.Text.Json;

namespace HousecarlMcp;

/// <summary>
/// The 2.0 alias layer's configuration — SPEC §5.3's old → new table, inverted out of
/// <see cref="ToolCallShim"/>'s hand-wired SynonymGroups (tool-surface-2.0 W0, CHARTER_PHASE4 §4).
///
/// <para><b>BUILD SCAFFOLDING — REMOVED AT 2.0.0 (clean cut, CHARTER_PHASE4 §3.4a).</b> During the
/// 2.0 build waves (W0→W6) this table lets each wave land its renames on <c>main</c> non-breaking:
/// old spellings keep binding on renamed tools, new spellings already bind on not-yet-renamed ones.
/// At 2.0.0 this file is deleted (the old → new table ships in the changelog instead); the
/// schema-driven machinery in <see cref="ToolCallShim"/> — underscore/case normalization, shape
/// coercion, named refusals — is permanent and stays.</para>
///
/// <para>Every entry is <b>schema-gated by construction</b>: a rename fires only when the old
/// spelling is NOT declared by the tool and the candidate IS declared and not already supplied
/// (see <see cref="ToolCallShim"/>.ResolveAliases). So a row whose new name no wave has shipped yet
/// is dormant — the table can carry the whole §5.3 census from day one without changing any
/// current tool's behaviour, and each wave "activates" its rows simply by renaming parameters.</para>
/// </summary>
internal static class AliasTable
{
    /// <summary>One directed rename: an old spelling → the canonical candidates it may become, in
    /// priority order (the FIRST candidate the tool declares decides; if that candidate is declared
    /// but already supplied, the entry deliberately does NOT fall through to a lower-priority
    /// candidate — reinterpreting a stray onto a different axis than its primary meaning is how a
    /// wrong guess binds silently). Names are stored normalized (lowercase, underscores stripped —
    /// <see cref="ToolCallShim"/>.Normalize); underscore/case variants need no entry at all.
    /// <paramref name="ExceptTools"/> suppresses a specific candidate on tools where the canonical
    /// word already means something else (registered tool name → suppressed candidate).</summary>
    internal readonly record struct Rename(string Old, string[] Candidates, (string Tool, string Candidate)[]? ExceptTools = null);

    /// <summary>The §5.3 rename rows (both directions where the row is a pure rename: old callers
    /// must keep binding on renamed tools, and new-vocabulary callers should already bind on
    /// not-yet-renamed ones — the build window is symmetric). Rows that DISSOLVE a parameter into
    /// grammar (§5.3's ⤳ rows) are not renames — they live in <see cref="Dissolutions"/>.</summary>
    static readonly Rename[] Renames =
    {
        // §5.3 row 1 — formid/formids: set-valued everywhere on 2.0; symmetric during the build
        // (today's singular tools keep accepting a plural guess and vice versa — the old SynonymGroups pair).
        new("formid",  new[] { "formids" }),
        new("formids", new[] { "formid" }),

        // §5.3 — plugin (whose-version, read tools) → source (the §4.2 pole). LOAD-BEARING (amended row,
        // Aaron-go 2026-07-29): maps to source, never to a {file, mod} form. On a tool declaring BOTH
        // source and plugins (2.0's records), source wins by order; on today's scan tools (plugins only)
        // this is #221's plugin→plugins tolerance, unchanged. EXCEPTION: place_asset's source= is a
        // file-path operand (the copy to place), not the version pole — a stray plugin= there must stay
        // a named unknown, not silently become a path.
        // The four 1.x spellings remain a full clique among themselves (PR #304 review F1: the old
        // SynonymGroups tolerated every pairing — plugin= bound on create_plugin's plugin_name, and
        // plugin_name= on the bare-plugin tools; dropping those edges was a live regression, restored here).
        new("plugin",  new[] { "source", "plugins", "pluginname", "pluginnames" },
            ExceptTools: new[] { (ToolNames.PlaceAsset, "source") }),
        // `source` last here for the same reason it is last on `pluginname` (PR #311 review 3 [low]): W3 PR 2 took
        // `plugin` off write_seq, and these two rows' only candidates were the three 1.x plugin spellings — so
        // BOTH dead-ended on the one tool whose pole they were reaching for, while `plugin=`, `plugin_name=`,
        // `mod=` and `from_plugin=` all still resolved. A row losing its route because its candidate ceased to
        // exist is a different failure from a row going dormant because the tool declares the word itself, and it
        // is the one worth catching.
        new("plugins", new[] { "plugin", "pluginname", "pluginnames", "source" },
            ExceptTools: new[] { (ToolNames.PlaceAsset, "source") }),
        // §5.3 — create_plugin's plugin_name → patch; today's guess-miss → plugins (#221 J3) or the bare-plugin
        // tools. `source` joins the list, and `patch` is suppressed on write_seq (PR #311 review [low]): once
        // write_seq declared source= AND patch=, the most likely 1.x spelling for THE PLUGIN on that tool became
        // the one word that could not reach the plugin pole — plugin_name= silently became the OUTPUT FOLDER's
        // name, so the .seq landed in a fresh folder called "MyQuestMod.esp" instead of the plugin's own houseCARL
        // folder, with nothing saying a rename happened. place_asset carries the same exception the sibling
        // plugin-naming rows do: its source= is a file-path operand, not the version pole.
        // `source` goes LAST, not ahead of `plugins`: on a tool declaring BOTH a scope word and a pole (records),
        // the 1.x guess-miss stays where W0 put it (#221 J3 — plugin_name -> plugins), and the pole is the
        // fallback for a tool that has no scope word at all. Which is exactly write_seq, once `patch` is
        // suppressed there.
        new("pluginname",  new[] { "patch", "plugins", "plugin", "pluginnames", "source" },
            ExceptTools: new[] { (ToolNames.WriteSeq, "patch"), (ToolNames.PlaceAsset, "source") }),
        new("pluginnames", new[] { "plugins", "plugin", "pluginname", "source" },
            ExceptTools: new[] { (ToolNames.PlaceAsset, "source") }),
        // Reverse: the new pole spelling on not-yet-renamed tools (read tools' plugin=, the NIF tools'
        // mod=). nexus_mod excepted (census catch): its mod= is a Nexus mod ID, not the S2/S3 provider
        // disambiguator — S8 is chartered untouched (§6.4), so nothing renames onto it.
        new("source", new[] { "plugin", "mod" },
            ExceptTools: new[] { (ToolNames.NexusMod, "mod") }),

        // §5.3 — type/types (SELECT is set-valued types; record_type stays a create operand — distinct
        // normalization, no entry needed).
        new("type",  new[] { "types" }),
        new("types", new[] { "type" }),

        // §5.3 — ONE name for "the new artifact": patch. Forward rows fire today on remove_record
        // (bare patch already declared there — the drift instance §5's census found); reverse row
        // lets patch= bind on today's patch_name / plugin_name / archive_name / output spellings.
        // Candidate order carries §5.3's "the new ARTIFACT" on tools declaring several output names
        // (final review finding 5, generalized by the census arm): merge_plugins declares patch_name
        // (mod-folder base) AND output (the merged plugin) — patch= must land on output; bsa_repack
        // declares patch_name AND archive_name (the repacked .bsa, §5.3's artifact there) — patch=
        // must land on archive_name. Hence output, then archivename, before patchname; everywhere
        // else the earlier candidates are undeclared and the order is inert.
        // `patchname` also falls through to `into` (PR #311 review 3 [low]): patch_name= is the output spelling on
        // EVERY 1.x write tool the caller has habits from (set_field, bulk_apply, create_record, bulk_create,
        // forward_record), so on housecarl_remove — where `patch=` now maps to into= — the sibling spelling
        // dead-ending was a split the caller has no way to predict. `archivename`/`output` deliberately do NOT get
        // it: each names ONE specific tool's artifact (bsa_repack's .bsa, merge_plugins' merged plugin), neither
        // of which is a removal habit, and a candidate list is a claim about what the word probably meant.
        new("patchname",   new[] { "patch", "into" }),
        new("archivename", new[] { "patch" }),
        new("output",      new[] { "patch" }),
        // `into` is the LAST candidate (W3 PR 2): on housecarl_remove the artifact a removal edits already
        // EXISTS, so 1.x remove_record's bare patch= names what §5.1 calls into= — and remove declares no
        // patch= for the row to skip. Every tool that has a "new artifact" spelling declares one of the four
        // candidates above, so this edge activates on exactly that one tool.
        new("patch",       new[] { "output", "archivename", "patchname", "pluginname", "into" }),

        // §5.3 — unmanaged filesystem output: out_path. The two 1.x spellings are also each other's
        // candidates (final review finding 6, the F1 clique logic): output_dir= on bsa_extract and
        // dest= on compile_script name the same concept and must keep binding during the build.
        new("outputdir", new[] { "outpath", "dest" }),
        new("dest",      new[] { "outpath", "outputdir" }),
        new("outpath",   new[] { "outputdir", "dest" }),

        // §5.3 — one verb-name (op) and the bulk list (ops).
        new("verb", new[] { "op" }),
        new("op",   new[] { "verb" }),
        new("operations", new[] { "ops" }),
        new("ops",        new[] { "operations" }),

        // §5.3 — readback absorbs full_readback; filter absorbs load_order_status's lookup.
        new("fullreadback", new[] { "readback" }),
        new("readback",     new[] { "fullreadback" }),
        new("lookup", new[] { "filter" }),
        new("filter", new[] { "lookup" }),

        // §5.2 — the target collision. Old nif_set target= becomes the NIF element. Deliberately NOT
        // mapped to in_place (PR #304 review F2/F3): a rename onto in_place would let a BARE target=
        // silently engage the opt-in in-place lane on a 2.0 write tool (the lane must never be entered
        // by a call that didn't spell it), and would fire today on compact_plugin (no target=, bool
        // in_place=), turning its named-unknown refusal into a type error about a key the caller never
        // sent. The 1.x in-place spellings are LaneCorrections' job instead: the complete pair
        // auto-maps, anything partial gets a naming correction (see ToolCallShim.LaneCorrections).
        // target_formid → target (§5.2(3)) is likewise NOT a mechanical rename (review F6): until the
        // copy successor ships, six write tools declare target= as the in-place FILENAME — renaming a
        // FormID onto it would answer with the in-place lane's refusal for a caller who never mentioned
        // that lane. It rides the assignments-gated hint below, like its source_formid siblings.
        new("target", new[] { "element" }),

        // §5.3 — forward_record's from_plugin and the S2/S3 mod= disambiguator both become source.
        // All three plugin-naming spellings carry the place_asset exception (its source= is a
        // file-path operand, not the version pole — final review finding 1: place_asset is the ONLY
        // tool declaring bare source today, so an unexcepted row is live exactly where it's wrong).
        new("fromplugin", new[] { "source" },
            ExceptTools: new[] { (ToolNames.PlaceAsset, "source") }),
        new("mod", new[] { "source" },
            ExceptTools: new[] { (ToolNames.PlaceAsset, "source") }),

        // §5.3 — set-valued file SELECT per substrate: paths. Forward rows dormant until W4; the
        // reverse row lets paths= bind on today's per-tool spellings (disjoint — schema-gating picks).
        new("assetpaths", new[] { "paths" }),
        new("meshpaths",  new[] { "paths" }),
        new("meshpath",   new[] { "paths" }),
        new("paths", new[] { "assetpaths", "meshpaths", "meshpath", "script", "pex" }),

        // §5.3 — bsa_repack's repack input (provisional pending §6's S5 verdict).
        new("sourcefolder", new[] { "fromfolder" }),
        new("fromfolder",   new[] { "sourcefolder" }),
    };

    /// <summary>The full tables, for the activation census (binding-shim-guard's CENSUS arm): the guard
    /// enumerates the REAL published schemas and asserts, per row, exactly where it activates — the
    /// executable form of "dormant by construction".</summary>
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

    /// <summary>A §5.3 ⤳ dissolution: the old parameter became GRAMMAR (a pole, a predicate, the walk),
    /// so no rename can express it — instead the unknown-parameter refusal carries a migration hint.
    /// Each hint is GATED on the replacement grammar's carrier parameter being declared by the tool,
    /// so it fires exactly when the tool's wave has landed (and never as noise on an unrelated tool
    /// that simply never had the old parameter). Names normalized.
    /// Wording contract (PR #304 review F4): a hint may fire on a 1.x tool that happens to declare the
    /// carrier (e.g. a validate_scripts spelling strayed onto where-bearing cross_plugin_query), so it
    /// must never claim the old parameter "was retired" — it states where the job lives, concretely,
    /// and the refusal's supported-parameter list does the rest. No bare ellipses: every hint spells a
    /// usable form.
    /// (conflict_tree's hint — deliberately absent at W0 — landed at W2 PR 1 gated on the `project`
    /// carrier, per the consumer-inventory obligation: the tree is a PROJECT form, not a parameter.)
    /// A hint may carry SEVERAL gate params — ALL must be declared. property_contains is gated on
    /// where AND findings: no 1.x tool declares both, and W3's check (its true successor) declares
    /// both, so the hint activates exactly at its wave. Its single-gate form fired today on
    /// cross_plugin_query, pointing at a script-property where= spelling nothing establishes that
    /// tool's grammar can resolve (final review finding 2 — held back the same way as conflict_tree).</summary>
    internal readonly record struct Dissolution(string Old, string[] GateParams, string Hint);

    static readonly Dissolution[] Dissolutions =
    {
        new("editoridcontains", new[] { "where" }, "not a parameter here — this job is the where= predicate grammar: where=[\"editorid contains <text>\"]"),

        // W2 PR 1 — the form-scoped PROJECT teachings (SPEC §2.2 F2): the flat 1.x spellings became sub-parameters
        // of the project= form object on `records`, so a stray flat one gets the form-scoping rule by name. All
        // gated on `project` — today that is exactly housecarl_records. conflict_tree is the CHARTERED W2 hint
        // (the most-taught changing spelling — 9+ skills teach conflict_tree=true; consumer-inventory obligation);
        // the tree form landed in W2 PR 2, so the interim "until then" clause is gone.
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

        // W3 PR 2 — housecarl_create absorbed the SCALAR create_record call: its five per-record operands became
        // members of a records= element, because one record is a set of one. A caller arriving with the 1.x habit
        // spells them at the TOP level, where they are genuinely gone rather than renamed — so each gets the
        // one-hop form. Gated on `records`, which housecarl_create declares. It USED to be declared by 1.x
        // housecarl_bulk_create as well, so each row fired on both tools and the census baked five
        // `housecarl_bulk_create: … => hint` activations alongside the create ones; the demolition catch-up (#468)
        // deleted that tool and those five census rows with it, so these rows now fire on housecarl_create alone.
        // (This comment is kept rather than deleted because its lesson outlived the second tool: a `records`-gated
        // row's activation set is whatever declares `records`, which is a thing to check rather than assume — an
        // earlier draft claimed "exactly housecarl_create" and contradicted the census in the same
        // commit.) (`operations` needs no row: it is a live rename to ops=, and inside a records element that is
        // where it lands.)
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

    // ---- retired tool NAMES (W2 PR 2 — the tool-NAME lane, on top of the parameter layer) -----------

    /// <summary>The 8 read tools `housecarl_records` absorbs, each mapped to its successor call shape.
    /// The lane is dormant PER ROW, not wholesale (CHARTER_PHASE4 §3.4a): the SDK resolves a registered name
    /// before the shim's filter runs, so this table answers only a call naming a tool the server does NOT
    /// have. That was nothing at all until the demolition catch-up (#468), which unregistered the six 1.x
    /// WRITE tools — those rows are LIVE now, and are the only thing standing between a caller on pre-2.0
    /// docs and a dead end. The eight read rows and the three check rows stay dormant while their tools are
    /// still registered, and go live at their own retirement. BindingShimProbe arm D3 drives every live row
    /// over the wire and treats an empty sweep as a broken guard, so how many are live is MEASURED there
    /// rather than asserted here. It turns the SDK's generic unknown-tool error into the successor
    /// spelling, so a caller working from old docs lands on `records` in one hop instead of a dead end.</summary>
    static readonly (string Old, string Successor)[] RetiredTools =
    {
        (ToolNames.ReadRecord,
         "absorbed into " + ToolNames.Records + ": formids=[\"XXXXXX:Plugin.esp\"] — project={\"form\": \"fields\", \"fields\": […]} for named fields, \"everything\" for the full body; plugin= is source=; conflict_tree=true is project={\"form\": \"tree\"}."),
        (ToolNames.BatchRecordDetail,
         "absorbed into " + ToolNames.Records + ": the same formids= list with a project= form (summary | fields | everything); plugin= is source=; to_file=/@file re-entry unchanged."),
        (ToolNames.Resolve,
         "absorbed into " + ToolNames.Records + ": formids=[…] with project={\"form\": \"identity\"} — the labeling form."),
        (ToolNames.CrossPluginQuery,
         "absorbed into " + ToolNames.Records + ": the same scan terms, set-valued — type= is types=, editorid_contains= is where=[\"editorid contains …\"], group_by= is project={\"form\": \"aggregate\", \"group_by\": …}, fields= lives inside project={\"form\": \"fields\"}."),
        (ToolNames.ReadPluginFile,
         "absorbed into " + ToolNames.Records + ": source=\"X.esp\" reads that plugin WHEREVER it lives (active or on disk out of the order — the response states which); types=/where= scan the file's records; formids= reads specific ones."),
        (ToolNames.DiffRecord,
         "absorbed into " + ToolNames.Records + ": project={\"form\": \"delta\"} — source= is the subject (was plugin_a), versus= the reference (was plugin_b; structured poles carry the mod disambiguator), project.fields narrows the comparison."),
        (ToolNames.EffectChain,
         "absorbed into " + ToolNames.Records + ": project={\"form\": \"chain\"} with walk={\"direction\": \"reverse\"} and the MGEF in formids= (types= still narrows the carrier types)."),
        (ToolNames.SkypatcherRead,
         "absorbed into " + ToolNames.Records + ": source={\"overlay\": \"skypatcher\", \"state\": \"post\"} reads the post-INI body; pre-vs-post is project={\"form\": \"delta\"} with the two overlay poles."),

        // W3 — the write side. Both field-edit tools became housecarl_apply; the LANE and vocabulary changes
        // (§5.1/§5.2) are named here too, because a caller arriving from old docs has the old PARAMETER habits
        // as well as the old tool name, and the one-hop redirect is worth more than a bare "use apply".
        ("housecarl_set_field",
         "absorbed into " + ToolNames.Apply + ": one op is a set of one — ops=[{formid, field_path, value}] (verb= is op=). patch_name= is patch=, full_readback= is readback=, and the target=+in_place=true pair is in_place=\"X.esp\" (the file being overwritten)."),
        ("housecarl_bulk_apply",
         "absorbed into " + ToolNames.Apply + ": operations= is ops= (verb= is op=), and from_file= is the @file convention — ops=\"@<absolute path>\". patch_name= is patch=, full_readback= is readback=, target=+in_place=true is in_place=\"X.esp\". Copying a field bundle BETWEEN records is bundle= + assignments=."),

        // W3 PR 2 — the rest of the write side. Same posture: the redirect carries the PARAMETER migration too,
        // since a caller arriving from old docs has both habits.
        ("housecarl_create_record",
         "absorbed into " + ToolNames.Create + ": one record is a set of one — records=[{record_type, editorid, ops}] (operations= is ops=, verb= is op=). record_type/editorid/parent/collection/grid are members of the record, not top-level arguments. patch_name= is patch=, full_readback= is readback=, and the target=+in_place=true pair is in_place=\"X.esp\" (the file being written into)."),
        ("housecarl_bulk_create",
         "absorbed into " + ToolNames.Create + ": records= is unchanged in shape except operations= is ops= (verb= is op=), and it also accepts \"@<absolute path>\". The nested one-shot is unchanged: declare a parent BEFORE the children whose parent= names its editorid, and '@editorid' still references a same-call sibling. patch_name= is patch=, full_readback= is readback=, target=+in_place=true is in_place=\"X.esp\"."),
        ("housecarl_remove_record",
         "absorbed into " + ToolNames.Remove + ": formids= is SET-VALUED — drop many records in one re-serialize (one is a set of one). The houseCARL-patch lane is into=\"MyPatch.esp\" (removal edits an artifact that EXISTS; patch= names a NEW one everywhere else on the surface), and the target=+in_place=true pair is in_place=\"X.esp\"."),
        // 4b — the derived-findings sweep. ALL THREE ancestors stay REGISTERED through the build waves (the W2/W3
        // precedent), so these rows are dormant by construction: a registered name resolves and never reaches the
        // retired-name check. They activate at the 2.0.0 clean cut, and nothing in a response mentions them until
        // then — no deprecation prose, in any of the three.
        //
        // THE DISPOSITION OF housecarl_validate_dialogue IS RECORDED HERE, because this table is what an old name
        // becomes at the cut and a row's absence reads as "kept" rather than as "not decided". It RETIRES with its
        // siblings: the merged surface carries its finding classes 1–7 under findings=["dialogue"], and class 8 —
        // the effective merged INFO order, which is an ordered sequence rather than a finding — went to
        // housecarl_records project={"form": "info_order"} at the SPEC §6.1 F1 split and ships there today. So the
        // successor teaching is TWO destinations, and a row naming only the sweep would send the caller who wants
        // "why does the wrong line play" to a surface that deliberately does not answer it.
        (ToolNames.CheckErrors,
         "absorbed into " + ToolNames.Check + ": the same sweep is findings=[\"errors\"], which is also the DEFAULT when findings= is omitted. type=/formids=/editorid_contains=/exclude=/counts_only=/limit=/max_chars=/format= are unchanged; the response is sectioned per family and states which families it did not run."),
        (ToolNames.ValidateScripts,
         "absorbed into " + ToolNames.Check + ": findings=[\"scripts\"] (or a class inside it — 'unbound_object', 'unbound_scalar', 'unbound', 'bound_null'). property_contains= and the record scope are unchanged, exclude= now scopes this family too, and the record listing is under the max_chars bound it was not under before."),

        (ToolNames.ValidateDialogue,
         "split in two. The findings are " + ToolNames.Check + " findings=[\"dialogue\"] with seeds= taking the same DIAL / QUST / DLVW / DLBR FormIDs — graph and branch wiring, LinkTo and previous-link targets, .fuz files, result scripts, CK-parity subrecords, malformed conditions and the .seq check — and limit= caps how many SEEDS one call expands. The effective merged INFO order is " + ToolNames.Records + " project={\"form\": \"info_order\"} with the DIAL in formids=: it is an ordered sequence rather than a finding, and the sweep's dialogue boundary says so."),

        ("housecarl_forward_record",
         "absorbed into " + ToolNames.Forward + ": from_plugin= is source= (an ACTIVE plugin — whose version to copy). patch_name= is patch=, full_readback= is readback=, and the target=+in_place=true pair is in_place=\"X.esp\". formids=, dry_run= and the into= replace-on-collision semantics are unchanged."),
    };

    /// <summary>The retired-name rows, for the guard that holds them against the surface they redirect INTO — the
    /// sibling of <see cref="AllRenames"/> / <see cref="AllDissolutions"/>. A merged surface gaining a family whose
    /// ancestor has no row here is the shape this exists to make visible.</summary>
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
