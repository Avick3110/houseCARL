using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Helpers;   // ShaderHelper.ShaderGameType — which game's flag layout a shader block was read as

namespace HousecarlCore;

/// <summary>The NIF format layer: raw mesh bytes in, the model behind <c>housecarl_nif_inspect</c> out. Pure format
/// logic, knowing nothing of MO2 or the VFS; contract in docs/architecture/nif.md.</summary>
public static class NifService
{
    // Skyrim SE header identity: NIF 20.2.0.7, user version 12, stream version 100 (LE = stream 83, FO4 = 130).
    const uint SkyrimSeUserVersion = 12;
    const uint SkyrimSeStreamVersion = 100;

    // NiAVObject flag defaults per concrete type, transcribed from nif.xml's own <default onlyT=...> table.
    internal static readonly IReadOnlyDictionary<string, uint> AvFlagsSseDefaults = new Dictionary<string, uint>(StringComparer.Ordinal)
    {
        ["NiNode"] = 0xE, ["NiLight"] = 0xE, ["BSMultiBoundNode"] = 0xE,
        ["BSTriShape"] = 0x8000E, ["BSSubIndexTriShape"] = 0xE, ["BSMeshLODTriShape"] = 0x100E,
        ["BSFadeNode"] = 0x8000E, ["NiParticleSystem"] = 0x8000E, ["BSMasterParticleSystem"] = 0x8000E,
        ["BSStripParticleSystem"] = 0x8000E, ["NiTriShape"] = 0x8000E, ["NiTriStrips"] = 0x8000E,
        ["BSSegmentedTriShape"] = 0xE, ["BSLeafAnimNode"] = 0x808000E, ["BSTreeNode"] = 0x8080E,
        ["BSDebrisNode"] = 0x8000F, ["BSBlastNode"] = 0x8000F, ["BSDamageStage"] = 0x8000F,
        ["BSOrderedNode"] = 0x8200E, ["BSLODTriShape"] = 0x800000E,
    };

    /// <summary>The SSE flag default for a block, resolved UP the inheritance chain per nif.xml's onlyT semantics; SE meshes only.</summary>
    static (uint? Value, string? FromType) ResolveAvDefault(Type? t)
    {
        for (var cur = t; cur is not null && cur != typeof(object); cur = cur.BaseType)
            if (AvFlagsSseDefaults.TryGetValue(cur.Name, out var v)) return (v, cur.Name);
        return (null, null);
    }

    /// <summary>Inspect a mesh from its raw bytes — the model, or a named error; never a throw and never a partial model.</summary>
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
            // The file loaded but reading its structure threw — a real defect, so fail loud with the type and message.
            return new NifInspectOutcome(null, $"the mesh parsed but reading its structure failed — {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Turn a load exception into a named, actionable error; the strict-boolean arm is kept against that rejection class returning.</summary>
    static string DescribeLoadException(Exception ex)
    {
        var m = ex.Message ?? "";
        // A count inside a block, or the block itself, runs past the block's stored size; the key does not say which, so lead with the usual cause.
        if (NifLoadErrors.IsBlockSizeMismatch(ex))
            return $"the mesh is malformed and was not read ({m.TrimEnd('.')}) — a count in that block is larger than the block's " +
                   "stored size allows, which usually means the count is corrupted, so reinstall whatever ships it (the mod, or the " +
                   "game files for a vanilla mesh) or open it in NifSkope to see where it breaks.";
        // NiflySharp's bound on a count, length or size that cannot fit in the bytes left in the file.
        if (ex is InvalidDataException)
            return $"the mesh is malformed and was not read ({m.TrimEnd('.')}) — the file is damaged or cut short, so reinstall " +
                   "whatever ships it (the mod, or the game files for a vanilla mesh) or open it in NifSkope to see where it breaks.";
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

        // Block census by ON-DISK type name, because GetType().Name flattens every unknown block to "NiUnknown".
        var typeById = new string[blockCount];
        for (int i = 0; i < blockCount; i++) typeById[i] = header.GetBlockTypeNameById(i) ?? "?";
        var blockTypes = typeById
            .GroupBy(t => t)
            .Select(g => new NifBlockTypeCount(g.Key, g.Count()))
            .OrderByDescending(c => c.Count).ThenBy(c => c.Type, StringComparer.Ordinal)
            .ToList();

        // Unknown blocks are preserved but opaque, and named from the header's block-type table.
        var unknownTypes = new List<string>();
        for (int i = 0; i < blocks.Count && i < blockCount; i++)
            if (blocks[i] is NiUnknown) unknownTypes.Add(typeById[i]);
        var unknownDistinct = unknownTypes.Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList();

        var shapes = new List<NifShape>();
        foreach (var shape in nif.GetShapes()) shapes.Add(BuildShape(nif, shape, isSe));

        return new NifInspect(
            version.VersionString ?? "", user, stream, isSe,
            blockCount, blockTypes,
            nif.HasUnknownBlocks, unknownDistinct,
            shapes, BuildNodeTree(nif, isSe), ReadHeaderStrings(header));
    }

    /// <summary>One shape's whitelisted values, every ref read DIRECTLY off the shape (the SE-safe path).</summary>
    static NifShape BuildShape(NifFile nif, INiShape shape, bool isSe)
    {
        string name = shape.Name?.String ?? "";
        uint flags = 0; float scale = 1f;
        if (shape is NiAVObject av) { flags = av.Flags_ui; scale = av.Scale; }
        var (defVal, defType) = isSe ? ResolveAvDefault(shape.GetType()) : (null, null);

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
        var shaderInfo = shader is null ? null : BuildShader(shader);
        if (shader?.TextureSetRef is not null && nif.GetBlock(shader.TextureSetRef) is BSShaderTextureSet ts)
            for (int i = 0; i < ts.Textures.Count; i++)
            {
                var c = ts.Textures[i]?.Content;
                if (!string.IsNullOrEmpty(c)) textures.Add(new NifTexture(i, c, SlotName(i, shader)));
            }

        var bones = nif.GetShapeBoneNames(shape) ?? new List<string>();
        return new NifShape(name, flags, scale, shape.GetType().Name, defVal, defType, partitions, alpha, textures, bones, shaderInfo);
    }

    /// <summary>Read the shape's shader property off <see cref="INiShader"/>, so every block nifly models as a shader
    /// is covered with no per-type wiring; the layout gates are in docs/architecture/nif.md.</summary>
    static NifShader BuildShader(INiShader shader)
    {
        var blockType = shader.GetType().Name;
        var game = shader.Type;

        // The flag pair that is REAL for this block's game layout; nifly names each game's word as its own enum.
        (NifShaderFlagWord? f1, NifShaderFlagWord? f2) = game switch
        {
            ShaderHelper.ShaderGameType.SK    => (DecodeFlagWord("SLSF1", shader.ShaderFlags_SSPF1), DecodeFlagWord("SLSF2", shader.ShaderFlags_SSPF2)),
            ShaderHelper.ShaderGameType.FO4   => (DecodeFlagWord("F4SPF1", shader.ShaderFlags_F4SPF1), DecodeFlagWord("F4SPF2", shader.ShaderFlags_F4SPF2)),
            ShaderHelper.ShaderGameType.FO3NV => (DecodeFlagWord("ShaderFlags", shader.ShaderFlags), DecodeFlagWord("ShaderFlags2", shader.ShaderFlags2)),
            // FO76/SF (and None) carry no flag word this library names — report the game type and no flags.
            _ => (null, null),
        };

        // Two gates, both in docs/architecture/nif.md: the block really reads the value (ReallyReads), and the block
        // was read as the Skyrim layout. RGB, never RGBA, because the interface widens a Color3 on disk to Color4.
        var t = shader.GetType();
        var skyrimLayout = game == ShaderHelper.ShaderGameType.SK;
        NifColor? Rgb3(string prop, Func<NifColor> read) => skyrimLayout && ReallyReads(t, prop) ? read() : null;
        float? Scalar(string prop, Func<float> read) => skyrimLayout && ReallyReads(t, prop) ? read() : null;

        return new NifShader(
            blockType, game.ToString(), ShaderTypeName(shader, blockType, game),
            f1, f2,
            Rgb3(nameof(INiShader.EmissiveColor), () => { var e = shader.EmissiveColor; return new NifColor(e.R, e.G, e.B); }),
            Scalar(nameof(INiShader.EmissiveMultiple), () => shader.EmissiveMultiple),
            Scalar(nameof(INiShader.Glossiness), () => shader.Glossiness),
            Scalar(nameof(INiShader.SpecularStrength), () => shader.SpecularStrength),
            Rgb3(nameof(INiShader.SpecularColor), () => { var s = shader.SpecularColor; return new NifColor(s.R, s.G, s.B); }),
            Scalar(nameof(INiShader.Alpha), () => shader.Alpha));
    }

    /// <summary>The shader's TYPE enum name, picked by the LAYOUT the block was parsed as, or null where no field
    /// carries it honestly — the two ways this reads a confident wrong default are in docs/architecture/nif.md.</summary>
    static string? ShaderTypeName(INiShader shader, string blockType, ShaderHelper.ShaderGameType game)
    {
        if (blockType == nameof(BSEffectShaderProperty)) return null;   // serializes no shader type on any layout
        return game switch
        {
            ShaderHelper.ShaderGameType.SK or ShaderHelper.ShaderGameType.FO4 => shader.ShaderType_SK_FO4.ToString(),
            ShaderHelper.ShaderGameType.FO76SF => shader.ShaderType_FO76_SF.ToString(),
            ShaderHelper.ShaderGameType.FO3NV => shader.ShaderType_FO3_NV.ToString(),
            _ => null,
        };
    }

    // Which INiShader accessors a concrete block really implements, cached per block type.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, HashSet<string>> RealReads = new();

    /// <summary>Whether <paramref name="blockType"/> REALLY reads <paramref name="property"/> or merely inherits
    /// <see cref="INiShader"/>'s stub, off the interface map; contract in docs/architecture/nif.md, pinned by
    /// NifShaderDecodeTests.EveryLightingValueRoundTripsItsAuthoredValue and .EveryLightingValueABlockOnlyStubsIsReportedUnread.</summary>
    static bool ReallyReads(Type blockType, string property)
    {
        var real = RealReads.GetOrAdd(blockType, static t =>
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            var map = t.GetInterfaceMap(typeof(INiShader));
            for (int i = 0; i < map.InterfaceMethods.Length; i++)
            {
                var name = map.InterfaceMethods[i].Name;
                if (name.StartsWith("get_", StringComparison.Ordinal) && map.TargetMethods[i].DeclaringType != typeof(INiShader))
                    set.Add(name[4..]);
            }
            return set;
        });
        return real.Contains(property);
    }

    /// <summary>The six shader lighting values by wire name, each paired to its library property through <c>nameof</c>
    /// so an upstream rename is a compile error; <c>Normalized</c> marks the 0–1 convention that warns rather than refuses.</summary>
    internal static readonly IReadOnlyList<(string Wire, string Property, bool Normalized)> ShaderValueNames = new[]
    {
        ("emissive_color",    nameof(INiShader.EmissiveColor),    true),
        ("emissive_multiple", nameof(INiShader.EmissiveMultiple), false),
        ("glossiness",        nameof(INiShader.Glossiness),       false),
        ("specular_strength", nameof(INiShader.SpecularStrength), false),
        ("specular_color",    nameof(INiShader.SpecularColor),    true),
        ("alpha",             nameof(INiShader.Alpha),            true),
    };

    /// <summary>A WARN-and-proceed note when a write lands outside the 0–1 convention, never a refusal.</summary>
    internal static string? ShaderRangeWarning(string wire, IReadOnlyList<float> nums)
    {
        var prop = ShaderValueProperty(wire);
        if (prop is null) return null;
        bool normalized = false;
        foreach (var (_, p, n) in ShaderValueNames) if (p == prop) normalized = n;
        if (!normalized) return null;
        var bad = nums.Where(v => v < 0f || v > 1f).ToList();
        if (bad.Count == 0) return null;
        return $"{WireName(prop)} was set to {string.Join(", ", bad.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)))} "
             + "— outside the 0-1 range Skyrim's shader treats this value as. Written as asked (the format stores the "
             + "float given, and real meshes do carry out-of-range values), but if this came from NifSkope's 0-255 "
             + "colour picker, divide by 255.";
    }

    /// <summary>Resolve a caller's value name to its library property, or null; the British spelling is an alias, and
    /// the case fold must run BEFORE the colour→color rewrite, which is ordinal.</summary>
    public static string? ShaderValueProperty(string wire)
    {
        var w = (wire ?? "").Trim().Replace('-', '_').ToLowerInvariant().Replace("colour", "color");
        foreach (var (n, p, _) in ShaderValueNames) if (n == w) return p;
        return null;
    }

    /// <summary>Whether <paramref name="blockType"/> can really be WRITTEN at <paramref name="property"/>, and with
    /// how many float components — reflected off the CONCRETE class, because <see cref="ReallyReads"/> cannot answer
    /// it; contract in docs/architecture/nif.md, pinned by NifSetGuardProbe (all three states).</summary>
    internal static NifShaderWritability ReallyWrites(Type blockType, string property)
    {
        var p = blockType.GetProperty(property, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (p is null || !p.CanWrite) return NifShaderWritability.NoSetter();
        if (p.PropertyType == typeof(float)) return NifShaderWritability.Ok(1);
        if (HasRgbFields(p.PropertyType)) return NifShaderWritability.Ok(3);
        // Settable, but of a shape houseCARL has no marshalling for — a different fact from "no setter", so a different reason.
        return NifShaderWritability.UnknownType(p.PropertyType.Name);
    }

    static bool HasRgbFields(Type t)
        => t.GetField("R") is not null && t.GetField("G") is not null && t.GetField("B") is not null;

    /// <summary>The accepted shader_value names as one list, built from the table so no refusal can go stale.</summary>
    public static string ShaderValueList => string.Join(", ", ShaderValueNames.Select(v => v.Wire));

    /// <summary>The WIRE name for a library property, so refusals speak the caller's vocabulary.</summary>
    static string WireName(string property)
    {
        foreach (var (w, p, _) in ShaderValueNames) if (p == property) return w;
        return property;
    }

    /// <summary>A shader value rendered for the before/after audit — rgb only, never a component the format does not carry.</summary>
    static string DescribeShaderValue(object? v, int components)
    {
        if (v is null) return "(null)";
        if (components == 1) return Convert.ToSingle(v).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var t = v.GetType();
        var (r, g, b) = ReadRgb(v, t);
        return $"rgb({r.ToString(System.Globalization.CultureInfo.InvariantCulture)},"
             + $"{g.ToString(System.Globalization.CultureInfo.InvariantCulture)},"
             + $"{b.ToString(System.Globalization.CultureInfo.InvariantCulture)})";
    }

    static (float R, float G, float B) ReadRgb(object v, Type t)
        => ((float)t.GetField("R")!.GetValue(v)!, (float)t.GetField("G")!.GetValue(v)!, (float)t.GetField("B")!.GetValue(v)!);

    /// <summary>Decode one shader flag word into its NAMED bits plus the unnamed remainder, off nifly's own enum;
    /// contract in docs/architecture/nif.md.</summary>
    internal static NifShaderFlagWord DecodeFlagWord(string label, Enum value)
    {
        uint raw = Convert.ToUInt32(value);
        var members = new List<(uint Bits, string Name)>();
        foreach (Enum m in Enum.GetValues(value.GetType()))
        {
            uint mb = Convert.ToUInt32(m);
            if (mb != 0) members.Add((mb, m.ToString()));
        }
        members.Sort((a, b) => b.Bits.CompareTo(a.Bits));        // descending — a combo before its constituent bits

        uint remainder = raw;
        var hit = new List<(uint Bits, string Name)>();
        foreach (var m in members)
            if ((remainder & m.Bits) == m.Bits) { hit.Add(m); remainder &= ~m.Bits; }
        hit.Sort((a, b) => a.Bits.CompareTo(b.Bits));            // report in bit order — how the word reads on disk
        return new NifShaderFlagWord(label, raw, hit.Select(h => h.Name).ToList(), remainder);
    }

    /// <summary>The SEMANTIC name of a BSShaderTextureSet slot, from the shader TYPE and FLAGS rather than the index,
    /// or null rather than a best guess. SKYRIM LAYOUT ONLY, and that gate is load-bearing: contract in
    /// docs/architecture/nif.md, pinned by NifShaderDecodeTests.EverySlotIsUnnamedOnANonSkyrimLayout.</summary>
    internal static string? SlotName(int slot, INiShader shader) =>
        shader.Type != ShaderHelper.ShaderGameType.SK ? null : slot switch
    {
        0 => "Diffuse",                                          // universal across every shader type
        1 => "Normal",                                           // universal (model-space when the MSN flag is set — the flag list says which)
        2 => shader.HasGlowmap ? "GlowMap"
           : shader.HasSoftlight ? "SoftLighting"
           : shader.IsTypeSkinTint || shader.IsTypeFaceTint ? "SubsurfaceTint"
           : null,
        3 => shader.Parallax || shader.IsTypeParallax || shader.IsTypeParallaxOcclusion ? "Height" : null,
        4 => EnvMapped(shader) ? "Environment" : null,
        5 => EnvMapped(shader) ? "EnvironmentMask" : null,
        6 => shader.IsTypeMultiLayerParallax ? "InnerLayer"
           : shader.IsTypeFaceTint || shader.IsTypeSkinTint || shader.IsTypeHairTint ? "TintMask"
           : null,
        7 => shader.HasBacklight ? "BacklightMask"
           : shader.ModelSpace ? "Specular"                      // MSN meshes carry specular in 7 (the normal's alpha is used up)
           : null,
        _ => null,                                               // beyond the Skyrim slot set — say nothing
    };

    /// <summary>Whether this shader environment-maps at all — the condition slots 4 and 5 both hang on.</summary>
    static bool EnvMapped(INiShader shader)
        => shader.HasEnvironmentMapping || shader.HasEyeEnvironmentMapping
        || shader.IsTypeEnvironmentMap || shader.IsTypeEyeEnvironmentMap;

    /// <summary>Pre-order the NiNode hierarchy, depth-annotated; a reference-identity visited set terminates a malformed cycle.</summary>
    static List<NifNode> BuildNodeTree(NifFile nif, bool isSe)
    {
        var nodes = new List<NifNode>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

        void Walk(NiNode node, int depth)
        {
            if (node is null || !seen.Add(node)) return;
            uint flags = node is NiAVObject av ? av.Flags_ui : 0u;
            var (defVal, defType) = isSe ? ResolveAvDefault(node.GetType()) : (null, null);
            nodes.Add(new NifNode(depth, node.Name?.String ?? "", flags, node.GetType().Name, defVal, defType));
            foreach (var cref in node.Children.References)
                if (nif.GetBlock(cref) is NiNode child) Walk(child, depth + 1);
        }

        foreach (var root in nif.GetRootNodes()) Walk(root, 0);
        return nodes;
    }

    /// <summary>The header string table, read from 0 until the first empty slot — an honest bound, capped so a corrupt count cannot spin.</summary>
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

    // Whitelisted NIF writes (housecarl_nif_set): pure bytes-in / verified-bytes-out.

    /// <summary>Apply the whitelisted write op(s) to a mesh's raw bytes and hand back the VERIFIED edited bytes, or a
    /// named refusal with nothing written. The refusal set and the two offset-immune gates are in
    /// docs/architecture/nif.md; the writer never hands back an unverified mesh.</summary>
    public static NifSetOutcome Set(byte[] bytes, IReadOnlyList<NifSetOp> ops)
    {
        if (bytes is null || bytes.Length == 0)
            return NifSetOutcome.Fail("the mesh is empty (0 bytes) — nothing to edit.");
        if (ops is null || ops.Count == 0)
            return NifSetOutcome.Fail("no write op was given.");

        // ---- parse (same fail-loud posture as Inspect) ----
        var nif = new NifFile();
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            if (nif.Load(ms) != 0)
                return NifSetOutcome.Fail("NiflySharp could not parse this mesh — it may be truncated, not a NIF, or a format the library rejects. Nothing was written.");
        }
        catch (Exception ex) { return NifSetOutcome.Fail(DescribeLoadException(ex) + " Nothing was written."); }

        NifInspect pre;
        try { pre = Build(nif); }
        catch (Exception ex) { return NifSetOutcome.Fail($"the mesh parsed but reading its structure failed — {ex.GetType().Name}: {ex.Message}. Nothing was written."); }

        // ---- SE-stream gate: nif_set refuses a non-SE mesh by name ----
        if (!pre.IsSkyrimSE)
            return NifSetOutcome.Fail(
                $"this is NOT a Skyrim SE mesh (user {pre.UserVersion} / stream {pre.StreamVersion}; SE is user 12 / stream 100). " +
                "nif_set only writes SE-stream meshes — a normalized cross-game (LE / FO4 / Starfield) write is untested and refused. Nothing was written.");

        // ---- apply each op; record the exact block(s)/header each is allowed to touch ----
        var applied = new List<NifOpResult>(ops.Count);
        var touched = new List<NiflySharp.INiObject>();
        bool expectHeader = false;
        foreach (var op in ops)
        {
            var r = ApplyOp(nif, op);
            if (r.Error is not null) return NifSetOutcome.Fail(r.Error);   // target-not-found / ambiguous / not-applicable → nothing written
            applied.Add(new NifOpResult(op.Kind.ToString(), r.Target!, r.Before!, r.After!));
            if (r.TouchedBlock is { } b) touched.Add(b);
            if (r.TouchedHeader) expectHeader = true;
        }

        // ---- save the edited mesh to memory ----
        byte[] edited;
        try
        {
            using var outMs = new MemoryStream();
            if (nif.Save(outMs) != 0) return NifSetOutcome.Fail("NiflySharp failed to save the edited mesh. Nothing was written.");
            edited = outMs.ToArray();
        }
        catch (Exception ex) { return NifSetOutcome.Fail($"saving the edited mesh threw — {ex.GetType().Name}: {ex.Message}. Nothing was written."); }

        // Ids are resolved only AFTER the save, which re-sorts the block list; see docs/architecture/nif.md.
        var expectedBlocks = new HashSet<int>();
        foreach (var b in touched)
        {
            int id = BlockIndexOf(nif, b);
            if (id < 0) return NifSetOutcome.Fail("verification could not find an edited block in the saved mesh — refusing to write. Nothing was written.");
            expectedBlocks.Add(id);
        }

        // ---- GATE 1: block-content diff (offset-immune) ----
        var g1 = VerifyBlockContent(bytes, edited, expectedBlocks, expectHeader);
        if (g1 is not null) return NifSetOutcome.Fail(g1);

        // ---- GATE 2: semantic read-back ----
        var g2 = VerifyReadBack(edited, pre, ops, out var warnings);
        if (g2 is not null) return NifSetOutcome.Fail(g2);

        // The ops' own warn-and-proceed notes, gathered only now: a warning about a failed write would be noise.
        var allWarnings = new List<string>();
        foreach (var op in ops)
            if (op.Kind == NifSetOpKind.SetShaderValue && op.ShaderValue is { } sv
                && ShaderRangeWarning(sv, op.ShaderNumbers ?? Array.Empty<float>()) is { } rw)
                allWarnings.Add(rw);
        allWarnings.AddRange(warnings);

        var report = new NifSetReport(applied,
            expectedBlocks.OrderBy(i => i).ToList(), expectHeader,
            edited.Length - bytes.Length, allWarnings);
        return new NifSetOutcome(edited, report, null);
    }

    /// <summary>Apply one op, returning before/after plus the single block it may change, or a named error that
    /// aborts the call; the two NiflySharp mutation rules are in docs/architecture/nif.md.</summary>
    static (string? Error, string? Target, string? Before, string? After, NiflySharp.INiObject? TouchedBlock, bool TouchedHeader) ApplyOp(NifFile nif, NifSetOp op)
    {
        switch (op.Kind)
        {
            case NifSetOpKind.RenameShape:
            {
                if (string.IsNullOrEmpty(op.NewName)) return ("rename_shape needs a new_name.", null, null, null, null, false);
                var (shape, err) = ResolveShape(nif, op.Target);
                if (err is not null) return (err, null, null, null, null, false);
                // Refuse renaming ONTO an existing name: it manufactures ambiguity, and gate 2 would false-pass.
                if (nif.GetShapes().Any(s => !ReferenceEquals(s, shape) && (s.Name?.String ?? "") == op.NewName))
                    return ($"a shape is already named '{op.NewName}' — renaming onto it would create an ambiguous duplicate. Refusing, nothing written.", null, null, null, null, false);
                var av = (NiflySharp.Blocks.NiAVObject)shape!;
                string before = av.Name?.String ?? "";
                av.Name = new NiStringRef(op.NewName);
                return (null, op.Target, before, op.NewName, null, true);   // a Name lives ONLY in the header string table
            }
            case NifSetOpKind.RenameNode:
            {
                if (string.IsNullOrEmpty(op.NewName)) return ("rename_node needs a new_name.", null, null, null, null, false);
                var (node, err) = ResolveNode(nif, op.Target);
                if (err is not null) return (err, null, null, null, null, false);
                if (nif.Blocks.OfType<NiNode>().Any(n => !ReferenceEquals(n, node) && (n.Name?.String ?? "") == op.NewName))
                    return ($"a node is already named '{op.NewName}' — renaming onto it would create an ambiguous duplicate. Refusing, nothing written.", null, null, null, null, false);
                string before = node!.Name?.String ?? "";
                node.Name = new NiStringRef(op.NewName);
                return (null, op.Target, before, op.NewName, null, true);
            }
            case NifSetOpKind.SetFlags:
            {
                if (op.Flags is not { } flags) return ("set_flags needs a flags value.", null, null, null, null, false);
                var (av, err) = ResolveAvObject(nif, op.Target);
                if (err is not null) return (err, null, null, null, null, false);
                string before = $"0x{av!.Flags_ui:X}";
                av.Flags_ui = flags;
                return (null, op.Target, before, $"0x{flags:X}", av, false);
            }
            case NifSetOpKind.SetScale:
            {
                if (op.Scale is not { } scale) return ("set_scale needs a scale value.", null, null, null, null, false);
                var (av, err) = ResolveAvObject(nif, op.Target);
                if (err is not null) return (err, null, null, null, null, false);
                string before = av!.Scale.ToString(System.Globalization.CultureInfo.InvariantCulture);
                av.Scale = scale;
                return (null, op.Target, before, scale.ToString(System.Globalization.CultureInfo.InvariantCulture), av, false);
            }
            case NifSetOpKind.SetAlpha:
            {
                if (op.AlphaFlags is null && op.AlphaThreshold is null) return ("set_alpha needs an alpha_flags word and/or an alpha_threshold.", null, null, null, null, false);
                var (shape, err) = ResolveShape(nif, op.Target);
                if (err is not null) return (err, null, null, null, null, false);
                if (!shape!.HasAlphaProperty || nif.GetBlock<NiAlphaProperty>(shape.AlphaPropertyRef) is not { } ap)
                    return ($"shape '{op.Target}' has no alpha property to set. Nothing was written.", null, null, null, null, false);
                string before = $"0x{ap.Flags.Value:X4}/thr{ap.Threshold}";
                if (op.AlphaFlags is { } fw) { var fl = ap.Flags; fl.Value = fw; ap.Flags = fl; }   // AlphaFlags is a STRUCT — reassign
                if (op.AlphaThreshold is { } th) ap.Threshold = th;
                return (null, op.Target, before, $"0x{ap.Flags.Value:X4}/thr{ap.Threshold}", ap, false);
            }
            case NifSetOpKind.SetPartition:
            {
                if (op.BodyPartId is not { } bp) return ("set_partition needs a body_part_id.", null, null, null, null, false);
                var (shape, err) = ResolveShape(nif, op.Target);
                if (err is not null) return (err, null, null, null, null, false);
                if (shape!.SkinInstanceRef is null || nif.GetBlock(shape.SkinInstanceRef) is not BSDismemberSkinInstance dis)
                    return ($"shape '{op.Target}' has no BSDismember skin instance — no partitions to set. Nothing was written.", null, null, null, null, false);
                var list = dis.Partitions;
                if (list is null || list.Count == 0) return ($"shape '{op.Target}' has an empty partition list. Nothing was written.", null, null, null, null, false);
                int idx;
                if (op.PartitionIndex is { } pi) { if (pi < 0 || pi >= list.Count) return ($"partition_index {pi} out of range (shape '{op.Target}' has {list.Count} partition(s)). Nothing was written.", null, null, null, null, false); idx = pi; }
                else if (list.Count == 1) idx = 0;
                else return ($"shape '{op.Target}' has {list.Count} partitions — pass partition_index to say which. Nothing was written.", null, null, null, null, false);
                string before = $"[{idx}]={(int)list[idx].BodyPart}";
                var p = list[idx]; p.BodyPart = (NiflySharp.Enums.BSDismemberBodyPartType)bp; list[idx] = p; dis.Partitions = list;   // list of STRUCT — reassign
                return (null, op.Target, before, $"[{idx}]={bp}", dis, false);
            }
            case NifSetOpKind.SetPath:
            {
                if (op.Path is null) return ("set_path needs a path.", null, null, null, null, false);
                // Two addressing forms, one op: a texture-set SLOT on a named shape, or — with no slot — the header
                // STRING itself, which is how a material (.bgsm), a .tri or a physics-xml ref is carried.
                if (op.TextureSlot is not { } slot) return SetHeaderString(nif, op);
                var (shape, err) = ResolveShape(nif, op.Target);
                if (err is not null) return (err, null, null, null, null, false);
                var shader = nif.GetShader(shape!);
                if (shader?.TextureSetRef is null || nif.GetBlock(shader.TextureSetRef) is not BSShaderTextureSet ts)
                    return ($"shape '{op.Target}' has no shader texture set — no path to swap. Nothing was written.", null, null, null, null, false);
                if (slot < 0 || slot >= ts.Textures.Count)
                    return ($"texture_slot {slot} out of range (shape '{op.Target}' has {ts.Textures.Count} slot(s)). Nothing was written.", null, null, null, null, false);
                var tex = ts.Textures[slot] ?? new NiflySharp.NiString4();
                string before = tex.Content ?? "";
                tex.Content = op.Path; ts.Textures[slot] = tex;
                return (null, op.Target, $"tex[{slot}]={before}", $"tex[{slot}]={op.Path}", ts, false);
            }
            case NifSetOpKind.SetShaderValue:
            {
                if (string.IsNullOrWhiteSpace(op.ShaderValue)) return ($"set_shader_value needs a shader_value name ({ShaderValueList}).", null, null, null, null, false);
                if (ShaderValueProperty(op.ShaderValue) is not { } prop)
                    return ($"unknown shader_value '{op.ShaderValue}'. Use one of: {ShaderValueList}.", null, null, null, null, false);
                var nums = op.ShaderNumbers ?? Array.Empty<float>();
                var (shape, err) = ResolveShape(nif, op.Target);
                if (err is not null) return (err, null, null, null, null, false);
                if (nif.GetShader(shape!) is not { } shader)
                    return ($"shape '{op.Target}' has no shader property — no lighting value to set. Nothing was written.", null, null, null, null, false);

                // GATE A — THE LAYOUT, the same scope claim the read path makes; it can only refuse, never fabricate.
                if (shader.Type != ShaderHelper.ShaderGameType.SK)
                    return ($"shape '{op.Target}' carries a {shader.Type} shader layout, not Skyrim's — houseCARL models the Skyrim "
                          + $"shader layout only, and some of these values address a field a {shader.Type} stream never carries "
                          + "(the write would be accepted and change nothing). Refusing. Nothing was written.", null, null, null, null, false);

                // GATE B — THE BLOCK TYPE; see ReallyWrites, and the refusal names the block whose accessor is a stub.
                var bt = shader.GetType();
                var w = ReallyWrites(bt, prop);
                if (!w.Writable && w.UnknownTypeName is { } badType)
                    return ($"this NiflySharp version exposes {WireName(prop)} on a {bt.Name} as a {badType}, which houseCARL has no "
                          + "marshalling for — the value IS settable, but houseCARL will not write a shape it cannot convert "
                          + "correctly. Refusing rather than guess (Q3). Please file this: it means the bundled library changed "
                          + "the value's type. Nothing was written.", null, null, null, null, false);
                if (!w.Writable)
                    return ($"this NiflySharp version cannot write {WireName(prop)} on a {bt.Name} — the value is not settable on that "
                          + "block type, so the write would be accepted and change nothing. Refusing (Q3). "
                          + $"{ToolNames.NifInspect} sections=shader names what it can and cannot see on this block. Nothing was written.", null, null, null, null, false);
                var components = w.Components;
                if (nums.Count != components)
                    return ($"{WireName(prop)} on a {bt.Name} takes {components} number{(components == 1 ? "" : "s")}"
                          + $"{(components == 3 ? " (r,g,b — conventionally 0-1)" : "")}; got {nums.Count}. Nothing was written.", null, null, null, null, false);

                var pi = bt.GetProperty(prop, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!;
                string before = DescribeShaderValue(pi.GetValue(shader), components);
                object boxed;
                if (components == 1) boxed = nums[0];
                else
                {
                    // READ-MODIFY-WRITE: the value is a STRUCT, and any component beyond rgb is carried over, never invented.
                    boxed = pi.GetValue(shader)!;
                    var t = boxed.GetType();
                    t.GetField("R")!.SetValue(boxed, nums[0]);
                    t.GetField("G")!.SetValue(boxed, nums[1]);
                    t.GetField("B")!.SetValue(boxed, nums[2]);
                }
                pi.SetValue(shader, boxed);
                return (null, op.Target, $"{WireName(prop)}={before}",
                        $"{WireName(prop)}={DescribeShaderValue(pi.GetValue(shader), components)}", shader, false);
            }
            default:
                return ($"unsupported op '{op.Kind}'.", null, null, null, null, false);
        }
    }

    // ---- target resolution: not-found and ambiguous are named refusals, never a silent first-match write ----

    static (INiShape? Shape, string? Error) ResolveShape(NifFile nif, string name)
    {
        var matches = nif.GetShapes().Where(s => (s.Name?.String ?? "") == name).ToList();
        if (matches.Count == 0) return (null, $"no shape named '{name}' in this mesh. Shapes: {ShapeNames(nif)}. Nothing was written.");
        if (matches.Count > 1) return (null, $"more than one shape is named '{name}' — ambiguous, refusing rather than guess which. Nothing was written.");
        return (matches[0], null);
    }

    /// <summary>set_path's HEADER-STRING form: swap the header string equal to <c>op.Target</c>, addressed by its own
    /// current VALUE, so every reference to it moves together. Touches the header only; the three refusals that keep
    /// the whitelist a whitelist are in docs/architecture/nif.md.</summary>
    static (string? Error, string? Target, string? Before, string? After, NiflySharp.INiObject? TouchedBlock, bool TouchedHeader)
        SetHeaderString(NifFile nif, NifSetOp op)
    {
        var target = op.Target;
        if (target.Length == 0)
            return ("set_path with no texture_slot swaps a HEADER STRING, so target must be the string to replace (from "
                  + "nif_inspect sections=strings). Nothing was written.", null, null, null, null, false);
        if (target == op.Path)
            return ($"the header string '{target}' already reads that way — nothing to change. Nothing was written.", null, null, null, null, false);

        var all = HeaderStringRefs(nif).ToList();
        var refs = all.Where(r => (r.String ?? "") == target).ToList();
        if (refs.Count == 0)
            return ($"no header string in this mesh reads '{target}'. Pass the string EXACTLY as {ToolNames.NifInspect} "
                  + "sections=strings prints it (matching is case-sensitive). Nothing was written.", null, null, null, null, false);

        // A named shape/node has its own op, with guards this form does not repeat.
        foreach (var av in nif.Blocks.OfType<NiflySharp.Blocks.NiAVObject>())
            if (av.Name is { } n && refs.Any(r => ReferenceEquals(r, n)))
                return ($"'{target}' is the NAME of a shape or node, not an asset reference — use op=rename_shape or "
                      + "op=rename_node, which refuse renaming onto a name already in use. Nothing was written.", null, null, null, null, false);

        // An extra-data block's Name is its KEY, not a path; its VALUE is the asset ref to swap.
        foreach (var ed in nif.Blocks.OfType<NiflySharp.Blocks.NiExtraData>())
            if (ed.Name is { } k && refs.Any(r => ReferenceEquals(r, k)))
                return ($"'{target}' is the KEY an extra-data block is looked up by, not an asset reference — swapping "
                      + "it would hide the block from the engine. Pass the block's VALUE instead. Nothing was written.", null, null, null, null, false);

        // Renumbering is indistinguishable from a collateral write at gate 1, so it is refused by name.
        if (all.Any(r => (r.String ?? "") == op.Path))
            return ($"'{op.Path}' is already a header string in this mesh, and pointing a second reference at it would "
                  + "renumber the string table — a change the write verification cannot tell from a collateral edit. "
                  + "Nothing was written.", null, null, null, null, false);

        foreach (var r in refs) r.String = op.Path;
        return (null, target, target, op.Path, null, true);
    }

    /// <summary>Every <see cref="NiStringRef"/> a block carries — the authored half, since the table is regenerated on save.</summary>
    static IEnumerable<NiStringRef> HeaderStringRefs(NifFile nif)
    {
        foreach (var b in nif.Blocks)
        {
            if (b is null) continue;
            var refs = b.StringRefs;
            if (refs is null) continue;
            foreach (var r in refs) if (r is not null) yield return r;
        }
    }

    static (NiNode? Node, string? Error) ResolveNode(NifFile nif, string name)
    {
        var matches = nif.Blocks.OfType<NiNode>().Where(n => (n.Name?.String ?? "") == name).ToList();
        if (matches.Count == 0) return (null, $"no node named '{name}' in this mesh. Nothing was written.");
        if (matches.Count > 1) return (null, $"more than one node is named '{name}' — ambiguous, refusing rather than guess which. Nothing was written.");
        return (matches[0], null);
    }

    /// <summary>Resolve a named NiAVObject, a shape OR a node; ambiguity is a named refusal.</summary>
    static (NiflySharp.Blocks.NiAVObject? Av, string? Error) ResolveAvObject(NifFile nif, string name)
    {
        var matches = nif.Blocks.OfType<NiflySharp.Blocks.NiAVObject>().Where(a => (a.Name?.String ?? "") == name).ToList();
        if (matches.Count == 0) return (null, $"no shape or node named '{name}' in this mesh. Nothing was written.");
        if (matches.Count > 1) return (null, $"more than one shape/node is named '{name}' — ambiguous, refusing rather than guess which. Nothing was written.");
        return (matches[0], null);
    }

    static string ShapeNames(NifFile nif) => string.Join(", ", nif.GetShapes().Select(s => "'" + (s.Name?.String ?? "") + "'"));

    /// <summary>The block id of a block by reference identity — call it AFTER the save, which re-sorts the list.</summary>
    static int BlockIndexOf(NifFile nif, NiflySharp.INiObject block)
    {
        var blocks = nif.Blocks;
        for (int i = 0; i < blocks.Count; i++) if (ReferenceEquals(blocks[i], block)) return i;
        return -1;
    }

    /// <summary>GATE 1 — block-content diff (docs/architecture/nif.md): null when only the expected block(s)/header
    /// changed, else a named refusal. Internal so NifSetGuardProbe can feed it a collateral change directly.</summary>
    internal static string? VerifyBlockContent(byte[] original, byte[] edited, HashSet<int> expectedBlocks, bool expectHeader)
    {
        byte[] normBaseline;
        try
        {
            var b = new NifFile();
            using var ms = new MemoryStream(original, writable: false);
            if (b.Load(ms) != 0) return "verification could not re-parse the original mesh to normalize it — refusing to write.";
            using var outMs = new MemoryStream();
            if (b.Save(outMs) != 0) return "verification could not normalize the original mesh — refusing to write.";
            normBaseline = outMs.ToArray();
        }
        catch (Exception ex) { return $"verification threw while normalizing the original mesh ({ex.GetType().Name}) — refusing to write."; }

        var a = SliceBlocks(normBaseline);
        var c = SliceBlocks(edited);
        if (a is null || c is null) return "verification could not recover the block layout to compare — refusing to write (nothing changed on disk).";
        if (a.Value.blocks.Length != c.Value.blocks.Length)
            return $"the edit changed the block COUNT ({a.Value.blocks.Length} → {c.Value.blocks.Length}) — a structural change no whitelist op should make. Refusing, nothing written.";

        var changed = new List<int>();
        for (int i = 0; i < c.Value.blocks.Length; i++)
            if (!a.Value.blocks[i].AsSpan().SequenceEqual(c.Value.blocks[i])) changed.Add(i);

        var unexpected = changed.Where(i => !expectedBlocks.Contains(i)).ToList();
        if (unexpected.Count > 0)
            return $"the edit changed block(s) [{string.Join(", ", unexpected.Select(i => i + " " + c.Value.types[i]))}] it should not have touched (expected only [{string.Join(", ", expectedBlocks.OrderBy(x => x))}]). Refusing, nothing written.";

        // A header change is legitimate in exactly two cases — a rename, or an expected block that changed SIZE.
        bool expectedBlockResized = expectedBlocks.Any(i => i >= 0 && i < a.Value.blocks.Length && a.Value.blocks[i].Length != c.Value.blocks[i].Length);
        if (c.Value.header.AsSpan().SequenceEqual(a.Value.header) == false && !expectHeader && !expectedBlockResized)
            return "the edit changed the header (its string table or a non-touched block's size entry), which no in-place same-size op should. Refusing, nothing written.";
        if (c.Value.footer.AsSpan().SequenceEqual(a.Value.footer) == false)
            return "the edit changed the file footer (root references) — a structural change no whitelist op should make. Refusing, nothing written.";
        return null;
    }

    /// <summary>Slice a normalized NIF buffer into header, per-block content and footer bytes; null if the layout cannot be recovered.</summary>
    static (byte[] header, byte[][] blocks, string[] types, byte[] footer)? SliceBlocks(byte[] buf)
    {
        NifFile nif;
        try
        {
            nif = new NifFile();
            using var ms = new MemoryStream(buf, writable: false);
            if (nif.Load(ms) != 0) return null;
        }
        catch { return null; }

        int bc = nif.Header.BlockCount;
        long sum = 0; var sizes = new int[bc]; var types = new string[bc];
        for (int i = 0; i < bc; i++) { sizes[i] = nif.Header.GetBlockSize(i); types[i] = nif.Header.GetBlockTypeNameById(i) ?? "?"; sum += sizes[i]; }

        // Recover the header/footer boundary: scan numRoots up from 1, accepting the first candidate whose own
        // Num-Roots field matches and whose every root ref indexes a real block.
        long headerEnd = -1; long footerLen = 0;
        for (int nRoots = 1; nRoots <= 64; nRoots++)
        {
            long fl = 4 + 4L * nRoots; long cand = buf.LongLength - fl - sum;
            if (cand <= 0) break;
            if (cand + sum + fl > buf.LongLength) continue;
            if (BitConverter.ToUInt32(buf, (int)(cand + sum)) != (uint)nRoots) continue;
            bool refsValid = true;
            for (int r = 0; r < nRoots; r++)
            {
                uint rootRef = BitConverter.ToUInt32(buf, (int)(cand + sum + 4 + 4L * r));
                if (rootRef >= (uint)bc) { refsValid = false; break; }   // a root ref must index a real block
            }
            if (refsValid) { headerEnd = cand; footerLen = fl; break; }
        }
        if (headerEnd < 0) return null;

        var header = new byte[headerEnd];
        Array.Copy(buf, 0, header, 0, headerEnd);
        var blocks = new byte[bc][];
        long pos = headerEnd;
        for (int i = 0; i < bc; i++)
        {
            if (pos + sizes[i] > buf.LongLength) return null;
            blocks[i] = new byte[sizes[i]];
            Array.Copy(buf, pos, blocks[i], 0, sizes[i]);
            pos += sizes[i];
        }
        var footer = new byte[footerLen];
        if (footerLen > 0 && pos + footerLen <= buf.LongLength) Array.Copy(buf, pos, footer, 0, footerLen);
        return (header, blocks, types, footer);
    }

    /// <summary>GATE 2 — semantic read-back (docs/architecture/nif.md): null on success, else a named refusal.
    /// Internal so NifSetGuardProbe can feed it a no-op write directly.</summary>
    internal static string? VerifyReadBack(byte[] edited, NifInspect pre, IReadOnlyList<NifSetOp> ops, out IReadOnlyList<string> warnings)
    {
        warnings = Array.Empty<string>();
        var re = new NifFile();
        try
        {
            using var ms = new MemoryStream(edited, writable: false);
            if (re.Load(ms) != 0) return "the written mesh failed to reload for verification — refusing (nothing changed on disk).";
        }
        catch (Exception ex) { return $"the written mesh threw on reload for verification ({ex.GetType().Name}) — refusing (nothing changed on disk)."; }

        NifInspect post;
        try { post = Build(re); }
        catch (Exception ex) { return $"the written mesh re-inspect failed ({ex.GetType().Name}) — refusing (nothing changed on disk)."; }

        if (!post.IsSkyrimSE) return "the written mesh is no longer an SE stream — refusing (nothing changed on disk).";
        if (!CensusEqual(pre.BlockTypes, post.BlockTypes))
            return "the written mesh's block census changed vs the original — a structural drift no whitelist op should cause. Refusing (nothing changed on disk).";
        if (pre.UnknownBlockTypes.Count != post.UnknownBlockTypes.Count)
            return "the written mesh's unknown-block count changed vs the original — refusing (nothing changed on disk).";

        foreach (var op in ops)
        {
            var (ok, actual) = ReadBackMatches(re, op);
            if (!ok)
                return $"read-back shows the {op.Kind} on '{op.Target}' did NOT take effect (now reads {actual}) — refusing (nothing changed on disk).";
        }

        if (post.HasUnknownBlocks)
            warnings = new[] { $"this mesh carries {post.UnknownBlockTypes.Count} unknown block type(s) ({string.Join(", ", post.UnknownBlockTypes)}) — preserved byte-for-byte (the census gate confirmed it); the edit did not touch them." };
        return null;
    }

    static bool CensusEqual(IReadOnlyList<NifBlockTypeCount> a, IReadOnlyList<NifBlockTypeCount> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (a[i].Type != b[i].Type || a[i].Count != b[i].Count) return false;
        return true;
    }

    /// <summary>Re-resolve an op's target in the RELOADED mesh and confirm the written value; a false is a write that did not persist.</summary>
    static (bool ok, string actual) ReadBackMatches(NifFile nif, NifSetOp op)
    {
        switch (op.Kind)
        {
            case NifSetOpKind.RenameShape:
                return (nif.GetShapes().Any(s => (s.Name?.String ?? "") == op.NewName), $"shape names {ShapeNames(nif)}");
            case NifSetOpKind.RenameNode:
                return (nif.Blocks.OfType<NiNode>().Any(n => (n.Name?.String ?? "") == op.NewName), "(node name)");
            case NifSetOpKind.SetFlags:
            {
                var av = nif.Blocks.OfType<NiflySharp.Blocks.NiAVObject>().FirstOrDefault(a => (a.Name?.String ?? "") == op.Target);
                return (av is not null && op.Flags is { } f && av.Flags_ui == f, av is null ? "(target gone)" : $"0x{av.Flags_ui:X}");
            }
            case NifSetOpKind.SetScale:
            {
                var av = nif.Blocks.OfType<NiflySharp.Blocks.NiAVObject>().FirstOrDefault(a => (a.Name?.String ?? "") == op.Target);
                return (av is not null && op.Scale is { } sc && Math.Abs(av.Scale - sc) < 1e-6f, av is null ? "(target gone)" : av.Scale.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            case NifSetOpKind.SetAlpha:
            {
                var s = nif.GetShapes().FirstOrDefault(x => (x.Name?.String ?? "") == op.Target);
                var ap = s is not null && s.HasAlphaProperty ? nif.GetBlock<NiAlphaProperty>(s.AlphaPropertyRef) : null;
                if (ap is null) return (false, "(no alpha)");
                bool okF = op.AlphaFlags is not { } fw || ap.Flags.Value == fw;
                bool okT = op.AlphaThreshold is not { } th || ap.Threshold == th;
                return (okF && okT, $"0x{ap.Flags.Value:X4}/thr{ap.Threshold}");
            }
            case NifSetOpKind.SetPartition:
            {
                var s = nif.GetShapes().FirstOrDefault(x => (x.Name?.String ?? "") == op.Target);
                var dis = s?.SkinInstanceRef is not null ? nif.GetBlock(s.SkinInstanceRef) as BSDismemberSkinInstance : null;
                if (dis?.Partitions is null || dis.Partitions.Count == 0) return (false, "(no partitions)");
                int idx = op.PartitionIndex ?? 0;
                if (idx < 0 || idx >= dis.Partitions.Count) return (false, "(index gone)");
                return (op.BodyPartId is { } bp && (int)dis.Partitions[idx].BodyPart == bp, $"[{idx}]={(int)dis.Partitions[idx].BodyPart}");
            }
            case NifSetOpKind.SetPath when op.TextureSlot is null:
            {
                // Read off the reloaded mesh's own TABLE, not the block refs the write went through, or a half-done swap verifies green.
                var strings = ReadHeaderStrings(nif.Header);
                return (strings.Contains(op.Path ?? "") && !strings.Contains(op.Target),
                        strings.Contains(op.Target) ? $"'{op.Target}' still present" : "(new string absent)");
            }
            case NifSetOpKind.SetPath:
            {
                var s = nif.GetShapes().FirstOrDefault(x => (x.Name?.String ?? "") == op.Target);
                var shader = s is not null ? nif.GetShader(s) : null;
                var ts = shader?.TextureSetRef is not null ? nif.GetBlock(shader.TextureSetRef) as BSShaderTextureSet : null;
                if (ts is null || op.TextureSlot is not { } slot || slot < 0 || slot >= ts.Textures.Count) return (false, "(no texset/slot)");
                return ((ts.Textures[slot]?.Content ?? "") == op.Path, $"tex[{slot}]={ts.Textures[slot]?.Content}");
            }
            case NifSetOpKind.SetShaderValue:
            {
                // Read back off the CONCRETE property, never INiShader, whose stub constant would false-pass.
                var s = nif.GetShapes().FirstOrDefault(x => (x.Name?.String ?? "") == op.Target);
                var shader = s is not null ? nif.GetShader(s) : null;
                if (shader is null) return (false, "(no shader)");
                if (op.ShaderValue is null || ShaderValueProperty(op.ShaderValue) is not { } prop) return (false, "(no value name)");
                var bt = shader.GetType();
                var w = ReallyWrites(bt, prop);
                int components = w.Components;
                var pi = w.Writable ? bt.GetProperty(prop, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance) : null;
                if (pi is null) return (false, $"(not readable back on {bt.Name})");
                var actual = pi.GetValue(shader);
                var want = op.ShaderNumbers ?? Array.Empty<float>();
                if (want.Count != components) return (false, DescribeShaderValue(actual, components));
                bool ok;
                if (components == 1) ok = Math.Abs(Convert.ToSingle(actual) - want[0]) < 1e-6f;
                else
                {
                    // RGB ONLY: a Color4's A is synthetic here, so comparing it would fail every colour write.
                    var (r, g, b) = ReadRgb(actual!, actual!.GetType());
                    ok = Math.Abs(r - want[0]) < 1e-6f && Math.Abs(g - want[1]) < 1e-6f && Math.Abs(b - want[2]) < 1e-6f;
                }
                return (ok, DescribeShaderValue(actual, components));
            }
            default: return (false, "(unknown op)");
        }
    }
}

// NIF-layer data model — the format-level result of an inspect; the service layer wraps it with VFS info.

/// <summary>The outcome of <see cref="NifService.Inspect"/>: exactly one of <see cref="Inspect"/> or <see cref="Error"/>.</summary>
public sealed record NifInspectOutcome(NifInspect? Inspect, string? Error);

/// <summary>Everything <see cref="NifService.Inspect"/> models about a mesh, built in full; <see cref="IsSkyrimSE"/>
/// is the header identity (user 12 / stream 100) and the renderer chooses which sections to show.</summary>
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

/// <summary>One shape and its whitelisted values, each absent one null or empty rather than defaulted;
/// <see cref="FlagsDefault"/> is the nif.xml SSE default the renderer decodes <see cref="Flags"/> by deviation from.</summary>
public sealed record NifShape(
    string Name,
    uint Flags,
    float Scale,
    string BlockType,
    uint? FlagsDefault,
    string? FlagsDefaultType,
    IReadOnlyList<NifPartition> Partitions,
    NifAlpha? Alpha,
    IReadOnlyList<NifTexture> Textures,
    IReadOnlyList<string> Bones,
    NifShader? Shader = null);

/// <summary>One BSDismember partition: the body-part id, its decoded enum name (e.g. SBP_30_HEAD), and the part flags.</summary>
public sealed record NifPartition(int BodyPartId, string BodyPartName, int PartFlags);

/// <summary>An NiAlphaProperty decoded: the raw 16-bit flags word plus its semantic pieces.</summary>
public sealed record NifAlpha(
    ushort Flags,
    bool Blend,
    string SourceBlendMode,
    string DestinationBlendMode,
    bool Test,
    string TestFunction,
    byte Threshold);

/// <summary>One embedded texture path at its BSShaderTextureSet slot index; <see cref="SlotName"/> is null where the
/// shader does not determine it, and the index is always kept alongside. See <c>NifService.SlotName</c>.</summary>
public sealed record NifTexture(int Slot, string Path, string? SlotName = null);

/// <summary>One shape's shader property. <see cref="GameType"/> is which game's layout nifly read it as, which selects
/// the flag words; <see cref="ShaderType"/> and the two flag words are null where no field carries them.
/// <para>Every LIGHTING VALUE is nullable, and null means this library version does not read it off THIS BLOCK TYPE —
/// never "the mesh doesn't have it"; see <c>NifService.ReallyReads</c> and docs/architecture/nif.md.</para></summary>
public sealed record NifShader(
    string BlockType,
    string GameType,
    string? ShaderType,
    NifShaderFlagWord? Flags1,
    NifShaderFlagWord? Flags2,
    NifColor? EmissiveColor,
    float? EmissiveMultiple,
    float? Glossiness,
    float? SpecularStrength,
    NifColor? SpecularColor,
    float? Alpha);

/// <summary>One decoded shader flag word: its label, raw value, named bits in bit order, and the mask of bits no enum member covers.</summary>
public sealed record NifShaderFlagWord(string Label, uint Raw, IReadOnlyList<string> Names, uint UnknownBits);

/// <summary>A shader colour — RGB, which is what the format carries; opacity is <see cref="NifShader.Alpha"/>.</summary>
public sealed record NifColor(float R, float G, float B);

/// <summary>One node in the pre-order NiNode tree, carrying the same flag default the renderer decodes shapes by.</summary>
public sealed record NifNode(int Depth, string Name, uint Flags, string BlockType, uint? FlagsDefault, string? FlagsDefaultType);

// NIF-layer write model — the whitelisted op(s) and the verified outcome.

/// <summary>Whether a shader lighting value can be WRITTEN on a given block, and if not, WHY — three states, because
/// two different facts refuse the write and each needs its own sentence; see docs/architecture/nif.md.</summary>
internal readonly record struct NifShaderWritability(bool Writable, int Components, string? UnknownTypeName)
{
    public static NifShaderWritability Ok(int components) => new(true, components, null);
    public static NifShaderWritability NoSetter() => new(false, 0, null);
    public static NifShaderWritability UnknownType(string typeName) => new(false, 0, typeName);
}

/// <summary>The whitelisted write op kinds. Renames edit the header string table; the others edit one block.</summary>
public enum NifSetOpKind { RenameShape, RenameNode, SetFlags, SetScale, SetPartition, SetAlpha, SetPath, SetShaderValue }

/// <summary>One write op. <see cref="Target"/> is the shape/node name it addresses, the CURRENT name for a rename, and
/// the value fields are read per <see cref="Kind"/> and are otherwise null. <see cref="ShaderNumbers"/> is an
/// ARITY-FREE list, because how many components a value takes is decided in one place, off the library's own type.</summary>
public sealed record NifSetOp(
    NifSetOpKind Kind,
    string Target,
    string? NewName = null,
    uint? Flags = null,
    float? Scale = null,
    int? BodyPartId = null,
    int? PartitionIndex = null,
    ushort? AlphaFlags = null,
    byte? AlphaThreshold = null,
    int? TextureSlot = null,
    string? Path = null,
    string? ShaderValue = null,
    IReadOnlyList<float>? ShaderNumbers = null);

/// <summary>The outcome of <see cref="NifService.Set"/>: exactly one of the VERIFIED bytes plus report, or a named refusal.</summary>
public sealed record NifSetOutcome(byte[]? WrittenBytes, NifSetReport? Report, string? Error)
{
    public static NifSetOutcome Fail(string error) => new(null, null, error);
}

/// <summary>What a successful <see cref="NifService.Set"/> did: per-op before→after, the block ids verification
/// confirmed were the only ones changed, the size delta, and any warn-and-proceed notes.</summary>
public sealed record NifSetReport(
    IReadOnlyList<NifOpResult> Ops,
    IReadOnlyList<int> ChangedBlocks,
    bool HeaderChanged,
    long SizeDelta,
    IReadOnlyList<string> Warnings);

/// <summary>One applied op's audit line: the op kind, the target it addressed, and the before/after value as read.</summary>
public sealed record NifOpResult(string Op, string Target, string Before, string After);
