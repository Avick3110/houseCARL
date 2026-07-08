using NiflySharp;
using NiflySharp.Blocks;

namespace HousecarlCore;

/// <summary>
/// The NIF-layer format service (Wave 1: read). Turns the RAW BYTES of a Skyrim mesh into the data model behind
/// <c>housecarl_nif_inspect</c> — the header/version, the block census, the shapes with their N2-whitelist values
/// (names, NiAVObject flags + scale, BSDismember partitions, alpha property, texture-set paths, bone lists), the node
/// tree, and the header string table. PURE format logic: bytes in, model out — it knows nothing of MO2, the VFS, or
/// which mod won (that seam is <see cref="LoadOrderService"/>, which resolves the winning bytes and hands them here),
/// so it is fully testable off a synthetic in-memory mesh with no live workspace (nif-service-guard).
///
/// Reads ride NiflySharp 1.0.0 (NuGet <c>Nifly</c>) — pure C#, source-generated from nifxml, spike-proven over 87,937
/// workspace nifs (SPIKE_NIF_LAYER_2026-07-08: 99.98% loose / 100.00% vanilla parse, zero unknown blocks in any SE
/// mesh). Library quirks coded around per the spike: the alpha/shader/skin refs are read DIRECTLY
/// (<see cref="INiShape.AlphaPropertyRef"/> etc.) — NEVER via <c>NifFile.GetPropertyOfType&lt;T&gt;</c>, which NREs on
/// SE-style shapes whose legacy <c>Properties</c> list is null.
///
/// Fail-loud (Q3, PRFAQ N6): a parse failure is a NAMED, recoverable outcome (<see cref="NifInspectOutcome.Error"/>),
/// not a throw or a half-model — including NiflySharp's one strict-boolean rejection class, which houseCARL surfaces
/// with the NifSkope remedy rather than hand-patching around. Unknown blocks are REPORTED (count + their real on-disk
/// type names) and left intact — the model tells the truth about what it could and could not model.
/// </summary>
public static class NifService
{
    // Skyrim SE header identity: NIF 20.2.0.7, user version 12, stream version 100 (LE = stream 83, FO4 = 130).
    const uint SkyrimSeUserVersion = 12;
    const uint SkyrimSeStreamVersion = 100;

    /// <summary>Inspect a mesh from its raw bytes. Returns the model on success, or a named error (Q3) — a parse the
    /// library rejects, an empty buffer, or a structure read that failed after a clean load. Never throws for an
    /// expected bad-file condition; never returns a partial model.</summary>
    public static NifInspectOutcome Inspect(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0)
            return new NifInspectOutcome(null, "the mesh is empty (0 bytes) — nothing to inspect.");

        var nif = new NifFile();
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            int rc = nif.Load(ms);
            if (rc != 0)
                return new NifInspectOutcome(null,
                    $"NiflySharp could not parse this mesh (Load returned {rc}) — it may be truncated, not a NIF, or a " +
                    "format the library rejects. If NifSkope opens it, it is a valid-but-nonstandard file houseCARL will not guess at.");
        }
        catch (Exception ex)
        {
            return new NifInspectOutcome(null, DescribeLoadException(ex));
        }

        try
        {
            return new NifInspectOutcome(Build(nif), null);
        }
        catch (Exception ex)
        {
            // The file loaded but reading its structure threw — a real defect surface, not an expected bad file. Fail
            // loud with the type + message; never hand back a half-built model (Q3).
            return new NifInspectOutcome(null, $"the mesh parsed but reading its structure failed — {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Turn a load exception into a named, actionable error. NiflySharp's one strict-boolean rejection class
    /// (the spike's 13 loud failures — "Byte value for boolean is > 2!", a nonstandard byte some exporters write) is
    /// called out by name with the NifSkope remedy, so the tool never reads as a silent "absent" and houseCARL never
    /// hand-patches around the strict read.</summary>
    static string DescribeLoadException(Exception ex)
    {
        var m = ex.Message ?? "";
        if (m.Contains("boolean", StringComparison.OrdinalIgnoreCase))
            return "NiflySharp refused this mesh: a boolean field holds a non-0/1 byte, which the library rejects strictly " +
                   "(some exporters write it). The file is otherwise a valid SE mesh — NifSkope can open it — and houseCARL " +
                   $"will not hand-patch around the strict read. ({ex.GetType().Name}: {m})";
        return $"NiflySharp threw while parsing this mesh — {ex.GetType().Name}: {m}";
    }

    static NifInspect Build(NifFile nif)
    {
        var header = nif.Header;
        var version = header.Version;
        uint user = version.UserVersion;
        uint stream = version.StreamVersion;
        bool isSe = user == SkyrimSeUserVersion && stream == SkyrimSeStreamVersion;

        int blockCount = header.BlockCount;
        var blocks = nif.Blocks;   // List<INiObject>, indexed by block id (parallel to Header.GetBlockTypeNameById)

        // Block census by ON-DISK type name (what xEdit/NifSkope show) — this also names unknown blocks faithfully,
        // where GetType().Name would flatten every one of them to "NiUnknown".
        var typeById = new string[blockCount];
        for (int i = 0; i < blockCount; i++) typeById[i] = header.GetBlockTypeNameById(i) ?? "?";
        var blockTypes = typeById
            .GroupBy(t => t)
            .Select(g => new NifBlockTypeCount(g.Key, g.Count()))
            .OrderByDescending(c => c.Count).ThenBy(c => c.Type, StringComparer.Ordinal)
            .ToList();

        // Unknown blocks reported by their real on-disk type (preserved-but-opaque — PRFAQ N6). GetType() on a
        // NiUnknown is just "NiUnknown"; the informative name lives in the header's block-type table.
        var unknownTypes = new List<string>();
        for (int i = 0; i < blocks.Count && i < blockCount; i++)
            if (blocks[i] is NiUnknown) unknownTypes.Add(typeById[i]);
        var unknownDistinct = unknownTypes.Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList();

        var shapes = new List<NifShape>();
        foreach (var shape in nif.GetShapes()) shapes.Add(BuildShape(nif, shape));

        return new NifInspect(
            version.VersionString ?? "", user, stream, isSe,
            blockCount, blockTypes,
            nif.HasUnknownBlocks, unknownDistinct,
            shapes, BuildNodeTree(nif), ReadHeaderStrings(header));
    }

    /// <summary>One shape's N2-whitelist values. Every ref is read DIRECTLY off the shape (the SE-safe path); the
    /// partition list is present only for a BSDismember skin instance, alpha only when the shape carries an alpha
    /// property, and a texture slot only when its path is non-empty.</summary>
    static NifShape BuildShape(NifFile nif, INiShape shape)
    {
        string name = shape.Name?.String ?? "";
        uint flags = 0; float scale = 1f;
        if (shape is NiAVObject av) { flags = av.Flags_ui; scale = av.Scale; }

        var partitions = new List<NifPartition>();
        if (shape.SkinInstanceRef is not null && nif.GetBlock(shape.SkinInstanceRef) is BSDismemberSkinInstance dis)
            foreach (var p in dis.Partitions)
                partitions.Add(new NifPartition((int)p.BodyPart, p.BodyPart.ToString(), (int)p.PartFlag));

        NifAlpha? alpha = null;
        if (shape.HasAlphaProperty && nif.GetBlock<NiAlphaProperty>(shape.AlphaPropertyRef) is { } ap)
        {
            var f = ap.Flags;
            alpha = new NifAlpha(f.Value, f.AlphaBlend, f.SourceBlendMode.ToString(), f.DestinationBlendMode.ToString(),
                                 f.AlphaTest, f.TestFunc.ToString(), ap.Threshold);
        }

        var textures = new List<NifTexture>();
        var shader = nif.GetShader(shape);
        if (shader?.TextureSetRef is not null && nif.GetBlock(shader.TextureSetRef) is BSShaderTextureSet ts)
            for (int i = 0; i < ts.Textures.Count; i++)
            {
                var c = ts.Textures[i]?.Content;
                if (!string.IsNullOrEmpty(c)) textures.Add(new NifTexture(i, c));
            }

        var bones = nif.GetShapeBoneNames(shape) ?? new List<string>();
        return new NifShape(name, flags, scale, partitions, alpha, textures, bones);
    }

    /// <summary>Pre-order the NiNode hierarchy from the root(s), depth-annotated, each node with its NiAVObject flags.
    /// Only NiNode children are walked (shapes are covered by <see cref="NifInspect.Shapes"/>). A reference-identity
    /// visited set guards against a malformed file's cycle so the walk always terminates.</summary>
    static List<NifNode> BuildNodeTree(NifFile nif)
    {
        var nodes = new List<NifNode>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

        void Walk(NiNode node, int depth)
        {
            if (node is null || !seen.Add(node)) return;
            uint flags = node is NiAVObject av ? av.Flags_ui : 0u;
            nodes.Add(new NifNode(depth, node.Name?.String ?? "", flags));
            foreach (var cref in node.Children.References)
                if (nif.GetBlock(cref) is NiNode child) Walk(child, depth + 1);
        }

        foreach (var root in nif.GetRootNodes()) Walk(root, 0);
        return nodes;
    }

    /// <summary>The header string table (shape/node/bone names, material and .tri/BODYTRI/physics-xml paths). The table
    /// is a flat, contiguous list; <c>GetString(i)</c> returns "" — never null — once past the end, so we read from 0
    /// until the first empty slot. A legitimately-empty entry ends the read early: an HONEST bound (the high-value
    /// strings sit contiguous at the front), never a silent wrong answer. Capped so a corrupt count cannot spin.</summary>
    static List<string> ReadHeaderStrings(NiHeader header)
    {
        var strings = new List<string>();
        const int cap = 8192;
        for (int i = 0; i < cap; i++)
        {
            var s = header.GetString(i);
            if (string.IsNullOrEmpty(s)) break;
            strings.Add(s);
        }
        return strings;
    }
}

// ======================================================================
//  NIF-layer data model — the format-level result of an inspect (core; the service layer wraps it with VFS info).
// ======================================================================

/// <summary>The outcome of <see cref="NifService.Inspect"/>: exactly one of <see cref="Inspect"/> (success) or
/// <see cref="Error"/> (a named, recoverable parse/read failure — Q3). Never both, never neither.</summary>
public sealed record NifInspectOutcome(NifInspect? Inspect, string? Error);

/// <summary>Everything <see cref="NifService.Inspect"/> can model about a mesh, built in full (a single mesh is
/// sub-second — the renderer chooses which sections to show). <see cref="IsSkyrimSE"/> is the header identity
/// (user 12 / stream 100); <see cref="UnknownBlockTypes"/> names any preserved-but-opaque block by its on-disk type.</summary>
public sealed record NifInspect(
    string VersionString,
    uint UserVersion,
    uint StreamVersion,
    bool IsSkyrimSE,
    int BlockCount,
    IReadOnlyList<NifBlockTypeCount> BlockTypes,
    bool HasUnknownBlocks,
    IReadOnlyList<string> UnknownBlockTypes,
    IReadOnlyList<NifShape> Shapes,
    IReadOnlyList<NifNode> Nodes,
    IReadOnlyList<string> HeaderStrings);

/// <summary>One block type and how many of it the mesh has, by the on-disk (xEdit/NifSkope) type name.</summary>
public sealed record NifBlockTypeCount(string Type, int Count);

/// <summary>One shape and its N2-whitelist values. <see cref="Partitions"/> is empty unless the shape has a
/// BSDismember skin instance; <see cref="Alpha"/> is null unless it carries an alpha property; <see cref="Textures"/>
/// lists only non-empty texture-set slots.</summary>
public sealed record NifShape(
    string Name,
    uint Flags,
    float Scale,
    IReadOnlyList<NifPartition> Partitions,
    NifAlpha? Alpha,
    IReadOnlyList<NifTexture> Textures,
    IReadOnlyList<string> Bones);

/// <summary>One BSDismember partition: the body-part id, its decoded enum name (e.g. SBP_30_HEAD), and the part flags.</summary>
public sealed record NifPartition(int BodyPartId, string BodyPartName, int PartFlags);

/// <summary>An NiAlphaProperty decoded: the raw 16-bit flags word plus the semantic pieces (blend on/off + source and
/// destination blend functions, alpha test on/off + test function, and the 0–255 threshold).</summary>
public sealed record NifAlpha(
    ushort Flags,
    bool Blend,
    string SourceBlendMode,
    string DestinationBlendMode,
    bool Test,
    string TestFunction,
    byte Threshold);

/// <summary>One embedded texture path at its BSShaderTextureSet slot index (0 diffuse, 1 normal, … 6 tint/detail, …).</summary>
public sealed record NifTexture(int Slot, string Path);

/// <summary>One node in the pre-order NiNode tree: its depth (0 = root), name, and NiAVObject flags.</summary>
public sealed record NifNode(int Depth, string Name, uint Flags);
