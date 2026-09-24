using System.Collections.Generic;
using System.IO;
using System.Linq;
using HousecarlCore;
using HousecarlMcp;
using NiflySharp;
using NiflySharp.Blocks;

namespace HousecarlMcpTests;

/// <summary>Meshes authored in memory with NiflySharp and synthetic inspect models, for the housecarl_nif_inspect
/// decode and render tests. No third-party mesh ships in the repo.</summary>
static class NifInspectFixtures
{
    /// <summary>A Skyrim SE mesh: a 3-level NiNode tree, one BSTriShape with name, flags, scale, two dismember
    /// partitions, an alpha property, a lighting shader and a texture set. At stream 130 the same mesh reads as the FO4 layout.</summary>
    public static byte[] BuildSyntheticSe(uint streamVersion = 100)
    {
        var ver = new NiVersion { FileVersion = NiVersion.ToFile("20.2.0.7"), UserVersion = 12, StreamVersion = streamVersion };
        var f = new NifFile();
        f.Create(ver, withRootNode: true);
        var root = f.GetRootNodes().First();
        root.Name = new NiStringRef("GuardRoot");
        root.Flags_ui = 0xE;

        var childA = new NiNode { Name = new NiStringRef("GuardChildA"), Flags_ui = 0x40000E };
        root.Children.AddBlockRef(f.AddBlock(childA));
        var childB = new NiNode { Name = new NiStringRef("GuardChildB"), Flags_ui = 0x408000E };
        childA.Children.AddBlockRef(f.AddBlock(childB));

        var shape = new BSTriShape { Name = new NiStringRef("GuardShape"), Flags_ui = 0x400000E, Scale = 1.25f };
        root.Children.AddBlockRef(f.AddBlock(shape));

        var alpha = new NiAlphaProperty { Threshold = 128 };
        alpha.Flags.Value = 0x12ED;
        shape.AlphaPropertyRef = new NiBlockRef<NiAlphaProperty>(f.AddBlock(alpha));

        // Soft_Lighting makes slot 2 SoftLighting, Model_Space_Normals with no backlight makes slot 7 Specular, nothing env-maps slot 4.
        var shader = new BSLightingShaderProperty
        {
            // nifly's shader accessors dispatch on Type; a block built in memory starts at None and would author zeros.
            Type = streamVersion == 100 ? NiflySharp.Helpers.ShaderHelper.ShaderGameType.SK
                                        : NiflySharp.Helpers.ShaderHelper.ShaderGameType.FO4,
            ShaderType_SK_FO4 = NiflySharp.Enums.BSLightingShaderType.SkinTint,
            ShaderFlags_SSPF1 = NiflySharp.Enums.SkyrimShaderPropertyFlags1.Specular
                              | NiflySharp.Enums.SkyrimShaderPropertyFlags1.Skinned
                              | NiflySharp.Enums.SkyrimShaderPropertyFlags1.Model_Space_Normals,
            ShaderFlags_SSPF2 = NiflySharp.Enums.SkyrimShaderPropertyFlags2.ZBuffer_Write
                              | NiflySharp.Enums.SkyrimShaderPropertyFlags2.Double_Sided
                              | NiflySharp.Enums.SkyrimShaderPropertyFlags2.Soft_Lighting,
            EmissiveColor = new NiflySharp.Structs.Color4 { R = 0.25f, G = 0.5f, B = 0.75f, A = 1f },
            // Each value differs from the stub constant (0, or 1 for Alpha) so none can pass by coincidence.
            EmissiveMultiple = 2.5f,
            Glossiness = 30f,
            SpecularStrength = 1.5f,
            Alpha = 0.5f,
            SpecularColor = new NiflySharp.Structs.Color3 { R = 1f, G = 0.5f, B = 0.25f },
        };
        var texSet = new BSShaderTextureSet
        {
            Textures = new List<NiString4>
            {
                new(@"textures\guard\diffuse.dds", false), new(@"textures\guard\normal.dds", false),
                new(@"textures\guard\soft.dds", false),    new("", false),
                new(@"textures\guard\slot4.dds", false),   new("", false),
                new("", false),                            new(@"textures\guard\slot7.dds", false),
                new("", false),
            },
        };
        texSet.NumTextures = (uint)texSet.Textures.Count;
        shader.TextureSetRef = new NiBlockRef<BSShaderTextureSet>(f.AddBlock(texSet));
        shape.ShaderPropertyRef = new NiBlockRef<BSShaderProperty>(f.AddBlock(shader));

        var skin = new BSDismemberSkinInstance
        {
            Partitions = new List<NiflySharp.Structs.BodyPartList>
            {
                new() { BodyPart = (NiflySharp.Enums.BSDismemberBodyPartType)30, PartFlag = (NiflySharp.Enums.BSPartFlag)257 },
                new() { BodyPart = (NiflySharp.Enums.BSDismemberBodyPartType)31, PartFlag = (NiflySharp.Enums.BSPartFlag)257 },
            },
        };
        skin.NumPartitions = (uint)skin.Partitions.Count;
        shape.SkinInstanceRef = new NiBlockRef<NiObject>(f.AddBlock(skin));

        using var ms = new MemoryStream();
        if (f.Save(ms) != 0) throw new InvalidOperationException("authoring the synthetic SE mesh failed to save");
        return ms.ToArray();
    }

    /// <summary>A minimal SK-layout mesh whose one shape carries a BSEffectShaderProperty, a block NiflySharp answers
    /// every lighting value on from the interface stub.</summary>
    public static byte[] BuildSyntheticEffectShader()
    {
        var ver = new NiVersion { FileVersion = NiVersion.ToFile("20.2.0.7"), UserVersion = 12, StreamVersion = 100 };
        var f = new NifFile();
        f.Create(ver, withRootNode: true);
        var root = f.GetRootNodes().First();
        root.Name = new NiStringRef("EffectRoot");
        root.Flags_ui = 0xE;

        var shape = new BSTriShape { Name = new NiStringRef("EffectShape"), Flags_ui = 0xE, Scale = 1f };
        root.Children.AddBlockRef(f.AddBlock(shape));

        var shader = new BSEffectShaderProperty
        {
            Type = NiflySharp.Helpers.ShaderHelper.ShaderGameType.SK,
            ShaderFlags_SSPF1 = NiflySharp.Enums.SkyrimShaderPropertyFlags1.Specular,
            ShaderFlags_SSPF2 = NiflySharp.Enums.SkyrimShaderPropertyFlags2.ZBuffer_Write,
            SourceTexture = new NiString4(@"textures\guard\effect.dds", false),
        };
        shape.ShaderPropertyRef = new NiBlockRef<BSShaderProperty>(f.AddBlock(shader));

        using var ms = new MemoryStream();
        if (f.Save(ms) != 0) throw new InvalidOperationException("authoring the synthetic effect-shader mesh failed to save");
        return ms.ToArray();
    }

    /// <summary>A flags enum with unnamed bits (0x2, 0x10, ...) and one multi-bit combo member.</summary>
    public enum GappedFlags : uint { Alpha = 0x1, Beta = 0x4, Combo = 0x9 }

    /// <summary>A one-shape inspect model whose shader carries <paramref name="word"/> as its first flag word.</summary>
    public static NifInspect FakeShaderInspect(NifShaderFlagWord word)
    {
        var shader = new NifShader("BSLightingShaderProperty", "SK", "Default", word, null,
                                   new NifColor(0f, 0f, 0f), 1f, 30f, 1f, new NifColor(1f, 1f, 1f), 1f);
        var shape = new NifShape("GapShape", 0xE, 1f, "BSTriShape", 0x8000E, "BSTriShape",
                                 new List<NifPartition>(), null, new List<NifTexture>(), new List<string>(), shader);
        return new NifInspect("20.2.0.7", 12, 100, true, 2,
            new List<NifBlockTypeCount> { new("BSTriShape", 1), new("NiNode", 1) },
            false, Array.Empty<string>(),
            new List<NifShape> { shape }, new List<NifNode> { new(0, "Root", 0xE, "NiNode", 0xE, "NiNode") },
            new List<string> { "Root", "GapShape" });
    }

    /// <summary>A synthetic inspect model: <paramref name="totalShapes"/> shapes at flags 0x400000E, the first
    /// <paramref name="withPartitions"/> carrying <paramref name="partsPerShape"/> partitions.</summary>
    public static NifInspect FakeInspect(int totalShapes, int withPartitions, bool hasUnknown, int partsPerShape = 2)
    {
        var shapes = new List<NifShape>();
        for (int i = 0; i < totalShapes; i++)
        {
            var parts = new List<NifPartition>();
            if (i < withPartitions)
                for (int p = 0; p < partsPerShape; p++)
                    parts.Add(new NifPartition(30 + p, "SBP_" + (30 + p) + "_PART", 257));
            shapes.Add(new NifShape("Shape" + i, 0x400000E, 1f, "BSTriShape", 0x8000E, "BSTriShape", parts, null, new List<NifTexture>(), new List<string>()));
        }
        return new NifInspect("20.2.0.7", 12, 100, true, totalShapes + 1,
            new List<NifBlockTypeCount> { new("BSTriShape", totalShapes), new("NiNode", 1) },
            hasUnknown, Array.Empty<string>(),
            shapes, new List<NifNode> { new(0, "Root", 0xE, "NiNode", 0xE, "NiNode") }, new List<string> { "Root", "Shape0" });
    }

    /// <summary>A batch of one: <paramref name="inspect"/> or <paramref name="error"/> with a ModA (loose) over Base.bsa (BSA) provider chain.</summary>
    public static NifInspectBatchData FakeData(NifInspect? inspect, string? error, bool ambiguous = false, IReadOnlyList<string>? bsaFailures = null)
    {
        var provs = new List<NifProvider> { new("ModA", "loose"), new("Base.bsa", "BSA") };
        var one = new NifInspectData(@"meshes\test.nif", inspect is null ? null : provs[0], provs, ambiguous, false, inspect, error);
        return new NifInspectBatchData(new[] { one }, bsaFailures ?? Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<string>(), "Default");
    }

    /// <summary>Renders <paramref name="d"/> with the named sections and unrecognized tokens at <paramref name="cap"/>.</summary>
    public static string Render(NifInspectBatchData d, string[] sections, string[]? unknown = null, int cap = 80_000)
        => NifWire.Render(d, new HashSet<string>(sections, StringComparer.OrdinalIgnoreCase), unknown ?? Array.Empty<string>(), cap);
}
