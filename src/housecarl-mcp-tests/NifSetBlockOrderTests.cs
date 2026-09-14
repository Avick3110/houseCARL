using System.Collections.Generic;
using System.IO;
using System.Linq;
using HousecarlCore;
using NiflySharp;
using NiflySharp.Blocks;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>#732: nif_set refused a correct set_path on meshes whose on-disk block order is not NiflySharp's own sort
/// order. The write verification diffs by the SAVED file's block ids, and NiflySharp's save re-sorts the block list,
/// so the expected id — taken from the list as LOADED — named a different block.</summary>
[Trait("tier", "unit")]
public sealed class NifSetBlockOrderTests
{
    /// <summary>A Skyrim SE mesh whose stored block order is NOT the order NiflySharp's save produces: the texture set
    /// and shader are written near the front, ahead of a run of NiNodes that the save's sort hoists above them. Saved
    /// with sorting off, which is what an exporter that writes its own order effectively does.</summary>
    static byte[] BuildUnsortedSe(int nodeCount = 20)
    {
        var ver = new NiVersion { FileVersion = NiVersion.ToFile("20.2.0.7"), UserVersion = 12, StreamVersion = 100 };
        var f = new NifFile();
        f.Create(ver, withRootNode: true);
        var root = f.GetRootNodes().First();
        root.Name = new NiStringRef("Root");
        root.Flags_ui = 0xE;

        var texSet = new BSShaderTextureSet
        {
            Textures = new List<NiString4>
            {
                new(@"textures\probe\diffuse.dds", false), new(@"textures\probe\normal.dds", false),
                new("", false), new("", false), new("", false),
                new("", false), new("", false), new("", false), new("", false),
            },
        };
        texSet.NumTextures = (uint)texSet.Textures.Count;
        int texSetId = f.AddBlock(texSet);

        var shader = new BSLightingShaderProperty
        {
            Type = NiflySharp.Helpers.ShaderHelper.ShaderGameType.SK,
            ShaderType_SK_FO4 = NiflySharp.Enums.BSLightingShaderType.SkinTint,
        };
        shader.TextureSetRef = new NiBlockRef<BSShaderTextureSet>(texSetId);
        int shaderId = f.AddBlock(shader);

        var shape = new BSTriShape { Name = new NiStringRef("ProbeShape"), Flags_ui = 0x400000E, Scale = 1f };
        shape.ShaderPropertyRef = new NiBlockRef<BSShaderProperty>(shaderId);
        root.Children.AddBlockRef(f.AddBlock(shape));

        for (int i = 0; i < nodeCount; i++)
            root.Children.AddBlockRef(f.AddBlock(new NiNode { Name = new NiStringRef($"Node{i}"), Flags_ui = 0xE, Scale = 1f }));

        using var ms = new MemoryStream();
        Assert.Equal(0, f.Save(ms, new NifFileSaveOptions { SortBlocks = false }));
        return ms.ToArray();
    }

    /// <summary>The stored block id of the mesh's single BSShaderTextureSet, read off the header's own type table.</summary>
    static int StoredTextureSetId(byte[] bytes)
    {
        var f = new NifFile();
        using var ms = new MemoryStream(bytes, writable: false);
        Assert.Equal(0, f.Load(ms));
        return Enumerable.Range(0, f.Header.BlockCount).Single(i => f.Header.GetBlockTypeNameById(i) == "BSShaderTextureSet");
    }

    /// <summary>The id the same texture set lands on once NiflySharp saves the mesh — the id space the gate's diff
    /// actually compares in.</summary>
    static int SavedTextureSetId(byte[] bytes)
    {
        var f = new NifFile();
        using (var ms = new MemoryStream(bytes, writable: false)) Assert.Equal(0, f.Load(ms));
        using (var o = new MemoryStream()) Assert.Equal(0, f.Save(o));
        var ts = f.Blocks.OfType<BSShaderTextureSet>().Single();
        return f.Blocks.Select((b, i) => (b, i)).Single(p => ReferenceEquals(p.b, ts)).i;
    }

    /// <summary>The fixture is only a test of #732 if the two id spaces really disagree on it. If NiflySharp ever
    /// stops re-sorting on save, this fires and says the test below no longer proves anything.</summary>
    [Fact]
    public void TheFixtureMeshStoresItsTextureSetAtADifferentIdThanASaveGivesIt()
    {
        var bytes = BuildUnsortedSe();
        Assert.NotEqual(StoredTextureSetId(bytes), SavedTextureSetId(bytes));
    }

    /// <summary>#732 itself: the swap goes through on such a mesh, and the block the verification confirms is the
    /// texture set at its SAVED id — not the id it happened to hold in the loaded list.</summary>
    [Fact]
    public void SetPathWritesOnAMeshWhoseStoredBlockOrderIsNotTheSaveOrder()
    {
        var bytes = BuildUnsortedSe();
        var op = new NifSetOp(NifSetOpKind.SetPath, "ProbeShape", TextureSlot: 0, Path: @"textures\probe\swapped.dds");

        var outcome = NifService.Set(bytes, new[] { op });

        Assert.Null(outcome.Error);
        Assert.NotNull(outcome.WrittenBytes);
        Assert.Equal(new[] { SavedTextureSetId(bytes) }, outcome.Report!.ChangedBlocks);

        var back = NifService.Inspect(outcome.WrittenBytes!).Inspect!;
        Assert.Contains(back.Shapes.Single().Textures, t => t.Path == @"textures\probe\swapped.dds");
    }
}
