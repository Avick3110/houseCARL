using System.Globalization;
using NiflySharp;
using NiflySharp.Blocks;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Synthetic Skyrim SE meshes the nif_set tests write against, authored through NiflySharp at test time so no
/// third-party mesh ships in the repo. Moved here from the retired nif-set-guard probe.</summary>
static class NifSetMeshes
{
    /// <summary>The main mesh's asset-reference header string: a BODYTRI value, not any shape or node's name.</summary>
    public const string GuardTriPath = @"meshes\actors\character\character assets\guard.tri";

    /// <summary>The slot-6 path the textured mesh carries, a facegen facetint like the one the probe's smoke mesh had.</summary>
    public const string FaceTintPath = @"textures\actors\character\facegendata\facetint\guard.esp\00000001.dds";

    static NiVersion Se => new() { FileVersion = NiVersion.ToFile("20.2.0.7"), UserVersion = 12, StreamVersion = 100 };

    static byte[] Save(NifFile f)
    {
        using var ms = new MemoryStream();
        Assert.Equal(0, f.Save(ms));
        return ms.ToArray();
    }

    /// <summary>Root, a GuardChildA node, a full GuardShape (flags 0x400000E, scale 1.25, alpha 0x12ED/128, partitions
    /// 30 and 31), a BareShape carrying none of those, and a BODYTRI extra-data string on the root.</summary>
    public static byte[] Guard()
    {
        var f = new NifFile();
        f.Create(Se, withRootNode: true);
        var root = f.GetRootNodes().First();
        root.Name = new NiStringRef("GuardRoot"); root.Flags_ui = 0xE;

        root.Children.AddBlockRef(f.AddBlock(new NiNode { Name = new NiStringRef("GuardChildA"), Flags_ui = 0x40000E }));

        var shape = new BSTriShape { Name = new NiStringRef("GuardShape"), Flags_ui = 0x400000E, Scale = 1.25f };
        root.Children.AddBlockRef(f.AddBlock(shape));
        var alpha = new NiAlphaProperty { Threshold = 128 }; alpha.Flags.Value = 0x12ED;
        shape.AlphaPropertyRef = new NiBlockRef<NiAlphaProperty>(f.AddBlock(alpha));
        shape.SkinInstanceRef = new NiBlockRef<NiObject>(f.AddBlock(Dismember(30, 31)));

        root.Children.AddBlockRef(f.AddBlock(new BSTriShape { Name = new NiStringRef("BareShape"), Flags_ui = 0x400000E, Scale = 1f }));

        var tri = new NiStringExtraData { Name = new NiStringRef("BODYTRI"), StringData = new NiStringRef(GuardTriPath) };
        root.ExtraDataList ??= new NiBlockRefArray<NiExtraData>();
        root.ExtraDataList.AddBlockRef(f.AddBlock(tri));
        root.NumExtraDataList = (uint)root.ExtraDataList.Count;
        return Save(f);
    }

    static BSDismemberSkinInstance Dismember(params int[] parts)
    {
        var skin = new BSDismemberSkinInstance
        {
            Partitions = parts.Select(p => new NiflySharp.Structs.BodyPartList
            {
                BodyPart = (NiflySharp.Enums.BSDismemberBodyPartType)p, PartFlag = (NiflySharp.Enums.BSPartFlag)257,
            }).ToList(),
        };
        skin.NumPartitions = (uint)skin.Partitions.Count;
        return skin;
    }

    /// <summary>A head-like shape with a lighting shader, a nine-slot texture set whose slot 6 is a facetint, flags and
    /// two partitions: the synthetic stand-in for the probe's real facegen smoke mesh.</summary>
    public static byte[] Textured()
    {
        var f = new NifFile();
        f.Create(Se, withRootNode: true);
        var root = f.GetRootNodes().First();
        root.Name = new NiStringRef("FaceRoot"); root.Flags_ui = 0xE;

        var texSet = new BSShaderTextureSet
        {
            Textures = new List<NiString4>
            {
                new(@"textures\actors\character\female\femalehead.dds", false),
                new(@"textures\actors\character\female\femalehead_msn.dds", false),
                new("", false), new("", false), new("", false), new("", false),
                new(FaceTintPath, false), new("", false), new("", false),
            },
        };
        texSet.NumTextures = (uint)texSet.Textures.Count;
        var shader = new BSLightingShaderProperty
        {
            Type = NiflySharp.Helpers.ShaderHelper.ShaderGameType.SK,
            ShaderType_SK_FO4 = NiflySharp.Enums.BSLightingShaderType.FaceTint,
        };
        shader.TextureSetRef = new NiBlockRef<BSShaderTextureSet>(f.AddBlock(texSet));

        var head = new BSTriShape { Name = new NiStringRef("FemaleHead"), Flags_ui = 0x400000E, Scale = 1f };
        head.ShaderPropertyRef = new NiBlockRef<BSShaderProperty>(f.AddBlock(shader));
        head.SkinInstanceRef = new NiBlockRef<NiObject>(f.AddBlock(Dismember(30, 230)));
        root.Children.AddBlockRef(f.AddBlock(head));
        return Save(f);
    }

    /// <summary>LitShape with a BSLightingShaderProperty whose six lighting values are all away from their defaults,
    /// EffShape with a BSEffectShaderProperty, and NoShaderShape with no shader.</summary>
    public static byte[] Shader()
    {
        var f = new NifFile();
        f.Create(Se, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Name = new NiStringRef("ShaderRoot"); root.Flags_ui = 0xE;

        var lit = new BSTriShape { Name = new NiStringRef("LitShape"), Flags_ui = 0x400000E, Scale = 1f };
        root.Children.AddBlockRef(f.AddBlock(lit));
        var lsp = new BSLightingShaderProperty
        {
            Glossiness = 30f,
            SpecularStrength = 1.5f,
            EmissiveMultiple = 2.5f,
            Alpha = 0.5f,
            EmissiveColor = new NiflySharp.Structs.Color4(0.25f, 0.5f, 0.75f, 0f),
            SpecularColor = new NiflySharp.Structs.Color3(1f, 0.5f, 0.25f),
        };
        lit.ShaderPropertyRef = new NiBlockRef<BSShaderProperty>(f.AddBlock(lsp));

        var eff = new BSTriShape { Name = new NiStringRef("EffShape"), Flags_ui = 0x400000E, Scale = 1f };
        root.Children.AddBlockRef(f.AddBlock(eff));
        eff.ShaderPropertyRef = new NiBlockRef<BSShaderProperty>(f.AddBlock(new BSEffectShaderProperty()));

        root.Children.AddBlockRef(f.AddBlock(new BSTriShape { Name = new NiStringRef("NoShaderShape"), Flags_ui = 0x400000E, Scale = 1f }));
        return Save(f);
    }

    /// <summary>Two shapes both named 'Dup'.</summary>
    public static byte[] DupNames()
    {
        var f = new NifFile();
        f.Create(Se, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Name = new NiStringRef("Root"); root.Flags_ui = 0xE;
        root.Children.AddBlockRef(f.AddBlock(new BSTriShape { Name = new NiStringRef("Dup"), Flags_ui = 0x400000E, Scale = 1f }));
        root.Children.AddBlockRef(f.AddBlock(new BSTriShape { Name = new NiStringRef("Dup"), Flags_ui = 0x400000E, Scale = 1f }));
        return Save(f);
    }

    /// <summary>A stream-83 (Oldrim) mesh, or null when this NiflySharp build will not author one that reads back non-SE.</summary>
    public static byte[]? TryNonSe()
    {
        try
        {
            var f = new NifFile();
            f.Create(new NiVersion { FileVersion = NiVersion.ToFile("20.2.0.7"), UserVersion = 12, StreamVersion = 83 }, withRootNode: true);
            var root = f.GetRootNodes().First(); root.Name = new NiStringRef("GuardShape"); root.Flags_ui = 0xE;
            using var ms = new MemoryStream();
            if (f.Save(ms) != 0) return null;
            var bytes = ms.ToArray();
            return NifService.Inspect(bytes).Inspect is { IsSkyrimSE: false } ? bytes : null;
        }
        catch { return null; }
    }

    /// <summary>The Guard mesh with GuardShape's flags AND GuardChildA's flags changed, plus both block ids.</summary>
    public static (byte[] Edited, int ShapeIdx, int ChildIdx) TwoBlockEdit(byte[] guard)
    {
        var nif = new NifFile();
        using (var ms = new MemoryStream(guard)) nif.Load(ms);
        var shape = nif.GetShapes().First(s => s.Name?.String == "GuardShape");
        var child = nif.Blocks.OfType<NiNode>().First(n => n.Name?.String == "GuardChildA");
        ((NiAVObject)shape).Flags_ui = 0x800000E;
        child.Flags_ui = 0x123456;
        nif.GetBlockIndex(shape, out int shapeIdx);
        nif.GetBlockIndex(child, out int childIdx);
        return (Save(nif), shapeIdx, childIdx);
    }

    public static NifShape? ShapeOf(byte[]? bytes, string name)
        => bytes is null ? null : NifService.Inspect(bytes).Inspect?.Shapes.FirstOrDefault(s => s.Name == name);

    /// <summary>A nullable shader scalar as invariant text; "(unread)" never equals a number.</summary>
    public static string Fmt(float? v) => v is { } f ? f.ToString(CultureInfo.InvariantCulture) : "(unread)";

    public static string Rgb(NifColor? c) => c is { } k ? $"rgb({Fmt(k.R)},{Fmt(k.G)},{Fmt(k.B)})" : "(unread)";

    /// <summary>A settable lighting value of a type houseCARL does not marshal: the ReallyWrites state no real shader block produces.</summary>
    public sealed class UnmarshalableShaderStandIn { public string Glossiness { get; set; } = ""; }
}

/// <summary>A temporary MO2 instance with one enabled mod, FaceMod, carrying <see cref="MeshRel"/> as a loose file and a
/// dummy plugin. Each instance is its own folder with its own user-config store, so consent never leaks between tests.</summary>
sealed class NifSetInstance : IDisposable
{
    public const string MeshRel = @"meshes\actors\character\facegendata\facegeom\Test.esp\00000001.nif";

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "hc-nifset-" + Guid.NewGuid().ToString("N"));
    public string ModDir => Path.Combine(Root, "inst", "mods", "FaceMod");
    public string LoosePath => Path.Combine(ModDir, MeshRel);
    public string StorePath => Path.Combine(Root, "houseCARL.user.json");
    public HousecarlMcp.LoadOrderService Svc { get; }

    public NifSetInstance(byte[] mesh)
    {
        var inst = Path.Combine(Root, "inst");
        var prof = Path.Combine(inst, "profiles", "Default");
        foreach (var d in new[] { ModDir, Path.Combine(inst, "game", "Data"), prof }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(inst, "game").Replace(@"\", @"\\") + ")\r\n");
        Directory.CreateDirectory(Path.GetDirectoryName(LoosePath)!);
        File.WriteAllBytes(LoosePath, mesh);
        File.WriteAllText(Path.Combine(ModDir, "Dummy.esp"), "x");
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\nDummy.esp\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*Dummy.esp\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+FaceMod\r\n");
        File.WriteAllText(Path.Combine(prof, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");
        Svc = HousecarlMcp.LoadOrderService.WithInstance(inst, 0, new UserConfigStore(StorePath));
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, recursive: true); } catch { /* temp scratch */ }
    }
}
