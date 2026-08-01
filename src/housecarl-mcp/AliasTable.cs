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
            ExceptTools: new[] { ("housecarl_place_asset", "source") }),
        new("plugins", new[] { "plugin", "pluginname", "pluginnames" }),
        new("pluginname",  new[] { "patch", "plugins", "plugin", "pluginnames" }),   // §5.3 — create_plugin's plugin_name → patch; today's guess-miss → plugins (#221 J3) or the bare-plugin tools
        new("pluginnames", new[] { "plugins", "plugin", "pluginname" }),
        new("source", new[] { "plugin", "mod" }),                 // reverse: the new pole spelling on not-yet-renamed tools (read tools' plugin=, the NIF tools' mod=)

        // §5.3 — type/types (SELECT is set-valued types; record_type stays a create operand — distinct
        // normalization, no entry needed).
        new("type",  new[] { "types" }),
        new("types", new[] { "type" }),

        // §5.3 — ONE name for "the new artifact": patch. Forward rows fire today on remove_record
        // (bare patch already declared there — the drift instance §5's census found); reverse row
        // lets patch= bind on today's patch_name / plugin_name / archive_name / output spellings.
        new("patchname",   new[] { "patch" }),
        new("archivename", new[] { "patch" }),
        new("output",      new[] { "patch" }),
        new("patch",       new[] { "patchname", "pluginname", "archivename", "output" }),

        // §5.3 — unmanaged filesystem output: out_path.
        new("outputdir", new[] { "outpath" }),
        new("dest",      new[] { "outpath" }),
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
        // mod= carries the same place_asset exception as plugin= (its source= is a file-path operand).
        new("fromplugin", new[] { "source" }),
        new("mod", new[] { "source" },
            ExceptTools: new[] { ("housecarl_place_asset", "source") }),

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
    /// (conflict_tree's hint is deliberately absent at W0: whether the tree is a parameter or a
    /// projection shape is §6's per-wave call — W2 adds the entry when the carrier exists.)</summary>
    internal readonly record struct Dissolution(string Old, string GateParam, string Hint);

    static readonly Dissolution[] Dissolutions =
    {
        new("editoridcontains", "where", "not a parameter here — this job is the where= predicate grammar: where=[\"editorid contains <text>\"]"),
        new("propertycontains", "where", "not a parameter here — script-property predicates belong to the where= grammar on tools that support them: where=[\"<property path> contains <text>\"]"),
        new("mgefformid",   "walk", "not a parameter here — seed the walk= construct with the MGEF's FormID instead"),
        new("closure",      "walk", "not a parameter here — the closure copy is expressed as the walk= construct"),
        new("winnerfields", "fieldssource", "not a parameter here — the display pole spells it: fields_source=\"winner\""),
        new("fromfile",     "ops", "not a parameter here — pass the file with the @file convention: ops=\"@<path>\""),
        new("plugina", "versus", "not a parameter here — the comparison poles are source= (subject) and versus= (reference)"),
        new("pluginb", "versus", "not a parameter here — the comparison poles are source= (subject) and versus= (reference)"),
        new("moda",    "versus", "not a parameter here — structured source=/versus= poles carry the mod disambiguator"),
        new("modb",    "versus", "not a parameter here — structured source=/versus= poles carry the mod disambiguator"),
        new("sourceformid", "assignments", "not a parameter here — the assignments= zip carries from= per assignment"),
        new("sourceplugin", "assignments", "not a parameter here — the assignments= zip carries from_source= per assignment"),
        new("sourcemod",    "assignments", "not a parameter here — the assignments= zip carries from_source= per assignment"),
        new("targetformid", "assignments", "not a parameter here — the assignments= zip carries target= per assignment (§5.2's record pole)"),
    };

    /// <summary>The migration hint for a normalized unknown key, or null — gated on the replacement
    /// grammar's carrier parameter being declared in <paramref name="declaredNormalized"/>.</summary>
    internal static string? DissolutionHint(string normalizedKey, IReadOnlySet<string> declaredNormalized)
    {
        foreach (var d in Dissolutions)
            if (d.Old == normalizedKey && declaredNormalized.Contains(d.GateParam)) return d.Hint;
        return null;
    }
}
