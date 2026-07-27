using System.ComponentModel;
using System.Text;
using HousecarlCore;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>
/// houseCARL NIF tool. Read-only. Reads the DATA VALUES inside one or many Skyrim meshes (.nif) — header/version, the
/// block census, each shape's name + NiAVObject flags + scale, its BSDismember partitions, its alpha property, its
/// texture-set paths and bone list, the node tree, and the header string table — resolving each Data-relative path
/// through MO2's VFS to the WINNING copy first (loose beats BSA), the same file-layer precedence the asset tools use,
/// with ONE load-order resolution for the whole batch (issue #229 — a facegen sweep is one call). Rides NiflySharp
/// (source-generated from nifxml = coverage by construction); reads BSA-packed meshes straight from archive bytes with
/// no disk extraction, and holds no file handles at rest. This is the asset-INTERNAL counterpart to housecarl_asset_status
/// (which answers WHICH file wins): once you know the winning mesh, this answers WHAT IS INSIDE it. Data values in; geometry
/// / visual content stays out of this capability by design (PRFAQ NIF-layer scope).
/// </summary>
[McpServerToolType]
public static class NifTools
{
    static readonly string[] KnownSections = { "shapes", "partitions", "alpha", "paths", "shader", "strings", "nodes", "bones" };

    /// <summary>The "(known: …)" hint shared by the unrecognized-section warning and the all-unrecognized loud error
    /// (#247): the legal tokens PLUS the pointer that there is NO 'textures' section — a mesh's embedded texture-set
    /// slot paths live under 'shapes' (per-shape detail) and 'paths', the two places a caller reaching for "textures"
    /// actually wants.</summary>
    internal static readonly string KnownSectionsHint =
        "known: " + string.Join(", ", KnownSections) + ", all — no 'textures' section; " +
        "embedded texture-set slot paths are under 'shapes' (detail) and 'paths'";

    [McpServerTool(Name = "housecarl_nif_inspect", ReadOnly = true, Title = "Inspect the data values inside one or many Skyrim meshes (.nif)"),
     Description(
         "Read the DATA VALUES inside one or many Skyrim meshes (.nif) at the data layer, beneath NifSkope. Resolve each " +
         "Data-relative path in mesh_paths through Mod Organizer 2's virtual file system to the copy the game actually uses " +
         "(loose beats BSA; among BSAs the later-loaded plugin wins) and report, per mesh: its header version + whether it is a Skyrim SE " +
         "stream; the block census (every block type and count); any UNKNOWN blocks (named + preserved, never silently " +
         "dropped); and per shape — the shape name, the NiAVObject flags (hex, decoded by deviation from the type's " +
         "documented default plus the 0x80000 bit) and scale, the BSDismember body-part " +
         "partitions (decoded to their SBP_* names), the alpha property (decoded blend / test / threshold), the SHADER " +
         "property (block type, the shader TYPE enum — SkinTint / FaceTint / HairTint / EnvironmentMap / Parallax / … — " +
         "the SLSF1+SLSF2 flags decoded to their names, and the lighting values — emissive colour and multiple, " +
         "glossiness, specular strength and colour, alpha; any one that would be a constant rather than this mesh's " +
         "number is NAMED as unreported, with which reason, rather than printed), the embedded " +
         "texture-set paths with their semantic slot names where the shader determines them, and the bone list; plus the node tree and the header string table. Use it to answer 'what " +
         "shapes / bones / textures / partitions / alpha does this mesh have', 'does this mesh glow / use soft lighting / " +
         "subsurface skin / env-mapping', to read a facegen mesh's baked shape names " +
         "and tint path, to check a skeleton's bone names, or to see a dark-face mesh's flags/alpha/partitions — the " +
         "asset-INTERNAL companion to housecarl_asset_status (which mod wins) once you know the winning file. Pass " +
         "mesh_paths = one or more paths (asset_status parity — a whole facegen sweep's flagged subset is ONE call, one " +
         "load-order resolution for the batch); results return in input order, and a failing path is reported LOUD on THAT " +
         "path without aborting the rest. Output is a " +
         "SUMMARY per mesh by default (header + census + shape names); pass sections to expand ('shapes','partitions','alpha'," +
         "'paths','shader','strings','nodes','bones', or 'all'). Pass mod= to inspect a specific provider instead of the winner; " +
         "sections, mod and max_chars apply to the whole batch. An " +
         "unreadable archive, an absent path, or a mesh the underlying mesh library refuses is " +
         "reported LOUD by name — never a silent 'absent' or a half-answer. Read-only: resolves nothing to disk, writes " +
         "nothing, changes no load order. Scope: data values only; it does not read or edit geometry / visual content.")]
    public static string NifInspect(
        LoadOrderService svc,
        [Description("The Data-relative mesh path(s) to inspect, e.g. " +
                     "'meshes\\actors\\character\\facegendata\\facegeom\\Skyrim.esm\\00000007.nif' or " +
                     "'meshes\\armor\\iron\\cuirass_1.nif'. One or many; inspected in order, results returned in the same " +
                     "order. Relative to the game's Data folder (forward or back slashes both fine).")]
            string[] mesh_paths,
        [Description("Optional. Which detail sections to show beyond the summary — any of 'shapes', 'partitions', 'alpha', " +
                     "'paths', 'shader', 'strings', 'nodes', 'bones', or 'all'. Comma-, space-, or JSON-array-separated (e.g. " +
                     "[\"shapes\",\"shader\"]). There is NO 'textures' section — a mesh's embedded texture-set slot paths " +
                     "appear under 'shapes' (per-shape detail) and 'paths'. 'shader' is the per-shape shader property: " +
                     "block type, shader TYPE enum, decoded SLSF1/SLSF2 flag names, and the lighting values that are readable. " +
                     "Applies to every mesh in the batch; " +
                     "unrecognized tokens are reported loud, and an all-unrecognized sections= is an error (never a " +
                     "silent fallback to the summary). Empty = summary only (header + block census + shape names).")]
            string sections = "",
        [Description("Optional. Inspect a specific provider's copy instead of the VFS winner — the mod folder " +
                     "name, 'overwrite', 'Data', or a BSA filename as listed in the providers chain. Applies to every mesh " +
                     "in the batch. Empty = the winner.")]
            string mod = "",
        [Description("Optional. Max characters before the output is cut with an explicit notice. 0 = the server default (~80k).")]
            int max_chars = 0) => Guard.Tool("housecarl_nif_inspect", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        if (mesh_paths is null || mesh_paths.Length == 0)
            return "error: mesh_paths is empty. Pass one or more Data-relative mesh paths (e.g. 'meshes\\armor\\iron\\cuirass_1.nif').";
        if (mesh_paths.All(string.IsNullOrWhiteSpace))
            return "error: mesh_paths contains only empty/blank entries. Pass Data-relative mesh paths (e.g. 'meshes\\armor\\iron\\cuirass_1.nif').";

        var (want, unknownTokens) = ParseSections(sections);
        // Q3 (#247): sections were requested but NONE resolved — do NOT run the inspect and silently render the summary
        // as if that were the answer (the reported quiet-fallback-to-defaults). Fail loud. (A PARTIAL request proceeds
        // — it renders the valid sections + a loud warning.)
        if (SectionsError(want, unknownTokens) is { } sectionsErr) return sectionsErr;

        var data = svc.NifInspect(mesh_paths, string.IsNullOrWhiteSpace(mod) ? null : mod);
        return NifWire.Render(data, want, unknownTokens, max_chars > 0 ? max_chars : 80_000);
    });

    /// <summary>Parse the sections argument into the recognized set + a list of any UNRECOGNIZED tokens (surfaced, never
    /// silently ignored — Q3). 'all' expands to every known section. Tolerates the JSON-array-as-string form an MCP
    /// client naturally sends for a list: <c>sections=["shapes","paths"]</c> arrives here as the literal string
    /// <c>["shapes","paths"]</c>, so the bracket and quote characters are split delimiters too — otherwise they glue
    /// onto the first/last token (<c>["shapes</c>) and the whole array reads as unrecognized, the #247 quiet-fallback.</summary>
    internal static (HashSet<string> Want, IReadOnlyList<string> Unknown) ParseSections(string sections)
    {
        var want = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unknown = new List<string>();
        foreach (var raw in (sections ?? "").Split(new[] { ',', ' ', ';', '\t', '[', ']', '"', '\'' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Equals("all", StringComparison.OrdinalIgnoreCase)) { foreach (var s in KnownSections) want.Add(s); }
            else if (Array.Exists(KnownSections, s => s.Equals(raw, StringComparison.OrdinalIgnoreCase))) want.Add(raw.ToLowerInvariant());
            else unknown.Add(raw);
        }
        return (want, unknown);
    }

    /// <summary>The #247 all-unrecognized guard as a testable seam: sections were REQUESTED but NONE resolved (a typo,
    /// or the non-existent 'textures') → the loud error string that replaces the silent fallback to the summary. Null
    /// when the request is fine — nothing requested (summary is the intended answer), or at least one section resolved
    /// (a partial request renders the valid ones + a warning, never an error).</summary>
    internal static string? SectionsError(HashSet<string> want, IReadOnlyList<string> unknown)
        => want.Count == 0 && unknown.Count > 0
            ? $"error: no recognized section(s) in sections — unrecognized: {string.Join(", ", unknown)}  " +
              $"({KnownSectionsHint}). Pass one or more known sections, or omit sections= for the summary only."
            : null;

    [McpServerTool(Name = "housecarl_nif_set", Title = "Write a whitelisted data value into a Skyrim mesh (.nif)"),
     Description(
         "Write ONE whitelisted DATA VALUE into a Skyrim SE mesh (.nif) at the data layer, beneath NifSkope — then VERIFY " +
         "the edit before anything lands. Resolve the Data-relative mesh_path through Mod Organizer 2's VFS to the winning " +
         "copy (or mod=), apply the op, and pass it two offset-immune verification gates (only the block/value the op " +
         "claims to touch changed; a reload re-reads the new value; census + SE-stream intact) — a failure writes NOTHING " +
         "and says why. The six ops (op=): rename_shape / rename_node (retitle a baked shape/node — the HDPT-EDID facegen " +
         "case), set_flags (NiAVObject flags on a shape/node — the 0x80000 head/hair-class bit), set_scale, set_partition " +
         "(a BSDismember body-part id — pass body_part_id [+ partition_index]), set_alpha (alpha_flags word and/or " +
         "alpha_threshold — the hair 0x12ED / hairline 0x12EE class), set_path (swap a BSShaderTextureSet slot — pass " +
         "texture_slot + path; e.g. the FaceTint slot 6 or skin slots 0/1). target= is the shape or node NAME the op edits " +
         "(from housecarl_nif_inspect). By DEFAULT the verified mesh is written into a NEW houseCARL MO2 mod folder at the " +
         "same path (originals untouched) — enable it and sort it ABOVE the current winner so the edit wins; a BSA-packed " +
         "source becomes a loose winning override this way. in_place=true instead OVERWRITES the winning LOOSE file where " +
         "it sits (opt-in; rides the one-time per-file consent handshake, needs acknowledge=true, NO backup). Only edits " +
         "data VALUES — never geometry / vertices / the .dds pixels. Refuses loud (Q3): a non-SE mesh, a target it can't " +
         "find or that's ambiguous, an op that doesn't apply, or any verification miss.")]
    public static string NifSet(
        LoadOrderService svc,
        [Description("The Data-relative mesh path to edit, e.g. 'meshes\\actors\\character\\facegendata\\facegeom\\Skyrim.esm\\00000007.nif'.")]
            string mesh_path,
        [Description("The write op: 'rename_shape', 'rename_node', 'set_flags', 'set_scale', 'set_partition', 'set_alpha', or 'set_path'.")]
            string op,
        [Description("The NAME of the shape or node the op edits (the current name; from housecarl_nif_inspect). For a rename this is the OLD name.")]
            string target,
        [Description("rename_shape / rename_node: the new name.")] string new_name = "",
        [Description("set_flags: the NiAVObject flags value — hex ('0x800000E') or decimal.")] string flags = "",
        [Description("set_scale: the scale, e.g. '1.0'.")] string scale = "",
        [Description("set_partition: the BSDismember body-part id, e.g. '32' (SBP_32_BODY).")] string body_part_id = "",
        [Description("set_partition: which partition to change when a shape has more than one (0-based). Omit if it has exactly one.")] string partition_index = "",
        [Description("set_alpha: the 16-bit alpha flags word — hex ('0x12ED') or decimal. Optional if only changing the threshold.")] string alpha_flags = "",
        [Description("set_alpha: the alpha test threshold, 0-255. Optional if only changing the flags word.")] string alpha_threshold = "",
        [Description("set_path: the BSShaderTextureSet slot index (0 diffuse, 1 normal, 6 tint/skin/detail, ...).")] string texture_slot = "",
        [Description("set_path: the new texture path (Data-relative, e.g. 'textures\\...\\facetint\\Mod.esp\\00000ABC.dds').")] string path = "",
        [Description("Optional. Edit a specific provider's copy instead of the VFS winner — the mod folder name, 'overwrite', 'Data', or a BSA filename from the providers chain. Empty = the winner.")]
            string mod = "",
        [Description("Optional. Base name for the NEW mod folder the edited mesh is written into (default lane; auto-suffixed if taken). Ignored with in_place=true.")]
            string patch_name = "",
        [Description("Optional. Write into an EXISTING houseCARL-owned mod folder instead of a fresh one (default lane). Mutually exclusive with in_place.")]
            string into = "",
        [Description("Optional, default false. IN-PLACE LANE (opt-in): OVERWRITE the winning LOOSE file where it sits instead of writing a new folder — NO backup. Requires acknowledge=true (see below). OMIT (the default) to write a new winning override and leave the original untouched.")]
            bool in_place = false,
        [Description("Optional, default false. Confirms the one-time in-place trade-off for this file — needed only on the FIRST in-place edit of a given mesh, never again for it. Waives the consent to overwrite your original ONLY; it NEVER skips the mesh verification.")]
            bool acknowledge = false) => Guard.Tool("housecarl_nif_set", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        if (string.IsNullOrWhiteSpace(mesh_path)) return "error: mesh_path is empty. Pass a Data-relative mesh path.";
        if (string.IsNullOrWhiteSpace(op)) return "error: op is empty. Pass one of: rename_shape, rename_node, set_flags, set_scale, set_partition, set_alpha, set_path.";
        if (string.IsNullOrWhiteSpace(target)) return "error: target is empty. Pass the shape/node NAME the op edits (from housecarl_nif_inspect).";

        var (built, buildErr) = BuildOp(op.Trim(), target.Trim(), new_name, flags, scale, body_part_id, partition_index, alpha_flags, alpha_threshold, texture_slot, path);
        if (buildErr is not null) return "error: " + buildErr;

        var data = svc.NifSet(mesh_path, new[] { built! },
            string.IsNullOrWhiteSpace(mod) ? null : mod,
            string.IsNullOrWhiteSpace(patch_name) ? null : patch_name,
            string.IsNullOrWhiteSpace(into) ? null : into,
            in_place, acknowledge);
        return NifSetWire.Render(data);
    });

    /// <summary>Turn the flat tool params into one <see cref="NifSetOp"/>, or a friendly NAMED error (Q3) for an unknown op
    /// or an unparseable value — before the value ever reaches the service. Numeric params are strings so an omitted one is
    /// distinguishable from a real 0 (partition_index 0 is valid).</summary>
    static (NifSetOp? Op, string? Error) BuildOp(string op, string target,
        string newName, string flags, string scale, string bodyPartId, string partitionIndex, string alphaFlags, string alphaThreshold, string textureSlot, string path)
    {
        switch (op.ToLowerInvariant())
        {
            case "rename_shape": return (new NifSetOp(NifSetOpKind.RenameShape, target, NewName: newName), null);
            case "rename_node": return (new NifSetOp(NifSetOpKind.RenameNode, target, NewName: newName), null);
            case "set_flags":
                if (!TryParseUInt(flags, out var fv)) return (null, $"set_flags needs a flags value (hex '0x800000E' or decimal); got '{flags}'.");
                return (new NifSetOp(NifSetOpKind.SetFlags, target, Flags: fv), null);
            case "set_scale":
                if (!float.TryParse(scale, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sc))
                    return (null, $"set_scale needs a numeric scale; got '{scale}'.");
                return (new NifSetOp(NifSetOpKind.SetScale, target, Scale: sc), null);
            case "set_partition":
                if (!int.TryParse(bodyPartId, out var bp)) return (null, $"set_partition needs a numeric body_part_id; got '{bodyPartId}'.");
                int? pidx = null;
                if (!string.IsNullOrWhiteSpace(partitionIndex)) { if (!int.TryParse(partitionIndex, out var pi)) return (null, $"partition_index must be a number; got '{partitionIndex}'."); pidx = pi; }
                return (new NifSetOp(NifSetOpKind.SetPartition, target, BodyPartId: bp, PartitionIndex: pidx), null);
            case "set_alpha":
                ushort? af = null; byte? at = null;
                if (!string.IsNullOrWhiteSpace(alphaFlags)) { if (!TryParseUInt(alphaFlags, out var afu) || afu > ushort.MaxValue) return (null, $"alpha_flags must be a 16-bit value (hex '0x12ED' or decimal); got '{alphaFlags}'."); af = (ushort)afu; }
                if (!string.IsNullOrWhiteSpace(alphaThreshold)) { if (!byte.TryParse(alphaThreshold, out var atb)) return (null, $"alpha_threshold must be 0-255; got '{alphaThreshold}'."); at = atb; }
                if (af is null && at is null) return (null, "set_alpha needs alpha_flags and/or alpha_threshold.");
                return (new NifSetOp(NifSetOpKind.SetAlpha, target, AlphaFlags: af, AlphaThreshold: at), null);
            case "set_path":
                if (!int.TryParse(textureSlot, out var slot)) return (null, $"set_path needs a numeric texture_slot; got '{textureSlot}'.");
                if (string.IsNullOrWhiteSpace(path)) return (null, "set_path needs a path.");
                return (new NifSetOp(NifSetOpKind.SetPath, target, TextureSlot: slot, Path: path), null);
            default:
                return (null, $"unknown op '{op}'. Use one of: rename_shape, rename_node, set_flags, set_scale, set_partition, set_alpha, set_path.");
        }
    }

    /// <summary>Parse a uint from hex ('0x...') or decimal.</summary>
    static bool TryParseUInt(string s, out uint value)
    {
        s = (s ?? "").Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value);
        return uint.TryParse(s, out value);
    }
}

/// <summary>Renders <see cref="NifInspectBatchData"/> as compact, scannable text: the build-level Q3 alarms first and
/// ONCE (archives that failed to read; discovery warnings — batch-level, so a long batch can't truncate them away),
/// then one block per mesh in INPUT ORDER — the resolution (which copy was read + the provider chain), then, on a clean
/// read, the summary (version, block census, unknown-block report, shape names, node count) and any requested detail
/// sections. An ABSENT / bad-path / unreadable / parse-refused result is that path's error line, loud and named,
/// without costing the rest of the batch. Output is bounded by max_chars with an explicit cut notice naming how many
/// meshes were omitted (Q3 — never silent truncation).</summary>
static class NifWire
{
    public static string Render(NifInspectBatchData d, HashSet<string> want, IReadOnlyList<string> unknownSections, int cap)
    {
        var sb = new StringBuilder();
        sb.Append("nif inspect — profile '").Append(d.ProfileName.Length > 0 ? d.ProfileName : "(unconfigured)")
          .Append("'  (").Append(d.Results.Count).Append(" mesh").Append(d.Results.Count == 1 ? "" : "es").Append(")\n");

        // Q3 alarms FIRST + ONCE (batch-level), so a long batch can't truncate them away.
        AppendReadFailures(sb, d.BsaFailures, cap);
        AppendDiscoveryWarnings(sb, d.Warnings, cap);
        if (unknownSections.Count > 0)
            sb.Append("\n[!] unrecognized section(s) ignored: ").Append(string.Join(", ", unknownSections))
              .Append("  (").Append(NifTools.KnownSectionsHint).Append(")\n");

        bool readIncomplete = d.BsaFailures.Count > 0, discoveryIncomplete = d.Warnings.Count > 0;
        int shown = 0;
        foreach (var r in d.Results)
        {
            // shown > 0: the FIRST mesh always renders its core answer (resolution/error/summary) even when the
            // alarms alone exhausted the cap — max_chars bounds the batch tail and detail lists, it never starves a
            // single-path call of the very answer it asked for (PR #243 review).
            if (shown > 0 && sb.Length >= cap)
            {
                sb.Append("\n… [").Append(d.Results.Count - shown)
                  .Append(" more mesh(es) omitted at max_chars=").Append(cap).Append("; raise max_chars to see all]\n");
                break;
            }
            AppendMesh(sb, r, want, cap, readIncomplete, discoveryIncomplete);
            shown++;
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>One mesh's block: the path line, then either its named error (+ provider chain when we have one) or the
    /// resolution + summary + requested detail sections. An ABSENT is hedged at POINT OF USE on both batch-level scan
    /// caveats (an archive that failed to READ; archives never DISCOVERED) — the top-of-output alarm alone scrolls away
    /// in a long batch, and "absent → the mesh is fine/missing" is exactly the over-trust the hedge exists to stop (Q3,
    /// asset_status parity).</summary>
    static void AppendMesh(StringBuilder sb, NifInspectData d, HashSet<string> want, int cap, bool readIncomplete, bool discoveryIncomplete)
    {
        sb.Append('\n').Append(d.RelPath.Length > 0 ? d.RelPath : "(empty path)").Append('\n');

        // Error path (ABSENT / bad path / unreadable / parse-refused). Still show the provider chain when we have one.
        if (d.Inspect is null)
        {
            sb.Append("  ").Append(d.Error ?? "unknown error").Append('\n');
            if (d.Absent)
            {
                if (readIncomplete)
                    sb.Append("  [!] but an archive failed to read this build (see the read-failure note above), so " +
                              "\"ABSENT\" may be incomplete — the mesh could live in the unreadable archive.\n");
                if (discoveryIncomplete)
                    sb.Append("  [!] some archives were not scanned this build (see the discovery note above), so " +
                              "\"ABSENT\" may be incomplete — base-game meshes live in BSAs that weren't enumerated.\n");
            }
            if (d.Providers.Count > 0) AppendProviders(sb, d.Providers);
            return;
        }

        var nif = d.Inspect;
        sb.Append("  read from: ").Append(d.Inspected!.Name).Append(" (").Append(d.Inspected.Kind).Append(")\n");
        AppendProviders(sb, d.Providers);
        if (d.Ambiguous)
            sb.Append("  note: more than one source provides this mesh — the winner above was read (loose beats BSA). " +
                      "Pass mod= to inspect another provider's copy.\n");

        sb.Append("  version: ").Append(nif.VersionString.Length > 0 ? nif.VersionString : "(unknown)")
          .Append("  user ").Append(nif.UserVersion).Append(" stream ").Append(nif.StreamVersion)
          .Append(nif.IsSkyrimSE ? "  [Skyrim SE]" : "  [NOT an SE stream — LE / FO4 / other]").Append('\n');

        AppendClampedList(sb, "  blocks: " + nif.BlockCount + " — ", nif.BlockTypes.Select(t => t.Type + " x" + t.Count), cap);

        if (!nif.HasUnknownBlocks)
            sb.Append("  unknown blocks: none\n");
        else if (nif.UnknownBlockTypes.Count == 0)
            // The library flagged unknown blocks but none resolved to a NiUnknown we could name (theoretical) — say so
            // honestly rather than render a bare "0 type(s) —".
            sb.Append("  unknown blocks: present (types not named — preserved intact, reported not modeled)\n");
        else
            sb.Append("  unknown blocks: ").Append(nif.UnknownBlockTypes.Count).Append(" type(s) — ")
              .Append(string.Join(", ", nif.UnknownBlockTypes))
              .Append("  (preserved intact, reported not modeled — likely another game's format)\n");

        AppendClampedList(sb, "  shapes (" + nif.Shapes.Count + "): ", nif.Shapes.Select(s => "'" + s.Name + "'"), cap);
        sb.Append("  nodes: ").Append(nif.Nodes.Count).Append("  (pass sections=nodes for the tree)\n");

        // ---- detail sections on demand ----
        if (want.Contains("shapes")) RenderShapesDetail(sb, nif, cap);
        if (want.Contains("partitions")) RenderPerShape(sb, nif, cap, "partitions", s => s.Partitions.Count > 0,
            s => string.Join(", ", s.Partitions.Select(p => $"{p.BodyPartId} ({p.BodyPartName}, flags {p.PartFlags})")));
        if (want.Contains("alpha")) RenderPerShape(sb, nif, cap, "alpha", s => s.Alpha is not null,
            s => AlphaLine(s.Alpha!));
        if (want.Contains("paths")) RenderPaths(sb, nif, cap);
        if (want.Contains("shader")) RenderShader(sb, nif, cap);
        if (want.Contains("bones")) RenderPerShape(sb, nif, cap, "bones", s => s.Bones.Count > 0,
            s => string.Join(", ", s.Bones));
        if (want.Contains("nodes")) RenderNodes(sb, nif, cap);
        if (want.Contains("strings")) RenderStrings(sb, nif, cap);
    }

    static void AppendProviders(StringBuilder sb, IReadOnlyList<NifProvider> providers)
    {
        sb.Append("  providers (").Append(providers.Count).Append("): ");
        for (int i = 0; i < providers.Count; i++)
        {
            if (i > 0) sb.Append(" > ");
            sb.Append(providers[i].Name).Append(" (").Append(providers[i].Kind).Append(')');
        }
        sb.Append('\n');
    }

    static void RenderShapesDetail(StringBuilder sb, NifInspect nif, int cap)
    {
        sb.Append("\n--- shapes (").Append(nif.Shapes.Count).Append(") ---\n");
        if (SlotNamingCaveat(nif) is { } shapesCaveat) sb.Append(shapesCaveat);
        int shown = 0;   // the cut notice counts the REMAINDER, not the total (PR #243 review — the RenderPerShape rule)
        foreach (var s in nif.Shapes)
        {
            if (Cut(sb, cap, nif.Shapes.Count - shown)) return;
            sb.Append("  '").Append(s.Name).Append("'  flags ").Append(DescribeFlags(s.Flags, s.FlagsDefault, s.FlagsDefaultType, s.BlockType))
              .Append("  scale ").Append(Fmt(s.Scale)).Append('\n');
            if (s.Partitions.Count > 0)
                sb.Append("    partitions: ").Append(string.Join(", ", s.Partitions.Select(p => $"{p.BodyPartId} ({p.BodyPartName}, flags {p.PartFlags})"))).Append('\n');
            if (s.Alpha is not null)
                sb.Append("    alpha: ").Append(AlphaLine(s.Alpha)).Append('\n');
            foreach (var t in s.Textures) AppendTexture(sb, t);
            if (s.Bones.Count > 0)
                sb.Append("    bones: ").Append(string.Join(", ", s.Bones)).Append('\n');
            shown++;
        }
    }

    static void RenderPerShape(StringBuilder sb, NifInspect nif, int cap, string title, Func<NifShape, bool> has, Func<NifShape, string> line)
    {
        sb.Append("\n--- ").Append(title).Append(" ---\n");
        var matched = nif.Shapes.Where(has).ToList();   // count the omitted remainder over the FILTERED subset, not the total shapes
        int shown = 0;
        foreach (var s in matched)
        {
            if (Cut(sb, cap, matched.Count - shown)) return;
            sb.Append("  '").Append(s.Name).Append("': ").Append(line(s)).Append('\n');
            shown++;
        }
        if (shown == 0) sb.Append("  (none)\n");
    }

    static void RenderPaths(StringBuilder sb, NifInspect nif, int cap)
    {
        sb.Append("\n--- paths (embedded texture-set slots; material/.tri/physics-xml refs appear under sections=strings) ---\n");
        if (SlotNamingCaveat(nif) is { } pathsCaveat) sb.Append(pathsCaveat);
        var textured = nif.Shapes.Where(s => s.Textures.Count > 0).ToList();   // omitted remainder counts the FILTERED subset, not total shapes
        int shown = 0;
        foreach (var s in textured)
        {
            if (Cut(sb, cap, textured.Count - shown)) return;
            sb.Append("  '").Append(s.Name).Append("':\n");
            foreach (var t in s.Textures) AppendTexture(sb, t);
            shown++;
        }
        if (shown == 0) sb.Append("  (no embedded texture paths)\n");
    }

    /// <summary>One texture slot line, shared by the shapes and paths sections. The INDEX is always printed — the
    /// semantic name (#272) rides ALONGSIDE it, never replaces it, because the index is what nif_set's texture_slot=
    /// takes. A slot whose meaning the shape's shader doesn't determine (slot 2 with no glow/soft-light/skin-tint
    /// signal, say) prints bare, so "unnamed" reads as "this shader doesn't say" rather than a confident wrong label.</summary>
    static void AppendTexture(StringBuilder sb, NifTexture t)
    {
        sb.Append("    tex[").Append(t.Slot).Append(']');
        if (t.SlotName is not null) sb.Append(" (").Append(t.SlotName).Append(')');
        sb.Append(": ").Append(t.Path).Append('\n');
    }

    /// <summary>The shader section (#272): per shape, the block type + shader TYPE enum, the decoded flag words, and
    /// the lighting values. Multi-line per shape rather than one long line — this is the section a visual diagnosis
    /// reads top to bottom (does it glow, does it scatter, is it env-mapped).</summary>
    static void RenderShader(StringBuilder sb, NifInspect nif, int cap)
    {
        sb.Append("\n--- shader (per shape; slot names above come from these type+flags) ---\n");
        var shaded = nif.Shapes.Where(s => s.Shader is not null).ToList();   // omitted remainder counts the FILTERED subset
        int shown = 0;
        foreach (var s in shaded)
        {
            if (Cut(sb, cap, shaded.Count - shown)) return;
            var sh = s.Shader!;
            sb.Append("  '").Append(s.Name).Append("': ").Append(sh.BlockType);
            // A block that doesn't serialize a shader type says so, rather than reporting a default-valued one (Q3).
            sb.Append(sh.ShaderType is null ? "  (no shader type on this block)" : "  type " + sh.ShaderType);
            sb.Append("  [").Append(sh.GameType).Append(" layout]\n");
            // The DECLINE is stated, not just performed. Slot naming models a Skyrim convention, so on any other
            // layout every slot prints bare — which is byte-identical to "this Skyrim shader doesn't determine that
            // slot" and means something entirely different. Left unsaid, the caller reads "no glow map here" off a
            // mesh houseCARL simply didn't interpret (review of PR #286).
            if (!IsSkyrimLayout(sh))
                sb.Append("    slot names: NOT DERIVED for this block — the slot semantics houseCARL models are a "
                          + "Skyrim convention, and this reads as the ").Append(sh.GameType)
                  .Append(" layout, so its slots print bare (unnamed here means unmodelled, not undetermined)\n");
            AppendFlagWord(sb, sh.Flags1);
            AppendFlagWord(sb, sh.Flags2);
            if (sh.Flags1 is null && sh.Flags2 is null)
                sb.Append("    flags: none decoded — this library models no named flag word for the ")
                  .Append(sh.GameType).Append(" layout (the raw block is intact; nothing is being hidden)\n");
            AppendShaderValues(sb, sh);
            shown++;
        }
        if (shown == 0) sb.Append("  (no shape carries a shader property)\n");
    }

    /// <summary>One decoded flag word. Unnamed bits are stated as an explicit hex mask — the #255 posture, carried
    /// here: a bit the library's enum doesn't name is a real thing the mesh carries, so it is surfaced, never dropped
    /// and never rolled silently into the named list.</summary>
    static void AppendFlagWord(StringBuilder sb, NifShaderFlagWord? w)
    {
        if (w is null) return;
        sb.Append("    ").Append(w.Label).Append(" 0x").Append(w.Raw.ToString("X8")).Append(": ")
          .Append(w.Names.Count > 0 ? string.Join(", ", w.Names) : "(no named bit set)");
        if (w.UnknownBits != 0) sb.Append("  (+unknown bits 0x").Append(w.UnknownBits.ToString("X")).Append(')');
        sb.Append('\n');
    }

    /// <summary>The shader's lighting values — only the ones this NiflySharp version genuinely reads off the block.
    /// The rest are NAMED as unread on their own line rather than printed as the constant the library's interface stub
    /// would hand back (Q3: a caller must be able to tell "the mesh says 0" from "we can't see it").
    ///
    /// EVERY value has both a read form and an unread form, with no shared condition between them, so a value can
    /// never fall through both and vanish. The emissive multiple is the one that could: it reads most naturally as a
    /// suffix of the emissive colour, but if upstream ever implements it WITHOUT the colour (it maps onto
    /// BSEffectShaderProperty's <c>_baseColorScale</c>) a suffix-only form would print it nowhere and name it nowhere
    /// — the one hole in the "implemented upstream ⇒ reported here, no code change" promise. It gets its own entry
    /// when the colour is unread (review of PR #286).</summary>
    static void AppendShaderValues(StringBuilder sb, NifShader sh)
    {
        var have = new List<string>(5);
        if (sh.EmissiveColor is { } ec) have.Add("emissive " + ColorText(ec) + (sh.EmissiveMultiple is { } m ? " x" + Fmt(m) : ""));
        else if (sh.EmissiveMultiple is { } m2) have.Add("emissive multiple x" + Fmt(m2));   // read, but no colour to hang it off
        if (sh.Glossiness is { } g) have.Add("glossiness " + Fmt(g));
        if (sh.SpecularStrength is { } ss) have.Add("specular " + Fmt(ss) + (sh.SpecularColor is { } sc ? " " + ColorText(sc) : ""));
        else if (sh.SpecularColor is { } sc2) have.Add("specular " + ColorText(sc2));
        if (sh.Alpha is { } a) have.Add("alpha " + Fmt(a));
        if (have.Count > 0) sb.Append("    ").Append(string.Join("  ", have)).Append('\n');

        var missing = new List<string>(5);
        if (sh.EmissiveColor is null) missing.Add("emissive colour");
        if (sh.EmissiveMultiple is null) missing.Add("emissive multiple");
        if (sh.Glossiness is null) missing.Add("glossiness");
        if (sh.SpecularStrength is null) missing.Add("specular strength");
        if (sh.SpecularColor is null) missing.Add("specular colour");
        if (sh.Alpha is null) missing.Add("alpha");
        if (missing.Count == 0) return;
        // TWO different reasons produce an unreported value, and saying the wrong one is its own wrong answer. On a
        // non-Skyrim layout houseCARL declines them all as a matter of ITS OWN SCOPE — several read fine there, and
        // blaming the library would be false — so that decline gets its own sentence (review of PR #290).
        if (!IsSkyrimLayout(sh))
        {
            sb.Append("    lighting values: NOT INTERPRETED for this block — houseCARL models the Skyrim shader "
                      + "layout, and this reads as the ").Append(sh.GameType).Append(" layout, where some of these "
                      + "accessors answer a field the stream never carried (glossiness is a constant 80 there, not "
                      + "the mesh's). Declined rather than guessed which: ")
              .Append(string.Join(", ", missing)).Append(". NifSkope reads this mesh's own layout.\n");
            return;
        }
        // "WHERE THIS BLOCK CARRIES THEM" is doing real work: for a lighting shader the values genuinely are on
        // disk and NifSkope shows them, but a BSEffectShaderProperty has no glossiness / specular-strength /
        // specular-colour field AT ALL, so an unconditional "the values ARE in the file" would send the reader
        // hunting in NifSkope for fields that don't exist (review of PR #286).
        sb.Append("    NOT READ by this NiflySharp version — its accessor returns a constant for these, so ")
          .Append("houseCARL reports nothing rather than a wrong number: ")
          .Append(string.Join(", ", missing))
          .Append(". Where this block carries them, NifSkope shows the real values.\n");
    }

    static string ColorText(NifColor c) => $"rgb({Fmt(c.R)},{Fmt(c.G)},{Fmt(c.B)})";

    /// <summary>Whether this shader was read as the SKYRIM layout — the only one whose texture-slot semantics
    /// houseCARL models, so the only one where a slot name can be derived at all.</summary>
    static bool IsSkyrimLayout(NifShader sh) => sh.GameType == "SK";

    /// <summary>The one-line caveat for a section that shows slot paths on a mesh whose shader(s) houseCARL does not
    /// interpret — or null when every shader here is the Skyrim layout (the overwhelmingly common case, which stays
    /// unannotated). Without it a bare <c>tex[2]:</c> is ambiguous between "this Skyrim shader doesn't determine slot
    /// 2" and "we don't model this layout at all". The header's <c>[NOT an SE stream]</c> marker does NOT disambiguate
    /// it: an LE mesh trips that marker but still parses as the SK layout and DOES get named slots (review of #286).</summary>
    static string? SlotNamingCaveat(NifInspect nif)
    {
        var layouts = nif.Shapes.Select(s => s.Shader).OfType<NifShader>().Where(sh => !IsSkyrimLayout(sh))
                         .Select(sh => sh.GameType).Distinct().OrderBy(g => g, StringComparer.Ordinal).ToList();
        if (layouts.Count == 0) return null;
        return "  [!] slot names are NOT DERIVED for shader(s) read as the " + string.Join(" / ", layouts)
             + " layout — houseCARL models Skyrim's slot semantics only, so those slots print bare. Unnamed there "
             + "means UNMODELLED, not undetermined; pass sections=shader for each shape's layout.\n";
    }

    static void RenderNodes(StringBuilder sb, NifInspect nif, int cap)
    {
        sb.Append("\n--- node tree (").Append(nif.Nodes.Count).Append(") ---\n");
        int shown = 0;
        foreach (var n in nif.Nodes)
        {
            if (Cut(sb, cap, nif.Nodes.Count - shown)) return;
            sb.Append("  ").Append(new string(' ', n.Depth * 2)).Append(n.Name.Length > 0 ? n.Name : "(unnamed)")
              .Append("  ").Append(DescribeFlags(n.Flags, n.FlagsDefault, n.FlagsDefaultType, n.BlockType)).Append('\n');
            shown++;
        }
    }

    static void RenderStrings(StringBuilder sb, NifInspect nif, int cap)
    {
        sb.Append("\n--- header string table (").Append(nif.HeaderStrings.Count).Append(") ---\n");
        int shown = 0;
        foreach (var s in nif.HeaderStrings)
        {
            if (Cut(sb, cap, nif.HeaderStrings.Count - shown)) return;
            sb.Append("  [").Append(shown).Append("] ").Append(s).Append('\n');
            shown++;
        }
        if (shown == 0) sb.Append("  (none)\n");
    }

    static string AlphaLine(NifAlpha a)
        => $"flags 0x{a.Flags:X4}  blend={a.Blend} ({a.SourceBlendMode} -> {a.DestinationBlendMode})  test={a.Test} ({a.TestFunction})  threshold {a.Threshold}";

    /// <summary>Render NiAVObject flags: raw hex + a by-construction decode. nif.xml does not name these bits, so we
    /// decode by DEVIATION from the type's nif.xml-documented SSE default (<paramref name="def"/> from
    /// <paramref name="defType"/>) — "= default", or the extra/missing bits vs it — and always state the one bit nif.xml
    /// DOES document, 0x80000 (Skyrim sets it on some AV objects, FO4 never; the head-vs-hair signal). When no default is
    /// documented for the type, the set-bit positions are listed instead (still exact, still no invented names).</summary>
    static string DescribeFlags(uint flags, uint? def, string? defType, string blockType)
    {
        var sb = new StringBuilder("0x").Append(flags.ToString("X"));
        sb.Append("  [0x80000 ").Append((flags & 0x80000) != 0 ? "set" : "clear");
        if (def is { } d)
        {
            if (flags == d) sb.Append("; = ").Append(defType).Append(" default");
            else
            {
                uint extra = flags & ~d, missing = d & ~flags;
                sb.Append("; vs ").Append(defType).Append(" default 0x").Append(d.ToString("X")).Append(':');
                if (extra != 0) sb.Append(" +0x").Append(extra.ToString("X"));
                if (missing != 0) sb.Append(" -0x").Append(missing.ToString("X"));
            }
        }
        else
            sb.Append("; no documented default for ").Append(blockType).Append("; bits ").Append(BitList(flags));
        return sb.Append(']').ToString();
    }

    /// <summary>The set bit positions of a 32-bit flags word, comma-joined (e.g. "1,2,3,26"), or "none".</summary>
    static string BitList(uint f)
    {
        var bits = new List<int>();
        for (int i = 0; i < 32; i++) if ((f & (1u << i)) != 0) bits.Add(i);
        return bits.Count == 0 ? "none" : string.Join(",", bits);
    }

    /// <summary>Append "<label><a, b, c>" as one line, cut with an explicit notice if it would blow the cap (Q3).</summary>
    static void AppendClampedList(StringBuilder sb, string label, IEnumerable<string> items, int cap)
    {
        sb.Append(label);
        var list = items.ToList();
        int shown = 0;
        for (; shown < list.Count; shown++)
        {
            if (sb.Length >= cap) break;
            if (shown > 0) sb.Append(", ");
            sb.Append(list[shown]);
        }
        if (shown < list.Count)
            sb.Append(" … [").Append(list.Count - shown).Append(" more omitted at max_chars=").Append(cap).Append("; raise max_chars to see all]");
        sb.Append('\n');
    }

    /// <summary>True (and appends the cut notice) when the buffer has hit the cap mid-section — the per-item loop breaks.</summary>
    static bool Cut(StringBuilder sb, int cap, int remaining)
    {
        if (sb.Length < cap) return false;
        sb.Append("  … [").Append(Math.Max(remaining, 0)).Append(" more omitted at max_chars=").Append(cap).Append("; raise max_chars to see all]\n");
        return true;
    }

    static string Fmt(float f) => f.ToString("0.#######", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The archive-read-failure alarm (Q3): BSAs that couldn't be read this build. A mesh present ONLY in one of
    /// these is indistinguishable from a truly absent one, so it is surfaced LOUD before the answer.</summary>
    static void AppendReadFailures(StringBuilder sb, IReadOnlyList<string> failures, int cap)
    {
        if (failures.Count == 0) return;
        sb.Append("\n[!] ").Append(failures.Count).Append(" archive(s) could NOT be read this build — a mesh present only in these may read as ABSENT:\n");
        int shown = 0;
        foreach (var f in failures)
        {
            if (sb.Length >= cap) { sb.Append("  ... [").Append(failures.Count - shown).Append(" more omitted]\n"); break; }
            sb.Append("  - ").Append(f).Append('\n'); shown++;
        }
    }

    static void AppendDiscoveryWarnings(StringBuilder sb, IReadOnlyList<string> warnings, int cap)
    {
        if (warnings.Count == 0) return;
        sb.Append("\n[!] discovery (").Append(warnings.Count).Append("):\n");
        int shown = 0;
        foreach (var w in warnings)
        {
            if (sb.Length >= cap) { sb.Append("  ... [").Append(warnings.Count - shown).Append(" more omitted]\n"); break; }
            sb.Append("  - ").Append(w).Append('\n'); shown++;
        }
    }
}

/// <summary>Renders <see cref="NifSetResult"/>: the resolution (which copy was edited + provider chain), then exactly one
/// of — the in-place CONSENT prompt (verbatim, a required confirmation not an error), a NAMED refusal (nothing written),
/// or the verified-write success (the op's before→after, what the verification confirmed changed, and the LANE outcome:
/// a new mod folder to enable+sort, or the file overwritten in place). Q3: a default-lane success says "wrote it, now
/// enable+sort" — it never claims the edit is winning on disk yet.</summary>
static class NifSetWire
{
    public static string Render(NifSetResult d)
    {
        var sb = new StringBuilder();
        sb.Append("nif set — ").Append(d.RelPath.Length > 0 ? d.RelPath : "(mesh)")
          .Append("  (profile '").Append(d.ProfileName.Length > 0 ? d.ProfileName : "(unconfigured)").Append("')\n");

        // in-place first-touch consent — a required confirmation, returned verbatim (NOT an error, Q3).
        if (d.NeedsAcknowledge)
        {
            sb.Append('\n').Append(d.AckPrompt).Append('\n');
            return sb.ToString().TrimEnd('\n');
        }

        // refusal — nothing written.
        if (d.Report is null || d.Error is not null)
        {
            sb.Append('\n').Append(d.Error ?? "unknown error").Append('\n');
            if (d.Providers.Count > 0) AppendProviders(sb, d.Providers);
            return sb.ToString().TrimEnd('\n');
        }

        // success.
        if (d.Edited is not null) sb.Append("  edited copy: ").Append(d.Edited.Name).Append(" (").Append(d.Edited.Kind).Append(")\n");
        AppendProviders(sb, d.Providers);
        if (d.Ambiguous)
            sb.Append("  note: more than one source provides this mesh — the winner above was edited (loose beats BSA). Pass mod= to edit another copy.\n");

        sb.Append("\n  applied + VERIFIED (two gates: only the op's block/header changed; reload re-reads the value; census intact):\n");
        foreach (var o in d.Report.Ops)
            sb.Append("    ").Append(o.Op).Append(" on '").Append(o.Target).Append("': ").Append(o.Before).Append("  ->  ").Append(o.After).Append('\n');
        sb.Append("  verification: ")
          .Append(d.Report.HeaderChanged ? "header string table" : "no header change")
          .Append(d.Report.ChangedBlocks.Count > 0 ? $" + block(s) [{string.Join(", ", d.Report.ChangedBlocks)}]" : " + 0 blocks")
          .Append("; net size vs source ").Append(d.Report.SizeDelta >= 0 ? "+" : "").Append(d.Report.SizeDelta)
          .Append(" byte(s) (includes nifly's canonical re-serialization, not just the edit)\n");

        // lane outcome
        if (d.InPlace)
        {
            sb.Append("\n  IN-PLACE: overwrote ").Append(d.InPlacePath).Append(" (your original — no houseCARL backup).");
            sb.Append(d.EditedIsWinner
                ? " The edit is live where the file already wins the VFS.\n"
                : " NOTE: you edited a copy that another provider currently SHADOWS (you passed mod=), so this is not the winning copy in game until that changes.\n");
        }
        else
        {
            sb.Append("\n  wrote the verified mesh into a new mod folder: ").Append(d.OutputModFolder).Append('\n');
            sb.Append("  TO MAKE IT WIN: enable this folder in MO2 and sort it ABOVE ")
              .Append(d.CurrentWinner ?? "the current winner")
              .Append(" (loose beats BSA; among loose, the later mod wins). 'Wrote it' is not 'it wins' until you do.\n");
        }

        if (d.Warnings.Count > 0)
        {
            sb.Append("\n  notes:\n");
            foreach (var w in d.Warnings) sb.Append("    - ").Append(w).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    static void AppendProviders(StringBuilder sb, IReadOnlyList<NifProvider> providers)
    {
        if (providers.Count == 0) return;
        sb.Append("  providers (").Append(providers.Count).Append("): ");
        for (int i = 0; i < providers.Count; i++)
        {
            if (i > 0) sb.Append(" > ");
            sb.Append(providers[i].Name).Append(" (").Append(providers[i].Kind).Append(')');
        }
        sb.Append('\n');
    }
}
