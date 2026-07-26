using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Helpers;   // ShaderHelper.ShaderGameType — which game's flag layout a shader block was read as (#272)

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

    // NiAVObject flag DEFAULTS per concrete type, SSE-effective — transcribed BY CONSTRUCTION from nif.xml's
    // NiAVObject.Flags <default onlyT=...> table (the SAME nif.xml NiflySharp is generated from): the SSE-specific value
    // where one exists, else the unversioned value; FO3/FO4-only variants excluded. nif.xml does NOT name the individual
    // bits (Flags is a plain uint there, and community docs note most are unused), so houseCARL decodes flags by
    // DEVIATION from these authoritative defaults rather than inventing bit-names — and always states the one bit nif.xml
    // DOES document: 0x80000 ("Skyrim lacks it sometimes; FO4 lacks it always"), the head-vs-hair-class signal.
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

    /// <summary>The SSE-effective NiAVObject flag default for a block, resolved UP the type's inheritance chain (a
    /// BSDynamicTriShape has no own default → its parent BSTriShape's applies, matching nif.xml's onlyT semantics), or
    /// (null, null) if no ancestor up to <see cref="object"/> has a documented default. Only consulted for SE meshes —
    /// the table is SSE values, so a non-SE mesh gets no (misleading) default comparison.</summary>
    static (uint? Value, string? FromType) ResolveAvDefault(Type? t)
    {
        for (var cur = t; cur is not null && cur != typeof(object); cur = cur.BaseType)
            if (AvFlagsSseDefaults.TryGetValue(cur.Name, out var v)) return (v, cur.Name);
        return (null, null);
    }

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
        foreach (var shape in nif.GetShapes()) shapes.Add(BuildShape(nif, shape, isSe));

        return new NifInspect(
            version.VersionString ?? "", user, stream, isSe,
            blockCount, blockTypes,
            nif.HasUnknownBlocks, unknownDistinct,
            shapes, BuildNodeTree(nif, isSe), ReadHeaderStrings(header));
    }

    /// <summary>One shape's N2-whitelist values. Every ref is read DIRECTLY off the shape (the SE-safe path); the
    /// partition list is present only for a BSDismember skin instance, alpha only when the shape carries an alpha
    /// property, and a texture slot only when its path is non-empty.</summary>
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

    /// <summary>Read the shape's SHADER PROPERTY (#272) — the block every visual diagnosis actually asks about: which
    /// shader is on this shape, and does it emit / scatter / env-map light. Read off <see cref="INiShader"/>, the
    /// interface <c>NifFile.GetShader</c> already returns, so this covers BSLightingShaderProperty and
    /// BSEffectShaderProperty (and anything else nifly models as a shader) with no per-block-type wiring.
    ///
    /// TWO Q3 calls live here. (1) <see cref="NifShader.ShaderType"/> is reported ONLY for a lighting shader: the
    /// <c>ShaderType_SK_FO4</c> property sits on the shared base, but a BSEffectShaderProperty never serializes a
    /// shader type, so reading it there yields a default 0 that renders as a confident "Default" — a wrong answer, not
    /// a missing one. (2) The flag WORDS are chosen by the shader's own <c>Type</c> (which game's layout the block was
    /// read as), never assumed to be Skyrim's — decoding an FO4 word against Skyrim names would mislabel every bit.</summary>
    static NifShader BuildShader(INiShader shader)
    {
        var blockType = shader.GetType().Name;
        var game = shader.Type;

        // The flag pair that is REAL for this block's game layout. nifly models each game's word as its own named enum,
        // so the decode below is reflection over that enum — the set of flag names IS the library's, by construction.
        (NifShaderFlagWord? f1, NifShaderFlagWord? f2) = game switch
        {
            ShaderHelper.ShaderGameType.SK    => (DecodeFlagWord("SLSF1", shader.ShaderFlags_SSPF1), DecodeFlagWord("SLSF2", shader.ShaderFlags_SSPF2)),
            ShaderHelper.ShaderGameType.FO4   => (DecodeFlagWord("F4SPF1", shader.ShaderFlags_F4SPF1), DecodeFlagWord("F4SPF2", shader.ShaderFlags_F4SPF2)),
            ShaderHelper.ShaderGameType.FO3NV => (DecodeFlagWord("ShaderFlags", shader.ShaderFlags), DecodeFlagWord("ShaderFlags2", shader.ShaderFlags2)),
            // FO76/SF (and None) carry no flag word this library exposes as a named enum — report the game type and no
            // flags, rather than decoding some other game's word and labelling it as this one's (Q3).
            _ => (null, null),
        };

        // Each lighting value is reported ONLY if this block really reads it (see ReallyReads) — otherwise null, and
        // the renderer names it as unread. RGB, never RGBA: a lighting shader's emissive is a THREE-component colour
        // on disk, and the interface widens it to Color4, so its 4th component is a synthetic 0 that would render as
        // "fully transparent emissive" — a fabricated value, the one thing worse than a missing one (Q3). Specular is
        // a Color3 both on disk and on the interface, so it needs no such narrowing — the same RGB shape either way.
        var t = shader.GetType();
        NifColor? Rgb3(string prop, Func<NifColor> read) => ReallyReads(t, prop) ? read() : null;
        float? Scalar(string prop, Func<float> read) => ReallyReads(t, prop) ? read() : null;

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

    /// <summary>The shader's TYPE enum name, or null when this block doesn't carry one we can read honestly.
    ///
    /// TWO independent ways to get this wrong, and both produce a confident DEFAULT rather than a blank:
    ///   • WRONG BLOCK — a BSEffectShaderProperty never serializes a shader type at all, but the property sits on the
    ///     shared base, so reading it there yields 0 → a confident "Default".
    ///   • WRONG FIELD — the type property is LAYOUT-DISPATCHED, not shared. A block read as FO76/SF answers its real
    ///     type on <c>ShaderType_FO76_SF</c> while <c>ShaderType_SK_FO4</c> reads 0 ("Default"); on an SK block the
    ///     reverse holds and <c>ShaderType_FO76_SF</c> is garbage. So the field must be picked by the LAYOUT, which is
    ///     what the block was actually parsed as — not by the block's type name (review of PR #286).
    /// A layout with no type field of its own reports null, and the renderer says so plainly.</summary>
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

    // Which INiShader value accessors a concrete shader block ACTUALLY implements — cached per block type (a mesh has
    // many shapes; the interface map is walked once each).
    static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, HashSet<string>> RealReads = new();

    /// <summary>Whether <paramref name="blockType"/> REALLY reads <paramref name="property"/>, or merely inherits
    /// <see cref="INiShader"/>'s DEFAULT INTERFACE IMPLEMENTATION for it.
    ///
    /// This is not defensive noise — it is a live upstream gap, and it MOVES between releases. In NiflySharp 1.0.0,
    /// <c>BSLightingShaderProperty</c> overrode <c>EmissiveColor</c> but NOT <c>Glossiness</c>,
    /// <c>SpecularStrength</c>, <c>SpecularColor</c>, <c>EmissiveMultiple</c> or <c>Alpha</c>. 1.1.0 (issue #287)
    /// implements all five there — while <c>BSEffectShaderProperty</c> still answers <c>EmissiveColor</c>,
    /// <c>SpecularColor</c>, <c>Glossiness</c>, <c>SpecularStrength</c>, <c>EmissiveMultiple</c> and <c>Alpha</c>
    /// from the interface's default-implementation stub, which returns a CONSTANT (0, or 1 for Alpha) no matter what
    /// the mesh holds. The values are genuinely on disk — the block's private fields round-trip a save/load intact —
    /// the library just doesn't surface them for that block. Reporting the stub would be a confident wrong number on
    /// every mesh carrying one: exactly the silent-wrong-answer the no-silent-failure rule forbids, and the "fail
    /// loud about the library's delta, never silently around it" posture the coverage cornerstone names.
    ///
    /// Doing it BY CONSTRUCTION rather than by a hand-kept list of "nifly's stubs" is what let the 1.0.0 → 1.1.0 bump
    /// start reporting five values with no change to this method: the day upstream implements one, houseCARL reports
    /// it; the day upstream stubs one, that one goes quiet on its own instead of turning into a wrong answer.</summary>
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

    /// <summary>Decode one shader flag word into its NAMED bits plus the unnamed remainder. The names come from
    /// <see cref="Enum.GetValues(Type)"/> over nifly's own enum, so coverage is the library's coverage — no hand-kept
    /// bit table to drift. Members are peeled largest-first so a multi-bit combo member wins over its constituent bits
    /// (the <c>ReadEngine.FlagBitsDisplay</c> algorithm, #255), and whatever no member covers is returned as
    /// <see cref="NifShaderFlagWord.UnknownBits"/> — surfaced explicitly by the renderer, never silently dropped, which
    /// is the whole point: an unnamed bit is a real thing the mesh carries.</summary>
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

    /// <summary>The SEMANTIC name of a BSShaderTextureSet slot, or null when this shape's shader doesn't determine one.
    ///
    /// Slot 2 is glow OR skin-subsurface OR soft-lighting; slot 7 is backlight OR specular — the meaning comes from the
    /// shader TYPE and FLAGS, not from the index. nifly models the slots as a bare path list (the semantics are a
    /// Skyrim engine convention, not something nif.xml names), so unlike the flag decode above this cannot be
    /// reflected out of the library — it is a small, explicit interpreter of engine semantics, each arm keyed to the
    /// flag or type that decides it, in the same posture as <c>effect_chain</c>.
    ///
    /// Q3 — returns NULL rather than a best guess whenever the deciding flag/type isn't set. An unnamed slot renders
    /// as bare <c>tex[N]</c>: "this shader doesn't tell us", never a confident wrong label.
    ///
    /// SKYRIM LAYOUT ONLY, and that gate is load-bearing rather than tidiness. The conditions read nifly's
    /// <c>Has*</c>/<c>IsType*</c> helpers, which DISPATCH on the block's game layout — and for a layout where nifly
    /// doesn't model the concept they return <c>true</c> unconditionally, not false. On an FO4-layout block with an
    /// all-zero flag word <c>HasSoftlight</c>, <c>HasBacklight</c> and <c>Parallax</c> are all true; on FO3NV every
    /// one of the seven helpers is. Left ungated, slots 2/3/7 (and on FO3NV 4/5) would take a confident label derived
    /// from nothing the mesh carries — precisely the plausible-wrong-label this interpreter exists to refuse, and it
    /// would ride <c>sections=paths</c> and <c>sections=shapes</c> too. nif_inspect reads non-SE meshes on purpose
    /// (an unconverted FO4 mesh shipped inside a Skyrim mod is a thing people bring here), so this is reachable, not
    /// theoretical. These slot semantics are a SKYRIM engine convention; on any other layout the honest answer is
    /// that we don't model it. (Review of PR #286.)</summary>
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

    /// <summary>Whether this shader environment-maps at all — the condition slots 4 and 5 both hang on (a plain env
    /// map, an eye env map, or the shader type that implies it).</summary>
    static bool EnvMapped(INiShader shader)
        => shader.HasEnvironmentMapping || shader.HasEyeEnvironmentMapping
        || shader.IsTypeEnvironmentMap || shader.IsTypeEyeEnvironmentMap;

    /// <summary>Pre-order the NiNode hierarchy from the root(s), depth-annotated, each node with its NiAVObject flags.
    /// Only NiNode children are walked (shapes are covered by <see cref="NifInspect.Shapes"/>). A reference-identity
    /// visited set guards against a malformed file's cycle so the walk always terminates.</summary>
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

    // ======================================================================
    //  NIF layer Wave 2 — whitelist WRITES (housecarl_nif_set). Pure bytes-in / verified-bytes-out.
    // ======================================================================

    /// <summary>Apply the N2-whitelist write op(s) to a mesh's raw bytes and hand back the VERIFIED edited bytes, or a
    /// named refusal (Q3) — the format-level core behind <c>housecarl_nif_set</c>. Like <see cref="Inspect"/> it knows
    /// nothing of MO2/VFS; the service layer resolves the winning bytes, calls this, and writes the result.
    ///
    /// Refuses (nothing written) when: the mesh won't parse; it is NOT a Skyrim SE stream (a normalized cross-game write
    /// is untested territory — spike §7); a target shape/node/property named by an op isn't found or is ambiguous; an op
    /// can't apply (e.g. set_partition on a shape with no dismember skin). Unknown blocks are WARN-and-proceed — they are
    /// preserved byte-for-byte by construction and the census gate proves it (PRFAQ N6; Wave-2 posture).
    ///
    /// Every successful write passes TWO offset-immune gates before its bytes are returned (spike §4, empirically refined
    /// 2026-07-08 from a position-diff to a block-content diff so a length-changing rename can't false-abort):
    ///   1. BLOCK-CONTENT DIFF — normalize the unedited mesh and the edited mesh through nifly's canonical writer, slice
    ///      BOTH by their own block-size tables, and compare block CONTENT by index. Only the block(s)/header the op
    ///      claims to touch may differ; any other change (a stray block, geometry, the footer) → abort. Content-diff is
    ///      immune to the file-offset shift a grown/shrunk string table causes, which a raw byte-position diff is not.
    ///   2. SEMANTIC READ-BACK — reload the written bytes, re-inspect, and assert each op's target now reads as requested
    ///      (catches a silent no-op write), the block census + unknown-block count are unchanged, the SE stream is intact,
    ///      and the file reloads clean.
    /// A gate failure returns the refusal with nothing written — the writer never hands back an unverified mesh.</summary>
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
        catch (Exception ex) { return NifSetOutcome.Fail(DescribeLoadException(ex)); }

        NifInspect pre;
        try { pre = Build(nif); }
        catch (Exception ex) { return NifSetOutcome.Fail($"the mesh parsed but reading its structure failed — {ex.GetType().Name}: {ex.Message}. Nothing was written."); }

        // ---- SE-stream gate: nif_set refuses a non-SE mesh by name (plan §2 version guard; spike §7) ----
        if (!pre.IsSkyrimSE)
            return NifSetOutcome.Fail(
                $"this is NOT a Skyrim SE mesh (user {pre.UserVersion} / stream {pre.StreamVersion}; SE is user 12 / stream 100). " +
                "nif_set only writes SE-stream meshes — a normalized cross-game (LE / FO4 / Starfield) write is untested and refused. Nothing was written.");

        // ---- apply each op; record the exact block(s)/header each is allowed to touch ----
        var applied = new List<NifOpResult>(ops.Count);
        var expectedBlocks = new HashSet<int>();
        bool expectHeader = false;
        foreach (var op in ops)
        {
            var r = ApplyOp(nif, op);
            if (r.Error is not null) return NifSetOutcome.Fail(r.Error);   // target-not-found / ambiguous / not-applicable → nothing written
            applied.Add(new NifOpResult(op.Kind.ToString(), r.Target!, r.Before!, r.After!));
            if (r.TouchedBlock is { } b) expectedBlocks.Add(b);
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

        // ---- GATE 1: block-content diff (offset-immune) ----
        var g1 = VerifyBlockContent(bytes, edited, expectedBlocks, expectHeader);
        if (g1 is not null) return NifSetOutcome.Fail(g1);

        // ---- GATE 2: semantic read-back ----
        var g2 = VerifyReadBack(edited, pre, ops, out var warnings);
        if (g2 is not null) return NifSetOutcome.Fail(g2);

        var report = new NifSetReport(applied,
            expectedBlocks.OrderBy(i => i).ToList(), expectHeader,
            edited.Length - bytes.Length, warnings);
        return new NifSetOutcome(edited, report, null);
    }

    /// <summary>Apply one op to <paramref name="nif"/>, returning the target's before/after value, the single block index
    /// it is allowed to change (or header for a rename), or a NAMED error (Q3) that aborts the whole call. The write APIs
    /// follow the empirically-confirmed NiflySharp rules: bitfield sub-values (alpha flags) are structs (read-modify-write
    /// then re-assign); a block is resolved and mutated via its OWNING ref, never a freshly-built one (which does not
    /// persist on save).</summary>
    static (string? Error, string? Target, string? Before, string? After, int? TouchedBlock, bool TouchedHeader) ApplyOp(NifFile nif, NifSetOp op)
    {
        switch (op.Kind)
        {
            case NifSetOpKind.RenameShape:
            {
                if (string.IsNullOrEmpty(op.NewName)) return ("rename_shape needs a new_name.", null, null, null, null, false);
                var (shape, err) = ResolveShape(nif, op.Target);
                if (err is not null) return (err, null, null, null, null, false);
                // Refuse renaming ONTO an existing shape name — it manufactures the ambiguity the resolver refuses, and it
                // would defeat the read-back check (which confirms a rename by the NEW name existing): if nifly ever
                // dropped the rename while another shape already bore that name, gate 2 would false-pass.
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
                return (null, op.Target, before, $"0x{flags:X}", BlockIndexOf(nif, av), false);
            }
            case NifSetOpKind.SetScale:
            {
                if (op.Scale is not { } scale) return ("set_scale needs a scale value.", null, null, null, null, false);
                var (av, err) = ResolveAvObject(nif, op.Target);
                if (err is not null) return (err, null, null, null, null, false);
                string before = av!.Scale.ToString(System.Globalization.CultureInfo.InvariantCulture);
                av.Scale = scale;
                return (null, op.Target, before, scale.ToString(System.Globalization.CultureInfo.InvariantCulture), BlockIndexOf(nif, av), false);
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
                return (null, op.Target, before, $"0x{ap.Flags.Value:X4}/thr{ap.Threshold}", BlockIndexOf(nif, ap), false);
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
                return (null, op.Target, before, $"[{idx}]={bp}", BlockIndexOf(nif, dis), false);
            }
            case NifSetOpKind.SetPath:
            {
                if (op.TextureSlot is not { } slot) return ("set_path needs a texture_slot.", null, null, null, null, false);
                if (op.Path is null) return ("set_path needs a path.", null, null, null, null, false);
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
                return (null, op.Target, $"tex[{slot}]={before}", $"tex[{slot}]={op.Path}", BlockIndexOf(nif, ts), false);
            }
            default:
                return ($"unsupported op '{op.Kind}'.", null, null, null, null, false);
        }
    }

    // ---- target resolution (Q3: not-found and ambiguous are NAMED refusals, never a silent first-match write) ----

    static (INiShape? Shape, string? Error) ResolveShape(NifFile nif, string name)
    {
        var matches = nif.GetShapes().Where(s => (s.Name?.String ?? "") == name).ToList();
        if (matches.Count == 0) return (null, $"no shape named '{name}' in this mesh. Shapes: {ShapeNames(nif)}. Nothing was written.");
        if (matches.Count > 1) return (null, $"more than one shape is named '{name}' — ambiguous, refusing rather than guess which. Nothing was written.");
        return (matches[0], null);
    }

    static (NiNode? Node, string? Error) ResolveNode(NifFile nif, string name)
    {
        var matches = nif.Blocks.OfType<NiNode>().Where(n => (n.Name?.String ?? "") == name).ToList();
        if (matches.Count == 0) return (null, $"no node named '{name}' in this mesh. Nothing was written.");
        if (matches.Count > 1) return (null, $"more than one node is named '{name}' — ambiguous, refusing rather than guess which. Nothing was written.");
        return (matches[0], null);
    }

    /// <summary>Resolve a named NiAVObject (a shape OR a node — set_flags/set_scale apply to either). Ambiguity across the
    /// whole AV-object population is a named refusal.</summary>
    static (NiflySharp.Blocks.NiAVObject? Av, string? Error) ResolveAvObject(NifFile nif, string name)
    {
        var matches = nif.Blocks.OfType<NiflySharp.Blocks.NiAVObject>().Where(a => (a.Name?.String ?? "") == name).ToList();
        if (matches.Count == 0) return (null, $"no shape or node named '{name}' in this mesh. Nothing was written.");
        if (matches.Count > 1) return (null, $"more than one shape/node is named '{name}' — ambiguous, refusing rather than guess which. Nothing was written.");
        return (matches[0], null);
    }

    static string ShapeNames(NifFile nif) => string.Join(", ", nif.GetShapes().Select(s => "'" + (s.Name?.String ?? "") + "'"));

    /// <summary>The block id of a block within the file (parallel to Header.GetBlockSize/TypeName), by reference identity —
    /// the index the block-content diff will compare. -1 (never expected) if the block isn't in the list.</summary>
    static int BlockIndexOf(NifFile nif, object block)
    {
        var blocks = nif.Blocks;
        for (int i = 0; i < blocks.Count; i++) if (ReferenceEquals(blocks[i], block)) return i;
        return -1;
    }

    /// <summary>GATE 1 — block-content diff. Normalize the ORIGINAL bytes (reload+save) and compare, block-content by
    /// index, against the edited output (both sliced by their OWN block-size tables so a grown/shrunk header string table
    /// can't misalign the comparison). Returns null when only the expected block(s)/header changed; else a NAMED refusal.
    /// If the block layout can't be recovered on either side, it REFUSES (can't verify ⇒ won't write — never a silent
    /// pass). Internal so the guard can RED-prove it catches a collateral (non-expected-block) change directly.</summary>
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

        // A header change is legitimate in exactly two cases: a rename (edits the authored string table — the op declares
        // it via expectHeader), OR a touched block changed SIZE (the header's derived block-SIZE table records each
        // block's byte length, so growing/shrinking an expected block mechanically updates it — e.g. a longer texture
        // path). Any OTHER header change is real collateral → refuse. (Found on real facegen data 2026-07-08: the
        // synthetic same-length ops never resize a block, so only a corpus set_path surfaced the block-size-table path.)
        bool expectedBlockResized = expectedBlocks.Any(i => i >= 0 && i < a.Value.blocks.Length && a.Value.blocks[i].Length != c.Value.blocks[i].Length);
        if (c.Value.header.AsSpan().SequenceEqual(a.Value.header) == false && !expectHeader && !expectedBlockResized)
            return "the edit changed the header (its string table or a non-touched block's size entry), which no in-place same-size op should. Refusing, nothing written.";
        if (c.Value.footer.AsSpan().SequenceEqual(a.Value.footer) == false)
            return "the edit changed the file footer (root references) — a structural change no whitelist op should make. Refusing, nothing written.";
        return null;
    }

    /// <summary>Slice a normalized NIF buffer into (header bytes, per-block content bytes, footer bytes) using its OWN
    /// block-size table + a footer-length recovery (numRoots consistency check — the spike's difftrace layout recovery).
    /// null if the layout can't be recovered (the caller refuses).</summary>
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

        // Recover the header/footer boundary. The footer is [Num Roots : uint][Roots : uint * numRoots]. We scan numRoots
        // upward and accept the FIRST candidate whose footer both (a) leads with a Num-Roots field equal to the candidate
        // AND (b) has every root ref a valid block index. Start at 1 — a NIF always has ≥1 root — so the degenerate
        // numRoots=0 case can't false-match on a mesh whose sole root is block 0 (its trailing root ref reads as 0, which
        // would spuriously satisfy a numRoots=0 candidate and shift every block window +4). The root-ref validity check
        // makes a spurious earlier match astronomically unlikely; the true footer always validates.
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

    /// <summary>GATE 2 — semantic read-back. Reload the WRITTEN bytes, re-inspect, and assert: the file reloads clean and
    /// is still an SE stream; the block census + unknown-block count match the pre-edit inspect (no structural drift); and
    /// each op's target now reads as REQUESTED (a value that didn't take — a silent no-op — aborts here). Returns null on
    /// success; else a NAMED refusal. Also emits any WARN-and-proceed notes (unknown blocks preserved). Internal so the
    /// guard can RED-prove it catches a no-op write directly.</summary>
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

    /// <summary>Re-resolve an op's target in the RELOADED mesh and confirm the written value equals what was requested
    /// (renames resolve by the NEW name). Returns (matched, actual-as-read) — a false catches a write that silently did
    /// not persist.</summary>
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
            case NifSetOpKind.SetPath:
            {
                var s = nif.GetShapes().FirstOrDefault(x => (x.Name?.String ?? "") == op.Target);
                var shader = s is not null ? nif.GetShader(s) : null;
                var ts = shader?.TextureSetRef is not null ? nif.GetBlock(shader.TextureSetRef) as BSShaderTextureSet : null;
                if (ts is null || op.TextureSlot is not { } slot || slot < 0 || slot >= ts.Textures.Count) return (false, "(no texset/slot)");
                return ((ts.Textures[slot]?.Content ?? "") == op.Path, $"tex[{slot}]={ts.Textures[slot]?.Content}");
            }
            default: return (false, "(unknown op)");
        }
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
/// lists only non-empty texture-set slots; <see cref="Shader"/> is null unless the shape carries a shader property.
/// <see cref="FlagsDefault"/> is the nif.xml-documented SSE default for the
/// block's type (via <see cref="FlagsDefaultType"/>, the ancestor it came from), or null when none is documented / the
/// mesh isn't SE — the renderer decodes <see cref="Flags"/> by deviation from it (nif.xml doesn't name the bits).</summary>
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

/// <summary>One embedded texture path at its BSShaderTextureSet slot index (0 diffuse, 1 normal, … 6 tint/detail, …).
/// <see cref="SlotName"/> is the slot's SEMANTIC name where the shape's shader determines it (#272) — slot 2 is glow
/// vs skin-subsurface vs soft-lighting depending on type/flags, slot 7 backlight vs specular — and null when it
/// doesn't. The index is always kept alongside, so an unnamed slot degrades to the old <c>tex[N]</c> form rather than
/// to a confident wrong name (Q3). See <c>NifService.SlotName</c>.</summary>
public sealed record NifTexture(int Slot, string Path, string? SlotName = null);

/// <summary>One shape's SHADER PROPERTY (#272) — the block that answers "how is this shape shaded, and does it emit or
/// scatter light". <see cref="BlockType"/> is the on-disk block name (BSLightingShaderProperty / BSEffectShaderProperty
/// / …); <see cref="GameType"/> is which game's layout nifly read it as (SK / FO4 / FO3NV / …), which is what selects
/// the flag words. <see cref="ShaderType"/> is the BSLightingShaderType enum name (Default, EnvironmentMap, SkinTint,
/// FaceTint, HairTint, Parallax, MultiLayerParallax, …) and is null for any block that does not serialize one — an
/// effect shader reports NO type rather than a default-valued wrong one. <see cref="Flags1"/> / <see cref="Flags2"/>
/// are null when the game layout carries no flag word this library names.
/// <para>Every LIGHTING VALUE below is nullable, and null means "this library version does not read it off THIS BLOCK
/// TYPE" — NOT "the mesh doesn't have it" (see <c>NifService.ReallyReads</c>: NiflySharp answers several of them from
/// an interface stub that returns a constant, and which ones moves between releases — 1.1.0 reads all of them off a
/// BSLightingShaderProperty and none off a BSEffectShaderProperty). The renderer names the unread ones explicitly, so
/// a caller is never handed a stub value as if it were the mesh's.</para></summary>
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

/// <summary>One decoded shader flag word: its on-disk <see cref="Label"/> (SLSF1 / SLSF2 / F4SPF1 / …), the
/// <see cref="Raw"/> value, the <see cref="Names"/> of every set bit the library's enum names (in bit order), and
/// <see cref="UnknownBits"/> — the bits NO enum member covers, kept as an explicit mask so an unnamed bit is stated
/// rather than dropped (#255's posture, carried to the shader words).</summary>
public sealed record NifShaderFlagWord(string Label, uint Raw, IReadOnlyList<string> Names, uint UnknownBits);

/// <summary>A shader colour — RGB, because that is what the format actually carries for both colours reported here
/// (emissive and specular are each a Color3 on disk). Opacity is a separate scalar, <see cref="NifShader.Alpha"/>.</summary>
public sealed record NifColor(float R, float G, float B);

/// <summary>One node in the pre-order NiNode tree: its depth (0 = root), name, NiAVObject flags, block type, and the
/// nif.xml-documented SSE flag default for that type (via <see cref="FlagsDefaultType"/>) — null when none is documented
/// / the mesh isn't SE. The renderer decodes <see cref="Flags"/> by deviation from the default, like it does for shapes.</summary>
public sealed record NifNode(int Depth, string Name, uint Flags, string BlockType, uint? FlagsDefault, string? FlagsDefaultType);

// ======================================================================
//  NIF-layer WRITE model — the N2 whitelist op(s) and the verified outcome (Wave 2).
// ======================================================================

/// <summary>The six N2-whitelist write op kinds. Renames edit the header string table; the others edit one block.</summary>
public enum NifSetOpKind { RenameShape, RenameNode, SetFlags, SetScale, SetPartition, SetAlpha, SetPath }

/// <summary>One write op. <see cref="Target"/> is the shape/node name it addresses (the CURRENT name, for a rename).
/// The value fields are read per <see cref="Kind"/> and are otherwise null: <see cref="NewName"/> (rename), <see
/// cref="Flags"/> (set_flags), <see cref="Scale"/> (set_scale), <see cref="BodyPartId"/> + optional
/// <see cref="PartitionIndex"/> (set_partition), <see cref="AlphaFlags"/> and/or <see cref="AlphaThreshold"/>
/// (set_alpha), <see cref="TextureSlot"/> + <see cref="Path"/> (set_path — a BSShaderTextureSet slot).</summary>
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
    string? Path = null);

/// <summary>The outcome of <see cref="NifService.Set"/>: exactly one of <see cref="WrittenBytes"/>+<see cref="Report"/>
/// (success — the VERIFIED edited mesh bytes for the service layer to place) or <see cref="Error"/> (a named refusal,
/// with nothing written — Q3). Never both, never neither.</summary>
public sealed record NifSetOutcome(byte[]? WrittenBytes, NifSetReport? Report, string? Error)
{
    public static NifSetOutcome Fail(string error) => new(null, null, error);
}

/// <summary>What a successful <see cref="NifService.Set"/> did: the per-op before→after accounting, the block id(s) the
/// verification confirmed were the only ones changed (+ whether the header string table changed — a rename), the size
/// delta, and any WARN-and-proceed notes (e.g. preserved unknown blocks).</summary>
public sealed record NifSetReport(
    IReadOnlyList<NifOpResult> Ops,
    IReadOnlyList<int> ChangedBlocks,
    bool HeaderChanged,
    long SizeDelta,
    IReadOnlyList<string> Warnings);

/// <summary>One applied op's audit line: the op kind, the target it addressed, and the before/after value as read.</summary>
public sealed record NifOpResult(string Op, string Target, string Before, string After);
