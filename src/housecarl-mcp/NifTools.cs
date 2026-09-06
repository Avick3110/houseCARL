using System.ComponentModel;
using System.Text;
using HousecarlCore;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>Reads the data values inside one or many Skyrim meshes (.nif): header and version, the block census, each
/// shape's name, NiAVObject flags and scale, its BSDismember partitions, its alpha property, its texture-set paths and
/// bone list, the node tree, and the header string table. Each Data-relative path resolves through MO2's VFS to the
/// winning copy first, with one load-order resolution for the whole batch. Runs on NiflySharp, source-generated from
/// nifxml; reads BSA-packed meshes straight from archive bytes with no disk extraction and holds no file handles at
/// rest. Where housecarl_asset_status answers which file wins, this answers what is inside it. Geometry and visual
/// content are deliberately out of scope.</summary>
[McpServerToolType]
public static class NifTools
{
    static readonly string[] KnownSections = { "shapes", "partitions", "alpha", "paths", "shader", "strings", "nodes", "bones" };

    /// <summary>The "(known: …)" hint shared by the unrecognized-section warning and the all-unrecognized error: the
    /// legal tokens, plus the note that there is no 'textures' section — a mesh's embedded texture-set slot paths live
    /// under 'shapes' and 'paths'.</summary>
    internal static readonly string KnownSectionsHint =
        "known: " + string.Join(", ", KnownSections) + ", all — no 'textures' section; " +
        "embedded texture-set slot paths are under 'shapes' (detail) and 'paths'";

    [McpServerTool(Name = ToolNames.NifInspect, ReadOnly = true, Title = "Inspect the data values inside one or many Skyrim meshes (.nif)"),
     Description(
         "Read the DATA VALUES inside one or many Skyrim meshes (.nif) at the data layer, beneath NifSkope — ONE " +
         "surface: which meshes (SELECT) x whose copy (SOURCE) x how much of each mesh (PROJECT) compose in a single " +
         "call.\n\n" +
         "Every Data-relative path — typed in mesh_paths, or derived from an npc= FormID — resolves through Mod " +
         "Organizer 2's virtual file system to the copy the game actually uses (loose beats BSA; among BSAs the " +
         "later-loaded plugin wins), with ONE load-order resolution for the whole batch.\n\n" +
         "WHAT A READ SEES, per mesh: the header version + whether it is a Skyrim SE stream; the block census (every " +
         "block type and count); any UNKNOWN blocks (named + preserved, never silently dropped); and the shape names " +
         "— that is the default summary. sections= expands it to each shape's flags, scale, partitions, alpha, shader " +
         "and texture-set paths, its bone list, the node tree and the header string table. Scope: data values only; " +
         "it does not read or edit geometry / visual content.\n\n" +
         "Use it to answer 'what shapes / bones / textures / partitions / alpha does this mesh have', 'does this mesh " +
         "glow / use soft lighting / subsurface skin / env-mapping', to read a facegen mesh's baked shape names and " +
         "tint path, to check a skeleton's bone names, or to see a dark-face mesh's flags/alpha/partitions — the " +
         "asset-INTERNAL companion to " + ToolNames.AssetStatus + " (which mod wins) once you know the winning " +
         "file.\n\n" +
         "Each axis's grammar is on its own parameters:\n" +
         "SELECT — mesh_paths= | npc=; one of the two is required, and the two compose into one batch.\n" +
         "SOURCE — mod= (empty = the VFS winner).\n" +
         "PROJECT — sections= (empty = the summary).\n" +
         "TRANSPORT — max_chars=.\n\n" +
         "Every per-path failure — an unreadable archive, an absent path, a mod= name nothing provides, a mesh the " +
         "underlying mesh library refuses — is reported LOUD by name on THAT path without aborting the rest; never a " +
         "silent 'absent' or a half-answer. Read-only: resolves nothing to disk, writes nothing, changes no load " +
         "order — " + ToolNames.NifSet + " is the write counterpart.")]
    public static string NifInspect(
        LoadOrderService svc,
        [Description("The Data-relative mesh path(s) to inspect, e.g. " +
                     "'meshes\\actors\\character\\facegendata\\facegeom\\Skyrim.esm\\00000007.nif' or " +
                     "'meshes\\armor\\iron\\cuirass_1.nif'. One or many at " + ToolNames.AssetStatus + " parity — a " +
                     "whole facegen sweep's flagged subset is ONE call; inspected in order, results returned in the " +
                     "same order. Relative to the game's Data folder (forward or back slashes both fine). Optional " +
                     "only if npc= is passed instead.")]
            string[]? mesh_paths = null,
        [Description("Optional. NPC FormID(s) to inspect the FaceGen HEAD MESH of — houseCARL derives each one's " +
                     "'meshes\\actors\\character\\facegendata\\facegeom\\<defining master>\\00<6 hex>.nif' and reads it " +
                     "like any other mesh path, so the record → facegen derivation stops being the caller's job. " +
                     "'XXXXXX:Plugin.esp', or the runtime form the game/console prints ('FE012800', '0501A51A'). " +
                     "The FOLDER is the plugin that DEFINES the NPC, never the conflict winner. Derived paths are " +
                     "inspected AFTER any mesh_paths, in the order given; mesh_paths and npc may be passed together, " +
                     "and one of the two is required. A FormID that will not PARSE refuses the whole call, naming it, " +
                     "before any mesh is read; once a path is derived it fails like any other — on that path alone.")]
            string[]? npc = null,
        [Description("Optional. Which detail sections to show beyond the summary — any of 'shapes', 'partitions', 'alpha', " +
                     "'paths', 'shader', 'strings', 'nodes', 'bones', or 'all'. Comma-, space-, or JSON-array-separated (e.g. " +
                     "[\"shapes\",\"shader\"]). What each adds — per shape, except 'nodes' and 'strings', which are " +
                     "per mesh: 'shapes' the shape " +
                     "name, the NiAVObject flags (hex, decoded by deviation from the type's documented default plus " +
                     "the 0x80000 bit) and scale, with that shape's partitions, alpha, texture-set paths and bones " +
                     "inline; 'partitions' the BSDismember body-part partitions (decoded to their SBP_* names); " +
                     "'alpha' the alpha property (decoded blend / test / threshold); 'paths' the embedded texture-set " +
                     "paths with their semantic slot names where the shader determines them; 'shader' the SHADER " +
                     "property (block type, the shader TYPE enum — SkinTint / FaceTint / HairTint / EnvironmentMap / " +
                     "Parallax / … — the SLSF1+SLSF2 flags decoded to their names, and the lighting values on a " +
                     "Skyrim-layout shader — emissive colour and multiple, glossiness, specular strength and colour, " +
                     "alpha. Anything not reported is NAMED, with its reason: the library stubs that accessor for " +
                     "that block type, or the mesh reads as another game's layout, where houseCARL declines the group " +
                     "rather than interpret the ones that survive the layout change and guess at the rest); 'bones' " +
                     "the bone list; 'nodes' the node tree, each node with the same flag decode; 'strings' the header " +
                     "string table. There is NO 'textures' section — a mesh's embedded texture-set slot paths " +
                     "appear under 'shapes' (per-shape detail) and 'paths'. " +
                     "Applies to every mesh in the batch; " +
                     "unrecognized tokens are reported loud, and an all-unrecognized sections= is an error (never a " +
                     "silent fallback to the summary). Empty = summary only (header + block census + shape names).")]
            string sections = "",
        [Description("Optional. Inspect a specific provider's copy instead of the VFS winner — the mod folder " +
                     "name, 'overwrite', 'Data', or a BSA filename. Pass the name EXACTLY as the providers chain " +
                     "shows it INSIDE the double quotes; the kind after them ('loose' / 'BSA') is not part of the name. " +
                     "Naming a MOD reaches that mod's loose files AND its own root archives, whether or not MO2 is " +
                     "loading it, so a donor mod can be read without enabling it; the response then SAYS the game is " +
                     "not loading that copy. '*winner' is the winner pole spelled out. A name that provides no copy " +
                     "of a given mesh is THAT path's own named miss, listing the providers that do; the rest of the " +
                     "batch still reads. Applies to every mesh " +
                     "in the batch. Empty = the winner.")]
            string mod = "",
        [Description("Optional. Max characters before the output is cut with an explicit notice — one cap over the " +
                     "WHOLE batch's render, not per mesh. 0 = the server default (~80k).")]
            int max_chars = 0) => Guard.Tool(ToolNames.NifInspect, () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        bool anyPath = mesh_paths is not null && mesh_paths.Any(p => !string.IsNullOrWhiteSpace(p));
        bool anyNpc = npc is not null && npc.Any(p => !string.IsNullOrWhiteSpace(p));
        if (!anyPath && !anyNpc)
        {
            if (mesh_paths is { Length: > 0 })
                return "error: mesh_paths contains only empty/blank entries. Pass Data-relative mesh paths (e.g. 'meshes\\armor\\iron\\cuirass_1.nif').";
            if (npc is { Length: > 0 })
                return "error: npc contains only empty/blank entries. Pass NPC FormIDs (e.g. '01A51A:Dawnguard.esm').";
            return "error: nothing to inspect. Pass mesh_paths (Data-relative mesh paths, e.g. 'meshes\\armor\\iron\\cuirass_1.nif') and/or npc (NPC FormIDs, whose FaceGen head mesh is derived).";
        }

        // npc= is a SELECT value, not a mode: each FormID becomes the FaceGen mesh path it derives to and joins the
        // same batch, read exactly like a path the caller typed.
        var selected = new List<string>((mesh_paths?.Length ?? 0) + (npc?.Length ?? 0));
        if (mesh_paths is not null) selected.AddRange(mesh_paths);
        if (anyNpc)
        {
            var door = svc.OpenFormIdDoor();
            foreach (var raw in npc!)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                try { selected.Add(FaceGenPath.For(door.Parse(raw), FaceGenSlot.Mesh)); }
                catch (Exception ex)
                {
                    return FormIdDoor.Sentence(ex, "error: ",
                        $"error: bad npc FormID '{raw.Trim()}' ({ex.Message}). Expected 'XXXXXX:Plugin.esp' or a runtime FormID.");
                }
            }
        }

        var (want, unknownTokens) = ParseSections(sections);
        // Sections were requested but none resolved: fail rather than render the summary as if that were the answer.
        // A partial request proceeds, rendering the valid sections plus a warning.
        if (SectionsError(want, unknownTokens) is { } sectionsErr) return sectionsErr;

        var data = svc.NifInspect(selected, string.IsNullOrWhiteSpace(mod) ? null : mod);
        return NifWire.Render(data, want, unknownTokens, max_chars > 0 ? max_chars : 80_000);
    });

    /// <summary>Parse the sections argument into the recognized set plus any unrecognized tokens, which are surfaced
    /// rather than ignored. 'all' expands to every known section. Tolerates the JSON-array-as-string form an MCP
    /// client naturally sends: <c>sections=["shapes","paths"]</c> arrives as that literal string, so brackets and
    /// quotes are split delimiters too — otherwise they glue onto the first and last tokens and the whole array reads
    /// as unrecognized.</summary>
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

    /// <summary>The error string for a call that requested sections where none resolved — a typo, or the non-existent
    /// 'textures'. Null when the request is fine: nothing requested, so the summary is the intended answer, or at
    /// least one section resolved, in which case a partial request renders the valid ones with a warning.</summary>
    internal static string? SectionsError(HashSet<string> want, IReadOnlyList<string> unknown)
        => want.Count == 0 && unknown.Count > 0
            ? $"error: no recognized section(s) in sections — unrecognized: {string.Join(", ", unknown)}  " +
              $"({KnownSectionsHint}). Pass one or more known sections, or omit sections= for the summary only."
            : null;

    [McpServerTool(Name = ToolNames.NifSet, Title = "Write a whitelisted data value into a Skyrim mesh (.nif)"),
     Description(
         "Write ONE whitelisted DATA VALUE into a Skyrim SE mesh (.nif) at the data layer, beneath NifSkope — then VERIFY " +
         "the edit before anything lands. Resolve the Data-relative mesh_path through Mod Organizer 2's VFS to the winning " +
         "copy (or mod=), apply the op, and pass it two offset-immune verification gates (only the block/value the op " +
         "claims to touch changed; a reload re-reads the new value; census + SE-stream intact) — a failure writes NOTHING " +
         "and says why. The ops (op=): rename_shape / rename_node (retitle a baked shape/node — the HDPT-EDID facegen " +
         "case), set_flags (NiAVObject flags on a shape/node — the 0x80000 head/hair-class bit), set_scale, set_partition " +
         "(a BSDismember body-part id — pass body_part_id [+ partition_index]), set_alpha (alpha_flags word and/or " +
         "alpha_threshold — the hair 0x12ED / hairline 0x12EE class), set_path (swap an asset reference, TWO addressing " +
         "forms: with texture_slot + path it swaps that BSShaderTextureSet slot on the named shape — e.g. the FaceTint " +
         "slot 6 or skin slots 0/1; with NO texture_slot it swaps the HEADER STRING target names for path — the " +
         "material (.bgsm), .tri / BODYTRI and physics-xml refs that sections=strings lists), set_shader_value (a shader LIGHTING value — " +
         "pass shader_value + value: glossiness, specular_strength, specular_color, emissive_color, emissive_multiple, " +
         "alpha; the plastic-looking armour or over-bright glow fix. NOT set_alpha — that is the separate NiAlphaProperty). " +
         "target= is the shape or node NAME the op edits " +
         "(from " + ToolNames.NifInspect + "). By DEFAULT the verified mesh is written into a NEW houseCARL MO2 mod folder at the " +
         "same path (originals untouched) — enable it and sort it ABOVE the current winner so the edit wins; a BSA-packed " +
         "source becomes a loose winning override this way. in_place=true instead OVERWRITES the winning LOOSE file where " +
         "it sits (opt-in; rides the per-file consent handshake, needs acknowledge=true, NO backup). Only edits " +
         "data VALUES — never geometry / vertices / the .dds pixels. Refuses loud (Q3): a non-SE mesh, a target it can't " +
         "find or that's ambiguous, an op that doesn't apply, or any verification miss.")]
    public static string NifSet(
        LoadOrderService svc,
        [Description("The Data-relative mesh path to edit, e.g. 'meshes\\actors\\character\\facegendata\\facegeom\\Skyrim.esm\\00000007.nif'.")]
            string mesh_path,
        // Built from OpList, a const so it is legal in an attribute, rather than spelled out: this is the string a
        // caller reads to choose an op, so a stale list here would make a shipped op invisible in the tool schema.
        [Description("The write op — one of: " + OpList + ".")]
            string op,
        [Description("What the op edits, as it currently reads (from " + ToolNames.NifInspect + "). For most ops the NAME of a " +
                     "shape or node; for a rename the OLD name; for set_path WITHOUT texture_slot the header STRING to " +
                     "replace, exactly as sections=strings prints it (case-sensitive).")]
            string target,
        [Description("rename_shape / rename_node: the new name.")] string new_name = "",
        [Description("set_flags: the NiAVObject flags value — hex ('0x800000E') or decimal.")] string flags = "",
        [Description("set_scale: the scale, e.g. '1.0'.")] string scale = "",
        [Description("set_partition: the BSDismember body-part id, e.g. '32' (SBP_32_BODY).")] string body_part_id = "",
        [Description("set_partition: which partition to change when a shape has more than one (0-based). Omit if it has exactly one.")] string partition_index = "",
        [Description("set_alpha: the 16-bit alpha flags word — hex ('0x12ED') or decimal. Optional if only changing the threshold.")] string alpha_flags = "",
        [Description("set_alpha: the alpha test threshold, 0-255. Optional if only changing the flags word.")] string alpha_threshold = "",
        [Description("set_path: the BSShaderTextureSet slot index (0 diffuse, 1 normal, 6 tint/skin/detail, ...). OMIT it to swap the header string target= names instead.")] string texture_slot = "",
        [Description("set_path: the new path — a texture (Data-relative, e.g. 'textures\\...\\facetint\\Mod.esp\\00000ABC.dds') with texture_slot, or the replacement header string (a .bgsm material, a .tri, a physics xml) without it.")] string path = "",
        [Description("set_shader_value: which lighting value — 'glossiness', 'specular_strength', 'specular_color', 'emissive_color', 'emissive_multiple', or 'alpha'.")] string shader_value = "",
        [Description("set_shader_value: the new value — one number for a scalar ('30'), or three comma-separated components for a colour ('1,0.5,0.25'). Colours and alpha are conventionally 0-1 (NOT 0-255); a value outside that is written as asked but WARNED about.")] string value = "",
        [Description("Optional. Edit a specific provider's copy instead of the VFS winner — the mod folder name, 'overwrite', " +
                     "'Data', or a BSA filename. Pass the name EXACTLY as the providers chain shows it INSIDE the double " +
                     "quotes; the kind after them ('loose' / 'BSA') is not part of the name. Naming a MOD reaches that mod's " +
                     "loose files AND its own root archives, whether or not MO2 is loading it — a copy the game is NOT " +
                     "loading is stated on the default lane and refused by in_place. '*winner' is the winner pole spelled " +
                     "out. Empty = the winner.")]
            string mod = "",
        [Description("Optional. Base name for the NEW mod folder the edited mesh is written into (default lane; auto-suffixed if taken). Ignored with in_place=true.")]
            string patch_name = "",
        [Description("Optional. Write into an EXISTING houseCARL-owned mod folder instead of a fresh one (default lane). Mutually exclusive with in_place.")]
            string into = "",
        [Description("Optional, default false. IN-PLACE LANE (opt-in): OVERWRITE the winning LOOSE file where it sits instead of writing a new folder — NO backup. Requires acknowledge=true (see below). OMIT (the default) to write a new winning override and leave the original untouched.")]
            bool in_place = false,
        [Description("Optional, default false. Confirms the one-time in-place trade-off for this file — needed only on the FIRST in-place edit of a given mesh, and not again once one has LANDED — a call that is refused records nothing, so it may be needed again. Waives the consent to overwrite your original ONLY; it NEVER skips the mesh verification.")]
            bool acknowledge = false) => Guard.Tool(ToolNames.NifSet, () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        if (string.IsNullOrWhiteSpace(mesh_path)) return "error: mesh_path is empty. Pass a Data-relative mesh path.";
        if (string.IsNullOrWhiteSpace(op)) return "error: op is empty. Pass one of: " + OpList + ".";
        if (string.IsNullOrWhiteSpace(target)) return "error: target is empty. Pass the shape/node NAME the op edits (from " + ToolNames.NifInspect + ").";

        var (built, buildErr) = BuildOp(op.Trim(), target.Trim(), new_name, flags, scale, body_part_id, partition_index, alpha_flags, alpha_threshold, texture_slot, path, shader_value, value);
        if (buildErr is not null) return "error: " + buildErr;

        var data = svc.NifSet(mesh_path, new[] { built! },
            string.IsNullOrWhiteSpace(mod) ? null : mod,
            string.IsNullOrWhiteSpace(patch_name) ? null : patch_name,
            string.IsNullOrWhiteSpace(into) ? null : into,
            in_place, acknowledge);
        return NifSetWire.Render(data);
    });

    /// <summary>Turn the flat tool params into one <see cref="NifSetOp"/>, or a named error for an unknown op or an
    /// unparseable value, before the value reaches the service. Numeric params are strings so an omitted one is
    /// distinguishable from a real 0 — partition_index 0 is valid.</summary>
    static (NifSetOp? Op, string? Error) BuildOp(string op, string target,
        string newName, string flags, string scale, string bodyPartId, string partitionIndex, string alphaFlags, string alphaThreshold, string textureSlot, string path,
        string shaderValue, string value)
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
                if (string.IsNullOrWhiteSpace(path)) return (null, "set_path needs a path.");
                // No texture_slot = the header-string form: target IS the string to replace. One op, two addressing
                // forms, chosen by whether a slot was given.
                if (string.IsNullOrWhiteSpace(textureSlot))
                    return (new NifSetOp(NifSetOpKind.SetPath, target, Path: path), null);
                if (!int.TryParse(textureSlot, out var slot))
                    return (null, $"set_path's texture_slot must be a number; got '{textureSlot}'. Omit it entirely to swap a header string instead (target = the string as {ToolNames.NifInspect} sections=strings prints it).");
                return (new NifSetOp(NifSetOpKind.SetPath, target, TextureSlot: slot, Path: path), null);
            case "set_shader_value":
            {
                if (string.IsNullOrWhiteSpace(shaderValue)) return (null, $"set_shader_value needs a shader_value ({NifService.ShaderValueList}).");
                if (NifService.ShaderValueProperty(shaderValue) is null)
                    return (null, $"unknown shader_value '{shaderValue}'. Use one of: {NifService.ShaderValueList}.");
                if (string.IsNullOrWhiteSpace(value)) return (null, "set_shader_value needs a value — one number for a scalar, or 'r,g,b' for a colour.");
                // Parsed to a bare list here; the arity is checked in core against the library's own property type, so
                // how many components a value takes has exactly one owner.
                var parts = value.Split(new[] { ',', ' ', ';', '[', ']' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var nums = new List<float>(parts.Length);
                foreach (var p in parts)
                {
                    if (!float.TryParse(p, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f))
                        return (null, $"set_shader_value: '{p}' in value is not a number. Pass one number for a scalar, or 'r,g,b' for a colour.");
                    nums.Add(f);
                }
                if (nums.Count == 0) return (null, "set_shader_value needs a value — one number for a scalar, or 'r,g,b' for a colour.");
                return (new NifSetOp(NifSetOpKind.SetShaderValue, target, ShaderValue: shaderValue.Trim(), ShaderNumbers: nums), null);
            }
            default:
                return (null, $"unknown op '{op}'. Use one of: {OpList}.");
        }
    }

    /// <summary>The op names in one place; every "unknown op" and "op is empty" refusal reads from it, so adding an op
    /// cannot leave a stale list behind in a message.</summary>
    internal const string OpList = "rename_shape, rename_node, set_flags, set_scale, set_partition, set_alpha, set_path, set_shader_value";

    /// <summary>Parse a uint from hex ('0x...') or decimal.</summary>
    static bool TryParseUInt(string s, out uint value)
    {
        s = (s ?? "").Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value);
        return uint.TryParse(s, out value);
    }
}

/// <summary>Renders <see cref="NifInspectBatchData"/>: the build-level alarms first and once — archives that failed to
/// read, discovery warnings — then one block per mesh in input order, giving the resolution and, on a clean read, the
/// summary and any requested detail sections. An absent, bad-path, unreadable or parse-refused result is that path's
/// own error line and does not cost the rest of the batch. Output is bounded by max_chars with an explicit cut notice
/// naming how many meshes were omitted.</summary>
static class NifWire
{
    public static string Render(NifInspectBatchData d, HashSet<string> want, IReadOnlyList<string> unknownSections, int cap)
    {
        var header = new StringBuilder("nif inspect — profile '")
            .Append(d.ProfileName.Length > 0 ? d.ProfileName : "(unconfigured)")
            .Append("'  (").Append(d.Results.Count).Append(" mesh").Append(d.Results.Count == 1 ? "" : "es")
            .Append(')').ToString();

        bool readIncomplete = d.BsaFailures.Count > 0, discoveryIncomplete = d.Warnings.Count > 0;
        return BatchRender.Render(
            header, d.Results, "mesh(es)", cap,
            // The alarms come first and once, at batch level, so a long batch cannot truncate them away.
            sb =>
            {
                BatchRender.AppendReadFailures(sb, d.BsaFailures, "a mesh", cap);
                BatchRender.AppendDiscoveryWarnings(sb, d.Warnings, cap);
                if (unknownSections.Count > 0)
                    sb.Append("\n[!] unrecognized section(s) ignored: ").Append(string.Join(", ", unknownSections))
                      .Append("  (").Append(NifTools.KnownSectionsHint).Append(")\n");
            },
            (sb, r) => AppendMesh(sb, r, want, cap, readIncomplete, discoveryIncomplete));
    }

    /// <summary>One mesh's block: the path line, then either its named error with the provider chain where there is
    /// one, or the resolution, summary and requested detail sections. An ABSENT is hedged at the point of use on both
    /// batch-level scan caveats — an archive that failed to read, and archives never discovered — because the
    /// top-of-output alarm scrolls away in a long batch.</summary>
    static void AppendMesh(StringBuilder sb, NifInspectData d, HashSet<string> want, int cap, bool readIncomplete, bool discoveryIncomplete)
    {
        sb.Append('\n').Append(d.RelPath.Length > 0 ? d.RelPath : "(empty path)").Append('\n');

        // Error path: absent, bad path, unreadable, or parse-refused. Still show the provider chain where there is one.
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
        sb.Append("  read from: ").Append(d.Inspected!.Text).Append('\n');
        if (d.Inspected.Provenance is { } prov) sb.Append("  [!] ").Append(prov).Append(".\n");
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
            // The library flagged unknown blocks but none resolved to a nameable NiUnknown — say so rather than render
            // a bare "0 type(s)".
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
        // Nothing ACTIVE provides the path — routine once mod= can reach a copy the game is not loading. Say that
        // rather than print a chain header with nothing after it.
        if (providers.Count == 0) { sb.Append("  providers: none — nothing in the active load order supplies this path\n"); return; }
        sb.Append("  providers (").Append(providers.Count).Append("): ");
        for (int i = 0; i < providers.Count; i++)
        {
            if (i > 0) sb.Append(" > ");
            sb.Append(providers[i].Text);
        }
        sb.Append('\n');
    }

    static void RenderShapesDetail(StringBuilder sb, NifInspect nif, int cap)
    {
        sb.Append("\n--- shapes (").Append(nif.Shapes.Count).Append(") ---\n");
        if (SlotNamingCaveat(nif) is { } shapesCaveat) sb.Append(shapesCaveat);
        int shown = 0;   // the cut notice counts the remainder, not the total
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
        var matched = nif.Shapes.Where(has).ToList();   // the omitted remainder counts the filtered subset, not total shapes
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
        var textured = nif.Shapes.Where(s => s.Textures.Count > 0).ToList();   // the omitted remainder counts the filtered subset, not total shapes
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

    /// <summary>One texture slot line, shared by the shapes and paths sections. The index is always printed and the
    /// semantic name rides alongside it rather than replacing it, because the index is what nif_set's texture_slot=
    /// takes. A slot whose meaning the shape's shader does not determine prints bare, so unnamed reads as "this shader
    /// does not say" rather than as a wrong label.</summary>
    static void AppendTexture(StringBuilder sb, NifTexture t)
    {
        sb.Append("    tex[").Append(t.Slot).Append(']');
        if (t.SlotName is not null) sb.Append(" (").Append(t.SlotName).Append(')');
        sb.Append(": ").Append(t.Path).Append('\n');
    }

    /// <summary>The shader section: per shape, the block type and shader type enum, the decoded flag words, and the
    /// lighting values. Multi-line per shape rather than one long line, because a visual diagnosis reads it top to
    /// bottom.</summary>
    static void RenderShader(StringBuilder sb, NifInspect nif, int cap)
    {
        sb.Append("\n--- shader (per shape; slot names above come from these type+flags) ---\n");
        var shaded = nif.Shapes.Where(s => s.Shader is not null).ToList();   // the omitted remainder counts the filtered subset
        int shown = 0;
        foreach (var s in shaded)
        {
            if (Cut(sb, cap, shaded.Count - shown)) return;
            var sh = s.Shader!;
            sb.Append("  '").Append(s.Name).Append("': ").Append(sh.BlockType);
            // A block that does not serialize a shader type says so, rather than reporting a default-valued one.
            sb.Append(sh.ShaderType is null ? "  (no shader type on this block)" : "  type " + sh.ShaderType);
            sb.Append("  [").Append(sh.GameType).Append(" layout]\n");
            // The decline is stated, not just performed: slot naming models a Skyrim convention, so on any other
            // layout every slot prints bare — identical output to "this Skyrim shader does not determine that slot",
            // which means something entirely different.
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

    /// <summary>One decoded flag word. Unnamed bits are stated as an explicit hex mask: a bit the library's enum does
    /// not name is still something the mesh carries, so it is surfaced rather than dropped or folded into the named
    /// list.</summary>
    static void AppendFlagWord(StringBuilder sb, NifShaderFlagWord? w)
    {
        if (w is null) return;
        sb.Append("    ").Append(w.Label).Append(" 0x").Append(w.Raw.ToString("X8")).Append(": ")
          .Append(w.Names.Count > 0 ? string.Join(", ", w.Names) : "(no named bit set)");
        if (w.UnknownBits != 0) sb.Append("  (+unknown bits 0x").Append(w.UnknownBits.ToString("X")).Append(')');
        sb.Append('\n');
    }

    /// <summary>The shader's lighting values — only the ones this NiflySharp version genuinely reads off the block.
    /// The rest are named as unread on their own line rather than printed as the constant the library's interface stub
    /// hands back, so a caller can tell "the mesh says 0" from "we cannot see it". Every value has both a read form
    /// and an unread form with no shared condition between them, so none can fall through both and vanish. The
    /// emissive multiple gets its own entry when the colour is unread, rather than only riding as a suffix of the
    /// colour, because upstream could implement it alone — it maps onto BSEffectShaderProperty's
    /// <c>_baseColorScale</c>.</summary>
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
        // Two different reasons produce an unreported value. On a non-Skyrim layout these are declined as a matter of
        // scope — several would read fine there, so blaming the library would be false — hence a separate sentence.
        if (!IsSkyrimLayout(sh))
        {
            // No constant is quoted here, and the one example is scoped to the block it holds for: a stub's fixed
            // value is a fact about a particular block type, not about a layout. Of the blocks implementing INiShader
            // only BSLightingShaderProperty carries Glossiness at all, so naming a number would describe a field an
            // FO3NV or Oblivion-era lighting block does not have.
            sb.Append("    lighting values: NOT INTERPRETED for this block — houseCARL models the Skyrim shader "
                      + "layout, and this reads as the ").Append(sh.GameType).Append(" layout. Some of these "
                      + "accessors read a field this layout's stream never carried, so they would answer a constant "
                      + "rather than this mesh's value (glossiness is the live case, on a block that carries it). "
                      + "Rather than interpret some and guess at the rest, all are declined: ")
              .Append(string.Join(", ", missing)).Append(". NifSkope reads this mesh's own layout.\n");
            return;
        }
        // "where this block carries them" is load-bearing: a lighting shader really does have these values on disk and
        // NifSkope shows them, but a BSEffectShaderProperty has no glossiness, specular-strength or specular-colour
        // field at all, so an unconditional "the values are in the file" would send the reader hunting for fields that
        // do not exist.
        sb.Append("    NOT READ by this NiflySharp version — its accessor returns a constant for these, so ")
          .Append("houseCARL reports nothing rather than a wrong number: ")
          .Append(string.Join(", ", missing))
          .Append(". Where this block carries them, NifSkope shows the real values.\n");
    }

    static string ColorText(NifColor c) => $"rgb({Fmt(c.R)},{Fmt(c.G)},{Fmt(c.B)})";

    /// <summary>Whether this shader was read as the Skyrim layout — the only one whose texture-slot semantics are
    /// modelled here, so the only one where a slot name can be derived at all.</summary>
    static bool IsSkyrimLayout(NifShader sh) => sh.GameType == "SK";

    /// <summary>The one-line caveat for a section showing slot paths on a mesh whose shaders are not interpreted, or
    /// null when every shader here is the Skyrim layout. Without it a bare <c>tex[2]:</c> is ambiguous between "this
    /// Skyrim shader does not determine slot 2" and "this layout is not modelled at all". The header's
    /// <c>[NOT an SE stream]</c> marker does not disambiguate it: an LE mesh trips that marker but still parses as the
    /// SK layout and does get named slots.</summary>
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

    /// <summary>Render NiAVObject flags: raw hex plus a decode. nif.xml does not name these bits, so the decode is by
    /// deviation from the type's nif.xml-documented SSE default (<paramref name="def"/> from
    /// <paramref name="defType"/>) — either "= default" or the extra and missing bits against it — and always states
    /// the one bit nif.xml does document, 0x80000, which Skyrim sets on some AV objects and FO4 never does. With no
    /// documented default for the type, the set-bit positions are listed instead: still exact, still no invented
    /// names.</summary>
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

    /// <summary>Append "&lt;label&gt;&lt;a, b, c&gt;" as one line, cut with an explicit notice if it would exceed the
    /// cap.</summary>
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
        BatchRender.AppendCut(sb, remaining, "", cap);
        return true;
    }

    static string Fmt(float f) => f.ToString("0.#######", System.Globalization.CultureInfo.InvariantCulture);

}

/// <summary>Renders <see cref="NifSetResult"/>: the resolution — which copy was edited, and the provider chain — then
/// exactly one of the in-place consent prompt (carried verbatim; a required confirmation, not an error), a named
/// refusal with nothing written, or the verified-write success with the op's before and after, what verification
/// confirmed changed, and where the file landed. A default-lane success says the file was written and must now be
/// enabled and sorted; it never claims the edit is already winning on disk.</summary>
static class NifSetWire
{
    public static string Render(NifSetResult d)
    {
        var sb = new StringBuilder();
        sb.Append("nif set — ").Append(d.RelPath.Length > 0 ? d.RelPath : "(mesh)")
          .Append("  (profile '").Append(d.ProfileName.Length > 0 ? d.ProfileName : "(unconfigured)").Append("')\n");

        // in-place first-touch consent: a required confirmation returned verbatim, not an error.
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
        if (d.Edited is not null) sb.Append("  edited copy: ").Append(d.Edited.Text).Append('\n');
        if (d.Edited?.Provenance is { } prov) sb.Append("  [!] ").Append(prov).Append(".\n");
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
            // mod= is answered ahead of the ABSENT return, so a successful write can land with NO current winner —
            // the donor was off-order and nothing active supplied the path. There is nothing to sort above then, and
            // saying so would name a winner that does not exist. Same branch place_asset's render already has.
            if (d.CurrentWinner is null)
                sb.Append("  TO MAKE IT WIN: nothing else provides this path — once '")
                  .Append(Path.GetFileName(d.OutputModFolder) is { Length: > 0 } f ? f : "the new folder")
                  .Append("' is enabled in MO2, the edited copy wins. 'Wrote it' is not 'it wins' until you do.\n");
            else
                sb.Append("  TO MAKE IT WIN: enable this folder in MO2 and sort it ABOVE ")
                  .Append(d.CurrentWinner)
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
        // Same sentence as the inspect chain's: an empty chain is a fact, not a reason to print a bare header.
        if (providers.Count == 0) { sb.Append("  providers: none — nothing in the active load order supplies this path\n"); return; }
        sb.Append("  providers (").Append(providers.Count).Append("): ");
        for (int i = 0; i < providers.Count; i++)
        {
            if (i > 0) sb.Append(" > ");
            sb.Append(providers[i].Text);
        }
        sb.Append('\n');
    }
}
